// What the app's screens need from a server, whichever it is: libraries,
// titles, the Home rows, a show's seasons and episodes, search, watched and
// favourite, pictures, and playing. Jellyfin and Emby answer through
// JellyfinClient; another server (Plex) through a client of its own that
// turns its answers into the same BaseItems. What only Jellyfin has (music,
// Live TV, channels, the plugins) is asked of JellyfinClient itself. A port
// of the Apple app's MediaServer protocol.

using Aster.Core.Browsing;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;

namespace Aster.Core.Server;

public interface IMediaServer
{
    ServerKind Kind { get; }

    /// <summary>What it can do beyond browsing and playing, so the app shows only what works there.</summary>
    ServerCapabilities Capabilities { get; }

    Uri BaseUrl { get; }
    string? UserId { get; }

    /// <summary>A picture's (or other file's) bytes; <paramref name="fresh"/> skips any cached copy.</summary>
    Task<byte[]> DataAsync(Uri url, bool fresh = false, CancellationToken ct = default);

    /// <summary>
    /// The address of one of a title's pictures. <paramref name="tag"/> is
    /// whatever the server put in the item for it: Jellyfin's image tag,
    /// another server's own reference.
    /// </summary>
    Uri ImageUrl(string itemId, ImageKind kind, string? tag = null, int? index = null, int? maxWidth = null, int? maxHeight = null, int quality = 90);

    // ---- Browsing ----

    /// <summary>The user's libraries.</summary>
    Task<IReadOnlyList<BaseItem>> UserViewsAsync(CancellationToken ct = default);

    Task<BaseItem> ItemAsync(string id, CancellationToken ct = default);
    Task<ItemsResult> ItemsAsync(ItemQuery query, CancellationToken ct = default);

    /// <summary>The newest titles, in a library or all of them; <paramref name="includeTypes"/> as Jellyfin names them ("Movie,Series").</summary>
    Task<IReadOnlyList<BaseItem>> LatestAsync(string? parentId, int limit = 20, string? includeTypes = null, CancellationToken ct = default);

    Task<IReadOnlyList<BaseItem>> ResumeAsync(int limit = 20, string? parentId = null, CancellationToken ct = default);
    Task<IReadOnlyList<BaseItem>> NextUpAsync(int limit = 20, string? seriesId = null, string? parentId = null, CancellationToken ct = default);
    Task<IReadOnlyList<BaseItem>> SeasonsAsync(string seriesId, CancellationToken ct = default);
    Task<IReadOnlyList<BaseItem>> EpisodesAsync(string seriesId, string? seasonId, CancellationToken ct = default);
    Task<IReadOnlyList<BaseItem>> SimilarAsync(string id, int limit = 20, CancellationToken ct = default);
    Task<IReadOnlyList<BaseItem>> SpecialFeaturesAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<BaseItem>> SearchAsync(string term, int limit = 60, CancellationToken ct = default);

    /// <summary>Every episode of a series in airing order, with its chapters: what finding openings and endings reads.</summary>
    Task<IReadOnlyList<BaseItem>> EpisodesWithChaptersAsync(string seriesId, CancellationToken ct = default);

    Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default);
    Task SetPlayedAsync(string id, bool played, CancellationToken ct = default);

    /// <summary>Ends this device's sign-in on the server, where it has one.</summary>
    Task LogoutAsync();

    /// <summary>The libraries among <paramref name="views"/> that hold anime, for the Anime tab.</summary>
    Task<IReadOnlyList<BaseItem>> AnimeLibrariesAsync(IEnumerable<BaseItem> views, CancellationToken ct = default);

    /// <summary>
    /// Every title of these types ("Series,Movie") in these libraries: anime,
    /// kept out of the TV Shows and Movies grids.
    /// </summary>
    Task<IReadOnlySet<string>> ItemIdsAsync(IEnumerable<string> libraryIds, string types, CancellationToken ct = default);

    // ---- Playing, in the shapes the player was written for: Jellyfin's
    // playback info and reports. Other servers answer in the same shapes. ----

    /// <summary>How the item can be played: its files and tracks, and the server's converted stream when it isn't played as it is.</summary>
    Task<PlaybackInfoResponse> PlaybackInfoAsync(
        string itemId,
        long startTicks,
        int maxBitrate,
        string? mediaSourceId = null,
        int? audioStreamIndex = null,
        int? subtitleStreamIndex = null,
        bool allowDirect = true,
        bool mostCompatible = false,
        DeviceProfile? profile = null,
        CancellationToken ct = default);

    /// <summary>The file itself, for playing as it is.</summary>
    Uri DirectStreamUrl(string itemId, string mediaSourceId, string? playSessionId);

    /// <summary>A video's file as it is, with no playback session: an opening looping behind its page.</summary>
    Uri VideoStreamUrl(string itemId);

    /// <summary>A path from the server's playback info (a converted stream) as a full address the player can open.</summary>
    Uri? AbsoluteUrl(string serverRelative);

    Task ReportStartAsync(PlaybackReport report);

    /// <summary>Also saves the resume point: when due, at once, or never.</summary>
    Task ReportProgressAsync(PlaybackReport report, ResumePoints.Saving saving = ResumePoints.Saving.WhenDue);

    Task ReportStoppedAsync(PlaybackReport report);

    /// <summary>Sets where the item was left, leaving its watched state alone.</summary>
    Task SaveResumePositionAsync(string itemId, long ticks, CancellationToken ct = default);

    /// <summary>Ends the server's conversion for a playback that's over.</summary>
    Task StopTranscodingAsync(string playSessionId);

    /// <summary>A text subtitle track's cues, for the app to draw.</summary>
    Task<IReadOnlyList<SubtitleCue>> SubtitleCuesAsync(string itemId, string mediaSourceId, int streamIndex, CancellationToken ct = default);

    /// <summary>A text subtitle track as a file in <paramref name="format"/> ("ass", "srt"), for the player to load beside a stream.</summary>
    Uri SubtitleFileUrl(string itemId, string mediaSourceId, int streamIndex, string format);

    /// <summary>The intro, credits and the like, to skip or offer the next episode at.</summary>
    Task<IReadOnlyList<MediaSegment>> MediaSegmentsAsync(string itemId, CancellationToken ct = default);

    Uri TrickplayTileUrl(string itemId, int width, int index, string mediaSourceId);
    Uri ChapterImageUrl(string itemId, int index, long? stamp);
    Uri ChapterImageUrl(string itemId, int index, string tag);
}

