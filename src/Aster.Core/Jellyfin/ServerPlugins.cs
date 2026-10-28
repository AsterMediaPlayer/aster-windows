// Aster's companion plugins for Jellyfin: which are on the server, and, for
// an administrator, installing them and turning them on or off. Installing
// adds Aster's repository to the server's list (keeping the others) and asks
// the server for the newest version that runs on it; any change takes a
// server restart, which the app can ask for. The Apple app's ServerPlugins.

namespace Aster.Core.Jellyfin;

/// <summary>A companion plugin with features in the app.</summary>
public enum AsterPlugin
{
    Channels,
    Disc,
    OpEd,
}

public static class AsterPlugins
{
    /// <summary>The repository the plugins are published to.</summary>
    public const string RepositoryUrl = "https://raw.githubusercontent.com/AsterMediaPlayer/aster-companion-plugins/main/manifest.json";
    public const string RepositoryName = "Aster Companion Plugins";

    public static IReadOnlyList<AsterPlugin> All { get; } = Enum.GetValues<AsterPlugin>();

    extension(AsterPlugin plugin)
    {
        public string Guid => plugin switch
        {
            AsterPlugin.Channels => "5b489227-2fc4-4dce-b7f8-6d0e3b2082ee",
            AsterPlugin.Disc => "913b9d44-cafe-4fe7-ae0b-f1362dc12cc7",
            _ => "d459e0a2-e073-4e1e-b0ec-71883075cf4a",
        };

        /// <summary>Its name in the repository, which installing asks for.</summary>
        public string PackageName => plugin switch
        {
            AsterPlugin.Channels => "Aster Channels",
            AsterPlugin.Disc => "Aster Disc",
            _ => "Aster OP-ED",
        };

        /// <summary>What it gets you, in a few words.</summary>
        public string Headline => plugin switch
        {
            AsterPlugin.Channels => "Live TV, sorted",
            AsterPlugin.Disc => "Real disc menus",
            _ => "Every anime opening and ending",
        };

        public string Summary => plugin switch
        {
            AsterPlugin.Channels => "Group your Live TV channels into sports, news, kids and more, in the guide and as you flip. Then rename, renumber and rearrange Aster's channels right from Jellyfin's dashboard.",
            AsterPlugin.Disc => "Explore a film's extras through a menu just like the one on the disc, with moving backgrounds and music. Pick from a community catalogue, or design your own.",
            _ => "Watch a show's openings and endings on their own, play them back to back, or have each series greet you with its theme song. The song and artist come along too.",
        };

        /// <summary>The oldest server it runs on, until the repository says.</summary>
        public string MinimumServer => plugin == AsterPlugin.Channels ? "12.1" : "12.0";

        /// <summary>
        /// A route that answers whenever the plugin is running, for anyone
        /// signed in: how a user who isn't an administrator, and can't list
        /// plugins, can tell it's there.
        /// </summary>
        public string ProbePath => plugin switch
        {
            AsterPlugin.Channels => "/AsterChannels/web/channels.js",
            AsterPlugin.Disc => "/DiscMenus/web/discmenus.js",
            _ => "/OpEd/Library",
        };
    }
}

/// <summary>Where a plugin stands on the server.</summary>
public enum PluginStatus
{
    /// <summary>Not known yet, or the server didn't say.</summary>
    Unknown,
    NotInstalled,

    /// <summary>Being downloaded onto the server.</summary>
    Installing,

    /// <summary>Changed: on (<see cref="PluginState.On"/>) or off after the server restarts.</summary>
    AfterRestart,
    Active,
    Disabled,

    /// <summary>No version of it runs on this server: it needs <see cref="PluginState.NeedsVersion"/> or later.</summary>
    NeedsNewerServer,

    /// <summary>Jellyfin couldn't load it.</summary>
    Broken,

    /// <summary>For someone who isn't an administrator: whether it's running (<see cref="PluginState.On"/>).</summary>
    Present,
}

public sealed record PluginState(PluginStatus Status, bool On = false, string? NeedsVersion = null)
{
    public static PluginState Unknown { get; } = new(PluginStatus.Unknown);

    public bool IsOn => Status switch
    {
        PluginStatus.Active or PluginStatus.Installing => true,
        PluginStatus.AfterRestart or PluginStatus.Present => On,
        _ => false,
    };

    /// <summary>Whether an administrator's switch can change it now.</summary>
    public bool CanSwitch => Status is PluginStatus.NotInstalled or PluginStatus.Active or PluginStatus.Disabled or PluginStatus.AfterRestart;

