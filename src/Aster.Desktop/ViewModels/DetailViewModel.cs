// The page for a movie, series, season or episode, laid out like the Apple
// TV app: full-bleed artwork, title and actions, then shelves; a show's
// page in tabs. A port of the Apple app's ItemDetailView and DetailModel.
//
// Not yet ported: the Now Playing
// panel for openings and endings (they play full screen, one at a time),
// Watch OP and Watch ED, playlists, recordings and downloads.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Anime;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

public enum DetailSection
{
    Episodes,
    Music,
    Extras,
    Related,
    Details,
}

/// <summary>A tab, or a season to pick: its name, whether it's the one chosen, and choosing it.</summary>
public sealed record Choice(string Title, bool IsSelected, IRelayCommand Select);

/// <summary>One fact in the page's Information section.</summary>
public sealed record InfoRow(string Label, string Value);

public partial class DetailViewModel : ViewModelBase, IRefreshable
{
    private readonly INavigator navigator;
    private bool playOnOpen;
    private IReadOnlyList<BaseItem> seasons = [];
    private IReadOnlyList<BaseItem> episodes = [];

    public DetailViewModel(INavigator navigator, BaseItem item, bool playOnOpen = false)
    {
        this.navigator = navigator;
        this.playOnOpen = playOnOpen;
        Item = item;
        _ = LoadAsync(item);
    }

