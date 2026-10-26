using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>The servers signed in to: one entry per server and user, kept up to date, forgotten on request.</summary>
public class SavedServerTests
{
    private static StoredSession Session(string url, string user, string token = "t", ServerKind kind = ServerKind.Jellyfin) =>
        new() { ServerUrl = new Uri(url), ServerName = "Server", UserId = user, UserName = user, AccessToken = token, ServerKind = kind };

    [Fact]
    public void ASignInIsAddedOnceAndKeptUpToDate()
    {
        var jellyfin = Session("http://192.168.1.240:8096/", "u1");
        var emby = Session("http://192.168.1.240:8098/", "u9", kind: ServerKind.Emby);
        var servers = SavedServers.Remember([], jellyfin);
        servers = SavedServers.Remember(servers, emby);
        Assert.Equal(2, servers.Count);

        // Signing in to the same server as the same user again replaces its entry, in place.
        servers = SavedServers.Remember(servers, jellyfin with { AccessToken = "fresh" });
        Assert.Equal(["http://192.168.1.240:8096/|u1", "http://192.168.1.240:8098/|u9"], servers.Select(s => s.Id));
        Assert.Equal("fresh", servers[0].Session.AccessToken);
    }

    [Fact]
    public void AnotherUserOnTheSameServerIsAnotherEntry()
    {
        var servers = SavedServers.Remember(SavedServers.Remember([], Session("http://s/", "a")), Session("http://s/", "b"));
        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public void ForgettingLeavesTheOthers()
    {
        var servers = SavedServers.Remember(SavedServers.Remember([], Session("http://a/", "u")), Session("http://b/", "u"));
        Assert.Equal(["http://b/|u"], SavedServers.Forget(servers, "http://a/|u").Select(s => s.Id));
    }

    [Fact]
    public void SigningInAgainKeepsTheServersLockSettings()
    {
        var servers = SavedServers.Remember([], Session("http://s/", "u"));
        servers = SavedServers.Change(servers, "http://s/|u", s => s with { AsksPasswordToSwitch = true, LockAfterMinutes = 15 });
        servers = SavedServers.Remember(servers, Session("http://s/", "u", token: "fresh"));
        Assert.True(servers[0].AsksPasswordToSwitch);
        Assert.Equal(15, servers[0].LockAfterMinutes);
        Assert.Equal("fresh", servers[0].Session.AccessToken);
    }

    [Fact]
    public void ItLocksOnlyOnceAwayLongEnough()
    {
        var left = new DateTimeOffset(2026, 10, 25, 20, 0, 0, TimeSpan.Zero);
        var server = new SavedServer { Session = Session("http://s/", "u"), LockAfterMinutes = 15 };
        Assert.False(server.LocksAfter(left, left.AddMinutes(14)));
        Assert.True(server.LocksAfter(left, left.AddMinutes(15)));
        // Never left, or never set to lock.
        Assert.False(server.LocksAfter(null, left.AddHours(5)));
        Assert.False((server with { LockAfterMinutes = null }).LocksAfter(left, left.AddHours(5)));
    }

    [Theory]
    [InlineData(null, "Never")]
    [InlineData(5, "5 Minutes")]
    [InlineData(30, "30 Minutes")]
    [InlineData(60, "1 Hour")]
    [InlineData(240, "4 Hours")]
    public void LockChoicesReadAsTheAppleApps(int? minutes, string title) =>
        Assert.Equal(title, SavedServer.LockTitle(minutes));

    [Fact]
    public void TheListKeepsEachServersKind()
    {
        IReadOnlyList<SavedServer> servers = [new SavedServer { Session = Session("http://e/", "u", kind: ServerKind.Emby) }];
        var json = JsonSerializer.Serialize(servers, JellyfinJson.Options);
        Assert.DoesNotContain("\"Id\"", json);
        var read = JsonSerializer.Deserialize<List<SavedServer>>(json, JellyfinJson.Options)!;
        Assert.Equal(ServerKind.Emby, read[0].Session.Kind);
        Assert.Equal("Server", read[0].Name);
    }
}
