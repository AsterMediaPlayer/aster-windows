using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;

namespace Aster.Core.Channels;

/// <summary>
/// A playlist as a channel: its number and name, the movies and episodes it
/// plays, how its shows take turns, its breaks, and its logo and bug. A port
/// of the Apple app's PlaylistChannel.
/// </summary>
public sealed record PlaylistChannel
{
    public required BaseItem Playlist { get; init; }
    public required int Number { get; init; }
    public required IReadOnlyList<BaseItem> Items { get; init; }
    public ChannelLineup Lineup { get; init; } = new();

    /// <summary>The guide shows the playlist's picture as the channel's logo.</summary>
    public bool ShowsLogo { get; init; }

    /// <summary>The player shows the logo in a corner, as a network's bug.</summary>
    public bool ShowsBug { get; init; }
    public BugCorner BugCorner { get; init; } = BugCorner.BottomRight;
    public int BugSize { get; init; } = 100;
    public int BugHideAfter { get; init; }
    public int BugOpacity { get; init; } = ChannelTags.DefaultBugOpacity;

    /// <summary>From channel-name=; null for the playlist's own name.</summary>
    public string? CustomName { get; init; }

    /// <summary>From group=; null for none.</summary>
    public string? Group { get; init; }

    public ChannelBreakClips BreakClips { get; init; } = ChannelBreakClips.None;

    /// <summary>The series' clear logo, when the playlist is all one series' episodes.</summary>
    public (string ItemId, string Tag)? SeriesLogo { get; init; }

    public string Id => Playlist.Id;
    public string Name => CustomName ?? Playlist.DisplayTitle;

    /// <summary>The picture the channel's logo and bug show: its one series' clear logo, else the playlist's picture.</summary>
    public Uri? LogoUrl(IMediaServer client, int width)
    {
        if (SeriesLogo is var (itemId, tag)) return client.ImageUrl(itemId, ImageKind.Logo, tag, maxWidth: width);
        return Playlist.ImageTags?.GetValueOrDefault("Primary") is { } primary ? client.ImageUrl(Playlist.Id, ImageKind.Primary, primary, maxWidth: width) : null;
    }

    /// <summary>The clear logo of the one series the items are all episodes of; null otherwise.</summary>
    public static (string, string)? SeriesLogoOf(IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 0 || items[0].SeriesId is not { } seriesId) return null;
        if (!items.All(i => i.Kind == ItemKind.Episode && i.SeriesId == seriesId)) return null;
        var logoItem = items.Select(i => i.ParentLogoItemId).OfType<string>().FirstOrDefault();
        var tag = items.Select(i => i.ParentLogoImageTag).OfType<string>().FirstOrDefault();
        return logoItem is not null && tag is not null ? (logoItem, tag) : null;
    }

    /// <summary>The programmes, each with the break after it, as the guide shows them.</summary>
    public IReadOnlyList<ScheduledProgram> Programs(DateTimeOffset from, DateTimeOffset to) =>
        ChannelSchedule.Programs(Id, Items, Lineup, BreakClips, from, to);

    /// <summary>Everything the channel plays, programmes and break clips alike.</summary>
    public IReadOnlyList<ScheduledProgram> Segments(DateTimeOffset from, DateTimeOffset to) =>
        ChannelSchedule.Segments(Id, Items, Lineup, BreakClips, from, to);

    /// <summary>The programme on now, its break included.</summary>
    public ScheduledProgram? Airing(DateTimeOffset date) => Programs(date, date.AddSeconds(1)).FirstOrDefault();

    /// <summary>What's playing now, programme or break clip.</summary>
    public ScheduledProgram? Segment(DateTimeOffset date) => Segments(date, date.AddSeconds(1)).FirstOrDefault();
}

