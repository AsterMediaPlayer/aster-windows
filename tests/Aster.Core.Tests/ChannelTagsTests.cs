using System.Runtime.CompilerServices;
using System.Text.Json;
using Aster.Core.Channels;

namespace Aster.Core.Tests;

/// <summary>
/// Channel tags against Conformance/channel-tags.json, the vectors the
/// Apple app and the Jellyfin dashboard plugin run too, so the three can't
/// drift apart; then a few cases of the Apple app's own tests. The file is
/// a copy of the Apple app's AsterFINTests/Conformance/channel-tags.json:
/// a change there is copied here.
/// </summary>
public class ChannelTagsTests
{
    private sealed record Case(string Name, string[] Tags, bool IsChannel, string[] Written);
    private sealed record Vectors(Case[] Cases);

    private static Vectors Load([CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "Conformance", "channel-tags.json");
        return JsonSerializer.Deserialize<Vectors>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void TheVectorsAreThere() => Assert.True(Load().Cases.Length >= 10);

    [Fact]
    public void TagsAreReadAndWrittenAsTheVectorsSay()
    {
        foreach (var vector in Load().Cases)
        {
            var tags = ChannelTags.Parse(vector.Tags);
            Assert.True(vector.IsChannel == tags.IsChannel, $"{vector.Name}: isChannel");
            Assert.True(vector.Written.SequenceEqual(tags.AppliedTo(vector.Tags)),
                $"{vector.Name}:\n  expected {string.Join(" ", vector.Written)}\n  written  {string.Join(" ", tags.AppliedTo(vector.Tags))}");
        }
    }

    [Fact]
    public void BadValuesFallBackToDefaults()
    {
        var tags = ChannelTags.Parse(["aster:channel", "aster:number=-4", "aster:mode=sideways", "aster:blocks=6-3", "aster:bug-size=900%", "aster:align=7"]);
        Assert.True(tags.IsChannel);
        Assert.Null(tags.Number);
        Assert.Equal(ChannelMode.Rotation, tags.Mode);
        Assert.Equal(ChannelLineup.DefaultBlocks, tags.Blocks);
        Assert.Equal(100, tags.BugSize);
        Assert.Equal(0, tags.Breaks.Align);
    }

    [Fact]
    public void CaseAndSpacesDontMatter()
    {
        var tags = ChannelTags.Parse([" ASTER:Channel ", "Aster:MODE = Blocks", "aster:Number=7"]);
        Assert.True(tags.IsChannel);
        Assert.Equal(ChannelMode.Blocks, tags.Mode);
        Assert.Equal(7, tags.Number);
    }

    [Fact]
    public void UnnumberedChannelsComeAfterTheRestByName()
    {
        var numbers = ChannelTags.Numbers([("a", "Zed", null), ("b", "Alpha", 5), ("c", "Beta", null), ("d", "Channel 10", null), ("e", "Channel 9", null)]);
        Assert.Equal(5, numbers["b"]);
        Assert.Equal(6, numbers["c"]);
        Assert.Equal(7, numbers["e"]);
        Assert.Equal(8, numbers["d"]);
        Assert.Equal(9, numbers["a"]);
    }

    [Fact]
    public void AClipsOwnTagSaysWhatItIs()
    {
        Assert.Equal(BreakKind.Commercials, ChannelTags.ClipKind(["Family", "aster:commercial"]));
        Assert.Equal(BreakKind.Bumpers, ChannelTags.ClipKind(["aster:bumper=in"]));
        Assert.Null(ChannelTags.ClipKind(["Family"]));
        Assert.Equal(BumperPlacement.In, ChannelTags.BumperSide(["aster:bumper=in"]));
        Assert.Equal(BumperPlacement.Out, ChannelTags.BumperSide(["aster:bumper=OUT"]));
        Assert.Equal(BumperPlacement.Both, ChannelTags.BumperSide(["Family"]));
        Assert.True(ChannelTags.IsFillerPlaylist(["aster:filler"]));
        Assert.False(ChannelTags.IsFillerPlaylist(["aster:filler=off"]));
    }

    [Fact]
    public void AChannelCanBeKeptToSomeUsers()
    {
        var tags = ChannelTags.Parse(["aster:channel", "aster:user=peppy,JD"]);
        Assert.True(tags.IsShownTo("Peppy"));
        Assert.True(tags.IsShownTo("jd"));
        Assert.False(tags.IsShownTo("someone"));
        Assert.True(ChannelTags.Parse(["aster:channel"]).IsShownTo(null));
    }

    [Fact]
    public void OldTagsAreConverted()
    {
        Assert.Equal(["aster:channel", "Family", "aster:number=7"], ChannelTags.Converted(["asterfin:channel", "Family", "AsterFIN:number=7"]));
        Assert.Null(ChannelTags.Converted(["aster:channel"]));
    }
    /// <summary>Plex keeps a channel's tags in its playlist's description: a comma before the next "aster:" parts them.</summary>
    [Fact]
    public void TagsAreReadFromADescription()
    {
        Assert.Equal(["aster:channel", "aster:number=7"], ChannelTags.FromDescription("aster:channel, aster:number=7"));
        // A comma inside a value stays in it.
        Assert.Equal(["aster:channel", "aster:user=alice,bob", "aster:channel-name=Laugh, Cry, Repeat", "aster:filler-types=bumpers,promos"],
            ChannelTags.FromDescription("aster:channel, aster:user=alice,bob, aster:channel-name=Laugh, Cry, Repeat,aster:filler-types=bumpers,promos"));
        // The description's own words, before them or on lines of their own, are left out.
        Assert.Equal(["ASTER:Channel", "asterfin:number=3", "aster:group=Kids"],
            ChannelTags.FromDescription("Cozy shows for Sunday mornings. ASTER:Channel, asterfin:number=3\r\n\nMore words, about it.\naster:group=Kids"));
        Assert.Empty(ChannelTags.FromDescription("A playlist about disaster: movies"));
        Assert.Empty(ChannelTags.FromDescription(null));

        var tags = ChannelTags.Parse(ChannelTags.FromDescription("aster:channel, aster:number=7, aster:mode=shuffle"));
        Assert.True(tags.IsChannel);
        Assert.Equal(7, tags.Number);
    }
}
