using System;
using System.ComponentModel;
using System.Linq;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.Audio;

/// <summary>
/// The song or book playing, whichever page is showing; only one ever is.
/// Feeds the bar at the foot of the sidebar (the Apple app's audio
/// accessory): play/pause there, the rest opens the full player, ✕ stops it.
/// Tells the system's media controls too (see <see cref="SystemMediaControls"/>),
/// and takes their buttons.
/// </summary>
public sealed partial class AudioSession : ObservableObject, IDisposable
{
    private readonly IMediaServer client;

    public AudioSession(IMediaServer client)
    {
        this.client = client;
        // Starting one stops the other.
        Music.Starting += () => Books.Stop();
        Books.Starting += () => Music.Stop();
        Music.PropertyChanged += Changed;
        Books.PropertyChanged += Changed;
        Sleep = new SleepTimer(this);
    }

    /// <summary>Pauses the song or book after a while, or at the end of the song or chapter.</summary>
    public SleepTimer Sleep { get; }

    // ---- The system's media controls ----

    private ISystemMediaControls? systemControls;
    private bool triedSystemControls;
    private NowPlayingInfo? shown;
    private DateTimeOffset shownAt;

    /// <summary>The cover fetched for the song or book shown, by its id.</summary>
    private (string Id, byte[] Bytes)? artwork;

