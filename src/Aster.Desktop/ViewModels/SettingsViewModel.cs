using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A choice in a settings list, shown by its title.</summary>
public sealed record SettingOption<T>(string Title, T Value)
{
    public override string ToString() => Title;
}

/// <summary>A page of Settings, opened from its list.</summary>
public enum SettingsPage
{
    Playback,
    AudioSubtitles,
    DiscMenus,
    TitlePages,
    Account,
    About,
}

/// <summary>
/// The Settings tab, as the Apple TV's: a list of pages (Playback, Audio
/// &amp; Subtitles, Disc Menus, Title Pages, Account, About), each opened
/// over it. This holds what they all show and change. The Up Next, intro,
/// theme, disc menu and styled-subtitle switches follow the user between
/// devices (<see cref="SettingsSync"/>); the languages are the user's own
/// Jellyfin settings, shared with the web app.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly Func<SignOutKind, Task> signOut;
    private UserConfiguration? configuration;

    /// <summary>True while values are being shown, rather than chosen.</summary>
    private bool showing;

    public SettingsViewModel(INavigator navigator, string userName, string serverName, Func<SignOutKind, Task> signOut)
    {
        this.navigator = navigator;
        this.signOut = signOut;
        UserName = userName;
        ServerName = serverName;
        ServerAddress = navigator.Client.BaseUrl.ToString().TrimEnd('/');
        ShowLocal();
        _ = LoadAsync();
    }

    /// <summary>Opens one of the pages over the list.</summary>
    [RelayCommand] private void Open(SettingsPage page) => navigator.Push(new SettingsPageViewModel(this, page));

    /// <summary>Turns a switch over, by its property's name: a row's Enter or click.</summary>
    [RelayCommand]
    private void Flip(string setting)
    {
        switch (setting)
        {
            case nameof(FullScreenPlayback): FullScreenPlayback = !FullScreenPlayback; break;
            case nameof(AutoplayNext): AutoplayNext = !AutoplayNext; break;
            case nameof(AutoSkipIntro): AutoSkipIntro = !AutoSkipIntro; break;
            case nameof(StyledSubtitles): StyledSubtitles = !StyledSubtitles; break;
            case nameof(ThemeCards): ThemeCards = !ThemeCards; break;
            case nameof(ThemeMusic): ThemeMusic = !ThemeMusic; break;
            case nameof(ThemeVideos): ThemeVideos = !ThemeVideos; break;
            case nameof(OpeningSound): OpeningSound = !OpeningSound; break;
            case nameof(PreferDiscMenu): PreferDiscMenu = !PreferDiscMenu; break;
            case nameof(DiscMenuSounds): DiscMenuSounds = !DiscMenuSounds; break;
        }
    }

    /// <summary>A list's choices for its menu, the one chosen ticked.</summary>
    private static IReadOnlyList<TrackChoice> Choices<T>(IReadOnlyList<SettingOption<T>> options, SettingOption<T>? current, Action<SettingOption<T>> pick) =>
        options.Select((option, i) => new TrackChoice(i, option.Title, option == current, new RelayCommand(() => pick(option)))).ToList();

    // ---- Playback ----

    public IReadOnlyList<SettingOption<int>> Bitrates { get; } =
    [
        new("Auto (120 Mbps)", 120_000_000),
        new("60 Mbps", 60_000_000),
        new("40 Mbps", 40_000_000),
        new("20 Mbps", 20_000_000),
        new("10 Mbps", 10_000_000),
        new("4 Mbps", 4_000_000),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitrateChoices))]
    public partial SettingOption<int>? Bitrate { get; set; }

    public IReadOnlyList<TrackChoice> BitrateChoices => Choices(Bitrates, Bitrate, b => Bitrate = b);
    [ObservableProperty] public partial bool FullScreenPlayback { get; set; }
    [ObservableProperty] public partial bool AutoplayNext { get; set; }
    [ObservableProperty] public partial bool AutoSkipIntro { get; set; }
    [ObservableProperty] public partial bool StyledSubtitles { get; set; }
    [ObservableProperty] public partial bool ThemeCards { get; set; }
    [ObservableProperty] public partial bool ThemeMusic { get; set; }
    [ObservableProperty] public partial bool ThemeVideos { get; set; }
    [ObservableProperty] public partial bool OpeningSound { get; set; }
    [ObservableProperty] public partial bool PreferDiscMenu { get; set; }
    [ObservableProperty] public partial bool DiscMenuSounds { get; set; }

    partial void OnBitrateChanged(SettingOption<int>? value)
    {
        if (value is not null) Change(s => s with { MaxBitrate = value.Value });
    }

    partial void OnFullScreenPlaybackChanged(bool value) => Change(s => s with { FullScreenPlayback = value });
    partial void OnAutoplayNextChanged(bool value) => Change(s => s with { AutoplayNext = value });
    partial void OnAutoSkipIntroChanged(bool value) => Change(s => s with { AutoSkipIntro = value });
    partial void OnStyledSubtitlesChanged(bool value) => Change(s => s with { StyledSubtitles = value });
    partial void OnThemeCardsChanged(bool value) => Change(s => s with { ThemeCards = value });
    partial void OnThemeMusicChanged(bool value) => Change(s => s with { ThemeMusic = value });
    partial void OnThemeVideosChanged(bool value) => Change(s => s with { ThemeVideos = value });
    partial void OnOpeningSoundChanged(bool value) => Change(s => s with { OpeningSound = value });
    partial void OnPreferDiscMenuChanged(bool value) => Change(s => s with { PreferDiscMenu = value });
    partial void OnDiscMenuSoundsChanged(bool value) => Change(s => s with { DiscMenuSounds = value });

    private void Change(Func<AppSettings, AppSettings> change)
    {
        if (!showing) _ = SettingsSync.ChangeAsync(navigator.Client, change);
    }

    /// <summary>This computer's settings, as they are now.</summary>
    private void ShowLocal()
    {
        var settings = AppSettings.Current;
        showing = true;
        Bitrate = Bitrates.FirstOrDefault(b => b.Value == settings.MaxBitrate) ?? Bitrates[0];
        FullScreenPlayback = settings.FullScreenPlayback;
        AutoplayNext = settings.AutoplayNext;
        AutoSkipIntro = settings.AutoSkipIntro;
        StyledSubtitles = settings.StyledSubtitles;
        ThemeCards = settings.ThemeCards;
        ThemeMusic = settings.ThemeMusic;
        ThemeVideos = settings.ThemeVideos;
        OpeningSound = settings.OpeningSound;
        PreferDiscMenu = settings.PreferDiscMenu;
        DiscMenuSounds = settings.DiscMenuSounds;
        showing = false;
    }

    // ---- Audio & Subtitles ----

    /// <summary>ISO 639-2 codes as Jellyfin stores them, with an "Any" entry.</summary>
    private static readonly SettingOption<string>[] Languages =
    [
        new("Any", ""), new("English", "eng"), new("Spanish", "spa"), new("French", "fre"), new("German", "ger"),
        new("Italian", "ita"), new("Portuguese", "por"), new("Japanese", "jpn"), new("Korean", "kor"), new("Chinese", "chi"),
        new("Russian", "rus"), new("Arabic", "ara"), new("Hindi", "hin"), new("Dutch", "dut"), new("Swedish", "swe"),
        new("Norwegian", "nor"), new("Danish", "dan"), new("Finnish", "fin"), new("Polish", "pol"), new("Turkish", "tur"),
        new("Hebrew", "heb"), new("Thai", "tha"), new("Vietnamese", "vie"), new("Greek", "gre"), new("Czech", "cze"), new("Hungarian", "hun"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioLanguageChoices))]
    public partial IReadOnlyList<SettingOption<string>> AudioLanguages { get; set; } = Languages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleLanguageChoices))]
    public partial IReadOnlyList<SettingOption<string>> SubtitleLanguages { get; set; } = Languages;

    public IReadOnlyList<SettingOption<SubtitleMode>> SubtitleModes { get; } =
        Enum.GetValues<SubtitleMode>().Select(m => new SettingOption<SubtitleMode>(m.Title, m)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioLanguageChoices))]
    public partial SettingOption<string>? AudioLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleLanguageChoices))]
    public partial SettingOption<string>? SubtitleLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleModeChoices))]
    public partial SettingOption<SubtitleMode>? SubtitleModeChoice { get; set; }

    public IReadOnlyList<TrackChoice> AudioLanguageChoices => Choices(AudioLanguages, AudioLanguage, l => AudioLanguage = l);
    public IReadOnlyList<TrackChoice> SubtitleLanguageChoices => Choices(SubtitleLanguages, SubtitleLanguage, l => SubtitleLanguage = l);
    public IReadOnlyList<TrackChoice> SubtitleModeChoices => Choices(SubtitleModes, SubtitleModeChoice, m => SubtitleModeChoice = m);

    /// <summary>The language settings can be changed once the user's server settings are in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageNote))]
    public partial bool HasConfiguration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageNote))]
    public partial string? ConfigurationError { get; set; }

    public string LanguageNote => ConfigurationError is { } error
        ? $"Your Jellyfin settings couldn't be loaded: {error}"
        : HasConfiguration
            ? "Synced with your Jellyfin profile, so the web app and this PC make the same choices. A title's own Audio & Subtitles menu overrides them for that title."
            : "Loading your Jellyfin settings…";

    partial void OnAudioLanguageChanged(SettingOption<string>? value)
    {
        if (value is not null) UpdateConfiguration(c => c with { AudioLanguagePreference = value.Value });
    }

    partial void OnSubtitleLanguageChanged(SettingOption<string>? value)
    {
        if (value is not null) UpdateConfiguration(c => c with { SubtitleLanguagePreference = value.Value });
    }

    partial void OnSubtitleModeChoiceChanged(SettingOption<SubtitleMode>? value)
    {
        if (value is not null) UpdateConfiguration(c => c with { SubtitleMode = value.Value.ToString() });
    }

    /// <summary>The language list, plus the current value if the server uses a code that isn't in it, so the list never shows nothing.</summary>
    private static IReadOnlyList<SettingOption<string>> LanguagesWith(string current) =>
        current.Length == 0 || Languages.Any(l => l.Value == current) ? Languages : [.. Languages, new(current.ToUpperInvariant(), current)];

    private async void UpdateConfiguration(Func<UserConfiguration, UserConfiguration> change)
    {
        if (showing || configuration is null) return;
        var updated = change(configuration);
        configuration = updated;
        try { await navigator.Client.UpdateConfigurationAsync(updated); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            ConfigurationError = error.Message;
        }
    }

    // ---- Account ----

    /// <summary>Disc menus come from a Jellyfin plugin: their page hides on Emby.</summary>
    public bool IsJellyfin => navigator.Client.IsJellyfin;

    /// <summary>"Jellyfin version" or "Emby version".</summary>
    public string ServerVersionLabel => $"{navigator.Client.Kind} version";

    public string UserName { get; }
    public string ServerName { get; }
    public string ServerAddress { get; }
    [ObservableProperty] public partial string? ServerVersion { get; set; }

    [RelayCommand] private Task SwitchUser() => signOut(SignOutKind.SwitchUser);
    [RelayCommand] private Task ChangeServer() => signOut(SignOutKind.ChangeServer);

    // ---- About ----

    public string AppVersion { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    /// <summary>
    /// The server's copies: the synced switches, the languages and the
    /// server's version, each shown as it arrives.
    /// </summary>
    private async Task LoadAsync()
    {
        var pull = SettingsSync.PullAsync(navigator.Client);
        var info = navigator.Client.PublicSystemInfoAsync();
        try
        {
            configuration = (await navigator.Client.MeAsync()).Configuration ?? new UserConfiguration();
            var audio = configuration.AudioLanguagePreference ?? "";
            var subtitles = configuration.SubtitleLanguagePreference ?? "";
            var mode = Enum.TryParse<SubtitleMode>(configuration.SubtitleMode, out var parsed) ? parsed : SubtitleMode.Default;
            showing = true;
            AudioLanguages = LanguagesWith(audio);
            SubtitleLanguages = LanguagesWith(subtitles);
            AudioLanguage = AudioLanguages.First(l => l.Value == audio);
            SubtitleLanguage = SubtitleLanguages.First(l => l.Value == subtitles);
            SubtitleModeChoice = SubtitleModes.First(m => m.Value == mode);
            showing = false;
            HasConfiguration = true;
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            ConfigurationError = error.Message;
        }
        await pull;
        ShowLocal();
        try { ServerVersion = (await info).Version; }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
    }
}

/// <summary>One page of Settings, over the list: what it shows is the list's (<see cref="SettingsViewModel"/>).</summary>
public sealed class SettingsPageViewModel(SettingsViewModel settings, SettingsPage page) : ViewModelBase
{
    public SettingsViewModel Settings { get; } = settings;

    public string Title => page switch
    {
        SettingsPage.Playback => "Playback",
        SettingsPage.AudioSubtitles => "Audio & Subtitles",
        SettingsPage.DiscMenus => "Disc Menus",
        SettingsPage.TitlePages => "Title Pages",
        SettingsPage.Account => "Account",
        _ => "About",
    };

    public bool IsPlayback => page == SettingsPage.Playback;
    public bool IsAudioSubtitles => page == SettingsPage.AudioSubtitles;
    public bool IsDiscMenus => page == SettingsPage.DiscMenus;
    public bool IsTitlePages => page == SettingsPage.TitlePages;
    public bool IsAccount => page == SettingsPage.Account;
    public bool IsAbout => page == SettingsPage.About;
}
