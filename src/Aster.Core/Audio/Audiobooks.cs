// The server's audiobooks, gathered into books. A port of the Apple app's
// AudiobookLibrary.swift.
//
// Jellyfin lists audiobook files, not books: an .m4b is a whole book with
// its chapters inside, but a book kept as a folder of MP3s is one item per
// file. Files in the same folder with the same album are taken as the parts
// of one book. Authors and series often aren't tagged, so they also come
// from the folders, laid out as Author/[Series/]Book/.

using System.Text.Json;
using Aster.Core.Browsing;
using Aster.Core.Jellyfin;

namespace Aster.Core.Audio;

/// <summary>A place in a book's chapter list: which part (file) it's in, and how far into that part it starts.</summary>
public sealed record AudiobookChapter(int Id, int Part, double Start, string Title);

public sealed record Audiobook
{
    /// <summary>The file's id for a one-file book, or the first part's for the rest.</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public string? Series { get; init; }

    /// <summary>Its place in the series, when the file name or tags give one.</summary>
    public int? SeriesIndex { get; init; }

    /// <summary>The book's files in order; never empty.</summary>
    public required IReadOnlyList<BaseItem> Parts { get; init; }

    /// <summary>A book from one file, as Search finds them: no folder to read the author or series from.</summary>
    public static Audiobook FromItem(BaseItem item) =>
        new() { Id = item.Id, Title = item.DisplayTitle, Author = item.AlbumArtist ?? item.ArtistText, Parts = [item] };

    /// <summary>The item whose cover and description stand for the book.</summary>
    public BaseItem Cover => Parts[0];

    public string? Overview => Parts.Select(p => p.Overview).FirstOrDefault(o => o is not null);
    public int? Year => Cover.ProductionYear;
    public string? DateAdded => Cover.DateCreated;

    /// <summary>Each part's length, in seconds.</summary>
    public IReadOnlyList<double> PartDurations => Parts.Select(p => (p.RunTimeTicks ?? 0) / 10_000_000.0).ToList();

    public double Duration => PartDurations.Sum();

    /// <summary>Seconds into the book where <paramref name="part"/> begins.</summary>
    public double OffsetOfPart(int part) => PartDurations.Take(Math.Max(part, 0)).Sum();

