// An open disc menu, over the whole window: the navigation (in
// Aster.Core.DiscMenu), its music and sounds, the trailer behind it, and the
// hand-off to the player and back. A port of the Apple app's
// DiscMenuController and DiscMenuView, less the drawing (DiscMenuView here).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.DiscMenu;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Desktop.Audio;
using Aster.Desktop.Images;
using Aster.Mpv;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

public partial class DiscMenuViewModel : ViewModelBase, IDisposable
{
    private readonly JellyfinClient client;
    private readonly Action<PlaybackQueueItem> play;
    private readonly Action close;
    private readonly DiscMenuAudio audio;
    private bool suspended;
    private bool closed;

    /// <summary>Which trailer is loaded, and whether muted, so a screen with the same one carries on with it.</summary>
    private (string Id, bool Muted)? trailerKey;

    /// <param name="play">Plays a title over the menu, which comes back (<see cref="ResumeAfterPlayback"/>) as the player closes.</param>
    /// <param name="close">The menu's done: back to the title's page.</param>
    public DiscMenuViewModel(JellyfinClient client, DiscMenuDocument document, string itemId, Action<PlaybackQueueItem> play, Action close)
    {
        this.client = client;
        this.play = play;
        this.close = close;
        Resolver = new DiscPictureResolver(client, itemId);
        audio = new DiscMenuAudio(client, AppSettings.Current.DiscMenuSounds);
        Navigator = new DiscMenuNavigator(document, itemId, (index, stamp) => client.ChapterImageUrl(itemId, index, stamp));
        Navigator.ScreenChanged += () =>
        {
            // Music, button sounds and the trailer follow the screen.
            audio.Apply(Navigator.Screen.Audio, Navigator.Document);
            SyncTrailer();
            OnPropertyChanged(nameof(CanGoBack));
            ScreenChanged?.Invoke();
        };
        Navigator.FocusChanged += () => FocusChanged?.Invoke();
        Navigator.Sound += audio.Play;
        Navigator.Alert += message => AlertMessage = message;
        Navigator.CloseRequested += Close;
        Navigator.PlaybackRequested += Play;
        Navigator.Open();
        PreloadPictures();
    }

    public DiscMenuNavigator Navigator { get; }
    public DiscPictureResolver Resolver { get; }

    /// <summary>The screen or page changed: the view draws it afresh.</summary>
    public event Action? ScreenChanged;

    public event Action? FocusChanged;

    /// <summary>The Back button's look: a step back, or closing the menu.</summary>
    public bool CanGoBack => Navigator.CanGoBack;

    /// <summary>Something a button couldn't do, said over the menu until dismissed.</summary>
    [ObservableProperty] public partial string? AlertMessage { get; set; }

    [RelayCommand] private void DismissAlert() => AlertMessage = null;

    [RelayCommand] private void Back() => Navigator.HardwareBack();

    // ---- Playing from the menu ----

    private async void Play(DiscPlayback request)
    {
        var ids = request.ItemIds;
        var start = request.StartTicks;
        // A season's or show's menu (a one-season show's is often its
        // season's): its feature is its episodes, one after another.
        if (ids is [var only] && only == Navigator.ItemId && await EpisodesOfAsync(only) is { } episodes)
        {
            if (episodes.Count == 0)
            {
                if (!closed) AlertMessage = "This menu's season or show has no episodes in this library to play.";
                return;
            }
            ids = episodes;
            start = 0;
        }
        if (closed) return;
        suspended = true;
        audio.Suspend();
        DropTrailer();
        play(new PlaybackQueueItem
        {
            ItemId = ids[0],
            StartTicks = start,
            Following = ids.Skip(1).ToList(),
        });
    }

