using Aster.Core.Anime;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>
/// The song card from AnimeThemes when there's no OP/ED plugin: its reply
/// (trimmed from the real one for Bad Girl), the episodes each theme plays
/// in, and the card each episode gets. The Apple app's AnimeThemesTests.
/// </summary>
public class AnimeThemesTests
{
    private const string Reply = """
        {"anime":{"name":"Bad Girl","slug":"bad_girl","year":2025,"animethemes":[
          {"type":"OP","sequence":null,"song":{"title":"Super Big Love!","performances":[{"artist":{"name":"Tenrou-gun"}}]},"animethemeentries":[{"episodes":"1-"}]},
          {"type":"ED","sequence":1,"song":{"title":"BAD SURPRISE","performances":[{"artist":{"name":"Tenrou-gun"}}]},"animethemeentries":[{"episodes":"1-6, 8-"}]},
          {"type":"ED","sequence":2,"song":{"title":"Heart Kakimawashite Ageru♡","performances":[{"artist":{"name":"Miharu Hanai"}}]},"animethemeentries":[{"episodes":"7"}]}
        ]}}
        """;

    [Fact]
    public void ARepliesThemesAreRead()
    {
        var themes = AnimeThemesLookup.Read(Reply);
        Assert.Equal(3, themes.Count);
        Assert.Equal((true, 1, "Super Big Love!", "1-"), (themes[0].IsOpening, themes[0].Sequence, themes[0].SongTitle, themes[0].Episodes));
        Assert.Equal(["Tenrou-gun"], themes[0].Artists);
        Assert.Equal(["Miharu Hanai"], themes[2].Artists);
    }

    [Fact]
    public void EpisodeRanges()
    {
        Assert.Equal([(1, 6), (8, int.MaxValue)], KnownTheme.Ranges("1-6, 8-"));
        Assert.Equal([(7, 7)], KnownTheme.Ranges("7"));
        var ending = new KnownTheme(false, 1, null, [], "1-6, 8-");
        Assert.True(ending.PlaysInEpisode(6));
        Assert.False(ending.PlaysInEpisode(7));
        Assert.True(ending.PlaysInEpisode(12));
        Assert.True(ending.PlaysInEpisode(null));
    }

    /// <summary>Episode 7 has its own ending; the rest share the first.</summary>
    [Theory]
    [InlineData(7, "Ending 2: Heart Kakimawashite Ageru♡ · Miharu Hanai")]
    [InlineData(3, "Ending 1: BAD SURPRISE · Tenrou-gun")]
    public void EachEpisodeGetsItsEnding(int number, string ending)
    {
        const long second = 10_000_000;
        var episode = new BaseItem
        {
            Id = $"e{number}",
            Type = "Episode",
            SeriesName = "Bad Girl",
            IndexNumber = number,
            ParentIndexNumber = 1,
            RunTimeTicks = 1440 * second,
            Chapters =
            [
                new ChapterInfo { StartPositionTicks = 0, Name = "Prologue" },
                new ChapterInfo { StartPositionTicks = 1_359_900_000, Name = "Opening" },
                new ChapterInfo { StartPositionTicks = 2_260_000_000, Name = "Part A" },
                new ChapterInfo { StartPositionTicks = 12_809_700_000, Name = "Ending" },
                new ChapterInfo { StartPositionTicks = 13_710_200_000, Name = "Epilogue" },
            ],
        };
        var cards = ThemeMoments.ForEpisode(episode, [], [], AnimeThemesLookup.Read(Reply))
            .Select(m => $"{m.Info.Kicker}: {m.Info.Title} · {m.Info.Artists}");
        Assert.Equal(["Opening 1: Super Big Love! · Tenrou-gun", ending], cards);
    }

    /// <summary>Frieren's first episode ends with "bliss", its own; the season's ending is listed first.</summary>
    [Fact]
    public void AnEpisodesOwnThemeBeatsTheSeasons()
    {
        KnownTheme[] themes = [new(false, 1, "Anytime Anywhere", ["milet"], "1-14, 16"), new(false, 1, "bliss", ["milet"], "1")];
        var episode = new BaseItem
        {
            Id = "e1",
            Type = "Episode",
            SeriesName = "Frieren",
            IndexNumber = 1,
            ParentIndexNumber = 1,
            RunTimeTicks = 1440 * 10_000_000L,
            Chapters = [new ChapterInfo { StartPositionTicks = 0, Name = "Part A" }, new ChapterInfo { StartPositionTicks = 13_000_000_000, Name = "Ending" },
                new ChapterInfo { StartPositionTicks = 13_900_000_000, Name = "Preview" }],
        };
        Assert.Equal("bliss", Assert.Single(ThemeMoments.ForEpisode(episode, [], [], themes)).Info.Title);
        Assert.Equal("Anytime Anywhere", Assert.Single(ThemeMoments.ForEpisode(episode with { IndexNumber = 2 }, [], [], themes)).Info.Title);
    }

    [Fact]
    public void WithoutAnyThemesTheCardNamesTheShow()
    {
        var episode = new BaseItem
        {
            Id = "e1",
            Type = "Episode",
            SeriesName = "Bad Girl",
            IndexNumber = 1,
            ParentIndexNumber = 1,
            RunTimeTicks = 1440 * 10_000_000L,
            Chapters = [new ChapterInfo { StartPositionTicks = 0, Name = "Opening" }, new ChapterInfo { StartPositionTicks = 900_000_000, Name = "Part A" }],
        };
        Assert.Equal("Bad Girl", Assert.Single(ThemeMoments.ForEpisode(episode, [], [])).Info.Title);
    }
}