    /// <summary>
    /// The chapters across every part: an .m4b's own chapters, and for a part
    /// without any, the part itself. Empty for a single file with none.
    /// </summary>
    public IReadOnlyList<AudiobookChapter> Chapters
    {
        get
        {
            var list = new List<AudiobookChapter>();
            for (var index = 0; index < Parts.Count; index++)
            {
                var part = Parts[index];
                var own = (part.Chapters ?? []).OrderBy(c => c.StartPositionTicks).ToList();
                if (own.Count == 0)
                {
                    if (Parts.Count > 1) list.Add(new AudiobookChapter(list.Count, index, 0, part.Name ?? $"Part {index + 1}"));
                    continue;
                }
                foreach (var chapter in own)
                {
                    var title = string.IsNullOrEmpty(chapter.Name) ? $"Chapter {list.Count + 1}" : chapter.Name;
                    list.Add(new AudiobookChapter(list.Count, index, chapter.StartPositionTicks / 10_000_000.0, title));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Where listening left off, from what Jellyfin recorded: the last part
    /// started and how far into it, or the start of the part after the last
    /// one finished. Null before the book is started and once it's finished.
    /// </summary>
    public (int Part, double Seconds)? ResumePoint
    {
        get
        {
            var last = -1;
            for (var i = 0; i < Parts.Count; i++)
            {
                if ((Parts[i].UserData?.PlaybackPositionTicks ?? 0) > 0 || Parts[i].UserData?.Played == true) last = i;
            }
            if (last < 0) return null;
            if (Parts[last].UserData?.PlaybackPositionTicks is { } ticks and > 0) return (last, ticks / 10_000_000.0);
            return last + 1 < Parts.Count ? (last + 1, 0) : null;
        }
    }

    public bool IsFinished => Parts[^1].UserData?.Played == true && ResumePoint is null;

    /// <summary>How much has been listened to, 0 to 1.</summary>
    public double Progress
    {
        get
        {
            if (Duration <= 0) return 0;
            if (IsFinished) return 1;
            if (ResumePoint is not { } point) return 0;
            return Math.Min((OffsetOfPart(point.Part) + point.Seconds) / Duration, 1);
        }
    }

    /// <summary>When any part was last played (ISO 8601, so it sorts as text).</summary>
    public string? LastPlayed => Parts.Select(p => p.UserData?.LastPlayedDate).OfType<string>().Max(StringComparer.Ordinal);

    /// <summary>"8 hr 12 min" for the whole book.</summary>
    public string? LengthText => MusicFormat.Length(Parts.Sum(p => p.RunTimeTicks ?? 0));

    /// <summary>A copy with the parts' listening state refreshed from the server.</summary>
    public Audiobook Updating(IEnumerable<BaseItem> fresh)
    {
        var byId = fresh.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        return this with { Parts = Parts.Select(p => byId.GetValueOrDefault(p.Id) ?? p).ToList() };
    }
}

public enum AudiobookShelfKind
{
    Author,
    Series,
}

/// <summary>An author's books, or a series in order: what an author or series page shows.</summary>
public sealed record AudiobookShelf(AudiobookShelfKind Kind, string Name, IReadOnlyList<Audiobook> Books);

public static class AudiobookLibrary
{
    /// <summary>What a book needs from each file: chapters, length, its folder.</summary>
    public const string Fields = "Overview,ProductionYear,RunTimeTicks,ParentId,DateCreated,Chapters,PrimaryImageAspectRatio";

    /// <summary>Every book in <paramref name="libraries"/>, by title.</summary>
    public static async Task<IReadOnlyList<Audiobook>> LoadAsync(JellyfinClient client, IEnumerable<BaseItem> libraries, CancellationToken ct = default)
    {
        var all = new List<Audiobook>();
        foreach (var library in libraries)
        {
            var folders = client.JellyfinItemsAsync(new()
            {
                ["parentId"] = library.Id,
                ["includeItemTypes"] = "Folder",
                ["recursive"] = "true",
                ["fields"] = "ParentId",
                ["enableUserData"] = "false",
            }, ct);
            var files = client.JellyfinItemsAsync(new()
            {
                ["parentId"] = library.Id,
                ["includeItemTypes"] = "AudioBook",
                ["recursive"] = "true",
                ["fields"] = Fields,
                ["sortBy"] = "SortName",
                ["sortOrder"] = "Ascending",
            }, ct);
            all.AddRange(Books((await files).Items, (await folders).Items));
        }
        return all.OrderBy(b => TitleSort.Key(b.Title), TitleSort.Comparer).ToList();
    }

    /// <summary>Groups files into books, and reads authors and series from the folders the files sit in.</summary>
    public static IReadOnlyList<Audiobook> Books(IReadOnlyList<BaseItem> files, IReadOnlyList<BaseItem> folders)
    {
        var folderById = folders.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());

        // The folder names from just inside the library down to `id`.
        List<string> Chain(string? id)
        {
            var names = new List<string>();
            var seen = new HashSet<string>();
            while (id is not null && folderById.TryGetValue(id, out var folder) && seen.Add(id))
            {
                // The library's own top folder has no folder above it.
                if (folder.ParentId is not { } parent || !folderById.ContainsKey(parent)) break;
                names.Insert(0, folder.DisplayTitle);
                id = parent;
            }
            return names;
        }

        // Parts of one book share a folder and an album; a file with no album is a book of its own.
        var groups = files
            .GroupBy(f => $"{f.ParentId}|{(string.IsNullOrEmpty(f.Album) ? f.Id : f.Album)}")
            .Select(g => g.ToList())
            .ToList();
        var booksPerFolder = groups.GroupBy(g => g[0].ParentId ?? "").ToDictionary(g => g.Key, g => g.Count());

        return groups.Select(group =>
        {
            var parts = group
                .OrderBy(p => p.ParentIndexNumber ?? 0)
                .ThenBy(p => p.IndexNumber ?? 0)
                .ThenBy(p => p.DisplayTitle, StringComparer.Ordinal)
                .ToList();
            var first = parts[0];
            var chain = Chain(first.ParentId);
            string? authorFolder = null;
            if (chain.Count > 0)
            {
                authorFolder = chain[0];
                chain.RemoveAt(0);
            }
            // A folder holding just this book is the book's own, not a series.
            if (booksPerFolder.GetValueOrDefault(first.ParentId ?? "") == 1 && chain.Count > 0) chain.RemoveAt(chain.Count - 1);
            var series = chain.FirstOrDefault();
            return new Audiobook
            {
                Id = first.Id,
                Title = parts.Count > 1 ? first.Album ?? first.DisplayTitle : first.DisplayTitle,
                Author = authorFolder ?? first.AlbumArtist ?? first.ArtistText,
                Series = series,
                SeriesIndex = series is not null && parts.Count == 1 ? first.IndexNumber : null,
                Parts = parts,
            };
        }).ToList();
    }

    /// <summary>Books started and not finished, most recently played first.</summary>
    public static IReadOnlyList<Audiobook> ContinueListening(IEnumerable<Audiobook> books) =>
        books.Where(b => b.ResumePoint is not null).OrderByDescending(b => b.LastPlayed ?? "", StringComparer.Ordinal).ToList();

    public static IReadOnlyList<Audiobook> RecentlyAdded(IEnumerable<Audiobook> books) =>
        books.OrderByDescending(b => b.DateAdded ?? "", StringComparer.Ordinal).Take(20).ToList();

    public static IReadOnlyList<AudiobookShelf> Shelves(IEnumerable<Audiobook> books, AudiobookShelfKind kind) =>
        books
            .Select(b => (Book: b, Key: kind == AudiobookShelfKind.Author ? b.Author : b.Series))
            .Where(e => e.Key is not null)
            .GroupBy(e => e.Key!)
            .Select(g => new AudiobookShelf(kind, g.Key, kind == AudiobookShelfKind.Series
                ? g.Select(e => e.Book).OrderBy(b => b.SeriesIndex ?? int.MaxValue).ThenBy(b => b.Title, StringComparer.Ordinal).ToList()
                : g.Select(e => e.Book).OrderBy(b => TitleSort.Key(b.Title), TitleSort.Comparer).ToList()))
            .OrderBy(s => s.Name, TitleSort.Comparer)
            .ToList();
}

/// <summary>The listening speed chosen for each book, kept on this computer.</summary>
public static class AudiobookRates
{
    public static readonly IReadOnlyList<double> Choices = [0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    private static readonly string FilePath = Path.Combine(AppPaths.Data, "audiobook-rates.json");
    private static readonly Lock Gate = new();
    private static Dictionary<string, double>? rates;

    public static double Rate(string bookId)
    {
        lock (Gate) return Load().GetValueOrDefault(bookId, 1);
    }

    public static void SetRate(double rate, string bookId)
    {
        lock (Gate)
        {
            var all = Load();
            if (rate == 1) all.Remove(bookId); else all[bookId] = rate;
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(all)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>"1×", "1.25×".</summary>
    public static string Label(double rate) => $"{rate:0.##}×";

    private static Dictionary<string, double> Load()
    {
        if (rates is not null) return rates;
        try { rates = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return rates ??= [];
    }
}
