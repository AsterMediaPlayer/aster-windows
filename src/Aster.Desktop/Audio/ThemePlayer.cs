using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Jellyfin;
using Aster.Mpv;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.Audio;

/// <summary>
/// Theme music and theme videos on title pages: a quiet looping song and a
/// silent looping video behind the page, faded in and out as pages change.
/// Episodes and seasons share their series' theme, so going from a show to
/// one of its episodes carries on rather than starting again. A port of the
/// Apple app's ThemePlayer, for a title's own themes.
///
/// The video draws in the shell, behind the page (one view for the whole
/// session, as mpv's video output can't move from view to view), and the
/// title page's still backdrop fades away from over it once it's playing.
/// </summary>
public sealed partial class ThemePlayer : ObservableObject, IDisposable
{
    /// <summary>mpv's volume is cubic: 70 is the Apple app's 0.35.</summary>
    private const double Volume = 70;

    private readonly JellyfinClient client;
    private readonly AudioSession audio;

    /// <summary>The series (or title) whose theme is loaded.</summary>
    private string? key;

    /// <summary>The page in view; only it may start a theme.</summary>
    private object? page;

    private CancellationTokenSource? loading;
    private MpvPlayer? music;

    /// <summary>The volume fades under way: the music, and any still fading out.</summary>
    private readonly VolumeFades fades = new();

    /// <summary>
    /// What loops behind the page: the title's theme video, or an anime
    /// opening standing in (a stretch of its episode between two times, with
    /// its sound or not). Kept to start again after a title has played.
    /// </summary>
    private sealed record VideoSource(Uri Url, double? Start = null, double? End = null, bool Sound = false);

    private VideoSource? videoSource;

    /// <summary>The theme's loading, which an opening standing in waits for: the title's own theme video always wins.</summary>
    private Task loaded = Task.CompletedTask;

    /// <summary>Which opening stands in as the theme (its video, or its song); null for the title's own theme, or none.</summary>
    private string? openingId;

    /// <summary>The music is an opening's song standing in, not the title's theme song.</summary>
    private bool musicIsOpening;

    private DispatcherTimer? hiding;
    private bool suspended;

    public ThemePlayer(JellyfinClient client, AudioSession audio)
    {
        this.client = client;
        this.audio = audio;
        // A song or book wins: the theme goes as one plays.
        audio.PropertyChanged += AudioChanged;
    }

    /// <summary>The theme video's player, for the shell's video view; null without one.</summary>
    [ObservableProperty] public partial MpvPlayer? Video { get; private set; }

    /// <summary>The theme video's frames are coming, so the page's still backdrop can fade from over it.</summary>
    [ObservableProperty] public partial bool IsVideoShowing { get; private set; }

    /// <summary>
    /// Another page is in view. The theme fades unless that page shows the
    /// same one, after a moment, so a series' page pushing an episode's
    /// carries on.
    /// </summary>
    public void PageChanged(object current)
    {
        page = current;
        hiding?.Stop();
        hiding = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        hiding.Tick += (_, _) =>
        {
            hiding?.Stop();
            hiding = null;
            Release();
        };
        hiding.Start();
    }

    /// <summary>
    /// <paramref name="owner"/>'s title is <paramref name="item"/>: plays its
    /// theme, as Settings allows, if that page is in view and no song or book
    /// is playing.
    /// </summary>
    public void Show(object owner, BaseItem item)
    {
        if (!ReferenceEquals(owner, page)) return;
        var settings = AppSettings.Current;
        var wantsMusic = settings.ThemeMusic;
        var wantsVideo = settings.ThemeVideos;
        if (!wantsMusic && !wantsVideo || audio.IsPlaying)
        {
            Release();
            return;
        }
        hiding?.Stop();
        hiding = null;
        // An episode or season known only by its id yet: its series comes with the rest of it.
        if (item.Kind is ItemKind.Episode or ItemKind.Season && item.SeriesId is null) return;
        var themeKey = item.SeriesId ?? item.Id;
        if (themeKey == key) return;
        Release();
        key = themeKey;
        loading = new CancellationTokenSource();
        loaded = LoadAsync(item.Id, themeKey, wantsMusic, wantsVideo, loading.Token);
    }

