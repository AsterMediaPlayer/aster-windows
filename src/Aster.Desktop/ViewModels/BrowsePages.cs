// The sidebar's library tabs, and what opens from them: the Apple app's
// TypeGridView (Movies, TV Shows), AnimeView, LibraryView, LibraryGridView
// and PersonView.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Server;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// All movies or all series across every library: a banner of what's in
/// progress, up next and new, Recently Added, then every title. Anime is
/// left out; it has its own tab.
/// </summary>
public sealed class TypeGridViewModel : BrowsePageViewModel
{
    private readonly ItemKind kind;
    private readonly HeroModel hero;
    private IReadOnlySet<string> anime = new HashSet<string>();

    public TypeGridViewModel(INavigator navigator, ItemKind kind) : base(navigator)
    {
        this.kind = kind;
        hero = new HeroModel(navigator);
        _ = LoadAsync();
    }

    private string Title => kind == ItemKind.Movie ? "Movies" : "TV Shows";
    protected override bool HasGrid => true;
    protected override string GridHeading => $"All {Title}";
    protected override IReadOnlySet<string> GridExcluding => anime;

    protected override ItemQuery GridQuery() => SortedQuery() with { Kinds = [kind], Recursive = true };

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var client = Navigator.Client;
        anime = (await Navigator.Anime).ItemIds;
        // Extra, as the anime ones are taken out.
        var latest = AnimeCatalog.OrEmpty(client.LatestAsync(null, 40, kind.ToString()));
        var resumed = AnimeCatalog.OrEmpty(client.ResumeAsync(limit: 30));
        var next = kind == ItemKind.Series ? AnimeCatalog.OrEmpty(client.NextUpAsync(limit: 20)) : Task.FromResult<IReadOnlyList<BaseItem>>([]);
        var newest = (await latest).Where(i => !anime.Contains(i.Id)).Take(20).ToList();

        IReadOnlyList<BaseItem> inProgress, upNext;
        if (kind == ItemKind.Movie)
        {
            inProgress = (await resumed).Where(i => i.Kind == ItemKind.Movie && !anime.Contains(i.Id)).ToList();
            upNext = [];
        }
        else
        {
            inProgress = (await resumed).Where(i => i.Kind == ItemKind.Episode && !anime.Contains(i.SeriesId ?? "")).ToList();
            upNext = (await next).Where(e => !anime.Contains(e.SeriesId ?? "") && inProgress.All(p => p.Id != e.Id)).ToList();
        }
        hero.Show(inProgress.Take(kind == ItemKind.Movie ? 4 : 3).Concat(upNext.Take(3)).Concat(newest.Take(5)));

        var rows = new List<BrowseRow>();
        if (hero.HasPages) rows.Add(new HeroRow(hero, this));
        rows.Add(new TitleRow(Title, this));
        if (LandscapeShelf("Recently Added", newest) is { } recent) rows.Add(recent);
        return rows;
    }
}

/// <summary>
/// The Anime tab: everything in the libraries Shoko matched, which Movies
/// and TV Shows leave out. Its libraries open as grids of their own.
/// </summary>
public sealed class AnimeViewModel : BrowsePageViewModel
{
    private readonly HeroModel hero;

    public AnimeViewModel(INavigator navigator) : base(navigator)
    {
        hero = new HeroModel(navigator);
        _ = LoadAsync();
    }

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var client = Navigator.Client;
        var catalog = await Navigator.Anime;
        var ids = catalog.Libraries.Select(l => l.Id).ToList();
        var showIds = catalog.Libraries.Where(l => l.CollectionType == "tvshows").Select(l => l.Id);
        var resume = AnimeCatalog.MergedAsync(ids, id => client.ResumeAsync(limit: 20, parentId: id));
        var next = AnimeCatalog.MergedAsync(showIds, id => client.NextUpAsync(limit: 20, parentId: id));
        var latest = AnimeCatalog.RecentlyAddedAsync(client, catalog.Libraries);
        // The OP/ED plugin's library, which only Jellyfin has.
        var musicVideos = Task.WhenAll(catalog.MusicVideoLibraries.Select(async l => (Library: l, Items: await Navigator.JellyfinOnly.LatestMusicVideosAsync(l.Id))));

        // Newest first across libraries; ISO 8601 dates sort as text.
        var resumed = await resume;
        var continueWatching = resumed.OrderByDescending(i => i.UserData?.LastPlayedDate ?? "", System.StringComparer.Ordinal).Take(20).ToList();
        var nextUp = (await next).Where(e => resumed.All(r => r.Id != e.Id)).Take(20).ToList();
        var recentlyAdded = await latest;
        hero.Show(continueWatching.Take(3).Concat(nextUp.Take(3)).Concat(recentlyAdded.Take(5)));