    private JellyfinClient Client => navigator.Client;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Backdrop), nameof(Logo), nameof(SeriesLogo), nameof(SeriesName), nameof(Title), nameof(EpisodeCode),
        nameof(Metadata), nameof(Tagline), nameof(Overview), nameof(IsFavorite), nameof(IsPlayed), nameof(ShowsWatched), nameof(Remaining),
        nameof(CanGoToSeries), nameof(UsesSections), nameof(PlayLabel), nameof(Resumable), nameof(Information), nameof(HasInformation),
        nameof(ShowsEpisodes), nameof(ShowsExtras), nameof(ShowsRelated), nameof(ShowsDetails))]
    public partial BaseItem Item { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpNextLine), nameof(PlayLabel), nameof(Resumable))]
    public partial BaseItem? NextEpisode { get; set; }

    /// <summary>The title's JellyDisc menu, when the server has one: a Disc Menu button, and maybe where Play goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiscMenu))]
    public partial Aster.Core.DiscMenu.DiscMenuDocument? DiscMenu { get; set; }

    public bool HasDiscMenu => DiscMenu is not null;

    /// <summary>The item the disc menu belongs to: the page's own, or for a show without one, the season it's showing.</summary>
    private string? discMenuItemId;

    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }

    // ---- Header ----

    public Uri? Backdrop => Client.BackdropUrl(Item, width: 1920);

    /// <summary>The theme music and video; the backdrop fades from over the video as it plays.</summary>
    public Aster.Desktop.Audio.ThemePlayer Theme => navigator.Theme;

    /// <summary>Plays the title's theme, while this page is the one in view: for anime without a theme video, an opening.</summary>
    public void ShowTheme()
    {
        navigator.Theme.Show(this, Item);
        ShowOpening();
    }

    /// <summary>An anime show's openings from the OP/ED plugin, or found in its episodes' chapters, for one to loop behind the page.</summary>
    private IReadOnlyList<OpEdTheme> pluginThemes = [];
    private IReadOnlyList<ThemeClip> openingClips = [];

    /// <summary>
    /// Anime with no theme video of its own loops an opening behind the page
    /// instead (with Settings' Theme videos), never one marked as a spoiler
    /// or NSFW: the plugin's opening for the season picked, then one found in
    /// that season's chapters, then the show's first opening of either kind.
    /// With "Opening plays with sound" it has its sound, unless a song or book
    /// is playing. A port of the Apple app's showOpening.
    /// </summary>
    private void ShowOpening()
    {
        var settings = Aster.Core.AppSettings.Current;
        if (!settings.ThemeVideos || Item.Kind != ItemKind.Series || pluginThemes.Count + openingClips.Count == 0) return;
        var sound = settings.OpeningSound && !navigator.Audio.IsPlaying;
        if (SelectedSeasonId is { } seasonId)
        {
            if (PreferredOpening(pluginThemes.Where(t => t.SeasonId == seasonId)) is { } seasonTheme && ShowOpening(seasonTheme, sound)) return;
            var number = seasons.FirstOrDefault(s => s.Id == seasonId)?.IndexNumber;
            if (openingClips.FirstOrDefault(c => c.Season == number) is { } seasonClip)
            {
                ShowOpening(seasonClip, sound);
                return;
            }
            // Nothing for this season: whatever opening is already playing stays.
            if (navigator.Theme.IsShowingOpening) return;
        }
        if (PreferredOpening(pluginThemes) is { } theme && ShowOpening(theme, sound)) return;
        if (openingClips.FirstOrDefault() is { } clip) ShowOpening(clip, sound);
    }

    /// <summary>The first opening that plays and isn't flagged, one with video before one with only its song.</summary>
    private static OpEdTheme? PreferredOpening(IEnumerable<OpEdTheme> themes)
    {
        var playable = themes.Where(t => t.IsOpening && t.PlayablePath is not null && !t.Spoiler && !t.Nsfw).ToList();
        return playable.FirstOrDefault(t => t.VideoMp4Url is not null) ?? playable.FirstOrDefault();
    }

    /// <summary>A plugin opening behind the page: its video, or with only its song, the song as the theme music when openings have sound.</summary>
    private bool ShowOpening(OpEdTheme theme, bool sound)
    {
        if (theme.VideoMp4Url is { } video)
        {
            navigator.Theme.ShowOpening(this, Item, theme.Id, Client.OpEdMediaUrl(video), sound: sound);
            return true;
        }
        if (sound && theme.AudioM4aUrl is { } song)
        {
            navigator.Theme.ShowOpeningSong(this, Item, theme.Id, Client.OpEdMediaUrl(song));
            return true;
        }
        return false;
    }

    /// <summary>An opening found in its episode's chapters: just that stretch of the episode, looping.</summary>
    private void ShowOpening(ThemeClip clip, bool sound) =>
        navigator.Theme.ShowOpening(this, Item, clip.Id, Client.VideoStreamUrl(clip.Episode.Id),
            clip.StartTicks / 10_000_000.0, clip.EndTicks / 10_000_000.0, sound);

    // The page's stub becomes the whole title as it loads: an episode's series is known then.
    partial void OnItemChanged(BaseItem value) => ShowTheme();

    /// <summary>A movie's or show's own logo, in place of its title.</summary>
    public Uri? Logo => Item.Kind is ItemKind.Episode or ItemKind.Season ? null : Client.LogoUrl(Item);

    /// <summary>An episode or season page names its show above its own title: the show's logo when it has one, else its name.</summary>
    public Uri? SeriesLogo => Item.Kind is ItemKind.Episode or ItemKind.Season ? Client.LogoUrl(Item) : null;

    public string? SeriesName => Item.Kind is ItemKind.Episode or ItemKind.Season ? Item.SeriesName : null;
    public string Title => Item.DisplayTitle;
    public string? EpisodeCode => Item.Kind == ItemKind.Episode ? Item.EpisodeCode : null;
    public MetadataLine Metadata => MetadataLine.For(Item, showsUnwatched: true);
    public string? Tagline => Item.Taglines?.FirstOrDefault();
    public string? Overview => Item.Overview;

    public string? UpNextLine => Item.Kind == ItemKind.Series && NextEpisode is { } next
        ? "Up next: " + string.Join(" · ", new[] { next.EpisodeCode, next.Name }.OfType<string>())
        : null;

    // ---- Actions ----

    public bool Resumable => Item.Kind == ItemKind.Series ? NextEpisode?.CanResume ?? false : Item.CanResume;

    public string PlayLabel => Item.Kind == ItemKind.Series
        ? NextEpisode is null ? "Play" : Resumable ? "Resume" : "Play Next"
        : Resumable ? "Resume" : "Play";

    public bool IsFavorite => Item.UserData?.IsFavorite == true;
    public bool IsPlayed => Item.UserData?.Played == true;
    public bool ShowsWatched => Item.Kind is not (ItemKind.Series or ItemKind.Season);
    public string? Remaining => Item.Kind == ItemKind.Series ? null : Item.RemainingText;
    public bool CanGoToSeries => Item.Kind is ItemKind.Episode or ItemKind.Season && Item.SeriesId is not null;

    // ---- What's below ----

    /// <summary>Shows and seasons are laid out in tabs; everything else is one scroll.</summary>
    public bool UsesSections => Item.Kind is ItemKind.Series or ItemKind.Season;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEpisodes), nameof(ShowsMusic), nameof(ShowsExtras), nameof(ShowsRelated), nameof(ShowsDetails))]
    public partial DetailSection Section { get; set; } = DetailSection.Episodes;

    [ObservableProperty] public partial IReadOnlyList<Choice> Sections { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<Choice> Seasons { get; set; } = [];
    [ObservableProperty] public partial string? SelectedSeasonId { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ItemCard> Episodes { get; set; } = [];

    /// <summary>An episode's page: the rest of its season.</summary>
    [ObservableProperty] public partial IReadOnlyList<ItemCard> SeasonEpisodes { get; set; } = [];

    /// <summary>The episode after this one, which the strip scrolls into view.</summary>
    [ObservableProperty] public partial ItemCard? StripTarget { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsExtras))]
    public partial IReadOnlyList<ItemCard> Extras { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRelated))]
    public partial IReadOnlyList<ItemCard> Similar { get; set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ItemCard> Cast { get; set; } = [];

    /// <summary>An anime show's openings and endings, each in its own row of the Music tab.</summary>
    [ObservableProperty] public partial IReadOnlyList<OpeningEndingCard> Openings { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<OpeningEndingCard> Endings { get; set; } = [];

    public bool ShowsEpisodes => UsesSections && Section == DetailSection.Episodes;
    public bool ShowsMusic => UsesSections && Section == DetailSection.Music;
    public bool ShowsExtras => (!UsesSections || Section == DetailSection.Extras) && Extras.Count > 0;
    public bool ShowsRelated => (!UsesSections || Section == DetailSection.Related) && Similar.Count > 0;
    public bool ShowsDetails => !UsesSections || Section == DetailSection.Details;

    public bool HasInformation => Information.Count > 0;

    public IReadOnlyList<InfoRow> Information
    {
        get
        {
            var rows = new List<InfoRow>();
            if (Item.Directors.Count > 0) rows.Add(new("Director", string.Join(", ", Item.Directors)));
            if (Item.Writers.Count > 0) rows.Add(new("Writers", string.Join(", ", Item.Writers.Take(4))));
            if (Item.Studios is { Count: > 0 } studios) rows.Add(new("Studio", string.Join(", ", studios.Select(s => s.Name).OfType<string>().Take(3))));
            if (Item.Genres is { Count: > 0 } genres) rows.Add(new("Genres", string.Join(", ", genres)));
            if (Item.OfficialRating is { } rating) rows.Add(new("Rated", rating));
            if (Item.PremiereDate is { Length: >= 10 } date) rows.Add(new("Released", date[..10]));
            return rows;
        }
    }

    // ---- Loading ----

    private async Task LoadAsync(BaseItem summary)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var full = await Client.ItemAsync(summary.Id);
            Item = full;
            Cast = full.Cast.Select(p => ItemCard.Person(navigator, p)).ToList();
            IsLoading = false;

            var similar = Client.SimilarAsync(full.Id, 20);
            var themes = full.Kind == ItemKind.Series && full.IsAnime ? OpeningsEndingsAsync(full) : Task.FromResult<IReadOnlyList<OpeningEndingCard>>([]);
            switch (full.Kind)
            {
                case ItemKind.Series:
                    seasons = await Client.SeasonsAsync(full.Id);
                    NextEpisode = (await OrEmpty(Client.NextUpAsync(1, seriesId: full.Id))).FirstOrDefault();
                    var preferred = NextEpisode?.SeasonId ?? seasons.FirstOrDefault(s => (s.IndexNumber ?? 0) > 0)?.Id ?? seasons.FirstOrDefault()?.Id;
                    if (preferred is not null) await SelectSeasonAsync(preferred);
                    break;
                case ItemKind.Season when full.SeriesId is { } seriesId:
                    seasons = await OrEmpty(Client.SeasonsAsync(seriesId));
                    await SelectSeasonAsync(full.Id);
                    break;
                case ItemKind.Episode when full.SeriesId is { } seriesId && full.SeasonId is { } seasonId:
                    ShowSeasonEpisodes(await OrEmpty(Client.EpisodesAsync(seriesId, seasonId)));
                    break;
            }
            if (full.Kind is ItemKind.Movie or ItemKind.Series or ItemKind.Season)
            {
                Extras = (await OrEmpty(Client.SpecialFeaturesAsync(full.Id))).Select(e => ItemCard.Extra(navigator, e)).ToList();
            }
            Similar = (await OrEmpty(similar)).Select(i => ItemCard.Landscape(navigator, i)).ToList();
            await LoadDiscMenuAsync();
            var found = await themes;
            Openings = found.Where(c => c.IsOpening).ToList();
            Endings = found.Where(c => !c.IsOpening).ToList();
            ShowOpening();
            UpdateSections();

            // Opened from a banner's Play: play now, with this page behind.
            if (playOnOpen)
            {
                playOnOpen = false;
                Play(fromBeginning: false);
            }
        }
        catch (Exception error) when (IsServerError(error))
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            ErrorMessage = error.Message;
            IsLoading = false;
        }
    }

    private static bool IsServerError(Exception error) => error is JellyfinException or HttpRequestException or TimeoutException;

    private async Task<Aster.Core.DiscMenu.DiscMenuDocument?> DiscMenuOrNullAsync(string itemId)
    {
        try { return await Client.DiscMenuAsync(itemId); }
        catch (Exception error) when (IsServerError(error) || error is System.Text.Json.JsonException) { return null; }
    }

    private static async Task<IReadOnlyList<BaseItem>> OrEmpty<T>(Task<T> request) where T : IReadOnlyList<BaseItem>
    {
        try { return await request; }
        catch (Exception error) when (IsServerError(error)) { return []; }
    }

    /// <summary>Freshens watched states and resume points, after playing or coming back to the page.</summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            Item = await Client.ItemAsync(Item.Id);
            if (SelectedSeasonId is { } seasonId && (Item.Kind == ItemKind.Series ? Item.Id : Item.SeriesId) is { } seriesId)
            {
                ShowEpisodes(await Client.EpisodesAsync(seriesId, seasonId));
            }
            if (Item.Kind == ItemKind.Series) NextEpisode = (await OrEmpty(Client.NextUpAsync(1, seriesId: Item.Id))).FirstOrDefault();
            if (Item.Kind == ItemKind.Episode && Item.SeriesId is { } series && Item.SeasonId is { } season)
            {
                ShowSeasonEpisodes(await Client.EpisodesAsync(series, season));
            }
            await LoadDiscMenuAsync();
        }
        catch (Exception error) when (IsServerError(error)) { }
    }

    private async Task SelectSeasonAsync(string seasonId)
    {
        if ((Item.Kind == ItemKind.Series ? Item.Id : Item.SeriesId) is not { } seriesId) return;
        SelectedSeasonId = seasonId;
        UpdateSeasonChoices();
        ShowEpisodes(await OrEmpty(Client.EpisodesAsync(seriesId, seasonId)));
        // The opening behind the page follows the season picked.
        if (Item.Kind == ItemKind.Series) ShowOpening();
        // A show's menu can be its season's: follow the season picked.
        if (Item.Kind == ItemKind.Series && discMenuItemId != Item.Id) await LoadDiscMenuAsync();
    }

    /// <summary>
    /// The page's disc menu: its own (a movie's, show's or season's), or for
    /// a show without one, the menu of the season it's showing, which is
    /// where a one-season show's menu is often bound.
    /// </summary>
    private async Task LoadDiscMenuAsync()
    {
        if (Item.Kind is not (ItemKind.Movie or ItemKind.Series or ItemKind.Season)) return;
        if (await DiscMenuOrNullAsync(Item.Id) is { } own)
        {
            discMenuItemId = Item.Id;
            DiscMenu = own;
        }
        else if (Item.Kind == ItemKind.Series && SelectedSeasonId is { } seasonId && await DiscMenuOrNullAsync(seasonId) is { } seasons)
        {
            discMenuItemId = seasonId;
            DiscMenu = seasons;
        }
        else
        {
            discMenuItemId = null;
            DiscMenu = null;
        }
    }

    private void ShowEpisodes(IReadOnlyList<BaseItem> found)
    {
        episodes = found;
        Episodes = found.Select(e => ItemCard.Landscape(navigator, e, showSeriesTitle: false)).ToList();
    }

    /// <summary>The rest of an episode's season, with the one after it to scroll to (or the one before, on the last).</summary>
    private void ShowSeasonEpisodes(IReadOnlyList<BaseItem> season)
    {
        var at = season.Select((e, i) => (e, i)).FirstOrDefault(p => p.e.Id == Item.Id, (null!, -1)).Item2;
        var cards = season.Where(e => e.Id != Item.Id).Select(e => ItemCard.Landscape(navigator, e, showSeriesTitle: false)).ToList();
        SeasonEpisodes = cards;
        var targetId = at >= 0 && at + 1 < season.Count ? season[at + 1].Id : at > 0 ? season[at - 1].Id : null;
        StripTarget = cards.FirstOrDefault(c => c.Item.Id == targetId);
    }

    /// <summary>
    /// The OP/ED plugin's themes when it has any that play (or soon will)
    /// for the show, so chapters needn't be read; otherwise clips found in
    /// the episodes' chapters. Openings (or endings) come season by season,
    /// themes for the whole show first.
    /// </summary>
    private async Task<IReadOnlyList<OpeningEndingCard>> OpeningsEndingsAsync(BaseItem show)
    {
        var themes = (await Client.OpEdThemesAsync(show.Id) ?? []).Where(t => t.PlayablePath is not null || t.IsPreparing).ToList();
        if (themes.Count > 0)
        {
            pluginThemes = themes;
            var allSeasons = await OrEmpty(Client.SeasonsAsync(show.Id));
            // A season's name only when there's more than one, since OP 1 comes round again each season.
            var named = allSeasons.Count(s => (s.IndexNumber ?? 0) > 0) > 1;
            return themes
                .Select(theme =>
                {
                    var at = theme.SeasonId is { } id ? allSeasons.Select((s, i) => (s, i)).FirstOrDefault(p => p.s.Id == id, (null!, -1)).Item2 : -1;
                    var seasonName = named && at >= 0 ? allSeasons[at].DisplayTitle : null;
                    return OpeningEndingCard.Theme(Client, theme, show, seasonName, at, PlayTheme);
                })
                .OrderBy(c => c.Order)
                .ToList();
        }
        IReadOnlyList<BaseItem> withChapters;
        try { withChapters = await Client.EpisodesWithChaptersAsync(show.Id); }
        catch (Exception error) when (IsServerError(error)) { return []; }
        var clips = OpeningsEndings.Find(withChapters);
        openingClips = clips.Where(c => c.Kind == ThemeKind.Opening).ToList();
        return clips.Select(clip => OpeningEndingCard.Clip(Client, clip, show.DisplayTitle, PlayTheme)).ToList();
    }

    /// <summary>An opening or ending, full screen; the page needn't follow, since nothing it shows changes.</summary>
    private void PlayTheme(PlaybackQueueItem entry) => navigator.Play(entry);

    private void UpdateSeasonChoices() =>
        Seasons = seasons.Count > 1
            ? seasons.Select(s => new Choice(s.DisplayTitle, s.Id == SelectedSeasonId, new AsyncRelayCommand(() => SelectSeasonAsync(s.Id)))).ToList()
            : [];

    /// <summary>The tabs with something in them, in order.</summary>
    private void UpdateSections()
    {
        if (!UsesSections) return;
        var found = new List<DetailSection> { DetailSection.Episodes };
        if (Openings.Count + Endings.Count > 0) found.Add(DetailSection.Music);
        if (Extras.Count > 0) found.Add(DetailSection.Extras);
        if (Similar.Count > 0) found.Add(DetailSection.Related);
        found.Add(DetailSection.Details);
        if (!found.Contains(Section)) Section = DetailSection.Episodes;
        Sections = found.Select(s => new Choice(s.ToString(), s == Section, new RelayCommand(() =>
        {
            Section = s;
            UpdateSections();
        }))).ToList();
    }

    // ---- Doing ----

    [RelayCommand] private void Back() => navigator.Back();

    [RelayCommand] private void PlayResume() => Play(fromBeginning: false);

    [RelayCommand] private void PlayFromBeginning() => Play(fromBeginning: true);

    /// <summary>The title's disc menu, like putting in the disc.</summary>
    [RelayCommand]
    private void OpenDiscMenu()
    {
        if (DiscMenu is { } document) navigator.ShowDiscMenu(document, discMenuItemId ?? Item.Id, Refresh);
    }

    /// <summary>
    /// What Play starts: the item itself, or for a show the next episode, for
    /// a season its first unwatched one. With Settings' "Open disc menu when
    /// pressing Play", a title with a menu opens it instead (From Beginning
    /// still plays).
    /// </summary>
    private void Play(bool fromBeginning)
    {
        if (!fromBeginning && Aster.Core.AppSettings.Current.PreferDiscMenu && DiscMenu is not null)
        {
            OpenDiscMenu();
            return;
        }
        PlaybackQueueItem? entry = Item.Kind switch
        {
            ItemKind.Series when NextEpisode is { } next => Entry(next, fromBeginning),
            ItemKind.Series => episodes.FirstOrDefault() is { } first ? new PlaybackQueueItem { ItemId = first.Id } : null,
            ItemKind.Season => (episodes.FirstOrDefault(e => e.UserData?.Played != true) ?? episodes.FirstOrDefault()) is { } target ? Entry(target, fromBeginning) : null,
            _ => Entry(Item, fromBeginning),
        };
        if (entry is null)
        {
            Message = "There is nothing to play yet.";
            return;
        }
        Message = null;
        navigator.Play(entry, FollowPlayback);
    }

    private static PlaybackQueueItem Entry(BaseItem item, bool fromBeginning) =>
        new() { ItemId = item.Id, StartTicks = fromBeginning ? 0 : item.UserData?.PlaybackPositionTicks ?? 0 };

    /// <summary>
    /// After the player closes, the page shows where viewing got to. When
    /// later episodes played on their own, an episode's page becomes the last
    /// one, and a show's page turns to its season; otherwise it just picks up
    /// the new watched state.
    /// </summary>
    private void FollowPlayback(BaseItem? reached)
    {
        var show = Item.Kind == ItemKind.Series ? Item.Id : Item.SeriesId;
        if (reached is not null && reached.Id != Item.Id && reached.SeriesId is not null && reached.SeriesId == show)
        {
            if (Item.Kind == ItemKind.Episode)
            {
                _ = LoadAsync(reached);
                return;
            }
            if (Item.Kind == ItemKind.Series && reached.SeasonId is { } seasonId && seasonId != SelectedSeasonId)
            {
                _ = SelectSeasonAsync(seasonId);
            }
        }
        Refresh();
    }

    [RelayCommand]
    private Task ToggleFavoriteAsync()
    {
        var favorite = !IsFavorite;
        return UpdateAsync((Item.UserData ?? new UserItemData()) with { IsFavorite = favorite }, () => Client.SetFavoriteAsync(Item.Id, favorite));
    }

    [RelayCommand]
    private Task TogglePlayedAsync()
    {
        var played = !IsPlayed;
        var data = (Item.UserData ?? new UserItemData()) with { Played = played };
        if (played) data = data with { PlaybackPositionTicks = 0 };
        return UpdateAsync(data, () => Client.SetPlayedAsync(Item.Id, played));
    }

    private async Task UpdateAsync(UserItemData data, Func<Task> send)
    {
        var before = Item;
        Item = Item with { UserData = data };
        try
        {
            await send();
            navigator.UserDataChanged();
        }
        catch (Exception error) when (IsServerError(error))
        {
            Item = before;
        }
    }

    [RelayCommand]
    private void GoToSeries()
    {
        if (Item.SeriesId is { } id) navigator.Open(new BaseItem { Id = id, Name = Item.SeriesName, Type = "Series" });
    }
}
