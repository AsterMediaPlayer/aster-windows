using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Browsing;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// One upcoming episode: the show's picture, its logo or name, and the
/// episode, marked when its file is already on the server. Opens the
/// episode when it can be watched, else its show.
/// </summary>
public sealed partial class UpcomingCard(INavigator navigator, BaseItem episode) : ICard
{
    public string Key => episode.Id;
    public Uri? Image { get; } = navigator.Client.BackdropUrl(episode, width: 800);
    public Uri? Logo { get; } = navigator.Client.LogoUrl(episode, width: 400);
    public string Title => episode.SeriesName ?? episode.DisplayTitle;
    public string EpisodeLine { get; } = UpcomingSchedule.EpisodeLine(episode);
    public bool IsAvailable { get; } = UpcomingSchedule.IsAvailable(episode);
    public string AccessibleName => string.Join(", ", new[] { Title, EpisodeLine, IsAvailable ? "Available" : null }.OfType<string>());

    [RelayCommand]
    private void Open() => navigator.Open(UpcomingSchedule.Destination(episode));
}

/// <summary>
/// The Upcoming tab: episodes of shows in the library that air soon. The
/// next to air fill the banner with a countdown; below, a row of cards for
/// each day. A port of the Apple app's UpcomingView.
/// </summary>
public sealed class UpcomingViewModel : BrowsePageViewModel
{
    private readonly HeroModel hero;

    public UpcomingViewModel(INavigator navigator) : base(navigator)
    {
        hero = new HeroModel(navigator,
            open: episode => navigator.Open(UpcomingSchedule.Destination(episode)),
            play: episode => navigator.Open(UpcomingSchedule.Destination(episode), playOnOpen: UpcomingSchedule.IsAvailable(episode)),
            tag: episode => UpcomingSchedule.AirDate(episode) is { } date ? UpcomingSchedule.Countdown(date, DateTime.Now) : null,
            action: episode => UpcomingSchedule.IsAvailable(episode) ? new HeroAction("Watch Now", "") : new HeroAction("Go to Show", ""));
        _ = LoadAsync();
    }

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var days = UpcomingSchedule.Days(await Navigator.JellyfinOnly.UpcomingAsync());
        if (days.Count == 0)
        {
            return
            [
                new TitleRow("Upcoming", this),
                new StatusRow("Nothing coming up", "Episodes of shows in your library that air soon show up here."),
            ];
        }
        // Every upcoming episode, soonest first; the banner keeps the first 8.
        hero.Show(days.SelectMany(d => d.Episodes));
        var now = DateTime.Now;
        var rows = new List<BrowseRow> { new HeroRow(hero, this) };
        rows.AddRange(days.Select(d => new ShelfRow(UpcomingSchedule.Heading(d.Day, now),
            d.Episodes.Select(e => (object)new UpcomingCard(Navigator, e)).ToList(), CardStyle.Upcoming)));
        return rows;
    }
}
