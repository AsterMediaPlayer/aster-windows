using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core;

/// <summary>
/// This computer's settings, kept in %LOCALAPPDATA%\Aster\settings.json.
/// The names and defaults are the Apple app's.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The streaming limit in bits per second; the top choice means "no limit".</summary>
    public int MaxBitrate { get; init; } = 120_000_000;

    /// <summary>Plays the next episode when the Up Next countdown ends.</summary>
    public bool AutoplayNext { get; init; } = true;

    /// <summary>Skips intros by themselves, once each.</summary>
    public bool AutoSkipIntro { get; init; }

    /// <summary>ASS and SSA subtitles drawn as styled; off, as plain text at the bottom.</summary>
    public bool StyledSubtitles { get; init; } = true;

    /// <summary>The song card as an anime episode's opening or ending plays, and on music videos.</summary>
    public bool ThemeCards { get; init; } = true;

    /// <summary>Openings and endings picked on a show's Music tab play in the Now Playing panel, one after another; off, full screen, one at a time.</summary>
    public bool ThemePanel { get; init; } = true;

    /// <summary>The version of each song a show's Music tab plays, where the OP/ED plugin has it (else the nearest it has).</summary>
    public Jellyfin.ThemeCut ThemeCut { get; init; }

    /// <summary>A title's theme song, played quietly on its page.</summary>
    public bool ThemeMusic { get; init; }

    /// <summary>A title's theme video, looped silently behind its page.</summary>
    public bool ThemeVideos { get; init; }

    /// <summary>An anime opening looping behind a show's page (with no theme video of its own) plays its sound, quietly.</summary>
    public bool OpeningSound { get; init; }

    /// <summary>Play on a title with a JellyDisc menu opens its menu, like putting in the disc.</summary>
    public bool PreferDiscMenu { get; init; }

    /// <summary>A disc menu's music and button sounds.</summary>
    public bool DiscMenuSounds { get; init; } = true;

    /// <summary>A video takes the whole screen as it starts (the Mac's setting).</summary>
    public bool FullScreenPlayback { get; init; } = true;

    /// <summary>
    /// The synced settings (by their server keys) chosen on this computer, so
    /// one never chosen keeps the default rather than overwriting another
    /// device's choice on the server. See <see cref="SettingsSync"/>.
    /// </summary>
    public IReadOnlyList<string> Chosen { get; init; } = [];

    /// <summary>
    /// The saved servers (<see cref="SavedServer.Id"/>) whose welcome screen,
    /// what Aster and its companion plugins do, was seen on this computer.
    /// </summary>
    public IReadOnlyList<string> WelcomeSeen { get; init; } = [];

    /// <summary>Below the top choice, a file over the limit is converted down to it.</summary>
    public bool LimitsQuality => MaxBitrate < 120_000_000;

    private static string filePath = Path.Combine(AppPaths.Data, "settings.json");
    private static readonly Lock Gate = new();
    private static AppSettings? current;

    public static AppSettings Current
    {
        get
        {
            lock (Gate) return current ??= Load();
        }
    }

    /// <summary>Keeps the settings in another file, for tests, which mustn't touch the real ones.</summary>
    internal static void UseFile(string path)
    {
        lock (Gate)
        {
            filePath = path;
            current = null;
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            current = settings;
            try { File.WriteAllText(filePath, JsonSerializer.Serialize(settings, JellyfinJson.Options)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), JellyfinJson.Options) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
}
