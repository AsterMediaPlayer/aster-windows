// "Add to Playlist": pick one of the user's playlists or make a new one. A
// page of its own over the one it was opened from, which it goes back to
// once the items are in. The Apple app's PlaylistPickerView.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>One of the user's playlists, offered to add to.</summary>
public sealed record PlaylistChoice(string Title, string? Detail, Uri? Image, IAsyncRelayCommand Add);

public sealed partial class PlaylistPickerViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly IReadOnlyList<string> itemIds;
    private readonly string mediaType;

    /// <param name="mediaType">"Audio" or "Video": only playlists of that kind are offered.</param>
    public PlaylistPickerViewModel(INavigator navigator, IReadOnlyList<string> itemIds, string mediaType)
    {
        this.navigator = navigator;
        this.itemIds = itemIds;
        this.mediaType = mediaType;
        _ = LoadAsync();
    }

    public string Count => $"{itemIds.Count} item{(itemIds.Count == 1 ? "" : "s")}";

    public string EmptyText => $"No {(mediaType == "Audio" ? "music" : "video")} playlists yet. Name one above to create it.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmpty), nameof(ShowsList))]
    public partial IReadOnlyList<PlaylistChoice> Playlists { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmpty), nameof(ShowsList))]
    public partial bool IsLoading { get; private set; } = true;

    public bool ShowsEmpty => !IsLoading && Playlists.Count == 0;
    public bool ShowsList => !IsLoading && Playlists.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty] public partial string? Message { get; private set; }

    private async Task LoadAsync()
    {
        try
        {
            var lists = await navigator.JellyfinOnly.PlaylistsAsync(mediaType);
            Playlists = lists.Select(p => new PlaylistChoice(
                p.DisplayTitle,
                p.ChildCount is { } count ? $"{count} item{(count == 1 ? "" : "s")}" : null,
                navigator.Client.PosterUrl(p, width: 120),
                new AsyncRelayCommand(() => AddAsync(p)))).ToList();
        }
        catch (Exception error) when (IsServerError(error))
        {
            Message = $"Couldn't load your playlists. {error.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewName) && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync() => WorkAsync(() => navigator.JellyfinOnly.CreatePlaylistAsync(NewName.Trim(), itemIds, mediaType));

    private Task AddAsync(BaseItem playlist) => WorkAsync(() => navigator.JellyfinOnly.AddToPlaylistAsync(playlist.Id, itemIds));

    /// <summary>Adds (or creates), then back to the page it came from; on a failure, says why and stays.</summary>
    private async Task WorkAsync(Func<Task> work)
    {
        if (IsWorking) return;
        IsWorking = true;
        Message = null;
        try
        {
            await work();
            navigator.Back();
        }
        catch (Exception error) when (IsServerError(error))
        {
            Message = error.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand] private void Back() => navigator.Back();

    private static bool IsServerError(Exception error) => error is JellyfinException or HttpRequestException or TimeoutException;
}

/// <summary>Add to Playlist, wherever it's offered: what a title adds, and the picker for it.</summary>
public static class PlaylistActions
{
    /// <summary>Playlists are Jellyfin's and Emby's.</summary>
    public static bool CanAdd(INavigator navigator) => navigator.Jellyfin is { } client && client.Can(ServerCapabilities.Playlists);

    /// <summary>The picker, for <paramref name="itemIds"/> of <paramref name="mediaType"/> ("Audio" or "Video").</summary>
    public static void Pick(INavigator navigator, IReadOnlyList<string> itemIds, string mediaType)
    {
        if (!CanAdd(navigator) || itemIds.Count == 0) return;
        navigator.Push(new PlaylistPickerViewModel(navigator, itemIds, mediaType));
    }

    /// <summary>
    /// A title's picker: a show adds all its episodes, a season its own, an
    /// album its songs; anything else, itself. As the Apple app's page does.
    /// </summary>
    public static async Task PickForAsync(INavigator navigator, BaseItem item)
    {
        if (!CanAdd(navigator)) return;
        var client = navigator.JellyfinOnly;
        IReadOnlyList<string> ids;
        var mediaType = item.Kind is ItemKind.MusicAlbum or ItemKind.Audio ? "Audio" : "Video";
        try
        {
            ids = item.Kind switch
            {
                ItemKind.Series => (await client.EpisodesAsync(item.Id, null)).Select(e => e.Id).ToList(),
                ItemKind.Season when item.SeriesId is { } seriesId => (await client.EpisodesAsync(seriesId, item.Id)).Select(e => e.Id).ToList(),
                ItemKind.MusicAlbum => (await client.AlbumTracksAsync(item.Id)).Select(t => t.Id).ToList(),
                _ => [item.Id],
            };
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            return;
        }
        Pick(navigator, ids, mediaType);
    }
}
