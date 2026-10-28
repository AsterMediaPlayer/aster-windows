// Plays one video through mpv, the port's counterpart of the Apple app's
// AetherPlayback. Usually the file streams from the server untouched, so
// every audio and subtitle track is in hand and switches in the player.
// When the file is too much (a lower quality chosen, or over the quality
// setting) or won't play, the server converts it and mpv plays that stream
// instead: one audio track at a time, a text subtitle loaded beside it, a
// picture one burned in. Progress goes to Jellyfin every ten seconds.
//
// Not yet ported: the hand-over to a second player (there is only mpv
// here, so a title that fails steps down through the server's encodes
// instead).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Anime;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;
using Aster.Desktop.Images;
using Aster.Mpv;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A track or setting to choose in a player menu.</summary>
public sealed record TrackChoice(int Id, string Title, bool IsSelected, IRelayCommand Select);

/// <summary>A chapter in the Chapters menu: its name and start, ticked while it plays.</summary>
public sealed record ChapterChoice(string Title, string Time, bool IsCurrent, IRelayCommand Select);

public partial class PlayerViewModel : ViewModelBase
{
    private readonly IMediaServer client;
    private readonly Action<BaseItem?> close;
    private readonly AppSettings settings = AppSettings.Current;
    private PlaybackQueueItem entry;
    private PlaybackPreferences? preferences;

    private string? playSessionId;
    private string? mediaSourceId;
    private string playMethod = "DirectPlay";
    private bool stopped;
    private bool stopReported;

    /// <summary>The file's streams as Jellyfin lists them.</summary>
    private IReadOnlyList<MediaStream> audioStreams = [];
    private IReadOnlyList<MediaStream> subtitleStreams = [];

    /// <summary>Jellyfin stream index to mpv track id, for what mpv has loaded.</summary>
    private readonly Dictionary<int, int> audioTracks = [];
    private readonly Dictionary<int, int> subtitleTracks = [];

    /// <summary>Playing a stream the server converts, rather than the file itself.</summary>
    private bool isConverted;

    /// <summary>The converted stream's bitrate cap; null for the quality setting.</summary>
    private int? qualityCap;

    /// <summary>The tracks playing, as Jellyfin stream indexes; -1 for no subtitles.</summary>
    private int? currentAudio;
    private int currentSubtitle = -1;

    private IReadOnlyList<MediaSegment> segments = [];
    private readonly HashSet<string> autoSkipped = [];
    private readonly StallWatch stallWatch = new();
    private TaskCompletionSource? loading;
    private bool updatingSeekBar;

