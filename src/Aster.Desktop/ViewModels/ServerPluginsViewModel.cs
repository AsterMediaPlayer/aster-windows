using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// One companion plugin as a card or a settings row: a switch for an
/// administrator, else whether it's on the server.
/// </summary>
public sealed partial class PluginRow(ServerPluginsViewModel owner, AsterPlugin plugin) : ObservableObject
{
    public AsterPlugin Plugin { get; } = plugin;
    public string PackageName => Plugin.PackageName;
    public string Headline => Plugin.Headline;
    public string Summary => Plugin.Summary;

    /// <summary>Its icon: a TV, a film, a note.</summary>
    public string Icon => Plugin switch
    {
        AsterPlugin.Channels => "",
        AsterPlugin.Disc => "",
        _ => "",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(Caption), nameof(IsGood), nameof(IsInstalling), nameof(OnOff), nameof(CanSwitch), nameof(ShowsSwitch))]
    public partial PluginState State { get; set; } = PluginState.Unknown;

    public bool IsOn => State.IsOn;
    public string Caption => State.Caption;
    public bool IsGood => State.IsGood;
    public bool IsInstalling => State.Status == PluginStatus.Installing;
    public string OnOff => State.IsOn ? "On" : "Off";

    /// <summary>An administrator's switch shows while the plugin can be switched.</summary>
    public bool ShowsSwitch => owner.IsAdministrator == true && State.CanSwitch;
    public bool CanSwitch => ShowsSwitch && !owner.IsRestarting;

    internal void Refresh() => OnPropertyChanged(nameof(CanSwitch));

    internal void RefreshAdministrator() => OnPropertyChanged(nameof(ShowsSwitch));

    [RelayCommand]
    private Task Flip() => CanSwitch ? owner.SetAsync(this, !IsOn) : Task.CompletedTask;
}

/// <summary>
/// The companion plugins on the signed-in server, for the welcome screen and
/// Settings ▸ Server Plugins. The Apple app's ServerPluginsModel.
/// </summary>
public sealed partial class ServerPluginsViewModel : ObservableObject
{
    private readonly JellyfinClient client;
    private IReadOnlyList<InstalledPlugin> installed = [];
    private IReadOnlyList<PluginPackage>? packages;
    private string? serverVersion;

    /// <summary>Changes made here since the last restart, which the server's list doesn't always show yet.</summary>
    private readonly Dictionary<AsterPlugin, bool> pending = [];

    public ServerPluginsViewModel(JellyfinClient client)
    {
        this.client = client;
        Plugins = AsterPlugins.All.Select(p => new PluginRow(this, p)).ToList();
    }

    public IReadOnlyList<PluginRow> Plugins { get; }

    /// <summary>Whether the user is an administrator; null until known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsAdmin), nameof(IsNotAdmin), nameof(ShowsRestart), nameof(ShowsRestartButton), nameof(ShowsRestartNote))]
    public partial bool? IsAdministrator { get; private set; }

    public bool IsLoading => IsAdministrator is null;
    public bool IsAdmin => IsAdministrator == true;
    public bool IsNotAdmin => IsAdministrator == false;

    /// <summary>A change waits on a restart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRestart), nameof(ShowsRestartButton), nameof(ShowsRestartNote))]
    public partial bool NeedsRestart { get; private set; }

