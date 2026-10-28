using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.LiveTV;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// One programme from the guide: what it is, when it airs, and Watch or
/// Record. The guide's listing shows at once; the full record (artwork,
/// overview, episode) replaces it as it arrives. A port of the Apple app's
/// ProgramDetailView.
/// </summary>
public sealed partial class ProgramViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly LiveTVViewModel live;
    private readonly BaseItem listing;

    public ProgramViewModel(INavigator navigator, LiveTVViewModel live, BaseItem program)
    {
        this.navigator = navigator;
        this.live = live;
        listing = program;
        Program = program;
        IsChannelFavorite = program.ChannelId is { } channel && live.IsFavorite(channel);
        _ = LoadAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelLine), nameof(Title), nameof(EpisodeTitle), nameof(Airing), nameof(Badges), nameof(Genres),
        nameof(Overview), nameof(Progress), nameof(HasProgress), nameof(CanWatch), nameof(CanWatchChannel), nameof(CanRecord),
        nameof(CanCancelRecording), nameof(CanRecordSeries), nameof(HasSeriesOptions), nameof(Backdrop))]
    public partial BaseItem Program { get; private set; }

    /// <summary>The detail record can leave out the image tags the listing had; the listing's picture is kept then.</summary>
    public Uri? Backdrop => live.Client.ProgramImageUrl(Program.ImageTags is { Count: > 0 } ? Program : listing, width: 1920);

    public string ChannelLine => string.Join(" ", new[] { Program.ChannelNumber, Program.ChannelName }.OfType<string>());
    public string Title => Program.DisplayTitle;
    public string? EpisodeTitle => Program.EpisodeTitle;
    public string Airing => LiveTVFormat.Airing(Program.Start, Program.End, DateTimeOffset.Now);
    public string? Genres => Program.Genres is { Count: > 0 } genres ? string.Join(", ", genres.Take(2)) : null;
    public string? Overview => Program.Overview;
    public double Progress => (Program.AiredFraction ?? 0) * 100;
    public bool HasProgress => Program.AiredFraction is not null;

    public IReadOnlyList<string> Badges => new[]
    {
        Program.IsAiringNow ? "ON NOW" : null,
        Program.IsLive == true ? "LIVE" : null,
        Program.IsPremiere == true ? "NEW" : null,
        Program.IsRepeat == true ? "REPEAT" : null,
        Program.OfficialRating,
    }.OfType<string>().ToList();

    private bool NotOver => Program.End is { } end && end > DateTimeOffset.UtcNow;

    // Watch (on now) and Watch Channel (later) are never both shown.
    public bool CanWatch => Program.IsAiringNow && Program.ChannelId is not null;
    public bool CanWatchChannel => !Program.IsAiringNow && Program.ChannelId is not null;
    public bool CanRecord => NotOver && Program.TimerId is null;
    public bool CanCancelRecording => NotOver && Program.TimerId is not null;
    public bool CanRecordSeries => NotOver && Program.IsSeries == true && Program.SeriesTimerId is null;
    public bool HasSeriesOptions => NotOver && Program.IsSeries == true && Program.SeriesTimerId is not null;
    public bool HasChannel => Program.ChannelId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteLabel))]
    public partial bool IsChannelFavorite { get; private set; }

    public string FavoriteLabel => IsChannelFavorite ? "Remove channel from favorites" : "Favorite channel";

    [ObservableProperty] public partial bool IsWorking { get; private set; }
    [ObservableProperty] public partial string? Message { get; private set; }

    private async Task LoadAsync()
    {
        try { Program = await live.Client.ProgramAsync(listing.Id); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
    }

    /// <summary>
    /// The page closes and then the channel plays, as in the Apple app, so
    /// closing the player lands on Live TV with the channel in its header.
    /// </summary>
    [RelayCommand]
    private void Watch()
    {
        if (Program.ChannelId is not { } channel) return;
        navigator.Back();
        live.PlayChannel(channel);
    }

    [RelayCommand] private Task Record() => PerformAsync(client => client.RecordProgramAsync(Program.Id));
    [RelayCommand] private Task CancelRecording() => PerformAsync(client => client.CancelTimerAsync(Program.TimerId!));
    [RelayCommand] private Task RecordSeries() => PerformAsync(client => client.RecordSeriesAsync(Program.Id));

    [RelayCommand]
    private void SeriesOptions()
    {
        if (Program.SeriesTimerId is not { } rule) return;
        navigator.Push(new SeriesTimerViewModel(navigator, rule, () =>
        {
            live.RecordingsRefresh();
            _ = LoadAsync();
        }));
    }

    [RelayCommand]
    private void ToggleChannelFavorite()
    {
        if (Program.ChannelId is not { } channel) return;
        IsChannelFavorite = !IsChannelFavorite;
        live.SetFavorite(channel, IsChannelFavorite);
    }

    [RelayCommand] private void Back() => navigator.Back();

    /// <summary>Does it, then shows the server's own record of the programme, and Recordings and the guide catch up.</summary>
    private async Task PerformAsync(Func<JellyfinClient, Task> work)
    {
        IsWorking = true;
        Message = null;
        try
        {
            await work(live.Client);
            Program = await live.Client.ProgramAsync(listing.Id);
            live.RecordingsRefresh();
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            Message = error.Message;
        }
        IsWorking = false;
    }
}
