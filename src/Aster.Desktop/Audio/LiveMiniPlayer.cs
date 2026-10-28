using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Jellyfin;
using Aster.Mpv;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.Audio;

/// <summary>
/// The Live TV channel that keeps playing in the Live TV header after the
/// full-screen player closes, as the Apple app's LiveMiniPlayer. mpv's
/// picture can't move from the full-screen view to this one, so the channel
/// is tuned again here (a moment's gap), with its own live stream, closed
/// when it stops.
/// </summary>
public sealed partial class LiveMiniPlayer : ObservableObject, IDisposable
{
    private readonly JellyfinClient client;
    private string? liveStreamId;
    private string? playSessionId;
    private int tuning;

    public LiveMiniPlayer(JellyfinClient client) => this.client = client;

    /// <summary>mpv, for the header's picture; null while nothing plays.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial MpvPlayer? Player { get; private set; }

    /// <summary>The channel playing: its number and name, for the tooltip and for going full screen again.</summary>
    [ObservableProperty] public partial BaseItem? Channel { get; private set; }

    public bool IsShown => Player is not null;

    public string Tip => Channel is { } channel
        ? $"Now playing {string.Join(" ", new[] { channel.ChannelNumber, channel.DisplayTitle }.OfType<string>())}: click to watch full screen"
        : "Live TV";

    partial void OnChannelChanged(BaseItem? value) => OnPropertyChanged(nameof(Tip));

    /// <summary>Tunes <paramref name="channel"/> in the header, from what's on now.</summary>
    public async Task StartAsync(BaseItem channel)
    {
        Stop();
        var number = ++tuning;
        MpvPlayer player;
        try
        {
            player = MpvPlayer.Create(ThemePlayer.LoopingOptions(new()
            {
                ["loop-file"] = "no",
                ["vo"] = "libmpv",
                ["hwdec"] = MpvHardware.Decoding,
                ["sid"] = "no",
                ["osc"] = "no",
                ["osd-level"] = "0",
                ["input-vo-keyboard"] = "no",
                ["cache"] = "yes",
            }));
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            return;
        }
        Channel = channel;
        Player = player;
        try
        {
            var info = await client.PlaybackInfoAsync(channel.Id, 0, AppSettings.Current.MaxBitrate, subtitleStreamIndex: -1);
            if (number != tuning || info.MediaSources.FirstOrDefault() is not { } source) return;
            liveStreamId = source.LiveStreamId;
            playSessionId = info.PlaySessionId;
            Uri? url = source.TranscodingUrl is { } path ? client.AbsoluteUrl(path)
                : source.Path is { } direct && direct.Contains("m3u8", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(direct, UriKind.Absolute, out var hls) ? hls
                : null;
            if (url is null)
            {
                Stop();
                return;
            }
            // mpv's video output needs the header's view to draw in before the stream opens.
            if (await Task.WhenAny(player.RenderContextReady, Task.Delay(TimeSpan.FromSeconds(5))) != player.RenderContextReady || number != tuning) return;
            await player.CommandAsync("loadfile", url.AbsoluteUri);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException or MpvException or ObjectDisposedException)
        {
            if (number == tuning) Stop();
        }
    }

    /// <summary>Ends it: the picture goes, and the server's live stream and conversion with it.</summary>
    public void Stop()
    {
        tuning++;
        var player = Player;
        Player = null;
        Channel = null;
        // The view lets go of mpv's render context as the picture leaves, which mpv waits for before it ends.
        player?.Dispose();
        if (liveStreamId is { } live) _ = Quietly(client.CloseLiveStreamAsync(live));
        if (playSessionId is { } session) _ = Quietly(client.StopTranscodingAsync(session));
        liveStreamId = null;
        playSessionId = null;
    }

    private static async Task Quietly(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
    }

    public void Dispose() => Stop();
}
