// Plex Media Server's JSON (asked for with Accept: application/json):
// everything comes wrapped in a MediaContainer, with titles as Metadata,
// libraries as Directory and Home rows and search results as Hubs. Only
// what the app reads is here; Plex adds fields freely. Fields are
// camelCase and lists of children PascalCase ("guid" and "Guid" are both
// there), so names are matched exactly (see PlexJson). A port of the Apple
// app's PlexModels.swift.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aster.Core.Plex;

public static class PlexJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

public sealed record PlexReply
{
    [JsonPropertyName("MediaContainer")]
    public PlexContainer MediaContainer { get; init; } = new();
}

public sealed record PlexContainer
{
    public int? Size { get; init; }
    public int? TotalSize { get; init; }
    public int? Offset { get; init; }
    [JsonPropertyName("Metadata")]
    public IReadOnlyList<PlexMetadata>? Metadata { get; init; }
    [JsonPropertyName("Directory")]
    public IReadOnlyList<PlexDirectory>? Directory { get; init; }
    [JsonPropertyName("Hub")]
    public IReadOnlyList<PlexHub>? Hub { get; init; }
    [JsonPropertyName("TranscodeSession")]
    public IReadOnlyList<PlexTranscodeSession>? TranscodeSession { get; init; }
}

/// <summary>A conversion the server is running; <see cref="Key"/> is the session the player gave it.</summary>
public sealed record PlexTranscodeSession
{
    public string? Key { get; init; }
}

/// <summary>
/// A library section. A show's season list also carries one with no type,
/// its "All episodes" link, so nothing here is required: one entry missing
/// a field mustn't lose the whole reply.
/// </summary>
public sealed record PlexDirectory
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>movie, show, artist, photo</summary>
    public string? Type { get; init; }

    public string? Agent { get; init; }
}

/// <summary>A row of titles: Continue Watching, or search results of one kind.</summary>
public sealed record PlexHub
{
    public string? Type { get; init; }
    public string? HubIdentifier { get; init; }
    public int? Size { get; init; }
    [JsonPropertyName("Metadata")]
    public IReadOnlyList<PlexMetadata>? Metadata { get; init; }
}

/// <summary>A tag on a title: a genre, or a person and their role.</summary>
public sealed record PlexTag
{
    public int? Id { get; init; }
    public string Tag { get; init; } = "";
    public string? Role { get; init; }
    public string? Thumb { get; init; }
}

public sealed record PlexGuid
{
    public string Id { get; init; } = "";
}

public sealed record PlexImage
{
    /// <summary>coverPoster, background, snapshot, clearLogo</summary>
    public string Type { get; init; } = "";

    public string Url { get; init; } = "";
}

public sealed record PlexChapter
{
    public string? Tag { get; init; }
    public long StartTimeOffset { get; init; }
    public long? EndTimeOffset { get; init; }

    /// <summary>The chapter's picture, when Plex has made chapter thumbnails.</summary>
    public string? Thumb { get; init; }
}

/// <summary>Plex Pass's detected intro and credits.</summary>
public sealed record PlexMarker
{
    /// <summary>intro, credits</summary>
    public string Type { get; init; } = "";

    public long StartTimeOffset { get; init; }
    public long EndTimeOffset { get; init; }
}

public sealed record PlexStream
{
    public long Id { get; init; }

    /// <summary>1 video, 2 audio, 3 subtitles</summary>
    public int StreamType { get; init; }

    public int? Index { get; init; }
    public string? Codec { get; init; }
    public string? LanguageCode { get; init; }
    public string? Language { get; init; }
    public string? DisplayTitle { get; init; }
    public string? Title { get; init; }
    public bool? Default { get; init; }
    public bool? Forced { get; init; }

    /// <summary>The track Plex would play for this account: its language settings, or what was last chosen for the title.</summary>
    public bool? Selected { get; init; }