    /// <summary>A season's episodes, or a show's (less its specials), in order; null for anything else.</summary>
    private async Task<IReadOnlyList<string>?> EpisodesOfAsync(string itemId)
    {
        try
        {
            var item = await client.ItemAsync(itemId);
            return item.Kind switch
            {
                ItemKind.Season when item.SeriesId is { } seriesId => (await client.EpisodesAsync(seriesId, item.Id)).Select(e => e.Id).ToList(),
                ItemKind.Series => (await client.EpisodesAsync(item.Id, null)).Where(e => e.ParentIndexNumber is not 0).Select(e => e.Id).ToList(),
                _ => null,
            };
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The player closed: the menu fetches its document again and shows the same screen.</summary>
    public async Task ResumeAfterPlayback()
    {
        DiscMenuDocument? fresh = null;
        try { fresh = await client.DiscMenuAsync(Navigator.ItemId); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
        if (closed) return;
        suspended = false;
        audio.Resume();
        Navigator.Reload(fresh);
    }

    private void Close()
    {
        if (closed) return;
        closed = true;
        close();
    }

    // ---- The trailer behind the menu ----

    /// <summary>A looping local trailer behind the screen, cover-fitted; null without one.</summary>
    [ObservableProperty] public partial MpvPlayer? Trailer { get; private set; }

    /// <summary>The trailer's frames are coming, so its poster can make way.</summary>
    [ObservableProperty] public partial bool IsTrailerShowing { get; private set; }

    private void SyncTrailer()
    {
        var background = Navigator.Screen.Background;
        var trailers = Navigator.Document.Trailers ?? [];
        var index = Math.Max(0, background?.TrailerIndex ?? 0);
        if (suspended || background?.Source != "trailer" || index >= trailers.Count || trailers[index] is not { Kind: "local", ItemId: { } trailerId })
        {
            DropTrailer();
            return;
        }
        var muted = background.Muted ?? true;
        if (Trailer is not null && trailerKey == (trailerId, muted)) return;
        DropTrailer();
        MpvPlayer player;
        try
        {
            player = MpvPlayer.Create(ThemePlayer.LoopingOptions(new()
            {
                // Frames go to the menu's MpvVideoView through the render API.
                ["vo"] = "libmpv",
                ["hwdec"] = MpvHardware.Decoding,
                ["aid"] = muted ? "no" : "auto",
                ["sid"] = "no",
                ["sub-auto"] = "no",
                ["osc"] = "no",
                ["osd-level"] = "0",
                ["input-vo-keyboard"] = "no",
                // Cover the screen, cropping what doesn't fit.
                ["panscan"] = "1.0",
            }));
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            return;
        }
        player.PlaybackRestart += () => Dispatcher.UIThread.Post(() =>
        {
            if (Trailer == player) IsTrailerShowing = true;
        });
        player.EndFile += end =>
        {
            if (end.Reason == MpvEndFileReason.Error) Dispatcher.UIThread.Post(() => { if (Trailer == player) DropTrailer(); });
        };
        trailerKey = (trailerId, muted);
        Trailer = player;
        _ = LoadTrailerAsync(player, client.VideoStreamUrl(trailerId));
    }

    /// <summary>mpv's video output fails for good on a file opened before the view gives it somewhere to draw.</summary>
    private static async Task LoadTrailerAsync(MpvPlayer player, Uri url)
    {
        try
        {
            await player.RenderContextReady.ConfigureAwait(false);
            await player.CommandAsync("loadfile", url.AbsoluteUri).ConfigureAwait(false);
        }
        catch (Exception error) when (error is MpvException or ObjectDisposedException) { }
    }

    /// <summary>Lets go of the trailer; the view drops its render context as it goes, which mpv waits for before it ends.</summary>
    private void DropTrailer()
    {
        IsTrailerShowing = false;
        trailerKey = null;
        var player = Trailer;
        Trailer = null;
        player?.Dispose();
    }

    // ---- Pictures ----

    /// <summary>Every picture of every screen, fetched as the menu opens.</summary>
    private void PreloadPictures()
    {
        var document = Navigator.Document;
        var urls = new List<Uri?>();
        void AddBackground(DiscBackground? background)
        {
            urls.Add(Resolver.Background(background));
            urls.Add(Resolver.Picture(background?.Poster));
        }
        AddBackground(document.Background);
        urls.AddRange((document.Layout?.Layers ?? []).Select(l => Resolver.Picture(l.Image)));
        foreach (var menu in document.Menus.Values)
        {
            AddBackground(menu.Background);
            urls.AddRange((menu.Layout?.Layers ?? []).Select(l => Resolver.Picture(l.Image)));
            foreach (var entry in menu.Entries)
            {
                urls.Add(Resolver.Picture(entry.Image));
                urls.Add(Resolver.Picture(entry.ImageFocus));
            }
        }
        ImageLoader.Prefetch(urls);
    }

    public void Dispose()
    {
        closed = true;
        audio.Dispose();
        DropTrailer();
    }
}
