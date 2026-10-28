using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Anime;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;
using Aster.Mpv;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.Audio;

/// <summary>
/// An opening or ending the Now Playing panel plays: a plugin theme's file
/// (<see cref="PlaybackQueueItem.MediaUrl"/>, from its start), or a clip of
/// an episode (its item, between <see cref="PlaybackQueueItem.StartTicks"/>
/// and <see cref="PlaybackQueueItem.ClipEndTicks"/>).
/// </summary>
/// <param name="Subtitle">The artists, or where a clip is from.</param>
/// <param name="Artwork">The card's picture, shown for a song with no video.</param>
/// <param name="PlaysUnasked">Playable, and not marked as a spoiler or NSFW: Next and Play All take it.</param>
public sealed record ThemeDeckEntry(string Id, PlaybackQueueItem Play, string Title, string? Subtitle, ThemeInfo? Card, Uri? Artwork, bool HasVideo, bool PlaysUnasked);

/// <summary>
/// The Music tab's Now Playing panel behind the scenes: openings and endings
/// played one after another, like a playlist, in mpv, with the panel's
/// picture drawn by the shell's view of <see cref="Player"/>. Title-page
/// themes go quiet while it plays; a title, a disc menu, or a song or book
/// starting ends it. A port of the Apple app's ThemeDeck.
/// </summary>
public sealed partial class ThemeDeck : ObservableObject, IDisposable
{
    private readonly IMediaServer client;
    private readonly AudioSession audio;
    private readonly ThemePlayer theme;
    private readonly Action<BaseItem> openShow;

    private IReadOnlyList<ThemeDeckEntry> queue = [];

    /// <summary>Where the one playing starts in its file: a clip's start in its episode, else zero.</summary>
    private double offset;

    /// <summary>Counts loads, so a load overtaken by the next doesn't act.</summary>
    private int loadNumber;

    public ThemeDeck(IMediaServer client, AudioSession audio, ThemePlayer theme, Action<BaseItem> openShow)
    {
        this.client = client;
        this.audio = audio;
        this.theme = theme;
        this.openShow = openShow;
        audio.PropertyChanged += AudioChanged;
    }

