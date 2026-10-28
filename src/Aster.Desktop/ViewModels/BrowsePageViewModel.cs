using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Browsing;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using Query = System.Collections.Generic.Dictionary<string, string?>;

namespace Aster.Desktop.ViewModels;

// A browse page is one long list of rows, so that a library of thousands
// scrolls smoothly: only the rows on screen are made. These are its kinds.

public abstract record BrowseRow;

public sealed record HeroRow(HeroModel Hero, BrowsePageViewModel Page) : BrowseRow;

/// <summary>The page's title, with its section picker and sort picker when it has them.</summary>
public sealed record TitleRow(string Title, BrowsePageViewModel Page, string? Subtitle = null) : BrowseRow
{
    /// <summary>The page's first row, with no banner above it: it leaves room for the collapsed sidebar's capsule.</summary>
    public bool IsFirst { get; init; }
}

/// <summary>A titled row of cards; <see cref="Style"/> says which kind.</summary>
public sealed record ShelfRow(string Title, IReadOnlyList<object> Cards, CardStyle Style) : BrowseRow;

/// <summary>"All Movies", or a letter over its section.</summary>
public sealed record HeadingRow(string Title, bool IsLetter = false) : BrowseRow;

/// <summary>One row of a grid; <see cref="Style"/> says which kind of card.</summary>
public sealed record CardsRow(IReadOnlyList<object> Cards, CardStyle Style) : BrowseRow
{
    /// <summary>A song, one to a row and the row's full width.</summary>
    public bool IsTrack => Style == CardStyle.Track;
}

/// <summary>Loading, nothing here, or why it couldn't load.</summary>
public sealed record StatusRow(string Title, string? Detail = null, bool IsLoading = false) : BrowseRow;

/// <summary>A person's photo, name and biography.</summary>
public sealed record PersonRow(string Name, Uri? Photo, string? Overview) : BrowseRow;

/// <summary>The kinds of card a shelf or grid holds.</summary>
public enum CardStyle
{
    /// <summary>2:3 posters: movies and shows.</summary>
    Poster,

    /// <summary>16:9: episodes, home videos.</summary>
    Landscape,

    /// <summary>Libraries, with their names over them.</summary>
    Tile,

    /// <summary>Albums, playlists and songs.</summary>
    Square,

    /// <summary>Artists, round.</summary>
    Round,

    /// <summary>Audiobook covers.</summary>
    Book,

    /// <summary>An author's or a series' books, as a stack.</summary>
    BookShelf,

    /// <summary>A song as a row of a list, one to a row.</summary>
    Track,

    /// <summary>An episode airing soon: its show's picture and logo.</summary>
    Upcoming,

    /// <summary>A Live TV programme that's on, with its channel and progress.</summary>
    Program,

    /// <summary>A Live TV channel's logo.</summary>
    Channel,

    /// <summary>A scheduled recording.</summary>
    Timer,

    /// <summary>A series recording rule.</summary>
    SeriesRule,
}

/// <summary>A card a grid can tell apart from the others, so unchanged rows are kept as pages arrive.</summary>
public interface ICard
{
    string Key { get; }
}

/// <summary>
/// Pages a query's results in from the server, 100 at a time. Items whose
/// ids are in the exclusions are skipped (anime, which has its own tab). A
/// port of the Apple app's GridModel.
/// </summary>
public sealed class GridLoader
{
    public List<BaseItem> Items { get; } = [];
    public string? ErrorMessage { get; private set; }
    public bool IsLoading { get; private set; }

    /// <summary>How far into the server's results loading has got, counting items left out, so pages carry on from the right place.</summary>
    private int fetched;
    private int total;

    /// <summary>Bumped on every reset, so a page still loading from before it is dropped instead of landing in the new results.</summary>
    private int generation;

    public bool HasMore => fetched < total;

    public void Reset()
    {
        Items.Clear();
        fetched = 0;
        total = 0;
        generation++;
        IsLoading = false;
        ErrorMessage = null;
    }

