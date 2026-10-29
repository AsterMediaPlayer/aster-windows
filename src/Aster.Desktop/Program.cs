using Avalonia;
using System;

namespace Aster.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // ASTER_LOG_AVALONIA: Avalonia's own messages (which renderer it chose, and why) on the console.
        if (Environment.GetEnvironmentVariable("ASTER_LOG_AVALONIA") is not null)
        {
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
            System.Diagnostics.Trace.AutoFlush = true;
        }
#if WINDOWS
        // Before any window, which takes the process's app id.
        WindowsAppIdentity.Apply();
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(LinuxOptions())
            .LogToTrace(Environment.GetEnvironmentVariable("ASTER_LOG_AVALONIA") is null ? Avalonia.Logging.LogEventLevel.Warning : Avalonia.Logging.LogEventLevel.Information);

    /// <summary>
    /// Video is drawn with OpenGL, so a renderer Avalonia turns down means no
    /// video at all. Its list also turns down VMware's and VirtualBox's
    /// SVGA3D, a virtual machine's only OpenGL, which plays well enough; this
    /// keeps only Mesa's software renderer (llvmpipe) out, which a PC with a
    /// graphics driver never needs. ASTER_SOFTWARE_GL=1 allows that too, for
    /// a virtual machine without 3D acceleration, slow as it is.
    /// </summary>
    private static X11PlatformOptions LinuxOptions() => new()
    {
        GlxRendererBlacklist = Environment.GetEnvironmentVariable("ASTER_SOFTWARE_GL") is null ? ["llvmpipe"] : [],
    };
}