/// <summary>
/// What's on a channel now, as something to play. The Channels tab registers
/// the channels it loads, and the player asks here what's next when a movie
/// or episode ends, so a channel stays with its schedule.
/// </summary>
public static class ChannelTuner
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, PlaylistChannel> channels = [];
    private static List<string> order = [];
    private static ChannelGrouping? grouping;

    /// <summary>Seconds before a programme's end that tuning in starts the next one.</summary>
    public const double TooLateToJoin = 60;

    /// <summary>Takes the full list the Channels tab loaded, in order, and their groups when they have any.</summary>
    public static void Register(IReadOnlyList<PlaylistChannel> loaded, ChannelGrouping? groups = null)
    {
        lock (Gate)
        {
            channels = new Dictionary<string, PlaylistChannel>(channels);
            foreach (var channel in loaded) channels[channel.Id] = channel;
            order = loaded.Select(c => c.Id).ToList();
            grouping = groups;
        }
    }

    /// <summary>The channels' groups, when they have any.</summary>
    public static ChannelGrouping? Grouping
    {
        get
        {
            lock (Gate) return grouping;
        }
    }

    /// <summary>The channels in number order (group by group, once they have groups), for flicking through them: on from one group into the next.</summary>
    public static IReadOnlyList<PlaylistChannel> OrderedChannels
    {
        get
        {
            lock (Gate) return order.Select(id => channels.GetValueOrDefault(id)).OfType<PlaylistChannel>().ToList();
        }
    }

    public static PlaylistChannel? Channel(string id)
    {
        lock (Gate) return channels.GetValueOrDefault(id);
    }

    /// <summary>
    /// What's on <paramref name="channelId"/> at <paramref name="date"/>, from
    /// as far in as it's got. Just after <paramref name="finishedItemId"/>
    /// ends, a viewer who paused is a little behind the schedule and it may
    /// still be "on"; what follows it then plays from the start.
    /// </summary>
    public static (BaseItem Item, PlaybackQueueItem Entry)? Live(string channelId, DateTimeOffset? date = null, string? finishedItemId = null)
    {
        var at = date ?? DateTimeOffset.UtcNow;
        if (Channel(channelId) is not { } channel || channel.Segment(at) is not { } program) return null;
        var offset = program.Offset(at);
        // Joining with under a minute left (a quarter of a short clip) starts
        // what's next instead: the last seconds can fall after the file's
        // final keyframe, with nothing to start the picture from.
        var almostOver = program.EndSeconds - ScheduledProgram.ToSeconds(at) < Math.Min(TooLateToJoin, program.Duration / 4);
        if ((almostOver || (finishedItemId is not null && program.Item.Id == finishedItemId))
            && channel.Segments(program.End, program.End.AddSeconds(1)).FirstOrDefault() is { } next)
        {
            program = next;
            offset = 0;
        }
        var entry = new PlaybackQueueItem { ItemId = program.Item.Id, StartTicks = (long)(offset * 10_000_000), ChannelId = channelId };
        // A clip cut short to land on the half hour stops where the schedule says.
        var length = (program.Item.RunTimeTicks ?? 0) / 10_000_000.0;
        if (program.Role.IsBreak && program.Duration < length - 0.5) entry = entry with { ChannelCutTicks = (long)(program.Duration * 10_000_000) };
        return (program.Item, entry);
    }
}

/// <summary>
/// Finds the playlists tagged as channels and loads what they play: their
/// own movies and episodes, and what their genres, networks and collections
/// hold. Playlists whose contents haven't changed aren't fetched again. A
/// port of the Apple app's ChannelsModel.
/// </summary>
public sealed class ChannelLoader
{
    private readonly Dictionary<string, (int? Count, IReadOnlyList<BaseItem> Items)> itemCache = [];
    private readonly Dictionary<string, (string Query, IReadOnlyList<string> TitleIds, IReadOnlyList<BaseItem> Items)> sourceCache = [];
    private Dictionary<string, IReadOnlyList<BaseItem>> episodeCache = [];

