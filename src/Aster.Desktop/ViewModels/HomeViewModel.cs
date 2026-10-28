using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The Apple TV app's Home: a featured banner, then shelves of what to
/// watch: Continue Watching, Up Next, the newest movies, shows and anime,
/// and one shelf for each other library.
/// </summary>
public partial class HomeViewModel : ViewModelBase, IRefreshable
{
    private readonly INavigator navigator;
    private bool refreshWanted;

    public HomeViewModel(INavigator navigator)
    {
        this.navigator = navigator;
        Hero = new HeroModel(navigator);
        _ = LoadAsync();
    }

    public HeroModel Hero { get; }

    [ObservableProperty] public partial IReadOnlyList<Shelf> Shelves { get; set; } = [];
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public void Refresh() => _ = LoadAsync();

    /// <summary>Refreshes the next time Home is shown.</summary>
    public void RefreshLater() => refreshWanted = true;

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        refreshWanted = false;
        IsLoading = !Hero.HasPages;
        ErrorMessage = null;
        var client = navigator.Client;
        try
        {
            var resume = client.ResumeAsync(limit: 20);
            var next = client.NextUpAsync(limit: 20);
            var libraries = await client.UserViewsAsync();
            // Anime has its own row, so the Movies and TV rows skip its libraries.
            var animeLibraries = (await navigator.Anime).Libraries;
            var animeIds = animeLibraries.Select(l => l.Id).ToHashSet();
            var moviesLibrary = libraries.FirstOrDefault(l => l.CollectionType == "movies" && !animeIds.Contains(l.Id))?.Id;
            var showsLibrary = libraries.FirstOrDefault(l => l.CollectionType == "tvshows" && !animeIds.Contains(l.Id))?.Id;
            var movies = OrEmpty(client.LatestAsync(moviesLibrary, 20, "Movie"));
            var shows = OrEmpty(client.LatestAsync(showsLibrary, 20, "Series"));
            var anime = AnimeCatalog.RecentlyAddedAsync(client, animeLibraries);
            var others = libraries
                .Where(l => l.CollectionType is not ("movies" or "tvshows" or "music" or "playlists" or "livetv" or "books" or "photos"))
                .Select(async l => (Library: l, Items: await OrEmpty(client.LatestAsync(l.Id, 20))))
                .ToList();

            var resumeItems = await resume;
            var nextItems = (await next).Where(e => resumeItems.All(r => r.Id != e.Id)).ToList();
            var movieItems = await movies;
            var showItems = await shows;

            var shelves = new List<Shelf>();
            void Add(string title, IEnumerable<BaseItem> items)
            {
                var cards = items.Select(i => ItemCard.Landscape(navigator, i)).ToList();
                if (cards.Count > 0) shelves.Add(new Shelf(title, cards));
            }
            Add("Continue Watching", resumeItems);
            Add("Up Next", nextItems);
            Add("Recently Added Movies", movieItems);
            Add("Recently Added TV Shows", showItems);
            Add("Recently Added Anime", await anime);
            foreach (var (library, items) in await Task.WhenAll(others)) Add(library.DisplayTitle, items);

            Hero.Show(resumeItems.Take(3).Concat(nextItems.Take(3)).Concat(movieItems.Take(4)).Concat(showItems.Take(3)));
            Shelves = shelves;
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            else ErrorMessage = error.Message;
        }
        IsLoading = false;
    }

    /// <summary>Called as Home comes back into view.</summary>
    public void Shown()
    {
        if (refreshWanted) Refresh();
    }

    private static Task<IReadOnlyList<BaseItem>> OrEmpty(Task<IReadOnlyList<BaseItem>> request) => AnimeCatalog.OrEmpty(request);
}
