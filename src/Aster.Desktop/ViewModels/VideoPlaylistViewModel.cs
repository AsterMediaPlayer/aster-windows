// A playlist of movies and episodes: Play All, Shuffle, and its entries,
// each opening its page and, on a right click, moved up or down or taken
// out. The Apple app's VideoPlaylistView.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>One entry: its picture and names, opening its page; its menu moves or removes it.</summary>
public sealed record PlaylistVideoCard(string Key, string Title, string? Subtitle, Uri? Image, string Placeholder, IRelayCommand Open, IReadOnlyList<TrackAction> Actions);

public sealed partial class VideoPlaylistViewModel : ViewModelBase, IRefreshable
{
    private readonly INavigator navigator;
    private readonly BaseItem playlist;
    private List<BaseItem> items = [];

    public VideoPlaylistViewModel(INavigator navigator, BaseItem playlist)
    {
        this.navigator = navigator;
        this.playlist = playlist;
        Title = playlist.DisplayTitle;
        _ = LoadAsync();
    }

    [ObservableProperty] public partial string Title { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<PlaylistVideoCard> Cards { get; private set; } = [];
    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;
    [ObservableProperty] public partial string? Message { get; private set; }

    public string Count => $"{items.Count} item{(items.Count == 1 ? "" : "s")}";
    public bool HasItems => items.Count > 0;
    public bool IsEmpty => !IsLoading && items.Count == 0 && Message is null;

    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var client = navigator.JellyfinOnly;
            var full = client.ItemAsync(playlist.Id);
            items = (await client.PlaylistItemsAsync(playlist.Id)).ToList();
            Title = (await full).DisplayTitle;
            Message = null;
        }
        catch (Exception error) when (IsServerError(error))
        {
            Message = error.Message;
        }
        IsLoading = false;
        Show();
    }

    private void Show()
    {
        Cards = items.Select((item, position) => new PlaylistVideoCard(
            item.PlaylistRowId,
            item.Kind == ItemKind.Episode ? item.SeriesName ?? item.DisplayTitle : item.DisplayTitle,
            item.Kind == ItemKind.Episode ? string.Join(" · ", new[] { item.EpisodeCode, item.Name }.OfType<string>()) : item.YearText,
            navigator.Client.LandscapeUrl(item, width: 800),
            item.Kind == ItemKind.Episode ? "" : "",
            new RelayCommand(() => navigator.Open(item)),
            Actions(item, position))).ToList();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        PlayAllCommand.NotifyCanExecuteChanged();
        ShuffleCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<TrackAction> Actions(BaseItem item, int position)
    {
        var actions = new List<TrackAction> { new("Open", new RelayCommand(() => navigator.Open(item))) };
        if (item.PlaylistItemId is null) return actions;
        if (position > 0) actions.Add(new("Move Up", new AsyncRelayCommand(() => MoveAsync(item, position - 1))));
        if (position < items.Count - 1) actions.Add(new("Move Down", new AsyncRelayCommand(() => MoveAsync(item, position + 1))));
        actions.Add(new("Remove from Playlist", new AsyncRelayCommand(() => RemoveAsync(item))));
        return actions;
    }

    // ---- Playing ----

    /// <summary>One after another, from the first (or in a shuffled order), each from its start.</summary>
    private void PlayInOrder(IReadOnlyList<BaseItem> order)
    {
        if (order.Count == 0) return;
        navigator.Play(new PlaybackQueueItem { ItemId = order[0].Id, Following = order.Skip(1).Select(i => i.Id).ToList() });
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void PlayAll() => PlayInOrder(items);

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void Shuffle() => PlayInOrder(items.OrderBy(_ => Random.Shared.Next()).ToList());

    // ---- Changing it: shown at once, put back if the server says no ----

    private async Task MoveAsync(BaseItem item, int newIndex)
    {
        var before = items.ToList();
        var from = items.FindIndex(i => i.PlaylistRowId == item.PlaylistRowId);
        if (from < 0) return;
        items.RemoveAt(from);
        items.Insert(newIndex, item);
        Show();
        try { await navigator.JellyfinOnly.MovePlaylistItemAsync(playlist.Id, item.PlaylistItemId!, newIndex); }
        catch (Exception error) when (IsServerError(error))
        {
            items = before;
            Message = error.Message;
            Show();
        }
    }

    private async Task RemoveAsync(BaseItem item)
    {
        var before = items.ToList();
        items.RemoveAll(i => i.PlaylistRowId == item.PlaylistRowId);
        Show();
        try { await navigator.JellyfinOnly.RemoveFromPlaylistAsync(playlist.Id, [item.PlaylistItemId!]); }
        catch (Exception error) when (IsServerError(error))
        {
            items = before;
            Message = error.Message;
            Show();
        }
    }

    [RelayCommand] private void Back() => navigator.Back();

    private static bool IsServerError(Exception error) => error is JellyfinException or HttpRequestException or TimeoutException;
}
