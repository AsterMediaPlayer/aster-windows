// Edit Channels: an administrator's page for making Channels and setting
// them up, as the Apple app's ChannelSettingsView. Channels are tags on
// Jellyfin video playlists (ChannelTags), so every change writes the
// playlist's tags at once, keeping its other tags, and the Channels tab
// picks it up. The page is a list of rows, built again after each change.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Channels;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A row of the page. Each kind has its own look (EditChannelsView's templates).</summary>
public abstract record EditRow;

/// <summary>A heading over the rows that follow: "Channel 3", "New Channel".</summary>
public sealed record EditSectionRow(string Title) : EditRow;

/// <summary>A quieter row of explanation.</summary>
public sealed record EditNoteRow(string Text) : EditRow;

/// <summary>A row that does something when clicked: open a channel, fold out part of it, Shuffle Again.</summary>
/// <param name="Detail">A second line under the title.</param>
/// <param name="Value">On the right, before the chevron.</param>
/// <param name="Chevron">"" for none; a right chevron or an up or down one.</param>
public sealed record EditActionRow(string Title, string? Detail, string? Value, string Chevron, IRelayCommand Command) : EditRow
{
    public bool HasDetail => Detail is not null;
}

/// <summary>A row with a menu of choices (one, or several), what's chosen on the right.</summary>
public sealed record EditChoiceRow(string Title, string Value, IReadOnlyList<TrackChoice> Choices) : EditRow;

/// <summary>On or off, flipped by a click.</summary>
public sealed record EditToggleRow(string Title, string? Detail, bool IsOn, IRelayCommand Flip) : EditRow
{
    public bool HasDetail => Detail is not null;
}

/// <summary>Text, saved on Enter or as the field is left.</summary>
public sealed record EditTextRow(string Title, string Placeholder, Action<string> Save) : EditRow
{
    /// <summary>What's typed, bound both ways.</summary>
    public string Text { get; set; } = "";

    public void Commit() => Save(Text);
}

/// <summary>What New Channel can make a channel from.</summary>
public enum NewChannelSource
{
    Show,
    Genre,
    Network,
    Collection,
    Playlist,
}

public sealed partial class EditChannelsViewModel : ViewModelBase, IRefreshable
{
    private readonly INavigator navigator;

    /// <summary>One channel alone, already open (from its guide row); null for them all.</summary>
    private readonly string? only;

    private List<BaseItem> playlists = [];
    private bool loaded;
    private bool? isAdministrator;

    /// <summary>A save the server refused for want of rights: the page turns read-only.</summary>
    private bool refused;

    private bool isCreating;
    private bool showsHelp;
    private readonly HashSet<string> expanded = [];

    /// <summary>The open channels' parts folded out: "plays|id" and "advanced|id".</summary>
    private readonly HashSet<string> folds = [];

    private IReadOnlyList<string> genreNames = [], libraryNames = [], networkNames = [], collectionNames = [], ratingNames = [], userNames = [];
    private IReadOnlyList<(string Id, string Name)> seriesOptions = [];

    public EditChannelsViewModel(INavigator navigator, string? only = null)
    {
        this.navigator = navigator;
        this.only = only;
        if (only is not null) expanded.Add(only);
        Rebuild();
        _ = LoadAsync();
    }

    public string Title => only is not null && Channels.FirstOrDefault(c => c.Playlist.Id == only) is { Playlist: not null } channel ? NameOf(channel) : "Channels (Beta)";

    [ObservableProperty] public partial IReadOnlyList<EditRow> Rows { get; private set; } = [];
    [ObservableProperty] public partial string? Message { get; private set; }

    private JellyfinClient Client => navigator.JellyfinOnly;
    private bool CanEdit => isAdministrator == true && !refused;

    public void Refresh() => _ = LoadAsync();

    [RelayCommand] private void Back() => navigator.Back();

    // ---- What there is ----

