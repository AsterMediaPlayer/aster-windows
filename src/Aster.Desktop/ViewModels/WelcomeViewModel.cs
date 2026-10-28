using System;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// Shown once for each Jellyfin server signed in to: what Aster does, and
/// what its companion plugins add. An administrator can switch the plugins
/// on here and restart the server; anyone else is told to ask theirs. The
/// plugin rows are also Settings ▸ Server Plugins. The Apple app's WelcomeView.
/// </summary>
public sealed partial class WelcomeViewModel : ViewModelBase
{
    private readonly Action done;

    public WelcomeViewModel(JellyfinClient client, string serverName, Action done)
    {
        this.done = done;
        ServerName = serverName;
        Plugins = new ServerPluginsViewModel(client);
        // The note and Start follow what the plugins learn.
        Plugins.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ServerPluginsViewModel.IsAdministrator)) OnPropertyChanged(nameof(PluginsNote));
            if (change.PropertyName == nameof(ServerPluginsViewModel.IsRestarting)) StartCommand.NotifyCanExecuteChanged();
        };
        _ = Plugins.LoadAsync();
    }

    public string ServerName { get; }
    public ServerPluginsViewModel Plugins { get; }

    public string Intro => $"Here's everything that's waiting for you on {ServerName}.";

    public string PluginsNote => Plugins.IsNotAdmin
        ? "Free add-ons for your Jellyfin server that light up new parts of Aster."
        : "Free add-ons for your Jellyfin server that light up new parts of Aster. Switch one on and Aster installs it for you.";

    /// <summary>Not while the server restarts: the app needs it back.</summary>
    private bool CanStart() => !Plugins.IsRestarting;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => done();
}
