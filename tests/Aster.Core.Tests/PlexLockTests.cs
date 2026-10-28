using System.Net;
using System.Net.Sockets;
using Aster.Core.Jellyfin;
using Aster.Core.Plex;

namespace Aster.Core.Tests;

/// <summary>
/// A Plex server's lock (the Plex Home profile's PIN, checked by plex.tv)
/// and finding it again when its saved address stops answering. As the
/// Apple app's PlexSessionTests.
/// </summary>
public class PlexLockTests
{
    private static StoredSession PlexSession(PlexLink link, string address = "http://192.168.1.240:32400") => new()
    {
        ServerUrl = new Uri(address),
        UserId = "1",
        UserName = "peppy",
        AccessToken = "server-token",
        ServerKind = ServerKind.Plex,
        Plex = link,
    };

    private static readonly PlexLink WithPin = new() { AccountToken = "a", MachineId = "m", HomeUserUuid = "uuid", HomeUserHasPin = true, HomeToken = "home" };

    [Fact]
    public void OnlyAProfileWithAPinLocks()
    {
        Assert.True(new SavedServer { Session = PlexSession(WithPin) }.CanBeLocked);
        Assert.False(new SavedServer { Session = PlexSession(WithPin with { HomeUserHasPin = false }) }.CanBeLocked);
        // Signed in before the Home's token was kept: nothing can check the PIN.
        Assert.False(new SavedServer { Session = PlexSession(WithPin with { HomeToken = null }) }.CanBeLocked);
        Assert.Equal("PIN", new SavedServer { Session = PlexSession(WithPin) }.CredentialName);
    }

    [Fact]
    public void AProfileWithoutAPinNeverLocksAfter()
    {
        var left = DateTimeOffset.UtcNow.AddHours(-1);
        var locked = new SavedServer { Session = PlexSession(WithPin), LockAfterMinutes = 5 };
        Assert.True(locked.LocksAfter(left, DateTimeOffset.UtcNow));
        Assert.False((locked with { Session = PlexSession(WithPin with { HomeUserHasPin = false }) }).LocksAfter(left, DateTimeOffset.UtcNow));
    }

    /// <summary>plex.tv lets a profile switch to itself without a PIN, so an empty one is never sent.</summary>
    [Fact]
    public async Task AnEmptyPinIsNeverSent()
    {
        var error = await Assert.ThrowsAsync<JellyfinException>(() => new SavedServer { Session = PlexSession(WithPin) }.SignInAgainAsync(""));
        Assert.Equal(PlexErrors.PinNeeded, error.Message);
    }

    private static int FreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    /// <summary>A stand-in Plex server that answers <c>/identity</c>.</summary>
    private static HttpListener Answering(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
                catch (Exception) { return; }
            }
        });
        return listener;
    }

    [Fact]
    public async Task AnotherSavedAddressTakesOver()
    {
        var answering = FreePort();
        using var server = Answering(answering);
        var gone = new Uri($"http://127.0.0.1:{FreePort()}/");
        var there = new Uri($"http://127.0.0.1:{answering}/");
        var stored = PlexSession(WithPin with { Connections = [new(gone, true, false), new(there, true, false)] }, gone.AbsoluteUri);

        var found = await PlexReconnect.FindAgainAsync(stored);
        Assert.NotNull(found);
        Assert.Equal(there, found.ServerUrl);
        Assert.Equal(SavedServer.IdOf(stored), SavedServer.IdOf(found));
    }

    [Fact]
    public async Task AnAnsweringAddressIsKept()
    {
        var answering = FreePort();
        using var server = Answering(answering);
        var stored = PlexSession(WithPin, $"http://127.0.0.1:{answering}/");
        Assert.Null(await PlexReconnect.FindAgainAsync(stored));
    }
}
