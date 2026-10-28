// Plex's titles as the app's BaseItems, so the screens built for Jellyfin
// show them unchanged. Picture fields hold Plex's own paths (a title's thumb
// and art), which PlexClient.ImageUrl sends through the server's photo
// transcoder; times go from milliseconds to the app's ticks (100 ns). A port
// of the Apple app's PlexMapper.swift.

using System.Globalization;
using Aster.Core.Jellyfin;

namespace Aster.Core.Plex;

public static class PlexMapper
{
    public const long TicksPerMillisecond = 10_000;

    /// <summary>A library section as a library.</summary>
    public static BaseItem Library(PlexDirectory directory) => new()
    {
        Id = directory.Key,
        Name = directory.Title,
        Type = nameof(ItemKind.CollectionFolder),
        CollectionType = directory.Type switch
        {
            "movie" => "movies",
            "show" => "tvshows",
            "artist" => "music",
            "photo" => "photos",
            _ => directory.Type ?? "",
        },
    };

    public static ItemKind Kind(string? type) => type switch
    {
        "movie" => ItemKind.Movie,
        "show" => ItemKind.Series,
        "season" => ItemKind.Season,
        "episode" => ItemKind.Episode,
        "collection" => ItemKind.BoxSet,
        "clip" => ItemKind.Video,
        _ => ItemKind.Other,
    };

    public static BaseItem Item(PlexMetadata metadata)
    {
        var kind = Kind(metadata.Type);
        var logo = metadata.Image?.FirstOrDefault(i => i.Type == "clearLogo")?.Url;

        // Pictures: the poster, the backdrop and the logo; a season's and an
        // episode's fall back to their show's.
        var tags = new Dictionary<string, string>();
        if (metadata.Thumb is { } thumb) tags["Primary"] = thumb;
        if (logo is not null && kind is not (ItemKind.Episode or ItemKind.Season)) tags["Logo"] = logo;

        var item = new BaseItem
        {
            Id = metadata.RatingKey,
            Type = kind.ToString(),
            Name = metadata.Title,
            OriginalTitle = metadata.OriginalTitle,
            SortName = metadata.TitleSort,
            Overview = metadata.Summary,
            Taglines = metadata.Tagline is { } tagline ? [tagline] : null,
            ProductionYear = metadata.Year,
            PremiereDate = metadata.OriginallyAvailableAt is { } date ? date + "T00:00:00Z" : null,
            DateCreated = metadata.AddedAt is { } added ? IsoDate(added) : null,
            OfficialRating = metadata.ContentRating,
            CommunityRating = metadata.AudienceRating ?? metadata.Rating,
            RunTimeTicks = metadata.Duration * TicksPerMillisecond,
            IndexNumber = metadata.Index,
            Genres = metadata.Genre?.Select(g => g.Tag).ToList(),
            Studios = metadata.Studio is { } studio ? [new NameGuidPair { Name = studio }] : null,
            People = People(metadata),
            ProviderIds = ProviderIds(metadata),
            Chapters = metadata.Chapter?.Select(c => new ChapterInfo { StartPositionTicks = c.StartTimeOffset * TicksPerMillisecond, Name = c.Tag }).ToList(),
            MediaStreams = metadata.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Stream?.Select(Stream).ToList(),
            Container = metadata.Media?.FirstOrDefault()?.Container,
            IsFolder = kind is ItemKind.Series or ItemKind.Season or ItemKind.BoxSet,
            ImageTags = tags.Count == 0 ? null : tags,
            BackdropImageTags = metadata.Art is { } art && kind != ItemKind.Episode ? [art] : null,
            UserData = UserData(metadata, kind),
        };

        return kind switch
        {
            ItemKind.Episode => item with
            {
                SeriesId = metadata.GrandparentRatingKey,
                SeriesName = metadata.GrandparentTitle,
                SeasonId = metadata.ParentRatingKey,
                SeasonName = metadata.ParentTitle,
                ParentId = metadata.ParentRatingKey,
                ParentIndexNumber = metadata.ParentIndex,
                SeriesPrimaryImageTag = metadata.GrandparentThumb,
                ParentBackdropItemId = (metadata.GrandparentArt ?? metadata.Art) is not null ? metadata.GrandparentRatingKey : null,
                ParentBackdropImageTags = (metadata.GrandparentArt ?? metadata.Art) is { } showArt ? [showArt] : null,
                ParentLogoItemId = logo is not null ? metadata.GrandparentRatingKey : null,
                ParentLogoImageTag = logo,
            },
            ItemKind.Season => item with
            {
                SeriesId = metadata.ParentRatingKey,
                SeriesName = metadata.ParentTitle,
                ParentId = metadata.ParentRatingKey,
                SeriesPrimaryImageTag = metadata.ParentThumb,
                ParentBackdropItemId = metadata.Art is not null ? metadata.ParentRatingKey : null,
                ParentBackdropImageTags = metadata.Art is { } seasonArt ? [seasonArt] : null,
                ChildCount = metadata.LeafCount,
            },
            ItemKind.Series => item with
            {
                ChildCount = metadata.ChildCount,
                RecursiveItemCount = metadata.LeafCount,
            },
            _ => item,
        };
    }