    /// <summary>
    /// The channels <paramref name="userName"/> gets (aster:user=), in
    /// channel-number order (group by group, once they have groups: see
    /// <see cref="ChannelTuner.Grouping"/>); a tagged playlist with nothing
    /// to play is left out, and so is one with anything on it the user's
    /// parental controls hold back (see <see cref="IsHeldBack"/>).
    /// </summary>
    public async Task<IReadOnlyList<PlaylistChannel>> LoadAsync(IChannelServer client, string? userName = null, bool force = false)
    {
        // Read each time, so an administrator's change counts on the next visit.
        var policy = (await client.ChannelViewerAsync().ConfigureAwait(false)).Policy;
        var playlists = await client.PlaylistsAsync("Video").ConfigureAwait(false);
        var tagged = TaggedChannels(playlists).Where(t => userName is null || t.Tags.IsShownTo(userName)).ToList();
        var numbers = ChannelTags.Numbers(tagged.Select(t => (t.Playlist.Id, t.Tags.Name ?? t.Playlist.DisplayTitle, t.Tags.Number)));
        // The playlists of break clips the channels name, fetched with the channels' own.
        var fillers = tagged.ToDictionary(t => t.Playlist.Id, t => FillerPlaylists(t.Tags, playlists));
        var wanted = tagged.Select(t => t.Playlist).ToList();
        foreach (var playlist in fillers.Values.SelectMany(f => f))
        {
            if (wanted.All(w => w.Id != playlist.Id)) wanted.Add(playlist);
        }
        var stale = wanted.Where(p => force || !itemCache.TryGetValue(p.Id, out var cached) || cached.Count != p.ChildCount).ToList();
        var fetched = await Task.WhenAll(stale.Select(async p =>
        {
            try { return (p, Items: await client.PlaylistItemsAsync(p.Id).ConfigureAwait(false)); }
            catch (Exception error) when (IsServerError(error)) { return (p, Items: (IReadOnlyList<BaseItem>?)null); }
        })).ConfigureAwait(false);
        foreach (var (playlist, items) in fetched)
        {
            if (items is not null) itemCache[playlist.Id] = (playlist.ChildCount, items);
        }
        // Genres, networks and collections as sources are Jellyfin's and Emby's for now.
        IReadOnlyList<ParentalRating> ratings = [];
        if (client is JellyfinClient jellyfin)
        {
            await LoadSourcesAsync(tagged, jellyfin, force).ConfigureAwait(false);
            // The ratings' ranks, only needed to clear a restricted user's genre, network or collection channel.
            if (policy is { RestrictsContent: true } && tagged.Any(t => t.Tags.HasSources && t.Tags.Ratings.Count > 0))
                ratings = await jellyfin.ParentalRatingsAsync().ConfigureAwait(false);
        }

        var built = new List<PlaylistChannel>();
        foreach (var (playlist, tags) in tagged)
        {
            if (policy is not null && IsHeldBack(playlist, tags, itemCache.TryGetValue(playlist.Id, out var cached) ? cached.Items.Count : null, policy, ratings)) continue;
            var own = itemCache.GetValueOrDefault(playlist.Id).Items ?? [];
            var fromSources = tags.HasSources ? sourceCache.GetValueOrDefault(playlist.Id).Items ?? [] : [];
            var items = Merged(own, fromSources).Where(i => i.Kind is ItemKind.Movie or ItemKind.Episode).ToList();
            if (items.Count == 0 || !numbers.TryGetValue(playlist.Id, out var number)) continue;
            var clips = tags.HasBreaks
                ? BreakClips(fillers[playlist.Id].SelectMany(f => itemCache.GetValueOrDefault(f.Id).Items ?? []), tags.FillerTypes)
                : ChannelBreakClips.None;
            built.Add(new PlaylistChannel
            {
                Playlist = playlist, Number = number, Items = items, Lineup = tags.Lineup, ShowsLogo = tags.ShowsLogo,
                ShowsBug = tags.ShowsBug, BugCorner = tags.BugCorner, BugSize = tags.BugSize, BugHideAfter = tags.BugHideAfter,
                BugOpacity = tags.BugOpacity, CustomName = tags.Name, Group = tags.Group, BreakClips = clips,
                SeriesLogo = PlaylistChannel.SeriesLogoOf(items),
            });
        }
        var byNumber = built.OrderBy(c => c.Number).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        // By group once channels have them: each in order of its lowest number.
        var grouped = ChannelGrouping.Make(byNumber, c => c.Id, c => c.Group);
        var ordered = grouped?.Ordered ?? byNumber;
        ChannelTuner.Register(ordered, grouped?.Grouping);
        return ordered;
    }

