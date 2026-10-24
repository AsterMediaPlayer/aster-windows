using System;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Desktop.Images;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>What the window shows: sign-in, the app (sidebar and pages), or the player over it all.</summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    private ShellViewModel? shell;

    /// <summary>The disc menu open, if one is, even while a title it played is showing.</summary>
    private DiscMenuViewModel? openMenu;

    /// <summary>Signed in from the ASTER_* variables: signing out leaves the saved session alone.</summary>
    private bool developmentSession;

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
        if ((development ?? StoredSession.Load()) is { } session)
        {
            var client = new JellyfinClient(session.ServerUrl, session.AccessToken, session.UserId, kind: session.Kind);
            ShowShell(client, session);
            if (tab is { } startTab) shell!.ShowTab(startTab);
            if (sort is { } startSort && shell!.CurrentPage is BrowsePageViewModel page) page.SortIndex = (int)startSort;
            if (section is not null && shell!.CurrentPage is LiveTVViewModel live) live.ShowSection(section);
            if (openItemId is not null) _ = OpenByIdAsync(client, openItemId);
            if (playItemId is not null) _ = PlayByIdAsync(client, playItemId, maxBitrate, startSeconds);
        }
        else
        {
            ShowSignIn();
        }
    }

    /// <summary>
    /// In a debug build, ASTER_SERVER, ASTER_TOKEN and ASTER_USER_ID sign in
    /// without touching the saved session, for trying the app against a
    /// server with an API key; ASTER_SERVER_KIND=emby for an Emby server.
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
        CurrentPage = new SignInViewModel(ShowShell) { Message = message, ServerAddress = serverAddress ?? "" };
    }

    private void ShowShell(JellyfinClient client, StoredSession session)
    {
        ImageLoader.Client = client;
        shell = new ShellViewModel(client, session, (entry, closed) => Play(client, entry, closed),
            (document, itemId, closed) => ShowDiscMenu(client, document, itemId, closed), kind => SignOutAsync(client, kind));
        CurrentPage = shell;
    }

    /// <summary>Plays over everything; closing comes back to the page it was played from.</summary>
    private void Play(JellyfinClient client, PlaybackQueueItem entry, Action<BaseItem?>? closed)
    {
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
    private async Task OpenByIdAsync(JellyfinClient client, string itemId)
    {
        try { shell?.Open(await client.ItemAsync(itemId)); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>
    /// Plays a title from where it was left (or <paramref name="startSeconds"/>),
    /// for <c>--play</c>: a song or album in the music player, a book in the
    /// book player, anything else in the video player.
    /// </summary>
    private async Task PlayByIdAsync(JellyfinClient client, string itemId, int? maxBitrate, double? startSeconds)
    {
        try
        {
            var item = await client.ItemAsync(itemId);
            if (shell is { } open && item.Kind is ItemKind.Audio or ItemKind.MusicAlbum)
            {
                var tracks = item.Kind == ItemKind.Audio ? [item] : await client.AlbumTracksAsync(item.Id);
                open.Audio.Music.Play(tracks, 0, shuffled: false, client);
                return;
            }
            if (shell is { } books && item.Kind == ItemKind.AudioBook)
            {
                books.Audio.Books.Play(Aster.Core.Audio.Audiobook.FromItem(item), null, client);
                return;
            }
            var start = startSeconds is { } seconds ? (long)(seconds * 10_000_000) : item.UserData?.PlaybackPositionTicks ?? 0;
            shell?.Play(new PlaybackQueueItem { ItemId = item.Id, StartTicks = start, MaxBitrate = maxBitrate });
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    private async Task SignOutAsync(JellyfinClient client, SignOutKind kind)
    {
        var expired = kind == SignOutKind.Expired;
        var address = kind == SignOutKind.ChangeServer ? null : client.BaseUrl.ToString().TrimEnd('/');
        if (developmentSession)
        {
            // Neither ends the key's access nor forgets the real sign-in.
            developmentSession = false;
            ShowSignIn("Signed out of the development session.", address);
            return;
        }
        if (!expired) await client.LogoutAsync();
        StoredSession.Clear();
        ShowSignIn(expired ? "Your session has ended. Please sign in again." : null, address);
    }

    /// <summary>The app is closing: a title playing reports where it stopped.</summary>
    public void Shutdown()
    {
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
