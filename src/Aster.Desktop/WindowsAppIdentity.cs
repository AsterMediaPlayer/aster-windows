#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Aster.Desktop;

/// <summary>
/// Aster's app id, which Windows' media overlay names its player by: from
/// the Start menu shortcut carrying the same id (the installer's), so it
/// says "Aster" with Aster's icon rather than "Unknown app".
/// </summary>
internal static partial class WindowsAppIdentity
{
    /// <summary>The installer's shortcuts carry the same id (installer/aster.iss).</summary>
    public const string AppId = "AsterMediaPlayer.Aster";

    public static void Apply()
    {
        // Only the name in Windows' own lists is lost.
        try { SetCurrentProcessExplicitAppUserModelID(AppId); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"App identity: {error.Message}"); }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
#endif
