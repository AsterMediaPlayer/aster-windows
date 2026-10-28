using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Channels;
using Aster.Core.Jellyfin;
using Aster.Core.LiveTV;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>What's on a channel in the flick row.</summary>
public sealed record FlickProgram(
    string Title,
    string? Subtitle,
    string? Overview,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    Uri? Image,
    // The library item on, for Go to Episode; null on Live TV, whose programmes aren't in the library.
    string? ItemId = null,
    bool IsMovie = false)
{
    public double? Progress(DateTimeOffset now) =>
        Start is { } start && End is { } end && end > start && start <= now && now < end ? (now - start) / (end - start) : null;

    /// <summary>"12 min left", "1 hr, 5 min left"; null once it's over.</summary>
    public string? TimeLeft(DateTimeOffset now)
    {
        if (End is not { } end || end <= now) return null;
        var minutes = Math.Max(1, (int)Math.Ceiling((end - now).TotalMinutes));
        return (minutes >= 60 ? $"{minutes / 60} hr, {minutes % 60} min" : $"{minutes} min") + " left";
    }
}

/// <summary>A channel in the row, what's on it now, and its group when the channels have groups.</summary>
public sealed record FlickChannel(string Id, string? Number, string Name, Uri? Logo, Uri? Bug, FlickProgram? Now, string? Group = null);

/// <summary>Live TV's channels in the guide's order, as the Live TV tab last loaded them, for the player to flick through.</summary>
public static class LiveChannelDirectory
{
    public static IReadOnlyList<BaseItem> Channels { get; private set; } = [];

    /// <summary>Their groups, from the Aster Channels plugin, when there are any, with favourites as a group of their own (they come first).</summary>
    public static ChannelGrouping? Grouping { get; private set; }

    /// <summary>Each channel's own group, for laying a fresh list out the same way.</summary>
    public static IReadOnlyDictionary<string, string> Groups { get; private set; } = new Dictionary<string, string>();

    public static void Register(IReadOnlyList<BaseItem> loaded, ChannelGrouping? grouping = null, IReadOnlyDictionary<string, string>? groups = null)
    {
        Channels = loaded;
        Grouping = grouping;
        Groups = groups ?? new Dictionary<string, string>();
    }
}

/// <summary>One of the five places along the row: the picked channel in the middle, two either side; empty where the row is too short.</summary>
public sealed record FlickCard(FlickChannel? Channel, bool IsSelected, bool IsCurrent, double? Progress, int Offset)
{
    public bool IsEmpty => Channel is null;
    public bool HasProgress => Progress is not null;
    public double ProgressPercent => (Progress ?? 0) * 100;
}

/// <summary>
/// Flicking through channels while one plays, as on a cable box: Up opens a
/// row of channels along the bottom, the one playing in the middle with
/// what's on it, and left and right go round them. Enter changes channel.
/// On a Channels-tab channel, Up picks Go to Episode (or Movie): what's on,
/// played on its own as from its show's page. A port of the Apple TV's
/// ChannelFlickModel and ChannelFlickBar.
/// </summary>
public sealed partial class ChannelFlickViewModel : ObservableObject
{
    private readonly JellyfinClient client;
    private readonly bool isLive;

    private ChannelFlickViewModel(JellyfinClient client, string currentId, bool isLive)
    {
        this.client = client;
        CurrentId = currentId;
        this.isLive = isLive;
        Channels = isLive ? LiveChannels(LiveChannelDirectory.Channels) : PlaylistChannels();
        SelectCurrent();
    }