    /// <summary>Set for a subtitle file beside the video, which can be fetched.</summary>
    public string? Key { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Channels { get; init; }
}

public sealed record PlexPart
{
    public long Id { get; init; }
    public string? Key { get; init; }
    public long? Duration { get; init; }
    public string? Container { get; init; }
    public long? Size { get; init; }
    [JsonPropertyName("Stream")]
    public IReadOnlyList<PlexStream>? Stream { get; init; }
}

public sealed record PlexMedia
{
    public long Id { get; init; }
    public long? Duration { get; init; }
    public int? Bitrate { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Container { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? VideoResolution { get; init; }
    [JsonPropertyName("Part")]
    public IReadOnlyList<PlexPart>? Part { get; init; }
}

/// <summary>A title: a movie, show, season, episode, collection or extra.</summary>
public sealed record PlexMetadata
{
    public string RatingKey { get; init; } = "";
    public string? Key { get; init; }

    /// <summary>movie, show, season, episode, collection, clip</summary>
    public string? Type { get; init; }

    public string? Subtype { get; init; }
    public string? Title { get; init; }
    public string? TitleSort { get; init; }
    public string? OriginalTitle { get; init; }
    public string? Summary { get; init; }
    public string? Tagline { get; init; }
    public int? Year { get; init; }

    /// <summary>Milliseconds.</summary>
    public long? Duration { get; init; }

    /// <summary>Where it was left, in milliseconds.</summary>
    public long? ViewOffset { get; init; }

    public int? ViewCount { get; init; }
    public long? LastViewedAt { get; init; }

    /// <summary>A show's or season's episodes, and how many have been watched.</summary>
    public int? LeafCount { get; init; }

    public int? ViewedLeafCount { get; init; }
    public int? ChildCount { get; init; }
    public int? Index { get; init; }
    public int? ParentIndex { get; init; }
    public string? Thumb { get; init; }

    /// <summary>A playlist's picture, made from its titles' posters.</summary>
    public string? Composite { get; init; }

    /// <summary>A playlist's kind: video, audio or photo.</summary>
    public string? PlaylistType { get; init; }

    /// <summary>A smart playlist, filled by a filter rather than by hand.</summary>
    public bool? Smart { get; init; }

    public string? Art { get; init; }
    public string? ParentThumb { get; init; }
    public string? GrandparentThumb { get; init; }
    public string? GrandparentArt { get; init; }
    public string? ParentRatingKey { get; init; }
    public string? GrandparentRatingKey { get; init; }
    public string? ParentTitle { get; init; }
    public string? GrandparentTitle { get; init; }
    public string? ContentRating { get; init; }
    public double? AudienceRating { get; init; }
    public double? Rating { get; init; }
    public string? Studio { get; init; }

    /// <summary>"2025-07-06"</summary>
    public string? OriginallyAvailableAt { get; init; }

    /// <summary>Seconds since 1970.</summary>
    public long? AddedAt { get; init; }

    public string? Guid { get; init; }
    public int? LibrarySectionID { get; init; }
    [JsonPropertyName("Genre")]
    public IReadOnlyList<PlexTag>? Genre { get; init; }
    [JsonPropertyName("Role")]
    public IReadOnlyList<PlexTag>? Role { get; init; }
    [JsonPropertyName("Director")]
    public IReadOnlyList<PlexTag>? Director { get; init; }
    [JsonPropertyName("Writer")]
    public IReadOnlyList<PlexTag>? Writer { get; init; }

    [JsonPropertyName("Guid")]
    public IReadOnlyList<PlexGuid>? Guids { get; init; }

    [JsonPropertyName("Image")]
    public IReadOnlyList<PlexImage>? Image { get; init; }
    [JsonPropertyName("Chapter")]
    public IReadOnlyList<PlexChapter>? Chapter { get; init; }
    [JsonPropertyName("Marker")]
    public IReadOnlyList<PlexMarker>? Marker { get; init; }
    [JsonPropertyName("Media")]
    public IReadOnlyList<PlexMedia>? Media { get; init; }
}
