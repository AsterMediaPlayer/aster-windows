using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>Where a companion plugin stands, from what the server says.</summary>
public class ServerPluginsTests
{
    private static InstalledPlugin Installed(AsterPlugin plugin, string version, string? status) =>
        new() { Id = PluginState.PlainGuid(plugin.Guid), Name = plugin.PackageName, Version = version, Status = status };

    [Theory]
    [InlineData("Active", PluginStatus.Active, true)]
    [InlineData(null, PluginStatus.Active, true)]
    [InlineData("Restart", PluginStatus.AfterRestart, true)]
    [InlineData("Disabled", PluginStatus.Disabled, false)]
    [InlineData("Deleted", PluginStatus.AfterRestart, false)]
    [InlineData("Malfunctioned", PluginStatus.Broken, false)]
    public void AnInstalledPluginReadsAsItsStatus(string? status, PluginStatus expected, bool on)
    {
        var state = PluginState.From([Installed(AsterPlugin.Disc, "1.0.0.0", status)], null, "12.1.0", AsterPlugin.Disc);
        Assert.Equal(expected, state.Status);
        Assert.Equal(on, state.IsOn);
    }

    [Fact]
    public void AReplacedVersionIsPassedOverForItsSuccessor()
    {
        var state = PluginState.From(
            [Installed(AsterPlugin.OpEd, "0.3.0.0", "Superceded"), Installed(AsterPlugin.OpEd, "0.4.0.0", "Disabled")],
            null, "12.1.0", AsterPlugin.OpEd);
        Assert.Equal(PluginStatus.Disabled, state.Status);
    }

    [Fact]
    public void ANotInstalledPluginSaysWhenTheServerIsTooOld()
    {
        PluginPackage[] packages = [new() { Name = "Aster Channels", Guid = AsterPlugin.Channels.Guid, Versions = [new() { TargetAbi = "12.1.0.0" }] }];
        var old = PluginState.From([], packages, "12.0.4", AsterPlugin.Channels);
        Assert.Equal(PluginStatus.NeedsNewerServer, old.Status);
        Assert.Equal("Needs Jellyfin 12.1 or newer", old.Caption);
        Assert.Equal(PluginStatus.NotInstalled, PluginState.From([], packages, "12.1.0", AsterPlugin.Channels).Status);
        // Without a catalogue, the plugin's own minimum.
        Assert.Equal(PluginStatus.NeedsNewerServer, PluginState.From([], null, "10.11.0", AsterPlugin.Disc).Status);
    }

    [Fact]
    public void VersionsCompareByNumber()
    {
        Assert.True(PluginState.CompareVersions("12.1.0", "12.1.0.1") < 0);
        Assert.Equal(0, PluginState.CompareVersions("12.1", "12.1.0.0"));
        Assert.True(PluginState.CompareVersions("12.10", "12.9") > 0);
        Assert.Equal("12.1", PluginState.ShortVersion("12.1.0.0"));
        Assert.Equal("d459e0a2e0734e1eb0ec71883075cf4a", PluginState.PlainGuid(AsterPlugin.OpEd.Guid));
    }
}
