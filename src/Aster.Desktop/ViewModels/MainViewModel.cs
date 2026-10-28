using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;
using Aster.Desktop.Images;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>What the window shows: sign-in, the app (sidebar and pages), or the player over it all.</summary>
public partial class MainViewModel : ViewModelBase, IServerAccounts
{
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    private ShellViewModel? shell;

    /// <summary>Every server signed in to, to switch between (the development session isn't one).</summary>
    public IReadOnlyList<SavedServer> Servers { get; private set; } = [];

    public string? CurrentId { get; private set; }

    /// <summary>The disc menu open, if one is, even while a title it played is showing.</summary>
    private DiscMenuViewModel? openMenu;

    /// <summary>Signed in from the ASTER_* variables: signing out leaves the saved session alone.</summary>
    private bool developmentSession;

    /// <summary>
    /// The server locked after Lock After, until its password is given (or
    /// it's signed in to afresh): switching back to it asks for it too.
    /// </summary>
    private static string? LockedId
    {
        get => AwayClock.LockedServer;
        set => AwayClock.LockedServer = value;
    }

    /// <param name="playItemId">A title to play straight away (<c>--play &lt;id&gt;</c>), for development.</param>
    /// <param name="maxBitrate">A quality cap for that title (<c>--max-bitrate</c>), to try converted streams.</param>
    /// <param name="startSeconds">Where to start that title (<c>--start</c>), instead of its resume point.</param>
    /// <param name="openItemId">A title's page to open straight away (<c>--open &lt;id&gt;</c>), for development.</param>
    /// <param name="tab">A sidebar tab to start on (<c>--tab movies</c>), for development.</param>
    /// <param name="sort">That tab's sort (<c>--sort DateAdded</c>), for development.</param>
    /// <param name="section">That tab's section (<c>--section guide</c> on Live TV), for development.</param>
    public MainViewModel(string? playItemId = null, int? maxBitrate = null, double? startSeconds = null, string? openItemId = null, AppTab? tab = null,
        Aster.Core.Browsing.LibrarySort? sort = null, string? section = null)
    {
        var development = DevelopmentSession();
        developmentSession = development is not null;
        var stored = StoredSession.Load();
        Servers = SavedServers.LoadOrStart(stored);
        if ((development ?? stored) is { } session)
        {
            var client = MediaServers.For(session);
            ShowShell(client, session);
            if (tab is { } startTab) shell!.ShowTab(startTab);
            if (sort is { } startSort && shell!.CurrentPage is BrowsePageViewModel page) page.SortIndex = (int)startSort;
            if (section is not null && shell!.CurrentPage is LiveTVViewModel live) live.ShowSection(section);
            if (openItemId is not null) _ = OpenByIdAsync(client, openItemId);
            if (playItemId is not null) _ = PlayByIdAsync(client, playItemId, maxBitrate, startSeconds);
            // Closed while away, or while locked: it's still locked.
            CheckLock();
        }
        else
        {
            ShowSignIn();
        }
    }

    /// <summary>
    /// In a debug build, ASTER_SERVER, ASTER_TOKEN and ASTER_USER_ID sign in
    /// without touching the saved session, for trying the app against a
    /// server with an API key; ASTER_SERVER_KIND=emby or plex for an Emby or
    /// Plex server (with a Plex token, and any user id).
    /// </summary>
    private static StoredSession? DevelopmentSession()
    {
#if DEBUG
        var server = Environment.GetEnvironmentVariable("ASTER_SERVER");
        var token = Environment.GetEnvironmentVariable("ASTER_TOKEN");
        var user = Environment.GetEnvironmentVariable("ASTER_USER_ID");
        if (server is not null && token is not null && user is not null)
        {
            var kind = Enum.TryParse<ServerKind>(Environment.GetEnvironmentVariable("ASTER_SERVER_KIND"), ignoreCase: true, out var named) ? named : ServerKind.Jellyfin;
            return new StoredSession { ServerUrl = new Uri(server), UserId = user, UserName = "Development", AccessToken = token, ServerKind = kind };
        }
#endif
        return null;
    }

    /// <param name="serverAddress">Filled in, to sign in to the same server again.</param>
    private void ShowSignIn(string? message = null, string? serverAddress = null)
    {
        shell?.Dispose();
        shell = null;
        ImageLoader.Client = null;
        CurrentId = null;
        CurrentPage = new SignInViewModel(SignedIn, Servers, SwitchTo, ShowPlexSignIn) { Message = message, ServerAddress = serverAddress ?? "" };
    }

