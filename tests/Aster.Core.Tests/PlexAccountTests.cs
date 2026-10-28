using System.Text.Json;
using Aster.Core.Jellyfin;
using Aster.Core.Plex;
using Aster.Core.Server;

namespace Aster.Core.Tests;

/// <summary>
/// plex.tv's replies (shapes from a real account, tokens and addresses
/// replaced): link codes, servers with their addresses ranked, and Home
/// users; then a Plex sign-in kept with its plex.tv side. A port of the Apple
/// app's PlexAccountTests and PlexSessionTests.
/// </summary>
public class PlexAccountTests
{
    private const string Resources = """
    [
      {"name":"Living Room","product":"Plex for Apple TV","provides":"client,player","clientIdentifier":"atv","connections":[]},
      {"name":"Friend's Server","product":"Plex Media Server","productVersion":"1.25.4.5487","provides":"server","clientIdentifier":"friend",
       "accessToken":"shared-token","owned":false,"home":false,
       "connections":[
         {"protocol":"https","address":"173.230.133.75","port":8443,"uri":"https://173-230-133-75.f.plex.direct:8443","local":false,"relay":true,"IPv6":false},
         {"protocol":"https","address":"66.78.154.177","port":13041,"uri":"https://66-78-154-177.f.plex.direct:13041","local":false,"relay":false,"IPv6":false},
         {"protocol":"https","address":"192.168.40.240","port":32400,"uri":"https://192-168-40-240.f.plex.direct:32400","local":true,"relay":false,"IPv6":false}
       ]},
      {"name":"Test Server","product":"Plex Media Server","productVersion":"1.43.5.11029","provides":"server","clientIdentifier":"4d09e81d",
       "accessToken":"own-token","owned":true,"home":false,
       "connections":[
         {"protocol":"https","address":"192.168.1.240","port":32400,"uri":"https://192-168-1-240.o.plex.direct:32400","local":true,"relay":false,"IPv6":false},
         {"protocol":"https","address":"172.18.0.1","port":32400,"uri":"https://172-18-0-1.o.plex.direct:32400","local":true,"relay":false,"IPv6":false}
       ]}
    ]
    """;

    private static List<PlexResource> All() => JsonSerializer.Deserialize<List<PlexResource>>(Resources, PlexJson.Options)!;

    [Fact]
    public void OnlyServersAreListedOwnFirst()
    {
        var servers = PlexAccount.OwnFirst(All());
        Assert.Equal(["Test Server", "Friend's Server"], servers.Select(s => s.Name));
        Assert.Equal("own-token", servers[0].AccessToken);
        Assert.Equal("shared-token", servers[1].AccessToken);
    }

    [Fact]
    public void AddressesRankHomeThenInternetThenRelay()
    {
        var friend = All()[1];
        Assert.Equal(
        [
            "https://192-168-40-240.f.plex.direct:32400/",
            // The same home address, plainly, in case the secure name's lookup fails.
            "http://192.168.40.240:32400/",
            "https://66-78-154-177.f.plex.direct:13041/",
            "https://173-230-133-75.f.plex.direct:8443/",
        ], friend.RankedConnections.Select(c => c.Uri.AbsoluteUri));
    }

    [Fact]
    public void HomeUsersAndTheirPins()
    {
        var users = PlexAccount.HomeUsers("""
        {"id":1,"name":"Home","guestUserID":2,"guestEnabled":false,"users":[
          {"id":10,"uuid":"a1","title":"peppy6582","username":"peppy6582","thumb":"https://plex.tv/users/a1/avatar","admin":true,"guest":false,"restricted":false,"protected":true,"hasPassword":true,"pin":"x"},
          {"id":11,"uuid":"b2","title":"Lana","username":"","thumb":"https://plex.tv/users/b2/avatar","admin":false,"guest":false,"restricted":true,"protected":false,"hasPassword":false}
        ]}
        """);
        Assert.Equal(["peppy6582", "Lana"], users.Select(u => u.Title));
        Assert.True(users[0].Protected);
        Assert.True(users[1].Restricted);
    }