    private async Task LoadAsync(string itemId, string themeKey, bool wantsMusic, bool wantsVideo, CancellationToken ct)
    {
        BaseItem? song = null, video = null;
        try
        {
            var songs = wantsMusic ? client.ThemeSongsAsync(itemId, ct) : null;
            var videos = wantsVideo ? client.ThemeVideosAsync(itemId, ct) : null;
            if (songs is not null) song = (await songs).FirstOrDefault();
            if (videos is not null) video = (await videos).FirstOrDefault();
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException or OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested || key != themeKey) return;
        if (song is not null) StartMusic(client.AudioStreamUrl(song.Id));
        if (video is not null)
        {
            videoSource = new VideoSource(client.VideoStreamUrl(video.Id));
            if (!suspended) StartVideo(videoSource);
        }
    }

    // ---- An anime opening standing in ----

    /// <summary>An anime opening (video or song) is standing in as the theme.</summary>
    public bool IsShowingOpening => openingId is not null;

    /// <summary>
    /// For a show with no theme video of its own (<paramref name="owner"/>'s,
    /// in view): loops an opening behind the page instead, from
    /// <paramref name="start"/> to <paramref name="end"/> seconds of its file
    /// when it's a stretch of an episode. Waits for the show's own theme
    /// video, which always wins; another opening standing in is swapped out
    /// (another season picked). With <paramref name="sound"/> it plays as
    /// quietly as theme music, and is the song in place of any theme song.
    /// A port of the Apple app's showOpeningIfNoVideo.
    /// </summary>
    public async void ShowOpening(object owner, BaseItem item, string id, Uri url, double? start = null, double? end = null, bool sound = false)
    {
        var themeKey = item.SeriesId ?? item.Id;
        await loaded;
        if (!ReferenceEquals(owner, page) || key != themeKey || !CanStandIn(id)) return;
        DropVideo();
        openingId = id;
        // The opening is the song now (two at once would clash), and an earlier opening's song gives way.
        if ((sound || musicIsOpening) && music is { } song)
        {
            music = null;
            musicIsOpening = false;
            fades.Fade(song, to: 0, seconds: 0.6, then: song.Dispose);
        }
        videoSource = new VideoSource(url, start, end, sound);
        if (!suspended) StartVideo(videoSource);
    }

    /// <summary>
    /// An opening the OP/ED plugin has only the song of: plays it as the
    /// page's theme music, in place of any theme song or opening video
    /// standing in. The show's own theme video still wins.
    /// </summary>
    public async void ShowOpeningSong(object owner, BaseItem item, string id, Uri url)
    {
        var themeKey = item.SeriesId ?? item.Id;
        await loaded;
        if (!ReferenceEquals(owner, page) || key != themeKey || !CanStandIn(id)) return;
        if (openingId is not null)
        {
            DropVideo();
            videoSource = null;
        }
        if (music is { } song)
        {
            music = null;
            fades.Fade(song, to: 0, seconds: 0.6, then: song.Dispose);
        }
        openingId = id;
        StartMusic(url);
        musicIsOpening = true;
    }

    /// <summary>Nothing standing in yet, or another opening that may be swapped for this one; never the title's own theme video.</summary>
    private bool CanStandIn(string id) => openingId != id && (videoSource is null || openingId is not null);

    /// <summary>A title starts playing over the window: the music goes quiet and the video lets go of its view.</summary>
    public void Suspend()
    {
        suspended = true;
        if (music is { } playing) fades.Fade(playing, to: 0, seconds: 0.4, then: () => Try(() => playing.SetProperty("pause", true)));
        DropVideo();
    }

    /// <summary>The player closed: the theme comes back, if its page is still there to show it.</summary>
    public void Resume()
    {
        suspended = false;
        if (key is null) return;
        if (music is { } paused)
        {
            Try(() => paused.SetProperty("pause", false));
            fades.Fade(paused, to: Volume, seconds: 0.8);
        }
        if (videoSource is { } source && Video is null) StartVideo(source);
    }

    /// <summary>Ends the theme: the music fades out and the video goes.</summary>
    public void Release()
    {
        hiding?.Stop();
        hiding = null;
        loading?.Cancel();
        loading = null;
        if (music is { } ending)
        {
            music = null;
            fades.Fade(ending, to: 0, seconds: 0.6, then: ending.Dispose);
        }
        DropVideo();
        videoSource = null;
        openingId = null;
        musicIsOpening = false;
        key = null;
    }

