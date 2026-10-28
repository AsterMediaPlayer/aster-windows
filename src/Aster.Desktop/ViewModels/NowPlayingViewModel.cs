// The full-screen players over the window: the song's (artwork, progress,
// transport, lyrics or Up Next) and the book's (cover, chapter progress,
// skips, speed and chapters). Ports of the Apple app's NowPlayingView and
// AudiobookNowPlayingView.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Aster.Core.Audio;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using Aster.Desktop.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A line of lyrics; the one being sung stands out.</summary>
public sealed record LyricRow(string Text, bool IsCurrent, bool IsSynced)
{
    public double Opacity => IsCurrent || !IsSynced ? 1 : 0.4;
}

/// <summary>A speed in the book player's menu.</summary>
public sealed record SpeedChoice(string Title, bool IsSelected, IRelayCommand Select);

public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly INavigator navigator;
    private bool updatingSeek;

    public NowPlayingViewModel(INavigator navigator)
    {
        this.navigator = navigator;
        Music.PropertyChanged += MusicChanged;
        Books.PropertyChanged += BooksChanged;
        Audio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioSession.IsNowPlayingShown)) OnPropertyChanged(nameof(IsShown));
            if (e.PropertyName == nameof(AudioSession.ShowsBookPlayer)) OnPropertyChanged(nameof(ShowsBook));
        };
    }

    public AudioSession Audio => navigator.Audio;
    public MusicPlayer Music => Audio.Music;
    public AudiobookPlayer Books => Audio.Books;

    public bool IsShown => Audio.IsNowPlayingShown;
    public bool ShowsBook => Audio.ShowsBookPlayer;

    // ---- The song ----

    public BaseItem? Track => Music.Current;
    public Uri? TrackArtwork => Track is { } track ? navigator.Client.PosterUrl(track, width: 800) : null;
    public string? TrackTitle => Track?.DisplayTitle;
    public string? TrackArtist => Track?.ArtistText;
    public string? TrackAlbum => Track?.Album;
    public string Elapsed => MusicFormat.Clock(Music.CurrentTime);
    public string Remaining => "-" + MusicFormat.Clock(Math.Max(Music.Duration - Music.CurrentTime, 0));
    public string RepeatGlyph => Music.RepeatMode == RepeatMode.One ? "" : "";
    public bool RepeatOn => Music.RepeatMode != RepeatMode.Off;
    public bool HasLyrics => Music.Lyrics is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLyricsPanel), nameof(ShowsUpNext))]
    public partial bool ShowLyrics { get; set; }

    public bool ShowsLyricsPanel => ShowLyrics && HasLyrics;
    public bool ShowsUpNext => !ShowsLyricsPanel && UpNext.Count > 0;

    [ObservableProperty] public partial IReadOnlyList<LyricRow> Lyrics { get; private set; } = [];
    [ObservableProperty] public partial int LyricIndex { get; private set; } = -1;
    [ObservableProperty] public partial IReadOnlyList<ItemCard> UpNext { get; private set; } = [];

    /// <summary>The seek bar: where the song is, or where it's dragged to.</summary>
    [ObservableProperty] public partial double SongSeek { get; set; }

    partial void OnSongSeekChanged(double value)
    {
        if (!updatingSeek && Math.Abs(value - Music.CurrentTime) > 1.5) Music.Seek(value);
    }

    private void MusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MusicPlayer.CurrentTime):
                updatingSeek = true;
                SongSeek = Music.CurrentTime;
                updatingSeek = false;
                OnPropertyChanged(nameof(Elapsed));
                OnPropertyChanged(nameof(Remaining));
                if (Music.CurrentLyricIndex is { } line && line != LyricIndex) BuildLyrics();
                break;
            case nameof(MusicPlayer.Duration):
                OnPropertyChanged(nameof(Remaining));
                break;
            case nameof(MusicPlayer.Current) or nameof(MusicPlayer.Index) or nameof(MusicPlayer.Queue):
                OnPropertyChanged(nameof(Track));
                OnPropertyChanged(nameof(TrackArtwork));
                OnPropertyChanged(nameof(TrackTitle));
                OnPropertyChanged(nameof(TrackArtist));
                OnPropertyChanged(nameof(TrackAlbum));
                UpNext = Music.UpNext.Select(song => ItemCard.Square(navigator, song, () => Music.SkipTo(song))).ToList();
                OnPropertyChanged(nameof(ShowsUpNext));
                break;
            case nameof(MusicPlayer.RepeatMode):
                OnPropertyChanged(nameof(RepeatGlyph));
                OnPropertyChanged(nameof(RepeatOn));
                break;
            case nameof(MusicPlayer.Lyrics):
                OnPropertyChanged(nameof(HasLyrics));
                OnPropertyChanged(nameof(ShowsLyricsPanel));
                OnPropertyChanged(nameof(ShowsUpNext));
                BuildLyrics();
                break;
        }
    }

    private void BuildLyrics()
    {
        LyricIndex = Music.CurrentLyricIndex ?? -1;
        Lyrics = Music.Lyrics is { } lyrics
            ? lyrics.Lyrics.Select((line, i) => new LyricRow(string.IsNullOrEmpty(line.Text) ? " " : line.Text, i == LyricIndex, lyrics.IsSynced)).ToList()
            : [];
    }

    [RelayCommand] private void ToggleLyrics() => ShowLyrics = !ShowLyrics;

    // ---- The book ----

    public Audiobook? Book => Books.Book;
    public Uri? BookCover => Book is { } book ? navigator.Client.PosterUrl(book.Cover, width: 800) : null;
    public string? BookTitle => Book?.Title;
    public string? BookAuthor => Book?.Author;
    public string? ChapterTitle => Books.CurrentChapter?.Title;
    public string RateLabel => AudiobookRates.Label(Books.Rate);
    public bool RateChanged => Books.Rate != 1;
    public bool HasChapters => Books.Chapters.Count > 0;

    [ObservableProperty] public partial bool ShowChapters { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ChapterRow> Chapters { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SpeedChoice> Speeds { get; private set; } = [];

    /// <summary>The chapter's progress bar, in seconds into the chapter.</summary>
    public double ChapterLength => Math.Max(Books.ChapterBounds.End - Books.ChapterBounds.Start, 0);
    public double ChapterInto => Math.Clamp(Books.CurrentTime - Books.ChapterBounds.Start, 0, ChapterLength);
    public string ChapterElapsed => MusicFormat.Clock(ChapterInto);
    public string ChapterRemaining => "-" + MusicFormat.Clock(Math.Max(ChapterLength - ChapterInto, 0));

    /// <summary>How much of the book is left, at the speed it's playing.</summary>
    public string BookLeft => $"{MusicFormat.Length((long)(Math.Max(Books.BookDuration - Books.BookElapsed, 0) / Books.Rate * 10_000_000)) ?? "0 min"} left";

    /// <summary>The chapter's seek bar.</summary>
    [ObservableProperty] public partial double ChapterSeek { get; set; }

    partial void OnChapterSeekChanged(double value)
    {
        if (!updatingSeek && Math.Abs(value - ChapterInto) > 1.5) Books.Seek(Books.ChapterBounds.Start + value);
    }

    private int? listedChapter = -1;

    private void BooksChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudiobookPlayer.CurrentTime) or nameof(AudiobookPlayer.Duration) or nameof(AudiobookPlayer.PartIndex))
        {
            OnPropertyChanged(nameof(ChapterLength));
            OnPropertyChanged(nameof(ChapterInto));
            OnPropertyChanged(nameof(ChapterElapsed));
            OnPropertyChanged(nameof(ChapterRemaining));
            OnPropertyChanged(nameof(BookLeft));
            OnPropertyChanged(nameof(ChapterTitle));
            updatingSeek = true;
            ChapterSeek = ChapterInto;
            updatingSeek = false;
        }
        if (e.PropertyName == nameof(AudiobookPlayer.Book))
        {
            OnPropertyChanged(nameof(Book));
            OnPropertyChanged(nameof(BookCover));
            OnPropertyChanged(nameof(BookTitle));
            OnPropertyChanged(nameof(BookAuthor));
            OnPropertyChanged(nameof(HasChapters));
            listedChapter = -1;
        }
        if (e.PropertyName == nameof(AudiobookPlayer.Rate) || e.PropertyName == nameof(AudiobookPlayer.Book))
        {
            OnPropertyChanged(nameof(RateLabel));
            OnPropertyChanged(nameof(RateChanged));
            Speeds = AudiobookRates.Choices
                .Select(rate => new SpeedChoice(AudiobookRates.Label(rate), rate == Books.Rate, new RelayCommand(() => Books.SetRate(rate))))
                .ToList();
        }
        // The chapter list is made again only when the chapter playing changes.
        if (Books.CurrentChapter?.Id != listedChapter && Books.Book is { } book)
        {
            listedChapter = Books.CurrentChapter?.Id;
            Chapters = book.Chapters
                .Select(c => new ChapterRow(c, MusicFormat.Clock(book.OffsetOfPart(c.Part) + c.Start), c.Id == listedChapter,
                    new RelayCommand(() =>
                    {
                        Audio.Sleep.MovedByHand();
                        Books.JumpTo(c);
                    })))
                .ToList();
        }
    }

    [RelayCommand] private void ToggleChapters() => ShowChapters = !ShowChapters;
    [RelayCommand] private void BackFifteen() => Books.Skip(-AudiobookPlayer.SkipBackSeconds);
    [RelayCommand] private void AheadThirty() => Books.Skip(AudiobookPlayer.SkipForwardSeconds);
    // Skips by hand aren't a song's or chapter's end, for the sleep timer.
    [RelayCommand]
    private void PreviousChapter()
    {
        Audio.Sleep.MovedByHand();
        Books.PreviousChapter();
    }

    [RelayCommand]
    private void NextChapter()
    {
        Audio.Sleep.MovedByHand();
        Books.NextChapter();
    }

    // ---- Both ----

    [RelayCommand] private void Toggle() => Audio.Toggle();
    [RelayCommand]
    private void Previous()
    {
        Audio.Sleep.MovedByHand();
        Music.Previous();
    }

    [RelayCommand]
    private void Next()
    {
        Audio.Sleep.MovedByHand();
        Music.Next();
    }
    [RelayCommand] private void Shuffle() => Music.ToggleShuffle();
    [RelayCommand] private void Repeat() => Music.CycleRepeat();
    [RelayCommand] private void Close() => Audio.IsNowPlayingShown = false;
    [RelayCommand] private void Stop() => Audio.Stop();
}