/// <summary>What a server can do beyond browsing and playing, so the app shows only what works there.</summary>
[Flags]
public enum ServerCapabilities
{
    None = 0,
    QuickConnect = 1 << 0,

    /// <summary>Aster's Jellyfin plugins: channel groups, disc menus, OP/ED.</summary>
    Plugins = 1 << 1,

    LiveTV = 1 << 2,
    Music = 1 << 3,
    Audiobooks = 1 << 4,

    /// <summary>Channels made from tagged playlists.</summary>
    Channels = 1 << 5,

    Downloads = 1 << 6,
    Upcoming = 1 << 7,
    Playlists = 1 << 8,
    Favorites = 1 << 9,
    Trickplay = 1 << 10,

    /// <summary>Settings kept on the server, to follow the user between devices.</summary>
    SettingsSync = 1 << 11,

    Lyrics = 1 << 12,
}

/// <summary>
/// A request for titles in the app's own terms, which each server turns into
/// its own query: Jellyfin and Emby into <c>/Items</c> parameters (below),
/// another server into its own filters. Screens ask with this rather than
/// with one server's parameter names.
/// </summary>
public sealed record ItemQuery
{
    /// <summary>Within one library, collection or folder; null for every library.</summary>
    public string? ParentId { get; init; }

    /// <summary>Only these kinds; empty for any.</summary>
    public IReadOnlyList<ItemKind> Kinds { get; init; } = [];

    /// <summary>Through sub-folders too (a library's shows, not its folders). Null leaves it to the server.</summary>
    public bool? Recursive { get; init; }

    public LibrarySort? Sort { get; init; }

    /// <summary>Null leaves the order to the server; the sorts' own direction otherwise.</summary>
    public bool? Descending { get; init; }

    public bool FavoritesOnly { get; init; }

    /// <summary>Titles this person is in.</summary>
    public string? PersonId { get; init; }

    /// <summary>Exactly these titles.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    public int? StartIndex { get; init; }
    public int? Limit { get; init; }

    /// <summary>The same query, one page of it.</summary>
    public ItemQuery Page(int start, int size) => this with { StartIndex = start, Limit = size };

