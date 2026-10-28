using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Plex;

namespace Aster.Core.Tests;

/// <summary>
/// Playing from Plex: what to skip (Plex Pass markers, else chapters named
/// for an opening or ending), subtitle files beside the video, which subtitle
/// tracks are fetched and which burned in, and the addresses handed to mpv.
/// A port of the Apple app's PlexPlaybackTests.
/// </summary>
public class PlexPlaybackTests
{
    private static PlexClient Client() => new(new Uri("http://192.168.1.240:32400"), "tok", "u");

    [Fact]
    public void MarkersComeFirst()
    {
        var segments = PlexSegments.Segments(
            [new PlexMarker { Type = "intro", StartTimeOffset = 60_000, EndTimeOffset = 150_000 },
             new PlexMarker { Type = "credits", StartTimeOffset = 1_300_000, EndTimeOffset = 1_400_000 }],
            [new PlexChapter { Tag = "Opening", StartTimeOffset = 135_990, EndTimeOffset = 226_000 }],
            "1", 1_422_428);
        Assert.Equal(["Intro", "Outro"], segments.Select(s => s.Type));
        Assert.Equal(600_000_000, segments[0].StartTicks);
        Assert.Equal(1_500_000_000, segments[0].EndTicks);
    }

    /// <summary>The anime episode on the test server: no markers, chapters named Opening and Ending.</summary>
    [Fact]
    public void NamedChaptersStandInForMarkers()
    {
        PlexChapter[] chapters =
        [
            new() { Tag = "Prologue", StartTimeOffset = 1002, EndTimeOffset = 135_990 },
            new() { Tag = "Opening", StartTimeOffset = 135_990, EndTimeOffset = 226_000 },
            new() { Tag = "As of Today, I'm a Bad Girl!", StartTimeOffset = 226_000, EndTimeOffset = 545_980 },
            new() { Tag = "Ending", StartTimeOffset = 1_280_970, EndTimeOffset = 1_371_020 },
            new() { Tag = "Epilogue", StartTimeOffset = 1_371_020, EndTimeOffset = 1_422_420 },
        ];
        var segments = PlexSegments.Segments([], chapters, "6139", 1_422_428);
        Assert.Equal(["Intro", "Outro"], segments.Select(s => s.Type));
        Assert.Equal(1_359_900_000, segments[0].StartTicks);
        Assert.Equal(2_260_000_000, segments[0].EndTicks);
        Assert.Equal(12_809_700_000, segments[1].StartTicks);
    }

    /// <summary>Beastars on the test server names its whole first half "Intro": too long to be an opening.</summary>
    [Fact]
    public void AHalfNamedIntroIsNoOpening()
    {
        PlexChapter[] chapters =
        [
            new() { Tag = "Intro", StartTimeOffset = 33, EndTimeOffset = 646_950 },
            new() { Tag = "Part B", StartTimeOffset = 646_950, EndTimeOffset = 1_279_920 },
            new() { Tag = "ED", StartTimeOffset = 1_279_920, EndTimeOffset = 1_397_800 },
        ];
        var segments = PlexSegments.Segments([], chapters, "6359", 1_397_800);
        Assert.Equal(["Outro"], segments.Select(s => s.Type));
    }

    [Fact]
    public void PlainChaptersGiveNothing() =>
        Assert.Empty(PlexSegments.Segments([], [new PlexChapter { Tag = "Chapter 1", StartTimeOffset = 0, EndTimeOffset = 60_000 }], "x", 60_000));

    [Fact]
    public void SrtAndWebVtt()
    {
        var srt = "1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Hello</i>\r\nthere\r\n\r\n2\r\n00:01:02,000 --> 00:01:04,250\r\nAgain\r\n";
        Assert.Equal([new SubtitleCue(1.5, 3, "Hello\nthere"), new SubtitleCue(62, 64.25, "Again")], PlexSubtitles.Cues(srt, "srt"));

        var vtt = "WEBVTT\n\n00:05.000 --> 00:06.000 align:start\nShort form\n";
        Assert.Equal([new SubtitleCue(5, 6, "Short form")], PlexSubtitles.Cues(vtt, "vtt"));
    }

    [Fact]
    public void AssDialogueAsText()
    {
        var ass = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:10.50,0:00:12.00,Default,,0,0,0,,{\\an8}Signs, here\\Nand there\n";
        Assert.Equal([new SubtitleCue(10.5, 12, "Signs, here\nand there")], PlexSubtitles.Cues(ass, "ass"));
    }

    [Fact]
    public void Latin1SubtitleFilesStillRead() =>
        Assert.Equal("Café", PlexSubtitles.Text([0x43, 0x61, 0x66, 0xE9]));

    /// <summary>
    /// Plex hands out subtitle files beside the video, not tracks inside it:
    /// mpv reads those from the file, and a converted stream burns them in.
    /// </summary>
    [Fact]
    public void OnlySubtitleFilesAreFetched()
    {
        var client = Client();
        var sidecar = client.PlaybackStream(new PlexStream { Id = 1, StreamType = 3, Codec = "srt", Key = "/library/streams/1" }, 9);
        var embedded = client.PlaybackStream(new PlexStream { Id = 2, StreamType = 3, Index = 4, Codec = "ass" }, 4);
        var picture = client.PlaybackStream(new PlexStream { Id = 3, StreamType = 3, Index = 5, Codec = "pgs" }, 5);
        Assert.True(TrackRules.IsTextTrack(sidecar));
        Assert.False(TrackRules.IsTextTrack(embedded));
        Assert.Equal("Encode", picture.DeliveryMethod);
        Assert.Equal(4, embedded.Index);
        // A file beside the video has no place in the container: numbered past it.
        Assert.Equal(1009, sidecar.Index);
        Assert.Equal("http://192.168.1.240:32400/library/streams/1?X-Plex-Token=tok", sidecar.DeliveryUrl);
        Assert.Null(embedded.DeliveryUrl);
    }

    [Fact]
    public void TheFileAndConvertedAddresses()
    {
        var client = Client();
        var file = client.DirectStreamUrl("6139", "8696", "s1");
        Assert.Equal("/library/parts/8696/file", file.AbsolutePath);
        Assert.Contains("X-Plex-Token=tok", file.Query);
        Assert.Contains("X-Plex-Session-Identifier=s1", file.Query);
        // The transcoder's address keeps its query.
        Assert.Equal("http://192.168.1.240:32400/video/:/transcode/universal/start.m3u8?a=1",
            client.AbsoluteUrl("/video/:/transcode/universal/start.m3u8?a=1")?.ToString());
        Assert.Equal("https://elsewhere/x?y=1", client.AbsoluteUrl("https://elsewhere/x?y=1")?.ToString());
    }

    [Theory]
    [InlineData("http://192.168.1.240:32400", true)]
    [InlineData("http://10.0.0.5:32400", true)]
    [InlineData("http://172.20.1.1:32400", true)]
    [InlineData("http://server:32400", true)]
    [InlineData("https://192-168-1-240.abc123.plex.direct:32400", true)]
    [InlineData("http://203.0.113.9:32400", false)]
    [InlineData("https://plex.example.com", false)]
    public void LocalServersConvertAtLocalQuality(string address, bool local) =>
        Assert.Equal(local, PlexClient.IsLocal(new Uri(address)));
}
