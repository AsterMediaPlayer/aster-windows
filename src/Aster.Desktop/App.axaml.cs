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
#endif
            desktop.MainWindow = new MainWindow { DataContext = main };
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