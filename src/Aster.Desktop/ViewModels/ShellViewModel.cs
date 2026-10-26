using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>What pages ask of the app: open a title, go back, play.</summary>
public interface INavigator
{
    JellyfinClient Client { get; }

    /// <summary>Which libraries hold anime, found once per sign-in.</summary>
    Task<AnimeCatalog> Anime { get; }

    /// <summary>The user's libraries, fetched once per sign-in.</summary>
    Task<IReadOnlyList<BaseItem>> Libraries { get; }

    /// <summary>The song or book playing.</summary>
    Aster.Desktop.Audio.AudioSession Audio { get; }

    /// <summary>Title pages' theme music and videos.</summary>
    Aster.Desktop.Audio.ThemePlayer Theme { get; }

    /// <summary>A book's page.</summary>
    void OpenBook(Aster.Core.Audio.Audiobook book);

    /// <summary>An author's books, or a series.</summary>
    void OpenShelf(Aster.Core.Audio.AudiobookShelf shelf);

    /// <summary>A page of the caller's own over the current one (a programme, a recording rule).</summary>
    void Push(ViewModelBase page);

    /// <summary>
    /// Opens a title's page over the current one. A stub item (id, name,
    /// type) is enough; the page loads the rest. With <paramref name="playOnOpen"/>
    /// it plays at once, the page behind the player.
    /// </summary>
    void Open(BaseItem item, bool playOnOpen = false);

    void Back();

    /// <summary>
    /// Plays over the whole window. <paramref name="closed"/> hears when the
    /// player closes, with the last title it played (an episode it moved on
    /// to by itself, say).
    /// </summary>
    void Play(PlaybackQueueItem entry, Action<BaseItem?>? closed = null);

    /// <summary>
    /// Opens a title's JellyDisc menu over the whole window, from which its
    /// buttons play; <paramref name="closed"/> hears when it closes.
    /// </summary>
    void ShowDiscMenu(Aster.Core.DiscMenu.DiscMenuDocument document, string itemId, Action closed);

    /// <summary>Something's watched or favourite state changed; pages refresh when shown again.</summary>
    void UserDataChanged();

    /// <summary>The server turned the session away (401): back to sign-in.</summary>
    Task SessionExpiredAsync();
}

/// <summary>A page that freshens what it shows when it comes back into view.</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>A tab in the sidebar.</summary>
public sealed record SidebarTab(string Title, string Glyph, bool IsSelected, IRelayCommand Select);

public enum AppTab
{
    Search,
    Home,
    Movies,
    Shows,
    Anime,
    Music,
    Audiobooks,
    Upcoming,
    LiveTV,
    Channels,
    Library,
    Settings,
}

/// <summary>Why the app is leaving the server in use for sign-in, which decides what sign-in starts with.</summary>
public enum SignOutKind
{
    /// <summary>Sign in to another server (or as someone else): the one in use stays saved and signed in, to switch back to.</summary>
    AddServer,

    /// <summary>Sign out of the server in use, which is forgotten; the others stay saved.</summary>
    SignOut,

    /// <summary>The server ended the session itself (401): it's forgotten, its address kept for signing in again.</summary>
    Expired,
}

/// <summary>The servers signed in to, for Settings and sign-in to switch between, add to and forget.</summary>
public interface IServerAccounts
{
    IReadOnlyList<SavedServer> Servers { get; }

    /// <summary>The saved server in use; null on sign-in, or for a development session.</summary>
    string? CurrentId { get; }

    /// <summary>Uses a saved server, with its saved sign-in (asking for its password first, when it's set to): the app starts afresh on it.</summary>
    void SwitchTo(SavedServer server);

    /// <summary>Changes a saved server's settings: its password to switch, its Lock After.</summary>
    void Change(string id, Func<SavedServer, SavedServer> change);

    /// <summary>Forgets a saved server (not the one in use), signing it out.</summary>
    void Remove(string id);
}

