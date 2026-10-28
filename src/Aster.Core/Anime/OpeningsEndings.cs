// Anime openings and endings, found in episodes' chapters, and the song card
// shown as one plays: a port of the Apple app's OpeningsEndings.swift and
// PlaybackCoordinator's themeMoments. Fansub and Blu-ray releases usually
// mark them ("OP", "Opening", "ED", "Ending"...). Chapters say where an OP
// is, not which song it is, so a new clip is kept each time the length
// changes noticeably: a rough sign the song changed.

using Aster.Core.Jellyfin;

namespace Aster.Core.Anime;

public enum ThemeKind
{
    Opening,
    Ending,
}

/// <summary>An opening or ending found in an episode's chapters.</summary>
public sealed record ThemeClip
{
    public required ThemeKind Kind { get; init; }
    public int Season { get; init; }

    /// <summary>1 for the season's first opening (or ending), 2 for the next...</summary>
    public int Number { get; init; }

    public required BaseItem Episode { get; init; }
    public int ChapterIndex { get; init; }
    public string? ChapterImageTag { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }

    public string Id => $"{Episode.Id}-{Kind}-{StartTicks}";

    public string Title => Number > 1 ? $"Season {Season} {Kind} {Number}" : $"Season {Season} {Kind}";

    public int Duration => (int)((EndTicks - StartTicks) / 10_000_000);
}

public static class OpeningsEndings
{
    // Whole words at the start of a chapter name. "OP"/"Opening" and
    // "ED"/"Ending" are definite; "Intro" and "Outro"/"Credits" are used only
    // when an episode has no definite one, since many releases call the cold
    // open "Intro".
    private static readonly HashSet<string> OpeningWords = ["op", "opening"];
    private static readonly HashSet<string> EndingWords = ["ed", "ending"];
    private static readonly HashSet<string> LooseOpeningWords = ["intro"];
    private static readonly HashSet<string> LooseEndingWords = ["outro", "credits"];

    /// <summary>Clips shorter or longer than this aren't really an OP or ED (a cold open marked "Intro", say).</summary>
    private const long ShortestTicks = 30 * 10_000_000L;
    private const long LongestTicks = 210 * 10_000_000L;

    /// <summary>A change in length bigger than this starts a new clip.</summary>
    private const long SameSongSlack = 3 * 10_000_000L;

    /// <summary>
    /// The openings and endings in <paramref name="episodes"/>, which must be
    /// in airing order, as <c>/Shows/{id}/Episodes</c> returns them. Specials
    /// (season 0) are skipped.
    /// </summary>
    public static IReadOnlyList<ThemeClip> Find(IEnumerable<BaseItem> episodes)
    {
        var clips = new List<ThemeClip>();
        // Every clip kept so far for each kind in each season: a length close
        // to any of them is the same song, even when lengths alternate.
        var kept = new Dictionary<(int, ThemeKind), List<ThemeClip>>();

        foreach (var episode in episodes)
        {
            if (episode.ParentIndexNumber is not (> 0 and var season) || episode.Chapters is not { Count: > 0 } unsorted) continue;
            var chapters = unsorted.OrderBy(c => c.StartPositionTicks).ToList();
            var marks = chapters.Select(c => Mark(c.Name)).ToList();
            var hasDefiniteOpening = marks.Any(m => m is (ThemeKind.Opening, true));
            var hasDefiniteEnding = marks.Any(m => m is (ThemeKind.Ending, true));

            for (var index = 0; index < chapters.Count; index++)
            {
                if (marks[index] is not var (kind, definite)) continue;
                // An "Intro" beside a real "OP" is the cold open.
                if (!definite && (kind == ThemeKind.Opening ? hasDefiniteOpening : hasDefiniteEnding)) continue;
                var start = chapters[index].StartPositionTicks;
                var end = index + 1 < chapters.Count ? chapters[index + 1].StartPositionTicks : episode.RunTimeTicks ?? 0;
                if (end - start is < ShortestTicks or > LongestTicks) continue;

                var earlier = kept.TryGetValue((season, kind), out var list) ? list : [];
                if (earlier.Any(c => Math.Abs((end - start) - (c.EndTicks - c.StartTicks)) <= SameSongSlack)) continue;
                var clip = new ThemeClip
                {
                    Kind = kind,
                    Season = season,
                    Number = earlier.Count + 1,
                    Episode = episode,
                    ChapterIndex = index,
                    ChapterImageTag = chapters[index].ImageTag,
                    StartTicks = start,
                    EndTicks = end,
                };
                clips.Add(clip);
                kept[(season, kind)] = [.. earlier, clip];
            }
        }
        // Season by season, each season's openings before its endings.
        return clips.OrderBy(c => c.Season).ThenBy(c => c.Kind).ThenBy(c => c.Number).ToList();
    }

