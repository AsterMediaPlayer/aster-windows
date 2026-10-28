// The Audiobooks tab: what's being listened to, what's new, and every book
// by title, author or series; an author's or series' books; and a book's
// page with its chapters. A port of the Apple app's AudiobooksView.swift.
// (Downloads are iPhone-only there, and have no part here.)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Audio;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using Aster.Desktop.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A book's cover, how far through it is, its title and author.</summary>
public sealed class AudiobookCard(INavigator navigator, Audiobook book) : ICard
{
    public Audiobook Book { get; } = book;
    public string Key => Book.Id;
    public Uri? Cover { get; } = navigator.JellyfinOnly.PosterUrl(book.Cover, width: 400);
    public string Title => Book.Title;
    public string? Subtitle => Book.SeriesIndex is { } index && Book.Series is { } series ? $"{series} {index}" : Book.Author;
    public double Progress => Book.Progress * 100;
    public bool HasProgress => Book.Progress > 0 && !Book.IsFinished;
    public bool IsFinished => Book.IsFinished;
    public IRelayCommand Open { get; } = new RelayCommand(() => navigator.OpenBook(book));
}

/// <summary>An author or series: its first books' covers as a stack, the name and how many books.</summary>
public sealed class AudiobookShelfCard(INavigator navigator, AudiobookShelf shelf) : ICard
{
    public AudiobookShelf Shelf { get; } = shelf;
    public string Key => $"{Shelf.Kind}-{Shelf.Name}";
    public Uri? Cover { get; } = navigator.JellyfinOnly.PosterUrl(shelf.Books[0].Cover, width: 400);
    public Uri? SecondCover { get; } = shelf.Books.Count > 1 ? navigator.JellyfinOnly.PosterUrl(shelf.Books[1].Cover, width: 300) : null;
    public bool HasSecond => SecondCover is not null;
    public string Title => Shelf.Name;
    public string Subtitle => $"{Shelf.Books.Count} book{(Shelf.Books.Count == 1 ? "" : "s")}";
    public IRelayCommand Open { get; } = new RelayCommand(() => navigator.OpenShelf(shelf));
}

/// <summary>
/// The Audiobooks tab, or one book library opened from Library. The book
/// playing reloads it when it changes, so Continue Listening moves along.
/// </summary>
public sealed class AudiobooksViewModel : BrowsePageViewModel
{
    private enum Section { Books, Authors, Series }

    private readonly IReadOnlyList<BaseItem>? library;
    private readonly HeroModel hero;
    private Section section = Section.Books;
    private IReadOnlyList<Audiobook> books = [];
    private IReadOnlyList<object>? cards;

    public AudiobooksViewModel(INavigator navigator, BaseItem? library = null) : base(navigator)
    {
        this.library = library is null ? null : [library];
        Title = library?.DisplayTitle ?? "Audiobooks";
        hero = new HeroModel(navigator, open: cover => { if (Find(cover) is { } book) navigator.OpenBook(book); }, play: PlayFeatured, squareArt: true);
        UpdateSections();
        _ = LoadAsync();
    }

    private string Title { get; }

    private void UpdateSections() => Sections = Enum.GetValues<Section>()
        .Select(s => new Choice(s.ToString(), s == section, new RelayCommand(() =>
        {
            if (s == section) return;
            section = s;
            UpdateSections();
            BuildCards();
            Refresh();
        })))
        .ToList();

    public override CardStyle GridStyle => section == Section.Books ? CardStyle.Book : CardStyle.BookShelf;
    protected override string GridHeading => section == Section.Books ? "All Books" : section.ToString();
    protected override IReadOnlyList<object>? LocalCards => cards;
    public override bool ShowsSort => false;

