// A Plex Media Server, through the same IMediaServer calls as Jellyfin:
// Plex's answers are turned into the app's BaseItems. Every request carries
// the server's token and this device's Plex identity as headers; addresses
// handed to players carry the token in the query instead. A port of the
// Apple app's PlexClient.swift: browsing, watched and pictures. Playing is
// in PlexClient.Playback.cs.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aster.Core.Jellyfin;
using Aster.Core.Server;

namespace Aster.Core.Plex;

/// <summary>Why a Plex request failed, in words fit to show.</summary>
public static class PlexErrors
{
    public const string NotBuilt = "This isn't available on Plex yet.";
    public const string PinNeeded = "Enter the PIN.";
    public const string WrongPin = "That PIN isn't right.";

    public static string Http(int code) => code switch
    {
        401 or 403 => "Plex no longer accepts this sign-in. Sign in with Plex again from the server's page in Settings.",
        404 => "That's no longer on the Plex server.",
        503 => "The Plex server is busy or still starting up. Try again in a moment.",
        _ => $"The Plex server answered with an error ({code}).",
    };

    public static string Unreachable(string serverName) =>
        $"{serverName} isn't answering. Check that it's on; away from home, it also needs Remote Access turned on (in Plex's settings for the server, under Remote Access).";

    /// <summary>Errors that mean nothing answered at the server's address.</summary>
    public static bool MeansUnreachable(Exception error) => error switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } => true,
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        TimeoutException => true,
        _ => false,
    };
}

/// <summary>This device as Plex knows it.</summary>
public static class PlexDevice
{
    public const string Product = "Aster";

    /// <summary>Stable per install, as Plex asks: the same id Jellyfin sees.</summary>
    public static string ClientIdentifier => JellyfinDevice.DeviceId;

    public static string Platform => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "macOS";

    /// <summary>The headers plex.tv and servers want on every request.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Headers(string? token)
    {
        yield return new("X-Plex-Product", Product);
        yield return new("X-Plex-Version", JellyfinDevice.Version);
        yield return new("X-Plex-Client-Identifier", ClientIdentifier);
        yield return new("X-Plex-Device-Name", JellyfinDevice.DeviceName);
        yield return new("X-Plex-Platform", Platform);
        if (token is not null) yield return new("X-Plex-Token", token);
    }
}