    /// <summary>Where it was left, and whether it's been watched: a show or season by its episodes.</summary>
    public static UserItemData UserData(PlexMetadata metadata, ItemKind kind)
    {
        var data = new UserItemData();
        if (metadata.LeafCount is { } leaves && kind is ItemKind.Series or ItemKind.Season)
        {
            var watched = metadata.ViewedLeafCount ?? 0;
            data = data with { UnplayedItemCount = Math.Max(0, leaves - watched), Played = leaves > 0 && watched >= leaves };
        }
        else
        {
            data = data with { Played = (metadata.ViewCount ?? 0) > 0, PlayCount = metadata.ViewCount };
        }
        if (metadata.ViewOffset is > 0 and var offset)
        {
            data = data with
            {
                PlaybackPositionTicks = offset * TicksPerMillisecond,
                PlayedPercentage = metadata.Duration is > 0 and var duration ? (double)offset / duration * 100 : null,
            };
        }
        return data with { LastPlayedDate = metadata.LastViewedAt is { } viewed ? IsoDate(viewed) : null };
    }

    /// <summary>The cast first, then who directed and wrote it.</summary>
    private static IReadOnlyList<PersonInfo>? People(PlexMetadata metadata)
    {
        static PersonInfo Person(PlexTag tag, string type) => new()
        {
            Id = tag.Id?.ToString(CultureInfo.InvariantCulture) ?? tag.Tag,
            Name = tag.Tag,
            Role = tag.Role,
            Type = type,
            PrimaryImageTag = tag.Thumb,
        };
        var all = (metadata.Role ?? []).Select(t => Person(t, "Actor"))
            .Concat((metadata.Director ?? []).Select(t => Person(t, "Director")))
            .Concat((metadata.Writer ?? []).Select(t => Person(t, "Writer")))
            .ToList();
        return all.Count == 0 ? null : all;
    }

    /// <summary>"tmdb://5439847" as Tmdb, and so on; Shoko's own id marks anime.</summary>
    private static IReadOnlyDictionary<string, string>? ProviderIds(PlexMetadata metadata)
    {
        var ids = new Dictionary<string, string>();
        foreach (var guid in metadata.Guids ?? [])
        {
            var parts = guid.Id.Split("://");
            if (parts.Length != 2) continue;
            var name = parts[0] switch
            {
                "tmdb" => "Tmdb",
                "imdb" => "Imdb",
                "tvdb" => "Tvdb",
                var other => other.Length == 0 ? other : char.ToUpperInvariant(other[0]) + other[1..].ToLowerInvariant(),
            };
            ids[name] = parts[1];
        }
        if (metadata.Guid is { } shoko && shoko.Contains(".shoko://", StringComparison.Ordinal)) ids["Shoko"] = shoko.Split('/')[^1];
        return ids.Count == 0 ? null : ids;
    }

    private static MediaStream Stream(PlexStream stream) => new()
    {
        Type = stream.StreamType switch
        {
            1 => "Video",
            2 => "Audio",
            3 => "Subtitle",
            _ => null,
        },
        Index = stream.Index,
        Codec = stream.Codec,
        Language = stream.LanguageCode,
        DisplayTitle = stream.DisplayTitle,
        Title = stream.Title,
        IsDefault = stream.Default,
        IsForced = stream.Forced,
        IsExternal = stream.Key is not null,
        Width = stream.Width,
        Height = stream.Height,
        Channels = stream.Channels,
    };

    private static string IsoDate(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
