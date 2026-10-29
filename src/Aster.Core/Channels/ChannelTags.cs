using System.Globalization;
using System.Text.RegularExpressions;

namespace Aster.Core.Channels;

/// <summary>What a channel made from genres, networks or collections takes from them.</summary>
public enum ChannelContent
{
    All,
    Movies,
    Shows,
}

/// <summary>Which corner of the picture a channel's bug sits in.</summary>
public enum BugCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public static class ChannelTagNames
{
    extension(ChannelContent content)
    {
        public string Tag => content.ToString().ToLowerInvariant();

        public string Title => content switch
        {
            ChannelContent.Movies => "Movies",
            ChannelContent.Shows => "Shows",
            _ => "Movies & Shows",
        };

        /// <summary>The item types to ask the server for: movies, and series (whose episodes are fetched by series).</summary>
        public string ItemTypes => content switch
        {
            ChannelContent.Movies => "Movie",
            ChannelContent.Shows => "Series",
            _ => "Movie,Series",
        };
    }

    extension(BugCorner corner)
    {
        public string Tag => corner switch
        {
            BugCorner.TopLeft => "top-left",
            BugCorner.TopRight => "top-right",
            BugCorner.BottomLeft => "bottom-left",
            _ => "bottom-right",
        };

        public string Title => corner switch
        {
            BugCorner.TopLeft => "Top Left",
            BugCorner.TopRight => "Top Right",
            BugCorner.BottomLeft => "Bottom Left",
            _ => "Bottom Right",
        };

        public bool IsTop => corner is BugCorner.TopLeft or BugCorner.TopRight;
        public bool IsLeft => corner is BugCorner.TopLeft or BugCorner.BottomLeft;
    }
}

/// <summary>
/// A Channel is set up with tags on its Jellyfin playlist, so the server is
/// the one place it lives: edited in Jellyfin's web interface (the
/// playlist's Edit Metadata ▸ Tags) or in Aster's Edit Channels, and every
/// device finds the same channels. The Apple app's docs/channel-tags.md
/// lists them; this reads and writes them exactly as its ChannelTags does,
/// held to the same vectors (a copy in the tests' Conformance folder).
///
/// "aster:" and the keys and values are read whatever their case (and so is
/// "asterfin:", the app's earlier name). A tag that isn't understood is left
/// alone, and a value that doesn't make sense falls back to the default. The
/// app writes every setting out, defaults too (all but the name), so whoever
/// opens the playlist in Jellyfin sees each one there to change.
/// </summary>
public sealed partial record ChannelTags
{
    public const string Prefix = "aster:";

    /// <summary>What the tags began with when the app was called AsterFIN; still read.</summary>
    public const string OldPrefix = "asterfin:";

    /// <summary>The keys this reads and writes; other aster: tags are kept as they are.</summary>
    private static readonly HashSet<string> Keys =
    [
        "channel", "number", "mode", "logo", "reshuffle", "seed", "blocks", "bug", "bug-location", "bug-size", "bug-hide", "bug-opacity",
        "genre", "type", "library", "network", "collection", "years", "rating", "weight", "min-length", "max-length", "filler-playlists",
        "filler-playlist", "filler-types", "filler-type", "bumpers-at", "promo-count", "trailer-count", "interstitial-count", "break-length",
        "break-every", "align", "align-fit", "user", "users", "channel-name", "group",
    ];

    public const int MostPromos = 5;
    public const int LongestBreak = 30;
    public const int LongestBreakGap = 10;
    public static readonly int[] Alignments = [5, 10, 15, 20, 30, 60];
    public const int HeaviestWeight = 5;
    public const int LongestBlock = 20;
    public const int DefaultBugOpacity = 45;

    public bool IsChannel { get; init; }
    public int? Number { get; init; }
    public ChannelMode Mode { get; init; } = ChannelMode.Rotation;
    public bool ShowsLogo { get; init; }
    public ChannelReshuffle Reshuffle { get; init; } = ChannelReshuffle.Day;
    public int Seed { get; init; }
    public (int Low, int High) Blocks { get; init; } = ChannelLineup.DefaultBlocks;
    public bool ShowsBug { get; init; }
    public BugCorner BugCorner { get; init; } = BugCorner.BottomRight;

    /// <summary>The bug's size, as a percentage of the usual.</summary>
    public int BugSize { get; init; } = 100;

