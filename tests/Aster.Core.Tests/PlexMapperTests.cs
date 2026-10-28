using System.Text.Json;
using Aster.Core.Jellyfin;
using Aster.Core.Plex;
using Aster.Core.Server;

namespace Aster.Core.Tests;

/// <summary>
/// Plex's titles become the app's: replies trimmed from a real Plex Media
/// Server 1.43 (an anime episode, a film, a show and its season). A port of
/// the Apple app's PlexMapperTests.
/// </summary>
public class PlexMapperTests
{
    private static PlexContainer Container(string json) => JsonSerializer.Deserialize<PlexReply>(json, PlexJson.Options)!.MediaContainer;

    private static PlexMetadata Metadata(string json) => Container(json).Metadata![0];

    private const string Episode = """
    {"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"6139","key":"/library/metadata/6139","parentRatingKey":"6138","grandparentRatingKey":"6137",
     "guid":"tv.plex.agents.custom.shoko://episode/e2255","type":"episode","title":"As of Today, I'm a Bad Girl!","grandparentTitle":"Bad Girl","parentTitle":"Season 1",
     "summary":"Yuu Yuutani is a model first year student.","index":1,"parentIndex":1,"year":2025,"viewOffset":62000,
     "thumb":"/library/metadata/6139/thumb/1792890348","art":"/library/metadata/6137/art/1792894637","parentThumb":"/library/metadata/6138/thumb/1792886180",
     "grandparentThumb":"/library/metadata/6137/thumb/1792894637","grandparentArt":"/library/metadata/6137/art/1792894637",
     "duration":1422428,"originallyAvailableAt":"2025-07-06","addedAt":1788704596,
     "Media":[{"id":8696,"duration":1422428,"bitrate":9007,"width":1920,"height":1080,"audioCodec":"opus","videoCodec":"hevc","videoResolution":"1080","container":"mkv",
       "Part":[{"id":8696,"key":"/library/parts/8696/1788704596/file.mkv","duration":1422428,"container":"mkv","Stream":[
         {"id":71904,"streamType":1,"default":true,"codec":"hevc","index":0,"width":1920,"height":1080,"displayTitle":"1080p"},
         {"id":71905,"streamType":2,"default":true,"codec":"opus","index":1,"channels":2,"languageCode":"eng","displayTitle":"English (OPUS Stereo)"},
         {"id":71907,"streamType":3,"codec":"ass","index":3,"forced":true,"languageCode":"eng","displayTitle":"English Forced"}]}]}],
     "Image":[{"alt":"x","type":"coverPoster","url":"/library/metadata/6137/thumb/1792894637"},{"alt":"x","type":"clearLogo","url":"/library/metadata/6137/clearLogo/1792894637"}],
     "Guid":[{"id":"tmdb://5439847"},{"id":"tvdb://10566016"}],
     "Chapter":[{"id":982,"tag":"Prologue","index":1,"startTimeOffset":1002,"endTimeOffset":135990},{"id":30356,"tag":"Opening","index":2,"startTimeOffset":135990,"endTimeOffset":226000}]}]}}
    """;

    [Fact]
    public void AnAnimeEpisode()
    {
        var item = PlexMapper.Item(Metadata(Episode));
        Assert.Equal("6139", item.Id);
        Assert.Equal(ItemKind.Episode, item.Kind);
        Assert.Equal("6137", item.SeriesId);
        Assert.Equal("Bad Girl", item.SeriesName);
        Assert.Equal("6138", item.SeasonId);
        Assert.Equal(1, item.IndexNumber);
        Assert.Equal(1, item.ParentIndexNumber);
        Assert.Equal(14_224_280_000, item.RunTimeTicks);
        Assert.Equal(620_000_000, item.UserData?.PlaybackPositionTicks);
        Assert.False(item.UserData?.Played);
        // Its own still, then its show's poster, backdrop and logo.
        Assert.Equal("/library/metadata/6139/thumb/1792890348", item.ImageTags?["Primary"]);
        Assert.False(item.ImageTags?.ContainsKey("Logo"));
        Assert.Equal("/library/metadata/6137/thumb/1792894637", item.SeriesPrimaryImageTag);
        Assert.Equal("6137", item.ParentBackdropItemId);
        Assert.Equal(["/library/metadata/6137/art/1792894637"], item.ParentBackdropImageTags!);
        Assert.Equal("/library/metadata/6137/clearLogo/1792894637", item.ParentLogoImageTag);
        Assert.Equal(new Dictionary<string, string> { ["Tmdb"] = "5439847", ["Tvdb"] = "10566016", ["Shoko"] = "e2255" }, item.ProviderIds);
        Assert.True(item.IsAnime);
        Assert.Equal("2025-07-06T00:00:00Z", item.PremiereDate);
        Assert.Equal(["Prologue", "Opening"], item.Chapters!.Select(c => c.Name));
        Assert.Equal(1_359_900_000, item.Chapters![1].StartPositionTicks);
        Assert.Equal(["Video", "Audio", "Subtitle"], item.MediaStreams!.Select(s => s.Type));
        Assert.True(item.MediaStreams![2].IsForced);
        Assert.Equal("mkv", item.Container);
    }