/// <summary>
/// The signed-in app: the sidebar and the pages opened from it, each over
/// the last, with Back going down the stack. The Apple app's sidebar also
/// has Upcoming, Live TV and Channels; they join as they're ported.
/// </summary>
public partial class ShellViewModel : ViewModelBase, INavigator, IDisposable
{
    private readonly Action<PlaybackQueueItem, Action<BaseItem?>?> play;
    private readonly Action<Aster.Core.DiscMenu.DiscMenuDocument, string, Action> showDiscMenu;
    private readonly Func<SignOutKind, Task> signOut;
    private readonly IServerAccounts accounts;
    private readonly List<ViewModelBase> stack = [];
    private readonly Dictionary<AppTab, ViewModelBase> tabs = [];
    private AppTab tab = AppTab.Home;
    private bool userDataChanged;
    private readonly HashSet<ViewModelBase> refreshTabs = [];

    /// <param name="signOut">Signs out, saying why.</param>
    /// <param name="showDiscMenu">Shows a disc menu over the window, calling back as it closes.</param>
    public ShellViewModel(JellyfinClient client, StoredSession session, Action<PlaybackQueueItem, Action<BaseItem?>?> play,
        Action<Aster.Core.DiscMenu.DiscMenuDocument, string, Action> showDiscMenu, Func<SignOutKind, Task> signOut, IServerAccounts accounts)
    {
        this.accounts = accounts;
        Client = client;
        this.play = play;
        this.showDiscMenu = showDiscMenu;
        this.signOut = signOut;
        Anime = AnimeCatalog.FindAsync(client);
        Libraries = LoadLibrariesAsync(client);
        HasUpcoming = CheckUpcomingAsync(client);
        HasChannels = CheckChannelsAsync(client);
        Audio = new Aster.Desktop.Audio.AudioSession(client);
        Theme = new Aster.Desktop.Audio.ThemePlayer(client, Audio);
        NowPlaying = new NowPlayingViewModel(this);
        UserName = session.UserName;
        ServerName = session.ServerName ?? session.ServerUrl.Host;
        Home = new HomeViewModel(this);
        tabs[AppTab.Home] = Home;
        CurrentPage = Home;
        UpdateTabs();
        // The Anime, Music and Audiobooks tabs show once it's known there's some.
        _ = Anime.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabs));
        _ = Libraries.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabs));
        _ = HasUpcoming.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabs));
        _ = HasChannels.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabs));
        // The settings that follow the user, as their other devices left them.
        _ = Aster.Core.SettingsSync.PullAsync(client);
    }

    public JellyfinClient Client { get; }
    public Task<AnimeCatalog> Anime { get; }
    public Task<IReadOnlyList<BaseItem>> Libraries { get; }
    public Aster.Desktop.Audio.AudioSession Audio { get; }

    public Aster.Desktop.Audio.ThemePlayer Theme { get; }

    /// <summary>The full player for the song or book, over the window.</summary>
    public NowPlayingViewModel NowPlaying { get; }

    /// <summary>Whether shows in the library have episodes airing soon, which the Upcoming tab needs.</summary>
    private Task<bool> HasUpcoming { get; }

    /// <summary>Whether any video playlist is tagged as a Channel, which the Channels tab needs.</summary>
    private Task<bool> HasChannels { get; }

    private static async Task<bool> CheckChannelsAsync(JellyfinClient client)
    {
        // Channels are playlist tags in Jellyfin's shape; Emby's differ.
        if (!client.IsJellyfin) return false;
        try { return Aster.Core.Channels.ChannelLoader.TaggedChannels(await client.PlaylistsAsync("Video")).Count > 0; }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { return false; }
    }

    private static async Task<bool> CheckUpcomingAsync(JellyfinClient client)
    {
        try { return (await client.UpcomingAsync(limit: 1)).Count > 0; }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { return false; }
    }

    private static async Task<IReadOnlyList<BaseItem>> LoadLibrariesAsync(JellyfinClient client)
    {
        try { return await client.UserViewsAsync(); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { return []; }
    }

    [ObservableProperty] public partial IReadOnlyList<SidebarTab> Tabs { get; set; } = [];

    /// <summary>The tab showing, for the collapsed sidebar's capsule.</summary>
    [ObservableProperty] public partial SidebarTab? CurrentTab { get; private set; }

    /// <summary>
    /// The sidebar slid out over the page. As on the Apple TV it's collapsed
    /// to a capsule (the tab's icon and name) the rest of the time, so the
    /// page has the whole window.
    /// </summary>
    [ObservableProperty] public partial bool IsSidebarOpen { get; set; }

    [RelayCommand] private void OpenSidebar() => IsSidebarOpen = true;
    [RelayCommand] private void CloseSidebar() => IsSidebarOpen = false;

    /// <summary>The capsule shows on a tab's own page; a page opened from one has its Back there instead.</summary>
    public bool ShowsCapsule => !CanGoBack;

    private void UpdateTabs()
    {
        var hasAnime = Anime.IsCompletedSuccessfully && Anime.Result.HasAnime;
        var libraries = Libraries.IsCompletedSuccessfully ? Libraries.Result : [];
        var hasMusic = libraries.Any(l => l.CollectionType == "music");
        var hasBooks = libraries.Any(l => l.CollectionType == "books");
        var all = new (AppTab Tab, string Title, string Glyph)[]
        {
            (AppTab.Home, "Home", ""),
            (AppTab.Movies, "Movies", ""),
            (AppTab.Shows, "TV Shows", ""),
            (AppTab.Anime, "Anime", ""),
            (AppTab.Music, "Music", ""),
            (AppTab.Audiobooks, "Audiobooks", ""),
            (AppTab.Upcoming, "Upcoming", ""),
            (AppTab.LiveTV, "Live TV", ""),
            (AppTab.Channels, "Channels (Beta)", ""),
            (AppTab.Library, "Library", ""),
            // At the foot, as on the Apple TV.
            (AppTab.Settings, "Settings", ""),
            (AppTab.Search, "Search", ""),
        };
        Tabs = all
            .Where(t => t.Tab switch
            {
                AppTab.Anime => hasAnime,
                AppTab.Music => hasMusic,
                AppTab.Audiobooks => hasBooks,
                AppTab.Upcoming => HasUpcoming.IsCompletedSuccessfully && HasUpcoming.Result,
                AppTab.LiveTV => libraries.Any(l => l.CollectionType == "livetv"),
                AppTab.Channels => HasChannels.IsCompletedSuccessfully && HasChannels.Result,
                _ => true,
            })
            .Select(t => new SidebarTab(t.Title, t.Glyph, t.Tab == tab, new RelayCommand(() => ShowTab(t.Tab))))
            .ToList();
        CurrentTab = Tabs.FirstOrDefault(t => t.IsSelected);
    }

    /// <summary>A tab's page, from the top: made the first time, kept after.</summary>
    public void ShowTab(AppTab chosen)
    {
        if (!tabs.TryGetValue(chosen, out var page))
        {
            page = chosen switch
            {
                AppTab.Movies => new TypeGridViewModel(this, ItemKind.Movie),
                AppTab.Shows => new TypeGridViewModel(this, ItemKind.Series),
                AppTab.Anime => new AnimeViewModel(this),
                AppTab.Music => new MusicViewModel(this),
                AppTab.Audiobooks => new AudiobooksViewModel(this),
                AppTab.Upcoming => new UpcomingViewModel(this),
                AppTab.LiveTV => new LiveTVViewModel(this),
                AppTab.Channels => new ChannelsViewModel(this),
                AppTab.Library => new LibraryViewModel(this),
                AppTab.Search => new SearchViewModel(this),
                AppTab.Settings => new SettingsViewModel(this, UserName, ServerName, signOut, accounts),
                _ => Home,
            };
            tabs[chosen] = page;
        }
        tab = chosen;
        IsSidebarOpen = false;
        foreach (var opened in stack.Skip(1).Append(CurrentPage)) if (!tabs.ContainsValue(opened)) Close(opened);
        stack.Clear();
        CurrentPage = page;
        UpdateTabs();
        Shown();
        if (page is SearchViewModel search) search.RequestFocus();
    }
    public HomeViewModel Home { get; }
    public string UserName { get; }
    public string ServerName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(ShowsCapsule), nameof(ShowsThemeVideo))]
    public partial ViewModelBase CurrentPage { get; set; }

    /// <summary>A title's page is in view, so its theme video can show behind it.</summary>
    public bool ShowsThemeVideo => CurrentPage is DetailViewModel;

    // The theme follows the page in view: a title's page plays its own (or
    // carries on with its series'), and any other lets it fade.
    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        Theme.PageChanged(value);
        if (value is DetailViewModel detail) detail.ShowTheme();
    }

    public bool CanGoBack => stack.Count > 0;

    /// <summary>A title's page; a library, collection or playlist opens as a grid of what's in it, and a person as their filmography.</summary>
    public void Open(BaseItem item, bool playOnOpen = false)
    {
        Push(item.Kind switch
        {
            ItemKind.Person => new PersonViewModel(this, item) { IsPushed = true },
            ItemKind.CollectionFolder or ItemKind.UserView or ItemKind.Folder when item.CollectionType == "music" => new MusicViewModel(this, item) { IsPushed = true },
            ItemKind.CollectionFolder or ItemKind.UserView or ItemKind.Folder when item.CollectionType == "books" => new AudiobooksViewModel(this, item) { IsPushed = true },
            ItemKind.MusicAlbum => new AlbumViewModel(this, item),
            ItemKind.Playlist when item.MediaType == "Audio" => new AlbumViewModel(this, item),
            ItemKind.MusicArtist => new ArtistViewModel(this, item),
            // A song opens its album, where it can be played in context.
            ItemKind.Audio when item.AlbumId is { } album => new AlbumViewModel(this, new BaseItem { Id = album, Name = item.Album, Type = "MusicAlbum" }),
            // Found by Search: the file as a book of its own.
            ItemKind.AudioBook => new AudiobookViewModel(this, Aster.Core.Audio.Audiobook.FromItem(item)),
            ItemKind.CollectionFolder or ItemKind.UserView or ItemKind.Folder or ItemKind.BoxSet or ItemKind.Playlist => new LibraryGridViewModel(this, item) { IsPushed = true },
            _ => new DetailViewModel(this, item, playOnOpen),
        });
    }

    public void OpenBook(Aster.Core.Audio.Audiobook book) => Push(new AudiobookViewModel(this, book));

    public void OpenShelf(Aster.Core.Audio.AudiobookShelf shelf) => Push(new AudiobookShelfViewModel(this, shelf) { IsPushed = true });

    public void Push(ViewModelBase page)
    {
        stack.Add(CurrentPage);
        CurrentPage = page;
    }

    /// <summary>A page gone for good lets go of what it watched (the players).</summary>
    private static void Close(ViewModelBase page)
    {
        if (page is IDisposable disposable) disposable.Dispose();
    }

    [RelayCommand]
    public void Back()
    {
        if (stack.Count == 0) return;
        Close(CurrentPage);
        // Off the stack first: the capsule shows by how deep it is as the page changes.
        var previous = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        CurrentPage = previous;
        Shown();
    }


    public void Play(PlaybackQueueItem entry, Action<BaseItem?>? closed = null)
    {
        // A song or book playing pauses for the video, and waits in the bar.
        Audio.Pause();
        Theme.Suspend();
        play(entry, last =>
        {
            Theme.Resume();
            // Watching changes resume points and watched states everywhere.
            userDataChanged = true;
            closed?.Invoke(last);
            Shown(alreadyRefreshed: closed is not null);
        });
    }

    public void ShowDiscMenu(Aster.Core.DiscMenu.DiscMenuDocument document, string itemId, Action closed)
    {
        // As for a title: the song or book and the theme wait while the menu's up.
        Audio.Pause();
        Theme.Suspend();
        showDiscMenu(document, itemId, () =>
        {
            Theme.Resume();
            userDataChanged = true;
            closed();
            Shown(alreadyRefreshed: true);
        });
    }

    public void UserDataChanged() => userDataChanged = true;

    /// <summary>The page in view refreshes if something changed while it was away.</summary>
    private void Shown(bool alreadyRefreshed = false)
    {
        if (userDataChanged)
        {
            userDataChanged = false;
            // The tabs list what's in progress, so they catch up when next shown.
            Home.RefreshLater();
            foreach (var other in tabs.Values) if (other != CurrentPage) refreshTabs.Add(other);
            if (!alreadyRefreshed && CurrentPage != Home && CurrentPage is IRefreshable page) page.Refresh();
        }
        if (CurrentPage == Home) Home.Shown();
        else if (CurrentPage is IRefreshable page && tabs.ContainsValue(CurrentPage) && refreshTabs.Remove(CurrentPage)) page.Refresh();
    }

    public Task SessionExpiredAsync() => signOut(SignOutKind.Expired);

    /// <summary>Signing out: the song or book stops, its place saved, and the theme with it.</summary>
    public void Dispose()
    {
        Theme.Dispose();
        Audio.Dispose();
    }
}
