// Views of the Jellyfin REST API. Property names on the wire are PascalCase,
// as C# names are, so they need no mapping; GUIDs arrive as 32 hex digits
// without hyphens. Derived helpers are extension members, so they are never
// serialized back to the server.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aster.Core.Playback;

namespace Aster.Core.Jellyfin;

public static class JellyfinJson
{
    /// <summary>
    /// Nulls are left out when writing, as Swift's Codable does, so an
    /// unset field never overwrites one on the server.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---- Server and users ----

public sealed record PublicSystemInfo
{
    public string? ServerName { get; init; }
    public string? Version { get; init; }
    public string? Id { get; init; }
    public string? LocalAddress { get; init; }

    /// <summary>"Jellyfin Server" from Jellyfin; Emby sends none.</summary>
    public string? ProductName { get; init; }

    /// <summary>Which server answered.</summary>
    public ServerKind Kind =>
        ProductName?.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase) == true ? ServerKind.Jellyfin : ServerKind.Emby;
}

/// <summary>
/// The two servers Aster signs in to. Jellyfin began as a fork of Emby, so
/// they share almost everything; where they differ (a few routes, and what
/// each has that the other doesn't) the client and the app ask this. The
/// Apple app's ServerKind.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerKind>))]
public enum ServerKind
{
    Jellyfin,
    Emby,
    Plex,
}

/// <summary>An id as text (Jellyfin) or a number (Emby's studios, genres and people), as text either way.</summary>
public sealed class FlexibleIdConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonTokenType.Null => null,
        _ => throw new JsonException($"An id can't be {reader.TokenType}."),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed record JellyfinUser
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? PrimaryImageTag { get; init; }
    public bool? HasPassword { get; init; }
    public bool? HasConfiguredPassword { get; init; }
    public UserConfiguration? Configuration { get; init; }
    public UserPolicy? Policy { get; init; }
}

/// <summary>What the server's administrator allows this user. Only what the app uses.</summary>
public sealed record UserPolicy
{
    /// <summary>"Allow media downloading" in the user's settings on the server.</summary>
    public bool? EnableContentDownloading { get; init; }

    /// <summary>An administrator can edit any item's metadata, a playlist's tags included.</summary>
    public bool? IsAdministrator { get; init; }
}

public sealed record AuthenticationResult
{
    public required JellyfinUser User { get; init; }
    public required string AccessToken { get; init; }
    public string? ServerId { get; init; }
}

public sealed record QuickConnectResult
{
    public bool Authenticated { get; init; }
    public required string Secret { get; init; }
    public required string Code { get; init; }
}

// ---- Items ----

public sealed record ItemsResult
{
    public IReadOnlyList<BaseItem> Items { get; init; } = [];
    public int? TotalRecordCount { get; init; }
}

/// <summary>Jellyfin's <c>{ Items: [...] }</c> wrapper for lists other than library items.</summary>
public sealed record ItemsEnvelope<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
}

public sealed record UserItemData
{
    public long? PlaybackPositionTicks { get; init; }
    public int? PlayCount { get; init; }
    public bool? IsFavorite { get; init; }
    public bool? Played { get; init; }
    public double? PlayedPercentage { get; init; }
    public int? UnplayedItemCount { get; init; }

    /// <summary>ISO 8601, so these sort as text.</summary>
    public string? LastPlayedDate { get; init; }
}

public sealed record PersonInfo
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Role { get; init; }
    public string? Type { get; init; }
    public string? PrimaryImageTag { get; init; }
}

public sealed record NameGuidPair
{
    public string? Name { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? Id { get; init; }
}

public sealed record ChapterInfo
{
    public long StartPositionTicks { get; init; }
    public string? Name { get; init; }
    public string? ImageTag { get; init; }

