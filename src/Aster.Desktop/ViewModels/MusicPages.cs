// Music browsing: albums, artists, playlists and songs, with album and
// artist pages. A port of the Apple app's MusicView.swift. (Add to
// Playlist waits on the playlist picker, which isn't ported yet.)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using Aster.Desktop.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Query = System.Collections.Generic.Dictionary<string, string?>;

namespace Aster.Desktop.ViewModels;

/// <summary>A row's action in its right-click menu.</summary>
public sealed record TrackAction(string Title, IRelayCommand Command);

/// <summary>
/// One song in a list: its number or artwork, title, artist and length.
/// Clicking plays the list from it; extra actions are on right-click.
/// </summary>
public sealed class TrackItem(BaseItem track, int? number, bool showArtist, Uri? artwork, bool isCurrent, Action play,
    IReadOnlyList<TrackAction>? actions = null) : ICard
{
    public BaseItem Track { get; } = track;
    public string Key => Track.PlaylistRowId;
    public string Title => Track.DisplayTitle;
    public string? Artist => showArtist ? Track.ArtistText : null;
    public string? Number => isCurrent ? null : number?.ToString();
    public bool IsCurrent => isCurrent;
    public Uri? Artwork => artwork;
    public bool ShowsArtwork => artwork is not null;
    public bool ShowsNumber => artwork is null;
    public bool IsFavorite => Track.UserData?.IsFavorite == true;
    public string? Length => Track.TrackDurationText;
    public IReadOnlyList<TrackAction> Actions { get; } = actions ?? [];
    public bool HasActions => Actions.Count > 0;
    public IRelayCommand Play { get; } = new RelayCommand(play);
}

/// <summary>
/// The Music tab, or one music library opened from Library: albums (with
/// what's new, recently played and favourite above them), artists,
/// playlists or every song.
/// </summary>
public sealed class MusicViewModel : BrowsePageViewModel
{
    private enum Section { Albums, Artists, Playlists, Songs }

    private readonly BaseItem? library;
    private Section section = Section.Albums;

    public MusicViewModel(INavigator navigator, BaseItem? library = null) : base(navigator)
    {
        this.library = library;
        UpdateSections();
        _ = LoadAsync();
    }

    private void UpdateSections() => Sections = Enum.GetValues<Section>()
        .Select(s => new Choice(s.ToString(), s == section, new RelayCommand(() =>
        {
            if (s == section) return;
            section = s;
            SortIndex = 0;
            UpdateSections();
            OnPropertyChanged(nameof(ShowsSort));
            _ = LoadAsync();
        })))
        .ToList();

    protected override bool HasGrid => true;
    public override bool ShowsSort => section == Section.Albums;
    protected override bool IndexesByName => section is Section.Albums or Section.Artists;

    public override CardStyle GridStyle => section switch
    {
        Section.Artists => CardStyle.Round,
        Section.Songs => CardStyle.Track,
        _ => CardStyle.Square,
    };

    protected override string GridHeading => section switch
    {
        Section.Albums => "All Albums",
        _ => section.ToString(),
    };

    protected override IReadOnlyList<SortOption> Sorts { get; } =
    [
        new("Name", "SortName", "Ascending"),
        new("Recently Added", "DateCreated,SortName", "Descending"),
        new("Artist", "AlbumArtist,SortName", "Ascending"),
        new("Year", "ProductionYear,SortName", "Descending"),
    ];

    /// <summary>The grid's titles as Jellyfin's parameters: music is Jellyfin's (and Emby's) alone.</summary>
    private Query MusicQuery() => section switch
    {
        Section.Albums => new(SortQuery()) { ["parentId"] = library?.Id, ["includeItemTypes"] = "MusicAlbum", ["recursive"] = "true" },
        Section.Playlists => new() { ["includeItemTypes"] = "Playlist", ["mediaTypes"] = "Audio", ["recursive"] = "true", ["sortBy"] = "SortName", ["sortOrder"] = "Ascending" },
        Section.Songs => new() { ["parentId"] = library?.Id, ["includeItemTypes"] = "Audio", ["recursive"] = "true", ["sortBy"] = "SortName", ["sortOrder"] = "Ascending" },
        _ => [],
    };

