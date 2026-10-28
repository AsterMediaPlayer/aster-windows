using Aster.Core.Anime;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

public class OpeningsEndingsTests
{
    private const long Second = 10_000_000;

    private static BaseItem Episode(int season, int number, params (string Name, int Start)[] chapters) => new()
    {
        Id = $"s{season}e{number}",
        Type = "Episode",
        SeriesName = "Frieren",
        SeasonName = "Season 1",
        ParentIndexNumber = season,
        IndexNumber = number,
        RunTimeTicks = 1473 * Second,
        Chapters = chapters.Select(c => new ChapterInfo { Name = c.Name, StartPositionTicks = c.Start * Second }).ToList(),
    };

    private static OpEdTheme Theme(string type, int sequence, bool plays, string? song = null) => new()
    {
        Id = $"{type}{sequence}",
        Type = type,
        Slug = $"{type}{sequence}",
        Sequence = sequence,
        Version = 1,
        SongTitle = song,
        Artists = song is null ? [] : ["Yorushika"],
        Episodes = "17-28",
        PlaysInEpisode = plays,
    };

    [Fact]
    public void FindsOpeningsAndEndingsByTheirChapterNames()
    {
        var clips = OpeningsEndings.Find([Episode(1, 1, ("Prologue", 0), ("OP", 60), ("Part A", 150), ("ED - bliss", 1380), ("Preview", 1460))]);
        Assert.Collection(clips,
            op => Assert.Equal((ThemeKind.Opening, 60 * Second, 150 * Second), (op.Kind, op.StartTicks, op.EndTicks)),
            ed => Assert.Equal((ThemeKind.Ending, 1380 * Second, 1460 * Second), (ed.Kind, ed.StartTicks, ed.EndTicks)));
    }

    [Fact]
    public void AnIntroBesideARealOpeningIsTheColdOpen()
    {
        var clips = OpeningsEndings.Find([Episode(1, 1, ("Intro", 0), ("Opening", 90), ("Part A", 180))]);
        Assert.Equal(90 * Second, Assert.Single(clips).StartTicks);
    }

    [Fact]
    public void ChaptersTooShortOrLongAreLeftOut()
    {
        Assert.Empty(OpeningsEndings.Find([Episode(1, 1, ("OP", 0), ("Part A", 20))]));
        Assert.Empty(OpeningsEndings.Find([Episode(1, 1, ("OP", 0), ("Part A", 300))]));
    }

    [Fact]
    public void ANewLengthIsANewSong()
    {
        var clips = OpeningsEndings.Find(
        [
            Episode(1, 1, ("OP", 0), ("A", 90)),
            Episode(1, 2, ("OP", 0), ("A", 91)),
            Episode(1, 3, ("OP", 0), ("A", 85)),
        ]);
        Assert.Equal([1, 2], clips.Select(c => c.Number));
        Assert.Equal(["Season 1 Opening", "Season 1 Opening 2"], clips.Select(c => c.Title));
    }

    [Fact]
    public void SpecialsAreSkipped() => Assert.Empty(OpeningsEndings.Find([Episode(0, 1, ("OP", 0), ("A", 90))]));

    [Fact]
    public void ChapterOpeningsCardTheirShow()
    {
        var moment = Assert.Single(ThemeMoments.ForEpisode(Episode(1, 3, ("OP", 0), ("A", 90)), [], []));
        Assert.Equal(new ThemeInfo("Opening", "Frieren", Detail: "Season 1 · From S1, E3"), moment.Info);
        Assert.Equal((0, 90), (moment.Start, moment.End));
    }

    /// <summary>As a server gives Frieren S1E25: chapters in Portuguese, the plugin's OP2 playing in it, and skip markers.</summary>
    [Fact]
    public void WithoutChaptersThePluginsThemePlacesItsCardByTheMarkers()
    {
        var episode = Episode(1, 25, ("Prólogo", 0), ("Abertura - 「Haru by Yorushika」", 45), ("Parte A", 135));
        IReadOnlyList<MediaSegment> markers =
        [
            new() { Id = "i", Type = "Intro", StartTicks = 45 * Second, EndTicks = 135 * Second },
            new() { Id = "o", Type = "Outro", StartTicks = 1370 * Second, EndTicks = 1473 * Second },
        ];
        var moments = ThemeMoments.ForEpisode(episode, markers, [Theme("OP", 1, plays: false, "Yuusha"), Theme("OP", 2, plays: true, "Haru")]);
        Assert.Collection(moments,
            opening => Assert.Equal(new ThemeInfo("Opening 2", "Haru", "Yorushika", "Frieren", "Season 1 · Episodes 17-28"), opening.Info),
            ending => Assert.Equal(new ThemeInfo("Ending", "Frieren", Detail: "S1, E25"), ending.Info));
    }

    [Fact]
    public void AnOrdinaryShowsIntroGetsNoCard()
    {
        IReadOnlyList<MediaSegment> markers = [new() { Id = "i", Type = "Intro", StartTicks = 0, EndTicks = 60 * Second }];
        Assert.Empty(ThemeMoments.ForEpisode(Episode(1, 1), markers, []));
        Assert.Empty(ThemeMoments.ForEpisode(Episode(1, 1), markers, [Theme("OP", 1, plays: false)]));
    }

    [Fact]
    public void ThePluginsKickerNamesItsVersion()
    {
        var theme = Theme("ED", 2, plays: true, "bliss") with { Version = 2, Variant = "TV", Cut = ThemeCut.Full };
        Assert.Equal("Ending 2 · Version 2 · TV · Full Version", ThemeInfo.From(theme, "Frieren", null).Kicker);
        Assert.EndsWith("· Music Video", ThemeInfo.From(theme with { Cut = ThemeCut.MusicVideo }, "Frieren", null).Kicker);
    }

    [Fact]
    public void TheCardShowsAtTheStartAndAgainAtTheEnd()
    {
        var moment = new ThemeMoment(45, 135, new ThemeInfo("Opening", "Frieren"));
        Assert.False(moment.ShowsCardAt(40));
        Assert.True(moment.ShowsCardAt(44.6));
        Assert.True(moment.ShowsCardAt(60));
        Assert.False(moment.ShowsCardAt(61));
        Assert.False(moment.ShowsCardAt(110));
        Assert.True(moment.ShowsCardAt(120));
        Assert.False(moment.ShowsCardAt(136));
        // Too short for a second card: just the one at the start.
        var brief = new ThemeMoment(0, 40, new ThemeInfo("Opening", "Frieren"));
        Assert.False(brief.ShowsCardAt(30));
    }
}