    /// <summary>Plex's sign-in, through plex.tv; Cancel comes back to the sign-in screen.</summary>
    public void ShowPlexSignIn() =>
        CurrentPage = new PlexSignInViewModel(session =>
        {
            session.Save();
            SignedIn(MediaServers.For(session), session);
        }, () => ShowSignIn());

    /// <summary>A sign-in that worked: kept with the others, and used.</summary>
    private void SignedIn(IMediaServer client, StoredSession session)
    {
        // Signing in afresh is as good as the password.
        if (LockedId == SavedServer.IdOf(session)) Unlocked();
        Servers = SavedServers.Remember(Servers, session);
        SavedServers.Save(Servers);
        ShowShell(client, session);
    }

    private void ShowShell(IMediaServer client, StoredSession session)
    {
        // Another server's: what the last one was showing and playing goes.
        shell?.Dispose();
        ImageLoader.Client = client;
        CurrentId = developmentSession ? null : SavedServer.IdOf(session);
        shell = new ShellViewModel(client, session, (entry, closed) => Play(client, entry, closed),
            // Disc menus come from Aster's Jellyfin plugin.
            (document, itemId, closed) => ShowDiscMenu((JellyfinClient)client, document, itemId, closed), kind => SignOutAsync(client, kind), this);
        CurrentPage = shell;
    }

    /// <summary>Uses a saved server, asking for its password first when it's set to, or locked.</summary>
    public void SwitchTo(SavedServer server)
    {
        if (server.Id == CurrentId) return;
        if (!server.AsksPasswordToSwitch && server.Id != LockedId)
        {
            Use(server.Session);
            return;
        }
        var back = CurrentPage;
        CurrentPage = new ServerPasswordViewModel(server, locking: false, fresh =>
        {
            if (server.Id == LockedId) Unlocked();
            Use(fresh);
        }, () => CurrentPage = back);
    }

    public void Change(string id, Func<SavedServer, SavedServer> change)
    {
        Servers = SavedServers.Change(Servers, id, change);
        SavedServers.Save(Servers);
    }

    /// <summary>A saved sign-in, or a fresh one from its password: the app starts afresh on it.</summary>
    private void Use(StoredSession session)
    {
        session.Save();
        developmentSession = false;
        Servers = SavedServers.Remember(Servers, session);
        SavedServers.Save(Servers);
        ShowShell(MediaServers.For(session), session);
    }