    private List<(BaseItem Playlist, ChannelTags Tags)> Channels
    {
        get
        {
            var numbers = Numbers;
            return ChannelLoader.TaggedChannels(playlists)
                .OrderBy(c => numbers.GetValueOrDefault(c.Playlist.Id))
                .ThenBy(NameOf, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    private static string NameOf((BaseItem Playlist, ChannelTags Tags) channel) => channel.Tags.Name ?? channel.Playlist.DisplayTitle;

    /// <summary>Each channel's number in the guide: its own, or the next free one.</summary>
    private IReadOnlyDictionary<string, int> Numbers =>
        ChannelTags.Numbers(ChannelLoader.TaggedChannels(playlists).Select(c => (c.Playlist.Id, NameOf(c), c.Tags.Number)));

    /// <summary>Playlists that could be channels: not one already, and not a filler playlist.</summary>
    private List<BaseItem> Others => playlists.Where(p => !ChannelTags.Parse(p.Tags).IsChannel && !ChannelTags.IsFillerPlaylist(p.Tags)).ToList();

    /// <summary>The filler playlists (aster:filler) a channel's breaks can play from.</summary>
    private List<string> FillerNames => playlists.Where(p => ChannelTags.IsFillerPlaylist(p.Tags)).Select(p => p.DisplayTitle).ToList();

    /// <summary>"Tooniverse and Sitcoms are all channel 3."</summary>
    private IEnumerable<string> Clashes =>
        Channels.Where(c => c.Tags.Number is not null).GroupBy(c => c.Tags.Number!.Value).OrderBy(g => g.Key)
            .Where(g => g.Count() > 1)
            .Select(g => $"{JoinAnd(g.Select(NameOf).ToList())} are all channel {g.Key}.");

    private static string JoinAnd(IReadOnlyList<string> names) =>
        names.Count <= 2 ? string.Join(" and ", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private async Task LoadAsync()
    {
        var client = Client;
        var me = client.MeAsync();
        var genres = client.VideoGenresAsync();
        var views = client.UserViewsAsync();
        var studios = client.VideoStudiosAsync();
        var boxSets = client.CollectionsAsync();
        var ratings = client.VideoRatingsAsync();
        var users = client.UserNamesAsync();
        var series = client.SeriesListAsync();
        try
        {
            playlists = (await client.PlaylistsAsync("Video")).ToList();
            Message = null;
        }
        catch (Exception error) when (IsServerError(error))
        {
            Message = error.Message;
        }
        isAdministrator = (await OrNull(me))?.Policy?.IsAdministrator == true;
        genreNames = ((await OrNull(genres)) ?? []).Select(g => g.DisplayTitle).ToList();
        // Video libraries only: a genre channel plays movies and episodes.
        string[] skipped = ["music", "books", "livetv", "playlists", "boxsets", "musicvideos"];
        libraryNames = ((await OrNull(views)) ?? []).Where(v => !skipped.Contains(v.CollectionType?.ToLowerInvariant() ?? "")).Select(v => v.DisplayTitle).ToList();
        networkNames = ((await OrNull(studios)) ?? []).Select(s => s.DisplayTitle).ToList();
        // Empty collections (book series often are, to video) are left out.
        collectionNames = ((await OrNull(boxSets)) ?? []).Where(c => (c.ChildCount ?? 1) > 0).Select(c => c.DisplayTitle).ToList();
        ratingNames = (await OrNull(ratings)) ?? [];
        userNames = (await OrNull(users)) ?? [];
        seriesOptions = ((await OrNull(series)) ?? []).Select(s => (s.Id, s.DisplayTitle)).ToList();
        loaded = true;
        OnPropertyChanged(nameof(Title));
        Rebuild();
    }

    private static async Task<T?> OrNull<T>(Task<T> task) where T : class
    {
        try { return await task; }
        catch (Exception error) when (IsServerError(error)) { return null; }
    }

    private static bool IsServerError(Exception error) => error is JellyfinException or HttpRequestException or TimeoutException;

    // ---- Changing ----

    /// <summary>Changes a channel starting from its latest tags, so a change made a moment after another never undoes it.</summary>
    private void Update(BaseItem playlist, Func<ChannelTags, ChannelTags> change)
    {
        var current = playlists.FirstOrDefault(p => p.Id == playlist.Id) ?? playlist;
        _ = SaveAsync(current, change(ChannelTags.Parse(current.Tags)));
    }

    /// <summary>
    /// Writes the playlist's tags with these channel settings, keeping its
    /// other tags; the page shows the change at once, and puts it back if the
    /// server refuses it. A channel numbered by itself keeps the number it
    /// has now, so later channels don't move it.
    /// </summary>
    private async Task SaveAsync(BaseItem playlist, ChannelTags tags)
    {
        if (!CanEdit) return;
        if (tags.Number is null && Numbers.TryGetValue(playlist.Id, out var number)) tags = tags with { Number = number };
        var index = playlists.FindIndex(p => p.Id == playlist.Id);
        if (index < 0) return;
        var previous = playlists[index].Tags;
        var updated = tags.AppliedTo(previous);
        playlists[index] = playlists[index] with { Tags = updated };
        Rebuild();
        try
        {
            await Client.SetTagsAsync(updated, playlist.Id);
            Message = null;
            // The Channels tab loads them again when next shown.
            navigator.UserDataChanged();
        }
        catch (Exception error) when (IsServerError(error))
        {
            var current = playlists.FindIndex(p => p.Id == playlist.Id);
            if (current >= 0) playlists[current] = playlists[current] with { Tags = previous };
            if (error is JellyfinException { StatusCode: 401 or 403 }) refused = true;
            Message = error.Message;
            Rebuild();
        }
    }

    /// <summary>Makes a playlist a channel, with the next free number and its picture as the logo when it has one, unless its tags say otherwise.</summary>
    private void TurnOn(BaseItem playlist)
    {
        var tags = ChannelTags.Parse(playlist.Tags) with { IsChannel = true };
        if (tags.Number is null) tags = tags with { Number = (Numbers.Values.DefaultIfEmpty(0).Max()) + 1 };
        if (!ChannelTags.Sets("logo", playlist.Tags)) tags = tags with { ShowsLogo = playlist.ImageTags?.ContainsKey("Primary") == true };
        expanded.Add(playlist.Id);
        _ = SaveAsync(playlist, tags);
    }

    /// <summary>
    /// New Channel: a public video playlist of a show's episodes, or an empty
    /// one filled from a genre, network or collection, tagged with the next
    /// free number and opened; or one of the user's playlists turned on.
    /// </summary>
    private async Task CreateAsync(NewChannelSource source, string id, string name)
    {
        if (source == NewChannelSource.Playlist)
        {
            if (playlists.FirstOrDefault(p => p.Id == id) is { } existing) TurnOn(existing);
            return;
        }
        if (!CanEdit || isCreating) return;
        isCreating = true;
        Rebuild();
        var tags = new ChannelTags { IsChannel = true, Number = Numbers.Values.DefaultIfEmpty(0).Max() + 1 };
        tags = source switch
        {
            NewChannelSource.Show => tags with { ShowsLogo = true },
            NewChannelSource.Genre => tags with { Genres = [name] },
            NewChannelSource.Network => tags with { Networks = [name] },
            NewChannelSource.Collection => tags with { Collections = [name] },
            _ => tags,
        };
        try
        {
            var episodes = source == NewChannelSource.Show ? (await Client.SeriesEpisodesAsync(id)).Select(e => e.Id).ToList() : [];
            var playlistId = await Client.CreatePlaylistAsync(name, episodes, "Video", isPublic: true);
            await Client.SetTagsAsync(tags.AppliedTo([]), playlistId);
            expanded.Add(playlistId);
            navigator.UserDataChanged();
            Message = null;
        }
        catch (Exception error) when (IsServerError(error))
        {
            Message = error.Message;
        }
        isCreating = false;
        await LoadAsync();
    }

    // ---- The rows ----

    private static readonly int[] BugHideChoices = [0, 10, 30, 60, 120, 300];
    private static readonly int[] BugOpacityChoices = [25, 35, 45, 60, 75, 90, 100];
    private static readonly int[] BugSizeChoices = [50, 75, 100, 125, 150, 200, 250, 300];
    private static readonly int[] CommercialChoices = [0, 1, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30];
    private static readonly int?[] LengthChoices = [null, 5, 10, 15, 20, 30, 45, 60, 90, 120, 150, 180];
    private static readonly (int Low, int High)?[] YearChoices = [null, .. Enumerable.Range(0, 8).Select(i => ((int, int)?)(1950 + i * 10, 1959 + i * 10))];
    private static readonly (int Low, int High)[] BlockChoices = [ChannelLineup.DefaultBlocks, (1, 2), (1, 3), (2, 3), (3, 4), (3, 5), (3, 6), (4, 6), (4, 8), (6, 10)];

    private const string Right = "", Down = "", Up = "";

    private void Rebuild()
    {
        var rows = new List<EditRow>();
        if (only is not null) BuildOne(rows);
        else BuildAll(rows);
        Rows = rows;
    }

    private void BuildOne(List<EditRow> rows)
    {
        if (!loaded)
        {
            rows.Add(new EditNoteRow(Message ?? "Loading…"));
            return;
        }
        if (Channels.FirstOrDefault(c => c.Playlist.Id == only) is { Playlist: not null } channel) ChannelRows(rows, channel.Playlist, channel.Tags);
        else rows.Add(new EditNoteRow("This playlist isn't a channel any more. Turn it back on in Edit Channels."));
    }

    private void BuildAll(List<EditRow> rows)
    {
        rows.Add(new EditNoteRow("Turn video playlists into TV-style channels. Each plays its movies and episodes around the clock, the same on every device; tune in and you join whatever's on. Watching a channel doesn't touch your watch history. The Channels tab appears once there is one."));
        if (!loaded)
        {
            rows.Add(new EditNoteRow(Message ?? "Loading…"));
            return;
        }
        if (isAdministrator != true)
        {
            rows.Add(new EditNoteRow("Only your server's administrator can set up channels. Ask them to, here or in Jellyfin."));
        }
        else
        {
            rows.Add(new EditSectionRow("NEW CHANNEL"));
            foreach (var source in Enum.GetValues<NewChannelSource>())
            {
                var options = Options(source);
                rows.Add(new EditChoiceRow(SourceTitle(source), isCreating ? "Making it…" : options.Count == 0 ? "None" : "",
                    options.Select((o, i) => new TrackChoice(i, o.Name, false, new RelayCommand(() => _ = CreateAsync(source, o.Id, o.Name), () => !isCreating))).ToList()));
            }
            rows.Add(new EditNoteRow("A show plays its episodes in order; a genre, network or collection plays every movie and show in it. A playlist plays what you put in it. Each is shared with everyone on your server."));
        }
        if (playlists.Count == 0) rows.Add(new EditNoteRow("No video playlists yet. Make one in Jellyfin, or with Add to Playlist on any movie."));
        var numbers = Numbers;
        foreach (var (playlist, tags) in Channels)
        {
            rows.Add(new EditSectionRow($"CHANNEL {numbers.GetValueOrDefault(playlist.Id)}"));
            var open = expanded.Contains(playlist.Id);
            rows.Add(new EditActionRow(tags.Name ?? playlist.DisplayTitle, Summary(playlist, tags), null, open ? Up : Down, new RelayCommand(() =>
            {
                if (!expanded.Remove(playlist.Id)) expanded.Add(playlist.Id);
                Rebuild();
            })));
            if (open) ChannelRows(rows, playlist, tags);
        }
        if (Clashes.ToList() is { Count: > 0 } clashes) rows.Add(new EditNoteRow(string.Join(" ", clashes) + " Give each its own number."));
        if (Others.Count > 0)
        {
            rows.Add(new EditSectionRow(Channels.Count == 0 ? "VIDEO PLAYLISTS" : "MORE PLAYLISTS"));
            foreach (var playlist in Others)
                rows.Add(new EditToggleRow(playlist.DisplayTitle, Count(playlist), false, new RelayCommand(() => TurnOn(playlist), () => CanEdit)));
        }
        rows.Add(new EditSectionRow(""));
        rows.Add(new EditActionRow("How Channels Work", null, null, showsHelp ? Up : Down, new RelayCommand(() =>
        {
            showsHelp = !showsHelp;
            Rebuild();
        })));
        if (showsHelp) foreach (var note in HelpNotes) rows.Add(new EditNoteRow(note));
    }

    /// <summary>An open channel's settings: the essentials, then what it plays and the rest folded away until asked for.</summary>
    private void ChannelRows(List<EditRow> rows, BaseItem playlist, ChannelTags tags)
    {
        var numbers = Numbers;
        rows.Add(new EditToggleRow(playlist.DisplayTitle, Count(playlist), true,
            new RelayCommand(() => Update(playlist, t => t with { IsChannel = false }), () => CanEdit)));
        rows.Add(new EditTextRow("Channel Name", playlist.DisplayTitle, text =>
        {
            var trimmed = text.Trim();
            var name = trimmed.Length == 0 || trimmed == playlist.DisplayTitle ? null : trimmed;
            if (name != tags.Name) Update(playlist, t => t with { Name = name });
        }) { Text = tags.Name ?? "" });
        var groups = Channels.Select(c => c.Tags.Group).OfType<string>().Append(tags.Group).OfType<string>().Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        rows.Add(Choice("Group", tags.Group ?? "None", [(null, "None"), .. groups.Select(g => ((string?)g, g))], tags.Group, g => Update(playlist, t => t with { Group = g })));
        rows.Add(new EditTextRow("New Group", "Name a new group…", text =>
        {
            if (text.Trim() is { Length: > 0 } group) Update(playlist, t => t with { Group = group });
        }));
        var current = tags.Number ?? numbers.GetValueOrDefault(playlist.Id, 1);
        rows.Add(Choice("Number", current.ToString(), Enumerable.Range(1, Math.Max(99, current)).Select(n => (n, n.ToString())), current,
            n => Update(playlist, t => t with { Number = n })));

        Fold(rows, "What It Plays", PlaysSummary(playlist, tags), "plays", playlist);
        if (folds.Contains("plays|" + playlist.Id))
        {
            rows.Add(Multi("Genres", genreNames, tags.Genres, "None", g => Update(playlist, t => t with { Genres = g })));
            rows.Add(Multi("Networks", networkNames, tags.Networks, "None", n => Update(playlist, t => t with { Networks = n })));
            rows.Add(Multi("Collections", collectionNames, tags.Collections, "None", c => Update(playlist, t => t with { Collections = c })));
            if (tags.HasSources)
            {
                rows.Add(Choice("Content", tags.Content.Title, Enum.GetValues<ChannelContent>().Select(c => (c, c.Title)), tags.Content,
                    c => Update(playlist, t => t with { Content = c })));
                if (tags.Genres.Count > 0 || tags.Networks.Count > 0)
                    rows.Add(Multi("Libraries", libraryNames, tags.Libraries, "All", l => Update(playlist, t => t with { Libraries = l })));
                var years = YearChoices.Contains(tags.Years) ? YearChoices : [.. YearChoices, tags.Years];
                rows.Add(Choice("Years", YearsTitle(tags.Years), years.Select(y => (y, YearsTitle(y))), tags.Years, y => Update(playlist, t => t with { Years = y })));
                rows.Add(Multi("Ratings", ratingNames, tags.Ratings, "Any", r => Update(playlist, t => t with { Ratings = r })));
            }
        }

        rows.Add(Choice("Mode", tags.Mode.Title, Enum.GetValues<ChannelMode>().Select(m => (m, m.Title)), tags.Mode, m => Update(playlist, t => t with { Mode = m })));
        rows.Add(new EditToggleRow("Playlist Picture as Logo", null, tags.ShowsLogo, new RelayCommand(() => Update(playlist, t => t with { ShowsLogo = !t.ShowsLogo }), () => CanEdit)));
        rows.Add(new EditToggleRow("Logo in the Corner While Playing", null, tags.ShowsBug, new RelayCommand(() => Update(playlist, t => t with { ShowsBug = !t.ShowsBug }), () => CanEdit)));
        // Breaks: the filler playlists a channel's breaks play from; none is off. Turning
        // breaks on lines the channel up on the half hour too, best effort, as a channel with breaks usually wants.
        var fillers = FillerNames.Concat(tags.FillerPlaylists.Where(f => !FillerNames.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
        if (fillers.Count > 0)
            rows.Add(Multi("Breaks", fillers, tags.FillerPlaylists, "Off", names => Update(playlist, t =>
            {
                var turningOn = t.FillerPlaylists.Count == 0 && names.Count > 0;
                var changed = t with { FillerPlaylists = names };
                return turningOn && t.Breaks.Align == 0 ? changed with { Breaks = changed.Breaks with { Align = 30, ExactAlign = false } } : changed;
            })));

        Fold(rows, "Advanced", null, "advanced", playlist);
        if (!folds.Contains("advanced|" + playlist.Id)) return;
        if (tags.Mode == ChannelMode.Blocks)
        {
            var blocks = BlockChoices.Contains(tags.Blocks) ? BlockChoices : [.. BlockChoices, tags.Blocks];
            rows.Add(Choice("Episodes in a Row", BlocksTitle(tags.Blocks), blocks.Select(b => (b, BlocksTitle(b))), tags.Blocks, b => Update(playlist, t => t with { Blocks = b })));
        }
        if (tags.Mode.Reshuffles)
        {
            rows.Add(Choice("Reshuffle", tags.Reshuffle.Title, Enum.GetValues<ChannelReshuffle>().Select(r => (r, r.Title)), tags.Reshuffle,
                r => Update(playlist, t => t with { Reshuffle = r })));
            rows.Add(new EditActionRow("Shuffle Again", "Deals a different order", null, "", new RelayCommand(() => Update(playlist, t => t with { Seed = t.Seed + 1 }), () => CanEdit)));
        }
        rows.Add(Choice("Shortest", LengthTitle(tags.MinLength), Lengths(tags.MinLength).Select(l => (l, LengthTitle(l))), tags.MinLength, l => Update(playlist, t => t with { MinLength = l })));
        rows.Add(Choice("Longest", LengthTitle(tags.MaxLength), Lengths(tags.MaxLength).Select(l => (l, LengthTitle(l))), tags.MaxLength, l => Update(playlist, t => t with { MaxLength = l })));
        if (tags.ShowsBug)
        {
            rows.Add(Choice("Corner", tags.BugCorner.Title, Enum.GetValues<BugCorner>().Select(c => (c, c.Title)), tags.BugCorner, c => Update(playlist, t => t with { BugCorner = c })));
            rows.Add(Choice("Hide After", HideTitle(tags.BugHideAfter), With(BugHideChoices, tags.BugHideAfter).Select(s => (s, HideTitle(s))), tags.BugHideAfter,
                s => Update(playlist, t => t with { BugHideAfter = s })));
            rows.Add(Choice("Opacity", $"{tags.BugOpacity}%", With(BugOpacityChoices, tags.BugOpacity).Select(o => (o, $"{o}%")), tags.BugOpacity,
                o => Update(playlist, t => t with { BugOpacity = o })));
            rows.Add(Choice("Logo Size", $"{tags.BugSize}%", With(BugSizeChoices, tags.BugSize).Select(s => (s, $"{s}%")), tags.BugSize,
                s => Update(playlist, t => t with { BugSize = s })));
        }
        if (tags.FillerPlaylists.Count > 0)
        {
            var kinds = Enum.GetValues<BreakKind>();
            rows.Add(Multi("Filler Types", kinds.Select(k => k.Title).ToList(), kinds.Where(tags.FillerTypes.Contains).Select(k => k.Title).ToList(), "All", titles =>
            {
                var chosen = kinds.Where(k => titles.Contains(k.Title)).ToHashSet();
                Update(playlist, t => t with { FillerTypes = chosen.Count == 0 ? kinds.ToHashSet() : chosen });
            }));
        }
        if (tags.HasBreaks)
        {
            var breaks = tags.Breaks;
            ChannelTags Breaks(ChannelTags t, Func<ChannelBreaks, ChannelBreaks> change) => t with { Breaks = change(t.Breaks) };
            if (tags.FillerTypes.Contains(BreakKind.Bumpers))
                rows.Add(Choice("Bumpers", breaks.BumpersAt.Title, Enum.GetValues<BumperPlacement>().Select(b => (b, b.Title)), breaks.BumpersAt,
                    b => Update(playlist, t => Breaks(t, x => x with { BumpersAt = b }))));
            if (tags.FillerTypes.Contains(BreakKind.Commercials))
                rows.Add(Choice("Commercial Time", breaks.Minutes == 0 ? "None" : LengthTitle(breaks.Minutes),
                    With(CommercialChoices, breaks.Minutes).Select(m => (m, m == 0 ? "None" : LengthTitle(m))), breaks.Minutes,
                    m => Update(playlist, t => Breaks(t, x => x with { Minutes = m }))));
            var counts = Enumerable.Range(0, ChannelTags.MostPromos + 1).ToList();
            if (tags.FillerTypes.Contains(BreakKind.Promos))
                rows.Add(Choice("Promos per Break", breaks.PromoCount.ToString(), counts.Select(c => (c, c.ToString())), breaks.PromoCount,
                    c => Update(playlist, t => Breaks(t, x => x with { PromoCount = c }))));
            if (tags.FillerTypes.Contains(BreakKind.Trailers))
                rows.Add(Choice("Trailers per Break", breaks.TrailerCount.ToString(), counts.Select(c => (c, c.ToString())), breaks.TrailerCount,
                    c => Update(playlist, t => Breaks(t, x => x with { TrailerCount = c }))));
            rows.Add(Choice("Line Up", breaks.Align == 0 ? "Off" : AlignTitle(breaks.Align), ChannelTags.Alignments.Prepend(0).Select(a => (a, a == 0 ? "Off" : AlignTitle(a))), breaks.Align,
                a => Update(playlist, t => Breaks(t, x => x with { Align = a }))));
            if (breaks.Align > 0)
                rows.Add(Choice("Fit", breaks.ExactAlign ? "Exact" : "Best Effort", [(false, "Best Effort"), (true, "Exact")], breaks.ExactAlign,
                    e => Update(playlist, t => Breaks(t, x => x with { ExactAlign = e }))));
            else
            {
                if (tags.FillerTypes.Contains(BreakKind.Interstitials))
                    rows.Add(Choice("Interstitials per Break", breaks.InterstitialCount.ToString(), counts.Select(c => (c, c.ToString())), breaks.InterstitialCount,
                        c => Update(playlist, t => Breaks(t, x => x with { InterstitialCount = c }))));
                rows.Add(Choice("Break After", EveryTitle(breaks.Every), Enumerable.Range(1, ChannelTags.LongestBreakGap).Select(e => (e, EveryTitle(e))), breaks.Every,
                    e => Update(playlist, t => Breaks(t, x => x with { Every = e }))));
            }
        }
        var users = userNames.Concat(tags.Users.Where(u => !userNames.Contains(u, StringComparer.OrdinalIgnoreCase))).ToList();
        if (users.Count > 0) rows.Add(Multi("Users", users, tags.Users, "Everyone", u => Update(playlist, t => t with { Users = u })));
        rows.Add(new EditNoteRow("Every setting is a tag on the playlist in Jellyfin, so you can change it there too: see How Channels Work."));
    }

    /// <summary>A row that opens or closes part of a channel's settings in place, with what's set there when closed.</summary>
    private void Fold(List<EditRow> rows, string title, string? summary, string key, BaseItem playlist)
    {
        var id = key + "|" + playlist.Id;
        var open = folds.Contains(id);
        rows.Add(new EditActionRow(title, null, open ? null : summary, open ? Up : Down, new RelayCommand(() =>
        {
            if (!folds.Remove(id)) folds.Add(id);
            Rebuild();
        })));
    }

    /// <summary>One of several, the one chosen ticked.</summary>
    private EditChoiceRow Choice<T>(string title, string value, IEnumerable<(T Value, string Title)> options, T current, Action<T> pick) =>
        new(title, value, options.Select((o, i) => new TrackChoice(i, o.Title, EqualityComparer<T>.Default.Equals(o.Value, current),
            new RelayCommand(() => pick(o.Value), () => CanEdit))).ToList());

    /// <summary>Any of several, each click adding or taking away one; <paramref name="none"/> when nothing is chosen.</summary>
    private EditChoiceRow Multi(string title, IReadOnlyList<string> options, IReadOnlyList<string> chosen, string none, Action<IReadOnlyList<string>> pick)
    {
        var all = options.Concat(chosen.Where(c => !options.Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
        bool Has(string name) => chosen.Contains(name, StringComparer.OrdinalIgnoreCase);
        var value = chosen.Count == 0 ? none : chosen.Count <= 2 ? string.Join(", ", chosen) : $"{chosen[0]} and {chosen.Count - 1} more";
        return new(title, value, all.Select((name, i) => new TrackChoice(i, name, Has(name), new RelayCommand(() =>
            pick(Has(name) ? chosen.Where(c => !string.Equals(c, name, StringComparison.OrdinalIgnoreCase)).ToList() : [.. chosen, name]), () => CanEdit))).ToList());
    }

    private IReadOnlyList<(string Id, string Name)> Options(NewChannelSource source) => source switch
    {
        NewChannelSource.Show => seriesOptions,
        NewChannelSource.Genre => genreNames.Select(n => (n, n)).ToList(),
        NewChannelSource.Network => networkNames.Select(n => (n, n)).ToList(),
        NewChannelSource.Collection => collectionNames.Select(n => (n, n)).ToList(),
        _ => Others.Select(p => (p.Id, p.DisplayTitle)).ToList(),
    };

    private static string SourceTitle(NewChannelSource source) => source switch
    {
        NewChannelSource.Show => "From a Show",
        NewChannelSource.Genre => "From a Genre",
        NewChannelSource.Network => "From a Network",
        NewChannelSource.Collection => "From a Collection",
        _ => "From One of Your Playlists",
    };

    private static string? Count(BaseItem playlist) => playlist.ChildCount is { } count ? count == 1 ? "1 title" : $"{count} titles" : null;

    /// <summary>"Horror, Thriller · Blocks · Weekly · 120 titles · Logo · Bug" for a closed channel.</summary>
    private static string Summary(BaseItem playlist, ChannelTags tags)
    {
        var parts = new List<string> { tags.Mode.Title };
        var sources = tags.Genres.Concat(tags.Networks).Concat(tags.Collections).ToList();
        if (sources.Count > 0) parts.Insert(0, string.Join(", ", sources));
        if (tags.Mode.Reshuffles && tags.Reshuffle != ChannelReshuffle.Day) parts.Add(tags.Reshuffle.Title);
        if (Count(playlist) is { } count) parts.Add(count);
        if (tags.ShowsLogo) parts.Add("Logo");
        if (tags.ShowsBug) parts.Add("Bug");
        if (tags.Users.Count > 0) parts.Add("Only " + JoinAnd(tags.Users));
        if (tags.HasBreaks) parts.Add(tags.Breaks.Align > 0 ? $"Breaks, {AlignTitle(tags.Breaks.Align).ToLowerInvariant()}" : "Breaks");
        return string.Join(" · ", parts);
    }

    /// <summary>"Horror, Thriller · Movies", or the playlist's own titles.</summary>
    private static string PlaysSummary(BaseItem playlist, ChannelTags tags)
    {
        var sources = tags.Genres.Concat(tags.Networks).Concat(tags.Collections).ToList();
        if (sources.Count == 0) return Count(playlist) ?? "Its playlist";
        return tags.Content == ChannelContent.All ? string.Join(", ", sources) : $"{string.Join(", ", sources)} · {tags.Content.Title}";
    }

    private static IEnumerable<int?> Lengths(int? current) => (LengthChoices.Contains(current) ? LengthChoices : [.. LengthChoices, current]).OrderBy(l => l ?? 0);

    private static IEnumerable<int> With(int[] choices, int current) => (choices.Contains(current) ? choices : [.. choices, current]).Order();

    private static string AlignTitle(int minutes) => minutes switch
    {
        30 => "On the Half Hour",
        60 => "On the Hour",
        _ => $"Every {minutes} Minutes",
    };

    private static string YearsTitle((int Low, int High)? years) => years switch
    {
        null => "Any Year",
        var (low, high) when low == high => $"{low}",
        var (low, high) when low % 10 == 0 && high - low == 9 => $"{low}s",
        var (low, high) => $"{low}–{high}",
    };

    private static string LengthTitle(int? minutes) => minutes switch
    {
        null => "Any Length",
        >= 60 when minutes % 60 == 0 => $"{minutes / 60} hr",
        >= 60 => $"{minutes / 60} hr {minutes % 60} min",
        _ => $"{minutes} min",
    };

    private static string BlocksTitle((int Low, int High) blocks) => blocks.Low == blocks.High ? $"{blocks.Low}" : $"{blocks.Low} to {blocks.High}";
    private static string HideTitle(int seconds) => seconds == 0 ? "Never" : seconds < 60 ? $"{seconds} sec" : $"{seconds / 60} min";
    private static string EveryTitle(int every) => every == 1 ? "Every Programme" : $"Every {every} Programmes";

    private static readonly string[] HelpNotes =
    [
        "Rotation plays one episode of each show in turn, and Blocks a few in a row; either way a show's episodes come in order and pick up where they left off, each movie takes a turn of its own, and specials are left out. Shuffle plays everything in its own order. These three deal a new order every day, week or month, as Reshuffle says, and Shuffle Again deals a different one. In Order plays the playlist as it's arranged, round and round.",
        "Channels are saved as tags on the playlists in Jellyfin, so every device finds them. You can set them there too, in the playlist's Edit Metadata ▸ Tags: aster:channel, aster:number=3, aster:mode=blocks (or rotation, shuffle, sequential, airdate), aster:reshuffle=week (or day, month), aster:blocks=3-6, aster:seed=2, aster:logo=on, aster:bug=on (with aster:bug-location=top-left, top-right, bottom-left or bottom-right, aster:bug-size=150%, aster:bug-hide=30 and aster:bug-opacity=60%), aster:channel-name=Your Name, aster:group=Kids (channels go in the guide by group, groups in order of their lowest number), aster:min-length=20 and aster:max-length=60 (in minutes), aster:weight=Friends:2 (a show taking two turns to everyone else's one), and to fill a channel aster:genre=Horror, aster:network=NBC and aster:collection=Star Wars (once each), narrowed by aster:type=movies (or shows, all), aster:years=1980-1989, aster:rating=TV-PG,PG and aster:library=TV Shows (once per library, for genres and networks).",
        "A channel with genres, networks or collections plays every movie and every episode of every show in them (any of them), as well as anything in its playlist. Air Date plays them all in the order they first aired.",
        "A channel with filler playlists has a break after its programmes: a bumper, commercials, promos, trailers, interstitials, and a bumper back. Put the clips in Jellyfin as videos and tag each one with what it is: aster:commercial, aster:promo, aster:trailer, aster:interstitial or aster:bumper (aster:bumper=in only closes a break, right before the show, and aster:bumper=out only opens one); a clip without one isn't played. Put them in playlists tagged aster:filler, and pick those for a channel here, or name them with aster:filler-playlists=Its Name. aster:filler-types=commercials,bumpers keeps to some kinds; aster:bumpers-at=both (or out, in), aster:break-length=2 (minutes of commercials), aster:promo-count=1, aster:trailer-count=1, aster:interstitial-count=1 and aster:break-every=1 shape each break. With aster:align=30, programmes line up on the hour and half hour: Best Effort (aster:align-fit=best) fills each break with whole clips, as near the mark as they get, so a programme can start a few seconds early; Exact (aster:align-fit=exact) fills with interstitials and cuts the last one short to land right on it.",
        "Other people on your server see a channel only if its playlist is shared with them, or public. Users (aster:user=peppy,jd) keeps a channel to certain people in Aster's Channels tab; it still lists every channel here, and doesn't hide the playlist in Jellyfin.",
    ];
}