    /// <summary>Emby's mark on a chapter: Chapter, or IntroStart, IntroEnd and CreditsStart (its stand-in for media segments).</summary>
    public string? MarkerType { get; init; }
}

public sealed record MediaStream
{
    public string? Codec { get; init; }
    public string? Type { get; init; }
    public int? Index { get; init; }
    public string? Language { get; init; }
    public string? DisplayTitle { get; init; }
    public bool? IsDefault { get; init; }
    public bool? IsExternal { get; init; }
    public bool? IsForced { get; init; }
    public string? Title { get; init; }
    public string? DeliveryMethod { get; init; }
    public string? DeliveryUrl { get; init; }
    public bool? IsTextSubtitleStream { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Channels { get; init; }
    public string? VideoRangeType { get; init; }
}

public static class MediaStreamExtensions
{
    extension(MediaStream stream)
    {
        /// <summary>
        /// How a track is named in menus: the server's display title, else
        /// language, codec and whether it is a separate file.
        /// </summary>
        public string TrackTitle
        {
            get
            {
                if (!string.IsNullOrEmpty(stream.DisplayTitle)) return stream.DisplayTitle;
                var parts = new List<string>();
                if (stream.Language is { } language) parts.Add(language.ToUpperInvariant());
                if (stream.Codec is { } codec) parts.Add(codec.ToUpperInvariant());
                if (stream.IsExternal == true) parts.Add("External");
                return parts.Count == 0 ? $"Track {stream.Index ?? 0}" : string.Join(" · ", parts);
            }
        }
    }
}

/// <summary>
/// How subtitles are chosen when nothing is picked for a title. Mirrors the
/// server's user setting so the web app and this one agree. The names are
/// the server's values.
/// </summary>
public enum SubtitleMode
{
    Default,
    Always,
    OnlyForced,
    None,
    Smart,
}

public static class SubtitleModeExtensions
{
    extension(SubtitleMode mode)
    {
        public string Title => mode switch
        {
            SubtitleMode.Always => "Always on",
            SubtitleMode.OnlyForced => "Only forced",
            SubtitleMode.None => "Off",
            SubtitleMode.Smart => "Smart",
            _ => "Default",
        };
    }
}

/// <summary>
/// The user's server-side settings. Fields the app doesn't name are kept in
/// <see cref="Extra"/>, so posting a change back never clears them.
/// </summary>
public sealed record UserConfiguration
{
    public string? AudioLanguagePreference { get; init; }
    public bool? PlayDefaultAudioTrack { get; init; }
    public string? SubtitleLanguagePreference { get; init; }
    public bool? DisplayMissingEpisodes { get; init; }
    public IReadOnlyList<string>? GroupedFolders { get; init; }
    public string? SubtitleMode { get; init; }
    public bool? DisplayCollectionsView { get; init; }
    public bool? EnableLocalPassword { get; init; }
    public IReadOnlyList<string>? OrderedViews { get; init; }
    public IReadOnlyList<string>? LatestItemsExcludes { get; init; }
    public IReadOnlyList<string>? MyMediaExcludes { get; init; }
    public bool? HidePlayedInLatest { get; init; }
    public bool? RememberAudioSelections { get; init; }
    public bool? RememberSubtitleSelections { get; init; }
    public bool? EnableNextEpisodeAutoPlay { get; init; }
    public string? CastReceiverId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>What the player should choose when a title has several tracks.</summary>
public sealed record PlaybackPreferences
{
    public string? AudioLanguage { get; init; }
    public string? SubtitleLanguage { get; init; }
    public SubtitleMode SubtitleMode { get; init; } = SubtitleMode.Default;
}

public sealed record MediaSourceInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Container { get; init; }
    public long? RunTimeTicks { get; init; }
    public bool? SupportsDirectPlay { get; init; }
    public bool? SupportsDirectStream { get; init; }
    public bool? SupportsTranscoding { get; init; }
    public string? TranscodingUrl { get; init; }
    public string? TranscodingContainer { get; init; }
    public string? TranscodingSubProtocol { get; init; }
    public IReadOnlyList<MediaStream>? MediaStreams { get; init; }
    public int? Bitrate { get; init; }
    public string? Protocol { get; init; }
    public string? Path { get; init; }
    public bool? IsInfiniteStream { get; init; }
    public string? LiveStreamId { get; init; }
    public bool? RequiresOpening { get; init; }
    public int? DefaultAudioStreamIndex { get; init; }
    public int? DefaultSubtitleStreamIndex { get; init; }

    /// <summary>Files embedded in the container, such as the fonts an ASS track uses.</summary>
    public IReadOnlyList<MediaAttachment>? MediaAttachments { get; init; }