    /// <summary>
    /// The next page, from <paramref name="fetch"/> (given where to start and
    /// how many); false when nothing was loaded (already loading, or it failed).
    /// </summary>
    public async Task<bool> LoadNextAsync(IReadOnlySet<string> excluding, Func<int, int, Task<ItemsResult>> fetch)
    {
        if (IsLoading) return false;
        var started = generation;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await fetch(fetched, 100);
            if (started != generation) return false;
            fetched += result.Items.Count;
            total = result.TotalRecordCount ?? fetched;
            var known = Items.Select(i => i.Id).ToHashSet();
            Items.AddRange(result.Items.Where(i => !excluding.Contains(i.Id) && known.Add(i.Id)));
            System.Diagnostics.Debug.WriteLine($"Grid: {fetched} of {total} fetched, {Items.Count} shown");
            return true;
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (started == generation) ErrorMessage = error.Message;
            return false;
        }
        finally
        {
            if (started == generation) IsLoading = false;
        }
    }
}

/// <summary>One way a page's grid can be ordered.</summary>
public sealed record SortOption(string Title, string SortBy, string Order);

/// <summary>
/// A page of the sidebar's tabs, or a library opened from one: rows above
/// (a banner, a title, shelves), and below them, for a page with one, a grid
/// of every title, sortable, in letter sections when sorted by name.
/// </summary>
public abstract partial class BrowsePageViewModel : ViewModelBase, IRefreshable
{
    protected readonly INavigator Navigator;
    private readonly GridLoader grid = new();
    private IReadOnlyList<BrowseRow> top = [];
    private IReadOnlyList<IndexSection> sections = [];
    private int gridRowsStart = -1;
    private int columns = 6;

    protected BrowsePageViewModel(INavigator navigator)
    {
        Navigator = navigator;
        SortTitles = Sorts.Select(s => s.Title).ToList();
    }

    public ObservableCollection<BrowseRow> Rows { get; } = [];

    /// <summary>Opened over another page, so it has a back arrow (which its title makes room for).</summary>
    public bool IsPushed { get; set; }

    /// <summary>The banner's height: the window's, as on Home.</summary>
    [ObservableProperty] public partial double HeroHeight { get; set; } = 700;

    /// <summary>The letters of the sections, for the index down the right; empty when not sorted by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIndex))]
    public partial IReadOnlyList<string> IndexKeys { get; set; } = [];

    public bool HasIndex => IndexKeys.Count > 0;

    // ---- What a page says about its grid ----

    /// <summary>Whether this page has a grid of titles from the server.</summary>
    protected virtual bool HasGrid => false;

    /// <summary>The kind of card in the grid.</summary>
    public virtual CardStyle GridStyle => CardStyle.Poster;

    /// <summary>"All Movies"; null for no heading over the grid.</summary>
    protected virtual string? GridHeading => null;

    /// <summary>The grid's titles, for a page whose grid any server can fill.</summary>
    protected virtual ItemQuery GridQuery() => new();

    protected virtual IReadOnlySet<string> GridExcluding => new HashSet<string>();

    /// <summary>Fetches one page of the grid: from its query, unless the page asks Jellyfin itself (music).</summary>
    protected virtual Task<ItemsResult> FetchGridPage(int start, int size) => Navigator.Client.ItemsAsync(GridQuery().Page(start, size));

    /// <summary>A card for one of the grid's items.</summary>
    protected virtual object MakeCard(BaseItem item) => GridStyle switch
    {
        CardStyle.Landscape => ItemCard.Landscape(Navigator, item),
        CardStyle.Square => ItemCard.Square(Navigator, item),
        CardStyle.Round => ItemCard.Round(Navigator, item),
        _ => ItemCard.Poster(Navigator, item),
    };

    /// <summary>
    /// The grid's cards when the page has them in hand (audiobooks, which
    /// are gathered from their files), rather than paging them in.
    /// </summary>
    protected virtual IReadOnlyList<object>? LocalCards => null;

    /// <summary>Whether the grid is in letter sections when sorted by name.</summary>
    protected virtual bool IndexesByName => true;

    // ---- Sorting and sections ----

    /// <summary>The ways the grid can be ordered; the first is by name.</summary>
    protected virtual IReadOnlyList<SortOption> Sorts { get; } = Enum.GetValues<LibrarySort>()
        .Select(s => new SortOption(s.Title, s.Query.SortBy, s.Query.Order)).ToList();

    public IReadOnlyList<string> SortTitles { get; }
    public virtual bool ShowsSort => HasGrid && Sorts.Count > 1;

    [ObservableProperty] public partial int SortIndex { get; set; }