    public void Remove(string id)
    {
        if (id == CurrentId || Servers.FirstOrDefault(s => s.Id == id) is not { } server) return;
        Servers = SavedServers.Forget(Servers, id);
        SavedServers.Save(Servers);
        var session = server.Session;
        _ = MediaServers.For(session).LogoutAsync()
            .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    // ---- Lock After ----

    private SavedServer? CurrentServer => Servers.FirstOrDefault(s => s.Id == CurrentId);

    /// <summary>
    /// The window went to the background (or is closing): the clock for the
    /// server's Lock After starts. Not while a video or disc menu is up, as
    /// one plays on while another window has the keyboard; closing always.
    /// </summary>
    public void NoteLeft(bool closing = false)
    {
        if ((closing ? shell is not null : CurrentPage is ShellViewModel) && CurrentServer?.LockAfterMinutes is not null) AwayClock.NoteLeft();
    }

    /// <summary>The window is back (or Aster just opened): locks the server in use if it was away past its Lock After.</summary>
    public void CheckLock()
    {
        if (shell is null || CurrentServer is not { } server) return;
        if (server.Id == LockedId || server.LocksAfter(AwayClock.LeftAt, DateTimeOffset.UtcNow))
        {
            Lock(server);
            return;
        }
        AwayClock.Clear();
    }

    /// <summary>Asks for the password to carry on: what was showing and playing goes.</summary>
    private void Lock(SavedServer server)
    {
        LockedId = server.Id;
        AwayClock.Clear();
        shell?.Dispose();
        shell = null;
        ImageLoader.Client = null;
        CurrentPage = new ServerPasswordViewModel(server, locking: true, fresh =>
        {
            Unlocked();
            Use(fresh);
        }, () => ShowSignIn());
    }

    private void Unlocked()
    {
        LockedId = null;
        AwayClock.Clear();
    }

    /// <summary>Plays over everything; closing comes back to the page it was played from.</summary>
    private void Play(IMediaServer server, PlaybackQueueItem entry, Action<BaseItem?>? closed)
    {
        // The player is Jellyfin's (and Emby's) until it plays from Plex too.
        if (server is not JellyfinClient client)
        {
            System.Diagnostics.Debug.WriteLine($"Playback: {server.Kind} playback comes later");
            return;
        }
        CurrentPage = new PlayerViewModel(client, entry, last =>
        {
            CurrentPage = shell;
            closed?.Invoke(last);
        });
    }

    /// <summary>
    /// A disc menu over everything. What it plays plays over it, and closing
    /// the player comes back to the menu; closing the menu comes back to the
    /// page it was opened from.
    /// </summary>
    private void ShowDiscMenu(JellyfinClient client, Aster.Core.DiscMenu.DiscMenuDocument document, string itemId, Action closed)
    {
        DiscMenuViewModel? menu = null;
        openMenu = menu = new DiscMenuViewModel(client, document, itemId,
            play: entry => CurrentPage = new PlayerViewModel(client, entry, played =>
            {
                CurrentPage = menu;
                _ = menu!.ResumeAfterPlayback();
            }),
            close: () =>
            {
                CurrentPage = shell;
                openMenu = null;
                menu!.Dispose();
                closed();
            });
        CurrentPage = menu;
    }

    /// <summary>Opens a page for <c>--open</c>: looked up first, so a library or person opens as one would from a card.</summary>
    private async Task OpenByIdAsync(IMediaServer client, string itemId)
    {
        try { shell?.Open(await client.ItemAsync(itemId)); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>
    /// Plays a title from where it was left (or <paramref name="startSeconds"/>),
    /// for <c>--play</c>: a song or album in the music player, a book in the
    /// book player, anything else in the video player.
    /// </summary>
    private async Task PlayByIdAsync(IMediaServer client, string itemId, int? maxBitrate, double? startSeconds)
    {
        try
        {
            var item = await client.ItemAsync(itemId);
            if (shell is { } open && client is JellyfinClient music && item.Kind is ItemKind.Audio or ItemKind.MusicAlbum)
            {
                var tracks = item.Kind == ItemKind.Audio ? [item] : await music.AlbumTracksAsync(item.Id);
                open.Audio.Music.Play(tracks, 0, shuffled: false, music);
                return;
            }
            if (shell is { } books && client is JellyfinClient library && item.Kind == ItemKind.AudioBook)
            {
                books.Audio.Books.Play(Aster.Core.Audio.Audiobook.FromItem(item), null, library);
                return;
            }
            var start = startSeconds is { } seconds ? (long)(seconds * 10_000_000) : item.UserData?.PlaybackPositionTicks ?? 0;
            shell?.Play(new PlaybackQueueItem { ItemId = item.Id, StartTicks = start, MaxBitrate = maxBitrate });
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>
    /// Off to sign-in. Adding a server keeps the one in use saved and signed
    /// in, to switch back to; signing out (or a session the server ended)
    /// forgets it, and the others stay saved.
    /// </summary>
    private async Task SignOutAsync(IMediaServer client, SignOutKind kind)
    {
        var address = kind == SignOutKind.AddServer ? null : client.BaseUrl.ToString().TrimEnd('/');
        if (developmentSession)
        {
            // Neither ends the key's access nor forgets the real sign-in.
            developmentSession = false;
            ShowSignIn("Signed out of the development session.", address);
            return;
        }
        if (kind != SignOutKind.AddServer && CurrentId is { } current)
        {
            Servers = SavedServers.Forget(Servers, current);
            SavedServers.Save(Servers);
        }
        if (kind == SignOutKind.SignOut)
        {
            try { await client.LogoutAsync(); }
            catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
        }
        StoredSession.Clear();
        ShowSignIn(kind == SignOutKind.Expired ? "Your session has ended. Please sign in again." : null, address);
    }

    /// <summary>The app is closing: a title playing reports where it stopped.</summary>
    public void Shutdown()
    {
        NoteLeft(closing: true);
        if (CurrentPage is PlayerViewModel player)
        {
            player.SaveProgressNow();
            player.Stop();
        }
        // A disc menu, whether showing or waiting behind a title it's playing.
        openMenu?.Dispose();
        shell?.Dispose();
    }
}