public sealed partial class PlexClient : IMediaServer
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly HttpClient http;

    /// <param name="userId">The Plex Home user (or account) signed in.</param>
    /// <param name="serverName">The server's name, for messages.</param>
    /// <param name="http">For tests; the app shares one client.</param>
    public PlexClient(Uri baseUrl, string accessToken, string? userId, string? serverName = null, HttpClient? http = null)
    {
        BaseUrl = baseUrl;
        AccessToken = accessToken;
        UserId = userId;
        ServerName = string.IsNullOrEmpty(serverName) ? "The Plex server" : serverName;
        this.http = http ?? SharedHttp;
    }

    public Uri BaseUrl { get; }
    public string AccessToken { get; }
    public string? UserId { get; }
    public string ServerName { get; }
    public ServerKind Kind => ServerKind.Plex;

    /// <summary>Browsing, playing, resume and watched; the rest comes later.</summary>
    public ServerCapabilities Capabilities => ServerCapabilities.None;

    // ---- Requests ----

    public Uri Url(string path, IReadOnlyDictionary<string, string?>? query = null)
    {
        var root = BaseUrl.GetLeftPart(UriPartial.Authority);
        var pairs = (query ?? new Dictionary<string, string?>())
            .Where(p => p.Value is not null)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}")
            .ToList();
        return new Uri(root + (path.StartsWith('/') ? path : "/" + path) + (pairs.Count > 0 ? "?" + string.Join("&", pairs) : ""));
    }

    private HttpRequestMessage Request(Uri url, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in PlexDevice.Headers(AccessToken)) request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    /// <summary>
    /// Sends <paramref name="request"/>: an error status, or nothing
    /// answering, becomes a JellyfinException whose message says why that
    /// usually happens.
    /// </summary>
    private async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw JellyfinException.Explained((int)response.StatusCode, PlexErrors.Http((int)response.StatusCode));
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && PlexErrors.MeansUnreachable(error))
        {
            throw JellyfinException.Explained(null, PlexErrors.Unreachable(ServerName));
        }
        finally
        {
            request.Dispose();
        }
    }

    public async Task<byte[]> DataAsync(Uri url, bool fresh = false, CancellationToken ct = default)
    {
        if (fresh) ImageCache.Remove(url);
        else if (await ImageCache.ReadAsync(url, ct).ConfigureAwait(false) is { } cached) return cached;
        var data = await SendAsync(Request(url), ct).ConfigureAwait(false);
        await ImageCache.WriteAsync(url, data, ct).ConfigureAwait(false);
        return data;
    }

    // ---- Pictures ----

    /// <summary>
    /// <paramref name="tag"/> is the picture's path on the server (a thumb or
    /// art), resized by the server's photo transcoder to fit inside the size
    /// asked for (the free side is left three times as long, so it never
    /// decides). Logos come as PNG, keeping their transparency.
    /// </summary>
    public Uri ImageUrl(string itemId, ImageKind kind, string? tag = null, int? index = null, int? maxWidth = null, int? maxHeight = null, int quality = 90) =>
        Url("/photo/:/transcode", new Dictionary<string, string?>
        {
            ["url"] = tag,
            ["width"] = Number(maxWidth) ?? Number(maxHeight * 3),
            ["height"] = Number(maxHeight) ?? Number(maxWidth * 3),
            ["minSize"] = "0",
            ["upscale"] = "1",
            ["quality"] = Number(quality),
            ["format"] = kind == ImageKind.Logo ? "png" : null,
            ["X-Plex-Token"] = AccessToken,
        });

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    // ---- Asking ----

    /// <summary>One page of a list, by Plex's paging headers (as query parameters they're ignored).</summary>
    private async Task<PlexContainer> ContainerAsync(string path, IReadOnlyDictionary<string, string?>? query = null, int? start = null, int? size = null, CancellationToken ct = default)
    {
        var request = Request(Url(path, query));
        if (start is { } from) request.Headers.TryAddWithoutValidation("X-Plex-Container-Start", from.ToString(CultureInfo.InvariantCulture));
        if (size is { } count) request.Headers.TryAddWithoutValidation("X-Plex-Container-Size", count.ToString(CultureInfo.InvariantCulture));
        byte[] data;
        try { data = await SendAsync(request, ct).ConfigureAwait(false); }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Plex: {path} -> {error.Message}");
            throw;
        }
        try
        {
            var container = JsonSerializer.Deserialize<PlexReply>(data, PlexJson.Options)?.MediaContainer ?? new PlexContainer();
            System.Diagnostics.Debug.WriteLine($"Plex: {path} -> {container.Metadata?.Count ?? 0} titles");
            return container;
        }
        catch (JsonException error)
        {
            System.Diagnostics.Debug.WriteLine($"Plex: {path} couldn't be read: {error.Message}");
            throw JellyfinException.Decoding(error.Message, error);
        }
    }

    private async Task<IReadOnlyList<BaseItem>> TitlesAsync(string path, IReadOnlyDictionary<string, string?>? query = null, int? start = null, int? size = null, CancellationToken ct = default) =>
        (await ContainerAsync(path, query, start, size, ct).ConfigureAwait(false)).Metadata?.Select(PlexMapper.Item).ToList() ?? [];

    /// <summary>The server's movie and TV libraries, asked once.</summary>
    private Task<IReadOnlyList<PlexDirectory>>? sections;

    private Task<IReadOnlyList<PlexDirectory>> SectionsAsync(CancellationToken ct)
    {
        var known = sections;
        if (known is { IsCompletedSuccessfully: true } || known is { IsCompleted: false }) return known;
        return sections = LoadSectionsAsync(ct);
    }

    private async Task<IReadOnlyList<PlexDirectory>> LoadSectionsAsync(CancellationToken ct)
    {
        var all = (await ContainerAsync("/library/sections", ct: ct).ConfigureAwait(false)).Directory ?? [];
        // Music and photos come later on Plex.
        return all.Where(s => s.Type is "movie" or "show").ToList();
    }

    /// <summary>Plex's number for a kind of title, in a section's listing.</summary>
    private static string? TypeNumber(ItemKind kind) => kind switch
    {
        ItemKind.Movie => "1",
        ItemKind.Series => "2",
        ItemKind.Season => "3",
        ItemKind.Episode => "4",
        ItemKind.BoxSet => "18",
        _ => null,
    };

    private static string? SortParameter(Browsing.LibrarySort? sort, bool? descending)
    {
        if (sort is not { } chosen) return null;
        var field = chosen switch
        {
            Browsing.LibrarySort.DateAdded => "addedAt",
            Browsing.LibrarySort.ReleaseDate => "originallyAvailableAt",
            Browsing.LibrarySort.Rating => "audienceRating",
            _ => "titleSort",
        };
        return descending == true ? field + ":desc" : field;
    }

    // ---- Browsing ----

    public async Task<IReadOnlyList<BaseItem>> UserViewsAsync(CancellationToken ct = default) =>
        (await SectionsAsync(ct).ConfigureAwait(false)).Select(PlexMapper.Library).ToList();

    public async Task<BaseItem> ItemAsync(string id, CancellationToken ct = default)
    {
        var found = await ContainerAsync($"/library/metadata/{id}", new Dictionary<string, string?>
        {
            ["includeGuids"] = "1",
            ["includeMarkers"] = "1",
            ["includeChapters"] = "1",
        }, ct: ct).ConfigureAwait(false);
        return found.Metadata?.FirstOrDefault() is { } metadata ? PlexMapper.Item(metadata) : throw JellyfinException.Explained(404, PlexErrors.Http(404));
    }

    public async Task<ItemsResult> ItemsAsync(ItemQuery query, CancellationToken ct = default)
    {
        // Plex keeps no favourites.
        if (query.FavoritesOnly) return new ItemsResult { Items = [], TotalRecordCount = 0 };
        if (query.Ids.Count > 0)
        {
            var titles = await TitlesAsync($"/library/metadata/{string.Join(",", query.Ids)}", ct: ct).ConfigureAwait(false);
            return new ItemsResult { Items = titles, TotalRecordCount = titles.Count };
        }
        var all = await SectionsAsync(ct).ConfigureAwait(false);
        var parameters = new Dictionary<string, string?> { ["sort"] = SortParameter(query.Sort, query.Descending) };
        if (query.Kinds.Count > 0) parameters["type"] = TypeNumber(query.Kinds[0]);
        if (query.PersonId is not null) parameters["actor"] = query.PersonId;

        // A collection's or folder's own titles.
        if (query.ParentId is { } parentId && all.All(s => s.Key != parentId))
        {
            var children = await TitlesAsync($"/library/metadata/{parentId}/children", ct: ct).ConfigureAwait(false);
            return new ItemsResult { Items = children, TotalRecordCount = children.Count };
        }

        // One library, or every library holding these kinds, one after
        // another: a page that runs past the end of one carries on into the next.
        IEnumerable<PlexDirectory> wanted;
        if (query.ParentId is { } library)
        {
            wanted = all.Where(s => s.Key == library);
        }
        else
        {
            var types = query.Kinds.Select(k => k switch
            {
                ItemKind.Movie => "movie",
                ItemKind.Series or ItemKind.Season or ItemKind.Episode => "show",
                _ => null,
            }).OfType<string>().ToHashSet();
            wanted = all.Where(s => types.Count == 0 || (s.Type is { } type && types.Contains(type)));
        }
        var start = query.StartIndex ?? 0;
        var remaining = query.Limit ?? int.MaxValue;
        var found = new List<BaseItem>();
        var total = 0;
        foreach (var section in wanted)
        {
            var path = $"/library/sections/{section.Key}/all";
            var size = (await ContainerAsync(path, parameters, start: 0, size: 0, ct: ct).ConfigureAwait(false)).TotalSize ?? 0;
            total += size;
            if (remaining <= 0 || start >= size)
            {
                start = Math.Max(0, start - size);
                continue;
            }
            var page = await TitlesAsync(path, parameters, start, Math.Min(remaining, size - start), ct).ConfigureAwait(false);
            found.AddRange(page);
            remaining -= page.Count;
            start = 0;
        }
        return new ItemsResult { Items = found, TotalRecordCount = total };
    }

    /// <summary>
    /// Newest first: films by when they were added, shows by their newest
    /// episode (as Jellyfin's Recently Added groups episodes by show).
    /// <paramref name="includeTypes"/> as Jellyfin names them: "Movie", "Series".
    /// </summary>
    public async Task<IReadOnlyList<BaseItem>> LatestAsync(string? parentId, int limit = 20, string? includeTypes = null, CancellationToken ct = default)
    {
        var all = await SectionsAsync(ct).ConfigureAwait(false);
        var wantsMovies = includeTypes is null || includeTypes.Contains("Movie", StringComparison.Ordinal);
        var wantsShows = includeTypes is null || includeTypes.Contains("Series", StringComparison.Ordinal);
        var wanted = parentId is not null
            ? all.Where(s => s.Key == parentId)
            : all.Where(s => s.Type == "movie" ? wantsMovies : wantsShows);
        var found = new List<BaseItem>();
        foreach (var section in wanted)
        {
            var isShows = section.Type == "show";
            found.AddRange(await TitlesAsync($"/library/sections/{section.Key}/all", new Dictionary<string, string?>
            {
                ["type"] = isShows ? "2" : "1",
                ["sort"] = isShows ? "episode.addedAt:desc" : "addedAt:desc",
            }, start: 0, size: limit, ct: ct).ConfigureAwait(false));
        }
        return found.Take(limit).ToList();
    }

    /// <summary>What's partway through, everywhere or in one library.</summary>
    public async Task<IReadOnlyList<BaseItem>> ResumeAsync(int limit = 20, string? parentId = null, CancellationToken ct = default)
    {
        var hubs = (await ContainerAsync("/hubs/continueWatching", new Dictionary<string, string?>
        {
            ["count"] = Number(parentId is null ? limit : limit * 4),
        }, ct: ct).ConfigureAwait(false)).Hub ?? [];
        int? section = int.TryParse(parentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        return hubs.SelectMany(h => h.Metadata ?? [])
            .Where(m => section is null || m.LibrarySectionID == section)
            .Select(PlexMapper.Item)
            .Where(i => (i.UserData?.PlaybackPositionTicks ?? 0) > 0)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// The next episode to watch: of one show, or across them all (Plex's On
    /// Deck, less what's partway through, which Continue Watching has).
    /// </summary>
    public async Task<IReadOnlyList<BaseItem>> NextUpAsync(int limit = 20, string? seriesId = null, string? parentId = null, CancellationToken ct = default)
    {
        if (seriesId is not null)
        {
            var episodes = await TitlesAsync($"/library/metadata/{seriesId}/allLeaves", ct: ct).ConfigureAwait(false);
            var next = episodes.FirstOrDefault(e => (e.UserData?.PlaybackPositionTicks ?? 0) > 0)
                ?? episodes.FirstOrDefault(e => e.UserData?.Played != true);
            return next is null ? [] : [next];
        }
        var deck = await TitlesAsync(parentId is null ? "/library/onDeck" : $"/library/sections/{parentId}/onDeck", start: 0, size: limit * 2, ct: ct).ConfigureAwait(false);
        return deck.Where(e => e.Kind == ItemKind.Episode && (e.UserData?.PlaybackPositionTicks ?? 0) == 0).Take(limit).ToList();
    }

    public async Task<IReadOnlyList<BaseItem>> SeasonsAsync(string seriesId, CancellationToken ct = default) =>
        (await TitlesAsync($"/library/metadata/{seriesId}/children", ct: ct).ConfigureAwait(false)).Where(s => s.Kind == ItemKind.Season).ToList();

    public Task<IReadOnlyList<BaseItem>> EpisodesAsync(string seriesId, string? seasonId, CancellationToken ct = default) =>
        TitlesAsync(seasonId is not null ? $"/library/metadata/{seasonId}/children" : $"/library/metadata/{seriesId}/allLeaves", ct: ct);

    public Task<IReadOnlyList<BaseItem>> SimilarAsync(string id, int limit = 20, CancellationToken ct = default) =>
        TitlesAsync($"/library/metadata/{id}/similar", new Dictionary<string, string?> { ["count"] = Number(limit) }, ct: ct);

    public Task<IReadOnlyList<BaseItem>> SpecialFeaturesAsync(string id, CancellationToken ct = default) =>
        TitlesAsync($"/library/metadata/{id}/extras", ct: ct);

    /// <summary>Films, shows and episodes, in that order.</summary>
    public async Task<IReadOnlyList<BaseItem>> SearchAsync(string term, int limit = 60, CancellationToken ct = default)
    {
        var hubs = (await ContainerAsync("/hubs/search", new Dictionary<string, string?>
        {
            ["query"] = term,
            ["limit"] = Number(limit),
        }, ct: ct).ConfigureAwait(false)).Hub ?? [];
        string[] order = ["movie", "show", "episode"];
        return hubs.Where(h => order.Contains(h.Type))
            .OrderBy(h => Array.IndexOf(order, h.Type))
            .SelectMany(h => (h.Metadata ?? []).Select(PlexMapper.Item))
            .ToList();
    }

    /// <summary>
    /// Every episode in airing order, with its chapters. Plex lists chapters
    /// only for one title at a time; this comes with Plex playback, which
    /// plays the clips it finds.
    /// </summary>
    public Task<IReadOnlyList<BaseItem>> EpisodesWithChaptersAsync(string seriesId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BaseItem>>([]);

    /// <summary>Plex keeps no favourites (its watchlist is plex.tv's, for later).</summary>
    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default) =>
        throw JellyfinException.Explained(null, PlexErrors.NotBuilt);

    public async Task SetPlayedAsync(string id, bool played, CancellationToken ct = default) =>
        await SendAsync(Request(Url(played ? "/:/scrobble" : "/:/unscrobble", new Dictionary<string, string?>
        {
            ["key"] = id,
            ["identifier"] = "com.plexapp.plugins.library",
        })), ct).ConfigureAwait(false);

    /// <summary>Plex has no per-device sign-in to end on the server.</summary>
    public Task LogoutAsync() => Task.CompletedTask;

    // ---- Anime ----

    /// <summary>Libraries matched by an anime agent: Shoko, HAMA or AniDB.</summary>
    public async Task<IReadOnlyList<BaseItem>> AnimeLibrariesAsync(IEnumerable<BaseItem> views, CancellationToken ct = default)
    {
        IReadOnlyList<PlexDirectory> all;
        try { all = await SectionsAsync(ct).ConfigureAwait(false); }
        catch (JellyfinException) { return []; }
        var anime = all.Where(s => (s.Agent ?? "").ToLowerInvariant() is var agent && (agent.Contains("shoko") || agent.Contains("hama") || agent.Contains("anidb")))
            .Select(s => s.Key)
            .ToHashSet();
        return views.Where(v => anime.Contains(v.Id)).ToList();
    }

    public async Task<IReadOnlySet<string>> ItemIdsAsync(IEnumerable<string> libraryIds, string types, CancellationToken ct = default)
    {
        var typeNumbers = new List<string>();
        if (types.Contains("Series", StringComparison.Ordinal)) typeNumbers.Add("2");
        if (types.Contains("Movie", StringComparison.Ordinal)) typeNumbers.Add("1");
        var chunks = await Task.WhenAll(libraryIds.SelectMany(library => typeNumbers.Select(async type =>
        {
            try
            {
                var found = await ContainerAsync($"/library/sections/{library}/all", new Dictionary<string, string?> { ["type"] = type }, ct: ct).ConfigureAwait(false);
                return found.Metadata?.Select(m => m.RatingKey) ?? [];
            }
            catch (JellyfinException) { return []; }
        }))).ConfigureAwait(false);
        return chunks.SelectMany(ids => ids).ToHashSet();
    }
}
