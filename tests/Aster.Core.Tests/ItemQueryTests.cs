using Aster.Core.Browsing;
using Aster.Core.Jellyfin;
using Aster.Core.Server;

namespace Aster.Core.Tests;

/// <summary>
/// Typed queries give Jellyfin exactly the parameters the screens sent
/// before they used them: each expectation is the literal dictionary the
/// screen built by hand. A port of the Apple app's ItemQueryTests.
/// </summary>
public class ItemQueryTests
{
    private static void Same(Dictionary<string, string?> expected, ItemQuery query) =>
        Assert.Equal(expected.OrderBy(p => p.Key), query.JellyfinParameters().OrderBy(p => p.Key));

    [Fact]
    public void LibraryFavorites() => Same(new()
    {
        ["recursive"] = "true",
        ["filters"] = "IsFavorite",
        ["includeItemTypes"] = "Movie,Series,Episode",
        ["sortBy"] = "SortName",
        ["limit"] = "40",
    }, new ItemQuery { Kinds = [ItemKind.Movie, ItemKind.Series, ItemKind.Episode], Recursive = true, Sort = LibrarySort.Name, FavoritesOnly = true, Limit = 40 });

    [Theory]
    [InlineData(LibrarySort.Name, false, "SortName", "Ascending")]
    [InlineData(LibrarySort.DateAdded, true, "DateCreated,SortName", "Descending")]
    [InlineData(LibrarySort.ReleaseDate, true, "PremiereDate,SortName", "Descending")]
    [InlineData(LibrarySort.Rating, true, "CommunityRating,SortName", "Descending")]
    public void EachSortAsBefore(LibrarySort sort, bool descending, string sortBy, string order) => Same(new()
    {
        ["includeItemTypes"] = "Movie",
        ["recursive"] = "true",
        ["sortBy"] = sortBy,
        ["sortOrder"] = order,
    }, new ItemQuery { Kinds = [ItemKind.Movie], Recursive = true, Sort = sort, Descending = descending });

    [Fact]
    public void ALibrarysTitles()
    {
        Same(new()
        {
            ["parentId"] = "lib",
            ["sortBy"] = "SortName",
            ["sortOrder"] = "Ascending",
            ["includeItemTypes"] = "Movie",
            ["recursive"] = "true",
        }, new ItemQuery { ParentId = "lib", Kinds = [ItemKind.Movie], Recursive = true, Sort = LibrarySort.Name, Descending = false });
        // A folder library: just its own contents.
        Same(new()
        {
            ["parentId"] = "lib",
            ["sortBy"] = "DateCreated,SortName",
            ["sortOrder"] = "Descending",
        }, new ItemQuery { ParentId = "lib", Sort = LibrarySort.DateAdded, Descending = true });
    }

    [Fact]
    public void APersonsFilmography() => Same(new()
    {
        ["personIds"] = "p1",
        ["recursive"] = "true",
        ["includeItemTypes"] = "Movie,Series",
        ["sortBy"] = "PremiereDate,SortName",
        ["sortOrder"] = "Descending",
    }, new ItemQuery { Kinds = [ItemKind.Movie, ItemKind.Series], Recursive = true, Sort = LibrarySort.ReleaseDate, Descending = true, PersonId = "p1" });

    /// <summary>Music's grids still take the sort in Jellyfin's terms.</summary>
    [Fact]
    public void LibrarySortsKeepTheirJellyfinNames()
    {
        Assert.Equal(("SortName", "Ascending"), LibrarySort.Name.Query);
        Assert.Equal(("DateCreated,SortName", "Descending"), LibrarySort.DateAdded.Query);
        Assert.Equal(("PremiereDate,SortName", "Descending"), LibrarySort.ReleaseDate.Query);
        Assert.Equal(("CommunityRating,SortName", "Descending"), LibrarySort.Rating.Query);
    }

    [Fact]
    public void ExactTitles() => Same(new() { ["ids"] = "a,b" }, new ItemQuery { Ids = ["a", "b"] });

    [Fact]
    public void PagesAddTheirPlace() => Same(new()
    {
        ["includeItemTypes"] = "Series",
        ["recursive"] = "true",
        ["startIndex"] = "200",
        ["limit"] = "100",
    }, new ItemQuery { Kinds = [ItemKind.Series], Recursive = true }.Page(200, 100));
}
