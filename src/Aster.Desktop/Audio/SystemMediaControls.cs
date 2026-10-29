// The system's own media controls: Windows' media overlay (the volume
// flyout, the lock screen, Bluetooth headsets' buttons) and Linux's MPRIS
// (KDE's and GNOME's media applets, playerctl, headsets through them). The
// counterpart of the Apple app's Now Playing and remote commands, for songs
// and audiobooks; a video is left to the player's own keys, as on the Mac.

using System;

namespace Aster.Desktop.Audio;

/// <summary>What the system shows while a song or book plays.</summary>
/// <param name="Title">The song, or the book.</param>
/// <param name="Artist">The song's artist, or the book's author.</param>
/// <param name="Album">The song's album, or the book's chapter.</param>
/// <param name="Artwork">The album's or book's cover.</param>
/// <param name="Duration">The song's length, or the book's chapter's, in seconds.</param>
/// <param name="Position">How far into it, in seconds.</param>
/// <param name="IsBook">A book: its chapters for next and previous, 15 and 30 second skips, and its speed.</param>
/// <param name="Id">Tells one song or book from the next.</param>
public sealed record NowPlayingInfo(
    string Id,
    string Title,
    string? Artist,
    string? Album,
    Uri? Artwork,
    double Duration,
    double Position,
    bool IsPlaying,
    bool IsBook,
    double Rate,
    bool CanGoNext,
    bool CanGoPrevious);

public enum MediaCommand
{
    Play,
    Pause,
    TogglePlayPause,
    Next,
    Previous,
    Stop,
    SkipForward,
    SkipBack,
}

/// <summary>One system's media controls.</summary>
public interface ISystemMediaControls : IDisposable
{
    /// <summary>Shows what's playing, or with null that nothing is.</summary>
    void Show(NowPlayingInfo? info, byte[]? artwork);

    /// <summary>A button pressed, on the screen or a headset.</summary>
    event Action<MediaCommand>? Command;

    /// <summary>A point on the timeline chosen, in seconds.</summary>
    event Action<double>? SeekRequested;
}

public static class SystemMediaControls
{
    /// <summary>
    /// This system's media controls, or null when it has none the app can
    /// reach (no D-Bus session, say). <paramref name="window"/> is the main
    /// window's handle, which Windows ties its controls to.
    /// </summary>
    public static ISystemMediaControls? Create(IntPtr window)
    {
        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && window != IntPtr.Zero) return new WindowsMediaControls(window);
#endif
            if (OperatingSystem.IsLinux()) return MprisMediaControls.Start();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Media controls: unavailable ({error.Message})");
        }
        return null;
    }
}