    /// <summary>Artists come from their own route, credited on albums.</summary>
    protected override Task<ItemsResult> FetchGridPage(int start, int size) => section == Section.Artists
        ? Navigator.JellyfinOnly.AlbumArtistsAsync(library?.Id, start, size)
        : Navigator.JellyfinOnly.JellyfinItemsAsync(new Query(MusicQuery())
        {
            ["startIndex"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    protected override object MakeCard(BaseItem item)
    {
        if (section != Section.Songs) return base.MakeCard(item);
        return new TrackItem(item, null, showArtist: true, Navigator.JellyfinOnly.PosterUrl(item, width: 120),
            Navigator.Audio.Music.Current?.Id == item.Id, () =>
            {
                var songs = GridItems;
                Navigator.Audio.Music.Play(songs, songs.ToList().FindIndex(s => s.Id == item.Id), shuffled: false, Navigator.JellyfinOnly);
            },
            PlaylistActions.CanAdd(Navigator) ? [new("Add to Playlist…", new RelayCommand(() => PlaylistActions.Pick(Navigator, [item.Id], "Audio")))] : null);
    }

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var rows = new List<BrowseRow> { new TitleRow(library?.DisplayTitle ?? "Music", this) };
        if (section != Section.Albums) return rows;

        var client = Navigator.JellyfinOnly;
        var recent = AnimeCatalog.OrEmpty(client.LatestAsync(library?.Id, 20, "MusicAlbum"));
        var played = OrEmpty(client.JellyfinItemsAsync(new()
        {
            ["parentId"] = library?.Id, ["includeItemTypes"] = "Audio", ["recursive"] = "true",
            ["filters"] = "IsPlayed", ["sortBy"] = "DatePlayed", ["sortOrder"] = "Descending", ["limit"] = "20",
        }));
        var favorites = OrEmpty(client.JellyfinItemsAsync(new()
        {
            ["parentId"] = library?.Id, ["includeItemTypes"] = "MusicAlbum", ["recursive"] = "true",
            ["filters"] = "IsFavorite", ["sortBy"] = "SortName", ["limit"] = "20",
        }));
        var recentlyPlayed = await played;
        rows.AddRange(new BrowseRow?[]
        {
            SquareShelf("Recently Added", await recent),
            // A song here plays the shelf from it.
            Shelf("Recently Played", recentlyPlayed.Select((song, i) => ItemCard.Square(Navigator, song,
                () => Navigator.Audio.Music.Play(recentlyPlayed, i, shuffled: false, Navigator.JellyfinOnly))), CardStyle.Square),
            SquareShelf("Favorite Albums", await favorites),
        }.OfType<BrowseRow>());
        return rows;
    }

    private static async Task<IReadOnlyList<BaseItem>> OrEmpty(Task<ItemsResult> request)
    {
        try { return (await request).Items; }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return []; }
    }
}

/// <summary>An album or playlist: artwork, details, Play and Shuffle, and the songs, by disc.</summary>
public sealed partial class AlbumViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly INavigator navigator;
    private readonly string id;
    private IReadOnlyList<BaseItem> tracks = [];

    public AlbumViewModel(INavigator navigator, BaseItem item)
    {
        this.navigator = navigator;
        id = item.Id;
        Item = item;
        IsPlaylist = item.Kind == ItemKind.Playlist;
        navigator.Audio.Music.PropertyChanged += MusicChanged;
        _ = LoadAsync();
    }

