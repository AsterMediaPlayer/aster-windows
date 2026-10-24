using System.Net;
using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>
/// Jellyfin and Emby told apart by their public info, sessions saved before
/// Emby still read as Jellyfin, Emby's per-user routes, its numeric ids, and
/// its chapter marks as skip segments. The Apple app's ServerKindTests, and
/// the routes this port calls.
/// </summary>
public class ServerKindTests
{
    private static PublicSystemInfo Info(string json) => JsonSerializer.Deserialize<PublicSystemInfo>(json, JellyfinJson.Options)!;

    /// <summary>The two servers' actual replies (Jellyfin 12.1, Emby 4.11).</summary>
    [Fact]
    public void EachServerIsKnownByItsPublicInfo()
    {
        var jellyfin = Info("""{"LocalAddress":"192.168.1.240","ServerName":"Server","Version":"12.1.0","ProductName":"Jellyfin Server","Id":"c76e"}""");
        var emby = Info("""{"LocalAddresses":[],"RemoteAddresses":[],"ServerName":"Server","Version":"4.11.0.3","Id":"8282"}""");
        Assert.Equal(ServerKind.Jellyfin, jellyfin.Kind);
        Assert.Equal(ServerKind.Emby, emby.Kind);
        Assert.Equal("4.11.0.3", emby.Version);
    }

    [Fact]
    public void ASessionSavedBeforeEmbyIsJellyfin()
    {
        var old = JsonSerializer.Deserialize<StoredSession>("""{"ServerUrl":"http://192.168.1.240:8096","UserId":"u","UserName":"peppy","AccessToken":"t"}""", JellyfinJson.Options)!;
        Assert.Equal(ServerKind.Jellyfin, old.Kind);

        var emby = new StoredSession { ServerUrl = new Uri("http://192.168.1.240:8098"), UserId = "u", UserName = "peppy", AccessToken = "t", ServerKind = ServerKind.Emby };
        var json = JsonSerializer.Serialize(emby, JellyfinJson.Options);
        Assert.Contains("\"ServerKind\":\"Emby\"", json);
        Assert.DoesNotContain("\"Kind\"", json);
        Assert.Equal(ServerKind.Emby, JsonSerializer.Deserialize<StoredSession>(json, JellyfinJson.Options)!.Kind);
    }

    [Fact]
    public void EmbysChapterMarksBecomeIntroAndCredits()
    {
        ChapterInfo[] chapters =
        [
            new() { StartPositionTicks = 0, Name = "Chapter 1", MarkerType = "Chapter" },
            new() { StartPositionTicks = 900_000_000, Name = "Intro", MarkerType = "IntroStart" },
            new() { StartPositionTicks = 1_800_000_000, Name = "Intro End", MarkerType = "IntroEnd" },
            new() { StartPositionTicks = 12_000_000_000, Name = "Credits", MarkerType = "CreditsStart" },
        ];
        var segments = JellyfinClient.SegmentsFromChapters(chapters, "739", 14_000_000_000);
        Assert.Equal(["Intro", "Outro"], segments.Select(s => s.Type));
        Assert.Equal(900_000_000, segments[0].StartTicks);
        Assert.Equal(1_800_000_000, segments[0].EndTicks);
        Assert.Equal(14_000_000_000, segments[1].EndTicks);
    }

    [Fact]
    public void PlainChaptersGiveNothingToSkip()
    {
        ChapterInfo[] chapters =
        [
            new() { StartPositionTicks = 0, Name = "Chapter 1", MarkerType = "Chapter" },
            new() { StartPositionTicks = 6_000_000_000, Name = "Chapter 2", MarkerType = "Chapter" },
        ];
        Assert.Empty(JellyfinClient.SegmentsFromChapters(chapters, "739", 14_000_000_000));
    }

    [Fact]
    public void EmbysNumericIdsReadAsText()
    {
        var item = JsonSerializer.Deserialize<BaseItem>("""
            {"Id":"739","Name":"Show","Type":"Series",
             "Studios":[{"Name":"Studio","Id":1234}],
             "People":[{"Name":"Actor","Id":56,"Type":"Actor"},{"Name":"Other","Id":"a1b2"}]}
            """, JellyfinJson.Options)!;
        Assert.Equal("1234", item.Studios![0].Id);
        Assert.Equal(["56", "a1b2"], item.People!.Select(p => p.Id));
    }

    [Fact]
    public async Task EmbyIsAskedByItsPerUserRoutes()
    {
        var server = new StubServer(request => (HttpStatusCode.OK, request.Url.AbsolutePath.EndsWith("/Views") || request.Url.AbsolutePath.EndsWith("/Resume") ? """{"Items":[]}""" : request.Url.AbsolutePath.EndsWith("/Latest") || request.Url.AbsolutePath.EndsWith("/SpecialFeatures") ? "[]" : """{"Id":"i","Name":"x","Type":"Movie"}"""));
        var emby = server.Client(kind: ServerKind.Emby);
        await emby.UserViewsAsync();
        await emby.ItemAsync("i");
        await emby.LatestAsync(null);
        await emby.ResumeAsync();
        await emby.SpecialFeaturesAsync("i");
        await emby.SetFavoriteAsync("i", true);
        await emby.SetPlayedAsync("i", false);
        await emby.SaveResumePositionAsync("i", 10);
        Assert.Equal(
            ["GET /base/Users/user/Views", "GET /base/Users/user/Items/i", "GET /base/Users/user/Items/Latest", "GET /base/Users/user/Items/Resume",
             "GET /base/Users/user/Items/i/SpecialFeatures", "POST /base/Users/user/FavoriteItems/i", "DELETE /base/Users/user/PlayedItems/i",
             "POST /base/Users/user/Items/i/UserData"],
            server.Requests.Select(r => $"{r.Method} {r.Url.AbsolutePath}"));
    }

    [Fact]
    public async Task JellyfinsPluginsAnswerNothingOnEmby()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, "{}"));
        var emby = server.Client(kind: ServerKind.Emby);
        Assert.False(await emby.QuickConnectEnabledAsync());
        Assert.Null(await emby.OpEdThemesAsync("i"));
        Assert.Null(await emby.OpEdLibrariesAsync());
        Assert.Null(await emby.LiveTvGroupsAsync());
        Assert.Null(await emby.DiscMenuAsync("i"));
        Assert.Empty(server.Requests);
    }
}