    /// <summary>Seconds the bug shows after tuning in or a programme starting; 0 for always.</summary>
    public int BugHideAfter { get; init; }

    /// <summary>How solid the bug is, in percent.</summary>
    public int BugOpacity { get; init; } = DefaultBugOpacity;

    /// <summary>Genres whose titles the channel plays too, any of them.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];
    public ChannelContent Content { get; init; } = ChannelContent.All;

    /// <summary>Libraries, by name, a genre channel takes from; none for every library.</summary>
    public IReadOnlyList<string> Libraries { get; init; } = [];
    public IReadOnlyList<string> Networks { get; init; } = [];
    public IReadOnlyList<string> Collections { get; init; } = [];
    public (int Low, int High)? Years { get; init; }
    public IReadOnlyList<string> Ratings { get; init; } = [];

    /// <summary>Shows that take more turns, by name: "Friends" 2 plays twice as often.</summary>
    public IReadOnlyList<(string Show, int Weight)> Weights { get; init; } = [];

    /// <summary>Titles shorter or longer than these, in minutes, are left out.</summary>
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }

    /// <summary>Playlists, by name, of the clips the breaks between programmes play.</summary>
    public IReadOnlyList<string> FillerPlaylists { get; init; } = [];

    /// <summary>The kinds of clip the breaks play from them.</summary>
    public IReadOnlySet<BreakKind> FillerTypes { get; init; } = AllKinds();

    public ChannelBreaks Breaks { get; init; } = new();

    /// <summary>The users, by Jellyfin user name, who get the channel; none for everyone.</summary>
    public IReadOnlyList<string> Users { get; init; } = [];

    /// <summary>Null for the playlist's own name.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The guide's group for the channel (group=Kids); null for none. Groups
    /// go in order of their lowest channel number, and flicking runs through
    /// one and into the next; without one a channel is under "Other", once
    /// any channel has a group.
    /// </summary>
    public string? Group { get; init; }

    public bool HasSources => Genres.Count > 0 || Networks.Count > 0 || Collections.Count > 0;
    public bool HasBreaks => FillerPlaylists.Count > 0 && FillerTypes.Count > 0;

    private static HashSet<BreakKind> AllKinds() => [.. Enum.GetValues<BreakKind>()];

    /// <summary>Whether <paramref name="userName"/> gets the channel: everyone does unless it names users.</summary>
    public bool IsShownTo(string? userName) =>
        Users.Count == 0 || (userName is not null && Users.Any(u => string.Equals(u, userName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>How the channel's lineup is dealt.</summary>
    public ChannelLineup Lineup => new()
    {
        Mode = Mode,
        Reshuffle = Reshuffle,
        Seed = Seed,
        Blocks = Blocks,
        Weights = Weights.GroupBy(w => w.Show.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Weight),
        MinLength = MinLength * 60.0,
        MaxLength = MaxLength * 60.0,
        Breaks = Breaks,
    };

    public static ChannelTags Parse(IEnumerable<string>? tags)
    {
        var isChannel = false;
        int? number = null;
        var mode = ChannelMode.Rotation;
        var logo = false;
        var reshuffle = ChannelReshuffle.Day;
        var seed = 0;
        var blocks = ChannelLineup.DefaultBlocks;
        var bug = false;
        var corner = BugCorner.BottomRight;
        var bugSize = 100;
        var bugHide = 0;
        var bugOpacity = DefaultBugOpacity;
        List<string> genres = [], libraries = [], networks = [], collections = [], ratings = [], fillers = [], users = [];
        var content = ChannelContent.All;
        (int, int)? years = null;
        var weights = new List<(string Show, int Weight)>();
        int? minLength = null, maxLength = null;
        var fillerTypes = AllKinds();
        var breaks = new ChannelBreaks();
        string? name = null;
        string? group = null;

        foreach (var tag in tags ?? [])
        {
            if (Split(tag) is not var (key, value)) continue;
            switch (key)
            {
                case "channel": isChannel = value is null || !IsOff(value); break;
                case "number": if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0) number = n; break;
                case "mode": mode = Option<ChannelMode>(value) ?? ChannelMode.Rotation; break;
                case "logo": logo = value is null || !IsOff(value); break;
                case "reshuffle": reshuffle = Option<ChannelReshuffle>(value) ?? ChannelReshuffle.Day; break;
                case "seed": seed = Int(value) is { } s ? Math.Max(s, 0) : 0; break;
                case "blocks": blocks = value is null ? ChannelLineup.DefaultBlocks : BlockRange(value) ?? ChannelLineup.DefaultBlocks; break;
                case "bug": bug = value is null || !IsOff(value); break;
                case "bug-location": corner = value is null ? BugCorner.BottomRight : Corner(value) ?? BugCorner.BottomRight; break;
                case "bug-size": bugSize = value is not null && Percent(value) is { } size && size is >= 25 and <= 300 ? size : 100; break;
                case "bug-opacity": bugOpacity = value is not null && Percent(value) is { } opacity && opacity is >= 5 and <= 100 ? opacity : DefaultBugOpacity; break;
                case "bug-hide": bugHide = value is not null && WholeSeconds(value) is { } hide && hide is >= 1 and <= 3600 ? hide : 0; break;
                case "genre": Collect(value, genres); break;
                case "type": content = Option<ChannelContent>(value) ?? ChannelContent.All; break;
                case "library": Collect(value, libraries); break;
                // Studio and collection names can have commas: one per tag.
                case "network": Collect(value, networks, splitting: false); break;
                case "collection": Collect(value, collections, splitting: false); break;
                case "years": years = value is null ? null : YearRange(value); break;
                case "rating": Collect(value, ratings); break;
                case "weight":
                    if (value is not null && ShowWeight(value) is { } weight)
                    {
                        weights.RemoveAll(w => string.Equals(w.Show, weight.Show, StringComparison.OrdinalIgnoreCase));
                        weights.Add(weight);
                    }
                    break;
                case "min-length": minLength = Int(value) is { } min && min is >= 1 and <= 600 ? min : null; break;
                case "max-length": maxLength = Int(value) is { } max && max is >= 1 and <= 600 ? max : null; break;
                case "filler-playlists" or "filler-playlist": Collect(value, fillers, splitting: false); break;
                case "filler-types" or "filler-type":
                {
                    // Kinds it doesn't know are ignored; none it does (or "all") means all.
                    var kinds = (value ?? "").Split(',').Select(k => Kind(k.Trim())).OfType<BreakKind>().ToHashSet();
                    fillerTypes = kinds.Count == 0 ? AllKinds() : kinds;
                    break;
                }
                case "bumpers-at": breaks = breaks with { BumpersAt = Option<BumperPlacement>(value) ?? BumperPlacement.Both }; break;
                case "promo-count": breaks = breaks with { PromoCount = Int(value) is { } p && p is >= 0 and <= MostPromos ? p : 1 }; break;
                case "trailer-count": breaks = breaks with { TrailerCount = Int(value) is { } t && t is >= 0 and <= MostPromos ? t : 1 }; break;
                case "interstitial-count": breaks = breaks with { InterstitialCount = Int(value) is { } i && i is >= 0 and <= MostPromos ? i : 1 }; break;
                case "break-length": breaks = breaks with { Minutes = value is not null && Minutes(value) is { } m && m is >= 0 and <= LongestBreak ? m : ChannelBreaks.DefaultMinutes }; break;
                case "break-every": breaks = breaks with { Every = Int(value) is { } e && e is >= 1 and <= LongestBreakGap ? e : 1 }; break;
                case "align-fit": breaks = breaks with { ExactAlign = string.Equals(value, "exact", StringComparison.OrdinalIgnoreCase) }; break;
                case "align": breaks = breaks with { Align = value is not null && Minutes(value) is { } a && Alignments.Contains(a) ? a : 0 }; break;
                case "user" or "users": Collect(value, users); break;
                case "channel-name": name = string.IsNullOrEmpty(value) ? null : value; break;
                case "group": group = value?.Trim(' ', '\t') is { Length: > 0 } trimmed ? trimmed : null; break;
            }
        }

        return new ChannelTags
        {
            IsChannel = isChannel, Number = number, Mode = mode, ShowsLogo = logo, Reshuffle = reshuffle, Seed = seed, Blocks = blocks,
            ShowsBug = bug, BugCorner = corner, BugSize = bugSize, BugHideAfter = bugHide, BugOpacity = bugOpacity,
            Genres = genres, Content = content, Libraries = libraries, Networks = networks, Collections = collections, Years = years,
            Ratings = ratings, Weights = weights, MinLength = minLength, MaxLength = maxLength, FillerPlaylists = fillers,
            FillerTypes = fillerTypes, Breaks = breaks, Users = users, Name = name, Group = group,
        };
    }

    /// <summary>
    /// <paramref name="tags"/> with Aster's own replaced by every one of
    /// these settings, defaults included; every other tag is kept, in its
    /// place. A playlist that stops being a channel is marked channel=off
    /// and keeps the rest, so turning it back on restores them.
    /// </summary>
    public IReadOnlyList<string> AppliedTo(IEnumerable<string>? tags)
    {
        var result = (tags ?? []).Where(tag => Split(tag) is not var (key, _) || !Keys.Contains(key)).ToList();
        static string On(bool flag) => flag ? "on" : "off";
        void Add(string text) => result.Add(Prefix + text);
        Add(IsChannel ? "channel" : "channel=off");
        if (Number is { } number) Add($"number={number}");
        Add($"mode={Mode.Tag}");
        Add($"reshuffle={Reshuffle.Tag}");
        Add(Blocks.Low == Blocks.High ? $"blocks={Blocks.Low}" : $"blocks={Blocks.Low}-{Blocks.High}");
        Add($"seed={Seed}");
        Add($"logo={On(ShowsLogo)}");
        Add($"bug={On(ShowsBug)}");
        Add($"bug-location={BugCorner.Tag}");
        Add($"bug-size={BugSize}%");
        Add(BugHideAfter > 0 ? $"bug-hide={BugHideAfter}" : "bug-hide=off");
        Add($"bug-opacity={BugOpacity}%");
        foreach (var genre in Genres) Add($"genre={genre}");
        Add($"type={Content.Tag}");
        foreach (var library in Libraries) Add($"library={library}");
        foreach (var network in Networks) Add($"network={network}");
        foreach (var collection in Collections) Add($"collection={collection}");
        if (Years is { } years) Add(years.Low == years.High ? $"years={years.Low}" : $"years={years.Low}-{years.High}");
        foreach (var rating in Ratings) Add($"rating={rating}");
        foreach (var (show, weight) in Weights) Add($"weight={show}:{weight}");
        if (MinLength is { } min) Add($"min-length={min}");
        if (MaxLength is { } max) Add($"max-length={max}");
        // The break settings only mean something with clips to play.
        if (HasBreaks)
        {
            foreach (var playlist in FillerPlaylists) Add($"filler-playlists={playlist}");
            var kinds = Enum.GetValues<BreakKind>();
            Add("filler-types=" + (FillerTypes.Count == kinds.Length ? "all" : string.Join(",", kinds.Where(FillerTypes.Contains).Select(k => k.Tag))));
            Add($"bumpers-at={Breaks.BumpersAt.Tag}");
            Add($"break-length={Breaks.Minutes}");
            Add($"promo-count={Breaks.PromoCount}");
            Add($"trailer-count={Breaks.TrailerCount}");
            Add($"interstitial-count={Breaks.InterstitialCount}");
            Add($"break-every={Breaks.Every}");
            Add(Breaks.Align > 0 ? $"align={Breaks.Align}" : "align=off");
            Add("align-fit=" + (Breaks.ExactAlign ? "exact" : "best"));
        }
        if (Users.Count > 0) Add("user=" + string.Join(",", Users));
        if (Name is { } name) Add($"channel-name={name}");
        if (Group is { } group) Add($"group={group}");
        return result;
    }

    // ---- Clips ----

    /// <summary>Whether a playlist's tags make it a filler playlist (aster:filler): one of break clips.</summary>
    public static bool IsFillerPlaylist(IEnumerable<string>? tags) =>
        (tags ?? []).Any(tag => Split(tag) is var (key, value) && key == "filler" && (value is null || !IsOff(value)));

    /// <summary>What a break clip is, from its own tag; null for a clip that doesn't say, which breaks leave out.</summary>
    public static BreakKind? ClipKind(IEnumerable<string>? tags)
    {
        foreach (var tag in tags ?? [])
        {
            if (Split(tag) is var (key, _) && Kind(key) is { } kind) return kind;
        }
        return null;
    }

    /// <summary>Where a bumper goes: =in only closes a break, =out only opens one, a plain one either end.</summary>
    public static BumperPlacement BumperSide(IEnumerable<string>? tags)
    {
        foreach (var tag in tags ?? [])
        {
            if (Split(tag) is not var (key, value) || Kind(key) != BreakKind.Bumpers) continue;
            return Option<BumperPlacement>(value) ?? BumperPlacement.Both;
        }
        return BumperPlacement.Both;
    }

    /// <summary>Whether <paramref name="tags"/> set <paramref name="key"/> at all, as opposed to leaving it to its default.</summary>
    public static bool Sets(string key, IEnumerable<string>? tags) => (tags ?? []).Any(tag => Split(tag)?.Key == key);

    /// <summary>
    /// The tags kept in a description, where a server has no tags on
    /// playlists (Plex): "aster:channel, aster:number=7" as two tags. Only a
    /// comma before the next "aster:" parts them, so a comma inside a value
    /// ("aster:user=alice,bob", a name) stays in it. Text before a line's
    /// first one is the description's own, and left out.
    /// </summary>
    public static IReadOnlyList<string> FromDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var tags = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (EntryStart().Match(line) is not { Success: true } start) continue;
            foreach (var entry in EntryBreak().Split(line[start.Index..]))
            {
                if (entry.Trim() is { Length: > 0 } tag) tags.Add(tag);
            }
        }
        return tags;
    }

    /// <summary>
    /// <paramref name="description"/> with its tags replaced by
    /// <paramref name="tags"/>, on a line of their own after the
    /// description's own words, which are kept as they were.
    /// </summary>
    public static string InDescription(string? description, IEnumerable<string> tags)
    {
        var own = new List<string>();
        foreach (var line in (description ?? "").Replace("\r", "").Split('\n'))
        {
            // A line's words before its tags stay; the tags go.
            var kept = EntryStart().Match(line) is { Success: true } start ? line[..start.Index].TrimEnd(' ', ',') : line;
            if (kept.Length > 0 || !EntryStart().IsMatch(line)) own.Add(kept);
        }
        while (own.Count > 0 && own[^1].Trim().Length == 0) own.RemoveAt(own.Count - 1);
        var entries = string.Join(", ", tags.Select(t => t.Trim()).Where(t => t.Length > 0));
        if (entries.Length == 0) return string.Join("\n", own);
        return own.Count == 0 ? entries : string.Join("\n", own) + "\n\n" + entries;
    }

    /// <summary>"aster:" (or "asterfin:") starting a line, or after a space or comma.</summary>
    [GeneratedRegex(@"(?<![^\s,])aster(?:fin)?:", RegexOptions.IgnoreCase)]
    private static partial Regex EntryStart();

    /// <summary>A comma before the next "aster:".</summary>
    [GeneratedRegex(@",\s*(?=aster(?:fin)?:)", RegexOptions.IgnoreCase)]
    private static partial Regex EntryBreak();

    /// <summary><paramref name="tags"/> with every "asterfin:" tag renamed "aster:"; null when there's none to rename.</summary>
    public static IReadOnlyList<string>? Converted(IEnumerable<string>? tags)
    {
        if (tags is null) return null;
        var changed = false;
        var renamed = tags.Select(tag =>
        {
            var trimmed = tag.Trim();
            if (!trimmed.StartsWith(OldPrefix, StringComparison.OrdinalIgnoreCase)) return tag;
            changed = true;
            return Prefix + trimmed[OldPrefix.Length..];
        }).ToList();
        return changed ? renamed : null;
    }

    /// <summary>
    /// Channel numbers for a set of channels: their own where they have one,
    /// then the rest after the highest of those, in name order. Two channels
    /// asking for the same number both keep it.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Numbers(IEnumerable<(string Id, string Name, int? Number)> channels)
    {
        var list = channels.ToList();
        var result = new Dictionary<string, int>();
        var next = (list.Select(c => c.Number).OfType<int>().DefaultIfEmpty(0).Max()) + 1;
        foreach (var channel in list)
        {
            if (channel.Number is { } number) result[channel.Id] = number;
        }
        foreach (var channel in list.Where(c => c.Number is null).OrderBy(c => c.Name, NaturalComparer.Instance))
        {
            result[channel.Id] = next++;
        }
        return result;
    }

    // ---- Reading values ----

    /// <summary>"aster:Number=101" (or "asterfin:Number=101") as ("number", "101"); null for a tag that isn't Aster's.</summary>
    private static (string Key, string? Value)? Split(string tag)
    {
        var trimmed = tag.Trim();
        var start = trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? Prefix
            : trimmed.StartsWith(OldPrefix, StringComparison.OrdinalIgnoreCase) ? OldPrefix : null;
        if (start is null) return null;
        var body = trimmed[start.Length..];
        var equals = body.IndexOf('=');
        var key = (equals < 0 ? body : body[..equals]).Trim().ToLowerInvariant();
        if (key.Length == 0) return null;
        var value = equals < 0 ? null : body[(equals + 1)..].Trim();
        return (key, value);
    }

    private static bool IsOff(string value) => value.ToLowerInvariant() is "off" or "no" or "false" or "0";

    private static int? Int(string? value) =>
        value is not null && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static T? Option<T>(string? value) where T : struct, System.Enum
    {
        if (value is null) return null;
        // Tag spellings are lower case, with hyphens for the corners' names.
        foreach (var option in System.Enum.GetValues<T>())
        {
            var tag = option switch
            {
                BugCorner corner => corner.Tag,
                _ => option.ToString().ToLowerInvariant(),
            };
            if (tag == value.ToLowerInvariant()) return option;
        }
        return null;
    }

    private static BugCorner? Corner(string value) => Option<BugCorner>(value);

    /// <summary>"bumper" or "bumpers", "commercial" or "commercials" and so on, as a kind.</summary>
    private static BreakKind? Kind(string word)
    {
        var lower = word.ToLowerInvariant();
        return Option<BreakKind>(lower) ?? Option<BreakKind>(lower + "s");
    }

    /// <summary>"1985-1999" as 1985…1999 (either way round), "1994" as 1994…1994; null unless sensible.</summary>
    private static (int, int)? YearRange(string value)
    {
        var parts = value.Split('-').Select(p => Int(p.Trim())).ToList();
        if (parts.Count is < 1 or > 2 || parts[0] is not { } first || parts[^1] is not { } last) return null;
        var (low, high) = (Math.Min(first, last), Math.Max(first, last));
        return low >= 1880 && high <= 2200 ? (low, high) : null;
    }

    /// <summary>"30", "30m" or ":30" as 30; "off" as 0; null for anything else.</summary>
    private static int? Minutes(string value)
    {
        var text = value.ToLowerInvariant().Trim();
        if (IsOff(text)) return 0;
        if (text.StartsWith(':')) text = text[1..];
        foreach (var unit in new[] { "minutes", "minute", "mins", "min", "m" })
        {
            if (!text.EndsWith(unit, StringComparison.Ordinal)) continue;
            text = text[..^unit.Length].Trim();
            break;
        }
        return Int(text);
    }

    /// <summary>"Friends:2" as Friends, 2; null unless the show is named and the weight at least 1. Over 5 counts as 5.</summary>
    private static (string Show, int Weight)? ShowWeight(string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon < 0) return null;
        var show = value[..colon].Trim();
        if (show.Length == 0 || Int(value[(colon + 1)..].Trim()) is not { } weight || weight < 1) return null;
        return (show, Math.Min(weight, HeaviestWeight));
    }

    /// <summary>"Horror, Thriller" into the list, once each whatever the case; without splitting, the whole value is one name.</summary>
    private static void Collect(string? value, List<string> list, bool splitting = true)
    {
        var pieces = splitting ? (value ?? "").Split(',') : [value ?? ""];
        foreach (var piece in pieces)
        {
            var name = piece.Trim();
            if (name.Length == 0 || list.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(name);
        }
    }

    /// <summary>"30", "30s" as 30; null for "off" or anything else.</summary>
    private static int? WholeSeconds(string value) => Int(value.ToLowerInvariant().Trim('s', ' '));

    /// <summary>"150%" or "150" as 150.</summary>
    private static int? Percent(string value) => Int(value.Trim('%', ' '));

    /// <summary>"3-6" as 3…6, "4" as 4…4; null unless it's 1 to 20, low to high.</summary>
    private static (int, int)? BlockRange(string value)
    {
        var parts = value.Split('-').Select(p => Int(p.Trim())).ToList();
        if (parts.Count is < 1 or > 2 || parts[0] is not { } low || parts[^1] is not { } high) return null;
        return low >= 1 && low <= high && high <= LongestBlock ? (low, high) : null;
    }
}

/// <summary>Names in the order Finder sorts them: numbers by their value, case ignored (localizedStandardCompare).</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
        var (i, j) = (0, 0);
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var (si, sj) = (i, j);
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var byNumber = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (byNumber != 0) return byNumber;
                continue;
            }
            var byText = string.Compare(x[i].ToString(), y[j].ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            if (byText != 0) return byText;
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
