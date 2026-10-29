namespace Aster.Core.Playback;

/// <summary>
/// Emby's preview frames: one BIF file per title, an index of times and
/// offsets followed by the JPEG frames themselves (Roku's format), made when
/// the library has thumbnail extraction turned on. The Apple app's BIFIndex.
/// </summary>
public sealed class BifIndex
{
    /// <summary>Each frame's start in seconds, with where its bytes are in the file.</summary>
    public IReadOnlyList<(double Seconds, int Offset, int Length)> Frames { get; }

    private BifIndex(IReadOnlyList<(double, int, int)> frames) => Frames = frames;

    private static ReadOnlySpan<byte> Magic => [0x89, 0x42, 0x49, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Null when <paramref name="data"/> isn't a BIF file.</summary>
    public static BifIndex? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64 || !data[..8].SequenceEqual(Magic)) return null;
        uint? Word(ReadOnlySpan<byte> bytes, int at) =>
            at + 4 <= bytes.Length ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]) : null;
        if (Word(data, 12) is not { } count || Word(data, 16) is not { } multiplier) return null;
        // Milliseconds per timestamp unit; 0 means 1000.
        var unit = (multiplier == 0 ? 1000 : multiplier) / 1000.0;
        // The index: a time and an offset per frame, then an end entry whose
        // offset is where the last frame stops.
        var entries = new List<(uint Time, long Offset)>();
        for (long index = 0; index <= count; index++)
        {
            var at = 64 + index * 8;
            if (at + 8 > data.Length) return null;
            entries.Add((Word(data, (int)at)!.Value, Word(data, (int)at + 4)!.Value));
        }
        var frames = new List<(double, int, int)>();
        foreach (var (entry, next) in entries.Zip(entries.Skip(1)))
        {
            if (entry.Time == uint.MaxValue) continue;
            if (entry.Offset >= next.Offset || next.Offset > data.Length) return null;
            frames.Add((entry.Time * unit, (int)entry.Offset, (int)(next.Offset - entry.Offset)));
        }
        return frames.Count > 0 ? new BifIndex(frames) : null;
    }

    /// <summary>The frame shown at <paramref name="seconds"/>: the last one starting at or before it.</summary>
    public (int Offset, int Length)? FrameAt(double seconds)
    {
        if (!double.IsFinite(seconds)) return null;
        var after = Frames.Count;
        for (var i = 0; i < Frames.Count; i++)
        {
            if (Frames[i].Seconds > seconds) { after = i; break; }
        }
        var frame = Frames[Math.Max(after - 1, 0)];
        return (frame.Offset, frame.Length);
    }
}
