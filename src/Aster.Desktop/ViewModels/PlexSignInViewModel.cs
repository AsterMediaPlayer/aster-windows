// Signing in with Plex: a code to enter at plex.tv/link, then who's watching
// (a Plex Home's people, some behind a PIN), then which server, then the first
// of its addresses that answers. A port of the Apple app's PlexSignInModel.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Plex;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>Someone in the Plex Home, to pick; a lock when they have a PIN.</summary>
public sealed record PlexUserChoice(string Title, bool HasPin, IRelayCommand Pick);

/// <summary>One of the account's servers, to pick: its name, and whether it's the account's own.</summary>
public sealed record PlexServerChoice(string Name, string Detail, IRelayCommand Pick);

public partial class PlexSignInViewModel : ViewModelBase
{
    private readonly Action<StoredSession> signedIn;
    private readonly Action cancel;
    private CancellationTokenSource? linking;

    private string? accountToken;

    /// <summary>The account or Home user signing in, and their token.</summary>
    private PlexAccountUser? user;
    private string? userToken;

    /// <summary>The Home user picked, if the account has a Home.</summary>
    private PlexHomeUser? homeUser;

    /// <param name="signedIn">The signed-in session, once an address of the server answers.</param>
    /// <param name="cancel">Back to the sign-in screen.</param>
    public PlexSignInViewModel(Action<StoredSession> signedIn, Action cancel)
    {
        this.signedIn = signedIn;
        this.cancel = cancel;
        Start();
    }

    // ---- What the page shows: one of these at a time ----