    /// <summary>The song playing is marked in the list.</summary>
    private void MusicChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlayer.Current)) BuildRows();
    }

    public void Dispose() => navigator.Audio.Music.PropertyChanged -= MusicChanged;

    public bool IsPlaylist { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Artwork), nameof(ArtistName), nameof(CanOpenArtist), nameof(Overview), nameof(IsFavorite), nameof(InfoLine))]
    public partial BaseItem Item { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<object> Rows { get; private set; } = [];
    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;
    [ObservableProperty] public partial string? Message { get; private set; }

    public string Title => Item.DisplayTitle;
    public Uri? Artwork => navigator.JellyfinOnly.PosterUrl(Item, width: 800);
    public Uri? Backdrop => navigator.JellyfinOnly.PosterUrl(Item, width: 400);
    public string? ArtistName => IsPlaylist ? null : Item.PrimaryArtist?.Name ?? Item.ArtistText;
    public bool CanOpenArtist => !IsPlaylist && Item.PrimaryArtist?.Id is not null;
    public string? Overview => string.IsNullOrEmpty(Item.Overview) ? null : Item.Overview;
    public bool IsFavorite => Item.UserData?.IsFavorite == true;
    public bool HasTracks => tracks.Count > 0;

    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (Item.YearText is { } year) parts.Add(year);
            if (Item.Genres?.FirstOrDefault() is { } genre) parts.Add(genre);
            parts.Add($"{tracks.Count} song{(tracks.Count == 1 ? "" : "s")}");
            if (MusicFormat.Length(tracks.Sum(t => t.RunTimeTicks ?? 0)) is { } length) parts.Add(length);
            return string.Join(" · ", parts);
        }
    }

    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var client = navigator.JellyfinOnly;
        try
        {
            var full = client.ItemAsync(id);
            tracks = IsPlaylist ? await client.PlaylistItemsAsync(id) : await client.AlbumTracksAsync(id);
            Item = await full;
            Message = tracks.Count == 0 ? IsPlaylist ? "This playlist is empty" : "No songs found" : null;
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            Message = error.Message;
        }
        IsLoading = false;
        OnPropertyChanged(nameof(InfoLine));
        OnPropertyChanged(nameof(HasTracks));
        BuildRows();
    }

    /// <summary>The songs, under a heading for each disc when there's more than one.</summary>
    private void BuildRows()
    {
        var current = navigator.Audio.Music.Current?.Id;
        var discs = tracks.Select(t => t.ParentIndexNumber).OfType<int>().Distinct().Order().ToList();
        var rows = new List<object>();
        foreach (var disc in discs.Count > 1 ? discs.Cast<int?>() : [null])
        {
            if (disc is { } number) rows.Add(new HeadingRow($"Disc {number}"));
            foreach (var song in tracks.Where(t => disc is null || t.ParentIndexNumber == disc))
            {
                var position = tracks.ToList().FindIndex(t => t.PlaylistRowId == song.PlaylistRowId);
                rows.Add(new TrackItem(song, IsPlaylist ? position + 1 : song.IndexNumber,
                    showArtist: IsPlaylist || song.ArtistText != Item.ArtistText, artwork: null, song.Id == current,
                    () => PlayFrom(position, shuffled: false), RowActions(song, position)));
            }
        }
        Rows = rows;
    }

    /// <summary>On a playlist's own page: reorder and remove.</summary>
    private IReadOnlyList<TrackAction> RowActions(BaseItem song, int position)
    {
        var actions = new List<TrackAction>();
        if (PlaylistActions.CanAdd(navigator)) actions.Add(new("Add to Playlist…", new RelayCommand(() => PlaylistActions.Pick(navigator, [song.Id], "Audio"))));
        if (!IsPlaylist || song.PlaylistItemId is null) return actions;
        if (position > 0) actions.Add(new("Move Up", new AsyncRelayCommand(() => MoveAsync(song, position - 1))));
        if (position < tracks.Count - 1) actions.Add(new("Move Down", new AsyncRelayCommand(() => MoveAsync(song, position + 1))));
        actions.Add(new("Remove from Playlist", new AsyncRelayCommand(() => RemoveAsync(song))));
        return actions;
    }

    private async Task MoveAsync(BaseItem song, int newIndex)
    {
        var before = tracks;
        var list = tracks.ToList();
        var from = list.FindIndex(t => t.PlaylistRowId == song.PlaylistRowId);
        var moved = list[from];
        list.RemoveAt(from);
        list.Insert(newIndex, moved);
        tracks = list;
        BuildRows();
        try { await navigator.JellyfinOnly.MovePlaylistItemAsync(id, song.PlaylistItemId!, newIndex); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            tracks = before;
            BuildRows();
            Message = error.Message;
        }
    }

    private async Task RemoveAsync(BaseItem song)
    {
        var before = tracks;
        tracks = tracks.Where(t => t.PlaylistRowId != song.PlaylistRowId).ToList();
        BuildRows();
        try { await navigator.JellyfinOnly.RemoveFromPlaylistAsync(id, [song.PlaylistItemId!]); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            tracks = before;
            BuildRows();
            Message = error.Message;
        }
    }

    private void PlayFrom(int position, bool shuffled)
    {
        if (tracks.Count > 0) navigator.Audio.Music.Play(tracks, position, shuffled, navigator.JellyfinOnly);
    }

    [RelayCommand] private void Play() => PlayFrom(0, shuffled: false);
    [RelayCommand] private void Shuffle() => PlayFrom(Random.Shared.Next(Math.Max(tracks.Count, 1)), shuffled: true);

    /// <summary>Every song on the page into a playlist.</summary>
    public bool CanAddToPlaylist => PlaylistActions.CanAdd(navigator);

    [RelayCommand] private void AddToPlaylist() => PlaylistActions.Pick(navigator, tracks.Select(t => t.Id).ToList(), "Audio");

    [RelayCommand]
    private async Task InstantMixAsync()
    {
        IReadOnlyList<BaseItem> mix;
        try { mix = await navigator.JellyfinOnly.InstantMixAsync(id); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { mix = tracks; }
        navigator.Audio.Music.Play(mix, 0, shuffled: false, navigator.JellyfinOnly);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        var favorite = !IsFavorite;
        Item = Item with { UserData = (Item.UserData ?? new UserItemData()) with { IsFavorite = favorite } };
        try { await navigator.JellyfinOnly.SetFavoriteAsync(id, favorite); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
    }

    [RelayCommand]
    private void OpenArtist()
    {
        if (Item.PrimaryArtist is { Id: { } artistId } artist) navigator.Open(new BaseItem { Id = artistId, Name = artist.Name, Type = "MusicArtist" });
    }

    [RelayCommand] private void Back() => navigator.Back();
}

/// <summary>An artist: photo, details, Play, Shuffle and Instant Mix, and their albums.</summary>
public sealed partial class ArtistViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly string id;

    public ArtistViewModel(INavigator navigator, BaseItem artist)
    {
        this.navigator = navigator;
        id = artist.Id;
        Item = artist;
        _ = LoadAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Photo), nameof(Backdrop), nameof(Genres), nameof(Overview), nameof(IsFavorite))]
    public partial BaseItem Item { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<ItemCard> Albums { get; private set; } = [];
    [ObservableProperty] public partial string? Message { get; private set; }

    public string Title => Item.DisplayTitle;
    public Uri? Photo => navigator.JellyfinOnly.PosterUrl(Item, width: 600);
    public Uri? Backdrop => navigator.JellyfinOnly.BackdropUrl(Item, width: 1920) ?? navigator.JellyfinOnly.PosterUrl(Item, width: 800);
    public string? Genres => Item.Genres is { Count: > 0 } genres ? string.Join(" · ", genres.Take(3)) : null;
    public string? Overview => string.IsNullOrEmpty(Item.Overview) ? null : Item.Overview;
    public bool IsFavorite => Item.UserData?.IsFavorite == true;

    private async Task LoadAsync()
    {
        try
        {
            var full = navigator.JellyfinOnly.ItemAsync(id);
            Albums = (await navigator.JellyfinOnly.ArtistAlbumsAsync(id)).Select(a => ItemCard.Square(navigator, a)).ToList();
            Item = await full;
            Message = Albums.Count == 0 ? "No albums found" : null;
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            Message = error.Message;
        }
    }

    private async Task PlayAllAsync(bool shuffled)
    {
        IReadOnlyList<BaseItem> songs;
        try { songs = await navigator.JellyfinOnly.ArtistTracksAsync(id); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return; }
        navigator.Audio.Music.Play(songs, shuffled ? Random.Shared.Next(Math.Max(songs.Count, 1)) : 0, shuffled, navigator.JellyfinOnly);
    }

    [RelayCommand] private Task PlayAsync() => PlayAllAsync(shuffled: false);
    [RelayCommand] private Task ShuffleAsync() => PlayAllAsync(shuffled: true);

    [RelayCommand]
    private async Task InstantMixAsync()
    {
        try { navigator.Audio.Music.Play(await navigator.JellyfinOnly.InstantMixAsync(id), 0, shuffled: false, navigator.JellyfinOnly); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        var favorite = !IsFavorite;
        Item = Item with { UserData = (Item.UserData ?? new UserItemData()) with { IsFavorite = favorite } };
        try { await navigator.JellyfinOnly.SetFavoriteAsync(id, favorite); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
    }

    [RelayCommand] private void Back() => navigator.Back();
}
