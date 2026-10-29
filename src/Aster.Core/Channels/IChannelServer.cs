using Aster.Core.Jellyfin;

namespace Aster.Core.Channels;

/// <summary>
/// What Channels needs from a server: its video playlists with their channel
/// tags, a playlist's titles, and who's watching. Jellyfin and Emby keep the
/// tags as the playlist's tags; Plex, which has none on playlists, in its
/// description (see <see cref="ChannelTags.FromDescription"/>).
/// </summary>
public interface IChannelServer
{
    /// <summary>The user's playlists of one media type ("Video"), each with its channel tags in <see cref="BaseItem.Tags"/>.</summary>
    Task<IReadOnlyList<BaseItem>> PlaylistsAsync(string mediaType, CancellationToken ct = default);

    /// <summary>A playlist's titles, in its order.</summary>
    Task<IReadOnlyList<BaseItem>> PlaylistItemsAsync(string playlistId, CancellationToken ct = default);

    /// <summary>The user's name, for channels kept to some users (aster:user=), and their parental controls; null where the server has none to read.</summary>
    Task<(string Name, UserPolicy? Policy)> ChannelViewerAsync(CancellationToken ct = default);
}
