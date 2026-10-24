using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

public partial class SignInViewModel : ViewModelBase
{
    private readonly Action<JellyfinClient, StoredSession> signedIn;
    private CancellationTokenSource? quickConnect;

    public SignInViewModel(Action<JellyfinClient, StoredSession> signedIn) => this.signedIn = signedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickConnectCommand))]
    public partial string ServerAddress { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    /// <summary>Why sign-in failed, or why the user is back here.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>The Quick Connect code to enter on another device, while one is waiting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickConnecting))]
    public partial string? QuickConnectCode { get; set; }

    public bool IsQuickConnecting => QuickConnectCode is not null;

    // Another server's code means nothing, so changing the address drops it.
    partial void OnServerAddressChanged(string value) => CancelQuickConnect();

    private bool CanSignIn() => ServerAddress.Trim().Length > 0 && UserName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        Message = null;
        if (ServerUrl(ServerAddress) is not { } url)
        {
            Message = JellyfinException.InvalidUrl().Message;
            return;
        }
        try
        {
            var info = await new JellyfinClient(url).PublicSystemInfoAsync();
            // Jellyfin or Emby, from its own reply: the few routes that differ follow.
            var server = new JellyfinClient(url, kind: info.Kind);
            var result = await server.AuthenticateAsync(UserName.Trim(), Password);
            CancelQuickConnect();
            Finish(server, url, info, result);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            Message = Describe(error);
        }
    }

    private bool CanQuickConnect() => ServerAddress.Trim().Length > 0;

    /// <summary>
    /// Asks the server for a code, shows it, and checks every 2 seconds
    /// until it's approved on another device (in Jellyfin's user menu ▸
    /// Quick Connect), then signs in as whoever approved it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanQuickConnect))]
    private async Task QuickConnectAsync()
    {
        Message = null;
        if (ServerUrl(ServerAddress) is not { } url)
        {
            Message = JellyfinException.InvalidUrl().Message;
            return;
        }
        CancelQuickConnect();
        var cancel = quickConnect = new CancellationTokenSource();
        var ct = cancel.Token;
        try
        {
            var info = await new JellyfinClient(url).PublicSystemInfoAsync(ct);
            var server = new JellyfinClient(url, kind: info.Kind);
            if (info.Kind == ServerKind.Emby)
            {
                Message = "Emby has no Quick Connect: sign in with your user name and password.";
                return;
            }
            if (!await server.QuickConnectEnabledAsync(ct))
            {
                Message = "Quick Connect is turned off on this server. An administrator can turn it on in the Dashboard, under General.";
                return;
            }
            var initiated = await server.QuickConnectInitiateAsync(ct);
            QuickConnectCode = initiated.Code;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var state = await server.QuickConnectStateAsync(initiated.Secret, ct);
                if (!state.Authenticated) continue;
                var result = await server.AuthenticateWithQuickConnectAsync(initiated.Secret, ct);
                QuickConnectCode = null;
                Finish(server, url, info, result);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (ct.IsCancellationRequested) return;
            QuickConnectCode = null;
            // The server forgets a code after 10 minutes, and then doesn't know its secret.
            Message = error is JellyfinException { StatusCode: 404 }
                ? "The Quick Connect code expired. Choose Quick Connect for a new one."
                : $"Quick Connect failed: {Describe(error)}";
        }
        finally
        {
            if (quickConnect == cancel) quickConnect = null;
            cancel.Dispose();
        }
    }

    [RelayCommand]
    private void CancelQuickConnect()
    {
        quickConnect?.Cancel();
        quickConnect = null;
        QuickConnectCode = null;
    }

    private void Finish(JellyfinClient server, Uri url, PublicSystemInfo info, AuthenticationResult result)
    {
        var session = new StoredSession
        {
            ServerUrl = url,
            ServerName = info.ServerName,
            ServerKind = info.Kind,
            UserId = result.User.Id,
            UserName = result.User.Name,
            AccessToken = result.AccessToken,
        };
        session.Save();
        signedIn(server.SignedIn(result.AccessToken, result.User.Id), session);
    }

    private static string Describe(Exception error) =>
        error is HttpRequestException ? $"Couldn't reach the server: {error.Message}" : error.Message;

    /// <summary>
    /// What was typed as a server address, with http:// assumed when no
    /// scheme is given ("192.168.1.10:8096").
    /// </summary>
    internal static Uri? ServerUrl(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null;
    }
}
