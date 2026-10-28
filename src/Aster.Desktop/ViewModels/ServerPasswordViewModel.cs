using System;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The user's own password for a saved server, checked by the server: to
/// switch to it, when it's set to ask, or to carry on with it after Lock
/// After (or use another server). The Apple app's ServerPasswordSheet and
/// ServerLockView.
/// </summary>
public sealed partial class ServerPasswordViewModel(SavedServer server, bool locking, Action<StoredSession> passed, Action other) : ViewModelBase
{
    public string ServerName => server.Name;
    public bool IsLock => locking;

    public string Prompt => locking
        ? $"Enter {server.Session.UserName}'s {Credential} to carry on."
        : $"Enter {server.Session.UserName}'s {Credential} to switch to this {server.Session.Kind} server.";

    /// <summary>"password", or a Plex Home profile's "Plex Home PIN".</summary>
    private string Credential => server.Session.Kind == ServerKind.Plex ? "Plex Home PIN" : "password";

    public string Placeholder => server.Session.Kind == ServerKind.Plex ? "PIN" : "Password";

    public string ActionTitle => locking ? "Unlock" : "Switch";
    public string OtherTitle => locking ? "Use Another Server" : "Cancel";

    /// <summary>Empty is allowed: a user without a password gets in with none, as the server says.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsChecking { get; set; }

    private bool CanCheck() => !IsChecking;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsChecking = true;
        Message = null;
        try
        {
            passed(await server.SignInAgainAsync(Password));
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            Message = error switch
            {
                // Plex's are already in words: a wrong PIN, or plex.tv not answering.
                JellyfinException when server.Session.Kind == ServerKind.Plex => error.Message,
                JellyfinException { StatusCode: 401 } => "That password didn't work.",
                HttpRequestException => $"Couldn't reach the server: {error.Message}",
                _ => $"That password didn't work. {error.Message}",
            };
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void Other() => other();
}