    [Fact]
    public void ALinkCodeBeforeAndAfter()
    {
        var waiting = JsonSerializer.Deserialize<PlexPin>("""{"id":123,"code":"ABCD","clientIdentifier":"c","authToken":null,"expiresIn":1800}""", PlexJson.Options)!;
        Assert.Equal("ABCD", waiting.Code);
        Assert.Null(waiting.AuthToken);
        var linked = JsonSerializer.Deserialize<PlexPin>("""{"id":123,"code":"ABCD","authToken":"tok","expiresIn":1700}""", PlexJson.Options)!;
        Assert.Equal("tok", linked.AuthToken);
    }

    [Fact]
    public void PicturesGoThroughThePhotoTranscoder()
    {
        var client = new PlexClient(new Uri("https://192-168-1-240.o.plex.direct:32400"), "own", "u");
        var url = client.ImageUrl("6139", ImageKind.Primary, "/library/metadata/6139/thumb/1792890348", maxWidth: 500);
        Assert.Equal("https://192-168-1-240.o.plex.direct:32400/photo/:/transcode?X-Plex-Token=own&height=1500&minSize=0&quality=90&upscale=1&url=%2Flibrary%2Fmetadata%2F6139%2Fthumb%2F1792890348&width=500", url.AbsoluteUri);
        // A logo keeps its transparency.
        Assert.Contains("format=png", client.ImageUrl("6137", ImageKind.Logo, "/library/metadata/6137/clearLogo/1", maxWidth: 900).Query);
    }

    // ---- The sign-in, kept ----

    private static readonly PlexLink Link = new()
    {
        AccountToken = "account",
        MachineId = "4d09e81d",
        Connections = [new PlexConnectionInfo(new Uri("https://192-168-1-240.abc.plex.direct:32400"), Local: true, Relay: false)],
    };

    private static StoredSession PlexSession(string address) => new()
    {
        ServerUrl = new Uri(address),
        ServerName = "Test Server",
        UserId = "home-user",
        UserName = "peppy",
        AccessToken = "server",
        ServerKind = ServerKind.Plex,
        Plex = Link,
    };

    [Fact]
    public void APlexSignInRoundTrips()
    {
        var stored = PlexSession("https://192-168-1-240.abc.plex.direct:32400");
        var decoded = JsonSerializer.Deserialize<StoredSession>(JsonSerializer.Serialize(stored, JellyfinJson.Options), JellyfinJson.Options)!;
        Assert.Equal(stored with { Plex = null }, decoded with { Plex = null });
        Assert.Equal(Link.MachineId, decoded.Plex?.MachineId);
        Assert.Equal(Link.Connections, decoded.Plex?.Connections);
        Assert.Equal(ServerKind.Plex, decoded.Kind);
        Assert.Equal(ServerKind.Plex, MediaServers.For(decoded).Kind);
    }

    /// <summary>The address used to reach a Plex server can change; its id can't.</summary>
    [Fact]
    public void APlexServerIsKnownByItsId() =>
        Assert.Equal("plex:4d09e81d|home-user", new SavedServer { Session = PlexSession("https://10-10-14-1.abc.plex.direct:32400") }.Id);

    [Fact]
    public void JellyfinAndEmbySignInsAreUnchanged()
    {
        var stored = JsonSerializer.Deserialize<StoredSession>("""{"serverUrl":"http://192.168.1.240:8096","userId":"u","userName":"peppy","accessToken":"t","serverKind":"Emby"}""", JellyfinJson.Options)!;
        Assert.Null(stored.Plex);
        Assert.Equal(ServerKind.Emby, stored.Kind);
        Assert.Equal("http://192.168.1.240:8096/|u", new SavedServer { Session = stored }.Id);
        Assert.IsType<JellyfinClient>(MediaServers.For(stored));
    }
}