    /// <summary>The code to enter at plex.tv/link, while one is waiting.</summary>
    [ObservableProperty] public partial string? Code { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsers))]
    public partial IReadOnlyList<PlexUserChoice> Users { get; private set; } = [];

    public bool HasUsers => Users.Count > 0;

    /// <summary>The Home user whose PIN is being asked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinPrompt))]
    public partial PlexHomeUser? PinUser { get; private set; }

    public string PinPrompt => PinUser is { } asked ? $"Enter {asked.Title}'s PIN" : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPinCommand))]
    public partial string Pin { get; set; } = "";

    /// <summary>A wrong PIN comes back to the PIN with a message.</summary>
    [ObservableProperty] public partial string? PinError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServers))]
    public partial IReadOnlyList<PlexServerChoice> Servers { get; private set; } = [];

    public bool HasServers => Servers.Count > 0;

    /// <summary>"Finding your servers…", "Connecting to Server…", or while starting.</summary>
    [ObservableProperty] public partial string? Progress { get; private set; }

    [ObservableProperty] public partial string? Failure { get; private set; }

    private void Show(string? code = null, IReadOnlyList<PlexUserChoice>? users = null, PlexHomeUser? pinUser = null,
        IReadOnlyList<PlexServerChoice>? servers = null, string? progress = null, string? failure = null)
    {
        Code = code;
        Users = users ?? [];
        PinUser = pinUser;
        Servers = servers ?? [];
        Progress = progress;
        Failure = failure;
    }

    // ---- Linking ----

    /// <summary>Shows a code and waits for it to be entered; a fresh code when one runs out.</summary>
    [RelayCommand]
    private void Start()
    {
        linking?.Cancel();
        var run = linking = new CancellationTokenSource();
        Show(progress: "Asking plex.tv for a code…");
        _ = LinkAsync(run.Token);
    }

    private async Task LinkAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pin = await PlexAccount.StartLinkAsync(ct);
                Show(code: pin.Code);
                var expires = DateTimeOffset.UtcNow.AddSeconds(pin.ExpiresIn ?? 900);
                while (DateTimeOffset.UtcNow < expires)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    string? token;
                    try { token = await PlexAccount.LinkedTokenAsync(pin, ct); }
                    catch (Exception error) when (error is JellyfinException or HttpRequestException) { continue; }
                    if (token is not null)
                    {
                        await LinkedAsync(token);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException)
        {
            Show(failure: $"Couldn't reach plex.tv. {error.Message}");
        }
    }

    private async Task LinkedAsync(string token)
    {
        accountToken = token;
        var people = await PlexAccount.HomeUsersAsync(token);
        if (people.Count > 1)
        {
            Show(users: people.Select(p => new PlexUserChoice(p.Title, p.Protected == true, new AsyncRelayCommand(() => PickAsync(p)))).ToList());
            return;
        }
        try
        {
            user = await PlexAccount.AccountAsync(token);
            userToken = token;
            await LoadServersAsync();
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException)
        {
            Show(failure: $"Couldn't read your Plex account. {error.Message}");
        }
    }

    // ---- Who's watching ----

    private Task PickAsync(PlexHomeUser person)
    {
        if (person.Protected != true) return SwitchToAsync(person, null);
        Pin = "";
        PinError = null;
        Show(pinUser: person);
        return Task.CompletedTask;
    }

    private bool CanSubmitPin() => Pin.Length > 0;

    /// <summary>An empty PIN never goes: plex.tv lets a profile switch to itself without one.</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitPin))]
    private Task SubmitPinAsync()
    {
        if (PinUser is not { } person) return Task.CompletedTask;
        var entered = Pin;
        Pin = "";
        return SwitchToAsync(person, entered);
    }

    private async Task SwitchToAsync(PlexHomeUser person, string? pin)
    {
        if (accountToken is null) return;
        PinError = null;
        try
        {
            var switched = await PlexAccount.SwitchUserAsync(person, pin, accountToken);
            user = switched;
            homeUser = person;
            userToken = switched.AuthToken ?? accountToken;
            await LoadServersAsync();
        }
        catch (JellyfinException refused) when (refused.StatusCode is 401 or 403)
        {
            PinError = "That PIN isn't right.";
            Show(pinUser: person);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException)
        {
            Show(failure: $"Couldn't switch to {person.Title}. {error.Message}");
        }
    }

    // ---- Which server ----

    private async Task LoadServersAsync()
    {
        if (userToken is null) return;
        Show(progress: "Finding your servers…");
        try
        {
            var found = await PlexAccount.ServersAsync(userToken);
            if (found.Count == 0) Show(failure: "This Plex account has no servers.");
            else if (found.Count == 1) await ConnectAsync(found[0]);
            else Show(servers: found.Select(s => new PlexServerChoice(s.Name, s.Owned == true ? "Your server" : "Shared with you", new AsyncRelayCommand(() => ConnectAsync(s)))).ToList());
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException)
        {
            Show(failure: $"Couldn't list your Plex servers. {error.Message}");
        }
    }

    private async Task ConnectAsync(PlexResource server)
    {
        if (userToken is null || user is null) return;
        Show(progress: $"Connecting to {server.Name}…");
        var token = server.AccessToken ?? userToken;
        var connections = server.RankedConnections;
        // A first connection can be slow (name lookups, secure set-up for
        // every address at once): one more go before giving up.
        var reached = await PlexConnectionRace.FastestAsync(connections, token)
            ?? await PlexConnectionRace.FastestAsync(connections, token);
        if (reached is null)
        {
            Show(failure: PlexErrors.Unreachable(server.Name));
            return;
        }
        signedIn(new StoredSession
        {
            ServerUrl = reached.Uri,
            ServerName = server.Name,
            UserId = user.Uuid,
            UserName = user.DisplayName,
            AccessToken = token,
            ServerKind = ServerKind.Plex,
            Plex = new PlexLink
            {
                AccountToken = userToken,
                MachineId = server.ClientIdentifier,
                Connections = connections,
                HomeUserUuid = homeUser?.Uuid,
                HomeUserHasPin = homeUser?.Protected ?? false,
                HomeToken = accountToken,
            },
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        linking?.Cancel();
        cancel();
    }
}
