using Aster.Core.Jellyfin;
using Aster.Core.Server;

namespace Aster.Core.Playback;

/// <summary>
/// Which tracks a title starts on, and what plays after it. The Apple app
/// keeps these on PlaybackCoordinator, shared by both of its players.
/// </summary>
public static class TrackRules
{
    /// <summary>
    /// The subtitle track for a title: the entry's explicit choice (-1 for
    /// none) wins, then the user's preferences, then the server's default.
    /// </summary>
    public static int ResolveSubtitle(PlaybackQueueItem entry, MediaSourceInfo source, IReadOnlyList<MediaStream> tracks, PlaybackPreferences preferences)
    {
        if (entry.SubtitleStreamIndex is { } explicitChoice) return explicitChoice;
        var serverDefault = source.DefaultSubtitleStreamIndex ?? -1;
        var language = preferences.SubtitleLanguage;

        // The first track in the preferred language, else the first at all.
        int? First(IEnumerable<MediaStream> candidates)
        {
            var list = candidates.ToList();
            return (list.FirstOrDefault(t => LanguageTag.SameLanguage(t.Language, language)) ?? list.FirstOrDefault())?.Index;
        }

        switch (preferences.SubtitleMode)
        {
            case SubtitleMode.None:
                return -1;
            case SubtitleMode.Always:
                var full = tracks.Where(t => t.IsForced != true).ToList();
                if (full.FirstOrDefault(t => LanguageTag.SameLanguage(t.Language, language)) is { } match) return match.Index ?? -1;
                return serverDefault >= 0 ? serverDefault : First(full) ?? First(tracks) ?? -1;
            case SubtitleMode.OnlyForced:
                return First(tracks.Where(t => t.IsForced == true)) ?? -1;
            default:
                return serverDefault;
        }
    }

    /// <summary>
    /// The first audio track in <paramref name="language"/>, preferring one
    /// the file marks as default; null with no preference or no such track.
    /// </summary>
    public static int? PreferredAudioTrack(IReadOnlyList<MediaStream> streams, string? language)
    {
        if (string.IsNullOrEmpty(language)) return null;
        var matching = streams.Where(s => s.Type == "Audio" && LanguageTag.SameLanguage(s.Language, language)).ToList();
        return (matching.FirstOrDefault(s => s.IsDefault == true) ?? matching.FirstOrDefault())?.Index;
    }

    /// <summary>
    /// Text tracks can be fetched as files; the rest (PGS, VobSub) are
    /// pictures the server has to burn into a converted stream.
    /// </summary>
    public static bool IsTextTrack(MediaStream track) => track.IsTextSubtitleStream ?? track.DeliveryMethod != "Encode";

    /// <summary>The user's track preferences, from their settings on the server.</summary>
    public static PlaybackPreferences Preferences(UserConfiguration? configuration) => new()
    {
        AudioLanguage = configuration?.AudioLanguagePreference,
        SubtitleLanguage = configuration?.SubtitleLanguagePreference,
        SubtitleMode = Enum.TryParse<SubtitleMode>(configuration?.SubtitleMode, out var mode) ? mode : SubtitleMode.Default,
    };

    /// <summary>
    /// The episode after <paramref name="episode"/> in its series, skipping
    /// missing ones and, unless it's a special itself, the specials.
    /// </summary>
    public static async Task<BaseItem?> NextEpisodeAsync(BaseItem episode, IMediaServer client, CancellationToken ct = default)
    {
        if (episode.SeriesId is not { } seriesId) return null;
        IReadOnlyList<BaseItem> all;
        try { all = await client.EpisodesAsync(seriesId, null, ct).ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        var position = all.Select((e, i) => (e, i)).FirstOrDefault(p => p.e.Id == episode.Id, (null!, -1)).Item2;
        if (position < 0) return null;
        var inSpecials = (episode.ParentIndexNumber ?? 1) == 0;
        return all.Skip(position + 1).FirstOrDefault(candidate =>
            candidate.LocationType != "Virtual" && (inSpecials || (candidate.ParentIndexNumber ?? 1) != 0));
    }
}