        var rows = new List<BrowseRow>();
        if (hero.HasPages) rows.Add(new HeroRow(hero, this));
        else rows.Add(new TitleRow("Anime", this));
        rows.AddRange(new BrowseRow?[]
        {
            TileShelf("Libraries", catalog.Libraries.Concat(catalog.MusicVideoLibraries)),
            LandscapeShelf("Continue Watching", continueWatching),
            LandscapeShelf("Next Up", nextUp),
            LandscapeShelf("Recently Added", recentlyAdded),
        }.OfType<BrowseRow>());
        // Openings & endings (the OP/ED plugin's library).
        foreach (var (library, items) in await musicVideos)
        {
            if (LandscapeShelf(library.DisplayTitle, items, showSeriesTitle: false) is { } shelf) rows.Add(shelf);
        }
        if (!catalog.HasAnime) rows.Add(new StatusRow("No anime libraries", "Libraries are found by their titles' Shoko or AniDB ids."));
        return rows;
    }
}

/// <summary>The Library tab: every library, favourites and playlists.</summary>
public sealed class LibraryViewModel : BrowsePageViewModel
{
    public LibraryViewModel(INavigator navigator) : base(navigator) => _ = LoadAsync();

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var client = Navigator.Client;
        var libraries = client.UserViewsAsync();
        var favorites = client.Can(ServerCapabilities.Favorites)
            ? client.ItemsAsync(new ItemQuery
            {
                Recursive = true,
                FavoritesOnly = true,
                Kinds = [ItemKind.Movie, ItemKind.Series, ItemKind.Episode],
                Sort = Aster.Core.Browsing.LibrarySort.Name,
                Limit = 40,
            })
            : Task.FromResult(new ItemsResult());
        var playlists = client is JellyfinClient jellyfin && jellyfin.Can(ServerCapabilities.Playlists)
            ? AnimeCatalog.OrEmpty(jellyfin.PlaylistsAsync("Video"))
            : Task.FromResult<IReadOnlyList<BaseItem>>([]);
        var favorite = (await favorites).Items;
        return new BrowseRow?[]
        {
            new TitleRow("Library", this),
            TileShelf("Libraries", (await libraries).Where(l => l.CollectionType != "playlists")),
            PosterShelf("Favorites", favorite.Where(i => i.Kind != ItemKind.Episode)),
            LandscapeShelf("Favorite Episodes", favorite.Where(i => i.Kind == ItemKind.Episode)),
            LandscapeShelf("Playlists", await playlists),
        }.OfType<BrowseRow>().ToList();
    }
}

/// <summary>Everything inside one library, collection or playlist, as a sortable grid.</summary>
public sealed class LibraryGridViewModel : BrowsePageViewModel
{
    private readonly BaseItem parent;

    public LibraryGridViewModel(INavigator navigator, BaseItem parent) : base(navigator)
    {
        this.parent = parent;
        _ = LoadAsync();
    }

    protected override bool HasGrid => true;
    public override CardStyle GridStyle => parent.CollectionType is "homevideos" or "musicvideos" ? CardStyle.Landscape : CardStyle.Poster;

    protected override ItemQuery GridQuery()
    {
        var query = SortedQuery() with { ParentId = parent.Id };
        // Every title, not the folders (one per show or artist) they're in.
        ItemKind? kind = parent.CollectionType switch
        {
            "movies" => ItemKind.Movie,
            "tvshows" => ItemKind.Series,
            "boxsets" => ItemKind.BoxSet,
            "musicvideos" => ItemKind.MusicVideo,
            _ => null,
        };
        return kind is { } titles ? query with { Kinds = [titles], Recursive = true } : query;
    }

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        // A stub (from a card) is enough to open; the name comes with the full item.
        var name = parent.Name ?? (await Navigator.Client.ItemAsync(parent.Id)).DisplayTitle;
        return [new TitleRow(name, this)];
    }
}

/// <summary>Someone in the cast or crew: their photo, biography and filmography.</summary>
public sealed class PersonViewModel : BrowsePageViewModel
{
    private readonly BaseItem person;

    public PersonViewModel(INavigator navigator, BaseItem person) : base(navigator)
    {
        this.person = person;
        // Newest first, as the Apple app lists a filmography.
        SortIndex = (int)Aster.Core.Browsing.LibrarySort.ReleaseDate;
        _ = LoadAsync();
    }

    protected override bool HasGrid => true;
    protected override string GridHeading => "Filmography";

    protected override ItemQuery GridQuery() => SortedQuery() with
    {
        PersonId = person.Id,
        Recursive = true,
        Kinds = [ItemKind.Movie, ItemKind.Series],
    };

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var details = await Navigator.Client.ItemAsync(person.Id);
        var photo = details.ImageTags?.TryGetValue("Primary", out var tag) == true
            ? Navigator.Client.ImageUrl(person.Id, ImageKind.Primary, tag, maxWidth: 500)
            : null;
        return [new PersonRow(details.DisplayTitle, photo, details.Overview)];
    }
}
