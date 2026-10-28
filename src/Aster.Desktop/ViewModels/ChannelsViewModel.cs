using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Channels;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The Channels tab: playlists made into TV-style channels (see
/// <see cref="ChannelTags"/>), laid out in the same guide as Live TV, its
/// listings worked out from each channel's schedule rather than fetched. A
/// port of the Apple app's ChannelsView and ChannelGuideSource.
/// </summary>
public sealed partial class ChannelsViewModel : ViewModelBase, IGuideSource, IRefreshable
{
    /// <summary>How far ahead the guide reaches, like a TV guide's week.</summary>
    private const int Days = 7;

    private readonly INavigator navigator;

    /// <summary>Edit Channels: make channels and set them up (an administrator's; others see why not).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void EditChannels() => navigator.Push(new EditChannelsViewModel(navigator));
    private readonly ChannelLoader loader = new();
    private IReadOnlyList<PlaylistChannel> channels = [];
    private ChannelGrouping? grouping;
    private string? error;

    /// <summary>The scheduled movie or episode behind each guide listing, by the listing's id.</summary>
    private readonly Dictionary<string, ScheduledProgram> scheduled = [];

    public ChannelsViewModel(INavigator navigator)
    {
        this.navigator = navigator;
        Loaded = LoadAsync();
        Guide = new GuideViewModel(this);
    }

    [ObservableProperty] public partial GuideViewModel Guide { get; private set; }

    public Task Loaded { get; private set; }

    private async Task LoadAsync()
    {
        try
        {
            // Channels can be kept to some users (aster:user=), by name.
            var userName = (await navigator.JellyfinOnly.MeAsync()).Name;
            channels = await loader.LoadAsync(navigator.JellyfinOnly, userName);
            // By group once channels have them (aster:group=), as loading put them.
            grouping = ChannelTuner.Grouping;
            error = null;
        }
        catch (Exception failure) when (failure is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            if (failure is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            if (channels.Count == 0) error = failure.Message;
        }
    }

    /// <summary>
    /// Back on the tab: the channels are whatever the server's playlists are
    /// tagged as now (only those whose contents changed are fetched again).
    /// The guide is made again when the channels changed, or once it's an
    /// hour behind.
    /// </summary>
    public async void Refresh()
    {
        var before = channels;
        Loaded = LoadAsync();
        await Loaded;
        if (!before.SequenceEqual(channels) || DateTimeOffset.UtcNow - Guide.GuideStart > TimeSpan.FromHours(1))
        {
            lock (scheduled) scheduled.Clear();
            Guide = new GuideViewModel(this);
        }
    }

    // ---- The guide ----

    public (string Title, string Detail)? Problem =>
        error is { } message ? ("Couldn't load", message)
        : channels.Count == 0 ? ("No channels", "Make a video playlist into a channel in Edit Channels, or tag it aster:channel in Jellyfin.")
        : null;

    /// <summary>A guide channel per playlist channel, its logo when it shows one (its series' clear logo, or the playlist's picture).</summary>
    public IReadOnlyList<GuideChannel> GuideChannels => channels
        .Select(c => new GuideChannel(c.Id, c.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Name,
            c.ShowsLogo ? c.LogoUrl(navigator.JellyfinOnly, 200) : null, IsFavorite: false))
        .ToList();

    public TimeSpan GuideLength => TimeSpan.FromDays(Days);
    public bool CanFavorite => false;
    public ChannelGrouping? Grouping => grouping;

    /// <summary>A channel's group is one of its tags, set in Edit Channels or Jellyfin.</summary>
    public bool CanGroup => false;

    public void ChooseGroup(string channelId) { }

    /// <summary>The channel's programmes, each with its break folded in, as listings; worked out away from the window.</summary>
    public Task<IReadOnlyList<BaseItem>> ProgramsAsync(string channelId, DateTimeOffset from, DateTimeOffset to)
    {
        if (channels.FirstOrDefault(c => c.Id == channelId) is not { } channel) return Task.FromResult<IReadOnlyList<BaseItem>>([]);
        return Task.Run<IReadOnlyList<BaseItem>>(() => channel.Programs(from, to).Select(Listing).ToList());
    }

    /// <summary>
    /// The item with the programme's own id and times, so the guide tells
    /// repeats apart and places it in the timeline. An episode is listed as
    /// the series, with the episode's name under it, as a TV guide does.
    /// </summary>
    private BaseItem Listing(ScheduledProgram program)
    {
        var item = program.Item;
        var listing = item with
        {
            Id = program.Id,
            ChannelId = program.ChannelId,
            StartDate = JellyfinDate.Format(program.Start),
            EndDate = JellyfinDate.Format(program.End),
            Name = item.Kind == ItemKind.Episode && item.SeriesName is { } series ? series : item.Name,
            EpisodeTitle = item.Kind == ItemKind.Episode && item.SeriesName is not null ? item.Name : item.EpisodeTitle,
        };
        lock (scheduled) scheduled[listing.Id] = program;
        return listing;
    }

    /// <summary>Listings already carry everything there is.</summary>
    public Task<BaseItem> DetailAsync(BaseItem program) => Task.FromResult(program);

    /// <summary>The item's own wide art (an episode's still), looked up by its real id.</summary>
    public Uri? PanelImageUrl(BaseItem program)
    {
        ScheduledProgram? found;
        lock (scheduled) found = scheduled.GetValueOrDefault(program.Id);
        return found is null ? null : navigator.JellyfinOnly.LandscapeUrl(found.Item, width: 400);
    }

    /// <summary>What's on now tunes in; anything later opens its page.</summary>
    public void OpenProgram(BaseItem program)
    {
        ScheduledProgram? found;
        lock (scheduled) found = scheduled.GetValueOrDefault(program.Id);
        if (found is null) return;
        if (found.Contains(DateTimeOffset.UtcNow)) PlayChannel(found.ChannelId);
        else navigator.Open(found.Item);
    }

    /// <summary>Plays what's on the channel now, from as far in as it's got.</summary>
    public void PlayChannel(string channelId)
    {
        if (ChannelTuner.Live(channelId) is var (_, entry)) navigator.Play(entry);
    }

    public void SetFavorite(string channelId, bool favorite) { }
}