    private void AudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioSession.IsPlaying) && audio.IsPlaying && key is not null) Release();
    }

    // ---- Music ----

    private void StartMusic(Uri url)
    {
        try
        {
            music = MpvPlayer.Create(LoopingOptions(new()
            {
                ["video"] = "no",
                ["vo"] = "null",
                ["audio-display"] = "no",
                ["volume"] = "0",
                ["pause"] = suspended ? "yes" : "no",
            }));
            _ = music.CommandAsync("loadfile", url.AbsoluteUri).ContinueWith(_ => { });
            if (!suspended) fades.Fade(music, to: Volume, seconds: 0.8);
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            music = null;
        }
    }

    // ---- Video ----

    private void StartVideo(VideoSource source)
    {
        MpvPlayer player;
        try
        {
            player = MpvPlayer.Create(LoopingOptions(new()
            {
                // Frames go to the shell's MpvVideoView through the render API.
                ["vo"] = "libmpv",
                ["hwdec"] = MpvHardware.Decoding,
                // Silent, but for an opening with its sound, as quiet as theme music.
                ["aid"] = source.Sound ? "auto" : "no",
                ["volume"] = source.Sound ? Volume.ToString(System.Globalization.CultureInfo.InvariantCulture) : "100",
                ["sid"] = "no",
                ["sub-auto"] = "no",
                ["osc"] = "no",
                ["osd-level"] = "0",
                ["input-vo-keyboard"] = "no",
            }));
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            return;
        }
        player.PlaybackRestart += () => Dispatcher.UIThread.Post(() =>
        {
            if (Video == player) IsVideoShowing = true;
        });
        // A video mpv can't play leaves the still backdrop where it is.
        player.EndFile += end =>
        {
            if (end.Reason == MpvEndFileReason.Error) Dispatcher.UIThread.Post(() =>
            {
                if (Video != player) return;
                DropVideo();
                videoSource = null;
            });
        };
        Video = player;
        _ = LoadVideoAsync(player, source);
    }

    /// <summary>
    /// mpv's video output fails for good on a file opened before the view
    /// gives it somewhere to draw. A stretch of a file loops between its
    /// two times (mpv's A-B loop).
    /// </summary>
    private static async Task LoadVideoAsync(MpvPlayer player, VideoSource source)
    {
        try
        {
            await player.RenderContextReady.ConfigureAwait(false);
            if (source.Start is { } start && source.End is { } end)
            {
                var a = start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                var b = end.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                await player.CommandAsync("loadfile", source.Url.AbsoluteUri, "replace", "-1", $"start={a},ab-loop-a={a},ab-loop-b={b}").ConfigureAwait(false);
            }
            else
            {
                await player.CommandAsync("loadfile", source.Url.AbsoluteUri).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is MpvException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Lets go of the video. The view drops its render context as it
    /// leaves the screen, which mpv waits for before it ends.
    /// </summary>
    private void DropVideo()
    {
        IsVideoShowing = false;
        var player = Video;
        Video = null;
        player?.Dispose();
    }

    // ---- ----

    /// <summary>What looping background players share (these two, and a disc menu's music and trailer): quiet about themselves, and unable to take the keys.</summary>
    internal static Dictionary<string, string> LoopingOptions(Dictionary<string, string> own)
    {
        var options = new Dictionary<string, string>
        {
            ["loop-file"] = "inf",
            ["idle"] = "yes",
            ["input-default-bindings"] = "no",
            ["terminal"] = "no",
            ["ytdl"] = "no",
            ["user-agent"] = $"{JellyfinDevice.ClientName}/{JellyfinDevice.Version}",
#if DEBUG
            // ASTER_MUTE: silent, for trying the app without disturbing anyone.
            ["mute"] = Environment.GetEnvironmentVariable("ASTER_MUTE") is null ? "no" : "yes",
#endif
        };
        foreach (var (name, value) in own) options[name] = value;
        return options;
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is MpvException or ObjectDisposedException) { }
    }

    /// <summary>Signing out: everything stops at once.</summary>
    public void Dispose()
    {
        audio.PropertyChanged -= AudioChanged;
        // Songs still fading out end now, with the one playing.
        fades.StopAll();
        music?.Dispose();
        music = null;
        Release();
    }
}