    [Fact]
    public void AFilmWithItsCast()
    {
        var item = PlexMapper.Item(Metadata("""
        {"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"769","type":"movie","title":"The 'Burbs","titleSort":"Burbs","year":1989,"duration":6082560,
         "thumb":"/library/metadata/769/thumb/1792876534","art":"/library/metadata/769/art/1792876534","contentRating":"PG","rating":5.7,"audienceRating":7.1,
         "studio":"Imagine Entertainment","tagline":"Paranoia Begins At Home","addedAt":1791657480,"originallyAvailableAt":"1989-02-17","viewCount":2,
         "Genre":[{"id":1,"tag":"Comedy"}],"Role":[{"id":798,"tag":"Tom Hanks","role":"Ray Peterson","thumb":"https://metadata-static.plex.tv/people/a.jpg"}],
         "Director":[{"id":900,"tag":"Joe Dante"}]}]}}
        """));
        Assert.Equal(ItemKind.Movie, item.Kind);
        Assert.Equal("Burbs", item.SortName);
        Assert.Equal("PG", item.OfficialRating);
        Assert.Equal(7.1, item.CommunityRating);
        Assert.Equal(["Comedy"], item.Genres!);
        Assert.Equal("Imagine Entertainment", item.Studios?.FirstOrDefault()?.Name);
        Assert.Equal(["Paranoia Begins At Home"], item.Taglines!);
        Assert.True(item.UserData?.Played);
        Assert.Equal(["/library/metadata/769/art/1792876534"], item.BackdropImageTags!);
        Assert.Equal(["Tom Hanks"], item.Cast.Select(p => p.Name));
        Assert.Equal("798", item.Cast.First().Id);
        Assert.Equal("Director", item.People?.Last().Type);
    }

    [Fact]
    public void AShowAndItsSeasonCountWhatsLeft()
    {
        var show = PlexMapper.Item(Metadata("""
        {"MediaContainer":{"Metadata":[{"ratingKey":"1188","type":"show","title":"No Reservations","leafCount":134,"viewedLeafCount":134,"childCount":8,
         "thumb":"/library/metadata/1188/thumb/1","art":"/library/metadata/1188/art/1"}]}}
        """));
        Assert.Equal(ItemKind.Series, show.Kind);
        Assert.Equal(8, show.ChildCount);
        Assert.True(show.UserData?.Played);
        Assert.Equal(0, show.UserData?.UnplayedItemCount);

        var season = PlexMapper.Item(Metadata("""
        {"MediaContainer":{"Metadata":[{"ratingKey":"1189","type":"season","title":"Season 1","index":1,"parentRatingKey":"1188","parentTitle":"No Reservations",
         "parentThumb":"/library/metadata/1188/thumb/1","leafCount":13,"viewedLeafCount":4,"thumb":"/library/metadata/1189/thumb/1"}]}}
        """));
        Assert.Equal(ItemKind.Season, season.Kind);
        Assert.Equal("1188", season.SeriesId);
        Assert.Equal(9, season.UserData?.UnplayedItemCount);
        Assert.False(season.UserData?.Played);
        Assert.Equal("/library/metadata/1188/thumb/1", season.SeriesPrimaryImageTag);
    }