    /// <summary>False where something else must restart the server (Docker, say).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRestartButton), nameof(ShowsRestartNote))]
    public partial bool CanRestart { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRestart), nameof(ShowsRestartButton), nameof(ShowsRestartNote))]
    [NotifyCanExecuteChangedFor(nameof(RestartServerCommand))]
    public partial bool IsRestarting { get; private set; }

    /// <summary>Why the restart didn't happen, or the server didn't come back.</summary>
    [ObservableProperty] public partial string? RestartError { get; private set; }

    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    public bool ShowsRestart => IsAdmin && NeedsRestart && !IsRestarting;
    public bool ShowsRestartButton => ShowsRestart && CanRestart;
    public bool ShowsRestartNote => ShowsRestart && !CanRestart;

    public string RepositoryUrl => AsterPlugins.RepositoryUrl;

    partial void OnIsAdministratorChanged(bool? value)
    {
        foreach (var row in Plugins) row.RefreshAdministrator();
    }

    partial void OnIsRestartingChanged(bool value)
    {
        foreach (var row in Plugins) row.Refresh();
    }

    public async Task LoadAsync()
    {
        if (!client.IsJellyfin) return;
        bool admin;
        try { admin = (await client.MeAsync()).Policy?.IsAdministrator ?? false; }
        catch (Exception error) when (IsServerError(error)) { admin = false; }
        if (admin)
        {
            var plugins = OrNull(client.InstalledPluginsAsync());
            var info = OrNull(client.ServerSystemInfoAsync());
            var catalogue = OrNull(client.PluginPackagesAsync());
            installed = await plugins ?? [];
            var system = await info;
            packages = await catalogue;
            serverVersion = system?.Version;
            CanRestart = system?.CanSelfRestart ?? true;
            NeedsRestart = NeedsRestart || system?.HasPendingRestart == true;
            IsAdministrator = true;
            UpdateStates();
        }
        else
        {
            IsAdministrator = false;
            foreach (var row in Plugins)
            {
                row.State = await client.ServerHasAsync(row.Plugin.ProbePath) is { } present ? new(PluginStatus.Present, On: present) : PluginState.Unknown;
            }
        }
    }

    /// <summary>An administrator's switch: installs, turns on or turns off.</summary>
    internal async Task SetAsync(PluginRow row, bool on)
    {
        var plugin = row.Plugin;
        var state = row.State;
        ErrorMessage = null;
        try
        {
            switch (state.Status, on)
            {
                case (PluginStatus.NotInstalled, true):
                    row.State = new(PluginStatus.Installing);
                    await AddRepositoryAsync();
                    await client.InstallPackageAsync(plugin.PackageName, plugin.Guid, AsterPlugins.RepositoryUrl);
                    break;
                case (PluginStatus.Disabled or PluginStatus.AfterRestart, true) or (PluginStatus.Active or PluginStatus.AfterRestart, false):
                    if (PluginState.Current(installed, plugin) is not { } current)
                    {
                        // Installed here and not listed yet: the server can't change it until it restarts.
                        ErrorMessage = $"Restart the server first, then turn {plugin.PackageName} off.";
                        return;
                    }
                    await client.SetPluginAsync(current.Id, current.Version, on);
                    break;
                default:
                    return;
            }
            pending[plugin] = on;
            NeedsRestart = true;
        }
        catch (Exception error) when (IsServerError(error))
        {
            ErrorMessage = on
                ? $"Couldn't install {plugin.PackageName}: {error.Message}"
                : $"Couldn't turn {plugin.PackageName} off: {error.Message}";
        }
        installed = await OrNull(client.InstalledPluginsAsync()) ?? installed;
        UpdateStates();
    }

    /// <summary>Restarts the server and waits for it to come back, then looks again.</summary>
    [RelayCommand(CanExecute = nameof(CanRestartNow))]
    private async Task RestartServerAsync()
    {
        IsRestarting = true;
        RestartError = null;
        try
        {
            await client.RestartServerAsync();
        }
        catch (Exception error) when (IsServerError(error))
        {
            RestartError = $"The server wouldn't restart: {error.Message}";
            IsRestarting = false;
            return;
        }
        // It takes a moment to go; then it's back once it answers again.
        var started = DateTime.UtcNow;
        var wentDown = false;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(3))
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var answers = await OrNull(client.PublicSystemInfoAsync()) is not null;
            if (!answers) wentDown = true;
            if (answers && (wentDown || DateTime.UtcNow - started > TimeSpan.FromSeconds(20)))
            {
                pending.Clear();
                NeedsRestart = false;
                IsRestarting = false;
                await LoadAsync();
                return;
            }
        }
        IsRestarting = false;
        RestartError = "The server hasn't come back after three minutes. Check on it, then open this page again.";
    }

    private bool CanRestartNow() => !IsRestarting;

    /// <summary>Aster's repository in the server's list, turned on, keeping the rest.</summary>
    private async Task AddRepositoryAsync()
    {
        var repositories = await client.PluginRepositoriesAsync();
        var index = repositories.FindIndex(r => string.Equals(r.Url, AsterPlugins.RepositoryUrl, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (repositories[index].Enabled != false) return;
            repositories[index] = repositories[index] with { Enabled = true };
        }
        else
        {
            repositories.Add(new PluginRepository { Name = AsterPlugins.RepositoryName, Url = AsterPlugins.RepositoryUrl, Enabled = true });
        }
        await client.SetPluginRepositoriesAsync(repositories);
    }

    private void UpdateStates()
    {
        foreach (var row in Plugins)
        {
            row.State = pending.TryGetValue(row.Plugin, out var on)
                ? new(PluginStatus.AfterRestart, On: on)
                : PluginState.From(installed, packages, serverVersion, row.Plugin);
        }
    }

    private static bool IsServerError(Exception error) => error is JellyfinException or HttpRequestException or TimeoutException;

    private static async Task<T?> OrNull<T>(Task<T> request) where T : class
    {
        try { return await request; }
        catch (Exception error) when (IsServerError(error)) { return null; }
    }
}

/// <summary>
/// Settings ▸ Server Plugins: the welcome screen's switches, any time, and
/// the welcome screen itself again. The Apple app's ServerPluginsSettingsView.
/// </summary>
public sealed partial class ServerPluginsPageViewModel : ViewModelBase
{
    private readonly Action showWelcome;

    public ServerPluginsPageViewModel(JellyfinClient client, Action showWelcome)
    {
        this.showWelcome = showWelcome;
        Plugins = new ServerPluginsViewModel(client);
        _ = Plugins.LoadAsync();
    }

    public ServerPluginsViewModel Plugins { get; }

    [RelayCommand] private void ShowWelcome() => showWelcome();
}
