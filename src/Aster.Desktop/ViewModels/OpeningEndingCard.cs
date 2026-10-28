// A card in an anime show's Openings and Endings rows: a theme from the
// OP/ED plugin, or a clip found in an episode's chapters. A port of the
// Apple app's ThemeCard and ThemeClipLockup: a click plays it in the Now
// Playing panel, or with the panel turned off, full screen. A plugin theme
// plays the version Settings prefers (TV size, full, or music video, or the
// nearest it has), and its menu (a right click) plays any other.

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
    private readonly Action<OpeningEndingCard, ThemeCut?> play;

    /// <summary>What a version plays (null: the one Settings prefers); null while it's still being made.</summary>
    private readonly Func<ThemeCut?, PlaybackQueueItem?> entryFor;

    /// <summary>A plugin theme, as the server sends it (the TV size); null for a clip.</summary>
    private readonly OpEdTheme? theme;

    private OpeningEndingCard(Action<OpeningEndingCard, ThemeCut?> play, Func<ThemeCut?, PlaybackQueueItem?> entryFor, OpEdTheme? theme = null)
    {
        this.play = play;
        this.entryFor = entryFor;
        this.theme = theme;
    }

    public bool IsOpening { get; private init; }

    /// <summary>What it plays, in the version Settings prefers; null while it's still being made.</summary>
    public PlaybackQueueItem? Entry => entryFor(null);

    /// <summary>The TV size: what Watch OP and Watch ED play.</summary>
    public PlaybackQueueItem? TvSizeEntry => entryFor(ThemeCut.TvSize);

    /// <summary>A version of it (null: the one Settings prefers).</summary>
    public PlaybackQueueItem? EntryFor(ThemeCut? cut) => entryFor(cut);

    /// <summary>The plugin theme in that version (null: Settings'); null for a clip, or a version it hasn't got.</summary>
    private OpEdTheme? ThemeIn(ThemeCut? cut) =>
        theme is null ? null : cut is { } wanted ? theme.WithCut(wanted) : theme.Preferring(Aster.Core.AppSettings.Current.ThemeCut);

    /// <summary>A plugin theme's season (null for one of the whole show).</summary>
    public string? SeasonId { get; private init; }

    /// <summary>A clip's season number.</summary>
    public int? SeasonNumber { get; private init; }

    /// <summary>As the Now Playing panel plays it, in the version Settings prefers; null when there's nothing to play yet.</summary>
    public Aster.Desktop.Audio.ThemeDeckEntry? DeckEntry => DeckEntryFor(null);

    /// <summary>As the Now Playing panel plays a version of it: "Yuusha (Full)".</summary>
    public Aster.Desktop.Audio.ThemeDeckEntry? DeckEntryFor(ThemeCut? cut)
    {
        if (entryFor(cut) is not { } entry) return null;
        var version = ThemeIn(cut);
        var title = version?.PlayedCut.Label is { } label ? $"{Title} ({label})" : Title;
        return new($"{entry.ItemId}-{entry.StartTicks}", entry, title, Artists ?? Detail, entry.ThemeCard, Image,
            HasVideo: version is null ? !IsAudioOnly : !version.IsAudioOnly, PlaysUnasked: !IsFlagged);
    }

    /// <summary>The plugin has its full-length song, or its music video (made, or being made).</summary>
    public bool HasFull => theme is { } t && (t.HasFullVersion == true || t.WithCut(ThemeCut.Full) is not null);
    public bool HasMusicVideo => theme is { } t && (t.HasMusicVideo == true || t.WithCut(ThemeCut.MusicVideo) is not null);

    /// <summary>The music video the OP/ED plugin published it as, in Settings' version; null for a clip, or one not published.</summary>
    public string? PublishedId => ThemeIn(null)?.LibraryItemId;

    /// <summary>Opens Add to Playlist for its published music video; set by the page.</summary>
    public Action<System.Collections.Generic.IReadOnlyList<string>>? AddToPlaylist { get; set; }

    /// <summary>
    /// The card's menu (a right click): every version it can play, the one
    /// Settings prefers ticked, and those still being made (which do
    /// nothing yet), when it has more than the TV size; then Add to Playlist
    /// for its published music video.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<TrackChoice> Menu
    {
        get
        {
            if (theme is null) return [];
            var preferred = ThemeIn(null)?.PlayedCut;
            var choices = theme.AvailableCuts.Select((cut, i) => new TrackChoice(i, cut.PlayTitle, cut == preferred, new RelayCommand(() => PlayCut(cut))))
                .Concat(theme.PreparingCuts.Select((cut, i) => new TrackChoice(100 + i, $"{cut.SettingTitle} (Preparing…)", false, new RelayCommand(() => { }, () => false))))
                .ToList();
            if (choices.Count < 2) choices.Clear();
            if (AddToPlaylist is { } add && PublishedId is { } id) choices.Add(new TrackChoice(200, "Add to Playlist…", false, new RelayCommand(() => add([id]))));
            return choices;
        }
    }

    public bool HasMenu => Menu.Count > 0;

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

    public bool CanPlay => TvSizeEntry is not null;

    /// <summary>"Spoiler", "NSFW", "Preparing…"; then, clicked once, what a second click does.</summary>
    public string? Notice => IsPreparing ? "Preparing…" : IsFlagged ? asking ? "Click again to play" : flagName : null;

    private string? flagName;
    private bool asking;

    /// <summary>A flagged card asks first: its first click only changes its notice.</summary>
    [RelayCommand]
    private void Play()
    {
        if (!CanPlay) return;
        if (IsFlagged && !asking)
        {
            asking = true;
            OnPropertyChanged(nameof(Notice));
            return;
        }
        asking = false;
        OnPropertyChanged(nameof(Notice));
        play(this, null);
    }

    /// <summary>A version picked from the card's menu: a choice made on purpose, so it plays at once.</summary>
    private void PlayCut(ThemeCut cut)
    {
        if (EntryFor(cut) is not null) play(this, cut);
    }

    /// <summary>A plugin theme: its MP4, or its song when there's no video.</summary>
    /// <param name="seasonOrder">Where the theme's season comes in the show; -1 for a theme of the whole show.</param>
    public static OpeningEndingCard Theme(JellyfinClient client, OpEdTheme theme, BaseItem show, string? seasonName, int seasonOrder, Action<OpeningEndingCard, ThemeCut?> play)
    {
        var series = show.DisplayTitle;
        PlaybackQueueItem? EntryOf(OpEdTheme? version) => version?.PlayablePath is { } path
            ? new PlaybackQueueItem
            {
                ItemId = version.Id,
                MediaUrl = client.OpEdMediaUrl(path),
                MediaTitle = (version.SongLine ?? version.Code) + (version.PlayedCut.Label is { } label ? $" ({label})" : ""),
                MediaSubtitle = $"{series} · {version.Code}",
                ThemeCard = ThemeInfo.From(version, series, seasonName),
            }
            : null;
        var badge = $"{(theme.IsOpening ? "OP" : "ED")} {theme.Sequence}" + (theme.Version > 1 ? $" v{theme.Version}" : "");
        var detail = string.Join(" · ", new[] { seasonName, theme.Episodes is { } episodes ? $"Episodes {episodes}" : null }.OfType<string>());
        return new OpeningEndingCard(play,
            cut => EntryOf(cut is { } wanted ? theme.WithCut(wanted) : theme.Preferring(Aster.Core.AppSettings.Current.ThemeCut)), theme)
        {
            IsOpening = theme.IsOpening,
            SeasonId = theme.SeasonId,
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

    /// <summary>
    /// A clip of its episode: just that stretch, its resume point and watched
    /// state left alone. With <paramref name="song"/> (AnimeThemes' theme for
    /// it), the card and its song card name the song and who performs it.
    /// </summary>
    public static OpeningEndingCard Clip(IMediaServer client, ThemeClip clip, string series, Action<OpeningEndingCard, ThemeCut?> play, KnownTheme? song = null)
    {
        var entry = new PlaybackQueueItem
        {
            ItemId = clip.Episode.Id,
            StartTicks = clip.StartTicks,
            ClipEndTicks = clip.EndTicks,
            ThemeCard = song is null ? ThemeInfo.From(clip, series) : ThemeInfo.From(song, series, clip.Episode.SeasonName),
        };
        // A clip has one version: its stretch of the episode.
        return new(play, _ => entry)
        {
            IsOpening = clip.Kind == ThemeKind.Opening,
            SeasonNumber = clip.Season,
            Length = $"{clip.Duration / 60}:{clip.Duration % 60:00}",
            Title = song?.SongTitle ?? clip.Title,
            Artists = song is { Artists.Count: > 0 } ? string.Join(", ", song.Artists) : null,
            Detail = clip.Episode.EpisodeCode is { } code ? $"From {code}" : clip.Episode.DisplayTitle,
            Image = clip.ChapterImageTag is { } tag
                ? client.ChapterImageUrl(clip.Episode.Id, clip.ChapterIndex, tag)
                : client.LandscapeUrl(clip.Episode),
        };
    }
}
