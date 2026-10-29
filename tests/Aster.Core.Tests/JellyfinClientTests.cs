using System.Net;
using System.Text.Json;
using Aster.Core.Jellyfin;
using Aster.Core.Server;

namespace Aster.Core.Tests;

public class JellyfinClientTests
{
    private static readonly JellyfinClient Client = new(new Uri("https://jellyfin.example/base/"), "token", "user");

    [Fact]
    public void UrlJoinsTheServerPathAndSortsTheQuery()
    {
        var url = Client.Url("Items", new() { ["zeta"] = "1", ["alpha"] = "two words", ["skipped"] = null });
        Assert.Equal("https://jellyfin.example/base/Items?alpha=two%20words&zeta=1", url.AbsoluteUri);
    }

    [Fact]
    public void UrlEscapesSeparatorsInValues()
    {
        var url = Client.Url("/Items", new() { ["officialRatings"] = "PG|PG-13", ["searchTerm"] = "Tom & Jerry+" });
        Assert.Equal("https://jellyfin.example/base/Items?officialRatings=PG%7CPG-13&searchTerm=Tom%20%26%20Jerry%2B", url.AbsoluteUri);
    }

    [Fact]
    public void TokenUrlCarriesBothKeyNames()
    {
        var url = Client.TokenUrl("/Videos/abc/stream", new() { ["static"] = "true" });
        Assert.Equal("https://jellyfin.example/base/Videos/abc/stream?ApiKey=token&api_key=token&static=true", url.AbsoluteUri);
    }

    [Fact]
    public void AbsoluteUrlKeepsTheServerRelativeQuery()
    {
        var url = Client.AbsoluteUrl("/videos/abc/master.m3u8?MediaSourceId=1&api_key=x");
        Assert.Equal("https://jellyfin.example/base/videos/abc/master.m3u8?MediaSourceId=1&api_key=x", url!.AbsoluteUri);
    }

    [Fact]
    public void DiscAssetUrlEscapesEachSegment()
    {
        var url = Client.DiscAssetUrl(["themes", "My Theme", "a#b.png"]);
        Assert.Equal("https://jellyfin.example/base/DiscMenus/Assets/themes/My%20Theme/a%23b.png", url.AbsoluteUri);
    }

    [Fact]
    public void OnlyTheServersOwnAddressesAreOnTheServer()
    {
        Assert.True(Client.IsOnServer(new Uri("https://jellyfin.example/base/Items/1/Images/Primary")));
        Assert.True(Client.IsOnServer(Client.DiscAssetUrl(["menu", "a.png"])));
        Assert.False(Client.IsOnServer(new Uri("https://jellyfin.example/basement/a.png")));
        Assert.False(Client.IsOnServer(new Uri("https://jellyfin.example/other/a.png")));
        Assert.False(Client.IsOnServer(new Uri("http://jellyfin.example/base/a.png")));
        Assert.False(Client.IsOnServer(new Uri("https://jellyfin.example.evil.com/base/a.png")));
        Assert.False(Client.IsOnServer(new Uri("https://image.tmdb.org/t/p/w1280/a.jpg")));
    }

    [Fact]
    public void OnlyRequestsToTheServerCarryTheToken()
    {
        using var own = Client.AuthorizedRequest(new Uri("https://jellyfin.example/base/Videos/1/master.m3u8"));
        Assert.True(own.Headers.Contains("Authorization"));
        Assert.True(own.Headers.Contains("X-Emby-Authorization"));
        using var other = Client.AuthorizedRequest(new Uri("https://image.tmdb.org/t/p/w1280/a.jpg"));
        Assert.False(other.Headers.Contains("Authorization"));
        Assert.False(other.Headers.Contains("X-Emby-Authorization"));
    }

    [Fact]
    public void AuthorizationHeaderNamesTheAppAndToken()
    {
        var header = Client.AuthorizationHeader;
        Assert.StartsWith("MediaBrowser Client=\"Aster\", Device=\"", header);
        Assert.Contains($"DeviceId=\"{JellyfinDevice.DeviceId}\"", header);
        Assert.EndsWith(", Token=\"token\"", header);
        Assert.DoesNotContain("Token=", new JellyfinClient(new Uri("https://x/")).AuthorizationHeader);
    }

