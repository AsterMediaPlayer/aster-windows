using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Aster.Desktop.ViewModels;
using Aster.Desktop.Views;

namespace Aster.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            // ASTER_LOG names a file for the app's and mpv's messages.
            if (System.Environment.GetEnvironmentVariable("ASTER_LOG") is { } log)
            {
                System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(log));
                System.Diagnostics.Trace.AutoFlush = true;
            }
#endif
            var args = desktop.Args ?? [];
            var play = System.Array.IndexOf(args, "--play") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            var bitrate = System.Array.IndexOf(args, "--max-bitrate") is var b and >= 0 && b + 1 < args.Length && int.TryParse(args[b + 1], out var cap) ? cap : (int?)null;
            var start = System.Array.IndexOf(args, "--start") is var s and >= 0 && s + 1 < args.Length
                && double.TryParse(args[s + 1], System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : (double?)null;
            var open = System.Array.IndexOf(args, "--open") is var o and >= 0 && o + 1 < args.Length ? args[o + 1] : null;
            var tab = System.Array.IndexOf(args, "--tab") is var t and >= 0 && t + 1 < args.Length
                && System.Enum.TryParse<Aster.Desktop.ViewModels.AppTab>(args[t + 1], ignoreCase: true, out var chosen) ? chosen : (Aster.Desktop.ViewModels.AppTab?)null;
            var sort = System.Array.IndexOf(args, "--sort") is var so and >= 0 && so + 1 < args.Length
                && System.Enum.TryParse<Aster.Core.Browsing.LibrarySort>(args[so + 1], ignoreCase: true, out var sorted) ? sorted : (Aster.Core.Browsing.LibrarySort?)null;
            var section = System.Array.IndexOf(args, "--section") is var se and >= 0 && se + 1 < args.Length ? args[se + 1] : null;
            var main = new MainViewModel(play, bitrate, start, open, tab, sort, section);
#if DEBUG
            // --plex-sign-in: straight to Plex's sign-in, for trying it.
            if (System.Array.IndexOf(args, "--plex-sign-in") >= 0) main.ShowPlexSignIn();
            // For screenshots: --disc-menu opens the --open title's disc menu;
            // --channel <number> tunes in a Channel (with --tab channels), and
            // --flick shows the channel row once it plays.
            if (System.Array.IndexOf(args, "--disc-menu") >= 0) main.OpensDiscMenu = true;
            if (System.Array.IndexOf(args, "--flick") >= 0) main.OpensChannelRow = true;
            if (System.Array.IndexOf(args, "--now-playing") >= 0) main.OpensNowPlaying = true;
            // --play-all (with --open on an anime show): its Music tab's Play All, in the Now Playing panel; --play-all-full, full screen.
            if (System.Array.IndexOf(args, "--play-all") >= 0) main.PlaysAll = 1;
            if (System.Array.IndexOf(args, "--play-all-full") >= 0) main.PlaysAll = 2;
            // --playlist-picker (with --open): that title's Add to Playlist.
            if (System.Array.IndexOf(args, "--playlist-picker") >= 0) main.OpensPlaylistPicker = true;
            // --paused (with --play): paused once it starts, for the pause screen.
            if (System.Array.IndexOf(args, "--paused") >= 0) main.StartsPaused = true;
            // --edit-channels [playlistId]: Edit Channels, or one channel's settings alone.
            if (System.Array.IndexOf(args, "--edit-channels") is var ec and >= 0)
                main.OpenEditChannels(ec + 1 < args.Length && !args[ec + 1].StartsWith("--", System.StringComparison.Ordinal) ? args[ec + 1] : null);
            // --mini <channelId> (with --tab livetv): that channel in the Live TV header's mini player.
            if (System.Array.IndexOf(args, "--mini") is var mi and >= 0 && mi + 1 < args.Length) main.TuneMiniPlayer(args[mi + 1]);
            if (System.Array.IndexOf(args, "--channel") is var ch and >= 0 && ch + 1 < args.Length && int.TryParse(args[ch + 1], out var number)) main.TuneChannelWhenLoaded(number);
#endif
            desktop.MainWindow = new MainWindow { DataContext = main };
#if DEBUG
            // --full-screen: the window starts full screen, as for screenshots.
            if (System.Array.IndexOf(args, "--full-screen") >= 0) Aster.Desktop.Controls.FullScreen.Toggle(desktop.MainWindow);
#endif
            // Game controllers, as the keys.
            var gamepads = Aster.Desktop.Input.Gamepads.Start(desktop.MainWindow);
            desktop.ShutdownRequested += (_, _) =>
            {
                main.Shutdown();
                gamepads.Dispose();
            };
            System.Threading.Tasks.Task.Run(() => Aster.Core.Jellyfin.ImageCache.Trim());
        }

        base.OnFrameworkInitializationCompleted();
    }
}