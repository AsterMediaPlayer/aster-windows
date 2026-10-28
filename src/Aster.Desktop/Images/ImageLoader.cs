using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using Avalonia.Media.Imaging;

namespace Aster.Desktop.Images;

/// <summary>
/// Pictures from the server, decoded once and shared. The bytes come
/// through the client's disk cache; the decoded pictures stay in memory,
/// the most recently used ones kept. The server sizes each picture to the
/// width asked for in its address, so it's decoded as it comes.
/// Used on the UI thread only.
/// </summary>
public static class ImageLoader
{
    /// <summary>The signed-in server, set at sign-in.</summary>
    public static IMediaServer? Client { get; set; }

    private const int Capacity = 400;
    private static readonly Dictionary<Uri, LinkedListNode<(Uri Url, Bitmap Bitmap)>> cache = [];
    private static readonly LinkedList<(Uri Url, Bitmap Bitmap)> recent = new();
    private static readonly Dictionary<Uri, Task<Bitmap?>> inFlight = [];

    public static Bitmap? Cached(Uri url)
    {
        if (!cache.TryGetValue(url, out var node)) return null;
        recent.Remove(node);
        recent.AddFirst(node);
        return node.Value.Bitmap;
    }

    /// <summary>The picture, or null when it couldn't be had.</summary>
    public static Task<Bitmap?> LoadAsync(Uri url)
    {
        if (Cached(url) is { } hit) return Task.FromResult<Bitmap?>(hit);
        if (inFlight.TryGetValue(url, out var pending)) return pending;
        var task = FetchAsync(url);
        inFlight[url] = task;
        return task;
    }

    /// <summary>Fetches pictures ahead, so they're waiting when shown.</summary>
    public static void Prefetch(IEnumerable<Uri?> urls)
    {
        foreach (var url in urls)
        {
            if (url is not null) _ = LoadAsync(url);
        }
    }

    private static async Task<Bitmap?> FetchAsync(Uri url)
    {
        try
        {
            // A picture written out in its address (a disc menu's data:image/png;base64,…).
            if (url.Scheme == "data")
            {
                var text = url.OriginalString;
                var inline = await Task.Run(() => Decode(Convert.FromBase64String(text[(text.IndexOf(',') + 1)..])));
                if (inline is not null) Remember(url, inline);
                return inline;
            }
            var client = Client;
            if (client is null) return null;
            var bitmap = await Task.Run(async () =>
            {
                var data = await client.DataAsync(url).ConfigureAwait(false);
                // A damaged copy in the disk cache (cut short when the app
                // stopped as it was written) would come back every time:
                // drop it and fetch the picture again, once.
                return Decode(data) ?? Decode(await client.DataAsync(url, fresh: true).ConfigureAwait(false));
            });
            if (bitmap is not null) Remember(url, bitmap);
            return bitmap;
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException or IOException or FormatException)
        {
            return null;
        }
        finally
        {
            inFlight.Remove(url);
        }
    }

    private static Bitmap? Decode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Remember(Uri url, Bitmap bitmap)
    {
        if (cache.ContainsKey(url)) return;
        cache[url] = recent.AddFirst((url, bitmap));
        // A picture let go here may still be on screen, so it isn't disposed;
        // the collector frees it once nothing shows it.
        while (cache.Count > Capacity && recent.Last is { } oldest)
        {
            recent.RemoveLast();
            cache.Remove(oldest.Value.Url);
        }
    }
}