    /// <summary>The file's size in bytes.</summary>
    public long? Size { get; init; }
}

/// <summary>A file inside a video's container, e.g. an MKV's fonts.</summary>
public sealed record MediaAttachment
{
    public int Index { get; init; }
    public string? FileName { get; init; }
    public string? MimeType { get; init; }
}

public static class MediaAttachmentExtensions
{
    extension(MediaAttachment attachment)
    {
        /// <summary>Whether this is a font a subtitle track can use.</summary>
        public bool IsFont
        {
            get
            {
                if (attachment.MimeType?.Contains("font", StringComparison.OrdinalIgnoreCase) == true) return true;
                var name = attachment.FileName ?? "";
                return name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

public sealed record PlaybackInfoResponse
{
    public IReadOnlyList<MediaSourceInfo> MediaSources { get; init; } = [];
    public string? PlaySessionId { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record RemoteTrailer
{
    public string? Url { get; init; }
    public string? Name { get; init; }
}

/// <summary>The subset of Jellyfin's BaseItemDto the app renders.</summary>
public sealed record BaseItem
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? OriginalTitle { get; init; }
    public string? Type { get; init; }
    public string? MediaType { get; init; }
    public string? CollectionType { get; init; }
    public string? Overview { get; init; }
    public IReadOnlyList<string>? Taglines { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public IReadOnlyList<NameGuidPair>? Studios { get; init; }
    public IReadOnlyList<PersonInfo>? People { get; init; }
    public int? ProductionYear { get; init; }

    /// <summary>
    /// What Jellyfin sorts the title by: lowercased, without a leading
    /// "The", "A" or "An", numbers padded ("0000000013 ghosts of …").
    /// </summary>
    public string? SortName { get; init; }

    public string? PremiereDate { get; init; }
    public string? EndDate { get; init; }
    public string? Status { get; init; }
    public string? OfficialRating { get; init; }
    public double? CommunityRating { get; init; }
    public double? CriticRating { get; init; }
    public long? RunTimeTicks { get; init; }
    public int? IndexNumber { get; init; }
    public int? ParentIndexNumber { get; init; }
    public int? ChildCount { get; init; }
    public int? RecursiveItemCount { get; init; }
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public string? SeasonId { get; init; }
    public string? SeasonName { get; init; }
    public string? ParentId { get; init; }
    public IReadOnlyDictionary<string, string>? ImageTags { get; init; }
    public IReadOnlyList<string>? BackdropImageTags { get; init; }
    public string? ParentBackdropItemId { get; init; }
    public IReadOnlyList<string>? ParentBackdropImageTags { get; init; }
    public string? ParentThumbItemId { get; init; }
    public string? ParentThumbImageTag { get; init; }
    public string? ParentLogoItemId { get; init; }
    public string? ParentLogoImageTag { get; init; }

    /// <summary>
    /// The file's container, as the server probed it ("mp3", "flac",
    /// "mov,mp4,m4a,…"); not given for every kind of item.
    /// </summary>
    public string? Container { get; init; }

    public string? ParentPrimaryImageItemId { get; init; }
    public string? ParentPrimaryImageTag { get; init; }
    public string? SeriesPrimaryImageTag { get; init; }
    public string? SeriesThumbImageTag { get; init; }
    public double? PrimaryImageAspectRatio { get; init; }
    public UserItemData? UserData { get; init; }
    public IReadOnlyList<ChapterInfo>? Chapters { get; init; }

    /// <summary>
    /// Trickplay images for scrubbing, by file (media source) id and then
    /// by width; only on a title the server has made them for.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrickplayInfo>>? Trickplay { get; init; }

    /// <summary>The item's tags in Jellyfin; a playlist's say whether it's a channel.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    public IReadOnlyList<MediaSourceInfo>? MediaSources { get; init; }
    public IReadOnlyList<MediaStream>? MediaStreams { get; init; }
    public IReadOnlyDictionary<string, string>? ProviderIds { get; init; }
    public IReadOnlyList<RemoteTrailer>? RemoteTrailers { get; init; }
    public string? LocationType { get; init; }
    public bool? IsFolder { get; init; }
    public string? DateCreated { get; init; }

    // Live TV: channels, programs, recordings.
    public string? ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public string? ChannelNumber { get; init; }
    public string? ChannelType { get; init; }
    public string? ChannelPrimaryImageTag { get; init; }
    public string? StartDate { get; init; }
    public string? EpisodeTitle { get; init; }
    public bool? IsSeries { get; init; }
    public bool? IsMovie { get; init; }
    public bool? IsSports { get; init; }
    public bool? IsNews { get; init; }
    public bool? IsKids { get; init; }
    public bool? IsLive { get; init; }
    public bool? IsPremiere { get; init; }
    public bool? IsRepeat { get; init; }
    public string? TimerId { get; init; }
    public string? SeriesTimerId { get; init; }
    public ChannelProgram? CurrentProgram { get; init; }

    // Music: albums, artists and songs.
    public string? Album { get; init; }
    public string? AlbumId { get; init; }
    public string? AlbumArtist { get; init; }
    public IReadOnlyList<NameGuidPair>? AlbumArtists { get; init; }
    public IReadOnlyList<string>? Artists { get; init; }
    public IReadOnlyList<NameGuidPair>? ArtistItems { get; init; }
    public string? AlbumPrimaryImageTag { get; init; }
    public int? AlbumCount { get; init; }
    public int? SongCount { get; init; }

    /// <summary>This entry's id inside a playlist, needed to remove or move it.</summary>
    public string? PlaylistItemId { get; init; }
}

public sealed record PlaylistCreationResult
{
    public required string Id { get; init; }
}

/// <summary>The program a channel is airing now, as embedded in a channel item.</summary>
public sealed record ChannelProgram
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public string? Overview { get; init; }
    public IReadOnlyDictionary<string, string>? ImageTags { get; init; }
    public bool? IsMovie { get; init; }
    public bool? IsSports { get; init; }
    public bool? IsNews { get; init; }
    public bool? IsKids { get; init; }
}

public static class ChannelProgramExtensions
{
    extension(ChannelProgram program)
    {
        public DateTimeOffset? Start => JellyfinDate.Parse(program.StartDate);
        public DateTimeOffset? End => JellyfinDate.Parse(program.EndDate);

        public double? AiredFraction => JellyfinDate.Fraction(program.Start, program.End, DateTimeOffset.UtcNow);
    }
}

public static class MusicFormat
{
    /// <summary>"m:ss", or "h:mm:ss" past an hour.</summary>
    public static string Clock(double seconds)
    {
        var total = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }

    /// <summary>"1 hr 12 min" or "42 min" for an album or playlist.</summary>
    public static string? Length(long ticks)
    {
        if (ticks <= 0) return null;
        var minutes = (int)(ticks / 600_000_000);
        return minutes >= 60 ? $"{minutes / 60} hr {minutes % 60} min" : $"{Math.Max(minutes, 1)} min";
    }
}

// ---- Live TV ----

public sealed record LiveTVInfo
{
    public bool? IsEnabled { get; init; }
    public IReadOnlyList<string>? EnabledUsers { get; init; }
}

/// <summary>A scheduled or in-progress recording of one program.</summary>
public sealed record TimerInfo
{
    public required string Id { get; init; }
    public string? ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public string? ProgramId { get; init; }
    public string? Name { get; init; }
    public string? Overview { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public string? Status { get; init; }
    public string? SeriesTimerId { get; init; }
    public BaseItem? ProgramInfo { get; init; }
}

/// <summary>
/// A series recording rule. Fields the app doesn't name are kept in
/// <see cref="Extra"/>, so changing one option never resets the others.
/// </summary>
public sealed record SeriesTimerInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Overview { get; init; }
    public string? ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public string? ChannelPrimaryImageTag { get; init; }
    public string? ProgramId { get; init; }
    public string? ExternalId { get; init; }
    public string? ExternalChannelId { get; init; }
    public string? ExternalProgramId { get; init; }
    public string? ServiceName { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public bool? RecordAnyChannel { get; init; }
    public bool? RecordAnyTime { get; init; }
    public bool? RecordNewOnly { get; init; }
    public bool? SkipEpisodesInLibrary { get; init; }
    public int? KeepUpTo { get; init; }
    public string? KeepUntil { get; init; }
    public int? Priority { get; init; }
    public int? PrePaddingSeconds { get; init; }
    public int? PostPaddingSeconds { get; init; }
    public bool? IsPrePaddingRequired { get; init; }
    public bool? IsPostPaddingRequired { get; init; }
    public string? DayPattern { get; init; }
    public IReadOnlyList<string>? Days { get; init; }
    public IReadOnlyDictionary<string, string>? ImageTags { get; init; }
    public string? ParentThumbItemId { get; init; }
    public string? ParentThumbImageTag { get; init; }
    public string? ParentPrimaryImageItemId { get; init; }
    public string? ParentPrimaryImageTag { get; init; }
    public string? ParentBackdropItemId { get; init; }
    public IReadOnlyList<string>? ParentBackdropImageTags { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Jellyfin writes dates as ISO 8601 with seven fractional digits. Unlike
/// Apple's formatters, .NET reads those as they are, quickly enough that no
/// cache is needed.
/// </summary>
public static class JellyfinDate
{
    public static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static string Format(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>How far through <paramref name="start"/>…<paramref name="end"/> <paramref name="now"/> is, or null outside it.</summary>
    internal static double? Fraction(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now)
    {
        if (start is not { } s || end is not { } e || e <= s) return null;
        if (now < s || now >= e) return null;
        return (now - s) / (e - s);
    }
}

// ---- Derived presentation helpers ----

public static class BaseItemExtensions
{
    extension(BaseItem item)
    {
        /// <summary>
        /// Matched by Shoko, which also records the AniDB id: how the app tells
        /// anime apart from other shows and films. Series and movies carry these.
        /// </summary>
        public bool IsAnime =>
            item.ProviderIds?.Keys.Any(key => key == "AniDB" || key.StartsWith("Shoko", StringComparison.Ordinal)) == true;

        public DateTimeOffset? Start => JellyfinDate.Parse(item.StartDate);
        public DateTimeOffset? End => JellyfinDate.Parse(item.EndDate);

        public bool IsAiringNow
        {
            get
            {
                if (item.Start is not { } start || item.End is not { } end) return false;
                var now = DateTimeOffset.UtcNow;
                return start <= now && now < end;
            }
        }

        /// <summary>Fraction of a program that has already aired.</summary>
        public double? AiredFraction => JellyfinDate.Fraction(item.Start, item.End, DateTimeOffset.UtcNow);

        public bool IsProgram => item.Type == "Program";
        public bool IsChannel => item.Type == "TvChannel";
        public bool IsRecording => item.Type == "Recording";

        public ItemKind Kind =>
            !string.IsNullOrEmpty(item.Type) && Enum.TryParse<ItemKind>(item.Type, out var kind) && kind != ItemKind.Other
                ? kind
                : ItemKind.Other;

        public string DisplayTitle => item.Name ?? "Untitled";

        /// <summary>Identity for a row in a playlist, where the same song may appear twice.</summary>
        public string PlaylistRowId => item.PlaylistItemId ?? item.Id;

        /// <summary>"S1, E4" for episodes, null otherwise.</summary>
        public string? EpisodeCode
        {
            get
            {
                if (item.Kind != ItemKind.Episode || item.IndexNumber is not { } episode) return null;
                return item.ParentIndexNumber is { } season ? $"S{season}, E{episode}" : $"Episode {episode}";
            }
        }

        public int? RuntimeMinutes => item.RunTimeTicks is > 0 and { } ticks ? (int)(ticks / 600_000_000) : null;

        public string? RuntimeText => item.RuntimeMinutes is { } minutes
            ? minutes >= 60 ? $"{minutes / 60} hr {minutes % 60} min" : $"{minutes} min"
            : null;

        public string? RemainingText
        {
            get
            {
                if (item.RunTimeTicks is not ({ } ticks and > 0) || item.UserData?.PlaybackPositionTicks is not ({ } position and > 0)) return null;
                var left = (int)Math.Max(0, (ticks - position) / 600_000_000);
                return left >= 60 ? $"{left / 60} hr {left % 60} min left" : $"{left} min left";
            }
        }

        public double? ProgressFraction
        {
            get
            {
                if (item.RunTimeTicks is not ({ } ticks and > 0) || item.UserData?.PlaybackPositionTicks is not ({ } position and > 0)) return null;
                return Math.Clamp((double)position / ticks, 0, 1);
            }
        }

        public bool CanResume => (item.UserData?.PlaybackPositionTicks ?? 0) > 0;

        public bool IsPlayable => item.Kind switch
        {
            ItemKind.Movie or ItemKind.Episode or ItemKind.Video or ItemKind.MusicVideo
                or ItemKind.Trailer or ItemKind.TvChannel or ItemKind.Recording => true,
            _ => item.MediaType == "Video",
        };

        public string? YearText
        {
            get
            {
                if (item.Kind == ItemKind.Series && item.ProductionYear is { } year)
                {
                    if (item.Status == "Continuing") return $"{year}–";
                    if (item.EndDate is { Length: >= 4 } end
                        && int.TryParse(end.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var endYear)
                        && endYear != year)
                    {
                        return $"{year}–{endYear}";
                    }
                }
                return item.ProductionYear?.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The one-line metadata shown under a title.</summary>
        public IReadOnlyList<string> MetadataLine
        {
            get
            {
                var parts = new List<string>();
                if (item.YearText is { } year) parts.Add(year);
                if (item.OfficialRating is { } rating) parts.Add(rating);
                if (item.RuntimeText is { } runtime && item.Kind != ItemKind.Series) parts.Add(runtime);
                if (item.Genres is { Count: > 0 } genres) parts.Add(string.Join(", ", genres.Take(2)));
                return parts;
            }
        }

        private MediaStream? VideoStream =>
            (item.MediaStreams ?? item.MediaSources?.FirstOrDefault()?.MediaStreams)?.FirstOrDefault(s => s.Type == "Video");

        public bool IsHD => (item.VideoStream?.Width ?? 0) >= 1280;
        public bool Is4K => (item.VideoStream?.Width ?? 0) >= 3800;

        public IReadOnlyList<string> Directors =>
            (item.People ?? []).Where(p => p.Type == "Director").Select(p => p.Name).OfType<string>().ToList();

        public IReadOnlyList<string> Writers =>
            (item.People ?? []).Where(p => p.Type == "Writer").Select(p => p.Name).OfType<string>().ToList();

        public IReadOnlyList<PersonInfo> Cast =>
            (item.People ?? []).Where(p => p.Type is "Actor" or "GuestStar").ToList();

        /// <summary>Who performs a song or album, for display.</summary>
        public string? ArtistText
        {
            get
            {
                if (item.Artists is { Count: > 0 } artists) return string.Join(", ", artists);
                if (!string.IsNullOrEmpty(item.AlbumArtist)) return item.AlbumArtist;
                return item.AlbumArtists?.Select(a => a.Name).OfType<string>().FirstOrDefault();
            }
        }

        /// <summary>The album artist's id, for linking from an album to its artist page.</summary>
        public NameGuidPair? PrimaryArtist => item.AlbumArtists?.FirstOrDefault() ?? item.ArtistItems?.FirstOrDefault();

        /// <summary>"3:42" for a song.</summary>
        public string? TrackDurationText =>
            item.RunTimeTicks is > 0 and { } ticks ? MusicFormat.Clock(ticks / 10_000_000.0) : null;
    }
}

/// <summary>
/// An intro, credits or similar span inside an item, from the server's media
/// segment providers (Intro Skipper, chapter markers, EDL files).
/// </summary>
public sealed record MediaSegment
{
    public required string Id { get; init; }
    public string? ItemId { get; init; }
    public required string Type { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
}

public static class MediaSegmentExtensions
{
    extension(MediaSegment segment)
    {
        public string SkipTitle => segment.Type switch
        {
            "Intro" => "Skip Intro",
            "Outro" => "Skip Credits",
            "Recap" => "Skip Recap",
            "Preview" => "Skip Preview",
            "Commercial" => "Skip Ad",
            _ => "Skip",
        };

        public bool Contains(long ticks) => ticks >= segment.StartTicks && ticks < segment.EndTicks;
    }
}

/// <summary>
/// A song's lyrics from the server: timed lines when a synced file exists,
/// plain lines otherwise.
/// </summary>
public sealed record SongLyrics
{
    public LyricMetadata? Metadata { get; init; }
    public IReadOnlyList<LyricLine> Lyrics { get; init; } = [];
}

public sealed record LyricMetadata
{
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }

    /// <summary>Shift applied to every line, in ticks.</summary>
    public long? Offset { get; init; }

    public bool? IsSynced { get; init; }
}

public sealed record LyricLine
{
    public required string Text { get; init; }

    /// <summary>When the line starts, in ticks; null for unsynced lyrics.</summary>
    public long? Start { get; init; }
}

public static class LyricsExtensions
{
    extension(SongLyrics lyrics)
    {
        public bool IsSynced => lyrics.Metadata?.IsSynced ?? lyrics.Lyrics.Any(line => line.Start is not null);
    }
}

public enum ItemKind
{
    Other,
    Movie,
    Series,
    Season,
    Episode,
    BoxSet,
    CollectionFolder,
    Folder,
    Person,
    Video,
    MusicVideo,
    Trailer,
    UserView,
    TvChannel,
    Program,
    Recording,
    MusicAlbum,
    MusicArtist,
    Audio,
    Playlist,
    MusicGenre,

    /// <summary>
    /// One audiobook file: a whole book (an .m4b), or one part of a book
    /// kept as a folder of files.
    /// </summary>
    AudioBook,
}

public static class ItemKindExtensions
{
    extension(ItemKind kind)
    {
        /// <summary>Albums, artists, playlists and songs.</summary>
        public bool IsMusic => kind is ItemKind.MusicAlbum or ItemKind.MusicArtist or ItemKind.Audio
            or ItemKind.Playlist or ItemKind.MusicGenre;
    }
}

/// <summary>The server's image types; the names are its values.</summary>
public enum ImageKind
{
    Primary,
    Backdrop,
    Thumb,
    Logo,
    Banner,
    Chapter,
}

/// <summary><c>GET</c>/<c>POST /AsterChannels/LiveTvGroups</c> from the Aster Channels server plugin: each Live TV channel's group.</summary>
public sealed record LiveTvGroupList
{
    public sealed record Entry(string ChannelId, string Group);

    public IReadOnlyList<Entry> Groups { get; init; } = [];

    /// <summary>The groups by channel id; a channel listed twice keeps its last.</summary>
    public IReadOnlyDictionary<string, string> ByChannel
    {
        get
        {
            var byChannel = new Dictionary<string, string>();
            foreach (var entry in Groups) byChannel[entry.ChannelId] = entry.Group;
            return byChannel;
        }
    }
}

// ---- Playback reporting ----

public sealed record PlaybackReport
{
    public required string ItemId { get; init; }
    public string? MediaSourceId { get; init; }
    public string? PlaySessionId { get; init; }
    public long PositionTicks { get; init; }
    public bool IsPaused { get; init; }
    public bool IsMuted { get; init; }
    public bool CanSeek { get; init; } = true;
    public required string PlayMethod { get; init; }
    public int VolumeLevel { get; init; } = 100;
    public string RepeatMode { get; init; } = "RepeatNone";
    public long? PlaybackStartTimeTicks { get; init; }
    public string? LiveStreamId { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>
    /// The title's running time, for the app's own resume-point rules
    /// (<see cref="ResumePoints"/>); not sent to the server.
    /// </summary>
    [JsonIgnore]
    public long? RunTimeTicks { get; init; }
}

// ---- OP/ED plugin ----

/// <summary>
/// <c>GET /OpEd/Library</c> (plugin 0.2): the libraries the plugin publishes
/// its themes in, each null until it exists.
/// </summary>
public sealed record OpEdLibraries
{
    public string? VideosLibraryId { get; init; }
    public string? SongsLibraryId { get; init; }
}

/// <summary>
/// <c>GET /OpEd/Items/{itemId}/Themes</c> from the OP/ED server plugin: anime
/// openings and endings the server owner supplied as files.
/// </summary>
public sealed record OpEdThemeList
{
    public required string ItemId { get; init; }
    public string? SeriesId { get; init; }
    public IReadOnlyList<OpEdTheme> Themes { get; init; } = [];
}

/// <summary>
/// One opening or ending. Absent fields are omitted by the server, so all
/// optional ones are optional here; unknown new fields are ignored.
/// </summary>
public sealed record OpEdTheme
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public int Sequence { get; init; }
    public int Version { get; init; }
    public string? Variant { get; init; }
    public required string Slug { get; init; }
    public string? SeriesId { get; init; }
    public string? SeasonId { get; init; }
    public int? AniDbId { get; init; }
    public string? SongTitle { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public string? Episodes { get; init; }
    public bool? PlaysInEpisode { get; init; }
    public bool Spoiler { get; init; }
    public bool Nsfw { get; init; }
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public string? VideoMp4Url { get; init; }
    public string? VideoMp4Status { get; init; }

    /// <summary>
    /// The AAC copy of the audio (or an original MP3/AAC). On Apple TV the
    /// original <c>AudioUrl</c> (often Ogg/Opus) can't play; mpv could, but
    /// the AAC copy keeps both apps on the same file.
    /// </summary>
    public string? AudioM4aUrl { get; init; }

    public string? AudioM4aStatus { get; init; }

    /// <summary>
    /// The MusicVideo item it was published as in the plugin's Music Videos
    /// library (plugin 0.2), and its song's Audio item; null when not.
    /// </summary>
    public string? LibraryItemId { get; init; }

    public string? SongItemId { get; init; }

    /// <summary>
    /// A full-length version beside it ("OP1-Full", plugin 0.4): its own
    /// playable copies and published items. See <c>FullVersion</c>.
    /// </summary>
    public bool? HasFullVersion { get; init; }

    public string? FullVideoMp4Url { get; init; }
    public string? FullVideoMp4Status { get; init; }
    public string? FullAudioM4aUrl { get; init; }
    public string? FullAudioM4aStatus { get; init; }
    public string? FullLibraryItemId { get; init; }
    public string? FullSongItemId { get; init; }

    /// <summary>From <c>LibraryItems/{id}/Theme</c>: that item is the full version.</summary>
    public bool? IsFullVersionItem { get; init; }

    /// <summary>This is the full version, made by <c>FullVersion</c> (not from the server).</summary>
    [JsonIgnore]
    public bool IsFullVersion { get; init; }
}

public static class OpEdThemeExtensions
{
    extension(OpEdTheme theme)
    {
        public bool IsOpening => theme.Type.Equals("OP", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// What to hand the player: the H.264 MP4 when ready, else the AAC
        /// audio. Null when nothing is playable yet.
        /// </summary>
        public string? PlayablePath => theme.VideoMp4Url ?? theme.AudioM4aUrl;

        /// <summary>
        /// The full-length version as a theme of its own: the same theme (same
        /// id, so it shows as playing on its card) with the full copies and
        /// items in place of the TV-size ones. Null when there's none playable.
        /// </summary>
        public OpEdTheme? FullVersion
        {
            get
            {
                if (theme.IsFullVersion || (theme.FullVideoMp4Url is null && theme.FullAudioM4aUrl is null)) return null;
                return theme with
                {
                    VideoMp4Url = theme.FullVideoMp4Url,
                    VideoMp4Status = theme.FullVideoMp4Status,
                    AudioM4aUrl = theme.FullAudioM4aUrl,
                    AudioM4aStatus = theme.FullAudioM4aStatus,
                    LibraryItemId = theme.FullLibraryItemId,
                    SongItemId = theme.FullSongItemId,
                    IsFullVersion = true,
                };
            }
        }

        /// <summary>The full version when <paramref name="full"/> and there is one, else this one.</summary>
        public OpEdTheme WithVersion(bool full) => full ? theme.FullVersion ?? theme : theme;

        /// <summary>Whether this is (or, from a library item, plays) the full version.</summary>
        public bool IsFull => theme.IsFullVersion || theme.IsFullVersionItem == true;

        /// <summary>Only the song plays: there's no video copy.</summary>
        public bool IsAudioOnly => theme.VideoMp4Url is null && theme.AudioM4aUrl is not null;

        /// <summary>A copy is still being made; worth showing and checking again.</summary>
        public bool IsPreparing =>
            theme.PlayablePath is null
            && new[] { theme.VideoMp4Status, theme.AudioM4aStatus }.Any(s => s is "pending" or "converting");

        /// <summary>"OP1", "OP1 v2", "ED1-TV".</summary>
        public string Code => theme.Version > 1 ? $"{theme.Slug} v{theme.Version}" : theme.Slug;

        /// <summary>"Yuusha — YOASOBI", or null when the plugin has no song details.</summary>
        public string? SongLine
        {
            get
            {
                var artists = string.Join(", ", theme.Artists);
                if (theme.SongTitle is not { } title) return theme.Artists.Count == 0 ? null : artists;
                return theme.Artists.Count == 0 ? title : $"{title} — {artists}";
            }
        }
    }
}