    private void BuildCards() => cards = section switch
    {
        Section.Authors => AudiobookLibrary.Shelves(books, AudiobookShelfKind.Author).Select(s => (object)new AudiobookShelfCard(Navigator, s)).ToList(),
        Section.Series => AudiobookLibrary.Shelves(books, AudiobookShelfKind.Series).Select(s => (object)new AudiobookShelfCard(Navigator, s)).ToList(),
        _ => books.Select(b => (object)new AudiobookCard(Navigator, b)).ToList(),
    };

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var libraries = library ?? (await Navigator.Libraries).Where(l => l.CollectionType == "books").ToList();
        books = await AudiobookLibrary.LoadAsync(Navigator.JellyfinOnly, libraries);
        BuildCards();
        var continuing = AudiobookLibrary.ContinueListening(books);
        var recent = AudiobookLibrary.RecentlyAdded(books);
        // The banner's books: those partway through, then the newest, as Home picks its titles.
        hero.Show(continuing.Take(4).Concat(recent.Take(5)).Select(b => b.Cover));

        var rows = new List<BrowseRow>();
        if (hero.HasPages) rows.Add(new HeroRow(hero, this));
        rows.Add(new TitleRow(Title, this));
        if (books.Count == 0) rows.Add(new StatusRow("No audiobooks yet"));
        if (section == Section.Books)
        {
            rows.AddRange(new BrowseRow?[]
            {
                Shelf("Continue Listening", continuing.Select(b => new AudiobookCard(Navigator, b)), CardStyle.Book),
                Shelf("Recently Added", recent.Select(b => new AudiobookCard(Navigator, b)), CardStyle.Book),
            }.OfType<BrowseRow>());
        }
        return rows;
    }

    private Audiobook? Find(BaseItem cover) => books.FirstOrDefault(b => b.Id == cover.Id);

    /// <summary>The banner's Play: the book plays straight away, as Play on a title does.</summary>
    private void PlayFeatured(BaseItem cover)
    {
        if (Find(cover) is { } book) Navigator.Audio.Books.Play(book, null, Navigator.JellyfinOnly);
    }
}

/// <summary>An author's books, or a series in order.</summary>
public sealed class AudiobookShelfViewModel : BrowsePageViewModel
{
    private readonly AudiobookShelf shelf;

    public AudiobookShelfViewModel(INavigator navigator, AudiobookShelf shelf) : base(navigator)
    {
        this.shelf = shelf;
        _ = LoadAsync();
    }

    public override CardStyle GridStyle => CardStyle.Book;
    public override bool ShowsSort => false;
    protected override IReadOnlyList<object> LocalCards => shelf.Books.Select(b => (object)new AudiobookCard(Navigator, b)).ToList();

    protected override Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var count = $"{shelf.Books.Count} book{(shelf.Books.Count == 1 ? "" : "s")}";
        return Task.FromResult<IReadOnlyList<BrowseRow>>(
            [new TitleRow(shelf.Name, this, shelf.Kind == AudiobookShelfKind.Series ? $"Series · {count}" : count)]);
    }
}

/// <summary>One chapter in a book's list: where it starts in the book, and whether it's playing.</summary>
public sealed record ChapterRow(AudiobookChapter Chapter, string Start, bool IsCurrent, IRelayCommand Play);

