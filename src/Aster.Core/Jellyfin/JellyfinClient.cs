// A thin async client for the Jellyfin REST API (10.9+ routes) plus the
// JellyDisc plugin's player-facing endpoint. A port of the Apple app's
// JellyfinClient.swift; keep the two in step.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aster.Core.DiscMenu;
using Aster.Core.Playback;
using Aster.Core.Server;
using Query = System.Collections.Generic.Dictionary<string, string?>;

namespace Aster.Core.Jellyfin;

public sealed class JellyfinClient : IMediaServer
{
    /// <summary>
    /// One connection pool for the whole app. Timeouts are per request, since
    /// a subtitle extraction can take minutes where everything else gets 30 s.
    /// </summary>
    /// <remarks>Redirects are followed by <see cref="Net.CredentialSafeRedirects"/>, so the sign-in never goes to another host.</remarks>
    private static readonly HttpClient SharedHttp = WithTimeout(Net.CredentialSafeRedirects.Client(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }), Timeout.InfiniteTimeSpan);

    private static HttpClient WithTimeout(HttpClient client, TimeSpan timeout)
    {
        client.Timeout = timeout;
        return client;
    }

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The first request for a text track inside an MKV makes the server
    /// extract it, which can take minutes; later ones come from its cache.
    /// </summary>
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromMinutes(10);

    public Uri BaseUrl { get; }
    public string? AccessToken { get; }
    public string? UserId { get; }

    /// <summary>Jellyfin or Emby: a few routes differ between them, and Jellyfin's plugins and extras answer nothing on Emby.</summary>
    public ServerKind Kind { get; }

    public bool IsJellyfin => Kind == ServerKind.Jellyfin;

    /// <summary>Everything Aster does, less Quick Connect and Aster's plugins on Emby.</summary>
    public ServerCapabilities Capabilities
    {
        get
        {
            const ServerCapabilities all = ServerCapabilities.QuickConnect | ServerCapabilities.Plugins | ServerCapabilities.LiveTV
                | ServerCapabilities.Music | ServerCapabilities.Audiobooks | ServerCapabilities.Channels | ServerCapabilities.Downloads
                | ServerCapabilities.Upcoming | ServerCapabilities.Playlists | ServerCapabilities.Favorites | ServerCapabilities.Trickplay
                | ServerCapabilities.SettingsSync | ServerCapabilities.Lyrics;
            return Kind == ServerKind.Emby ? all & ~(ServerCapabilities.QuickConnect | ServerCapabilities.Plugins) : all;
        }
    }

    private readonly HttpClient http;

    /// <param name="http">For tests; the app shares one client.</param>
    public JellyfinClient(Uri baseUrl, string? accessToken = null, string? userId = null, HttpClient? http = null, ServerKind kind = ServerKind.Jellyfin)
    {
        BaseUrl = baseUrl;
        AccessToken = accessToken;
        UserId = userId;
        Kind = kind;
        this.http = http ?? SharedHttp;
    }

    public JellyfinClient SignedIn(string token, string userId) => new(BaseUrl, token, userId, http, Kind);

    /// <summary>The same server, known now to be <paramref name="kind"/> (from its public info).</summary>
    public JellyfinClient As(ServerKind kind) => new(BaseUrl, AccessToken, UserId, http, kind);

    /// <summary>
    /// Jellyfin 10.9+'s path, or on Emby its older per-user form (the user in
    /// the path; the <c>userId</c> query sent as well is ignored there).
    /// </summary>
    private string Route(string jellyfin, Func<string> emby) => Kind == ServerKind.Emby ? emby() : jellyfin;

    // ---- Request plumbing ----

    public string AuthorizationHeader
    {
        get
        {
            var header = $"MediaBrowser Client=\"{JellyfinDevice.ClientName}\", Device=\"{HeaderValue(JellyfinDevice.DeviceName)}\", DeviceId=\"{JellyfinDevice.DeviceId}\", Version=\"{JellyfinDevice.Version}\"";
            if (AccessToken is not null) header += $", Token=\"{AccessToken}\"";
            return header;
        }
    }

    /// <summary>A computer's name may hold quotes or non-ASCII letters, which a header can't.</summary>
    private static string HeaderValue(string text) => Uri.EscapeDataString(text);

    /// <summary>The server's root, with no trailing slash, for paths to be added to.</summary>
    private string Root => BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');

    public Uri Url(string path, Query? query = null)
    {
        var url = new StringBuilder(Root);
        if (!path.StartsWith('/')) url.Append('/');
        url.Append(path);
        var items = (query ?? [])
            .Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value!))
            .ToList();
        if (items.Count > 0) url.Append('?').AppendJoin('&', items);
        return new Uri(url.ToString());
    }

    /// <summary>
    /// A URL that carries the token as a query parameter, for players and
    /// other loaders that cannot send headers. Newer servers read
    /// <c>ApiKey</c> and only honour the legacy <c>api_key</c> on a few
    /// stream endpoints, so both are sent.
    /// </summary>
    public Uri TokenUrl(string path, Query? query = null)
    {
        var merged = new Query(query ?? [])
        {
            ["ApiKey"] = AccessToken,
            ["api_key"] = AccessToken,
        };
        return Url(path, merged);
    }

    /// <summary>Whether <paramref name="url"/> is the server's own (same scheme, host, port and base path).</summary>
    public bool IsOnServer(Uri url) => url.GetLeftPart(UriPartial.Path).StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A request carrying the sign-in, for the server only: any other address
    /// (a disc menu's artwork or sound from a CDN or YouTube, say) gets a
    /// plain request, so the token never leaves for another site.
    /// </summary>
    public HttpRequestMessage AuthorizedRequest(Uri url, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        if (!IsOnServer(url)) return request;
        request.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeader);
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", AuthorizationHeader);
        return request;
    }

    private static HttpContent JsonBody(object body) =>
        new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JellyfinJson.Options))
        {
            Headers = { ContentType = new("application/json") },
        };

    /// <summary>
    /// Sends the request and returns the body. With <paramref name="allowNotFound"/>
    /// a 404 returns null, for endpoints where it means "nothing here".
    /// </summary>
    private async Task<byte[]?> SendAsync(HttpRequestMessage request, CancellationToken ct, TimeSpan? timeout = null, bool allowNotFound = false)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout ?? RequestTimeout);
        try
        {
            using (request)
            using (var response = await http.SendAsync(request, timer.Token).ConfigureAwait(false))
            {
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
                var data = await response.Content.ReadAsByteArrayAsync(timer.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var message = data.Length == 0 ? null : Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 300));
                    throw JellyfinException.Http((int)response.StatusCode, message);
                }
                return data;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The server took too long to answer.");
        }
    }

    private async Task<byte[]> SendRequiredAsync(HttpRequestMessage request, CancellationToken ct, TimeSpan? timeout = null) =>
        (await SendAsync(request, ct, timeout).ConfigureAwait(false))!;

    private static T Decode<T>(byte[] data)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(data, JellyfinJson.Options)
                ?? throw JellyfinException.Decoding("The response was empty.");
        }
        catch (JsonException error)
        {
            throw JellyfinException.Decoding(error.Message, error);
        }
    }

    public async Task<T> GetAsync<T>(string path, Query? query = null, CancellationToken ct = default) =>
        Decode<T>(await SendRequiredAsync(AuthorizedRequest(Url(path, query)), ct).ConfigureAwait(false));

    public async Task<T> PostAsync<T>(string path, Query? query = null, object? body = null, CancellationToken ct = default)
    {
        var request = AuthorizedRequest(Url(path, query), HttpMethod.Post);
        if (body is not null) request.Content = JsonBody(body);
        return Decode<T>(await SendRequiredAsync(request, ct).ConfigureAwait(false));
    }

    public async Task PostAsync(string path, Query? query = null, object? body = null, CancellationToken ct = default)
    {
        var request = AuthorizedRequest(Url(path, query), HttpMethod.Post);
        if (body is not null) request.Content = JsonBody(body);
        await SendAsync(request, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, Query? query = null, CancellationToken ct = default) =>
        await SendAsync(AuthorizedRequest(Url(path, query), HttpMethod.Delete), ct).ConfigureAwait(false);

    /// <summary>
    /// Raw bytes with authentication (pictures, sounds), from the disk cache
    /// when it has them. <paramref name="fresh"/> skips the cache and drops
    /// what it held, for a cached copy that turned out to be damaged.
    /// </summary>
    public async Task<byte[]> DataAsync(Uri url, bool fresh = false, CancellationToken ct = default)
    {
        if (fresh) ImageCache.Remove(url);
        else if (await ImageCache.ReadAsync(url, ct).ConfigureAwait(false) is { } cached) return cached;
        // A disc menu can name pictures anywhere on the web: only the server's get the token.
        var data = await SendRequiredAsync(AuthorizedRequest(url), ct).ConfigureAwait(false);
        await ImageCache.WriteAsync(url, data, ct).ConfigureAwait(false);
        return data;
    }

    // ---- Server and sign-in ----

    public Task<PublicSystemInfo> PublicSystemInfoAsync(CancellationToken ct = default) =>
        GetAsync<PublicSystemInfo>("/System/Info/Public", ct: ct);

    public Task<List<JellyfinUser>> PublicUsersAsync(CancellationToken ct = default) =>
        GetAsync<List<JellyfinUser>>("/Users/Public", ct: ct);

    public Task<AuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken ct = default) =>
        PostAsync<AuthenticationResult>("/Users/AuthenticateByName", body: new { Username = username, Pw = password }, ct: ct);

    /// <summary>Jellyfin's; Emby has no Quick Connect.</summary>
    public Task<bool> QuickConnectEnabledAsync(CancellationToken ct = default) =>
        IsJellyfin ? GetAsync<bool>("/QuickConnect/Enabled", ct: ct) : Task.FromResult(false);

    public Task<QuickConnectResult> QuickConnectInitiateAsync(CancellationToken ct = default) =>
        PostAsync<QuickConnectResult>("/QuickConnect/Initiate", ct: ct);

    public Task<QuickConnectResult> QuickConnectStateAsync(string secret, CancellationToken ct = default) =>
        GetAsync<QuickConnectResult>("/QuickConnect/Connect", new() { ["secret"] = secret }, ct);

    public Task<AuthenticationResult> AuthenticateWithQuickConnectAsync(string secret, CancellationToken ct = default) =>
        PostAsync<AuthenticationResult>("/Users/AuthenticateWithQuickConnect", body: new { Secret = secret }, ct: ct);

    public async Task LogoutAsync()
    {
        try { await PostAsync("/Sessions/Logout").ConfigureAwait(false); }
        catch (Exception) { }
    }

    // ---- Library ----

    public const string ListFields = "SortName,PrimaryImageAspectRatio,Overview,Genres,ProductionYear,OfficialRating,CommunityRating,RunTimeTicks,ParentId,Status,EndDate,ChildCount,DateCreated,MediaStreams,ProviderIds";
    public const string DetailFields = "Trickplay,PrimaryImageAspectRatio,Overview,Genres,Studios,People,Taglines,ProductionYear,OfficialRating,CommunityRating,CriticRating,RunTimeTicks,ParentId,Status,EndDate,ChildCount,RecursiveItemCount,Chapters,MediaSources,MediaStreams,ProviderIds,RemoteTrailers,DateCreated";

    private string RequireUser() => UserId ?? throw JellyfinException.NotSignedIn();

    public async Task<IReadOnlyList<BaseItem>> UserViewsAsync(CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>(Route("/UserViews", () => $"/Users/{RequireUser()}/Views"), new() { ["userId"] = RequireUser() }, ct).ConfigureAwait(false)).Items;

    public Task<BaseItem> ItemAsync(string id, CancellationToken ct = default) =>
        GetAsync<BaseItem>(Route($"/Items/{id}", () => $"/Users/{RequireUser()}/Items/{id}"), new() { ["userId"] = RequireUser(), ["fields"] = DetailFields }, ct);

    public Task<ItemsResult> ItemsAsync(ItemQuery query, CancellationToken ct = default) =>
        JellyfinItemsAsync(query.JellyfinParameters(), ct);

    /// <summary>Titles by Jellyfin's own <c>/Items</c> parameters, for what only Jellyfin has (music, playlists, channels' sources).</summary>
    public Task<ItemsResult> JellyfinItemsAsync(Query query, CancellationToken ct = default)
    {
        var merged = new Query
        {
            ["userId"] = RequireUser(),
            ["fields"] = ListFields,
            ["enableImageTypes"] = "Primary,Backdrop,Thumb,Logo",
            ["imageTypeLimit"] = "1",
        };
        foreach (var (key, value) in query) merged[key] = value;
        return GetAsync<ItemsResult>("/Items", merged, ct);
    }

    // ---- Sources (for genre, network and collection Channels) ----

    /// <summary>The genres of the server's movies and shows, by name.</summary>
    public async Task<IReadOnlyList<BaseItem>> VideoGenresAsync(CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>("/Genres", new()
        {
            ["userId"] = RequireUser(),
            ["recursive"] = "true",
            ["includeItemTypes"] = "Movie,Series",
            ["sortBy"] = "SortName",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>Every show on the server, by name, for making a channel of one.</summary>
    public Task<List<BaseItem>> SeriesListAsync(CancellationToken ct = default) =>
        AllItemsAsync(new()
        {
            ["userId"] = RequireUser(),
            ["recursive"] = "true",
            ["includeItemTypes"] = "Series",
            ["sortBy"] = "SortName",
            ["enableImages"] = "false",
            ["enableUserData"] = "false",
        }, ct);

    /// <summary>The studios and networks of the server's movies and shows, by name.</summary>
    public async Task<IReadOnlyList<BaseItem>> VideoStudiosAsync(CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>("/Studios", new()
        {
            ["userId"] = RequireUser(),
            ["recursive"] = "true",
            ["includeItemTypes"] = "Movie,Series",
            ["sortBy"] = "SortName",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>The server's collections.</summary>
    public async Task<IReadOnlyList<BaseItem>> CollectionsAsync(CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Users/{RequireUser()}/Items", new()
        {
            ["includeItemTypes"] = "BoxSet",
            ["recursive"] = "true",
            ["sortBy"] = "SortName",
            ["fields"] = "ChildCount",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>The content ratings the server's movies and shows carry, most used first.</summary>
    public async Task<IReadOnlyList<string>> VideoRatingsAsync(CancellationToken ct = default)
    {
        var items = (await GetAsync<ItemsResult>("/Items", new()
        {
            ["userId"] = RequireUser(),
            ["recursive"] = "true",
            ["includeItemTypes"] = "Movie,Series",
            ["fields"] = "OfficialRating",
            ["enableImages"] = "false",
            ["enableUserData"] = "false",
        }, ct).ConfigureAwait(false)).Items;
        return items
            .Select(item => item.OfficialRating)
            .OfType<string>()
            .Where(rating => rating.Length > 0)
            .GroupBy(rating => rating)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .ToList();
    }

    /// <summary>
    /// What a Channel's schedule and menus use of each title, and no more
    /// (no media streams), so thousands of episodes stay a small download.
    /// </summary>
    private static Query ChannelItemQuery() => new()
    {
        ["fields"] = "Overview",
        ["enableImageTypes"] = "Primary,Backdrop,Thumb,Logo",
        ["imageTypeLimit"] = "1",
        ["enableUserData"] = "false",
    };

    /// <summary>Every item <paramref name="query"/> finds, 500 at a time.</summary>
    private async Task<List<BaseItem>> AllItemsAsync(Query query, CancellationToken ct)
    {
        var found = new List<BaseItem>();
        const int pageSize = 500;
        while (true)
        {
            var page = new Query(query)
            {
                ["startIndex"] = found.Count.ToString(CultureInfo.InvariantCulture),
                ["limit"] = pageSize.ToString(CultureInfo.InvariantCulture),
            };
            var result = await GetAsync<ItemsResult>("/Items", page, ct).ConfigureAwait(false);
            found.AddRange(result.Items);
            if (result.Items.Count < pageSize || found.Count >= (result.TotalRecordCount ?? 0)) break;
        }
        return found;
    }

    /// <summary>
    /// The movies and series in any of <paramref name="genreIds"/>, or from any
    /// of <paramref name="studioIds"/> (ids, not names, so a name with "&amp;" in
    /// it can't break the request), of <paramref name="types"/>, under
    /// <paramref name="parentId"/> (a library) or everywhere, from
    /// <paramref name="years"/> and with <paramref name="ratings"/> when given.
    /// </summary>
    public Task<List<BaseItem>> SourceTitlesAsync(
        string types,
        string? parentId,
        (int First, int Last)? years,
        IReadOnlyList<string> ratings,
        IReadOnlyList<string>? genreIds = null,
        IReadOnlyList<string>? studioIds = null,
        CancellationToken ct = default)
    {
        var query = ChannelItemQuery();
        query["userId"] = RequireUser();
        query["recursive"] = "true";
        query["includeItemTypes"] = types;
        query["parentId"] = parentId;
        query["isMissing"] = "false";
        query["sortBy"] = "SortName";
        // Ids are comma-separated; names (ratings, below) take "|". Ids
        // joined with "|" make Jellyfin drop the filter and return
        // everything, so a two-network channel played the whole library.
        if (genreIds is { Count: > 0 }) query["genreIds"] = string.Join(',', genreIds);
        if (studioIds is { Count: > 0 }) query["studioIds"] = string.Join(',', studioIds);
        if (years is var (first, last)) query["years"] = string.Join(',', Enumerable.Range(first, last - first + 1));
        if (ratings.Count > 0) query["officialRatings"] = string.Join('|', ratings);
        return AllItemsAsync(query, ct);
    }

    /// <summary>What a collection holds: its movies and series.</summary>
    public Task<List<BaseItem>> CollectionTitlesAsync(string collectionId, CancellationToken ct = default)
    {
        var query = ChannelItemQuery();
        query["userId"] = RequireUser();
        query["parentId"] = collectionId;
        query["fields"] = "Overview,OfficialRating";
        return AllItemsAsync(query, ct);
    }

    /// <summary>A series' episodes, as a Channel plays them (missing ones left out).</summary>
    public Task<List<BaseItem>> SeriesEpisodesAsync(string seriesId, CancellationToken ct = default)
    {
        var query = ChannelItemQuery();
        query["userId"] = RequireUser();
        query["parentId"] = seriesId;
        query["recursive"] = "true";
        query["includeItemTypes"] = "Episode";
        query["isMissing"] = "false";
        return AllItemsAsync(query, ct);
    }

    public async Task<IReadOnlyList<BaseItem>> LatestAsync(string? parentId, int limit = 20, string? includeTypes = null, CancellationToken ct = default) =>
        await GetAsync<List<BaseItem>>(Route("/Items/Latest", () => $"/Users/{RequireUser()}/Items/Latest"), new()
        {
            ["userId"] = RequireUser(),
            ["parentId"] = parentId,
            ["limit"] = Number(limit),
            ["fields"] = ListFields,
            ["includeItemTypes"] = includeTypes,
            ["enableImageTypes"] = "Primary,Backdrop,Thumb,Logo",
            ["imageTypeLimit"] = "1",
        }, ct);

    public async Task<IReadOnlyList<BaseItem>> ResumeAsync(int limit = 20, string? parentId = null, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>(Route("/UserItems/Resume", () => $"/Users/{RequireUser()}/Items/Resume"), new()
        {
            ["userId"] = RequireUser(),
            ["parentId"] = parentId,
            ["limit"] = Number(limit),
            ["mediaTypes"] = "Video",
            ["fields"] = ListFields,
            ["enableImageTypes"] = "Primary,Backdrop,Thumb,Logo",
            ["imageTypeLimit"] = "1",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>
    /// Episodes of shows in the library that air soon, soonest first; most
    /// are placeholders from the show's schedule rather than files.
    /// </summary>
    public async Task<IReadOnlyList<BaseItem>> UpcomingAsync(int limit = 100, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>("/Shows/Upcoming", new()
        {
            ["userId"] = RequireUser(),
            ["limit"] = Number(limit),
            ["fields"] = ListFields,
            ["enableImageTypes"] = "Primary,Backdrop,Thumb,Logo",
            ["imageTypeLimit"] = "1",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>
    /// The TV and movie libraries that hold anime. Reading a library's
    /// metadata settings needs an admin account, so instead a few titles in
    /// each are sampled: when at least half were matched by Shoko/AniDB, the
    /// library is anime. One stray anime title doesn't claim a library.
    /// </summary>
    public async Task<IReadOnlyList<BaseItem>> AnimeLibrariesAsync(IEnumerable<BaseItem> views, CancellationToken ct = default)
    {
        var candidates = views.Where(v => v.CollectionType is "tvshows" or "movies").ToList();
        var verdicts = await Task.WhenAll(candidates.Select(async library =>
        {
            IReadOnlyList<BaseItem> sample;
            try
            {
                sample = (await JellyfinItemsAsync(new()
                {
                    ["parentId"] = library.Id,
                    ["recursive"] = "true",
                    ["includeItemTypes"] = "Series,Movie",
                    ["limit"] = "5",
                    ["fields"] = "ProviderIds",
                }, ct).ConfigureAwait(false)).Items;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                sample = [];
            }
            var anime = sample.Count(item => item.IsAnime);
            return sample.Count > 0 && anime * 2 >= sample.Count;
        })).ConfigureAwait(false);
        return candidates.Where((_, index) => verdicts[index]).ToList();
    }

    /// <summary>
    /// Just the ids of the given kinds of item in some libraries: no images
    /// or extra fields, so even large libraries come back quickly.
    /// </summary>
    public async Task<IReadOnlySet<string>> ItemIdsAsync(IEnumerable<string> libraryIds, string types, CancellationToken ct = default)
    {
        var chunks = await Task.WhenAll(libraryIds.Select(async libraryId =>
        {
            try
            {
                var result = await JellyfinItemsAsync(new()
                {
                    ["parentId"] = libraryId,
                    ["recursive"] = "true",
                    ["includeItemTypes"] = types,
                    ["fields"] = null,
                    ["enableImageTypes"] = null,
                    ["imageTypeLimit"] = null,
                    ["enableImages"] = "false",
                    ["enableUserData"] = "false",
                }, ct).ConfigureAwait(false);
                return result.Items.Select(item => item.Id);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return [];
            }
        })).ConfigureAwait(false);
        return chunks.SelectMany(ids => ids).ToHashSet();
    }

    public async Task<IReadOnlyList<BaseItem>> NextUpAsync(int limit = 20, string? seriesId = null, string? parentId = null, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>("/Shows/NextUp", new()
        {
            ["userId"] = RequireUser(),
            ["seriesId"] = seriesId,
            ["parentId"] = parentId,
            ["limit"] = Number(limit),
            ["fields"] = ListFields,
            ["enableImageTypes"] = "Primary,Backdrop,Thumb,Logo",
            ["imageTypeLimit"] = "1",
            ["enableResumable"] = "false",
        }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<BaseItem>> SeasonsAsync(string seriesId, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Shows/{seriesId}/Seasons", new()
        {
            ["userId"] = RequireUser(),
            ["fields"] = ListFields,
        }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<BaseItem>> EpisodesAsync(string seriesId, string? seasonId, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Shows/{seriesId}/Episodes", new()
        {
            ["userId"] = RequireUser(),
            ["seasonId"] = seasonId,
            ["fields"] = ListFields + ",MediaSources",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>
    /// Openings and endings from the OP/ED server plugin for a series, season
    /// or episode. Null when the plugin isn't installed, the item is unknown,
    /// or the request fails; callers then fall back to chapter markers.
    /// </summary>
    public Task<IReadOnlyList<OpEdTheme>?> OpEdThemesAsync(string itemId, CancellationToken ct = default) =>
        // Aster's server plugins are Jellyfin plugins.
        IsJellyfin ? OrNullAsync(async () => (await GetAsync<OpEdThemeList>($"/OpEd/Items/{itemId}/Themes", ct: ct).ConfigureAwait(false)).Themes, ct) : Task.FromResult<IReadOnlyList<OpEdTheme>?>(null);

    /// <summary>
    /// The theme a published music video (or song) is, from the OP/ED
    /// plugin; null for any other item, or without the plugin.
    /// </summary>
    public Task<OpEdTheme?> OpEdThemeForLibraryItemAsync(string itemId, CancellationToken ct = default) =>
        IsJellyfin ? OrNullAsync(() => GetAsync<OpEdTheme>($"/OpEd/LibraryItems/{itemId}/Theme", ct: ct), ct) : Task.FromResult<OpEdTheme?>(null);

    /// <summary>
    /// Live TV channels' groups, by channel id, from the Aster Channels
    /// plugin; null without it (or before 0.2), which leaves Live TV as it was.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>?> LiveTvGroupsAsync(CancellationToken ct = default) =>
        IsJellyfin ? OrNullAsync(async () => (await GetAsync<LiveTvGroupList>("/AsterChannels/LiveTvGroups", ct: ct).ConfigureAwait(false)).ByChannel, ct) : Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    /// <summary>Puts a Live TV channel in <paramref name="group"/>, or out of its group for null (administrators only); every channel's group after.</summary>
    public async Task<IReadOnlyDictionary<string, string>> SetLiveTvGroupAsync(string channelId, string? group, CancellationToken ct = default) =>
        (await PostAsync<LiveTvGroupList>("/AsterChannels/LiveTvGroups", body: new LiveTvGroupList.Entry(channelId, group ?? ""), ct: ct).ConfigureAwait(false)).ByChannel;

    /// <summary>The OP/ED plugin's published libraries; null without the plugin (or before 0.2).</summary>
    public Task<OpEdLibraries?> OpEdLibrariesAsync(CancellationToken ct = default) =>
        IsJellyfin ? OrNullAsync(() => GetAsync<OpEdLibraries>("/OpEd/Library", ct: ct), ct) : Task.FromResult<OpEdLibraries?>(null);

    // ---- Server plugins (administrators only, but for the probe) ----

    /// <summary>The server's installed plugins.</summary>
    public Task<List<InstalledPlugin>> InstalledPluginsAsync(CancellationToken ct = default) => GetAsync<List<InstalledPlugin>>("/Plugins", ct: ct);

    /// <summary>The plugin repositories in the server's settings.</summary>
    public Task<List<PluginRepository>> PluginRepositoriesAsync(CancellationToken ct = default) => GetAsync<List<PluginRepository>>("/Repositories", ct: ct);

    /// <summary>Replaces the server's whole list of plugin repositories.</summary>
    public Task SetPluginRepositoriesAsync(IReadOnlyList<PluginRepository> repositories, CancellationToken ct = default) =>
        PostAsync("/Repositories", body: repositories, ct: ct);

    /// <summary>Every plugin the server's repositories offer, fetched by the server.</summary>
    public Task<List<PluginPackage>> PluginPackagesAsync(CancellationToken ct = default) => GetAsync<List<PluginPackage>>("/Packages", ct: ct);

    /// <summary>Downloads a plugin onto the server: the newest version that runs on it. It loads at the next restart.</summary>
    public Task InstallPackageAsync(string name, string guid, string repositoryUrl, CancellationToken ct = default) =>
        PostAsync($"/Packages/Installed/{Uri.EscapeDataString(name)}", new() { ["assemblyGuid"] = guid, ["repositoryUrl"] = repositoryUrl }, ct: ct);

    /// <summary>Turns an installed plugin on or off; either takes a restart.</summary>
    public Task SetPluginAsync(string id, string version, bool enabled, CancellationToken ct = default) =>
        PostAsync($"/Plugins/{id}/{version}/{(enabled ? "Enable" : "Disable")}", ct: ct);

    public Task<ServerSystemInfo> ServerSystemInfoAsync(CancellationToken ct = default) => GetAsync<ServerSystemInfo>("/System/Info", ct: ct);

    public Task RestartServerAsync(CancellationToken ct = default) => PostAsync("/System/Restart", ct: ct);

    /// <summary>
    /// Whether the server answers <paramref name="path"/> at all: true for a
    /// reply, false for 404 (a plugin that isn't there), null when it can't tell.
    /// </summary>
    public async Task<bool?> ServerHasAsync(string path, CancellationToken ct = default)
    {
        if (!IsJellyfin) return null;
        try { return await SendAsync(AuthorizedRequest(Url(path)), ct, allowNotFound: true).ConfigureAwait(false) is not null; }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return null; }
    }

    /// <summary>A library's newest music videos, for a shelf.</summary>
    public async Task<IReadOnlyList<BaseItem>> LatestMusicVideosAsync(string libraryId, int limit = 20, CancellationToken ct = default) =>
        await OrNullAsync(async () => (await JellyfinItemsAsync(new()
        {
            ["parentId"] = libraryId,
            ["includeItemTypes"] = "MusicVideo",
            ["recursive"] = "true",
            ["sortBy"] = "DateCreated,SortName",
            ["sortOrder"] = "Descending",
            ["limit"] = Number(limit),
        }, ct).ConfigureAwait(false)).Items, ct).ConfigureAwait(false) ?? [];

    /// <summary>A plugin media path (e.g. <c>VideoMp4Url</c>) as a URL a player can open, with the token in the query string.</summary>
    public Uri OpEdMediaUrl(string path) => TokenUrl(path);

    /// <summary>
    /// Every episode of a series in airing order, with its chapters, in one
    /// request: what finding openings and endings reads.
    /// </summary>
    public async Task<IReadOnlyList<BaseItem>> EpisodesWithChaptersAsync(string seriesId, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Shows/{seriesId}/Episodes", new()
        {
            ["userId"] = RequireUser(),
            ["fields"] = "Chapters,RunTimeTicks",
            ["enableImageTypes"] = "Primary,Thumb",
            ["imageTypeLimit"] = "1",
            ["enableUserData"] = "false",
        }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<BaseItem>> SimilarAsync(string id, int limit = 20, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Items/{id}/Similar", new()
        {
            ["userId"] = RequireUser(),
            ["limit"] = Number(limit),
            ["fields"] = ListFields,
        }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<BaseItem>> SpecialFeaturesAsync(string id, CancellationToken ct = default) =>
        await GetAsync<List<BaseItem>>(Route($"/Items/{id}/SpecialFeatures", () => $"/Users/{RequireUser()}/Items/{id}/SpecialFeatures"), new() { ["userId"] = RequireUser() }, ct);

    public async Task<IReadOnlyList<BaseItem>> SearchAsync(string term, int limit = 60, CancellationToken ct = default) =>
        (await JellyfinItemsAsync(new()
        {
            ["searchTerm"] = term,
            ["recursive"] = "true",
            ["includeItemTypes"] = "Movie,Series,Episode,MusicAlbum,MusicArtist,Audio,Playlist",
            ["limit"] = Number(limit),
            ["sortBy"] = "SortName",
        }, ct).ConfigureAwait(false)).Items;

    // ---- User data ----

    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default)
    {
        var query = new Query { ["userId"] = RequireUser() };
        var path = Route($"/UserFavoriteItems/{id}", () => $"/Users/{RequireUser()}/FavoriteItems/{id}");
        return favorite ? PostAsync(path, query, ct: ct) : DeleteAsync(path, query, ct);
    }

    public Task SetPlayedAsync(string id, bool played, CancellationToken ct = default)
    {
        var query = new Query { ["userId"] = RequireUser() };
        var path = Route($"/UserPlayedItems/{id}", () => $"/Users/{RequireUser()}/PlayedItems/{id}");
        return played ? PostAsync(path, query, ct: ct) : DeleteAsync(path, query, ct);
    }

    // ---- Playback ----

    /// <summary>Everyone's user names on the server, which only an administrator can list.</summary>
    public async Task<IReadOnlyList<string>> UserNamesAsync(CancellationToken ct = default) =>
        (await GetAsync<List<JellyfinUser>>("/Users", ct: ct).ConfigureAwait(false))
            .Select(user => user.Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// The signed-in user, including their server-side settings. Asked for by
    /// id rather than /Users/Me, which an API key has no user for.
    /// </summary>
    public Task<JellyfinUser> MeAsync(CancellationToken ct = default) => GetAsync<JellyfinUser>($"/Users/{RequireUser()}", ct: ct);

    /// <summary>Replaces the user's settings wholesale; send a complete configuration.</summary>
    public Task UpdateConfigurationAsync(UserConfiguration configuration, CancellationToken ct = default) =>
        PostAsync(Route("/Users/Configuration", () => $"/Users/{RequireUser()}/Configuration"), new() { ["userId"] = RequireUser() }, configuration, ct);

    /// <summary>
    /// <paramref name="allowDirect"/> false makes the server offer an HLS
    /// stream even when the file could play directly.
    /// <paramref name="subtitleStreamIndex"/> only matters to the server for
    /// tracks it would burn in; -1 means none. <paramref name="mostCompatible"/>
    /// asks for a fresh 8-bit H.264 + AAC encode, copying nothing from the
    /// file: the last resort when other streams won't play.
    /// </summary>
    public Task<PlaybackInfoResponse> PlaybackInfoAsync(
        string itemId,
        long startTicks,
        int maxBitrate,
        string? mediaSourceId = null,
        int? audioStreamIndex = null,
        int? subtitleStreamIndex = null,
        bool allowDirect = true,
        bool mostCompatible = false,
        DeviceProfile? profile = null,
        CancellationToken ct = default)
    {
        var direct = allowDirect && !mostCompatible;
        var body = new
        {
            DeviceProfile = profile ?? (mostCompatible ? DeviceProfile.DesktopMostCompatible(maxBitrate) : DeviceProfile.Desktop(maxBitrate)),
            // The server applies the track indexes only when this names the file.
            MediaSourceId = mediaSourceId,
            StartTimeTicks = startTicks,
            MaxStreamingBitrate = maxBitrate,
            AudioStreamIndex = audioStreamIndex,
            SubtitleStreamIndex = subtitleStreamIndex,
            AutoOpenLiveStream = true,
            EnableDirectPlay = direct,
            EnableDirectStream = direct,
            EnableTranscoding = true,
            AllowVideoStreamCopy = !mostCompatible,
            AllowAudioStreamCopy = !mostCompatible,
        };
        return PostAsync<PlaybackInfoResponse>($"/Items/{itemId}/PlaybackInfo", new() { ["userId"] = RequireUser() }, body, ct);
    }

    /// <summary>
    /// The cues of a text subtitle track, converted by the server to its
    /// JSON form (what Jellyfin's web player reads).
    /// </summary>
    public async Task<IReadOnlyList<SubtitleCue>> SubtitleCuesAsync(string itemId, string mediaSourceId, int streamIndex, CancellationToken ct = default)
    {
        var request = AuthorizedRequest(Url($"/Videos/{itemId}/{mediaSourceId}/Subtitles/{streamIndex}/Stream.js"));
        return SubtitleCue.Parse(await SendRequiredAsync(request, ct, ExtractionTimeout).ConfigureAwait(false));
    }

    /// <summary>
    /// An ASS/SSA track as the script itself, with its styles and positions,
    /// for drawing with libass. Extracted on first request, as with
    /// <see cref="SubtitleCuesAsync"/>.
    /// </summary>
    public async Task<string> SubtitleScriptAsync(string itemId, string mediaSourceId, int streamIndex, CancellationToken ct = default)
    {
        var request = AuthorizedRequest(Url($"/Videos/{itemId}/{mediaSourceId}/Subtitles/{streamIndex}/Stream.ass"));
        var data = await SendRequiredAsync(request, ct, ExtractionTimeout).ConfigureAwait(false);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    /// <summary>A file embedded in the video's container, such as a font.</summary>
    public Task<byte[]> AttachmentAsync(string itemId, string mediaSourceId, int index, CancellationToken ct = default) =>
        SendRequiredAsync(AuthorizedRequest(Url($"/Videos/{itemId}/{mediaSourceId}/Attachments/{index}")), ct);

    public async Task ReportStartAsync(PlaybackReport report)
    {
        try { await PostAsync("/Sessions/Playing", body: report).ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>
    /// Also saves the position as the resume point when it's due, at once,
    /// or never (music, which starts each song from the top); see <see cref="ResumePoints"/>.
    /// </summary>
    public async Task ReportProgressAsync(PlaybackReport report, ResumePoints.Saving saving = ResumePoints.Saving.WhenDue)
    {
        try { await PostAsync("/Sessions/Playing/Progress", body: report).ConfigureAwait(false); }
        catch (Exception) { }
        if (saving == ResumePoints.Saving.Never) return;
        await ResumePoints.Shared.ProgressAsync(report, this, force: saving == ResumePoints.Saving.Now).ConfigureAwait(false);
    }

    public async Task ReportStoppedAsync(PlaybackReport report)
    {
        // A resume point still on its way mustn't land after the stop.
        await ResumePoints.Shared.StoppingAsync(report.ItemId).ConfigureAwait(false);
        try { await PostAsync("/Sessions/Playing/Stopped", body: report).ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>Sets the user's resume point on an item, leaving its watched state alone.</summary>
    public async Task SaveResumePositionAsync(string itemId, long ticks, CancellationToken ct = default)
    {
        if (UserId is null) return;
        await PostAsync(Route($"/UserItems/{itemId}/UserData", () => $"/Users/{UserId}/Items/{itemId}/UserData"), new() { ["userId"] = UserId }, new { PlaybackPositionTicks = ticks }, ct).ConfigureAwait(false);
    }

    // ---- Images ----

    /// <summary><paramref name="quality"/> is the server's JPEG/WebP quality, 0 to 100.</summary>
    public Uri ImageUrl(string itemId, ImageKind kind, string? tag = null, int? index = null, int? maxWidth = null, int? maxHeight = null, int quality = 90)
    {
        var path = $"/Items/{itemId}/Images/{kind}";
        if (index is { } i) path += $"/{i}";
        return Url(path, new()
        {
            ["tag"] = tag,
            ["maxWidth"] = Number(maxWidth),
            ["maxHeight"] = Number(maxHeight),
            ["quality"] = Number(quality),
        });
    }

    public Uri UserImageUrl(string userId, string? tag) =>
        Url("/UserImage", new() { ["userId"] = userId, ["tag"] = tag, ["maxWidth"] = "300" });

    // ---- Streams ----

    public Uri DirectStreamUrl(string itemId, string mediaSourceId, string? playSessionId) =>
        TokenUrl($"/Videos/{itemId}/stream", new()
        {
            ["static"] = "true",
            ["mediaSourceId"] = mediaSourceId,
            ["playSessionId"] = playSessionId,
            ["deviceId"] = JellyfinDevice.DeviceId,
        });

    /// <summary>The original file, for saving; refused unless the account may download.</summary>
    public Uri DownloadUrl(string itemId) => TokenUrl($"/Items/{itemId}/Download");

    public Uri AudioStreamUrl(string itemId) => TokenUrl($"/Audio/{itemId}/stream", new() { ["static"] = "true" });

    public Uri VideoStreamUrl(string itemId) => TokenUrl($"/Videos/{itemId}/stream", new() { ["static"] = "true" });

    public Uri SubtitleUrl(string itemId, string mediaSourceId, int streamIndex) =>
        TokenUrl($"/Videos/{itemId}/{mediaSourceId}/Subtitles/{streamIndex}/Stream.vtt");

    /// <summary>A text track as a file in <paramref name="format"/> ("ass", "srt"), for the player to load beside a stream.</summary>
    public Uri SubtitleFileUrl(string itemId, string mediaSourceId, int streamIndex, string format) =>
        TokenUrl($"/Videos/{itemId}/{mediaSourceId}/Subtitles/{streamIndex}/Stream.{format}");

    // ---- Music ----

    /// <summary>Artists credited on albums, paged.</summary>
    public Task<ItemsResult> AlbumArtistsAsync(string? parentId = null, int startIndex = 0, int limit = 100, CancellationToken ct = default) =>
        GetAsync<ItemsResult>("/Artists/AlbumArtists", new()
        {
            ["userId"] = RequireUser(),
            ["parentId"] = parentId,
            ["startIndex"] = Number(startIndex),
            ["limit"] = Number(limit),
            ["sortBy"] = "SortName",
            ["sortOrder"] = "Ascending",
            ["fields"] = "PrimaryImageAspectRatio,Overview",
            ["enableImageTypes"] = "Primary,Backdrop",
            ["imageTypeLimit"] = "1",
        }, ct);

    /// <summary>An album's songs in disc and track order.</summary>
    public async Task<IReadOnlyList<BaseItem>> AlbumTracksAsync(string albumId, CancellationToken ct = default) =>
        (await JellyfinItemsAsync(new()
        {
            ["parentId"] = albumId,
            ["includeItemTypes"] = "Audio",
            ["sortBy"] = "ParentIndexNumber,IndexNumber,SortName",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>Albums an artist is credited on, newest first.</summary>
    public async Task<IReadOnlyList<BaseItem>> ArtistAlbumsAsync(string artistId, CancellationToken ct = default) =>
        (await JellyfinItemsAsync(new()
        {
            ["albumArtistIds"] = artistId,
            ["includeItemTypes"] = "MusicAlbum",
            ["recursive"] = "true",
            ["sortBy"] = "ProductionYear,SortName",
            ["sortOrder"] = "Descending",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>Every song by an artist, in album order, for Play and Shuffle.</summary>
    public async Task<IReadOnlyList<BaseItem>> ArtistTracksAsync(string artistId, int limit = 400, CancellationToken ct = default) =>
        (await JellyfinItemsAsync(new()
        {
            ["artistIds"] = artistId,
            ["includeItemTypes"] = "Audio",
            ["recursive"] = "true",
            ["sortBy"] = "Album,ParentIndexNumber,IndexNumber",
            ["limit"] = Number(limit),
        }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<BaseItem>> PlaylistItemsAsync(string playlistId, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Playlists/{playlistId}/Items", new()
        {
            ["userId"] = RequireUser(),
            // Tags too, for a break clip's `aster:bumper=in`.
            ["fields"] = ListFields + ",Tags",
            // Logo too, so an episode carries its series' clear logo.
            ["enableImageTypes"] = "Primary,Logo",
            ["imageTypeLimit"] = "1",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>Songs the server thinks go with an item: a song, album, artist or genre.</summary>
    public async Task<IReadOnlyList<BaseItem>> InstantMixAsync(string itemId, int limit = 100, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Items/{itemId}/InstantMix", new()
        {
            ["userId"] = RequireUser(),
            ["limit"] = Number(limit),
            ["fields"] = ListFields,
            ["enableImageTypes"] = "Primary",
            ["imageTypeLimit"] = "1",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>
    /// A stream the player can play: the file itself in any format mpv
    /// reads, else an HLS transcode to AAC. The server decides per request.
    /// </summary>
    public Uri UniversalAudioUrl(string itemId, string playSessionId, int maxBitrate) =>
        TokenUrl($"/Audio/{itemId}/universal", new()
        {
            ["userId"] = UserId,
            ["deviceId"] = JellyfinDevice.DeviceId,
            ["playSessionId"] = playSessionId,
            ["container"] = "mp3,aac,m4a,m4b,flac,alac,wav,aiff,ogg,oga,opus,webma,wma,ape,wv,mka,dsf",
            ["transcodingContainer"] = "ts",
            ["transcodingProtocol"] = "hls",
            ["audioCodec"] = "aac",
            ["maxStreamingBitrate"] = Number(maxBitrate),
            ["startTimeTicks"] = "0",
            ["enableRedirection"] = "true",
            ["enableRemoteMedia"] = "false",
        });

    /// <summary>A path the server gave (a transcode playlist, say) made absolute on this server.</summary>
    public Uri? AbsoluteUrl(string serverRelative)
    {
        var question = serverRelative.IndexOf('?');
        var path = question < 0 ? serverRelative : serverRelative[..question];
        var query = question < 0 ? "" : serverRelative[question..];
        return Uri.TryCreate(Root + (path.StartsWith('/') ? path : "/" + path) + query, UriKind.Absolute, out var url) ? url : null;
    }

    // ---- Live TV ----

    public Task<LiveTVInfo> LiveTVInfoAsync(CancellationToken ct = default) => GetAsync<LiveTVInfo>("/LiveTv/Info", ct: ct);

    public async Task<IReadOnlyList<BaseItem>> ChannelsAsync(int limit = 500, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>("/LiveTv/Channels", new()
        {
            ["userId"] = RequireUser(),
            ["limit"] = Number(limit),
            ["addCurrentProgram"] = "true",
            ["enableUserData"] = "true",
            ["enableImages"] = "true",
            ["imageTypeLimit"] = "1",
            ["enableImageTypes"] = "Primary",
            ["sortBy"] = "DefaultChannelOrder",
            ["sortOrder"] = "Ascending",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>Programs on one channel that overlap the window.</summary>
    public async Task<IReadOnlyList<BaseItem>> ProgramsAsync(string channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>("/LiveTv/Programs", new()
        {
            ["userId"] = RequireUser(),
            ["channelIds"] = channelId,
            ["minEndDate"] = JellyfinDate.Format(from),
            ["maxStartDate"] = JellyfinDate.Format(to),
            ["sortBy"] = "StartDate",
            ["limit"] = "200",
            // Image tags and the overview let the guide's now-playing panel
            // show artwork and text without a second request per programme.
            ["enableImages"] = "true",
            ["imageTypeLimit"] = "1",
            ["enableImageTypes"] = "Primary,Thumb",
            ["fields"] = "Overview",
            ["enableUserData"] = "false",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>What the server recommends right now, optionally filtered by kind.</summary>
    public async Task<IReadOnlyList<BaseItem>> RecommendedProgramsAsync(int limit = 24, bool? isMovie = null, bool? isSports = null, bool? isKids = null, bool? isNews = null, bool? isSeries = null, CancellationToken ct = default)
    {
        static string? Flag(bool? value) => value is { } v ? (v ? "true" : "false") : null;
        return (await GetAsync<ItemsResult>("/LiveTv/Programs/Recommended", new()
        {
            ["userId"] = RequireUser(),
            ["isAiring"] = "true",
            ["limit"] = Number(limit),
            ["enableImages"] = "true",
            ["imageTypeLimit"] = "1",
            ["enableImageTypes"] = "Primary,Thumb,Backdrop",
            ["fields"] = "ChannelInfo,PrimaryImageAspectRatio,Overview",
            ["isMovie"] = Flag(isMovie),
            ["isSports"] = Flag(isSports),
            ["isKids"] = Flag(isKids),
            ["isNews"] = Flag(isNews),
            ["isSeries"] = Flag(isSeries),
        }, ct).ConfigureAwait(false)).Items;
    }

    public Task<BaseItem> ProgramAsync(string id, CancellationToken ct = default) =>
        GetAsync<BaseItem>($"/LiveTv/Programs/{id}", new() { ["userId"] = RequireUser() }, ct);

    public async Task<IReadOnlyList<BaseItem>> RecordingsAsync(int limit = 100, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>("/LiveTv/Recordings", new()
        {
            ["userId"] = RequireUser(),
            ["limit"] = Number(limit),
            ["fields"] = ListFields,
            ["enableImages"] = "true",
            ["imageTypeLimit"] = "1",
            ["enableImageTypes"] = "Primary,Thumb,Backdrop",
        }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<TimerInfo>> TimersAsync(CancellationToken ct = default) =>
        (await GetAsync<ItemsEnvelope<TimerInfo>>("/LiveTv/Timers", new() { ["isScheduled"] = "true" }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<SeriesTimerInfo>> SeriesTimersAsync(CancellationToken ct = default) =>
        (await GetAsync<ItemsEnvelope<SeriesTimerInfo>>("/LiveTv/SeriesTimers", ct: ct).ConfigureAwait(false)).Items;

    /// <summary>Schedules a recording of one program using the server's defaults.</summary>
    public async Task RecordProgramAsync(string programId, CancellationToken ct = default)
    {
        var defaults = await RawGetAsync("/LiveTv/Timers/Defaults", new() { ["programId"] = programId }, ct).ConfigureAwait(false);
        await PostRawAsync("/LiveTv/Timers", defaults, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records every airing of a series using the server's defaults. The
    /// server has a single defaults endpoint, <c>Timers/Defaults</c>, whose
    /// response is a series timer; the web client posts that same payload to
    /// <c>SeriesTimers</c>.
    /// </summary>
    public async Task RecordSeriesAsync(string programId, CancellationToken ct = default)
    {
        var defaults = await RawGetAsync("/LiveTv/Timers/Defaults", new() { ["programId"] = programId }, ct).ConfigureAwait(false);
        await PostRawAsync("/LiveTv/SeriesTimers", defaults, ct: ct).ConfigureAwait(false);
    }

    public Task CancelTimerAsync(string timerId, CancellationToken ct = default) => DeleteAsync($"/LiveTv/Timers/{timerId}", ct: ct);

    public Task CancelSeriesTimerAsync(string seriesTimerId, CancellationToken ct = default) => DeleteAsync($"/LiveTv/SeriesTimers/{seriesTimerId}", ct: ct);

    public Task<SeriesTimerInfo> SeriesTimerAsync(string id, CancellationToken ct = default) => GetAsync<SeriesTimerInfo>($"/LiveTv/SeriesTimers/{id}", ct: ct);

    /// <summary>Saves a changed series rule; the whole record is posted back.</summary>
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo timer, CancellationToken ct = default) =>
        PostAsync($"/LiveTv/SeriesTimers/{timer.Id}", body: timer, ct: ct);

    public Task DeleteRecordingAsync(string recordingId, CancellationToken ct = default) => DeleteAsync($"/LiveTv/Recordings/{recordingId}", ct: ct);

    /// <summary>
    /// The text at a stream address (an HLS playlist), signed in; for checking
    /// a stream is serving video before switching to it.
    /// </summary>
    public async Task<string> PlaylistTextAsync(Uri url, CancellationToken ct = default) =>
        Encoding.UTF8.GetString(await SendRequiredAsync(AuthorizedRequest(url), ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false));

    public async Task CloseLiveStreamAsync(string liveStreamId)
    {
        try { await PostAsync("/LiveStreams/Close", new() { ["liveStreamId"] = liveStreamId }).ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>
    /// Ends the server's transcode for a play session that playback reports
    /// don't cover, so it doesn't keep encoding after playback moves on.
    /// </summary>
    public async Task StopTranscodingAsync(string playSessionId)
    {
        try { await DeleteAsync("/Videos/ActiveEncodings", new() { ["deviceId"] = JellyfinDevice.DeviceId, ["playSessionId"] = playSessionId }).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private Task<byte[]> RawGetAsync(string path, Query? query, CancellationToken ct) =>
        SendRequiredAsync(AuthorizedRequest(Url(path, query)), ct);

    private async Task PostRawAsync(string path, byte[] body, Query? query = null, CancellationToken ct = default)
    {
        var request = AuthorizedRequest(Url(path, query), HttpMethod.Post);
        request.Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } };
        await SendAsync(request, ct).ConfigureAwait(false);
    }

    // ---- Settings on the server ----

    /// <summary>
    /// The app's own settings for this user are kept in Jellyfin's display
    /// preferences, the place the server gives each app for its settings
    /// (Jellyfin's web app keeps its own under the client name "emby"). The
    /// record is the Apple app's, so settings follow the user between them.
    /// </summary>
    private const string SettingsRecord = "asterfin";
    private const string SettingsClient = "AsterFIN";

    /// <summary>
    /// Account-wide web settings the server mixes into every record it
    /// returns, and saves to the account from any record posted. Never sent
    /// back, so this app can't change them.
    /// </summary>
    private static readonly HashSet<string> AccountWidePrefs =
        new(["chromecastversion", "skipforwardlength", "skipbacklength", "enablenextvideoinfooverlay", "tvhome", "dashboardtheme"], StringComparer.OrdinalIgnoreCase);

    private async Task<JsonObject> SettingsRecordJsonAsync(CancellationToken ct)
    {
        var data = await RawGetAsync($"/DisplayPreferences/{SettingsRecord}", new() { ["userId"] = RequireUser(), ["client"] = SettingsClient }, ct).ConfigureAwait(false);
        return JsonNode.Parse(data) as JsonObject ?? [];
    }

    private static Dictionary<string, string> OwnPrefs(JsonObject record)
    {
        var prefs = new Dictionary<string, string>();
        if (record["CustomPrefs"] is not JsonObject custom) return prefs;
        foreach (var (key, value) in custom)
        {
            if (AccountWidePrefs.Contains(key)) continue;
            if (value is JsonValue v && v.TryGetValue<string>(out var text)) prefs[key] = text;
        }
        return prefs;
    }

    /// <summary>The app's settings saved on the server, by key.</summary>
    public async Task<Dictionary<string, string>> AppSettingsAsync(CancellationToken ct = default) =>
        OwnPrefs(await SettingsRecordJsonAsync(ct).ConfigureAwait(false));

    /// <summary>Saves settings on the server, keeping the others already there.</summary>
    public async Task SaveAppSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        var record = await SettingsRecordJsonAsync(ct).ConfigureAwait(false);
        var prefs = OwnPrefs(record);
        foreach (var (key, value) in values) prefs[key] = value;
        var custom = new JsonObject();
        foreach (var (key, value) in prefs) custom[key] = value;
        record["CustomPrefs"] = custom;
        await PostRawAsync($"/DisplayPreferences/{SettingsRecord}", Encoding.UTF8.GetBytes(record.ToJsonString()),
            new() { ["userId"] = RequireUser(), ["client"] = SettingsClient }, ct).ConfigureAwait(false);
    }

    // ---- Tags ----

    /// <summary>
    /// The fields an item update writes back. Each is asked for when reading
    /// the item first, since one left out of the update would be cleared.
    /// </summary>
    private const string EditableFields = "Tags,Overview,Genres,Studios,People,ProviderIds,DateCreated,Taglines,SortName,PremiereDate,ProductionYear,OfficialRating,CustomRating,LockedFields,LockData,ChildCount";

    /// <summary>
    /// Replaces an item's tags, leaving the rest of its metadata as it was:
    /// the full record is read and written back with only the tags changed.
    /// Needs a user allowed to edit metadata (an administrator).
    /// </summary>
    public async Task SetTagsAsync(IEnumerable<string> tags, string itemId, CancellationToken ct = default)
    {
        var data = await RawGetAsync($"/Users/{RequireUser()}/Items/{itemId}", new() { ["fields"] = EditableFields }, ct).ConfigureAwait(false);
        if (JsonNode.Parse(data) is not JsonObject record) throw JellyfinException.Decoding("The item's record wasn't readable");
        record["Tags"] = new JsonArray([.. tags.Select(tag => JsonValue.Create(tag))]);
        await PostRawAsync($"/Items/{itemId}", Encoding.UTF8.GetBytes(record.ToJsonString()), ct: ct).ConfigureAwait(false);
    }

    // ---- Playlists ----

    /// <summary>The user's playlists of one media type ("Audio" or "Video").</summary>
    public async Task<IReadOnlyList<BaseItem>> PlaylistsAsync(string mediaType, CancellationToken ct = default) =>
        (await JellyfinItemsAsync(new()
        {
            ["fields"] = ListFields + ",Tags",
            ["includeItemTypes"] = "Playlist",
            ["mediaTypes"] = mediaType,
            ["recursive"] = "true",
            ["sortBy"] = "SortName",
            ["sortOrder"] = "Ascending",
        }, ct).ConfigureAwait(false)).Items;

    /// <summary>
    /// Creates a playlist holding the items and returns its id.
    /// <paramref name="isPublic"/> lets every user on the server see it (a genre Channel's).
    /// </summary>
    public async Task<string> CreatePlaylistAsync(string name, IReadOnlyList<string> itemIds, string mediaType, bool? isPublic = null, CancellationToken ct = default) =>
        // Emby reads these from the query rather than the body: both, there.
        (await PostAsync<PlaylistCreationResult>("/Playlists", IsJellyfin ? null : new()
        {
            ["Name"] = name,
            ["Ids"] = string.Join(',', itemIds),
            ["UserId"] = RequireUser(),
            ["MediaType"] = mediaType,
        }, new
        {
            Name = name,
            Ids = itemIds,
            UserId = RequireUser(),
            MediaType = mediaType,
            IsPublic = isPublic,
        }, ct: ct).ConfigureAwait(false)).Id;

    public Task AddToPlaylistAsync(string playlistId, IEnumerable<string> itemIds, CancellationToken ct = default) =>
        PostAsync($"/Playlists/{playlistId}/Items", new() { ["ids"] = string.Join(',', itemIds), ["userId"] = RequireUser() }, ct: ct);

    /// <summary>Removes entries by their playlist entry ids, not item ids.</summary>
    public Task RemoveFromPlaylistAsync(string playlistId, IEnumerable<string> entryIds, CancellationToken ct = default) =>
        DeleteAsync($"/Playlists/{playlistId}/Items", new() { ["entryIds"] = string.Join(',', entryIds) }, ct);

    public Task MovePlaylistItemAsync(string playlistId, string entryId, int newIndex, CancellationToken ct = default) =>
        PostAsync($"/Playlists/{playlistId}/Items/{entryId}/Move/{newIndex}", ct: ct);

    // ---- Theme media and lyrics ----

    /// <summary>Theme songs for a title, inheriting the series' for seasons and episodes.</summary>
    public async Task<IReadOnlyList<BaseItem>> ThemeSongsAsync(string itemId, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Items/{itemId}/ThemeSongs", new() { ["userId"] = RequireUser(), ["inheritFromParent"] = "true" }, ct).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<BaseItem>> ThemeVideosAsync(string itemId, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"/Items/{itemId}/ThemeVideos", new() { ["userId"] = RequireUser(), ["inheritFromParent"] = "true" }, ct).ConfigureAwait(false)).Items;

    /// <summary>A song's lyrics, or null when the server has none.</summary>
    public async Task<SongLyrics?> LyricsAsync(string itemId, CancellationToken ct = default) =>
        await SendAsync(AuthorizedRequest(Url($"/Audio/{itemId}/Lyrics")), ct, allowNotFound: true).ConfigureAwait(false) is { } data
            ? Decode<SongLyrics>(data)
            : null;

    // ---- Media segments ----

    /// <summary>The marker kinds the player offers to skip.</summary>
    private static readonly HashSet<string> SkippableSegmentTypes = ["Intro", "Outro", "Preview", "Recap", "Commercial"];

    /// <summary>
    /// Intro, credits and similar markers for an item, when a provider has
    /// made them. An empty list is the normal "nothing to skip", as is a 404.
    /// No <c>includeSegmentTypes</c> filter is sent: the server wants each
    /// type as its own repeated parameter and rejects a comma-joined list
    /// with a 400, so all markers are fetched and the skippable kinds kept here.
    /// </summary>
    public async Task<IReadOnlyList<MediaSegment>> MediaSegmentsAsync(string itemId, CancellationToken ct = default)
    {
        if (!IsJellyfin)
        {
            var item = await ItemAsync(itemId, ct).ConfigureAwait(false);
            return SegmentsFromChapters(item.Chapters ?? [], itemId, item.RunTimeTicks);
        }
        if (await SendAsync(AuthorizedRequest(Url($"/MediaSegments/{itemId}")), ct, allowNotFound: true).ConfigureAwait(false) is not { } data) return [];
        return Decode<ItemsEnvelope<MediaSegment>>(data).Items.Where(s => SkippableSegmentTypes.Contains(s.Type)).ToList();
    }

    /// <summary>
    /// Emby has no media segments: it marks an intro (and where the credits
    /// begin) as chapters, which become the same Intro and Outro segments:
    /// the intro from IntroStart to IntroEnd, the credits from CreditsStart to
    /// the end. Chapters without those marks give nothing.
    /// </summary>
    public static IReadOnlyList<MediaSegment> SegmentsFromChapters(IReadOnlyList<ChapterInfo> chapters, string itemId, long? runTimeTicks)
    {
        long? Mark(string type) => chapters.FirstOrDefault(c => string.Equals(c.MarkerType, type, StringComparison.OrdinalIgnoreCase))?.StartPositionTicks;
        var segments = new List<MediaSegment>();
        if (Mark("IntroStart") is { } introStart && Mark("IntroEnd") is { } introEnd && introEnd > introStart)
        {
            segments.Add(new MediaSegment { Id = $"{itemId}-intro", ItemId = itemId, Type = "Intro", StartTicks = introStart, EndTicks = introEnd });
        }
        if (Mark("CreditsStart") is { } creditsStart && runTimeTicks is { } end && end > creditsStart)
        {
            segments.Add(new MediaSegment { Id = $"{itemId}-credits", ItemId = itemId, Type = "Outro", StartTicks = creditsStart, EndTicks = end });
        }
        return segments;
    }

    // ---- JellyDisc ----

    /// <summary>
    /// The resolved disc menu for a movie, series or season, or null when the
    /// server has none (404 is "nothing to show", never an error).
    /// </summary>
    public async Task<DiscMenuDocument?> DiscMenuAsync(string itemId, CancellationToken ct = default) =>
        IsJellyfin && await SendAsync(AuthorizedRequest(Url($"/DiscMenus/{itemId}/Menu")), ct, allowNotFound: true).ConfigureAwait(false) is { } data
            ? Decode<DiscMenuDocument>(data)
            : null;

    public Uri DiscAssetUrl(IEnumerable<string> segments) =>
        new(Root + "/DiscMenus/Assets/" + string.Join('/', segments.Select(Uri.EscapeDataString)));

    public Uri FanartUrl(string itemId, string fanartId) => Url($"/DiscMenus/Fanart/{itemId}/{fanartId}");

    public Uri ChapterImageUrl(string itemId, int index, long? stamp) =>
        Url($"/Items/{itemId}/Images/Chapter/{index}", new()
        {
            ["maxWidth"] = "480",
            ["tag"] = stamp?.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>A chapter's frame from Jellyfin's own chapter images, as JPEG for the player's chapter list.</summary>
    public Uri ChapterImageUrl(string itemId, int index, string tag) =>
        Url($"/Items/{itemId}/Images/Chapter/{index}", new()
        {
            ["maxWidth"] = "480",
            ["tag"] = tag,
            ["format"] = "Jpg",
        });

    /// <summary>One sheet of a title's trickplay frames (see <see cref="TrickplayInfo"/>).</summary>
    public Uri TrickplayTileUrl(string itemId, int width, int index, string mediaSourceId) =>
        Url($"/Videos/{itemId}/Trickplay/{width}/{index}.jpg", new() { ["MediaSourceId"] = mediaSourceId });

    // ---- Helpers ----

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The result, or null if the request fails for any reason but the
    /// caller cancelling: the C# for Swift's <c>try?</c>.
    /// </summary>
    private static async Task<T?> OrNullAsync<T>(Func<Task<T>> request, CancellationToken ct) where T : class
    {
        try { return await request().ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }
}