    /// <summary>What a chapter name marks, and whether the name is definite.</summary>
    private static (ThemeKind Kind, bool Definite)? Mark(string? name)
    {
        var first = name?.ToLowerInvariant()
            .SplitWhere(c => !char.IsLetter(c))
            .FirstOrDefault(word => word.Length > 0);
        if (first is null) return null;
        if (OpeningWords.Contains(first)) return (ThemeKind.Opening, true);
        if (EndingWords.Contains(first)) return (ThemeKind.Ending, true);
        if (LooseOpeningWords.Contains(first)) return (ThemeKind.Opening, false);
        if (LooseEndingWords.Contains(first)) return (ThemeKind.Ending, false);
        return null;
    }

    private static string[] SplitWhere(this string text, Func<char, bool> isSeparator)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && !isSeparator(text[i])) continue;
            words.Add(text[start..i]);
            start = i + 1;
        }
        return [.. words];
    }
}

/// <summary>
/// What the lower-left card says while an opening or ending plays, like the
/// credit a music channel shows: which theme it is, the song, who performs
/// it, and the show it belongs to.
/// </summary>
/// <param name="Kicker">"Opening 1", "Ending 2 · Version 2".</param>
/// <param name="Title">The song, or the show when the song is unknown.</param>
/// <param name="Detail">"Season 2 · Episodes 1–12", "Season 1 · From S1, E3".</param>
public sealed record ThemeInfo(string Kicker, string Title, string? Artists = null, string? Series = null, string? Detail = null)
{
    /// <summary>A theme the OP/ED plugin knows: the song and who performs it.</summary>
    public static ThemeInfo From(OpEdTheme theme, string series, string? seasonName)
    {
        var kicker = $"{(theme.IsOpening ? "Opening" : "Ending")} {theme.Sequence}";
        if (theme.Version > 1) kicker += $" · Version {theme.Version}";
        if (!string.IsNullOrEmpty(theme.Variant)) kicker += $" · {theme.Variant}";
        if (theme.IsFull) kicker += " · Full Version";
        var parts = new[] { seasonName, theme.Episodes is { } episodes ? $"Episodes {episodes}" : null }.OfType<string>().ToList();
        return new ThemeInfo(
            kicker,
            theme.SongTitle ?? series,
            theme.Artists.Count == 0 ? null : string.Join(", ", theme.Artists),
            theme.SongTitle is null ? null : series,
            parts.Count == 0 ? null : string.Join(" · ", parts));
    }

    /// <summary>From AnimeThemes, when the server has no OP/ED plugin.</summary>
    public static ThemeInfo From(KnownTheme theme, string series, string? seasonName)
    {
        var parts = new[] { seasonName, theme.Episodes is { } episodes ? $"Episodes {episodes}" : null }.OfType<string>().ToList();
        return new ThemeInfo(
            $"{(theme.IsOpening ? "Opening" : "Ending")} {theme.Sequence}",
            theme.SongTitle ?? series,
            theme.Artists.Count == 0 ? null : string.Join(", ", theme.Artists),
            theme.SongTitle is null ? null : series,
            parts.Count == 0 ? null : string.Join(" · ", parts));
    }

    /// <summary>
    /// The AnimeThemes theme a clip is, when one's known: of the same kind,
    /// playing in the clip's episode, and of those the one in the fewest
    /// episodes (a season's own over one listed for the whole show), as the
    /// player matches a song card.
    /// </summary>
    public static KnownTheme? Match(ThemeClip clip, IReadOnlyList<KnownTheme> known) =>
        known.Where(t => t.IsOpening == (clip.Kind == ThemeKind.Opening) && t.PlaysInEpisode(clip.Episode.IndexNumber))
            .OrderBy(t => t.EpisodeCount)
            .FirstOrDefault();

    /// <summary>A clip found from chapters alone: which theme it is, and the show.</summary>
    public static ThemeInfo From(ThemeClip clip, string series)
    {
        var parts = new[] { $"Season {clip.Season}", clip.Episode.EpisodeCode is { } code ? $"From {code}" : null }.OfType<string>();
        return new ThemeInfo(clip.Number > 1 ? $"{clip.Kind} {clip.Number}" : clip.Kind.ToString(), series, Detail: string.Join(" · ", parts));
    }
}