    /// <summary>The query as Jellyfin's (and Emby's) <c>/Items</c> parameters.</summary>
    public Dictionary<string, string?> JellyfinParameters()
    {
        var query = new Dictionary<string, string?>();
        if (ParentId is not null) query["parentId"] = ParentId;
        if (Kinds.Count > 0) query["includeItemTypes"] = string.Join(",", Kinds);
        if (Recursive is { } recursive) query["recursive"] = recursive ? "true" : "false";
        if (Sort is { } sort)
        {
            query["sortBy"] = sort switch
            {
                LibrarySort.DateAdded => "DateCreated,SortName",
                LibrarySort.ReleaseDate => "PremiereDate,SortName",
                LibrarySort.Rating => "CommunityRating,SortName",
                _ => "SortName",
            };
        }
        if (Descending is { } descending) query["sortOrder"] = descending ? "Descending" : "Ascending";
        if (FavoritesOnly) query["filters"] = "IsFavorite";
        if (PersonId is not null) query["personIds"] = PersonId;
        if (Ids.Count > 0) query["ids"] = string.Join(",", Ids);
        if (StartIndex is { } start) query["startIndex"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Limit is { } limit) query["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return query;
    }
}

/// <summary>A title's pictures, from the references any server puts in its items.</summary>
public static class MediaServerPictures
{
    extension(IMediaServer server)
    {
        /// <summary>
        /// Best landscape picture for a lockup: the item's own backdrop or thumb,
        /// then its parent's, then the primary image for episodes.
        /// </summary>
        public Uri? LandscapeUrl(BaseItem item, int width = 800)
        {
            if (item.Kind == ItemKind.Episode && Tag(item.ImageTags, "Primary") is { } episodeTag)
                return server.ImageUrl(item.Id, ImageKind.Primary, episodeTag, maxWidth: width);
            if (Tag(item.ImageTags, "Thumb") is { } thumb)
                return server.ImageUrl(item.Id, ImageKind.Thumb, thumb, maxWidth: width);
            if (item.BackdropImageTags?.FirstOrDefault() is { } backdrop)
                return server.ImageUrl(item.Id, ImageKind.Backdrop, backdrop, index: 0, maxWidth: width);
            if (item.ParentThumbItemId is { } thumbParent && item.ParentThumbImageTag is { } parentThumb)
                return server.ImageUrl(thumbParent, ImageKind.Thumb, parentThumb, maxWidth: width);
            if (item.ParentBackdropItemId is { } backdropParent && item.ParentBackdropImageTags?.FirstOrDefault() is { } parentBackdrop)
                return server.ImageUrl(backdropParent, ImageKind.Backdrop, parentBackdrop, index: 0, maxWidth: width);
            if (item.SeriesId is { } series && item.SeriesThumbImageTag is { } seriesThumb)
                return server.ImageUrl(series, ImageKind.Thumb, seriesThumb, maxWidth: width);
            if (Tag(item.ImageTags, "Primary") is { } primary)
                return server.ImageUrl(item.Id, ImageKind.Primary, primary, maxWidth: width);
            return null;
        }

        public Uri? PosterUrl(BaseItem item, int width = 500, int quality = 90)
        {
            if (Tag(item.ImageTags, "Primary") is { } primary)
                return server.ImageUrl(item.Id, ImageKind.Primary, primary, maxWidth: width, quality: quality);
            if (item.AlbumId is { } album && item.AlbumPrimaryImageTag is { } albumTag)
                return server.ImageUrl(album, ImageKind.Primary, albumTag, maxWidth: width, quality: quality);
            if (item.SeriesId is { } series && item.SeriesPrimaryImageTag is { } seriesTag)
                return server.ImageUrl(series, ImageKind.Primary, seriesTag, maxWidth: width, quality: quality);
            if (item.ParentPrimaryImageItemId is { } parent && item.ParentPrimaryImageTag is { } parentTag)
                return server.ImageUrl(parent, ImageKind.Primary, parentTag, maxWidth: width, quality: quality);
            return null;
        }

        public Uri? BackdropUrl(BaseItem item, int width = 1920)
        {
            if (item.BackdropImageTags?.FirstOrDefault() is { } backdrop)
                return server.ImageUrl(item.Id, ImageKind.Backdrop, backdrop, index: 0, maxWidth: width);
            if (item.ParentBackdropItemId is { } parent && item.ParentBackdropImageTags?.FirstOrDefault() is { } parentBackdrop)
                return server.ImageUrl(parent, ImageKind.Backdrop, parentBackdrop, index: 0, maxWidth: width);
            return server.LandscapeUrl(item, width);
        }

        public Uri? LogoUrl(BaseItem item, int width = 800)
        {
            if (Tag(item.ImageTags, "Logo") is { } logo)
                return server.ImageUrl(item.Id, ImageKind.Logo, logo, maxWidth: width);
            if (item.ParentLogoItemId is { } parent && item.ParentLogoImageTag is { } parentLogo)
                return server.ImageUrl(parent, ImageKind.Logo, parentLogo, maxWidth: width);
            return null;
        }

        public Uri? PersonImageUrl(PersonInfo person, int width = 300) =>
            person.PrimaryImageTag is { } tag ? server.ImageUrl(person.Id, ImageKind.Primary, tag, maxWidth: width) : null;

        /// <summary>Whether the server can do all of <paramref name="capabilities"/>.</summary>
        public bool Can(ServerCapabilities capabilities) => (server.Capabilities & capabilities) == capabilities;
    }

    private static string? Tag(IReadOnlyDictionary<string, string>? tags, string kind) =>
        tags is not null && tags.TryGetValue(kind, out var tag) ? tag : null;
}
