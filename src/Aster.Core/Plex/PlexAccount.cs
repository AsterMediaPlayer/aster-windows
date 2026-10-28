// Signing in through plex.tv: a link code the viewer enters at plex.tv/link,
// the account's servers (its own and friends' shared ones, each with its own
// token and addresses), and Plex Home users to pick from, some behind a PIN.
// A port of the Apple app's PlexAccount.swift.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Plex;

/// <summary>A link code: shown to the viewer, then asked about until plex.tv says it was entered.</summary>
public sealed record PlexPin
{
    public long Id { get; init; }
    public string Code { get; init; } = "";

    /// <summary>Set once the code has been entered at plex.tv/link.</summary>
    public string? AuthToken { get; init; }

    /// <summary>Seconds the code lasts.</summary>
    public int? ExpiresIn { get; init; }
}

/// <summary>A plex.tv account, or a Home user switched to.</summary>
public sealed record PlexAccountUser
{
    public long Id { get; init; }
    public string Uuid { get; init; } = "";
    public string? Username { get; init; }
    public string? Title { get; init; }
    public string? Thumb { get; init; }
    public string? AuthToken { get; init; }

    public string DisplayName => Title ?? Username ?? "Plex";
}

/// <summary>One of the account's devices that is a server.</summary>
public sealed record PlexResource
{
    public string Name { get; init; } = "";
    public string ClientIdentifier { get; init; } = "";
    public string? Provides { get; init; }
    public string? ProductVersion { get; init; }

    /// <summary>The token for this server, for this account: a shared server's is its own, not the account's.</summary>
    public string? AccessToken { get; init; }

    public bool? Owned { get; init; }
    public IReadOnlyList<Connection> Connections { get; init; } = [];

    public bool IsServer => Provides?.Split(',').Contains("server") == true;

    public sealed record Connection
    {
        public string Uri { get; init; } = "";
        public string? Address { get; init; }
        public int? Port { get; init; }
        public bool? Local { get; init; }
        public bool? Relay { get; init; }
    }