    /// <summary>
    /// Tells the system what's playing: at once when the song, book, chapter
    /// or state changes; as it plays, every few seconds, which is all the
    /// system needs to keep its timeline moving.
    /// </summary>
    private void UpdateSystemControls()
    {
        var info = NowPlaying();
        if (info is not null && !triedSystemControls)
        {
            // Made as the first song or book plays, when the window is there to tie them to.
            triedSystemControls = true;
            var window = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            systemControls = SystemMediaControls.Create(window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
            if (systemControls is not null)
            {
                systemControls.Command += command => Avalonia.Threading.Dispatcher.UIThread.Post(() => Run(command));
                systemControls.SeekRequested += seconds => Avalonia.Threading.Dispatcher.UIThread.Post(() => SeekTo(seconds));
            }
        }
        if (systemControls is null) return;
        var now = DateTimeOffset.UtcNow;
        if (info == shown) return;
        if (info is not null && shown is not null && info == shown with { Position = info.Position } && now - shownAt < TimeSpan.FromSeconds(5))
        {
            // Only the position moved: as playing moves it, it waits; a seek goes at once.
            var expected = shown.Position + (shown.IsPlaying ? (now - shownAt).TotalSeconds * shown.Rate : 0);
            if (Math.Abs(info.Position - expected) < 2) return;
        }
        var newItem = info?.Id != shown?.Id;
        shown = info;
        shownAt = now;
        systemControls.Show(info, artwork is { } held && held.Id == info?.Id ? held.Bytes : null);
        if (newItem && info?.Artwork is { } url) _ = FetchArtworkAsync(info.Id, url);
    }

    private async System.Threading.Tasks.Task FetchArtworkAsync(string id, Uri url)
    {
        byte[] bytes;
        try { bytes = await client.DataAsync(url); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException or System.IO.IOException) { return; }
        if (shown is not { } current || current.Id != id) return;
        artwork = (id, bytes);
        systemControls?.Show(current, bytes);
    }

    /// <summary>The song or book loaded, as the system shows it; null with neither.</summary>
    private NowPlayingInfo? NowPlaying()
    {
        if (Books.Book is { } book)
        {
            var chapter = Books.CurrentChapter;
            return new NowPlayingInfo(
                book.Id, book.Title, book.Author, chapter?.Title ?? book.Series, client.PosterUrl(book.Cover, width: 600),
                Books.BookDuration, Books.BookElapsed, Books.IsPlaying, IsBook: true, Books.Rate,
                CanGoNext: chapter is not null && Books.Chapters.Any(c => c.Id == chapter.Id + 1), CanGoPrevious: true);
        }
        if (Music.Current is { } track)
        {
            return new NowPlayingInfo(
                track.Id, track.DisplayTitle, track.ArtistText, track.Album, client.PosterUrl(track, width: 600),
                Music.Duration, Music.CurrentTime, Music.IsPlaying, IsBook: false, Rate: 1,
                CanGoNext: Music.HasNext, CanGoPrevious: true);
        }
        return null;
    }

    /// <summary>A system button, as the window's own media keys do it: next and previous are a book's chapters.</summary>
    private void Run(MediaCommand command)
    {
        var book = Books.Book is not null;
        switch (command)
        {
            case MediaCommand.Play when book: Books.Resume(); break;
            case MediaCommand.Play: Music.Resume(); break;
            case MediaCommand.Pause: Pause(); break;
            case MediaCommand.TogglePlayPause: Toggle(); break;
            case MediaCommand.Stop: Stop(); break;
            case MediaCommand.Next when book: Books.NextChapter(); break;
            case MediaCommand.Next: Music.Next(); break;
            case MediaCommand.Previous when book: Books.PreviousChapter(); break;
            case MediaCommand.Previous: Music.Previous(); break;
            case MediaCommand.SkipForward when book: Books.Skip(AudiobookPlayer.SkipForwardSeconds); break;
            case MediaCommand.SkipBack when book: Books.Skip(-AudiobookPlayer.SkipBackSeconds); break;
        }
    }

    /// <summary>A point on the system's timeline: in the song, or in the whole book.</summary>
    private void SeekTo(double seconds)
    {
        if (Books.Book is not null) Books.SeekInBook(seconds);
        else if (Music.Current is not null) Music.Seek(seconds);
    }

    public MusicPlayer Music { get; } = new();
    public AudiobookPlayer Books { get; } = new();

    /// <summary>Whether a song or a book is loaded (playing or paused).</summary>
    public bool IsLoaded => Music.Current is not null || Books.Book is not null;

    public bool IsPlaying => Music.IsPlaying || Books.IsPlaying;

    /// <summary>What the bar shows: the book and its chapter, or the song and its artist.</summary>
    public string? Title => Books.Book?.Title ?? Music.Current?.DisplayTitle;

    public string? Detail => Books.Book is { } book
        ? Books.CurrentChapter?.Title ?? book.Author
        : Music.Current is { } track ? track.ArtistText ?? track.Album : null;

    public Uri? Artwork => Books.Book is { } book
        ? client.PosterUrl(book.Cover, width: 200)
        : Music.Current is { } track ? client.PosterUrl(track, width: 200) : null;

    /// <summary>Whether the full player is up, over the window.</summary>
    [ObservableProperty] public partial bool IsNowPlayingShown { get; set; }

    /// <summary>Which full player: the book's, when a book is loaded.</summary>
    public bool ShowsBookPlayer => Books.Book is not null;

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Artwork));
        OnPropertyChanged(nameof(ShowsBookPlayer));
        if (!IsLoaded) IsNowPlayingShown = false;
        UpdateSystemControls();
    }

    /// <summary>Pauses or resumes whichever is loaded.</summary>
    [RelayCommand]
    public void Toggle()
    {
        if (Books.Book is not null) Books.TogglePlayPause();
        else if (Music.Current is not null) Music.TogglePlayPause();
    }

    /// <summary>Ends the song or book: the player closes and the bar goes away. A book's place is saved first.</summary>
    [RelayCommand]
    public void Stop()
    {
        Books.Stop();
        Music.Stop();
    }

    /// <summary>Pauses the song or book as a video starts, so the two never play over each other. It stays loaded, to carry on from the bar.</summary>
    public void Pause()
    {
        if (Books.IsPlaying) Books.Pause();
        if (Music.IsPlaying) Music.Pause();
    }

    [RelayCommand] private void ShowPlayer() => IsNowPlayingShown = IsLoaded;
    [RelayCommand] private void HidePlayer() => IsNowPlayingShown = false;

    public void Dispose()
    {
        systemControls?.Dispose();
        Music.Dispose();
        Books.Dispose();
    }
}