    /// <summary>mpv, for the panel's picture; null while nothing plays.</summary>
    [ObservableProperty] public partial MpvPlayer? Player { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(ShowsArtwork), nameof(TakesRoom))]
    public partial ThemeDeckEntry? Current { get; private set; }

    [ObservableProperty] public partial bool IsPlaying { get; private set; }

    /// <summary>A clip's episode is being opened.</summary>
    [ObservableProperty] public partial bool IsLoading { get; private set; }

    /// <summary>Into the one playing, and its length, in seconds: a clip counts from its own start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText), nameof(Progress), nameof(ShowsCard))]
    public partial double Elapsed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(Progress))]
    public partial double Duration { get; private set; }

    /// <summary>The panel fills the window (and the window the screen), the song card over it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCard), nameof(TakesRoom))]
    public partial bool IsFullScreen { get; set; }

    /// <summary>The show it was started from, for Go to the Show.</summary>
    [ObservableProperty] public partial BaseItem? Show { get; private set; }

    public bool IsActive => Current is not null;

    /// <summary>The panel's at the side, so pages leave it room on the right rather than going under it.</summary>
    public bool TakesRoom => IsActive && !IsFullScreen;

    /// <summary>A song with no video shows its picture in the video's place.</summary>
    public bool ShowsArtwork => Current is { HasVideo: false };

    public double Progress => Duration > 0 ? Math.Clamp(Elapsed / Duration * 100, 0, 100) : 0;
    public string ElapsedText => Clock(Elapsed);
    public string DurationText => Duration > 0 ? Clock(Duration) : "–:––";

    /// <summary>Full screen, the song card shows for its first seconds, as in the player.</summary>
    public bool ShowsCard => IsFullScreen && Current?.Card is not null && Elapsed < ThemeMoment.ShowFor;

    private static string Clock(double seconds)
    {
        var whole = Math.Max(0, (int)seconds);
        return $"{whole / 60}:{whole % 60:00}";
    }

    // ---- Playing ----

    /// <summary>Plays <paramref name="entry"/>, then the rest of <paramref name="list"/> after it.</summary>
    public void Play(ThemeDeckEntry entry, IReadOnlyList<ThemeDeckEntry> list, BaseItem? show)
    {
        if (Current is null)
        {
            // The song or book waits, and the page's theme goes quiet.
            audio.Pause();
            theme.Suspend();
        }
        queue = list;
        Show = show;
        if (Player is null && !CreatePlayer()) return;
        Current = entry;
        Elapsed = 0;
        IsPlaying = true;
        if (entry.Play.MediaUrl is { } file)
        {
            offset = 0;
            Duration = 0;
            IsLoading = false;
            _ = LoadAsync(file, null, null);
            return;
        }
        offset = entry.Play.StartTicks / 10_000_000.0;
        Duration = entry.Play.ClipEndTicks is { } end ? (end - entry.Play.StartTicks) / 10_000_000.0 : 0;
        IsLoading = true;
        Uri url;
        try { url = client.VideoStreamUrl(entry.Play.ItemId); }
        catch (NotSupportedException)
        {
            Next();
            return;
        }
        _ = LoadAsync(url, offset, entry.Play.ClipEndTicks / 10_000_000.0);
    }

    private bool CreatePlayer()
    {
        MpvPlayer player;
        try
        {
            player = MpvPlayer.Create(ThemePlayer.LoopingOptions(new()
            {
                // One song after another, not one looping.
                ["loop-file"] = "no",
                ["vo"] = "libmpv",
                ["hwdec"] = MpvHardware.Decoding,
                ["sid"] = "no",
                ["sub-auto"] = "no",
                ["osc"] = "no",
                ["osd-level"] = "0",
                ["input-vo-keyboard"] = "no",
            }));
            player.Observe("time-pos", typeof(double));
            player.Observe("duration", typeof(double));
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            return false;
        }
        player.PropertyChanged += (name, value) => Dispatcher.UIThread.Post(() => Changed(player, name, value));
        player.PlaybackRestart += () => Dispatcher.UIThread.Post(() => { if (Player == player) IsLoading = false; });
        // Played through: the next. One mpv can't open: the next too, rather than stopping.
        player.EndFile += end =>
        {
            if (end.Reason is MpvEndFileReason.Eof or MpvEndFileReason.Error) Dispatcher.UIThread.Post(() => { if (Player == player) Next(); });
        };
        Player = player;
        return true;
    }

    private void Changed(MpvPlayer player, string name, object? value)
    {
        if (Player != player || value is not double seconds || !double.IsFinite(seconds)) return;
        if (name == "time-pos") Elapsed = Math.Max(0, seconds - offset);
        // A plugin file's length comes from the file; a clip's is known from its chapters.
        else if (name == "duration" && Current?.Play.MediaUrl is not null) Duration = seconds;
    }

    /// <summary>Opens the file once the panel's view gives mpv somewhere to draw; a stretch of it between two times.</summary>
    private async Task LoadAsync(Uri url, double? start, double? end)
    {
        var number = ++loadNumber;
        var player = Player!;
        try
        {
            if (await Task.WhenAny(player.RenderContextReady, Task.Delay(TimeSpan.FromSeconds(5))) != player.RenderContextReady || number != loadNumber) return;
            var options = new List<string>();
            if (start is { } a) options.Add("start=" + a.ToString("0.###", CultureInfo.InvariantCulture));
            if (end is { } b) options.Add("end=" + b.ToString("0.###", CultureInfo.InvariantCulture));
            player.SetProperty("pause", !IsPlaying);
            await player.CommandAsync("loadfile", url.AbsoluteUri, "replace", "-1", string.Join(",", options));
        }
        catch (Exception error) when (error is MpvException or ObjectDisposedException) { }
    }

    [RelayCommand]
    public void TogglePlaying()
    {
        if (Player is not { } player) return;
        IsPlaying = !IsPlaying;
        Try(() => player.SetProperty("pause", !IsPlaying));
    }

    /// <summary>The next one that plays without asking; past the last, it stops.</summary>
    [RelayCommand]
    public void Next()
    {
        if (Neighbour(after: true) is { } upcoming) Play(upcoming, queue, Show);
        else Stop();
    }

    /// <summary>Back to the start, or within its first seconds, back to the one before.</summary>
    [RelayCommand]
    public void Previous()
    {
        if (Elapsed <= 3 && Neighbour(after: false) is { } earlier)
        {
            Play(earlier, queue, Show);
            return;
        }
        if (Player is { } player) Try(() => player.Command("seek", offset.ToString("0.###", CultureInfo.InvariantCulture), "absolute"));
        Elapsed = 0;
    }

    private ThemeDeckEntry? Neighbour(bool after)
    {
        if (Current is not { } current) return null;
        var index = queue.ToList().FindIndex(e => e.Id == current.Id);
        if (index < 0) return null;
        var candidates = after ? queue.Skip(index + 1) : queue.Take(index).Reverse();
        return candidates.FirstOrDefault(e => e.PlaysUnasked);
    }

    /// <summary>Ends it: the panel goes, and the page's theme comes back.</summary>
    [RelayCommand]
    public void Stop()
    {
        if (Current is null && Player is null) return;
        var wasActive = Current is not null;
        loadNumber++;
        Current = null;
        IsPlaying = false;
        IsLoading = false;
        IsFullScreen = false;
        Elapsed = 0;
        Duration = 0;
        offset = 0;
        queue = [];
        // The view lets go of mpv's render context as the panel leaves, which mpv waits for before it ends.
        var player = Player;
        Player = null;
        player?.Dispose();
        if (wasActive) theme.Resume();
    }

    [RelayCommand]
    private void ToggleFullScreen()
    {
        if (Current is not null) IsFullScreen = !IsFullScreen;
    }

    /// <summary>Back to the show's page; it keeps playing.</summary>
    [RelayCommand]
    private void GoToShow()
    {
        IsFullScreen = false;
        if (Show is { } show) openShow(show);
    }

    /// <summary>A song or book starting ends it.</summary>
    private void AudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioSession.IsPlaying) && audio.IsPlaying && Current is not null) Stop();
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is MpvException or ObjectDisposedException) { }
    }

    /// <summary>Signing out.</summary>
    public void Dispose()
    {
        audio.PropertyChanged -= AudioChanged;
        Stop();
    }
}
