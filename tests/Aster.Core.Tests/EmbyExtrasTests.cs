using Aster.Core.Jellyfin;
using Aster.Core.Playback;

namespace Aster.Core.Tests;

/// <summary>
/// Emby's own forms of what Jellyfin has routes for: scrub previews as a BIF
/// file, and lyrics as LRC text. The Apple app's EmbyExtrasTests.
/// </summary>
public class EmbyExtrasTests
{
    /// <summary>A BIF file with <paramref name="frames"/> (time, bytes) and the given multiplier.</summary>
    private static byte[] Bif((uint Time, byte[] Bytes)[] frames, uint multiplier = 1000)
    {
        static byte[] Word(uint value) => BitConverter.GetBytes(value);
        var file = new List<byte>([0x89, 0x42, 0x49, 0x46, 0x0D, 0x0A, 0x1A, 0x0A]);
        file.AddRange(Word(0));
        file.AddRange(Word((uint)frames.Length));
        file.AddRange(Word(multiplier));
        file.AddRange(new byte[64 - file.Count]);
        var offset = (uint)(64 + (frames.Length + 1) * 8);
        foreach (var (time, bytes) in frames)
        {
            file.AddRange(Word(time));
            file.AddRange(Word(offset));
            offset += (uint)bytes.Length;
        }
        file.AddRange(Word(uint.MaxValue));
        file.AddRange(Word(offset));
        foreach (var (_, bytes) in frames) file.AddRange(bytes);
        return [.. file];
    }

    [Fact]
    public void ReadsABifIndex()
    {
        var data = Bif([(0, [1, 2]), (10, [3, 4, 5]), (20, [6])]);
        var index = BifIndex.Parse(data)!;
        Assert.Equal([0.0, 10.0, 20.0], index.Frames.Select(f => f.Seconds));
        byte[] At(double seconds) => index.FrameAt(seconds) is { } f ? data[f.Offset..(f.Offset + f.Length)] : [];
        Assert.Equal([1, 2], At(0));
        Assert.Equal([3, 4, 5], At(14.5));
        Assert.Equal([6], At(999));
    }

    [Fact]
    public void ABifMultiplierIsMillisecondsPerUnit()
    {
        // Roku's own files: one unit per frame, 10 s apart.
        Assert.Equal([0.0, 10.0], BifIndex.Parse(Bif([(0, [1]), (1, [2])], multiplier: 10_000))!.Frames.Select(f => f.Seconds));
    }

    [Fact]
    public void NotABifFile() => Assert.Null(BifIndex.Parse("<html>not found</html>"u8));

    [Fact]
    public void ReadsTimedLrc()
    {
        var lyrics = SongLyrics.FromLrc("[ar:Someone]\r\n[ti:A Song]\r\n[00:12.50]First line\r\n[00:05.00][01:02.25]Chorus")!;
        Assert.True(lyrics.IsSynced);
        Assert.Equal(["Chorus", "First line", "Chorus"], lyrics.Lyrics.Select(l => l.Text));
        Assert.Equal([50_000_000L, 125_000_000L, 622_500_000L], lyrics.Lyrics.Select(l => l.Start!.Value));
    }

    [Fact]
    public void AnLrcOffsetShowsLinesSooner() =>
        Assert.Equal(15_000_000, SongLyrics.FromLrc("[offset:+500]\n[00:02.00]Line")!.Lyrics[0].Start);

    [Fact]
    public void PlainTextIsUnsynced()
    {
        var lyrics = SongLyrics.FromLrc("\nOne\nTwo\n\n")!;
        Assert.False(lyrics.IsSynced);
        Assert.Equal(["One", "Two"], lyrics.Lyrics.Select(l => l.Text));
        Assert.Null(SongLyrics.FromLrc("[ar:Only tags]\n"));
    }
}