    /// <summary>
    /// What each channel's genres, networks and collections hold: their
    /// movies and series, narrowed by its years, ratings, type and
    /// libraries; a series brings all its episodes. A channel whose titles
    /// haven't changed keeps what it had.
    /// </summary>
    private async Task LoadSourcesAsync(List<(BaseItem Playlist, ChannelTags Tags)> tagged, JellyfinClient client, bool force)
    {
        var sourceChannels = tagged.Where(t => t.Tags.HasSources).ToList();
        if (sourceChannels.Count == 0) return;
        if (force) episodeCache = [];
        var genreList = sourceChannels.Any(c => c.Tags.Genres.Count > 0) ? OrEmpty(client.VideoGenresAsync()) : Task.FromResult<IReadOnlyList<BaseItem>>([]);
        var studioList = sourceChannels.Any(c => c.Tags.Networks.Count > 0) ? OrEmpty(client.VideoStudiosAsync()) : Task.FromResult<IReadOnlyList<BaseItem>>([]);
        var collectionList = sourceChannels.Any(c => c.Tags.Collections.Count > 0) ? OrEmpty(client.CollectionsAsync()) : Task.FromResult<IReadOnlyList<BaseItem>>([]);
        var libraryList = OrEmpty(client.UserViewsAsync());
        var genreIds = IdsByName(await genreList.ConfigureAwait(false));
        var studioIds = IdsByName(await studioList.ConfigureAwait(false));
        var collectionIds = IdsByName(await collectionList.ConfigureAwait(false));
        var libraryIds = IdsByName(await libraryList.ConfigureAwait(false));

        // First the titles of every channel, which is quick.
        var titles = await Task.WhenAll(sourceChannels.Select(async entry =>
        {
            var tags = entry.Tags;
            var genres = tags.Genres.Select(g => genreIds.GetValueOrDefault(g.ToLowerInvariant())).OfType<string>().ToList();
            var studios = tags.Networks.Select(n => studioIds.GetValueOrDefault(n.ToLowerInvariant())).OfType<string>().ToList();
            var collections = tags.Collections.Select(c => collectionIds.GetValueOrDefault(c.ToLowerInvariant())).OfType<string>().ToList();
            // Libraries the server doesn't know are ignored; none left means all.
            var chosen = tags.Libraries.Select(l => libraryIds.GetValueOrDefault(l.ToLowerInvariant())).OfType<string>().ToList();
            IReadOnlyList<string?> parents = chosen.Count == 0 ? [null] : chosen;
            var types = tags.Content.ItemTypes;
            IReadOnlyList<BaseItem> found = [];
            try
            {
                foreach (var parent in parents)
                {
                    if (genres.Count > 0) found = Merged(found, await client.SourceTitlesAsync(types, parent, tags.Years, tags.Ratings, genreIds: genres).ConfigureAwait(false));
                    if (studios.Count > 0) found = Merged(found, await client.SourceTitlesAsync(types, parent, tags.Years, tags.Ratings, studioIds: studios).ConfigureAwait(false));
                }
                // A collection is filtered here, as Jellyfin's filters don't reach into it.
                foreach (var collection in collections)
                {
                    var held = await client.CollectionTitlesAsync(collection).ConfigureAwait(false);
                    found = Merged(found, held.Where(t => Passes(t, tags)).ToList());
                }
            }
            catch (Exception error) when (IsServerError(error))
            {
                return (entry.Playlist.Id, Found: (IReadOnlyList<BaseItem>?)null);
            }
            return (entry.Playlist.Id, Found: (IReadOnlyList<BaseItem>?)found);
        })).ConfigureAwait(false);
        var titleMap = titles.Where(t => t.Found is not null).ToDictionary(t => t.Id, t => t.Found!);

        // Then the episodes of each series not already known, a few at a time.
        var missing = titleMap.Values.SelectMany(f => f).Where(t => t.Kind == ItemKind.Series).Select(t => t.Id).Distinct()
            .Where(id => !episodeCache.ContainsKey(id)).ToList();
        var fetched = new Dictionary<string, IReadOnlyList<BaseItem>>();
        using (var gate = new SemaphoreSlim(6))
        {
            await Task.WhenAll(missing.Select(async id =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var episodes = await client.SeriesEpisodesAsync(id).ConfigureAwait(false);
                    lock (fetched) fetched[id] = episodes;
                }
                catch (Exception error) when (IsServerError(error)) { }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        }
        foreach (var (id, episodes) in fetched) episodeCache[id] = episodes;

        foreach (var (playlist, tags) in sourceChannels)
        {
            // A fetch that failed keeps what was there.
            if (!titleMap.TryGetValue(playlist.Id, out var found)) continue;
            var query = string.Join("/", new[]
            {
                string.Join("|", tags.Genres), string.Join("|", tags.Networks), string.Join("|", tags.Collections),
                string.Join("|", tags.Libraries), string.Join("|", tags.Ratings),
                tags.Content.ItemTypes + "|" + (tags.Years is var (low, high) ? $"{low}-{high}" : ""),
            });
            var ids = found.Select(t => t.Id).ToList();
            if (sourceCache.TryGetValue(playlist.Id, out var previous) && previous.Query == query && previous.TitleIds.SequenceEqual(ids)
                && !found.Any(t => t.Kind == ItemKind.Series && fetched.ContainsKey(t.Id))) continue;
            var items = found.SelectMany(t => t.Kind == ItemKind.Series ? episodeCache.GetValueOrDefault(t.Id) ?? [] : [t]).ToList();
            sourceCache[playlist.Id] = (query, ids, items);
        }
    }

    /// <summary>Whether a title from a collection fits a channel's years, ratings and type.</summary>
    /// <summary>
    /// Whether a channel has anything on it the user's parental controls hold
    /// back, so it's left out rather than shown with gaps. The server leaves
    /// those titles out of what it sends this user, so a playlist lists more
    /// titles than came back (or none came back). Genres, networks and
    /// collections can't be counted that way, so a restricted user gets such a
    /// channel only when its own rating= tags keep it within their rating cap
    /// and nothing else is restricted. A user without restrictions gets every
    /// channel, whatever the counts.
    /// </summary>
    /// <param name="fetched">How many of the playlist's titles came back; null when it couldn't be fetched.</param>
    public static bool IsHeldBack(BaseItem playlist, ChannelTags tags, int? fetched, UserPolicy policy, IReadOnlyList<ParentalRating> ratings)
    {
        if (!policy.RestrictsContent) return false;
        if (playlist.ChildCount is > 0 and var listed && (fetched ?? 0) < listed) return true;
        if (!tags.HasSources) return false;
        if (policy.RestrictsBeyondRating || policy.MaxParentalRating is not { } cap || tags.Ratings.Count == 0) return true;
        return tags.Ratings.Any(name =>
        {
            if (ratings.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is not { Score: { } score } rating) return true;
            return score > cap || (score == cap && (rating.SubScore ?? 0) > (policy.MaxParentalSubRating ?? int.MaxValue));
        });
    }

    public static bool Passes(BaseItem item, ChannelTags tags)
    {
        switch (item.Kind)
        {
            case ItemKind.Movie when tags.Content == ChannelContent.Shows: return false;
            case ItemKind.Series when tags.Content == ChannelContent.Movies: return false;
            case ItemKind.Movie or ItemKind.Series: break;
            default: return false;
        }
        if (tags.Years is var (low, high) && (item.ProductionYear is not { } year || year < low || year > high)) return false;
        if (tags.Ratings.Count > 0 && (item.OfficialRating is not { } rating || !tags.Ratings.Any(r => string.Equals(r, rating, StringComparison.OrdinalIgnoreCase)))) return false;
        return true;
    }

    /// <summary>The filler playlists (tagged aster:filler) a channel's filler-playlists= tags name, by name whatever the case.</summary>
    public static IReadOnlyList<BaseItem> FillerPlaylists(ChannelTags tags, IReadOnlyList<BaseItem> playlists)
    {
        var fillers = playlists.Where(p => ChannelTags.IsFillerPlaylist(p.Tags)).ToList();
        return tags.FillerPlaylists.Select(name => fillers.FirstOrDefault(f => string.Equals(f.DisplayTitle, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<BaseItem>().ToList();
    }

    /// <summary>Break clips sorted by what each one's own tag says it is, keeping only the kinds asked for.</summary>
    public static ChannelBreakClips BreakClips(IEnumerable<BaseItem> items, IReadOnlySet<BreakKind> types)
    {
        List<BaseItem> bumpers = [], promos = [], commercials = [], trailers = [], interstitials = [];
        foreach (var item in items)
        {
            if (ChannelTags.ClipKind(item.Tags) is not { } kind || !types.Contains(kind)) continue;
            (kind switch
            {
                BreakKind.Bumpers => bumpers,
                BreakKind.Promos => promos,
                BreakKind.Commercials => commercials,
                BreakKind.Trailers => trailers,
                _ => interstitials,
            }).Add(item);
        }
        return new ChannelBreakClips { Bumpers = bumpers, Promos = promos, Commercials = commercials, Trailers = trailers, Interstitials = interstitials };
    }

    /// <summary>Lower-cased names to ids, the first of any name used twice.</summary>
    private static Dictionary<string, string> IdsByName(IReadOnlyList<BaseItem> items)
    {
        var ids = new Dictionary<string, string>();
        foreach (var item in items) ids.TryAdd(item.DisplayTitle.ToLowerInvariant(), item.Id);
        return ids;
    }

    /// <summary>A channel's own playlist items, then its sources' titles that aren't already among them.</summary>
    public static IReadOnlyList<BaseItem> Merged(IReadOnlyList<BaseItem> own, IReadOnlyList<BaseItem> extra)
    {
        var seen = own.Select(i => i.Id).ToHashSet();
        return [.. own, .. extra.Where(i => seen.Add(i.Id))];
    }

    /// <summary>The playlists tagged as channels, with their tags.</summary>
    public static List<(BaseItem Playlist, ChannelTags Tags)> TaggedChannels(IReadOnlyList<BaseItem> playlists) =>
        playlists.Select(p => (Playlist: p, Tags: ChannelTags.Parse(p.Tags))).Where(t => t.Tags.IsChannel).ToList();

    private static async Task<IReadOnlyList<BaseItem>> OrEmpty<T>(Task<T> loading) where T : IReadOnlyList<BaseItem>
    {
        try { return await loading.ConfigureAwait(false); }
        catch (Exception error) when (IsServerError(error)) { return []; }
    }

    private static bool IsServerError(Exception error) =>
        error is JellyfinException or HttpRequestException or TimeoutException or OperationCanceledException;
}
