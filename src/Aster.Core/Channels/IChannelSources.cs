using Aster.Core.Jellyfin;

namespace Aster.Core.Channels;

/// <summary>
/// A server that fills a channel from its genres, networks and collections
/// by itself (Plex); Jellyfin's and Emby's are looked up in the loader, by
/// their ids.
/// </summary>
public interface IChannelSources
{
    /// <summary>
    /// The movies and shows a channel's genres, networks and collections
    /// hold, narrowed by its libraries, type, years and ratings
    /// (<see cref="ChannelLoader.Passes"/>); shows not yet opened into episodes.
    /// </summary>
    Task<IReadOnlyList<BaseItem>> ChannelSourceTitlesAsync(ChannelTags tags, CancellationToken ct = default);

    /// <summary>All of a show's episodes, in order.</summary>
    Task<IReadOnlyList<BaseItem>> ChannelSeriesEpisodesAsync(string seriesId, CancellationToken ct = default);

    /// <summary>What a new channel can be made from, and its filters chosen from, by name: for Edit Channels.</summary>
    Task<ChannelSourceNames> ChannelSourceNamesAsync(CancellationToken ct = default);
}

/// <summary>The genres, networks, collections, libraries and ratings a server's video libraries have, by name.</summary>
public sealed record ChannelSourceNames(
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> Collections,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<string> Ratings);