    private bool SortedByName => SortIndex == 0;

    /// <summary>The page has loaded once, so a new sort reloads its grid.</summary>
    private bool started;

    partial void OnSortIndexChanged(int value)
    {
        if (started) _ = ReloadGridAsync();
    }

    /// <summary>The grid's query sorted as chosen, for a page with the library's own sorts (<see cref="LibrarySort"/>).</summary>
    protected ItemQuery SortedQuery()
    {
        var sort = (LibrarySort)Math.Clamp(SortIndex, 0, Enum.GetValues<LibrarySort>().Length - 1);
        return new ItemQuery { Sort = sort, Descending = sort != LibrarySort.Name };
    }

    /// <summary>The sort chosen as Jellyfin's parameters, for a page with sorts of its own (music).</summary>
    protected Query SortQuery()
    {
        var sort = Sorts[Math.Clamp(SortIndex, 0, Sorts.Count - 1)];
        return new() { ["sortBy"] = sort.SortBy, ["sortOrder"] = sort.Order };
    }

    /// <summary>Albums / Artists / Playlists / Songs, for a page with sections.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSections))]
    public partial IReadOnlyList<Choice> Sections { get; protected set; } = [];

    public bool HasSections => Sections.Count > 0;

    // ---- Building the page ----

    /// <summary>Loads everything above the grid; returns its rows.</summary>
    protected abstract Task<IReadOnlyList<BrowseRow>> LoadTopAsync();

    /// <summary>Loads the page, as when first shown or on a new section.</summary>
    protected async Task LoadAsync()
    {
        started = true;
        if (Rows.Count == 0) Rows.Add(new StatusRow("Loading…", IsLoading: true));
        try
        {
            top = await LoadTopAsync();
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await Navigator.SessionExpiredAsync();
            top = [new StatusRow("Couldn't load", error.Message)];
        }
        RebuildTop();
        await ReloadGridAsync();
    }

    public virtual void Refresh() => _ = RefreshTopAsync();

    /// <summary>Freshens the rows above the grid (what's in progress); the grid stays where it was scrolled.</summary>
    private async Task RefreshTopAsync()
    {
        try { top = await LoadTopAsync(); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return; }
        RebuildTop();
        if (LocalCards is not null) RebuildGrid();
    }

    /// <summary>Everything when sorted by name (for the sections), else page by page as the grid is scrolled.</summary>
    protected async Task ReloadGridAsync()
    {
        grid.Reset();
        sections = [];
        IndexKeys = [];
        if (!HasGrid || LocalCards is not null)
        {
            RebuildGrid();
            return;
        }
        RebuildGrid();
        if (!await grid.LoadNextAsync(GridExcluding, FetchGridPage))
        {
            RebuildGrid();
            return;
        }
        if (SortedByName && IndexesByName)
        {
            RebuildGrid();
            while (grid.HasMore && await grid.LoadNextAsync(GridExcluding, FetchGridPage)) { }
            sections = grid.HasMore ? [] : LibraryIndex.Sections(grid.Items);
            IndexKeys = sections.Count > 1 ? sections.Select(s => s.Key).ToList() : [];
        }
        RebuildGrid();
    }

    /// <summary>The grid is near the bottom of the window: the next page, unless it's loaded whole.</summary>
    public async Task LoadMoreAsync()
    {
        if (!HasGrid || LocalCards is not null || (SortedByName && IndexesByName) || !grid.HasMore || grid.IsLoading) return;
        if (await grid.LoadNextAsync(GridExcluding, FetchGridPage)) RebuildGrid();
    }

    /// <summary>The items in the grid so far, for playing a list from one of them.</summary>
    protected IReadOnlyList<BaseItem> GridItems => grid.Items;

    /// <summary>How many cards fit across, from the window's width; the grid is laid out again when it changes.</summary>
    public void SetColumns(int count)
    {
        count = Math.Max(1, count);
        if (count == columns) return;
        columns = count;
        RebuildGrid();
    }

    /// <summary>The row a letter's section starts at, to scroll to.</summary>
    public int RowOfSection(string key)
    {
        for (var i = Math.Max(gridRowsStart, 0); i < Rows.Count; i++)
        {
            if (Rows[i] is HeadingRow { IsLetter: true } heading && heading.Title == key) return i;
        }
        return -1;
    }

