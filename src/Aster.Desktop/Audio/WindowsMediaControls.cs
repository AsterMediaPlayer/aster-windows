#if WINDOWS
// Windows' media overlay (the volume flyout's player, the lock screen, a
// Bluetooth headset's buttons), reached through the System Media Transport
// Controls for the main window. Its events come on a thread of their own;
// AudioSession brings them to the UI thread.

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage.Streams;

namespace Aster.Desktop.Audio;

[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class WindowsMediaControls : ISystemMediaControls
{
    private readonly SystemMediaTransportControls controls;
    private string? shownId;
    /// <summary>The title, artist and album shown: a book's album is its chapter, which changes as it plays.</summary>
    private (string, string?, string?)? shownText;

    public WindowsMediaControls(IntPtr window)
    {
        controls = SystemMediaTransportControlsInterop.GetForWindow(window);
        controls.IsEnabled = false;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsStopEnabled = true;
        controls.ButtonPressed += OnButtonPressed;
        controls.PlaybackPositionChangeRequested += OnPositionChangeRequested;
    }

    public event Action<MediaCommand>? Command;
    public event Action<double>? SeekRequested;

    public void Show(NowPlayingInfo? info, byte[]? artwork)
    {
        if (info is null)
        {
            shownId = null;
            shownText = null;
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            controls.IsEnabled = false;
            return;
        }
        controls.IsEnabled = true;
        controls.IsNextEnabled = info.CanGoNext;
        controls.IsPreviousEnabled = info.CanGoPrevious;
        // A headset's (or a keyboard's) fast-forward and rewind skip a book.
        controls.IsFastForwardEnabled = info.IsBook;
        controls.IsRewindEnabled = info.IsBook;
        controls.PlaybackStatus = info.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        controls.PlaybackRate = info.Rate;

        // The title and picture only as they change; the timeline each time.
        if (shownId != info.Id || shownText != (info.Title, info.Artist, info.Album) || artwork is not null)
        {
            shownId = info.Id;
            shownText = (info.Title, info.Artist, info.Album);
            var display = controls.DisplayUpdater;
            display.ClearAll();
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = info.Title;
            display.MusicProperties.Artist = info.Artist ?? "";
            display.MusicProperties.AlbumTitle = info.Album ?? "";
            display.Update();
            if (artwork is not null) _ = SetThumbnailAsync(info.Id, artwork);
        }
        var duration = TimeSpan.FromSeconds(Math.Max(info.Duration, 0));
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = duration,
            MaxSeekTime = duration,
            Position = TimeSpan.FromSeconds(Math.Clamp(info.Position, 0, Math.Max(info.Duration, 0))),
        });
    }

    /// <summary>
    /// The cover, in a stream of Windows' own: the overlay reads it from
    /// another process, which a .NET stream wrapped for WinRT can't serve.
    /// </summary>
    private async Task SetThumbnailAsync(string id, byte[] artwork)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artwork.AsBuffer());
            stream.Seek(0);
            if (shownId != id) return;
            var display = controls.DisplayUpdater;
            display.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            display.Update();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Media controls: no cover ({error.Message})");
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        MediaCommand? command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaCommand.Play,
            SystemMediaTransportControlsButton.Pause => MediaCommand.Pause,
            SystemMediaTransportControlsButton.Stop => MediaCommand.Stop,
            SystemMediaTransportControlsButton.Next => MediaCommand.Next,
            SystemMediaTransportControlsButton.Previous => MediaCommand.Previous,
            SystemMediaTransportControlsButton.FastForward => MediaCommand.SkipForward,
            SystemMediaTransportControlsButton.Rewind => MediaCommand.SkipBack,
            _ => null,
        };
        if (command is { } pressed) Command?.Invoke(pressed);
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        SeekRequested?.Invoke(args.RequestedPlaybackPosition.TotalSeconds);

    public void Dispose()
    {
        controls.ButtonPressed -= OnButtonPressed;
        controls.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
        Show(null, null);
    }
}
#endif
