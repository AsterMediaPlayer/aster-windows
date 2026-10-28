// A card in an anime show's Openings and Endings rows: a theme from the
// OP/ED plugin, or a clip found in an episode's chapters. A port of the
// Apple app's ThemeCard and ThemeClipLockup, as they play with the Now
// Playing panel turned off: one at a time, full screen.

using System;
using System.Linq;
using Aster.Core.Anime;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

public partial class OpeningEndingCard : ObservableObject
{
    private readonly Action<PlaybackQueueItem> play;
    private readonly PlaybackQueueItem? entry;

    private OpeningEndingCard(Action<PlaybackQueueItem> play, PlaybackQueueItem? entry)
    {
        this.play = play;
        this.entry = entry;
    }

    public bool IsOpening { get; private init; }

    /// <summary>The theme's season, place and version, for putting the row in the show's order.</summary>
    public (int Season, int Sequence, int Version) Order { get; private init; }

    /// <summary>"OP 1", "ED 2 v2" on a plugin theme; a clip's length.</summary>
    public string? Badge { get; private init; }

    public string? Length { get; private init; }

    /// <summary>The song, or the theme's name when the plugin doesn't know it.</summary>
    public required string Title { get; init; }

    public string? Artists { get; private init; }

    /// <summary>"Season 2 · Episodes 1-12", "From S1, E3".</summary>
    public string? Detail { get; private init; }

    /// <summary>The chapter's frame, the episode's picture, or for a plugin theme the show's.</summary>
    public Uri? Image { get; private init; }

    /// <summary>A music note, before (or without) a picture.</summary>
    public static string Placeholder => "";

    /// <summary>Marked as a spoiler or NSFW: blurred, and played only on a second click.</summary>
    public bool IsFlagged { get; private init; }

    /// <summary>Its copy is still being made on the server; nothing to play yet.</summary>
    public bool IsPreparing { get; private init; }

    public bool IsAudioOnly { get; private init; }

    public bool CanPlay => entry is not null;

    /// <summary>"Spoiler", "NSFW", "Preparing…"; then, clicked once, what a second click does.</summary>
    public string? Notice => IsPreparing ? "Preparing…" : IsFlagged ? asking ? "Click again to play" : flagName : null;

    private string? flagName;
    private bool asking;

    /// <summary>A flagged card asks first: its first click only changes its notice.</summary>
    [RelayCommand]
    private void Play()
    {
        if (entry is null) return;
        if (IsFlagged && !asking)
        {
            asking = true;
            OnPropertyChanged(nameof(Notice));
            return;
        }
        asking = false;
        OnPropertyChanged(nameof(Notice));
        play(entry);
    }

    /// <summary>A plugin theme: its MP4, or its song when there's no video.</summary>
    /// <param name="seasonOrder">Where the theme's season comes in the show; -1 for a theme of the whole show.</param>
    public static OpeningEndingCard Theme(JellyfinClient client, OpEdTheme theme, BaseItem show, string? seasonName, int seasonOrder, Action<PlaybackQueueItem> play)
    {
        var series = show.DisplayTitle;
        var entry = theme.PlayablePath is { } path
            ? new PlaybackQueueItem
            {
                ItemId = theme.Id,
                MediaUrl = client.OpEdMediaUrl(path),
                MediaTitle = theme.SongLine ?? theme.Code,
                MediaSubtitle = $"{series} · {theme.Code}",
                ThemeCard = ThemeInfo.From(theme, series, seasonName),
            }
            : null;
        var badge = $"{(theme.IsOpening ? "OP" : "ED")} {theme.Sequence}" + (theme.Version > 1 ? $" v{theme.Version}" : "");
        var detail = string.Join(" · ", new[] { seasonName, theme.Episodes is { } episodes ? $"Episodes {episodes}" : null }.OfType<string>());
        return new OpeningEndingCard(play, entry)
        {
            IsOpening = theme.IsOpening,
            Order = (seasonOrder, theme.Sequence, theme.Version),
            Badge = badge,
            Title = theme.SongTitle ?? badge,
            Artists = theme.Artists.Count == 0 ? null : string.Join(", ", theme.Artists),
            Detail = detail.Length == 0 ? null : detail,
            Image = client.LandscapeUrl(show),
            IsFlagged = theme.Spoiler || theme.Nsfw,
            flagName = theme.Nsfw ? "NSFW" : "Spoiler",
            IsPreparing = theme.IsPreparing,
            IsAudioOnly = theme.IsAudioOnly,
        };
    }

    /// <summary>A clip of its episode: just that stretch, its resume point and watched state left alone.</summary>
    public static OpeningEndingCard Clip(IMediaServer client, ThemeClip clip, string series, Action<PlaybackQueueItem> play) =>
        new(play, new PlaybackQueueItem
        {
            ItemId = clip.Episode.Id,
            StartTicks = clip.StartTicks,
            ClipEndTicks = clip.EndTicks,
            ThemeCard = ThemeInfo.From(clip, series),
        })
        {
            IsOpening = clip.Kind == ThemeKind.Opening,
            Length = $"{clip.Duration / 60}:{clip.Duration % 60:00}",
            Title = clip.Title,
            Detail = clip.Episode.EpisodeCode is { } code ? $"From {code}" : clip.Episode.DisplayTitle,
            Image = clip.ChapterImageTag is { } tag
                ? client.ChapterImageUrl(clip.Episode.Id, clip.ChapterIndex, tag)
                : client.LandscapeUrl(clip.Episode),
        };
}