    private void RebuildTop()
    {
        var gridRows = gridRowsStart >= 0 ? Rows.Skip(gridRowsStart).ToList() : [];
        Rows.Clear();
        // A title with no banner above it steps down, clear of the collapsed sidebar's capsule.
        for (var index = 0; index < top.Count; index++)
            Rows.Add(index == 0 && top[index] is TitleRow { IsFirst: false } title && !IsPushed ? title with { IsFirst = true } : top[index]);
        gridRowsStart = Rows.Count;
        foreach (var row in gridRows) Rows.Add(row);
    }

    /// <summary>
    /// Lays the grid out again below the rows above it. Rows that haven't
    /// changed are left alone, so new pages arriving don't disturb the ones
    /// already on screen.
    /// </summary>
    protected void RebuildGrid()
    {
        if (gridRowsStart < 0) gridRowsStart = Rows.Count;
        var rows = new List<BrowseRow>();
        if (LocalCards is { } local)
        {
            if (local.Count > 0 && GridHeading is { } localHeading) rows.Add(new HeadingRow(localHeading));
            rows.AddRange(CardRows(local));
        }
        else if (HasGrid)
        {
            if (GridHeading is { } heading && (grid.Items.Count > 0 || grid.IsLoading)) rows.Add(new HeadingRow(heading));
            if (grid.Items.Count == 0)
            {
                rows.Add(grid.ErrorMessage is { } error ? new StatusRow("Couldn't load", error)
                    : grid.IsLoading ? new StatusRow("Loading…", IsLoading: true)
                    : new StatusRow("Nothing here yet"));
            }
            else if (sections.Count > 1)
            {
                foreach (var section in sections)
                {
                    rows.Add(new HeadingRow(section.Key, IsLetter: true));
                    rows.AddRange(CardRows(section.Items.Select(MakeCard)));
                }
            }
            else
            {
                rows.AddRange(CardRows(grid.Items.Select(MakeCard)));
                if (grid.HasMore && !(SortedByName && IndexesByName)) rows.Add(new StatusRow("Loading…", IsLoading: true));
            }
        }

        // Keep what's the same; replace from the first difference.
        var at = 0;
        while (at < rows.Count && gridRowsStart + at < Rows.Count && SameRow(Rows[gridRowsStart + at], rows[at])) at++;
        while (Rows.Count > gridRowsStart + at) Rows.RemoveAt(Rows.Count - 1);
        foreach (var row in rows.Skip(at)) Rows.Add(row);
    }

    private static bool SameRow(BrowseRow a, BrowseRow b) => (a, b) switch
    {
        (CardsRow x, CardsRow y) => x.Style == y.Style && x.Cards.Select(Key).SequenceEqual(y.Cards.Select(Key)),
        _ => a == b,
    };

    private static string Key(object card) => card is ICard keyed ? keyed.Key : card.GetHashCode().ToString();

    /// <summary>Cards in rows of as many as fit; songs one to a row.</summary>
    private IEnumerable<BrowseRow> CardRows(IEnumerable<object> cards) =>
        cards.Chunk(GridStyle == CardStyle.Track ? 1 : columns).Select(chunk => new CardsRow(chunk, GridStyle));

    protected ShelfRow? Shelf(string title, IEnumerable<object> cards, CardStyle style)
    {
        var list = cards.ToList();
        return list.Count > 0 ? new ShelfRow(title, list, style) : null;
    }

    protected ShelfRow? LandscapeShelf(string title, IEnumerable<BaseItem> items, bool showSeriesTitle = true) =>
        Shelf(title, items.Select(i => ItemCard.Landscape(Navigator, i, showSeriesTitle)), CardStyle.Landscape);

    protected ShelfRow? PosterShelf(string title, IEnumerable<BaseItem> items) =>
        Shelf(title, items.Select(i => ItemCard.Poster(Navigator, i)), CardStyle.Poster);

    protected ShelfRow? TileShelf(string title, IEnumerable<BaseItem> libraries) =>
        Shelf(title, libraries.Select(l => ItemCard.Tile(Navigator, l)), CardStyle.Tile);

    protected ShelfRow? SquareShelf(string title, IEnumerable<BaseItem> items) =>
        Shelf(title, items.Select(i => ItemCard.Square(Navigator, i)), CardStyle.Square);
}
