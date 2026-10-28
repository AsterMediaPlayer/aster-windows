using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Server;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// Which libraries hold anime, found once per sign-in as the Apple app's
/// session does: the TV and movie libraries Shoko matched, every title in
/// them (so Movies and TV Shows can leave them out), and the music video
/// library of the OP/ED plugin's openings and endings.
/// </summary>
public sealed record AnimeCatalog(IReadOnlyList<BaseItem> Libraries, IReadOnlySet<string> ItemIds, IReadOnlyList<BaseItem> MusicVideoLibraries)
{
    public static readonly AnimeCatalog Empty = new([], new HashSet<string>(), []);

    public bool HasAnime => Libraries.Count > 0;

    public static async Task<AnimeCatalog> FindAsync(IMediaServer client)
    {
        try
        {
            var views = await client.UserViewsAsync();
            var libraries = await client.AnimeLibrariesAsync(views);
            var ids = client.ItemIdsAsync(libraries.Select(l => l.Id), "Series,Movie");
            // The OP/ED plugin's music videos, on Jellyfin.
            var opEd = client is JellyfinClient jellyfin && jellyfin.Can(ServerCapabilities.Plugins)
                && (await jellyfin.OpEdLibrariesAsync())?.VideosLibraryId is { } videos ? PlainId(videos) : null;
            var musicVideos = views.Where(v => v.CollectionType == "musicvideos" && PlainId(v.Id) == opEd).ToList();
            return new AnimeCatalog(libraries, await ids, musicVideos);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            return Empty;
        }
    }

    /// <summary>An id as Jellyfin writes it either way: lowercase, without dashes.</summary>
    private static string PlainId(string id) => id.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);

    /// <summary>
    /// The newest series and films across the anime libraries, for Home's
    /// shelf and the Anime tab. Jellyfin reports a season when new episodes
    /// land in it; its series is shown instead, and each show once.
    /// </summary>
    public static async Task<IReadOnlyList<BaseItem>> RecentlyAddedAsync(IMediaServer client, IReadOnlyList<BaseItem> libraries, int limit = 20)
    {
        var all = (await Task.WhenAll(libraries.Select(l => OrEmpty(client.LatestAsync(l.Id, limit))))).SelectMany(x => x);
        var newest = all.OrderByDescending(i => i.DateCreated ?? "", StringComparer.Ordinal).ToList();
        var seriesIds = newest.Where(i => i.Kind == ItemKind.Season).Select(i => i.SeriesId).OfType<string>().Distinct().ToList();
        var series = new Dictionary<string, BaseItem>();
        if (seriesIds.Count > 0)
        {
            try
            {
                foreach (var found in (await client.ItemsAsync(new ItemQuery { Ids = seriesIds })).Items) series.TryAdd(found.Id, found);
            }
            catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
        }
        var shown = new List<BaseItem>();
        foreach (var item in newest)
        {
            var entry = item.Kind == ItemKind.Season && item.SeriesId is { } id && series.TryGetValue(id, out var show) ? show : item;
            if (shown.All(s => s.Id != entry.Id)) shown.Add(entry);
        }
        return shown.Take(limit).ToList();
    }

    /// <summary>
    /// <paramref name="fetch"/> for each library side by side, joined in
    /// library order, skipping any that fail.
    /// </summary>
    public static async Task<IReadOnlyList<BaseItem>> MergedAsync(IEnumerable<string> libraryIds, Func<string, Task<IReadOnlyList<BaseItem>>> fetch) =>
        (await Task.WhenAll(libraryIds.Select(id => OrEmpty(fetch(id))))).SelectMany(x => x).ToList();

    /// <summary>The items, or none if the request fails.</summary>
    public static async Task<IReadOnlyList<BaseItem>> OrEmpty<T>(Task<T> request) where T : IReadOnlyList<BaseItem>
    {
        try { return await request; }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return []; }
    }
}
