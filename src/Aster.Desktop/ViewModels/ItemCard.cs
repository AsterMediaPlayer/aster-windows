using System;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// A title as a card in a shelf: its picture, its two lines, its progress,
/// and the right-click menu (the Apple app's long-press menu). A change made
/// from the menu shows at once and is put back if the server refuses it.
/// </summary>
public partial class ItemCard : ObservableObject, ICard
{
    private readonly INavigator navigator;
    private readonly bool showSeriesTitle;

    private readonly Action? activate;

    /// <param name="activate">What a click does in place of opening the title's page (a song in a shelf plays its list).</param>
    public ItemCard(INavigator navigator, BaseItem item, Uri? image, bool showSeriesTitle = true, bool clickPlays = false, Action? activate = null)
    {
        this.navigator = navigator;
        this.showSeriesTitle = showSeriesTitle;
        this.activate = activate;
        ClickPlays = clickPlays;
        Item = item;
        Image = image;
    }

    public string Key => Item.PlaylistRowId;

    /// <summary>Square artwork: an album, a playlist or a song.</summary>
    public static ItemCard Square(INavigator navigator, BaseItem item, Action? activate = null) =>
        new(navigator, item, navigator.Client.PosterUrl(item, width: 400), activate: activate);

    /// <summary>An artist, round.</summary>
    public static ItemCard Round(INavigator navigator, BaseItem artist) =>
        new(navigator, artist, navigator.Client.PosterUrl(artist, width: 400));

    /// <summary>A special feature: plays when clicked, rather than opening a page.</summary>
    public static ItemCard Extra(INavigator navigator, BaseItem item) =>
        new(navigator, item, navigator.Client.LandscapeUrl(item, width: 800), clickPlays: true);

    /// <summary>Clicking plays at once (an extra), with a play symbol over the picture.</summary>
    public bool ClickPlays { get; }

    /// <summary>A 16:9 card: an episode's still, or a title's backdrop or thumb.</summary>
    public static ItemCard Landscape(INavigator navigator, BaseItem item, bool showSeriesTitle = true) =>
        new(navigator, item, navigator.Client.LandscapeUrl(item, width: 800), showSeriesTitle);

    /// <summary>A 2:3 poster card, for grids. At quality 80 the picture is about 40% smaller than at 90, with no difference at card size.</summary>
    public static ItemCard Poster(INavigator navigator, BaseItem item) =>
        new(navigator, item, navigator.Client.PosterUrl(item, width: 330, quality: 80));

    /// <summary>A library as a wide tile with its name over it.</summary>
    public static ItemCard Tile(INavigator navigator, BaseItem library) =>
        new(navigator, library, navigator.Client.LandscapeUrl(library, width: 800));