    [Fact]
    public void LandscapeUrlPrefersAnEpisodesOwnPicture()
    {
        var episode = new BaseItem
        {
            Id = "ep",
            Type = "Episode",
            ImageTags = new Dictionary<string, string> { ["Primary"] = "p1" },
            ParentBackdropItemId = "series",
            ParentBackdropImageTags = ["b1"],
        };
        Assert.Equal("https://jellyfin.example/base/Items/ep/Images/Primary?maxWidth=800&quality=90&tag=p1", Client.LandscapeUrl(episode)!.AbsoluteUri);
        Assert.Equal("https://jellyfin.example/base/Items/series/Images/Backdrop/0?maxWidth=1920&quality=90&tag=b1", Client.BackdropUrl(episode)!.AbsoluteUri);
    }

    [Fact]
    public async Task AuthenticateSendsCredentialsAndReadsTheSession()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, """{"User":{"Id":"u1","Name":"Phil"},"AccessToken":"t1","ServerId":"s"}"""));
        var result = await server.Client(token: null, userId: null).AuthenticateAsync("Phil", "secret");

        Assert.Equal("t1", result.AccessToken);
        Assert.Equal("Phil", result.User.Name);
        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/base/Users/AuthenticateByName", request.Url.AbsolutePath);
        Assert.Equal("""{"Username":"Phil","Pw":"secret"}""", request.Body);
    }

    /// <summary>
    /// A password check before loosening a server's lock signs in as another
    /// device, which Jellyfin doesn't end this one's sign-in for, and straight
    /// out again with the token it got.
    /// </summary>
    [Fact]
    public async Task CheckingAPasswordLeavesThisComputersSignIn()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, """{"User":{"Id":"u1","Name":"Phil"},"AccessToken":"check","ServerId":"s"}"""));
        var client = server.Client(token: "in-use", userId: "u1");
        await client.CheckPasswordAsync("Phil", "secret");

        Assert.Equal(["/base/Users/AuthenticateByName", "/base/Sessions/Logout"], server.Requests.Select(r => r.Url.AbsolutePath));
        Assert.All(server.Requests, r => Assert.DoesNotContain($"DeviceId=\"{JellyfinDevice.DeviceId}\"", r.Authorization));
        Assert.DoesNotContain("in-use", server.Requests[0].Authorization);
        Assert.Contains("Token=\"check\"", server.Requests[1].Authorization);
        Assert.Equal("in-use", client.AccessToken);
    }

    [Fact]
    public async Task AWrongPasswordFailsTheCheck()
    {
        var server = new StubServer(_ => (HttpStatusCode.Unauthorized, ""));
        await Assert.ThrowsAsync<JellyfinException>(() => server.Client().CheckPasswordAsync("Phil", "wrong"));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task QuickConnectAsksForACodeChecksItAndSignsInWithItsSecret()
    {
        var server = new StubServer(request => request.Url.AbsolutePath switch
        {
            "/base/QuickConnect/Enabled" => (HttpStatusCode.OK, "true"),
            "/base/QuickConnect/Initiate" => (HttpStatusCode.OK, """{"Authenticated":false,"Secret":"s3cret","Code":"123456"}"""),
            "/base/QuickConnect/Connect" => (HttpStatusCode.OK, """{"Authenticated":true,"Secret":"s3cret","Code":"123456"}"""),
            _ => (HttpStatusCode.OK, """{"User":{"Id":"u1","Name":"Phil"},"AccessToken":"t1","ServerId":"s"}"""),
        });
        var client = server.Client(token: null, userId: null);

        Assert.True(await client.QuickConnectEnabledAsync());
        var initiated = await client.QuickConnectInitiateAsync();
        Assert.Equal("123456", initiated.Code);
        Assert.True((await client.QuickConnectStateAsync(initiated.Secret)).Authenticated);
        var result = await client.AuthenticateWithQuickConnectAsync(initiated.Secret);

        Assert.Equal("t1", result.AccessToken);
        Assert.Equal(HttpMethod.Post, server.Requests[1].Method);
        Assert.Equal("?secret=s3cret", server.Requests[2].Url.Query);
        Assert.Equal("/base/Users/AuthenticateWithQuickConnect", server.Requests[3].Url.AbsolutePath);
        Assert.Equal("""{"Secret":"s3cret"}""", server.Requests[3].Body);
    }

    [Fact]
    public async Task UnauthorizedReadsAsAWrongPassword()
    {
        var server = new StubServer(_ => (HttpStatusCode.Unauthorized, ""));
        var error = await Assert.ThrowsAsync<JellyfinException>(() => server.Client().AuthenticateAsync("a", "b"));
        Assert.Equal(JellyfinErrorKind.Http, error.Kind);
        Assert.Equal(401, error.StatusCode);
        Assert.Equal("Incorrect user name or password.", error.Message);
    }

    [Fact]
    public async Task RequestsThatNeedAUserFailWithoutOne()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, "{}"));
        var error = await Assert.ThrowsAsync<JellyfinException>(() => server.Client(userId: null).UserViewsAsync());
        Assert.Equal(JellyfinErrorKind.NotSignedIn, error.Kind);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task NotFoundMeansNothingForLyricsSegmentsAndDiscMenus()
    {
        var server = new StubServer(_ => (HttpStatusCode.NotFound, ""));
        var client = server.Client();
        Assert.Null(await client.LyricsAsync("i"));
        Assert.Empty(await client.MediaSegmentsAsync("i"));
        Assert.Null(await client.DiscMenuAsync("i"));
    }

    [Fact]
    public async Task MediaSegmentsKeepOnlySkippableKinds()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, """
            {"Items":[
              {"Id":"1","Type":"Intro","StartTicks":0,"EndTicks":10},
              {"Id":"2","Type":"Unknown","StartTicks":10,"EndTicks":20},
              {"Id":"3","Type":"Outro","StartTicks":20,"EndTicks":30}]}
            """));
        var segments = await server.Client().MediaSegmentsAsync("i");
        Assert.Equal(["Skip Intro", "Skip Credits"], segments.Select(s => s.SkipTitle));
    }

    [Fact]
    public async Task OptionalPluginCallsReturnNullWhenAbsent()
    {
        var server = new StubServer(_ => (HttpStatusCode.NotFound, ""));
        Assert.Null(await server.Client().OpEdThemesAsync("i"));
        Assert.Null(await server.Client().OpEdLibrariesAsync());
        Assert.Empty(await server.Client().LatestMusicVideosAsync("lib"));
    }

    [Fact]
    public async Task AllItemsPagesUntilTheTotal()
    {
        var server = new StubServer(request =>
        {
            var start = int.Parse(System.Web.HttpUtility.ParseQueryString(request.Url.Query)["startIndex"]!);
            var count = start == 0 ? 500 : 3;
            var items = string.Join(",", Enumerable.Range(start, count).Select(i => $$"""{"Id":"{{i}}"}"""));
            return (HttpStatusCode.OK, $$"""{"Items":[{{items}}],"TotalRecordCount":503}""");
        });
        var shows = await server.Client().SeriesListAsync();
        Assert.Equal(503, shows.Count);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task SourceTitlesJoinIdsWithCommasAndNamesWithBars()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, """{"Items":[],"TotalRecordCount":0}"""));
        await server.Client().SourceTitlesAsync("Movie", null, (1999, 2001), ["PG", "R"], studioIds: ["a", "b"]);
        var query = System.Web.HttpUtility.ParseQueryString(Assert.Single(server.Requests).Url.Query);
        Assert.Equal("a,b", query["studioIds"]);
        Assert.Equal("PG|R", query["officialRatings"]);
        Assert.Equal("1999,2000,2001", query["years"]);
        Assert.Null(query["parentId"]);
    }

    [Fact]
    public async Task SaveAppSettingsKeepsOtherSettingsAndSkipsAccountWideOnes()
    {
        var server = new StubServer(request => request.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, """{"Id":"x","CustomPrefs":{"theme":"dark","skipForwardLength":"30000"},"SortBy":"SortName"}""")
            : (HttpStatusCode.NoContent, ""));
        await server.Client().SaveAppSettingsAsync(new Dictionary<string, string> { ["quality"] = "high" });

        var posted = JsonDocument.Parse(server.Requests[1].Body!).RootElement;
        Assert.Equal("SortName", posted.GetProperty("SortBy").GetString());
        var prefs = posted.GetProperty("CustomPrefs");
        Assert.Equal("dark", prefs.GetProperty("theme").GetString());
        Assert.Equal("high", prefs.GetProperty("quality").GetString());
        Assert.False(prefs.TryGetProperty("skipForwardLength", out _));
    }

    [Fact]
    public async Task PlaybackInfoAsksForTheDesktopProfile()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, """{"MediaSources":[{"Id":"m"}],"PlaySessionId":"p"}"""));
        var info = await server.Client().PlaybackInfoAsync("i", startTicks: 0, maxBitrate: 40_000_000, audioStreamIndex: 2);

        Assert.Equal("p", info.PlaySessionId);
        var body = JsonDocument.Parse(Assert.Single(server.Requests).Body!).RootElement;
        Assert.Equal("Aster Windows", body.GetProperty("DeviceProfile").GetProperty("Name").GetString());
        Assert.Equal(2, body.GetProperty("AudioStreamIndex").GetInt32());
        Assert.False(body.TryGetProperty("SubtitleStreamIndex", out _));
        Assert.True(body.GetProperty("EnableDirectPlay").GetBoolean());
    }
}