    /// <summary>
    /// Its connections, best first: on the home network, then over the
    /// internet, then through Plex's relay. Each home-network address is also
    /// tried as plain HTTP by its number, as Plex's own apps do: the secure
    /// plex.direct name needs a lookup that a home router can be slow with,
    /// or refuse.
    /// </summary>
    public IReadOnlyList<PlexConnectionInfo> RankedConnections
    {
        get
        {
            var all = new List<PlexConnectionInfo>();
            foreach (var connection in Connections)
            {
                if (!System.Uri.TryCreate(connection.Uri, UriKind.Absolute, out var uri)) continue;
                var local = connection.Local ?? false;
                all.Add(new PlexConnectionInfo(uri, local, connection.Relay ?? false));
                if (local && uri.Scheme == "https" && connection.Address is { } address && connection.Port is { } port
                    && System.Uri.TryCreate($"http://{address}:{port.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute, out var plain))
                {
                    all.Add(new PlexConnectionInfo(plain, Local: true, Relay: false));
                }
            }
            // Stable: within a rank, plex.tv's order (secure first) is kept.
            return all.Select((c, i) => (c, i)).OrderBy(p => PlexConnectionRace.Rank(p.c)).ThenBy(p => p.i).Select(p => p.c).ToList();
        }
    }
}

/// <summary>A Plex Home's people.</summary>
public sealed record PlexHomeUser
{
    public long Id { get; init; }
    public string Uuid { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Username { get; init; }
    public string? Thumb { get; init; }
    public bool? Admin { get; init; }
    public bool? Restricted { get; init; }

    /// <summary>A PIN is asked before switching to them.</summary>
    public bool? Protected { get; init; }
}

public static class PlexAccount
{
    public static readonly Uri Base = new("https://plex.tv/");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static async Task<T> SendAsync<T>(string path, HttpMethod? method = null, IReadOnlyDictionary<string, string>? query = null, string? token = null, CancellationToken ct = default)
    {
        var pairs = (query ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}").ToList();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(Base, path + (pairs.Count > 0 ? "?" + string.Join("&", pairs) : "")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in PlexDevice.Headers(token)) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        // The answer, never the address (it can carry a PIN).
        System.Diagnostics.Debug.WriteLine($"plex.tv: {request.Method} {path} -> {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode) throw JellyfinException.Explained((int)response.StatusCode, PlexErrors.Http((int)response.StatusCode));
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<T>(data, PlexJson.Options)!; }
        catch (JsonException error) { throw JellyfinException.Decoding(error.Message, error); }
    }

    /// <summary>A new link code to show.</summary>
    public static Task<PlexPin> StartLinkAsync(CancellationToken ct = default) =>
        SendAsync<PlexPin>("api/v2/pins", HttpMethod.Post, new Dictionary<string, string> { ["strong"] = "false" }, ct: ct);

    /// <summary>The account's token once the code has been entered; null until then.</summary>
    public static async Task<string?> LinkedTokenAsync(PlexPin pin, CancellationToken ct = default) =>
        (await SendAsync<PlexPin>($"api/v2/pins/{pin.Id.ToString(CultureInfo.InvariantCulture)}", ct: ct).ConfigureAwait(false)).AuthToken;

    public static Task<PlexAccountUser> AccountAsync(string token, CancellationToken ct = default) =>
        SendAsync<PlexAccountUser>("api/v2/user", token: token, ct: ct);

    /// <summary>The servers this account can use, its own first.</summary>
    public static async Task<IReadOnlyList<PlexResource>> ServersAsync(string token, CancellationToken ct = default)
    {
        var resources = await SendAsync<List<PlexResource>>("api/v2/resources", query: new Dictionary<string, string>
        {
            ["includeHttps"] = "1",
            ["includeRelay"] = "1",
        }, token: token, ct: ct).ConfigureAwait(false);
        return OwnFirst(resources);
    }

    /// <summary>Only the servers among the account's devices, its own before friends' shared ones.</summary>
    public static IReadOnlyList<PlexResource> OwnFirst(IEnumerable<PlexResource> resources) =>
        resources.Where(r => r.IsServer).OrderBy(r => r.Owned == true ? 0 : 1).ToList();

    private sealed record Home
    {
        public IReadOnlyList<PlexHomeUser> Users { get; init; } = [];
    }

    /// <summary>The account's Home users; none for an account without a Home.</summary>
    public static async Task<IReadOnlyList<PlexHomeUser>> HomeUsersAsync(string token, CancellationToken ct = default)
    {
        try { return (await SendAsync<Home>("api/v2/home/users", token: token, ct: ct).ConfigureAwait(false)).Users; }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException) { return []; }
    }

    /// <summary>The Home users in a plex.tv reply, for tests.</summary>
    public static IReadOnlyList<PlexHomeUser> HomeUsers(string json) =>
        JsonSerializer.Deserialize<Home>(json, PlexJson.Options)?.Users ?? [];

    /// <summary>Switches to a Home user, with their PIN if they have one: their own account token.</summary>
    public static Task<PlexAccountUser> SwitchUserAsync(PlexHomeUser user, string? pin, string token, CancellationToken ct = default) =>
        SendAsync<PlexAccountUser>($"api/v2/home/users/{user.Uuid}/switch", HttpMethod.Post,
            pin is null ? null : new Dictionary<string, string> { ["pin"] = pin }, token, ct);
}

/// <summary>
/// Finds which of a server's addresses answers. All are tried at once: a
/// server can list a dozen addresses (one per network it's on), most of which
/// don't answer from here, and one at a time would take minutes.
/// </summary>
public static class PlexConnectionRace
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Home network first, then the internet, then Plex's relay.</summary>
    public static int Rank(PlexConnectionInfo connection) => connection.Relay ? 2 : connection.Local ? 0 : 1;

    /// <summary>
    /// The best address that answers, or null if none does. A home-network
    /// one is taken as soon as it answers; otherwise every address gets its
    /// chance, and the best of those that answered wins.
    /// </summary>
    public static async Task<PlexConnectionInfo?> FastestAsync(IReadOnlyList<PlexConnectionInfo> connections, string token, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        var pending = connections.Select(async c => await AnswersAsync(c.Uri, token, race.Token).ConfigureAwait(false) ? c : null).ToList();
        PlexConnectionInfo? best = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (await done.ConfigureAwait(false) is not { } answered) continue;
            if (Rank(answered) == 0)
            {
                await race.CancelAsync().ConfigureAwait(false);
                return answered;
            }
            if (best is null || Rank(answered) < Rank(best)) best = answered;
        }
        return best;
    }

    private static async Task<bool> AnswersAsync(Uri uri, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "identity"));
            foreach (var (name, value) in PlexDevice.Headers(token)) request.Headers.TryAddWithoutValidation(name, value);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (Exception) { return false; }
    }
}