    /// <summary>A round photo of someone in the cast or crew.</summary>
    public static ItemCard Person(INavigator navigator, PersonInfo person) =>
        new(navigator, new BaseItem { Id = person.Id, Name = person.Name, Type = "Person", Overview = person.Role }, navigator.Client.PersonImageUrl(person, width: 300));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress), nameof(HasProgress), nameof(ShowsUnplayed), nameof(WatchedLabel), nameof(FavoriteLabel), nameof(CanPlayFromBeginning), nameof(PlayLabel))]
    public partial BaseItem Item { get; set; }

    public Uri? Image { get; }

    public string Title => Item.Kind == ItemKind.Episode && showSeriesTitle && Item.SeriesName is { } series ? series : Item.DisplayTitle;

    public string? Subtitle => Item.Kind switch
    {
        ItemKind.Episode => string.Join(" · ", new[] { Item.EpisodeCode, showSeriesTitle ? Item.Name : null }.OfType<string>()) is { Length: > 0 } line ? line : null,
        ItemKind.Series or ItemKind.Movie => Item.YearText,
        ItemKind.Person => Item.Overview,
        ItemKind.MusicAlbum => Item.ArtistText ?? Item.YearText,
        ItemKind.Audio => Item.ArtistText ?? Item.Album,
        ItemKind.Playlist when Item.MediaType == "Audio" => Item.ChildCount is { } count ? $"{count} song{(count == 1 ? "" : "s")}" : null,
        ItemKind.MusicArtist => null,
        _ => Item.RuntimeText,
    };

    public double Progress => (Item.ProgressFraction ?? 0) * 100;
    public bool HasProgress => Item.ProgressFraction is not null;

    /// <summary>The dot on an episode not yet started.</summary>
    public bool ShowsUnplayed => Item.Kind == ItemKind.Episode && Item.UserData?.Played != true && Item.ProgressFraction is null;

    /// <summary>The glyph shown while the picture loads, or without one.</summary>
    public string Placeholder => Item.Kind switch
    {
        ItemKind.Episode or ItemKind.Series or ItemKind.Season => "",
        ItemKind.Person => "",
        ItemKind.MusicAlbum or ItemKind.Audio or ItemKind.MusicArtist => "",
        ItemKind.Playlist when Item.MediaType == "Audio" => "",
        _ when Item.CollectionType is "tvshows" => "",
        _ when Item.CollectionType is "music" => "",
        _ when Item.CollectionType is "boxsets" or "playlists" => "",
        _ when Item.CollectionType is not null => "",
        _ => "",
    };

    // ---- The menu ----

    /// <summary>Movies, shows and episodes have the menu; other cards just open.</summary>
    public bool HasMenu => Item.Kind is ItemKind.Movie or ItemKind.Series or ItemKind.Season or ItemKind.Episode
        or ItemKind.Video or ItemKind.MusicVideo or ItemKind.Recording;

    /// <summary>The menu's favourite, where the server keeps favourites (Plex doesn't).</summary>
    public bool CanFavorite => HasMenu && navigator.Client.Can(ServerCapabilities.Favorites);

    /// <summary>A movie, show, season or episode can go in a video playlist (a show or season as its episodes).</summary>
    public bool CanAddToPlaylist => HasMenu && PlaylistActions.CanAdd(navigator);

    [RelayCommand] private Task AddToPlaylistAsync() => PlaylistActions.PickForAsync(navigator, Item);

    private bool PlaysItself => Item.Kind is not (ItemKind.Series or ItemKind.Season);
    private long ResumeTicks => Item.UserData?.PlaybackPositionTicks ?? 0;

    public bool CanPlay => HasMenu && PlaysItself;
    public string PlayLabel => ResumeTicks > 0 ? "Resume" : "Play";
    public bool CanPlayFromBeginning => CanPlay && ResumeTicks > 0;
    public bool CanGoToSeries => Item.Kind is ItemKind.Episode or ItemKind.Season && Item.SeriesId is not null;
    public bool CanGoToSeason => Item.Kind == ItemKind.Episode && Item.SeasonId is not null;
    public string WatchedLabel => Item.UserData?.Played == true ? "Mark as Unwatched" : "Mark as Watched";
    public string FavoriteLabel => Item.UserData?.IsFavorite == true ? "Remove from Favorites" : "Add to Favorites";

    /// <summary>A click: the title's page, or for an extra, playing it.</summary>
    [RelayCommand]
    private void Open()
    {
        if (activate is not null) activate();
        else if (ClickPlays) PlayFromBeginning();
        else navigator.Open(Item);
    }

    [RelayCommand] private void Play() => navigator.Play(new PlaybackQueueItem { ItemId = Item.Id, StartTicks = ResumeTicks });

    [RelayCommand] private void PlayFromBeginning() => navigator.Play(new PlaybackQueueItem { ItemId = Item.Id });

    [RelayCommand]
    private void GoToSeries()
    {
        if (Item.SeriesId is { } id) navigator.Open(new BaseItem { Id = id, Name = Item.SeriesName, Type = "Series" });
    }

    [RelayCommand]
    private void GoToSeason()
    {
        if (Item.SeasonId is { } id) navigator.Open(new BaseItem { Id = id, Name = Item.SeasonName, Type = "Season", SeriesId = Item.SeriesId, SeriesName = Item.SeriesName });
    }

    [RelayCommand]
    private Task ToggleWatchedAsync()
    {
        var played = Item.UserData?.Played != true;
        var data = (Item.UserData ?? new UserItemData()) with { Played = played };
        if (played) data = data with { PlaybackPositionTicks = 0, PlayedPercentage = null, UnplayedItemCount = 0 };
        return UpdateAsync(data, client => client.SetPlayedAsync(Item.Id, played));
    }

    [RelayCommand]
    private Task ToggleFavoriteAsync()
    {
        var favorite = Item.UserData?.IsFavorite != true;
        return UpdateAsync((Item.UserData ?? new UserItemData()) with { IsFavorite = favorite }, client => client.SetFavoriteAsync(Item.Id, favorite));
    }

    /// <summary>Shows the change right away and puts it back if the server refuses.</summary>
    private async Task UpdateAsync(UserItemData data, Func<IMediaServer, Task> send)
    {
        var before = Item;
        Item = Item with { UserData = data };
        try
        {
            await send(navigator.Client);
            navigator.UserDataChanged();
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            Item = before;
        }
    }
}