    public string Caption => Status switch
    {
        PluginStatus.NotInstalled => "Not installed yet",
        PluginStatus.Installing => "Installing…",
        PluginStatus.AfterRestart => On ? "Ready after a quick server restart" : "Turns off after a server restart",
        PluginStatus.Active => "On",
        PluginStatus.Disabled => "Off",
        PluginStatus.NeedsNewerServer => $"Needs Jellyfin {NeedsVersion} or newer",
        PluginStatus.Broken => "Jellyfin couldn't start it. Check Plugins in your dashboard.",
        PluginStatus.Present => On ? "On your server" : "Not on your server yet",
        _ => "",
    };

    /// <summary>Whether the caption is good news: on, or running.</summary>
    public bool IsGood => Status == PluginStatus.Active || (Status == PluginStatus.Present && On);

    /// <summary>What the server's plugin list, its catalogue and its version say.</summary>
    public static PluginState From(IReadOnlyList<InstalledPlugin> installed, IReadOnlyList<PluginPackage>? packages, string? serverVersion, AsterPlugin plugin)
    {
        if (Current(installed, plugin) is { } current)
        {
            return current.Status switch
            {
                "Active" or null => new(PluginStatus.Active),
                "Restart" => new(PluginStatus.AfterRestart, On: true),
                "Disabled" => new(PluginStatus.Disabled),
                "Deleted" => new(PluginStatus.AfterRestart, On: false),
                _ => new(PluginStatus.Broken),
            };
        }
        // Not installed: can it be, on this server?
        if (serverVersion is null) return new(PluginStatus.NotInstalled);
        var guid = PlainGuid(plugin.Guid);
        var versions = packages?.FirstOrDefault(p => PlainGuid(p.Guid) == guid)?.Versions ?? [];
        var oldestNeeded = versions.Select(v => v.TargetAbi).OfType<string>().Order(Comparer<string>.Create(CompareVersions)).FirstOrDefault() ?? plugin.MinimumServer;
        return CompareVersions(serverVersion, oldestNeeded) < 0
            ? new(PluginStatus.NeedsNewerServer, NeedsVersion: ShortVersion(oldestNeeded))
            : new(PluginStatus.NotInstalled);
    }

    /// <summary>The plugin's newest installed version; a replaced one stays listed as superseded beside its successor.</summary>
    public static InstalledPlugin? Current(IReadOnlyList<InstalledPlugin> installed, AsterPlugin plugin)
    {
        var guid = PlainGuid(plugin.Guid);
        return installed
            .Where(p => PlainGuid(p.Id) == guid && p.Status is not ("Superceded" or "Superseded"))
            .Order(Comparer<InstalledPlugin>.Create((a, b) => CompareVersions(a.Version, b.Version)))
            .LastOrDefault();
    }

    /// <summary>Jellyfin writes GUIDs without dashes; the repository with them.</summary>
    public static string PlainGuid(string guid) => guid.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>"12.1.0" before "12.1.0.1": by number, missing parts as 0.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var left = Parts(a);
        var right = Parts(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }

    /// <summary>"12.1.0.0" as "12.1".</summary>
    public static string ShortVersion(string version)
    {
        var parts = version.Split('.').ToList();
        while (parts.Count > 2 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }
}

/// <summary>A plugin on the server, from <c>/Plugins</c> (administrators only).</summary>
public sealed record InstalledPlugin
{
    /// <summary>The plugin's GUID, which Jellyfin writes without dashes.</summary>
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required string Version { get; init; }

    /// <summary>"Active", "Restart", "Disabled", "NotSupported", "Malfunctioned", "Superceded" or "Deleted".</summary>
    public string? Status { get; init; }
}

/// <summary>A plugin repository in the server's settings, from <c>/Repositories</c>.</summary>
public sealed record PluginRepository
{
    public string? Name { get; init; }
    public string? Url { get; init; }
    public bool? Enabled { get; init; }
}

/// <summary>A plugin in the catalogue of the server's repositories, from <c>/Packages</c>.</summary>
public sealed record PluginPackage
{
    public required string Name { get; init; }
    public required string Guid { get; init; }
    public IReadOnlyList<PluginVersion> Versions { get; init; } = [];
}

public sealed record PluginVersion
{
    public string? Version { get; init; }

    /// <summary>The oldest server it runs on, as "12.1.0.0".</summary>
    public string? TargetAbi { get; init; }
    public string? RepositoryUrl { get; init; }
}

/// <summary>The parts of the signed-in <c>/System/Info</c> the plugin settings use.</summary>
public sealed record ServerSystemInfo
{
    public string? Version { get; init; }
    public bool? HasPendingRestart { get; init; }

    /// <summary>False where something else (Docker, a service manager) must restart it.</summary>
    public bool? CanSelfRestart { get; init; }
}
