using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Aster.Desktop.Images;

/// <summary>
/// The pictures shown above the timeline while scrubbing, from Jellyfin's
/// trickplay images: sheets of small frames, one every few seconds, for the
/// whole title. The port's counterpart of the Apple app's
/// ScrubPreviewSource. A sheet decodes to some 20 MB, so only the few last
/// used are kept, apart from the shared image cache. Used on the UI thread only.
/// </summary>
public sealed class ScrubPreviewSource
{
    public const int PreviewWidth = 320;
    private const int SheetsKept = 3;

    private readonly IMediaServer client;
    private readonly string itemId;
    private readonly string mediaSourceId;
    private readonly TrickplayInfo info;
    private readonly Dictionary<int, Task<Bitmap?>> sheets = [];
    /// <summary>Sheet numbers, the most recently used last.</summary>
    private readonly List<int> recent = [];

    private ScrubPreviewSource(IMediaServer client, string itemId, string mediaSourceId, TrickplayInfo info)
    {
        this.client = client;
        this.itemId = itemId;
        this.mediaSourceId = mediaSourceId;
        this.info = info;
    }

    /// <summary>Null when the item has no trickplay images for this file.</summary>
    public static ScrubPreviewSource? For(IMediaServer client, BaseItem item, string? mediaSourceId)
    {
        if (item.Trickplay is not { Count: > 0 } trickplay) return null;
        var sourceId = mediaSourceId is not null && trickplay.ContainsKey(mediaSourceId)
            ? mediaSourceId
            : trickplay.Keys.Order(StringComparer.Ordinal).First();
        if (TrickplayInfo.Best(trickplay[sourceId], PreviewWidth) is not { } info) return null;
        return new ScrubPreviewSource(client, item.Id, sourceId, info);
    }

    /// <summary>The frame for a moment, or null when its sheet couldn't be had.</summary>
    public async Task<IImage?> FrameAtAsync(double seconds)
    {
        if (info.CellAt(seconds) is not { } cell || await SheetAsync(cell.Sheet) is not { } sheet) return null;
        var rect = info.RectOf(cell);
        var size = sheet.PixelSize;
        var crop = new PixelRect(rect.X, rect.Y, rect.Width, rect.Height).Intersect(new PixelRect(size));
        return crop.Width > 0 && crop.Height > 0 ? new CroppedBitmap(sheet, crop) : null;
    }

    private Task<Bitmap?> SheetAsync(int index)
    {
        recent.Remove(index);
        recent.Add(index);
        if (!sheets.TryGetValue(index, out var sheet)) sheets[index] = sheet = FetchAsync(index);
        while (recent.Count > SheetsKept)
        {
            sheets.Remove(recent[0]);
            recent.RemoveAt(0);
        }
        return sheet;
    }

    private async Task<Bitmap?> FetchAsync(int index)
    {
        var url = client.TrickplayTileUrl(itemId, info.Width, index, mediaSourceId);
        try
        {
            return await Task.Run(async () =>
            {
                using var stream = new MemoryStream(await client.DataAsync(url).ConfigureAwait(false));
                return new Bitmap(stream);
            });
        }
        catch (Exception)
        {
            // A sheet that won't load leaves the preview showing just the time.
            return null;
        }
    }
}
