using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The options of one series recording rule: which episodes to record, how
/// many to keep, and padding around each airing. Saved together; the rule's
/// other fields go back to the server as they came. A port of the Apple
/// app's SeriesTimerOptionsView.
/// </summary>
public sealed partial class SeriesTimerViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly string ruleId;
    private readonly Action changed;
    private SeriesTimerInfo? rule;

    public SeriesTimerViewModel(INavigator navigator, string ruleId, Action changed)
    {
        this.navigator = navigator;
        this.ruleId = ruleId;
        this.changed = changed;
        _ = LoadAsync();
    }

    public IReadOnlyList<SettingOption<int>> KeepUpToOptions { get; } =
        [new("All", 0), new("1", 1), new("3", 3), new("5", 5), new("10", 10), new("20", 20)];

    public IReadOnlyList<SettingOption<string>> KeepUntilOptions { get; } =
        [new("Until deleted", "UntilDeleted"), new("Until space is needed", "UntilSpaceNeeded"), new("Until watched", "UntilWatched")];

    public IReadOnlyList<SettingOption<int>> PaddingOptions { get; } =
        [new("None", 0), new("1 min", 60), new("2 min", 120), new("5 min", 300), new("10 min", 600), new("15 min", 900)];

    [ObservableProperty] public partial string Title { get; private set; } = "Series Recording";
    [ObservableProperty] public partial string? ChannelName { get; private set; }
    [ObservableProperty] public partial bool IsLoaded { get; private set; }
    [ObservableProperty] public partial bool IsWorking { get; private set; }
    [ObservableProperty] public partial string? Message { get; private set; }

    [ObservableProperty] public partial bool NewOnly { get; set; }
    [ObservableProperty] public partial bool AnyChannel { get; set; }
    [ObservableProperty] public partial bool AnyTime { get; set; } = true;
    [ObservableProperty] public partial bool SkipInLibrary { get; set; }
    [ObservableProperty] public partial SettingOption<int>? KeepUpTo { get; set; }
    [ObservableProperty] public partial SettingOption<string>? KeepUntil { get; set; }
    [ObservableProperty] public partial SettingOption<int>? StartEarly { get; set; }
    [ObservableProperty] public partial SettingOption<int>? EndLate { get; set; }

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await navigator.JellyfinOnly.SeriesTimerAsync(ruleId);
            rule = loaded;
            Title = loaded.Name ?? "Series Recording";
            ChannelName = loaded.ChannelName;
            NewOnly = loaded.RecordNewOnly ?? false;
            AnyChannel = loaded.RecordAnyChannel ?? false;
            AnyTime = loaded.RecordAnyTime ?? true;
            SkipInLibrary = loaded.SkipEpisodesInLibrary ?? false;
            KeepUpTo = Pick(KeepUpToOptions, loaded.KeepUpTo ?? 0);
            KeepUntil = Pick(KeepUntilOptions, loaded.KeepUntil ?? "UntilDeleted");
            StartEarly = Pick(PaddingOptions, loaded.PrePaddingSeconds ?? 0);
            EndLate = Pick(PaddingOptions, loaded.PostPaddingSeconds ?? 0);
            IsLoaded = true;
        }
        catch (Exception error) when (IsServerError(error))
        {
            Message = error.Message;
        }
    }

    /// <summary>The option with this value, or one made for it when the server has a value that isn't in the list.</summary>
    private static SettingOption<T> Pick<T>(IReadOnlyList<SettingOption<T>> options, T value) =>
        options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? options[0];

    [RelayCommand]
    private async Task Save()
    {
        if (rule is null) return;
        var updated = rule with
        {
            RecordNewOnly = NewOnly,
            RecordAnyChannel = AnyChannel,
            RecordAnyTime = AnyTime,
            SkipEpisodesInLibrary = SkipInLibrary,
            KeepUpTo = KeepUpTo?.Value ?? rule.KeepUpTo,
            KeepUntil = KeepUntil?.Value ?? rule.KeepUntil,
            PrePaddingSeconds = StartEarly?.Value ?? rule.PrePaddingSeconds,
            PostPaddingSeconds = EndLate?.Value ?? rule.PostPaddingSeconds,
        };
        await PerformAsync(() => navigator.JellyfinOnly.UpdateSeriesTimerAsync(updated));
    }

    [RelayCommand]
    private Task CancelSeries() => PerformAsync(() => navigator.JellyfinOnly.CancelSeriesTimerAsync(ruleId));

    [RelayCommand] private void Back() => navigator.Back();

    /// <summary>Does it and goes back, or says why it couldn't.</summary>
    private async Task PerformAsync(Func<Task> work)
    {
        IsWorking = true;
        Message = null;
        try
        {
            await work();
            changed();
            navigator.Back();
        }
        catch (Exception error) when (IsServerError(error))
        {
            Message = error.Message;
        }
        IsWorking = false;
    }

    private static bool IsServerError(Exception error) =>
        error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException;
}
