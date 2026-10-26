using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Anime;

/// <summary>One opening or ending, as AnimeThemes (animethemes.moe) knows it.</summary>
/// <param name="Episodes">The episodes it plays in, as AnimeThemes writes them: "1-6, 8-".</param>
public sealed record KnownTheme(bool IsOpening, int Sequence, string? SongTitle, IReadOnlyList<string> Artists, string? Episodes)
{
    /// <summary>Whether it plays in episode <paramref name="number"/>; any episode when unknown.</summary>
    public bool PlaysInEpisode(int? number) =>
        number is not { } n || string.IsNullOrWhiteSpace(Episodes) || Ranges(Episodes).Any(r => n >= r.Low && n <= r.High);

    /// <summary>
    /// How many episodes it plays in, to prefer the one made for an episode
    /// (Frieren's first, which ends with "bliss" alone) over the season's.
    /// </summary>
    public long EpisodeCount => string.IsNullOrWhiteSpace(Episodes) ? long.MaxValue : Ranges(Episodes).Sum(r => (long)r.High - r.Low + 1);

    /// <summary>"1-6, 8-" as 1–6 and 8 on; "7" as 7 alone.</summary>
    public static IReadOnlyList<(int Low, int High)> Ranges(string text) =>
        text.Split(',').Select(part =>
        {
            var bounds = part.Split('-').Select(b => b.Trim()).ToArray();
            if (!int.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var low)) return ((int, int)?)null;
            if (bounds.Length == 1) return (low, low);
            var high = int.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : int.MaxValue;
            return high >= low ? (low, high) : null;
        }).OfType<(int, int)>().ToList();
}

/// <summary>
/// The song card's details from AnimeThemes when the server has no OP/ED
/// plugin to give them: on Emby, and on Jellyfin without the plugin. A show
/// is found by its season's AniDB id when the server knows it (Jellyfin
/// with Shoko), else by its name and the year the season began, since
/// AnimeThemes lists each season apart and by its Japanese name. Each show
/// is asked about once per launch. The Apple app's AnimeThemesLookup.
/// </summary>
public static class AnimeThemesLookup
{
    private const string Base = "https://api.animethemes.moe";
    private const string Include = "animethemes.song.performances.artist,animethemes.animethemeentries";

    /// <summary>What each show turned out to have, nothing included.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyList<KnownTheme>> Found = new();

