using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>One open-source component in the app.</summary>
/// <param name="Role">What it does in Aster.</param>
/// <param name="Files">The bundled texts (in Licenses/, without ".txt"), in order.</param>
/// <param name="Note">Anything the license or its holders ask to be said.</param>
public sealed record OpenSourceComponent(string Name, string Role, string License, string Url, IReadOnlyList<string> Files, string? Note = null);

public sealed record OpenSourceGroup(string Title, IReadOnlyList<OpenSourceRow> Rows);

public sealed record OpenSourceRow(OpenSourceComponent Component, IRelayCommand Open);

/// <summary>
/// Settings ▸ About ▸ Open Source &amp; Licenses, as the Apple app has it:
/// Aster's own license, then every open-source component in the app (on
/// this system: Windows' download carries libmpv and ANGLE, Linux uses
/// the system's mpv), what it does here, and its license in full. The
/// texts ship in the app (the Licenses folder), copied from each project,
/// as their licenses ask.
/// </summary>
public sealed class OpenSourceViewModel : ViewModelBase
{
    public static readonly OpenSourceComponent Aster = new(
        "Aster Media Player", "This app. Its source is published at the address shown.",
        "MPL-2.0", OperatingSystem.IsWindows() ? "https://github.com/AsterMediaPlayer/aster-windows" : "https://github.com/AsterMediaPlayer/aster-linux", ["Aster-MPL-2.0"]);

    /// <summary>Every component in the app on this system, by what it's for.</summary>
    public static IReadOnlyList<(string Title, IReadOnlyList<OpenSourceComponent> Components)> Groups(bool windows) =>
    [
        ("Playback", windows
            ?
            [
                new("mpv", "Plays every video and song: shinchiro's build of libmpv, included with Aster.",
                    "GPL-3.0 or later, as built", "https://github.com/shinchiro/mpv-winbuild-cmake", ["GPL-3.0"],
                    "Built with FFmpeg and the libraries built into it (among them libass, libplacebo, dav1d, x264 and x265), each under its own license; " +
                    "the build's configuration, and where each library's source is, are in mpv-winbuild-cmake at the address shown. mpv's own source is at mpv.io."),
                new("FFmpeg", "Opens and decodes the files mpv plays, built into libmpv.",
                    "GPL-3.0 or later, as built", "https://ffmpeg.org", ["GPL-3.0"], "FFmpeg is a trademark of Fabrice Bellard."),
                new("SDL", "Reads game controllers.", "Zlib", "https://libsdl.org", ["SDL3"]),
                new("SDL3-CS", "Lets the app call SDL.", "MIT", "https://github.com/ppy/SDL3-CS", ["SDL3-CS"]),
            ]
            :
            [
                new("mpv", "Plays every video and song, with the FFmpeg it's built with. Aster uses the mpv library your system provides (libmpv), so neither is included in Aster's download.",
                    "GPL-2.0 or later (LGPL-2.1 or later when built without GPL parts)", "https://mpv.io", []),
                new("SDL", "Reads game controllers.", "Zlib", "https://libsdl.org", ["SDL3"]),
                new("SDL3-CS", "Lets the app call SDL.", "MIT", "https://github.com/ppy/SDL3-CS", ["SDL3-CS"]),
            ]),
        ("Interface",
        [
            new("Avalonia", "Draws the app's windows and controls.", "MIT", "https://avaloniaui.net", ["Avalonia"]),
            new("SkiaSharp and HarfBuzzSharp", "Draw the interface with Skia, and lay out its text with HarfBuzz.",
                "MIT; Skia, HarfBuzz and the libraries in them under their own licenses", "https://github.com/mono/SkiaSharp", ["SkiaSharp", "SkiaSharp-notices"]),
            .. (windows
                ? new OpenSourceComponent[] { new("ANGLE", "Turns the app's drawing into Direct3D.", "BSD-3-Clause", "https://angleproject.org", ["ANGLE"]) }
                : []),
            new("Inter", "The app's typeface.", "SIL Open Font License 1.1", "https://rsms.me/inter", ["Inter"]),
            new("Fluent UI System Icons", windows ? "The few icons Windows' own Segoe Fluent Icons doesn't have." : "The app's icons.",
                "MIT", "https://github.com/microsoft/fluentui-system-icons", ["FluentSystemIcons"]),
            new("CommunityToolkit.Mvvm", "Connects each screen to what it shows.", "MIT", "https://github.com/CommunityToolkit/dotnet", ["CommunityToolkit", "CommunityToolkit-notices"]),
            new("MicroCom", "Avalonia's bridge to the system's native interfaces.", "MIT", "https://github.com/kekekeks/MicroCom", ["MicroCom"]),
        ]),
        ("System",
        [
            new(".NET", "What Aster is built on, included so nothing else needs installing.",
                "MIT; the libraries in it under their own licenses", "https://dot.net", ["dotnet", "dotnet-notices"]),
            new("Tmds.DBus", "Talks to the Linux desktop: the media keys, and what's playing in its media controls.",
                "MIT", "https://github.com/tmds/Tmds.DBus", ["Tmds.DBus"]),
            .. (windows
                ? new OpenSourceComponent[] { new("C#/WinRT", "Lets the app use Windows' own interfaces: the media keys, and what's playing in Windows' media controls.", "MIT", "https://github.com/microsoft/CsWinRT", ["CsWinRT"]) }
                : []),
        ]),
    ];

    public OpenSourceViewModel(INavigator navigator)
    {
        IRelayCommand Open(OpenSourceComponent component) => new RelayCommand(() => navigator.Push(new LicenseViewModel(component)));
        Own = new OpenSourceRow(Aster, Open(Aster));
        Sections = Groups(OperatingSystem.IsWindows())
            .Select(g => new OpenSourceGroup(g.Title.ToUpperInvariant(), g.Components.Select(c => new OpenSourceRow(c, Open(c))).ToList()))
            .ToList();
    }

    public OpenSourceRow Own { get; }

    public IReadOnlyList<OpenSourceGroup> Sections { get; }
}

/// <summary>One component's page: what it is, where its source is, and its license texts in full.</summary>
public sealed class LicenseViewModel(OpenSourceComponent component) : ViewModelBase
{
    public OpenSourceComponent Component { get; } = component;

    public IReadOnlyList<string> Texts { get; } = component.Files.Select(Text).ToList();

    /// <summary>A bundled license text.</summary>
    public static string Text(string file)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Aster.Desktop/Licenses/{file}.txt"));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimStart('﻿').TrimEnd();
    }
}