    private readonly DispatcherTimer reportTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer timedTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DispatcherTimer? skipOfferTimer;
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    /// <param name="close">Called as the player closes, with the last title it played.</param>
    public PlayerViewModel(IMediaServer client, PlaybackQueueItem entry, Action<BaseItem?> close)
    {
        this.client = client;
        this.entry = entry;
        this.close = close;
        reportTimer.Tick += async (_, _) => await ReportProgressAsync();
        timedTimer.Tick += (_, _) => UpdateTimedState();
        countdownTimer.Tick += (_, _) => CountdownTick();
        hideTimer.Tick += (_, _) => HideControlsIfIdle();
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast = null; };
        bugTimer.Tick += (_, _) => { bugTimer.Stop(); BugOpacity = 0; };
        flickTimer.Tick += (_, _) => CloseFlick();
        try
        {
#if DEBUG
            Player = MpvPlayer.Create(Options(), logLevel: "info");
#else
            Player = MpvPlayer.Create(Options());
#endif
            Player.PropertyChanged += (name, value) => Dispatcher.UIThread.Post(() => OnPlayerProperty(name, value));
            Player.FileLoaded += () => loading?.TrySetResult();
            Player.EndFile += OnEndFile;
            Player.Observe("pause", typeof(bool));
            Player.Observe("eof-reached", typeof(bool));
            Player.Observe("paused-for-cache", typeof(bool));
            Player.Observe("duration", typeof(double));
#if DEBUG
            Player.Log += (prefix, level, text) => System.Diagnostics.Debug.WriteLine($"mpv [{prefix}] {level}: {text}");
#endif
        }
        catch (Exception error) when (error is DllNotFoundException or MpvException)
        {
            ErrorMessage = $"The video player couldn't start: {error.Message}";
            return;
        }
        _ = StartAsync();
    }

    /// <summary>mpv's options, with the subtitle style chosen in Settings.</summary>
    private Dictionary<string, string> Options() => new(MpvOptions)
    {
        // Off: ASS and SSA lose their positions, drawings and fonts, as plain text at the bottom.
        ["sub-ass-override"] = settings.StyledSubtitles ? "scale" : "strip",
    };

    private static readonly Dictionary<string, string> MpvOptions = new()
    {
        // Frames go to MpvVideoView through the render API.
        ["vo"] = "libmpv",
        ["hwdec"] = MpvHardware.Decoding,
        // Stay on the last frame at the end, so the Up Next card shows over it.
        ["keep-open"] = "yes",
        ["idle"] = "yes",
        ["pause"] = "yes",
        // The app draws its own controls and messages, and handles the keys.
        ["osc"] = "no",
        ["osd-level"] = "0",
        ["input-default-bindings"] = "no",
        ["input-vo-keyboard"] = "no",
        ["terminal"] = "no",
        // Only the subtitles Jellyfin lists, never files mpv finds itself.
        ["sub-auto"] = "no",
        ["ytdl"] = "no",
        ["audio-display"] = "no",
        ["user-agent"] = $"{JellyfinDevice.ClientName}/{JellyfinDevice.Version}",
#if DEBUG
        // ASTER_MUTE: silent, for trying the app without disturbing anyone.
        ["mute"] = Environment.GetEnvironmentVariable("ASTER_MUTE") is null ? "no" : "yes",
#endif
    };

    public MpvPlayer? Player { get; }

    // ---- State the view shows ----

    [ObservableProperty] public partial BaseItem? Item { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string? Subtitle { get; set; }

    /// <summary>Playing has begun, so the loading cover can come off.</summary>
    [ObservableProperty] public partial bool HasStarted { get; set; }

    /// <summary>A Live TV channel: no timeline, no skipping, no speeds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline), nameof(HasBadge), nameof(Badge), nameof(OffersQuality))]
    public partial bool IsLive { get; set; }

    /// <summary>The Channel playing, "5  Late Night", while on one; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline), nameof(HasBadge), nameof(Badge), nameof(OffersQuality))]
    public partial string? ChannelName { get; private set; }

    /// <summary>Live TV and Channels play on a timeline the viewer can't move.</summary>
    public bool HasTimeline => !IsLive && ChannelName is null;

    public bool HasBadge => IsLive || ChannelName is not null;
    public string Badge => ChannelName ?? "LIVE";

    /// <summary>On a Channel: its schedule decides what plays, and nothing's reported (resume points and watched state stay as they were).</summary>
    private bool OnChannel => entry.ChannelId is not null;

    /// <summary>An opening or ending played on its own: a clip of its episode, or the OP/ED plugin's file.</summary>
    private bool IsThemePlayback => entry.ClipEndTicks is not null || entry.MediaUrl is not null;

    /// <summary>Whether the server hears about this playback: not on a Channel, nor for an opening played on its own.</summary>
    private bool Reports => !OnChannel && !IsThemePlayback;

    /// <summary>A file with no item behind it (an OP/ED plugin theme): no other quality to ask the server for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersQuality))]
    public partial bool PlaysFile { get; private set; }

    public bool OffersQuality => HasTimeline && !PlaysFile;

    /// <summary>A break clip cut short reached its cut.</summary>
    private bool reachedCut;

    // ---- The channel's bug ----

    [ObservableProperty] public partial Uri? BugUrl { get; private set; }
    [ObservableProperty] public partial double BugWidth { get; private set; } = 128;
    [ObservableProperty] public partial double BugOpacity { get; private set; }
    [ObservableProperty] public partial Aster.Core.Channels.BugCorner BugCorner { get; private set; }

    /// <summary>The picture's shape, width over height, so the bug sits on it rather than out in the bars.</summary>
    [ObservableProperty] public partial double VideoAspect { get; private set; }

    private readonly DispatcherTimer bugTimer = new();

    // ---- Flicking through channels ----

    /// <summary>The row of channels along the bottom, while it's open.</summary>
    [ObservableProperty] public partial ChannelFlickViewModel? Flick { get; private set; }

    /// <summary>The row closes itself after a while left alone.</summary>
    private readonly DispatcherTimer flickTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    private bool flickWhenStarted;

    /// <summary>For screenshots (<c>--flick</c>): the row opens once playing.</summary>
    public void OpenFlickWhenStarted() => flickWhenStarted = true;

    /// <summary>Opens the row on the channel playing; false when this isn't a channel.</summary>
    public bool OpenFlick()
    {
        if (stopped || client is not JellyfinClient jellyfin || ChannelFlickViewModel.For(jellyfin, IsLive ? entry.ItemId : null, entry.ChannelId) is not { } flick) return false;
        Flick = flick;
        ControlsVisible = false;
        FlickActivity();
        _ = flick.RefreshAsync();
        return true;
    }

    [RelayCommand] private void ShowChannels() => OpenFlick();

    /// <summary>A card clicked: that channel.</summary>
    [RelayCommand]
    private void FlickTo(FlickCard card)
    {
        if (card.Channel is not { } channel) return;
        Flick?.Select(channel.Id);
        ChooseFromFlick();
    }

    /// <summary>Go to Episode clicked.</summary>
    [RelayCommand]
    private void FlickGoTo()
    {
        Flick?.PickGoTo(true);
        ChooseFromFlick();
    }

    public void CloseFlick()
    {
        flickTimer.Stop();
        Flick = null;
    }

    /// <summary>Something was done in the row: it stays open a while longer.</summary>
    public void FlickActivity()
    {
        flickTimer.Stop();
        flickTimer.Start();
    }

    /// <summary>Changes to the picked channel, or just closes on the one playing; with Go to Episode picked, plays what's on by itself.</summary>
    public void ChooseFromFlick()
    {
        if (Flick is not { } flick) return;
        CloseFlick();
        if (flick.IsOnGoTo && flick.Selected?.Now?.ItemId is { } itemId) _ = GoToAsync(itemId);
        else if (flick.Selected is { } picked && picked.Id != flick.CurrentId) Tune(picked);
    }

    /// <summary>Another channel, Live TV or Channels, in this same player.</summary>
    private void Tune(FlickChannel channel)
    {
        if (IsLive)
        {
            LeaveCurrent();
            Play(new BaseItem { Id = channel.Id, Name = channel.Name, Type = "TvChannel" }, new PlaybackQueueItem { ItemId = channel.Id });
        }
        else if (Aster.Core.Channels.ChannelTuner.Live(channel.Id) is var (item, next))
        {
            LeaveCurrent();
            Play(item, next);
        }
        ShowToast(string.Join("  ", new[] { channel.Number, channel.Name }.OfType<string>()));
    }

    /// <summary>
    /// Leaves the channel for what's on it, played on its own as if picked
    /// from its show or the library: from where it was left off, with
    /// progress saved, its markers, and Up Next after it.
    /// </summary>
    private async Task GoToAsync(string itemId)
    {
        BaseItem found;
        try { found = await client.ItemAsync(itemId); }
        catch (Exception error) when (IsServerError(error))
        {
            ShowToast("Couldn't open that title");
            return;
        }
        if (stopped) return;
        LeaveCurrent();
        Play(found, new PlaybackQueueItem { ItemId = found.Id, StartTicks = found.UserData?.PlaybackPositionTicks ?? 0 });
    }

    /// <summary>What's playing is being left for something else: the server hears it stopped, and lets go of its stream.</summary>
    private void LeaveCurrent()
    {
        reportTimer.Stop();
        if (HasStarted && !stopReported && Reports)
        {
            stopReported = true;
            _ = client.ReportStoppedAsync(Report(paused: true));
        }
        StopServerSession();
        if (liveStreamId is { } live && client is JellyfinClient jellyfin) _ = jellyfin.CloseLiveStreamAsync(live);
        liveStreamId = null;
    }

    /// <summary>The server's live stream for a channel, closed when it's done.</summary>
    private string? liveStreamId;

    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial bool IsBuffering { get; set; }
    [ObservableProperty] public partial double Position { get; set; }
    [ObservableProperty] public partial double Duration { get; set; }

    /// <summary>The seek bar's value: the position, or where it's being dragged to.</summary>
    [ObservableProperty] public partial double SeekBarValue { get; set; }

    /// <summary>The seek bar's thumb is being dragged; the seek happens when it's let go.</summary>
    public bool IsScrubbing { get; set; }

    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    /// <summary>A brief message for a key press: the track or volume now.</summary>
    [ObservableProperty] public partial string? Toast { get; set; }
    [ObservableProperty] public partial bool ControlsVisible { get; set; } = true;

    /// <summary>A menu is open, so the controls stay up.</summary>
    public int OpenMenus { get; set; }

    [ObservableProperty] public partial IReadOnlyList<TrackChoice> AudioChoices { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TrackChoice> SubtitleChoices { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TrackChoice> QualityChoices { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TrackChoice> SpeedChoices { get; set; } = [];

    /// <summary>The intro, credits or recap the playhead is in.</summary>
    [ObservableProperty] public partial MediaSegment? ActiveSegment { get; set; }

    /// <summary>Near the end of an episode with one after it: Play Next Episode.</summary>
    [ObservableProperty] public partial bool ShowsUpNextAction { get; set; }

    /// <summary>A skip (or Play Next Episode) has just come up: it shows by itself for 5 seconds, then only with the controls.</summary>
    [ObservableProperty] public partial bool SkipOfferIsFresh { get; set; }

    [ObservableProperty] public partial BaseItem? UpNext { get; set; }
    [ObservableProperty] public partial bool EndCardVisible { get; set; }
    [ObservableProperty] public partial int? Countdown { get; set; }

    /// <summary>Playback keeps stalling, and the lower qualities are on offer.</summary>
    [ObservableProperty] public partial bool QualityOfferVisible { get; set; }

    public string SkipTitle => ActiveSegment?.SkipTitle ?? "Play Next Episode";
    public bool ShowsSkip => (ActiveSegment is not null || ShowsUpNextAction) && !EndCardVisible;
    public string PositionText => Clock(Position);
    public string DurationText => Duration > 0 ? Clock(Duration) : "";
    public string UpNextTitle => UpNext is { } next ? $"{next.SeriesName} · {next.EpisodeCode} · {next.DisplayTitle}" : "";
    public string CountdownText => Countdown is { } seconds ? $"Playing in {seconds}" : "";

    /// <summary>The skip button shows when there's something to skip, while it's fresh or with the controls.</summary>
    public bool SkipVisible => ShowsSkip && (SkipOfferIsFresh || ControlsVisible);

    private void SkipChanged()
    {
        OnPropertyChanged(nameof(SkipTitle));
        OnPropertyChanged(nameof(ShowsSkip));
        OnPropertyChanged(nameof(SkipVisible));
    }

    partial void OnActiveSegmentChanged(MediaSegment? value) => SkipChanged();
    partial void OnShowsUpNextActionChanged(bool value) => SkipChanged();
    partial void OnEndCardVisibleChanged(bool value) => SkipChanged();
    partial void OnSkipOfferIsFreshChanged(bool value) => SkipChanged();
    partial void OnControlsVisibleChanged(bool value) => SkipChanged();
    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));
    partial void OnUpNextChanged(BaseItem? value) => OnPropertyChanged(nameof(UpNextTitle));
    partial void OnCountdownChanged(int? value) => OnPropertyChanged(nameof(CountdownText));

    partial void OnSeekBarValueChanged(double value)
    {
        if (IsScrubbing && !updatingSeekBar) PreviewAt(value);
        // A click on the bar moves it; the player's own updates and drags don't seek.
        if (updatingSeekBar || IsScrubbing || !HasStarted) return;
        if (Math.Abs(value - Position) > 1) SeekTo(value);
    }

    private static string Clock(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? MusicFormat.Clock(seconds) : "0:00";

    // ---- Starting ----

    private async Task StartAsync()
    {
        try
        {
            preferences ??= await LoadPreferencesAsync();
            PlaysFile = entry.MediaUrl is not null;
            if (entry.MediaUrl is { } file)
            {
                await StartFileAsync(file);
                return;
            }
            var item = await client.ItemAsync(entry.ItemId);
            if (stopped) return;
            Item = item;
            if (item.Kind == ItemKind.TvChannel)
            {
                IsLive = true;
                Title = string.Join("  ", new[] { item.ChannelNumber, item.DisplayTitle }.OfType<string>());
                Subtitle = item.CurrentProgram?.Name;
                await LoadLiveAsync();
                if (!stopped) await BeginAsync();
                return;
            }
            Title = item.SeriesName ?? item.DisplayTitle;
            Subtitle = item.SeriesName is null ? string.Join(" · ", item.MetadataLine) : $"{item.EpisodeCode} · {item.DisplayTitle}";
            ShowChannel(item);
            // The song cards' details, looked up alongside the stream so they're in by the opening.
            themeDetails = settings.ThemeCards && item.Kind == ItemKind.Episode && entry.ClipEndTicks is null ? ThemeDetailsAsync(item) : null;
            await LoadTitleAsync(item);
            if (stopped) return;
            if (HasTimeline)
            {
                Chapters = item.Chapters is { Count: > 1 } all ? all.OrderBy(c => c.StartPositionTicks).ToList() : [];
                scrubPreview = ScrubPreviewSource.For(client, item, mediaSourceId);
            }
            await BeginAsync();
            // An opening played on its own: its song card, and no markers or Up Next.
            if (entry.ClipEndTicks is { } clipEnd)
            {
                if (settings.ThemeCards && entry.ThemeCard is { } card) themeMoments = [new ThemeMoment(entry.StartTicks / 10_000_000.0, clipEnd / 10_000_000.0, card)];
                return;
            }
            // An episode's cards from its OP and ED chapters at once (an
            // opening at the very start is over before the lookups finish),
            // gaining the song's details in place as they come.
            if (settings.ThemeCards && item.Kind == ItemKind.Episode)
            {
                themeMoments = ThemeMoments.ForEpisode(item, [], []);
                _ = LoadThemeMomentsAsync(item);
            }
            // A channel gets no markers (skipping an intro would leave the
            // schedule behind) and goes on to what it's showing, never Up Next.
            if (OnChannel)
            {
                if (item.Kind != ItemKind.Episode) await LoadThemeMomentsAsync(item);
                return;
            }

            // Markers and the next episode load once it plays, so they never hold up the start.
            var markers = client.MediaSegmentsAsync(item.Id);
            var next = item.Kind == ItemKind.Episode ? TrackRules.NextEpisodeAsync(item, client) : Task.FromResult<BaseItem?>(null);
            IReadOnlyList<MediaSegment> loaded;
            try { loaded = await markers; }
            catch (Exception error) when (IsServerError(error)) { loaded = []; }
            var after = await next;
            if (stopped || Item?.Id != item.Id) return;
            segments = loaded;
            UpNext = after;
            System.Diagnostics.Debug.WriteLine($"Playback: markers {string.Join(", ", loaded.Select(s => $"{s.Type} {s.StartTicks / 10_000_000}-{s.EndTicks / 10_000_000}s"))}; up next {after?.DisplayTitle ?? "none"}");
            // Markers place the cards when there are no chapters.
            await LoadThemeMomentsAsync(item);
        }
        catch (Exception error) when (IsServerError(error) || error is MpvException or ObjectDisposedException or NoVideoOutputException)
        {
            if (!stopped) FallBack(error.Message);
        }
    }

    /// <summary>An OP/ED plugin theme: its file played as it is from the start, with its song card.</summary>
    private async Task StartFileAsync(Uri file)
    {
        Item = null;
        Title = entry.MediaTitle ?? "";
        Subtitle = entry.MediaSubtitle;
        ChannelName = null;
        BugUrl = null;
        playMethod = "DirectPlay";
        await LoadAsync(file, 0);
        if (stopped) return;
        await BeginAsync();
        if (settings.ThemeCards && entry.ThemeCard is { } card)
        {
            // Without a length yet, just the card at the start.
            var length = Player?.GetDouble("duration") is > 0 and var seconds ? seconds : double.PositiveInfinity;
            themeMoments = [new ThemeMoment(0, length, card)];
        }
    }

    /// <summary>
    /// On a Channel: its name in place of the timeline, and its bug, which
    /// steps aside for a break's clips (anything but a movie or episode) and,
    /// with bug-hide=, fades a while after tuning in or a programme starting.
    /// </summary>
    private void ShowChannel(BaseItem item)
    {
        bugTimer.Stop();
        if (entry.ChannelId is not { } channelId || Aster.Core.Channels.ChannelTuner.Channel(channelId) is not { } channel)
        {
            ChannelName = null;
            BugUrl = null;
            return;
        }
        ChannelName = $"{channel.Number}  {channel.Name}";
        var programme = item.Kind is ItemKind.Movie or ItemKind.Episode;
        BugCorner = channel.BugCorner;
        BugWidth = 128 * channel.BugSize / 100.0;
        BugUrl = channel.ShowsBug && client is JellyfinClient jellyfin ? channel.LogoUrl(jellyfin, (int)(BugWidth * 2)) : null;
        BugOpacity = programme ? channel.BugOpacity / 100.0 : 0;
        if (programme && channel.BugHideAfter > 0)
        {
            bugTimer.Interval = TimeSpan.FromSeconds(channel.BugHideAfter);
            bugTimer.Start();
        }
    }

    /// <summary>
    /// The next or previous channel (Live TV's in the guide's order,
    /// Channels' by number) from what it's on now; false when this isn't a
    /// channel or there's no other.
    /// </summary>
    public bool SwitchChannel(int step)
    {
        if (stopped || client is not JellyfinClient jellyfin || ChannelFlickViewModel.For(jellyfin, IsLive ? entry.ItemId : null, entry.ChannelId) is not { Channels.Count: > 1 } row) return false;
        row.Move(step);
        if (row.Selected is not { } next) return false;
        Tune(next);
        return true;
    }

    /// <summary>A converted stream's encode stops on the server once it's left behind.</summary>
    private void StopServerSession()
    {
        if (isConverted && playSessionId is { } session) _ = client.StopTranscodingAsync(session);
    }

    /// <summary>The user's track settings on Jellyfin and Emby; Plex's stay on Plex, so its files' own defaults lead.</summary>
    private async Task<PlaybackPreferences> LoadPreferencesAsync()
    {
        if (client is not JellyfinClient jellyfin) return new PlaybackPreferences();
        try { return TrackRules.Preferences((await jellyfin.MeAsync()).Configuration); }
        catch (Exception error) when (IsServerError(error)) { return new PlaybackPreferences(); }
    }

    private static bool IsServerError(Exception error) =>
        error is JellyfinException or HttpRequestException or TimeoutException or TaskCanceledException;

    /// <summary>
    /// A Live TV channel: the server opens its live stream, and mpv plays
    /// the HLS it offers (converted, or the tuner's own when it speaks HLS).
    /// </summary>
    private async Task LoadLiveAsync()
    {
        var info = await client.PlaybackInfoAsync(entry.ItemId, 0, settings.MaxBitrate, subtitleStreamIndex: -1);
        if (info.ErrorCode is { } code) throw JellyfinException.Http(0, code);
        var source = info.MediaSources.FirstOrDefault() ?? throw JellyfinException.Http(0, "No media source");
        playSessionId = info.PlaySessionId;
        mediaSourceId = source.Id;
        liveStreamId = source.LiveStreamId;
        Uri? url;
        if (source.TranscodingUrl is { } path && client.AbsoluteUrl(path) is { } converted)
        {
            playMethod = "Transcode";
            url = converted;
        }
        else if (source.Path is { } direct && direct.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                 && Uri.TryCreate(direct, UriKind.Absolute, out var hls) && hls.Scheme.StartsWith("http", StringComparison.Ordinal))
        {
            playMethod = "DirectPlay";
            url = hls;
        }
        else
        {
            throw JellyfinException.Http(0, "This channel's stream format isn't supported.");
        }
        System.Diagnostics.Debug.WriteLine($"Playback: live channel {Item?.DisplayTitle} ({playMethod})");
        await LoadAsync(url, 0);
        // A broadcast's captions aren't listed by the server, so they follow
        // the subtitle setting alone: on only when subtitles are always on.
        if (!stopped && preferences?.SubtitleMode != SubtitleMode.Always) Player!.SetProperty("sid", "no");
    }

    /// <summary>A movie, episode or other video: the file itself, or a stream the server converts.</summary>
    private async Task LoadTitleAsync(BaseItem item)
    {
        // Asked only for the session id and the file's id: the stream below
        // is the file itself, whatever the server would choose.
        var info = await client.PlaybackInfoAsync(entry.ItemId, entry.StartTicks, settings.MaxBitrate,
            mediaSourceId: entry.MediaSourceId, subtitleStreamIndex: -1);
        if (info.ErrorCode is { } code) throw JellyfinException.Http(0, code);
        var source = info.MediaSources.FirstOrDefault() ?? throw JellyfinException.Http(0, "No media source");
        playSessionId = info.PlaySessionId;
        mediaSourceId = source.Id;

        var streams = source.MediaStreams ?? [];
        audioStreams = streams.Where(s => s.Type == "Audio").ToList();
        subtitleStreams = streams.Where(s => s.Type == "Subtitle").ToList();
        var audio = entry.AudioStreamIndex ?? TrackRules.PreferredAudioTrack(streams, preferences!.AudioLanguage)
            // Plex keeps its account's language settings to itself, choosing the track it would play.
            ?? (client.Kind == ServerKind.Plex ? source.DefaultAudioStreamIndex : null);
        var subtitle = TrackRules.ResolveSubtitle(entry, source, subtitleStreams, preferences!);
        var start = entry.StartTicks / 10_000_000.0;

        qualityCap = entry.MaxBitrate;
        // Over the quality limit, the file is converted down to it: played
        // as it is, a big one takes a minute to start on a slow connection.
        if (qualityCap is null && settings.LimitsQuality && source.Bitrate > settings.MaxBitrate) qualityCap = settings.MaxBitrate;
        isConverted = qualityCap is not null || entry.Fallback != StreamFallback.None;
        System.Diagnostics.Debug.WriteLine($"Playback: {item.DisplayTitle} video={streams.FirstOrDefault(s => s.Type == "Video")?.Codec} bitrate={source.Bitrate} cap={qualityCap} fallback={entry.Fallback} audio={audio} subtitle={subtitle} -> {(isConverted ? "converted" : "original file")}");

        if (isConverted)
        {
            await LoadConvertedAsync(audio ?? source.DefaultAudioStreamIndex, subtitle, start);
            return;
        }

        playMethod = "DirectPlay";
        await LoadAsync(client.DirectStreamUrl(entry.ItemId, source.Id, info.PlaySessionId), start);
        if (stopped) return;
        MapTracks();
        await AddExternalSubtitlesAsync();
        if (audio is { } wanted && audioTracks.ContainsKey(wanted))
        {
            SetAudioTrack(wanted);
        }
        else
        {
            // mpv's own pick (the file's default track), for the reports and menu.
            var playing = Player!.Tracks().FirstOrDefault(t => t.Type == "audio" && t.Selected)?.Id;
            currentAudio = audioTracks.Where(pair => pair.Value == playing).Select(pair => (int?)pair.Key).FirstOrDefault();
        }
        SetSubtitleTrack(subtitleTracks.ContainsKey(subtitle) ? subtitle : -1);
        System.Diagnostics.Debug.WriteLine($"Playback: mpv has {audioTracks.Count}/{audioStreams.Count} audio and {subtitleTracks.Count}/{subtitleStreams.Count} subtitle tracks; playing audio {currentAudio}, subtitle {currentSubtitle}");
    }

    private async Task LoadConvertedAsync(int? audio, int subtitle, double start)
    {
        var burnIn = subtitle >= 0 && !IsTextSubtitle(subtitle) ? subtitle : (int?)null;
        var info = await client.PlaybackInfoAsync(entry.ItemId, entry.StartTicks, qualityCap ?? settings.MaxBitrate,
            mediaSourceId: mediaSourceId,
            audioStreamIndex: audio,
            subtitleStreamIndex: burnIn ?? -1,
            allowDirect: false,
            mostCompatible: entry.Fallback == StreamFallback.MostCompatible);
        if (info.ErrorCode is { } code) throw JellyfinException.Http(0, code);
        var source = info.MediaSources.FirstOrDefault();
        if (source?.TranscodingUrl is not { } path || client.AbsoluteUrl(path) is not { } url)
            throw JellyfinException.Http(0, "The server didn't offer a converted stream");
        playSessionId = info.PlaySessionId;
        playMethod = "Transcode";
        currentAudio = audio;
        currentSubtitle = subtitle;
        await LoadAsync(url, start);
        if (stopped) return;
        if (burnIn is null && subtitle >= 0) await ShowConvertedSubtitleAsync(subtitle);
        RefreshChoices();
    }

    /// <summary>Opens <paramref name="url"/> paused at <paramref name="start"/> seconds; done once mpv has its tracks.</summary>
    private async Task LoadAsync(Uri url, double start)
    {
        var player = Player!;
        // The video view gives mpv its surface on its first frame; a file
        // opened before then would have nowhere to draw.
        if (await Task.WhenAny(player.RenderContextReady, Task.Delay(TimeSpan.FromSeconds(5))) != player.RenderContextReady)
            throw new NoVideoOutputException();
        player.SetProperty("pause", true);
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loading = waiter;
        var options = start > 0 ? "start=" + start.ToString("0.###", CultureInfo.InvariantCulture) : "";
        await player.CommandAsync("loadfile", url.AbsoluteUri, "replace", "-1", options);
        await waiter.Task.WaitAsync(TimeSpan.FromMinutes(1));
        audioTracks.Clear();
        subtitleTracks.Clear();
    }

    private void OnEndFile(MpvEndFile end)
    {
        if (end.Reason != MpvEndFileReason.Error) return;
        var message = end.ErrorMessage ?? "The video couldn't be played.";
        // During a load, the load fails; once playing, the stream broke.
        if (loading is { Task.IsCompleted: false } waiter) waiter.TrySetException(new MpvException(message));
        else Dispatcher.UIThread.Post(() => FallBack(message));
    }

    /// <summary>
    /// Jellyfin numbers a file's streams as the container does, so mpv's
    /// <c>ff-index</c> is the Jellyfin index. External files come later.
    /// </summary>
    private void MapTracks()
    {
        foreach (var track in Player!.Tracks().Where(t => !t.External && t.FfIndex is not null))
        {
            var index = track.FfIndex!.Value;
            if (track.Type == "audio" && audioStreams.Any(s => s.Index == index)) audioTracks[index] = track.Id;
            if (track.Type == "sub" && subtitleStreams.Any(s => s.Index == index)) subtitleTracks[index] = track.Id;
        }
    }

    /// <summary>Subtitle files beside the video, which mpv loads from the server.</summary>
    private async Task AddExternalSubtitlesAsync()
    {
        foreach (var stream in subtitleStreams.Where(s => s.IsExternal == true && s.Index is not null))
        {
            var url = SubtitleFileUrl(stream);
            try
            {
                await Player!.CommandAsync("sub-add", url.AbsoluteUri, "auto", stream.TrackTitle, stream.Language ?? "");
            }
            catch (MpvException) { continue; }
            if (Player.Tracks().LastOrDefault(t => t.Type == "sub" && t.External) is { } added)
            {
                subtitleTracks[stream.Index!.Value] = added.Id;
            }
        }
    }

    private Uri SubtitleFileUrl(MediaStream stream)
    {
        if (stream.DeliveryUrl is { } delivery && client.AbsoluteUrl(delivery) is { } url) return url;
        var format = stream.Codec?.ToLowerInvariant() switch
        {
            "ass" or "ssa" => "ass",
            "webvtt" or "vtt" => "vtt",
            _ => "srt",
        };
        return client.SubtitleFileUrl(entry.ItemId, mediaSourceId!, stream.Index!.Value, format);
    }

    /// <summary>A converted stream's text subtitle, loaded beside it as a file.</summary>
    private async Task ShowConvertedSubtitleAsync(int index)
    {
        if (subtitleStreams.FirstOrDefault(s => s.Index == index) is not { } stream) return;
        foreach (var id in subtitleTracks.Values) Player!.Command("sub-remove", id.ToString(CultureInfo.InvariantCulture));
        subtitleTracks.Clear();
        try
        {
            await Player!.CommandAsync("sub-add", SubtitleFileUrl(stream).AbsoluteUri, "select", stream.TrackTitle, stream.Language ?? "");
            if (Player.Tracks().LastOrDefault(t => t.Type == "sub" && t.External) is { } added) subtitleTracks[index] = added.Id;
        }
        catch (MpvException) { }
    }

    /// <summary>Playing from here on, once mpv has the stream.</summary>
    private async Task BeginAsync()
    {
        Player!.SetProperty("pause", false);
        HasStarted = true;
        stopReported = false;
        RefreshChoices();
        PokeControls();
        VideoAspect = Player.GetDouble("video-params/aspect") ?? 0;
        // After the controls showed, which the row takes the place of.
        if (flickWhenStarted && OpenFlick()) flickWhenStarted = false;
        if (!Reports)
        {
            timedTimer.Start();
            return;
        }
        await client.ReportStartAsync(Report(paused: false));
        reportTimer.Start();
        timedTimer.Start();
    }

    private void OnPlayerProperty(string name, object? value)
    {
        switch (name)
        {
            case "pause":
                IsPaused = value is true;
                // mpv pauses on the last frame at the end; the Up Next card has the screen then.
                if (EndCardVisible) break;
                if (IsPaused) ControlsVisible = true; else PokeControls();
                break;
            case "paused-for-cache":
                IsBuffering = value is true;
                break;
            case "duration":
                Duration = value as double? ?? 0;
                break;
            case "eof-reached":
                if (value is true && HasStarted && !EndCardVisible && !stopped && !PlaybackEnded()) Close();
                break;
        }
    }

    // ---- Falling back ----

    /// <summary>
    /// The load failed or mpv gave up partway. Each try asks the server for
    /// something safer: its own choice of stream, then a plain H.264 encode;
    /// after that the error shows. Playback carries on from where it got to,
    /// on the same tracks.
    /// </summary>
    private void FallBack(string reason)
    {
        if (stopped) return;
        // Another stream won't help a computer that can't show video.
        if (reason == NoVideoOutputException.Explanation)
        {
            ErrorMessage = reason;
            ControlsVisible = true;
            return;
        }
        // A channel has no other stream to step down to.
        if (IsLive)
        {
            ErrorMessage = reason;
            ControlsVisible = true;
            return;
        }
        StreamFallback? next = entry.Fallback switch
        {
            StreamFallback.None => isConverted ? StreamFallback.MostCompatible : StreamFallback.ServerTranscode,
            StreamFallback.ServerTranscode => StreamFallback.MostCompatible,
            _ => null,
        };
        if (next is not { } fallback || Item is null)
        {
            ErrorMessage = reason;
            ControlsVisible = true;
            return;
        }
        System.Diagnostics.Debug.WriteLine($"Playback failed ({reason}); trying {fallback}");
        Restart(e => e with { Fallback = fallback, MediaSourceId = mediaSourceId ?? e.MediaSourceId });
    }

    /// <summary>Where playback has got to, for starting again from here.</summary>
    private long CurrentTicks =>
        HasStarted && Player?.GetDouble("time-pos") is { } seconds && seconds > 0 ? (long)(seconds * 10_000_000) : entry.StartTicks;

    /// <summary>Plays the same title again from here with <paramref name="change"/> made: another quality, or another track on a converted stream.</summary>
    private void Restart(Func<PlaybackQueueItem, PlaybackQueueItem> change)
    {
        var next = change(entry with
        {
            StartTicks = CurrentTicks,
            AudioStreamIndex = currentAudio ?? entry.AudioStreamIndex,
            SubtitleStreamIndex = HasStarted ? currentSubtitle : entry.SubtitleStreamIndex,
        });
        EndServerSession();
        if (Item is { } item) Play(item, next);
    }

    /// <summary>Lets the server know this stream is done: the stop report, or for a stream nothing reported, ending its encode.</summary>
    private void EndServerSession()
    {
        reportTimer.Stop();
        if (HasStarted && !stopReported && Reports)
        {
            stopReported = true;
            _ = client.ReportStoppedAsync(Report(paused: true));
        }
        else if (isConverted && playSessionId is { } session)
        {
            _ = client.StopTranscodingAsync(session);
        }
    }

    /// <summary>Starts another item (or the same one again) in this same player.</summary>
    private void Play(BaseItem next, PlaybackQueueItem nextEntry)
    {
        countdownTimer.Stop();
        Countdown = null;
        timedTimer.Stop();
        segments = [];
        ActiveSegment = null;
        ShowsUpNextAction = false;
        autoSkipped.Clear();
        EndCardVisible = false;
        UpNext = null;
        Chapters = [];
        scrubPreview = null;
        PreviewAt(null);
        themeMoments = [];
        themeDetails = null;
        ThemeCardOpacity = 0;
        HasStarted = false;
        ErrorMessage = null;
        currentAudio = null;
        currentSubtitle = -1;
        Item = next;
        entry = nextEntry;
        reachedCut = false;
        IsLive = false;
        _ = StartAsync();
    }

    // ---- Controls ----

    [RelayCommand]
    private void TogglePause()
    {
        if (!HasStarted || Player is null) return;
        Player.SetProperty("pause", !IsPaused);
        if (IsPaused) _ = ReportProgressAsync();
    }

    public void SeekBy(double seconds)
    {
        if (!HasStarted || Player is null || IsLive) return;
        _ = SafeCommand("seek", seconds.ToString(CultureInfo.InvariantCulture), "relative+exact");
        PokeControls();
    }

    public void SeekTo(double seconds)
    {
        if (!HasStarted || Player is null || IsLive) return;
        _ = SafeCommand("seek", Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture), "absolute");
        PokeControls();
    }

    [RelayCommand] private void Back10() => SeekBy(-10);
    [RelayCommand] private void Forward10() => SeekBy(10);

    private async Task SafeCommand(params string[] args)
    {
        try { await Player!.CommandAsync(args); }
        catch (MpvException) { }
        catch (OperationCanceledException) { }
    }

    /// <summary>Shows the controls, and hides them again after a while of nothing.</summary>
    public void PokeControls()
    {
        ControlsVisible = true;
        hideTimer.Stop();
        hideTimer.Start();
    }

    private void HideControlsIfIdle()
    {
        hideTimer.Stop();
        // Not while the pointer rests on the timeline, or drags along it.
        if (HasStarted && !IsPaused && OpenMenus == 0 && ErrorMessage is null && PreviewSeconds is null) ControlsVisible = false;
    }

    private void ShowToast(string text)
    {
        Toast = text;
        toastTimer.Stop();
        toastTimer.Start();
    }

    /// <summary>The next audio track, round to the first.</summary>
    public void CycleAudio()
    {
        if (AudioChoices.Count < 2) return;
        var at = AudioChoices.ToList().FindIndex(c => c.IsSelected);
        var next = AudioChoices[(at + 1) % AudioChoices.Count];
        next.Select.Execute(null);
        ShowToast($"Audio: {next.Title}");
    }

    /// <summary>The next subtitle track, then off.</summary>
    public void CycleSubtitle()
    {
        if (SubtitleChoices.Count < 2) return;
        var at = SubtitleChoices.ToList().FindIndex(c => c.IsSelected);
        var next = SubtitleChoices[(at + 1) % SubtitleChoices.Count];
        next.Select.Execute(null);
        ShowToast($"Subtitles: {next.Title}");
    }

    public void ChangeVolume(double by)
    {
        if (Player?.GetDouble("volume") is not { } volume) return;
        var level = Math.Clamp(volume + by, 0, 100);
        Player.SetProperty("volume", level);
        Player.SetProperty("mute", false);
        ShowToast($"Volume {level:0}%");
    }

    public void ToggleMute()
    {
        if (Player?.GetFlag("mute") is not { } muted) return;
        Player.SetProperty("mute", !muted);
        ShowToast(muted ? "Sound on" : "Muted");
    }

    // ---- Tracks ----

    private void RefreshChoices()
    {
        AudioChoices = audioStreams
            .Where(s => s.Index is { } i && (isConverted || audioTracks.ContainsKey(i)))
            .Select(s => new TrackChoice(s.Index!.Value, s.TrackTitle, s.Index == currentAudio, new RelayCommand(() => SelectAudio(s.Index!.Value))))
            .ToList();
        var subtitles = subtitleStreams
            .Where(s => s.Index is { } i && (isConverted || subtitleTracks.ContainsKey(i)))
            .Select(s => new TrackChoice(s.Index!.Value, s.TrackTitle, s.Index == currentSubtitle, new RelayCommand(() => SelectSubtitle(s.Index!.Value))));
        SubtitleChoices = [new TrackChoice(-1, "Off", currentSubtitle < 0, new RelayCommand(() => SelectSubtitle(-1))), .. subtitles];
        var quality = qualityCap ?? 0;
        QualityChoices =
        [
            new TrackChoice(0, "Original", quality == 0, new RelayCommand(() => ChooseQuality(0))),
            .. StreamQuality.Caps.Select(cap => new TrackChoice(cap, StreamQuality.Title(cap), quality == cap, new RelayCommand(() => ChooseQuality(cap)))),
        ];
        var speed = Player?.GetDouble("speed") ?? 1;
        SpeedChoices = Speeds
            .Select(s => new TrackChoice((int)(s * 100), s == 1 ? "Normal" : $"{s:0.##}×", Math.Abs(s - speed) < 0.01, new RelayCommand(() => SetSpeed(s))))
            .ToList();
    }

    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 2];

    private void SetSpeed(double speed)
    {
        Player?.SetProperty("speed", speed);
        RefreshChoices();
    }

    private void SetAudioTrack(int index)
    {
        Player!.SetProperty("aid", audioTracks[index].ToString(CultureInfo.InvariantCulture));
        currentAudio = index;
    }

    private void SetSubtitleTrack(int index)
    {
        Player!.SetProperty("sid", index >= 0 ? subtitleTracks[index].ToString(CultureInfo.InvariantCulture) : "no");
        currentSubtitle = index;
    }

    /// <summary>A converted stream carries one audio track, so another one is a new stream from the server, from here.</summary>
    private void SelectAudio(int index)
    {
        if (isConverted)
        {
            if (index != currentAudio) Restart(e => e with { AudioStreamIndex = index, SubtitleStreamIndex = currentSubtitle });
            return;
        }
        if (audioTracks.ContainsKey(index)) SetAudioTrack(index);
        RefreshChoices();
    }

    /// <summary>A subtitle track by Jellyfin index, or -1 for none.</summary>
    private void SelectSubtitle(int index)
    {
        if (isConverted)
        {
            if (index == currentSubtitle) return;
            // A picture-based track is burned in by the server, so taking
            // one on or off needs a new stream; text ones load beside it.
            var burnedIn = currentSubtitle >= 0 && !IsTextSubtitle(currentSubtitle);
            if (burnedIn || (index >= 0 && !IsTextSubtitle(index)))
            {
                Restart(e => e with { SubtitleStreamIndex = index, AudioStreamIndex = currentAudio });
                return;
            }
            currentSubtitle = index;
            if (index >= 0) _ = ShowConvertedSubtitleAsync(index).ContinueWith(_ => Dispatcher.UIThread.Post(RefreshChoices));
            else Player!.SetProperty("sid", "no");
            RefreshChoices();
            return;
        }
        if (index < 0 || subtitleTracks.ContainsKey(index)) SetSubtitleTrack(index);
        RefreshChoices();
    }

    private bool IsTextSubtitle(int index) =>
        subtitleStreams.FirstOrDefault(s => s.Index == index) is not { } stream || TrackRules.IsTextTrack(stream);

    // ---- Quality ----

    /// <summary>Plays on from here at another quality: a cap has the server convert the file down to it; 0 goes back to the original.</summary>
    private void ChooseQuality(int cap)
    {
        QualityOfferVisible = false;
        if (cap == (qualityCap ?? 0)) return;
        Restart(e => e with
        {
            MaxBitrate = cap > 0 ? cap : null,
            // A file that needed the plain encode still does.
            Fallback = e.Fallback == StreamFallback.MostCompatible && cap > 0 ? e.Fallback : StreamFallback.None,
        });
    }

    [RelayCommand] private void LowerQuality() => ChooseQuality(StreamQuality.Caps.FirstOrDefault(c => c < (qualityCap ?? int.MaxValue), StreamQuality.Caps[^1]));

    /// <summary>Stays as it is; the offer holds off for a while.</summary>
    [RelayCommand]
    private void KeepTrying()
    {
        QualityOfferVisible = false;
        stallWatch.Declined(DateTimeOffset.UtcNow);
    }

    // ---- Chapters ----

    /// <summary>The title's chapters in order, when there are enough to be worth choosing from; none on Live TV or a Channel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChapters))]
    public partial IReadOnlyList<ChapterInfo> Chapters { get; private set; } = [];

    public bool HasChapters => Chapters.Count > 0;

    [ObservableProperty] public partial IReadOnlyList<ChapterChoice> ChapterChoices { get; private set; } = [];

    /// <summary>The chapter the playhead is in; -1 before the first.</summary>
    private int currentChapter = -1;

    partial void OnChaptersChanged(IReadOnlyList<ChapterInfo> value)
    {
        currentChapter = -1;
        RefreshChapterChoices();
    }

    private void RefreshChapterChoices() =>
        ChapterChoices = Chapters
            .Select((chapter, index) => new ChapterChoice(ChapterTitle(index), Clock(ChapterStart(index)), index == currentChapter, new RelayCommand(() => PlayChapter(index))))
            .ToList();

    private string ChapterTitle(int index) =>
        string.IsNullOrWhiteSpace(Chapters[index].Name) ? $"Chapter {index + 1}" : Chapters[index].Name!;

    private double ChapterStart(int index) => Chapters[index].StartPositionTicks / 10_000_000.0;

    /// <summary>The chapter <paramref name="seconds"/> falls in; -1 before the first, or with none.</summary>
    private int ChapterAt(double seconds)
    {
        for (var index = Chapters.Count - 1; index >= 0; index--)
        {
            if (ChapterStart(index) <= seconds) return index;
        }
        return -1;
    }

    /// <summary>Plays from the start of a chapter, as choosing one in the Apple app does.</summary>
    private void PlayChapter(int index)
    {
        if (!HasStarted || Player is null || index < 0 || index >= Chapters.Count) return;
        SeekTo(ChapterStart(index));
        Player.SetProperty("pause", false);
    }

    /// <summary>
    /// The next chapter, or back to the start of this one (or the one before,
    /// within its first 3 seconds), as mpv's Page Up and Page Down do; false
    /// when there are no chapters.
    /// </summary>
    public bool StepChapter(int step)
    {
        if (!HasStarted || Chapters.Count == 0) return false;
        var position = Player?.GetDouble("time-pos") ?? Position;
        var at = ChapterAt(position);
        var target = step > 0 ? at + 1 : at >= 0 && position - ChapterStart(at) > 3 ? at : at - 1;
        if (target >= Chapters.Count) return true;
        target = Math.Max(target, 0);
        PlayChapter(target);
        ShowToast(ChapterTitle(target));
        return true;
    }

    // ---- The scrub preview ----

    /// <summary>The trickplay frames, when the server has made them for this file.</summary>
    private ScrubPreviewSource? scrubPreview;
    private int previewRequest;

    /// <summary>The point on the timeline under the pointer, or being dragged to; null with no preview showing.</summary>
    [ObservableProperty] public partial double? PreviewSeconds { get; private set; }
    [ObservableProperty] public partial string PreviewTime { get; private set; } = "";

    /// <summary>The chapter at that point, when there are chapters.</summary>
    [ObservableProperty] public partial string? PreviewChapter { get; private set; }

    /// <summary>Its trickplay frame; null until loaded, or without trickplay (the preview shows just the time).</summary>
    [ObservableProperty] public partial IImage? PreviewFrame { get; private set; }

    /// <summary>Shows the preview for a point on the timeline, or hides it with null.</summary>
    public void PreviewAt(double? seconds)
    {
        PreviewSeconds = seconds;
        if (seconds is not { } at)
        {
            PreviewFrame = null;
            return;
        }
        PokeControls();
        PreviewTime = Clock(at);
        PreviewChapter = Chapters.Count > 0 && ChapterAt(at) is var chapter and >= 0 ? ChapterTitle(chapter) : null;
        if (scrubPreview is { } source) _ = LoadPreviewFrameAsync(source, at, ++previewRequest);
    }

    private async Task LoadPreviewFrameAsync(ScrubPreviewSource source, double seconds, int request)
    {
        var frame = await source.FrameAtAsync(seconds);
        // The last frame stays up while the next sheet loads, rather than blinking.
        if (frame is not null && request == previewRequest && PreviewSeconds is not null && source == scrubPreview) PreviewFrame = frame;
    }

    // ---- The song card ----

    /// <summary>The openings and endings in what's playing (an anime episode's, or a whole music video), with what their card says.</summary>
    private IReadOnlyList<ThemeMoment> themeMoments = [];

    /// <summary>What the card says; kept as it fades out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeCardKicker))]
    public partial ThemeInfo? ThemeCard { get; private set; }

    public string? ThemeCardKicker => ThemeCard?.Kicker.ToUpperInvariant();

    /// <summary>1 while the card shows, 0 while it doesn't; the view fades between them.</summary>
    [ObservableProperty] public partial double ThemeCardOpacity { get; private set; }

    /// <summary>The episode's themes: the OP/ED plugin's, else (without the plugin) AnimeThemes'.</summary>
    private Task<(IReadOnlyList<OpEdTheme> Plugin, IReadOnlyList<KnownTheme> Known)>? themeDetails;

    private async Task<(IReadOnlyList<OpEdTheme> Plugin, IReadOnlyList<KnownTheme> Known)> ThemeDetailsAsync(BaseItem item)
    {
        if (client is JellyfinClient jellyfin && await jellyfin.OpEdThemesAsync(item.Id) is { } plugin) return (plugin, []);
        var known = await AnimeThemesLookup.ForEpisodeAsync(item, client);
        System.Diagnostics.Debug.WriteLine($"Playback: AnimeThemes has {known.Count} themes for {item.SeriesName}");
        return ([], known);
    }

    /// <summary>
    /// Finds the song cards once playing, as the Apple app does: from an
    /// episode's chapters, or its skip markers (as far as they've loaded)
    /// when the OP/ED plugin or AnimeThemes says a theme plays in it; a music
    /// video's for the whole video.
    /// </summary>
    private async Task LoadThemeMomentsAsync(BaseItem item)
    {
        if (!settings.ThemeCards) return;
        IReadOnlyList<ThemeMoment> moments;
        if (item.Kind == ItemKind.Episode)
        {
            var (plugin, known) = await (themeDetails ??= ThemeDetailsAsync(item));
            moments = ThemeMoments.ForEpisode(item, segments, plugin, known);
        }
        else
        {
            moments = item.Kind == ItemKind.MusicVideo && client is JellyfinClient jellyfin ? await ThemeMoments.ForMusicVideoAsync(item, jellyfin) : [];
        }
        if (stopped || Item?.Id != item.Id) return;
        themeMoments = moments;
        System.Diagnostics.Debug.WriteLine($"Playback: song cards {string.Join(", ", moments.Select(m => $"{m.Info.Kicker} {m.Start:0}-{m.End:0}s"))}");
    }

    private void UpdateThemeCard(double seconds)
    {
        var showing = themeMoments.FirstOrDefault(m => m.ShowsCardAt(seconds));
        if (showing is not null) ThemeCard = showing.Info;
        ThemeCardOpacity = showing is null ? 0 : 1;
    }

    // ---- Skipping and Up Next ----

    /// <summary>The skip button: past the intro, credits or recap, or on to the next episode.</summary>
    [RelayCommand]
    private void Skip()
    {
        if (ActiveSegment is { } segment)
        {
            ActiveSegment = null;
            SeekTo(segment.EndTicks / 10_000_000.0);
        }
        else if (ShowsUpNextAction)
        {
            PlayUpNextNow();
        }
    }

    /// <summary>
    /// Four times a second: the position, stalls, which marker the playhead
    /// is in (skipping an intro by itself when that's set), and whether it's
    /// time to offer the next episode, at the credits or in the last 30 seconds.
    /// </summary>
    private void UpdateTimedState()
    {
        if (!HasStarted || EndCardVisible || Player is null) return;
        if (Player.GetDouble("time-pos") is not { } seconds) return;
        Position = seconds;
        if (!IsScrubbing)
        {
            updatingSeekBar = true;
            SeekBarValue = seconds;
            updatingSeekBar = false;
        }
        if (Chapters.Count > 0 && ChapterAt(seconds) is var chapter && chapter != currentChapter)
        {
            currentChapter = chapter;
            RefreshChapterChoices();
        }
        UpdateThemeCard(seconds);
        if (!QualityOfferVisible && stallWatch.Note(IsBuffering, DateTimeOffset.UtcNow)) QualityOfferVisible = true;

        var ticks = (long)(seconds * 10_000_000);
        // A break clip cut short to land on the half hour ends at its cut, as if it had played out.
        if (entry.ChannelCutTicks is { } cut && ticks >= cut && !reachedCut)
        {
            reachedCut = true;
            if (!PlaybackEnded()) Close();
            return;
        }
        // An opening played on its own ends with it.
        if (entry.ClipEndTicks is { } clipEnd && ticks >= clipEnd)
        {
            Close();
            return;
        }
        var inside = segments.FirstOrDefault(s => s.Contains(ticks));
        if (inside?.Id != ActiveSegment?.Id)
        {
            ActiveSegment = inside;
            if (inside is not null) FreshenSkipOffer();
        }
        if (settings.AutoSkipIntro && inside is { Type: "Intro" } intro && autoSkipped.Add(intro.Id)) Skip();

        var offer = false;
        var runTime = Duration > 0 ? (long)(Duration * 10_000_000) : Item?.RunTimeTicks ?? 0;
        if (UpNext is not null && runTime > 0)
        {
            var creditsStart = segments.FirstOrDefault(s => s.Type == "Outro")?.StartTicks;
            var threshold = creditsStart ?? Math.Max(runTime - 30 * 10_000_000L, (long)(runTime * 0.92));
            offer = ticks >= threshold;
        }
        if (offer != ShowsUpNextAction)
        {
            ShowsUpNextAction = offer;
            if (offer && ActiveSegment is null) FreshenSkipOffer();
        }
    }

    private void FreshenSkipOffer()
    {
        SkipOfferIsFresh = true;
        skipOfferTimer?.Stop();
        skipOfferTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        skipOfferTimer.Tick += (_, _) => { SkipOfferIsFresh = false; skipOfferTimer?.Stop(); };
        skipOfferTimer.Start();
    }

    /// <summary>
    /// The item played to its end: the server hears it finished, and when
    /// there's a next episode its card holds the screen, counting down to play
    /// it if autoplay is on. False when there's nothing next, and the player should close.
    /// </summary>
    private bool PlaybackEnded()
    {
        reportTimer.Stop();
        timedTimer.Stop();
        ActiveSegment = null;
        ShowsUpNextAction = false;
        // The Up Next card has the screen to itself.
        ThemeCardOpacity = 0;
        // A channel goes on to whatever it's showing now.
        if (entry.ChannelId is { } channelId)
        {
            StopServerSession();
            if (Aster.Core.Channels.ChannelTuner.Live(channelId, finishedItemId: entry.ItemId) is not var (item, next)) return false;
            Play(item, next);
            return true;
        }
        // An opening played on its own closes the player, unreported.
        if (IsThemePlayback) return false;
        if (HasStarted && !stopReported)
        {
            stopReported = true;
            var final = Report(paused: true);
            // At the very end, so the server marks it watched.
            if (Duration > 0) final = final with { PositionTicks = (long)(Duration * 10_000_000) };
            _ = client.ReportStoppedAsync(final);
        }
        // A disc menu's Play All goes straight on to the next of its extras.
        if (entry.Following.Count > 0)
        {
            var following = entry.Following[0];
            Play(new BaseItem { Id = following }, new PlaybackQueueItem { ItemId = following, Following = entry.Following.Skip(1).ToList() });
            return true;
        }
        if (UpNext is null) return false;
        EndCardVisible = true;
        ControlsVisible = false;
        if (settings.AutoplayNext)
        {
            Countdown = 10;
            countdownTimer.Start();
        }
        return true;
    }

    private void CountdownTick()
    {
        if (Countdown is not { } seconds) return;
        if (seconds > 1) Countdown = seconds - 1;
        else PlayUpNextNow();
    }

    /// <summary>Plays the next episode in this same player.</summary>
    [RelayCommand]
    private void PlayUpNextNow()
    {
        countdownTimer.Stop();
        Countdown = null;
        if (UpNext is not { } next) return;
        // From Play Next Episode, before the end: this one stops here.
        if (HasStarted && !stopReported)
        {
            stopReported = true;
            reportTimer.Stop();
            _ = client.ReportStoppedAsync(Report(paused: true));
        }
        Play(next, new PlaybackQueueItem { ItemId = next.Id, StartTicks = next.UserData?.PlaybackPositionTicks ?? 0 });
    }

    /// <summary>Closes the Up Next card without playing on; the player closes too.</summary>
    [RelayCommand]
    private void DismissEndCard()
    {
        countdownTimer.Stop();
        Countdown = null;
        EndCardVisible = false;
        Close();
    }

    // ---- Reporting and closing ----

    private async Task ReportProgressAsync()
    {
        if (!HasStarted || stopped || stopReported || !Reports) return;
        System.Diagnostics.Debug.WriteLine($"Playback: at {Position:0}s, decoder {Player?.GetString("hwdec-current")}, {Player?.GetString("estimated-vf-fps")} fps, dropped {Player?.GetString("frame-drop-count")} by the output and {Player?.GetString("decoder-frame-drop-count")} by the decoder");
        await client.ReportProgressAsync(Report(paused: IsPaused));
    }

    private PlaybackReport Report(bool paused)
    {
        var seconds = Player?.GetDouble("time-pos") ?? 0;
        return new PlaybackReport
        {
            ItemId = entry.ItemId,
            MediaSourceId = mediaSourceId,
            PlaySessionId = playSessionId,
            PositionTicks = double.IsFinite(seconds) && seconds > 0 ? (long)(seconds * 10_000_000) : 0,
            IsPaused = paused,
            PlayMethod = playMethod,
            AudioStreamIndex = currentAudio,
            SubtitleStreamIndex = currentSubtitle >= 0 ? currentSubtitle : null,
            RunTimeTicks = Item?.RunTimeTicks ?? (Duration > 0 ? (long)(Duration * 10_000_000) : null),
        };
    }

    /// <summary>Sends where playback is and saves it as the resume point now, for when the app is closing.</summary>
    public void SaveProgressNow()
    {
        if (!HasStarted || stopped || stopReported || !Reports) return;
        _ = client.ReportProgressAsync(Report(paused: IsPaused), ResumePoints.Saving.Now);
    }

    [RelayCommand]
    private void Close()
    {
        Stop();
        close(Item);
    }

    /// <summary>The report <see cref="Stop"/> sent, for the app to wait on as it closes.</summary>
    private Task stopReport = Task.CompletedTask;

    /// <summary>
    /// Closing the app: waits a moment for the server to hear playback
    /// stopped, which otherwise goes unsent as the app exits (Plex would show
    /// it playing until it gave up on it).
    /// </summary>
    public void FinishReporting(TimeSpan longest)
    {
        try { stopReport.Wait(longest); }
        catch (AggregateException) { }
    }

    /// <summary>Stops playback and tells the server where it ended. Safe to call twice.</summary>
    public void Stop()
    {
        if (stopped) return;
        stopped = true;
        reportTimer.Stop();
        timedTimer.Stop();
        countdownTimer.Stop();
        hideTimer.Stop();
        toastTimer.Stop();
        skipOfferTimer?.Stop();
        loading?.TrySetCanceled();
        if (Player is null) return;
        var final = Report(paused: true);
        try { Player.Command("stop"); }
        catch (MpvException) { }
        bugTimer.Stop();
        flickTimer.Stop();
        if (HasStarted && !stopReported && Reports)
        {
            stopReported = true;
            stopReport = client.ReportStoppedAsync(final);
        }
        else
        {
            StopServerSession();
        }
        if (liveStreamId is { } live && client is JellyfinClient jellyfin) _ = jellyfin.CloseLiveStreamAsync(live);
        Player.Dispose();
    }
}

/// <summary>The video view never gave mpv an OpenGL surface: no stream can be shown, so none is tried.</summary>
public sealed class NoVideoOutputException() : Exception(Explanation)
{
    public const string Explanation = "Video can't be shown: OpenGL isn't available. On Linux, check the graphics driver; in a virtual machine, turn on 3D acceleration.";
}