/// <summary>A book's page: its cover and details, Play or Resume, and its chapters.</summary>
public sealed partial class AudiobookViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly INavigator navigator;

    public AudiobookViewModel(INavigator navigator, Audiobook book)
    {
        this.navigator = navigator;
        Book = book;
        Player.PropertyChanged += PlayerChanged;
        Refresh();
    }

    private void PlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudiobookPlayer.Book) or nameof(AudiobookPlayer.IsPlaying) or nameof(AudiobookPlayer.CurrentTime)) Update();
        // Where listening got to, once the player lets go of the book.
        if (e.PropertyName == nameof(AudiobookPlayer.Book) && Player.Book is null) Refresh();
    }

    public void Dispose() => Player.PropertyChanged -= PlayerChanged;

    private AudiobookPlayer Player => navigator.Audio.Books;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoLine), nameof(Overview), nameof(HasChapters))]
    public partial Audiobook Book { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<ChapterRow> Chapters { get; private set; } = [];
    [ObservableProperty] public partial string PlayLabel { get; private set; } = "Play";
    [ObservableProperty] public partial bool ShowsFromBeginning { get; private set; }
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty] public partial string? ProgressText { get; private set; }

    private int? currentChapter = -1;

    public Uri? Cover => navigator.JellyfinOnly.PosterUrl(Book.Cover, width: 800);
    public Uri? Backdrop => navigator.JellyfinOnly.PosterUrl(Book.Cover, width: 400);
    public string Title => Book.Title;
    public string? Author => Book.Author;
    public string? Overview => string.IsNullOrEmpty(Book.Overview) ? null : Book.Overview;
    public bool HasChapters => Book.Chapters.Count > 0;

    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (Book.SeriesIndex is { } index && Book.Series is { } series) parts.Add($"Book {index} of {series}");
            else if (Book.Series is { } only) parts.Add(only);
            if (Book.Year is { } year) parts.Add(year.ToString());
            if (Book.LengthText is { } length) parts.Add(length);
            return string.Join(" · ", parts);
        }
    }

    private bool IsLoaded => Player.Book?.Id == Book.Id;

    /// <summary>How far in, from the player while it has this book.</summary>
    private double Elapsed => IsLoaded ? Player.BookElapsed
        : Book.ResumePoint is { } point ? Book.OffsetOfPart(point.Part) + point.Seconds
        : Book.IsFinished ? Book.Duration : 0;

    private void Update()
    {
        PlayLabel = IsLoaded ? Player.IsPlaying ? "Pause" : "Resume" : Book.ResumePoint is not null ? "Resume" : "Play";
        ShowsFromBeginning = Book.ResumePoint is not null || IsLoaded;
        var elapsed = Elapsed;
        Progress = Book.Duration > 0 ? elapsed / Book.Duration * 100 : 0;
        ProgressText = Book.IsFinished && !IsLoaded ? "Finished"
            : elapsed > 0 && Book.Duration > 0 ? $"{MusicFormat.Length((long)(Math.Max(Book.Duration - elapsed, 0) * 10_000_000)) ?? "0 min"} left"
            : null;
        // The list is made again only when the chapter playing changes, not every half second.
        var playing = IsLoaded ? Player.CurrentChapter?.Id : null;
        if (playing == currentChapter && Chapters.Count > 0) return;
        currentChapter = playing;
        Chapters = Book.Chapters
            .Select(c => new ChapterRow(c, MusicFormat.Clock(Book.OffsetOfPart(c.Part) + c.Start), c.Id == playing,
                new RelayCommand(() => Player.Play(Book, c, navigator.JellyfinOnly))))
            .ToList();
    }

    /// <summary>The parts' listening state, fresh from the server.</summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var fresh = await navigator.JellyfinOnly.JellyfinItemsAsync(new()
            {
                ["ids"] = string.Join(',', Book.Parts.Select(p => p.Id)),
                ["fields"] = AudiobookLibrary.Fields,
            });
            if (fresh.Items.Count > 0) Book = Book.Updating(fresh.Items);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
        currentChapter = -1;
        Update();
    }

    [RelayCommand]
    private void Play()
    {
        if (IsLoaded)
        {
            Player.TogglePlayPause();
            navigator.Audio.IsNowPlayingShown = true;
        }
        else
        {
            Player.Play(Book, null, navigator.JellyfinOnly);
        }
    }

    [RelayCommand]
    private void FromBeginning() => Player.Play(Book, Book.Chapters.FirstOrDefault() ?? new AudiobookChapter(0, 0, 0, ""), navigator.JellyfinOnly);

    [RelayCommand] private void Back() => navigator.Back();
}
