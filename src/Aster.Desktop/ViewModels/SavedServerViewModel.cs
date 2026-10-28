using System;
using System.Collections.Generic;
using System.Linq;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// One saved server's page under Settings ▸ Account: switch to it, ask for
/// its password to switch, lock it after time away, or forget it. The Apple
/// app's SavedServerPage.
/// </summary>
public sealed partial class SavedServerViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly IServerAccounts accounts;
    private readonly Action changed;
    private SavedServer server;

    /// <param name="changed">Hears when the server's settings change, or it's forgotten, for the list to show.</param>
    public SavedServerViewModel(INavigator navigator, IServerAccounts accounts, SavedServer server, Action changed)
    {
        this.navigator = navigator;
        this.accounts = accounts;
        this.server = server;
        this.changed = changed;
        AsksPasswordToSwitch = server.AsksPasswordToSwitch;
        LockAfter = server.LockAfterMinutes;
    }

    public string Name => server.Name;
    public string Kind => server.Session.Kind.ToString();
    public string Address => server.Session.ServerUrl.ToString().TrimEnd('/');
    public string UserName => server.Session.UserName;
    public bool IsInUse => server.Id == accounts.CurrentId;
    public bool IsOther => !IsInUse;

    /// <summary>A password to switch, and Lock After: checked by Jellyfin and Emby, or by plex.tv as the Plex Home profile's PIN.</summary>
    public bool CanLock => server.CanBeLocked;

    /// <summary>A Plex profile with nothing to ask for: its page offers signing in again instead (to a profile with a PIN, or one set since).</summary>
    public bool OffersPlexSignIn => !server.CanBeLocked;

    private bool IsPlex => server.Session.Kind == ServerKind.Plex;

    private string Credential => IsPlex ? $"{UserName}'s Plex Home PIN" : $"{UserName}'s password";

    public string AskTitle => $"Ask for {server.CredentialName} to switch";

    public string AsksNote => $"Switching to this server asks for {Credential} first, checked by {(IsPlex ? "plex.tv" : "the server")}, so others on this computer can't switch to it.";

    public string LockNote => $"After this long away from Aster (its window in the background, minimized or closed), coming back to this server asks for {Credential}. Time watching a video doesn't count.";

    public string PlexSignInNote => $"Asking for a PIN to switch, and Lock After, need a Plex Home profile with a PIN. If {UserName} has one now (set at plex.tv), sign in with Plex again to use it.";

    [ObservableProperty]
    public partial bool AsksPasswordToSwitch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockAfterTitle), nameof(LockAfterChoices))]
    public partial int? LockAfter { get; set; }

    public string LockAfterTitle => SavedServer.LockTitle(LockAfter);

    public IReadOnlyList<TrackChoice> LockAfterChoices =>
        new int?[] { null }.Concat(SavedServer.LockChoices.Select(m => (int?)m))
            .Select((minutes, i) => new TrackChoice(i, SavedServer.LockTitle(minutes), minutes == LockAfter, new RelayCommand(() => LockAfter = minutes)))
            .ToList();

    partial void OnAsksPasswordToSwitchChanged(bool value) => Change(s => s with { AsksPasswordToSwitch = value });
    partial void OnLockAfterChanged(int? value) => Change(s => s with { LockAfterMinutes = value });

    private void Change(Func<SavedServer, SavedServer> change)
    {
        // Set from the server as the page opens: nothing's changed yet.
        if (change(server) == server) return;
        server = change(server);
        accounts.Change(server.Id, change);
        changed();
    }

    [RelayCommand] private void FlipAsksPassword() => AsksPasswordToSwitch = !AsksPasswordToSwitch;

    [RelayCommand] private void SignInWithPlexAgain() => accounts.SignInWithPlexAgain();

    /// <summary>The app starts afresh on it (after its password, when it asks).</summary>
    [RelayCommand] private void Switch() => accounts.SwitchTo(server);

    [RelayCommand]
    private void Remove()
    {
        accounts.Remove(server.Id);
        changed();
        navigator.Back();
    }
}