    /// <summary>Its own client: nothing of the server's (its token above all) goes to another site.</summary>
    /// <remarks>AnimeThemes refuses a request that doesn't say what it's from (403).</remarks>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", $"{JellyfinDevice.ClientName}/{JellyfinDevice.Version}" } },
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The episode's openings and endings, for a server without the OP/ED
    /// plugin: only for anime (matched by Shoko, or with OP/ED chapters).
    /// </summary>
    public static async Task<IReadOnlyList<KnownTheme>> ForEpisodeAsync(BaseItem item, JellyfinClient client, CancellationToken ct = default)
    {
        if (item.Kind != ItemKind.Episode || item.SeriesName is not { } seriesName
            || !(item.IsAnime || OpeningsEndings.Find([item]).Count > 0)) return [];
        BaseItem? season = null;
        if (item.SeasonId is { } seasonId)
        {
            try { season = await client.ItemAsync(seasonId, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
        }
        var aniDb = season?.ProviderIds?.GetValueOrDefault("AniDB");
        var year = season?.ProductionYear ?? Year(season?.PremiereDate) ?? Year(item.PremiereDate) ?? item.ProductionYear;
        return await ThemesAsync(aniDb, seriesName, year, ct).ConfigureAwait(false);
    }

    private static int? Year(string? date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.Year : null;

    /// <summary>The themes of a show: by <paramref name="aniDbId"/>, else by <paramref name="seriesName"/> and the year it began.</summary>
    public static async Task<IReadOnlyList<KnownTheme>> ThemesAsync(string? aniDbId, string seriesName, int? year, CancellationToken ct = default)
    {
        var key = aniDbId is not null ? $"anidb:{aniDbId}" : $"name:{seriesName.ToLowerInvariant()}|{year ?? 0}";
        if (Found.TryGetValue(key, out var cached)) return cached;
        var themes = aniDbId is not null ? await ByAniDbAsync(aniDbId, ct).ConfigureAwait(false) : await ByNameAsync(seriesName, year, ct).ConfigureAwait(false);
        Found[key] = themes;
        return themes;
    }

    private static async Task<IReadOnlyList<KnownTheme>> ByAniDbAsync(string id, CancellationToken ct)
    {
        var reply = await GetAsync<AnimeList>("/anime", new()
        {
            ["filter[has]"] = "resources",
            ["filter[site]"] = "AniDB",
            ["filter[external_id]"] = id,
            ["include"] = Include,
        }, ct).ConfigureAwait(false);
        return reply?.Anime.FirstOrDefault() is { } anime ? Themes(anime) : [];
    }

    /// <summary>
    /// The show that began in <paramref name="year"/> (or the year before: a
    /// season running over New Year began in the earlier one) among those the
    /// name finds; with no year, the first found.
    /// </summary>
    private static async Task<IReadOnlyList<KnownTheme>> ByNameAsync(string name, int? year, CancellationToken ct)
    {
        var search = await GetAsync<SearchReply>("/search", new() { ["q"] = name, ["fields[search]"] = "anime" }, ct).ConfigureAwait(false);
        var candidates = search?.Search.Anime ?? [];
        var pick = year is { } y
            ? candidates.FirstOrDefault(a => a.Year == y) ?? candidates.FirstOrDefault(a => a.Year == y - 1)
            : candidates.FirstOrDefault();
        if (pick is null) return [];
        var reply = await GetAsync<AnimeReply>($"/anime/{Uri.EscapeDataString(pick.Slug)}", new() { ["include"] = Include }, ct).ConfigureAwait(false);
        return reply is null ? [] : Themes(reply.Anime);
    }

    /// <summary>The themes in an <c>/anime/{slug}</c> reply, for tests.</summary>
    public static IReadOnlyList<KnownTheme> Read(string animeReply) =>
        JsonSerializer.Deserialize<AnimeReply>(animeReply, Json) is { } reply ? Themes(reply.Anime) : [];

    private static IReadOnlyList<KnownTheme> Themes(Anime anime) =>
        (anime.AnimeThemes ?? []).Where(t => t.Type is "OP" or "ED").Select(theme => new KnownTheme(
            theme.Type == "OP",
            theme.Sequence ?? 1,
            theme.Song?.Title,
            (theme.Song?.Performances ?? []).Select(p => p.Artist?.Name).OfType<string>().ToList(),
            (theme.AnimeThemeEntries ?? []).Select(e => e.Episodes).OfType<string>().FirstOrDefault())).ToList();

    private static async Task<T?> GetAsync<T>(string path, Dictionary<string, string> query, CancellationToken ct) where T : class
    {
        var url = Base + path + "?" + string.Join("&", query.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(body, Json, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // ---- AnimeThemes' JSON ----

    private sealed record AnimeList(IReadOnlyList<Anime> Anime);
    private sealed record AnimeReply(Anime Anime);
    private sealed record SearchReply(SearchResults Search);
    private sealed record SearchResults(IReadOnlyList<Anime> Anime);
    private sealed record Anime(string Name, string Slug, int? Year, IReadOnlyList<Theme>? AnimeThemes);
    private sealed record Theme(string Type, int? Sequence, Song? Song, IReadOnlyList<Entry>? AnimeThemeEntries);
    private sealed record Song(string? Title, IReadOnlyList<Performance>? Performances);
    private sealed record Performance(Artist? Artist);
    private sealed record Artist(string Name);
    private sealed record Entry(string? Episodes);
}