/// <summary>An opening or ending inside the video playing, with its song card.</summary>
/// <param name="Start">Where it starts in the video, in seconds.</param>
/// <param name="End">Where it ends, in seconds.</param>
public sealed record ThemeMoment(double Start, double End, ThemeInfo Info)
{
    /// <summary>How long the card stays up, in seconds.</summary>
    public const double ShowFor = 16;

    /// <summary>
    /// Whether the card shows at <paramref name="seconds"/>: for the theme's
    /// first seconds and, when it's long enough for two, its last.
    /// </summary>
    public bool ShowsCardAt(double seconds)
    {
        if (!double.IsFinite(seconds)) return false;
        // Only inside the theme: an episode's opening comes after its start.
        var inside = seconds >= Start - 0.5 && seconds <= End;
        var atStart = inside && seconds - Start < ShowFor;
        var atEnd = inside && End - Start > ShowFor * 3 && End - seconds < ShowFor;
        return atStart || atEnd;
    }
}

public static class ThemeMoments
{
    /// <summary>
    /// Where an episode's openings and endings are, and what the card says.
    /// Placed by its OP and ED chapters; with none, by its skip markers, but
    /// only when the OP/ED plugin says a theme plays in it, so an ordinary
    /// show's intro gets no song card. The plugin names the song when it
    /// knows it; without the plugin, AnimeThemes' <paramref name="knownThemes"/> do.
    /// </summary>
    public static IReadOnlyList<ThemeMoment> ForEpisode(BaseItem item, IReadOnlyList<MediaSegment> segments, IReadOnlyList<OpEdTheme> pluginThemes,
        IReadOnlyList<KnownTheme>? knownThemes = null)
    {
        if (item.Kind != ItemKind.Episode) return [];
        var series = item.SeriesName ?? item.DisplayTitle;
        var playing = pluginThemes.Where(t => t.PlaysInEpisode == true).ToList();
        var known = (knownThemes ?? []).Where(t => t.PlaysInEpisode(item.IndexNumber)).OrderBy(t => t.EpisodeCount).ToList();
        var found = OpeningsEndings.Find([item])
            .Select(clip => (Opening: clip.Kind == ThemeKind.Opening, Start: clip.StartTicks, End: clip.EndTicks, Clip: (ThemeClip?)clip))
            .ToList();
        if (found.Count == 0 && (playing.Count > 0 || known.Count > 0))
        {
            foreach (var segment in segments.Where(s => s.Type is "Intro" or "Outro" && s.EndTicks > s.StartTicks))
            {
                found.Add((segment.Type == "Intro", segment.StartTicks, segment.EndTicks, null));
            }
        }
        return found.Select(entry =>
        {
            var info = playing.FirstOrDefault(t => t.IsOpening == entry.Opening) is { } theme
                ? ThemeInfo.From(theme, series, item.SeasonName)
                : known.FirstOrDefault(t => t.IsOpening == entry.Opening) is { } knownTheme
                    ? ThemeInfo.From(knownTheme, series, item.SeasonName)
                    : entry.Clip is { } clip
                        ? ThemeInfo.From(clip, series)
                        : new ThemeInfo(entry.Opening ? "Opening" : "Ending", series, Detail: item.EpisodeCode);
            return new ThemeMoment(entry.Start / 10_000_000.0, entry.End / 10_000_000.0, info);
        }).ToList();
    }

    /// <summary>
    /// A music video's card, for the whole video: the plugin's theme when
    /// it published the video, else the video's own title and artists.
    /// </summary>
    public static async Task<IReadOnlyList<ThemeMoment>> ForMusicVideoAsync(BaseItem item, JellyfinClient client, CancellationToken ct = default)
    {
        if (item.Kind != ItemKind.MusicVideo || item.RunTimeTicks is not (> 0 and var ticks)) return [];
        var end = ticks / 10_000_000.0;
        if (await client.OpEdThemeForLibraryItemAsync(item.Id, ct).ConfigureAwait(false) is { } theme)
        {
            // The plugin writes the show as the album.
            var series = item.Album ?? item.DisplayTitle;
            string? seasonName = null;
            if (theme.SeasonId is { } seasonId)
            {
                try { seasonName = (await client.ItemAsync(seasonId, ct).ConfigureAwait(false)).DisplayTitle; }
                catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException or TaskCanceledException) { }
            }
            return [new ThemeMoment(0, end, ThemeInfo.From(theme, series, seasonName))];
        }
        var artists = string.Join(", ", (item.Artists ?? []).Where(a => a.Length > 0));
        return [new ThemeMoment(0, end, new ThemeInfo("Music Video", item.DisplayTitle, artists.Length > 0 ? artists : null, item.Album))];
    }
}