    [Fact]
    public void SectionsBecomeLibraries()
    {
        var libraries = Container("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies","agent":"tv.plex.agents.movie"},{"key":"4","type":"show","title":"Anime","agent":"tv.plex.agents.custom.shoko"}]}}""")
            .Directory!.Select(PlexMapper.Library).ToList();
        Assert.Equal(["movies", "tvshows"], libraries.Select(l => l.CollectionType));
        Assert.All(libraries, l => Assert.Equal(ItemKind.CollectionFolder, l.Kind));
    }

    /// <summary>A show's season list (Beastars, three seasons) carries an "All episodes" Directory with no type; the seasons still come through.</summary>
    [Fact]
    public void ASeasonListWithItsAllEpisodesLink()
    {
        var container = Container("""
        {"MediaContainer":{"size":3,"key":"6357","title2":"Beastars","Directory":[{"leafCount":48,"thumb":"/library/metadata/6357/thumb/1","viewedLeafCount":0,"key":"/library/metadata/6357/allLeaves","title":"All episodes"}],
         "Metadata":[{"ratingKey":"6358","key":"/library/metadata/6358/children","parentRatingKey":"6357","type":"season","title":"Season 1","parentTitle":"Beastars","index":1,"leafCount":12,"viewedLeafCount":0},
          {"ratingKey":"11377","key":"/library/metadata/11377/children","parentRatingKey":"6357","type":"season","title":"Season 2","parentTitle":"Beastars","index":2,"leafCount":12,"viewedLeafCount":0},
          {"ratingKey":"11352","key":"/library/metadata/11352/children","parentRatingKey":"6357","type":"season","title":"Season 3","parentTitle":"Beastars","index":3,"leafCount":24,"viewedLeafCount":0}]}}
        """);
        Assert.Null(container.Directory?.FirstOrDefault()?.Type);
        var seasons = container.Metadata!.Select(PlexMapper.Item).ToList();
        Assert.Equal(["6358", "11377", "11352"], seasons.Select(s => s.Id));
        Assert.All(seasons, s => Assert.Equal(ItemKind.Season, s.Kind));
    }

    /// <summary>Failures say what usually causes them, naming the server.</summary>
    [Fact]
    public void ErrorsSayWhy()
    {
        Assert.Contains("Remote Access", PlexErrors.Unreachable("Test Server"));
        Assert.Contains("Sign in with Plex again", PlexErrors.Http(401));
        Assert.Contains("500", PlexErrors.Http(500));
        Assert.True(PlexErrors.MeansUnreachable(new HttpRequestException(HttpRequestError.ConnectionError)));
        Assert.False(PlexErrors.MeansUnreachable(new OperationCanceledException()));

        var sessions = Container("""{"MediaContainer":{"size":1,"TranscodeSession":[{"key":"ffr9g73a1ms6tt7us6c3ni7a","throttled":true,"progress":24.1,"videoDecision":"copy"}]}}""").TranscodeSession;
        Assert.Equal(["ffr9g73a1ms6tt7us6c3ni7a"], sessions!.Select(s => s.Key));
    }

    /// <summary>The shared picture choice finds the right Plex paths, through the server's photo transcoder.</summary>
    [Fact]
    public void LockupsFindThePlexPictures()
    {
        var client = new PlexClient(new Uri("https://s.plex.direct:32400"), "t", "u");
        var item = PlexMapper.Item(Metadata(Episode));
        Assert.Contains("url=%2Flibrary%2Fmetadata%2F6139%2Fthumb%2F1792890348", client.LandscapeUrl(item)!.Query);
        Assert.Contains("url=%2Flibrary%2Fmetadata%2F6137%2Fart%2F1792894637", client.BackdropUrl(item)!.Query);
        var logo = client.LogoUrl(item)!.Query;
        Assert.Contains("url=%2Flibrary%2Fmetadata%2F6137%2FclearLogo%2F1792894637", logo);
        Assert.Contains("format=png", logo);
        Assert.StartsWith("https://s.plex.direct:32400/photo/:/transcode?", client.LandscapeUrl(item)!.AbsoluteUri);
    }

    [Fact]
    public void PlexHasNoneOfJellyfinsExtras()
    {
        var client = new PlexClient(new Uri("http://server:32400"), "t", "u");
        Assert.Equal(ServerKind.Plex, client.Kind);
        Assert.False(client.Can(ServerCapabilities.Favorites));
        Assert.False(client.Can(ServerCapabilities.Music));
    }
}
