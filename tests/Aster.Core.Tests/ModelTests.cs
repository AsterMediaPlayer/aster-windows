using System.Text.Json;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;

namespace Aster.Core.Tests;

public class ModelTests
{
    private static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, JellyfinJson.Options)!;

    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, JellyfinJson.Options);

    [Fact]
    public void BaseItemDecodesFromServerJson()
    {
        var item = Decode<BaseItem>("""
            {
              "Id": "a1", "Name": "Pilot", "Type": "Episode", "IndexNumber": 4, "ParentIndexNumber": 1,
              "RunTimeTicks": 27000000000, "UserData": { "PlaybackPositionTicks": 9000000000 },
              "ProviderIds": { "AniDB": "123" },
              "MediaStreams": [ { "Type": "Video", "Width": 3840 } ],
              "Trickplay": { "src": { "320": { "Width": 320, "Height": 180, "TileWidth": 10, "TileHeight": 10, "ThumbnailCount": 400, "Interval": 10000 } } },
              "SomethingNew": true
            }
            """);

        Assert.Equal(ItemKind.Episode, item.Kind);
        Assert.Equal("S1, E4", item.EpisodeCode);
        Assert.Equal("45 min", item.RuntimeText);
        Assert.Equal("30 min left", item.RemainingText);
        Assert.Equal(1.0 / 3, item.ProgressFraction!.Value, 6);
        Assert.True(item.IsAnime);
        Assert.True(item.Is4K);
        Assert.True(item.IsPlayable);
        Assert.Equal(400, item.Trickplay!["src"]["320"].ThumbnailCount);
    }

    [Fact]
    public void UnknownItemTypesAreOther()
    {
        Assert.Equal(ItemKind.Other, new BaseItem { Id = "x", Type = "SomethingElse" }.Kind);
        Assert.Equal(ItemKind.Other, new BaseItem { Id = "x" }.Kind);
        Assert.Equal(ItemKind.Other, new BaseItem { Id = "x", Type = "Other" }.Kind);
        Assert.Equal(ItemKind.TvChannel, new BaseItem { Id = "x", Type = "TvChannel" }.Kind);
    }

    [Theory]
    [InlineData("Continuing", null, "2019–")]
    [InlineData("Ended", "2023-05-01T00:00:00.0000000Z", "2019–2023")]
    [InlineData("Ended", "2019-12-01T00:00:00.0000000Z", "2019")]
    public void SeriesYearText(string status, string? endDate, string expected)
    {
        var item = new BaseItem { Id = "s", Type = "Series", ProductionYear = 2019, Status = status, EndDate = endDate };
        Assert.Equal(expected, item.YearText);
    }

    [Fact]
    public void JellyfinDatesWithSevenFractionalDigitsParse()
    {
        var date = JellyfinDate.Parse("2026-10-20T18:30:15.1234567Z");
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 18, 30, 15, TimeSpan.Zero).AddTicks(1234567), date);
        Assert.Null(JellyfinDate.Parse(null));
        Assert.Null(JellyfinDate.Parse("not a date"));
        Assert.Equal("2026-10-20T18:30:15Z", JellyfinDate.Format(date!.Value));
    }

    [Fact]
    public void WritingLeavesOutNullsAndTheLocalRunTime()
    {
        var json = Encode(new PlaybackReport { ItemId = "i", PlayMethod = "DirectPlay", RunTimeTicks = 100 });
        Assert.DoesNotContain("RunTimeTicks", json);
        Assert.DoesNotContain("MediaSourceId", json);
        Assert.Contains("\"RepeatMode\":\"RepeatNone\"", json);
    }

    [Fact]
    public void UserConfigurationKeepsFieldsItDoesNotName()
    {
        var configuration = Decode<UserConfiguration>("""{"SubtitleMode":"Smart","FutureSetting":[1,2]}""");
        var json = Encode(configuration with { SubtitleMode = "Always" });
        Assert.Contains("\"SubtitleMode\":\"Always\"", json);
        Assert.Contains("\"FutureSetting\":[1,2]", json);
    }

    [Fact]
    public void DeviceProfileConditionsUseTheServersNames()
    {
        var json = Encode(DeviceProfile.DesktopMostCompatible(8_000_000));
        Assert.Contains("""{"Condition":"LessThanEqual","Property":"VideoBitDepth","Value":"8","IsRequired":false}""", json);
        Assert.Contains("\"DirectPlayProfiles\":[]", json);
    }

    [Fact]
    public void DesktopProfileDirectPlaysAnything()
    {
        var json = Encode(DeviceProfile.Desktop(8_000_000));
        Assert.Contains("""{"Type":"Video"}""", json);
        Assert.Contains("""{"Format":"pgssub","Method":"Embed"}""", json);
    }

    [Fact]
    public void FullVersionSwapsInTheFullCopies()
    {
        var theme = new OpEdTheme
        {
            Id = "t", Type = "OP", Slug = "OP1", Version = 2,
            VideoMp4Url = "/short.mp4", FullVideoMp4Url = "/full.mp4", FullLibraryItemId = "lib-full",
        };
        var full = theme.WithCut(ThemeCut.Full)!;
        Assert.Equal("/full.mp4", full.PlayablePath);
        Assert.Equal("lib-full", full.LibraryItemId);
        Assert.Equal(ThemeCut.Full, full.PlayedCut);
        // A version's own versions are only itself.
        Assert.Null(full.WithCut(ThemeCut.MusicVideo));
        Assert.Equal("OP1 v2", theme.Code);
        Assert.Same(theme, theme.WithCut(ThemeCut.TvSize));
    }

    /// <summary>Settings' version, or the nearest the theme has: a music video falls back to the full version, then the TV size.</summary>
    [Fact]
    public void ThePreferredVersionFallsBackToTheNearest()
    {
        var both = new OpEdTheme
        {
            Id = "t", Type = "OP", Slug = "OP1",
            VideoMp4Url = "/tv.mp4", FullVideoMp4Url = "/full.mp4", MvVideoMp4Url = "/mv.mp4",
        };
        Assert.Equal([ThemeCut.TvSize, ThemeCut.Full, ThemeCut.MusicVideo], both.AvailableCuts);
        Assert.Equal("/mv.mp4", both.Preferring(ThemeCut.MusicVideo).PlayablePath);

        var fullOnly = both with { MvVideoMp4Url = null, HasMusicVideo = true };
        Assert.Equal("/full.mp4", fullOnly.Preferring(ThemeCut.MusicVideo).PlayablePath);
        Assert.Equal([ThemeCut.MusicVideo], fullOnly.PreparingCuts);

        var tvOnly = both with { MvVideoMp4Url = null, FullVideoMp4Url = null };
        Assert.Same(tvOnly, tvOnly.Preferring(ThemeCut.MusicVideo));
        Assert.Empty(tvOnly.PreparingCuts);
    }

    [Fact]
    public void SongLineJoinsTitleAndArtists()
    {
        var theme = new OpEdTheme { Id = "t", Type = "OP", Slug = "OP1", SongTitle = "Yuusha", Artists = ["YOASOBI"] };
        Assert.Equal("Yuusha — YOASOBI", theme.SongLine);
        Assert.Null((theme with { SongTitle = null, Artists = [] }).SongLine);
    }

    [Theory]
    [InlineData(222, "3:42")]
    [InlineData(3725, "1:02:05")]
    public void Clock(double seconds, string expected) => Assert.Equal(expected, MusicFormat.Clock(seconds));

    [Theory]
    [InlineData(0L, 60L * 60 * 10_000_000, false)]
    [InlineData(1L * 60 * 10_000_000, 60L * 60 * 10_000_000, false)]
    [InlineData(30L * 60 * 10_000_000, 60L * 60 * 10_000_000, true)]
    [InlineData(57L * 60 * 10_000_000, 60L * 60 * 10_000_000, false)]
    [InlineData(2L * 60 * 10_000_000, 4L * 60 * 10_000_000, false)]
    [InlineData(5L, null, true)]
    public void ResumePointRules(long ticks, long? runTime, bool keeps) =>
        Assert.Equal(keeps, ResumePoints.KeepsResumePoint(ticks, runTime));

    [Fact]
    public void TrickplayCellsRunAcrossThenDownThenToTheNextSheet()
    {
        var info = new TrickplayInfo { Width = 320, Height = 180, TileWidth = 10, TileHeight = 10, ThumbnailCount = 250, Interval = 10_000 };
        Assert.Equal(new TrickplayInfo.Cell(0, 3, 1), info.CellAt(130));
        Assert.Equal(new TrickplayInfo.Cell(1, 0, 0), info.CellAt(1000));
        Assert.Equal(new TrickplayInfo.Cell(2, 9, 4), info.CellAt(1e9));
        Assert.Equal(new TrickplayInfo.PixelRect(960, 180, 320, 180), info.RectOf(new TrickplayInfo.Cell(0, 3, 1)));
        Assert.Null(info.CellAt(double.NaN));
    }
}