    /// <summary>The row for a Live TV channel or a Channels-tab one; null for anything else.</summary>
    public static ChannelFlickViewModel? For(JellyfinClient client, string? liveChannel, string? channel) =>
        liveChannel is not null ? new(client, liveChannel, isLive: true)
        : channel is not null ? new(client, channel, isLive: false)
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected), nameof(Cards), nameof(CanGoTo), nameof(GoToTitle), nameof(IsCurrentSelected), nameof(WatchHint),
        nameof(Image), nameof(ChannelLine), nameof(Bug), nameof(Title), nameof(Subtitle), nameof(Overview), nameof(TimeRange), nameof(ProgressPercent), nameof(HasProgress), nameof(TimeLeft),
        nameof(SelectedGroup))]
    public partial IReadOnlyList<FlickChannel> Channels { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected), nameof(Cards), nameof(CanGoTo), nameof(GoToTitle), nameof(IsCurrentSelected), nameof(WatchHint),
        nameof(Image), nameof(ChannelLine), nameof(Bug), nameof(Title), nameof(Subtitle), nameof(Overview), nameof(TimeRange), nameof(ProgressPercent), nameof(HasProgress), nameof(TimeLeft),
        nameof(SelectedGroup))]
    public partial int SelectedIndex { get; private set; }

    /// <summary>The picked channel's group, over the row; it changes as flicking crosses from one group into the next.</summary>
    public string? SelectedGroup => Selected?.Group;

    /// <summary>The channel playing now.</summary>
    public string CurrentId { get; private set; }

    /// <summary>Go to Episode is picked, above the row, rather than the channel.</summary>
    [ObservableProperty] public partial bool IsOnGoTo { get; private set; }

    public FlickChannel? Selected => SelectedIndex >= 0 && SelectedIndex < Channels.Count ? Channels[SelectedIndex] : null;

    // ---- What's on the picked channel ----

    public Uri? Image => Selected?.Now?.Image;
    public string ChannelLine => string.Join("  ", new[] { Selected?.Number, Selected?.Name }.OfType<string>());
    public string Title => Selected?.Now?.Title ?? "No programme information";
    public string? Subtitle => Selected?.Now?.Subtitle;
    public string? Overview => Selected?.Now?.Overview;
    public Uri? Bug => Selected?.Bug;
    public string TimeRange => Selected?.Now is { } now ? LiveTVFormat.TimeRange(now.Start, now.End) : "";
    public double ProgressPercent => (Selected?.Now?.Progress(DateTimeOffset.UtcNow) ?? 0) * 100;
    public bool HasProgress => Selected?.Now?.Progress(DateTimeOffset.UtcNow) is not null;
    public string? TimeLeft => Selected?.Now?.TimeLeft(DateTimeOffset.UtcNow);
    public bool IsCurrentSelected => Selected?.Id == CurrentId;
    public string WatchHint => IsCurrentSelected ? "Watching" : "Press Enter to Watch";

    /// <summary>The picked channel's programme can be gone to on its own.</summary>
    public bool CanGoTo => Selected?.Now?.ItemId is not null;
    public string GoToTitle => Selected?.Now?.IsMovie == true ? "Go to Movie" : "Go to Episode";

    /// <summary>The five cards: two either side of the picked one, each channel once (a spare one goes on the right).</summary>
    public IReadOnlyList<FlickCard> Cards
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var cards = new List<FlickCard>();
            for (var offset = -2; offset <= 2; offset++)
            {
                var channel = Neighbour(offset);
                cards.Add(new FlickCard(channel, offset == 0, channel?.Id == CurrentId, channel?.Now?.Progress(now), offset));
            }
            return cards;
        }
    }

    private FlickChannel? Neighbour(int offset)
    {
        if (Channels.Count == 0) return null;
        var reach = offset > 0 ? Channels.Count / 2 : (Channels.Count - 1) / 2;
        if (Math.Abs(offset) > reach) return null;
        return Channels[((SelectedIndex + offset) % Channels.Count + Channels.Count) % Channels.Count];
    }

    // ---- Moving ----

    /// <summary>Along the row, going round from the last to the first; back down from Go to Episode, if it was picked.</summary>
    public void Move(int step)
    {
        if (Channels.Count == 0) return;
        IsOnGoTo = false;
        SelectedIndex = ((SelectedIndex + step) % Channels.Count + Channels.Count) % Channels.Count;
    }

    /// <summary>Up to Go to Episode, where there's one; down back to the row.</summary>
    public void PickGoTo(bool on) => IsOnGoTo = on && CanGoTo;

    public void Select(string channelId)
    {
        var index = Channels.ToList().FindIndex(c => c.Id == channelId);
        if (index < 0) return;
        IsOnGoTo = false;
        SelectedIndex = index;
    }

    private void SelectCurrent()
    {
        IsOnGoTo = false;
        SelectedIndex = Math.Max(0, Channels.ToList().FindIndex(c => c.Id == CurrentId));
    }

    /// <summary>
    /// What's on now, which the lists kept for the player may have had since
    /// they loaded: Live TV asks the server again; Channels' schedules are
    /// worked out fresh. The pick stays where it was.
    /// </summary>
    public async Task RefreshAsync()
    {
        var picked = Selected?.Id;
        if (isLive)
        {
            try
            {
                var groups = LiveChannelDirectory.Groups;
                var arranged = LiveGuide.Arranged(await client.ChannelsAsync(), groups);
                LiveChannelDirectory.Register(arranged.Channels, arranged.Listed, groups);
                Channels = LiveChannels(arranged.Channels);
            }
            catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { return; }
        }
        else
        {
            Channels = PlaylistChannels();
        }
        var index = Channels.ToList().FindIndex(c => c.Id == picked);
        if (index >= 0) SelectedIndex = index; else SelectCurrent();
        if (!CanGoTo) IsOnGoTo = false;
    }

    private IReadOnlyList<FlickChannel> LiveChannels(IReadOnlyList<BaseItem> channels) => channels.Select(channel =>
    {
        var program = channel.CurrentProgram is { } current
            ? new FlickProgram(
                current.Name ?? "",
                current.EpisodeTitle is { } episode && episode != current.Name ? episode : null,
                current.Overview,
                current.Start,
                current.End,
                current.ImageTags?.GetValueOrDefault("Primary") is { } tag ? client.ImageUrl(current.Id, ImageKind.Primary, tag, maxWidth: 480) : null)
            : null;
        return new FlickChannel(channel.Id, channel.ChannelNumber, channel.DisplayTitle, client.ChannelLogoUrl(channel, width: 300), null, program,
            LiveChannelDirectory.Grouping?.GroupOf(channel.Id));
    }).ToList();

    private IReadOnlyList<FlickChannel> PlaylistChannels()
    {
        var now = DateTimeOffset.UtcNow;
        return ChannelTuner.OrderedChannels.Select(channel =>
        {
            FlickProgram? program = null;
            if (channel.Airing(now) is { } airing)
            {
                // In the break after it: what's coming up, from now to its start.
                if (channel.Segment(now)?.Role.IsBreak == true && channel.Airing(airing.End) is { } next)
                {
                    var (title, subtitle) = Titles(next.Item);
                    program = new FlickProgram(
                        "Break",
                        "Up next: " + string.Join(" · ", new[] { title, subtitle }.OfType<string>()),
                        next.Item.Overview,
                        channel.Segments(airing.Start, now).FirstOrDefault(s => s.Role.IsBreak)?.Start,
                        next.Start,
                        client.LandscapeUrl(next.Item, width: 480),
                        next.Item.Id,
                        next.Item.Kind == ItemKind.Movie);
                }
                else
                {
                    var (title, subtitle) = Titles(airing.Item);
                    program = new FlickProgram(title, subtitle, airing.Item.Overview, airing.Start, airing.End,
                        client.LandscapeUrl(airing.Item, width: 480), airing.Item.Id, airing.Item.Kind == ItemKind.Movie);
                }
            }
            var picture = channel.LogoUrl(client, 560);
            // The picture is a logo when it's the bug or the logo.
            return new FlickChannel(channel.Id, channel.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), channel.Name,
                channel.ShowsLogo ? picture : null, channel.ShowsBug || channel.ShowsLogo ? picture : null, program, ChannelTuner.Grouping?.GroupOf(channel.Id));
        }).ToList();
    }

    /// <summary>A show's name and "S2, E5 · Name", or a movie's name alone.</summary>
    private static (string Title, string? Subtitle) Titles(BaseItem item)
    {
        if (item.Kind != ItemKind.Episode) return (item.DisplayTitle, null);
        var subtitle = string.Join(" · ", new[] { item.EpisodeCode, item.Name }.OfType<string>());
        return (item.SeriesName ?? item.DisplayTitle, subtitle.Length == 0 ? null : subtitle);
    }
}
