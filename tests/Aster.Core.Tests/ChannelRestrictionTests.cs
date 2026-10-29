using System.Text.Json;
using Aster.Core.Channels;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>
/// A channel with anything on it the user's parental controls hold back is
/// left out. The numbers are Jellyfin 12's for a user capped at PG: the
/// Sitcoms playlist lists 824 titles but sends 349. The Apple app's
/// ChannelRestrictionTests.
/// </summary>
public class ChannelRestrictionTests
{
    private static BaseItem Playlist(int listing) =>
        JsonSerializer.Deserialize<BaseItem>($$"""{"Id":"p1","Name":"Channel","Type":"Playlist","ChildCount":{{listing}}}""", JellyfinJson.Options)!;

    private static readonly UserPolicy Pg = new() { MaxParentalRating = 10 };
    private static readonly UserPolicy Unrestricted = new();
    private static readonly IReadOnlyList<ParentalRating> Ratings =
    [
        new() { Name = "G", Value = 0 },
        new() { Name = "PG", Value = 10 },
        new() { Name = "TV-PG", Value = 10 },
        new() { Name = "PG-13", Value = 13 },
        new() { Name = "TV-MA", RatingScore = new() { Score = 17, SubScore = 1 } },
    ];

    [Fact]
    public void APlaylistMissingTitlesIsHidden()
    {
        var tags = ChannelTags.Parse(["aster:channel"]);
        Assert.True(ChannelLoader.IsHeldBack(Playlist(824), tags, 349, Pg, []));
        Assert.False(ChannelLoader.IsHeldBack(Playlist(29), tags, 29, Pg, []));
        // The server refused the playlist outright.
        Assert.True(ChannelLoader.IsHeldBack(Playlist(35), tags, null, Pg, []));
    }

    [Fact]
    public void AnUnrestrictedUserGetsEveryChannel()
    {
        // A missing file shouldn't cost an administrator the channel.
        Assert.False(ChannelLoader.IsHeldBack(Playlist(10), ChannelTags.Parse(["aster:channel", "aster:genre=Horror"]), 9, Unrestricted, Ratings));
    }

    [Fact]
    public void AGenreChannelNeedsRatingsWithinTheCap()
    {
        var empty = Playlist(0);
        Assert.True(ChannelLoader.IsHeldBack(empty, ChannelTags.Parse(["aster:channel", "aster:genre=Horror"]), 0, Pg, Ratings));
        Assert.False(ChannelLoader.IsHeldBack(empty, ChannelTags.Parse(["aster:channel", "aster:genre=Animation", "aster:rating=G,PG,TV-PG"]), 0, Pg, Ratings));
        Assert.True(ChannelLoader.IsHeldBack(empty, ChannelTags.Parse(["aster:channel", "aster:genre=Animation", "aster:rating=PG,PG-13"]), 0, Pg, Ratings));
        // A rating the server doesn't know can't be cleared.
        Assert.True(ChannelLoader.IsHeldBack(empty, ChannelTags.Parse(["aster:channel", "aster:genre=Animation", "aster:rating=Kids"]), 0, Pg, Ratings));
    }

    [Fact]
    public void OtherRestrictionsHideGenreChannels()
    {
        var family = ChannelTags.Parse(["aster:channel", "aster:genre=Animation", "aster:rating=G"]);
        Assert.True(ChannelLoader.IsHeldBack(Playlist(0), family, 0, Pg with { BlockedTags = ["gore"] }, Ratings));
        Assert.True(ChannelLoader.IsHeldBack(Playlist(0), family, 0, new UserPolicy { EnableAllFolders = false }, Ratings));
    }

    [Fact]
    public void ReadsTheServersRatingsAndPolicy()
    {
        var ratings = JsonSerializer.Deserialize<List<ParentalRating>>("""[{"Name":"TV-MA","Value":17,"RatingScore":{"score":17,"subScore":1}},{"Name":"PG","Value":10}]""", JellyfinJson.Options)!;
        Assert.Equal([17, 10], ratings.Select(r => r.Score));
        Assert.Equal([1, null], ratings.Select(r => r.SubScore));
        var policy = JsonSerializer.Deserialize<UserPolicy>("""{"MaxParentalRating":10,"BlockedTags":[],"AllowedTags":[],"BlockUnratedItems":[],"EnableAllFolders":true}""", JellyfinJson.Options)!;
        Assert.True(policy.RestrictsContent);
        Assert.False(policy.RestrictsBeyondRating);
        var open = JsonSerializer.Deserialize<UserPolicy>("""{"BlockedTags":[],"AllowedTags":[],"BlockUnratedItems":[],"EnableAllFolders":true}""", JellyfinJson.Options)!;
        Assert.False(open.RestrictsContent);
    }
}
