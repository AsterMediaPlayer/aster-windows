// The Windows counterpart of the Apple app's Keychain wrapper: the signed-in
// session is kept in Windows Credential Manager (Control Panel ›
// Credential Manager › Windows Credentials, under "Aster/…"), encrypted to
// the Windows account. Linux keeps it with the Secret Service instead
// (SecretServiceStore); StoredSession picks.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Aster.Core.Jellyfin;

[SupportedOSPlatform("windows")]
public static class CredentialStore
{
    private const string TargetPrefix = "Aster/";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>
    /// The most one credential holds (CRED_MAX_CREDENTIAL_BLOB_SIZE). A
    /// longer secret (a Plex sign-in, with its tokens and every address, or
    /// many saved servers) goes in parts: "Aster/session", then
    /// "Aster/session/2" and on, read back in order.
    /// </summary>
    public const int PartSize = 2560;

    private static string Target(string account, int part) => part == 1 ? TargetPrefix + account : $"{TargetPrefix}{account}/{part}";

    public static void Save(byte[] data, string account)
    {
        var parts = Math.Max(1, (data.Length + PartSize - 1) / PartSize);
        for (var part = 1; part <= parts; part++)
        {
            var start = (part - 1) * PartSize;
            SaveOne(data.AsSpan(start, Math.Min(PartSize, data.Length - start)), Target(account, part));
        }
        // A shorter secret than before leaves no stale parts to be read after it.
        for (var part = parts + 1; DeleteOne(Target(account, part)); part++) { }
    }

    public static byte[]? Load(string account)
    {
        if (LoadOne(Target(account, 1)) is not { } first) return null;
        var all = new List<byte>(first);
        for (var part = 2; LoadOne(Target(account, part)) is { } next; part++) all.AddRange(next);
        return [.. all];
    }

    public static void Delete(string account)
    {
        for (var part = 1; DeleteOne(Target(account, part)) || part == 1; part++) { }
    }

    private static void SaveOne(ReadOnlySpan<byte> data, string target)
    {
        var blob = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        try
        {
            Marshal.Copy(data.ToArray(), 0, blob, data.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)data.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private static byte[]? LoadOne(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var data = new byte[credential.CredentialBlobSize];
            if (data.Length > 0) Marshal.Copy(credential.CredentialBlob, data, 0, data.Length);
            return data;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>False when there was none to delete.</summary>
    private static bool DeleteOne(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new System.ComponentModel.Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public ComFileTime LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>What survives a relaunch: where the server is and who is signed in.</summary>
public sealed record StoredSession
{
    public required Uri ServerUrl { get; init; }
    public string? ServerName { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required string AccessToken { get; init; }

    /// <summary>Jellyfin, Emby or Plex; sessions saved before Emby was supported have none, and are Jellyfin.</summary>
    public ServerKind? ServerKind { get; init; }

    /// <summary>A Plex sign-in's plex.tv side; null for Jellyfin and Emby.</summary>
    public PlexLink? Plex { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ServerKind Kind => ServerKind ?? Jellyfin.ServerKind.Jellyfin;

    private const string Account = "session";

    public static StoredSession? Load()
    {
        var json = Secrets.Read(Account);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<StoredSession>(json, JellyfinJson.Options); }
        catch (JsonException) { return null; }
    }

    public void Save() => Secrets.Write(Account, JsonSerializer.Serialize(this, JellyfinJson.Options));

    public static void Clear() => Secrets.Delete(Account);
}

/// <summary>A Plex sign-in's plex.tv side, kept with the session. The Apple app's PlexLink.</summary>
public sealed record PlexLink
{
    /// <summary>The plex.tv account's token (the Home user's, once one is picked).</summary>
    public required string AccountToken { get; init; }

    /// <summary>The server's machineIdentifier.</summary>
    public required string MachineId { get; init; }

    /// <summary>Every connection plex.tv listed, best first.</summary>
    public IReadOnlyList<PlexConnectionInfo> Connections { get; init; } = [];

    /// <summary>
    /// The Plex Home user signed in, and whether their profile has a PIN
    /// (which locking and switching then ask for). Null without a Home.
    /// </summary>
    public string? HomeUserUuid { get; init; }

    public bool? HomeUserHasPin { get; init; }

    /// <summary>
    /// The token from the plex.tv link code (the Home's account), which checks
    /// a PIN: a profile's own token switching to itself is let through without one.
    /// </summary>
    public string? HomeToken { get; init; }
}

/// <summary>One way to reach a Plex server.</summary>
public sealed record PlexConnectionInfo(Uri Uri, bool Local, bool Relay);

/// <summary>
/// A server signed in to, kept so the app can switch back to it without
/// signing in again. The Apple app's SavedServer.
/// </summary>
public sealed record SavedServer
{
    public required StoredSession Session { get; init; }

    /// <summary>One per server and user.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id => IdOf(Session);

    /// <summary>
    /// The server and user: by address, or a Plex server by its own id, since
    /// the address used to reach it can change.
    /// </summary>
    public static string IdOf(StoredSession session) => session.Plex is { } plex
        ? $"plex:{plex.MachineId}|{session.UserId}"
        : session.ServerUrl.AbsoluteUri + "|" + session.UserId;

    /// <summary>"Server", from its name, else its address.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Name => Session.ServerName ?? Session.ServerUrl.Host;

    /// <summary>Switching to it asks for the user's own password first.</summary>
    public bool AsksPasswordToSwitch { get; init; }

    /// <summary>
    /// Minutes away from Aster after which it asks for the password to
    /// carry on with this server; null for never.
    /// </summary>
    public int? LockAfterMinutes { get; init; }

    /// <summary>The Lock After choices offered, in minutes.</summary>
    public static IReadOnlyList<int> LockChoices { get; } = [5, 15, 30, 60, 240];

    /// <summary>"Never", "5 Minutes", "1 Hour", "4 Hours".</summary>
    public static string LockTitle(int? minutes) => minutes switch
    {
        null or <= 0 => "Never",
        1 => "1 Minute",
        60 => "1 Hour",
        >= 60 when minutes % 60 == 0 => $"{minutes / 60} Hours",
        _ => $"{minutes} Minutes",
    };

    /// <summary>What switching and Lock After ask for: the user's password on the server, or a Plex Home profile's PIN.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CredentialName => Session.Kind == ServerKind.Plex ? "PIN" : "password";

    /// <summary>
    /// Whether there's anything to ask for: a Plex profile without a PIN, or
    /// signed in without the Home's token (which checks it), can't be locked.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanBeLocked => Session.Kind != ServerKind.Plex || (Session.Plex is { HomeUserHasPin: true, HomeToken: not null, HomeUserUuid: not null });

    /// <summary>Whether coming back at <paramref name="now"/>, having left at <paramref name="leftAt"/>, asks for the password.</summary>
    public bool LocksAfter(DateTimeOffset? leftAt, DateTimeOffset now) =>
        CanBeLocked && LockAfterMinutes is { } minutes && leftAt is { } left && now - left >= TimeSpan.FromMinutes(minutes);

    /// <summary>
    /// Checks the password with the server itself, for switching to it or
    /// unlocking it: the fresh sign-in to carry on with (a new sign-in on the
    /// same device replaces the old one's token on Jellyfin).
    /// </summary>
    public async Task<StoredSession> SignInAgainAsync(string password, CancellationToken ct = default)
    {
        // Plex: the Home profile's PIN, checked by plex.tv by switching to it.
        if (Session.Kind == ServerKind.Plex)
        {
            if (Session.Plex is not { HomeUserUuid: { } uuid, HomeToken: { } homeToken } link) throw JellyfinException.Explained(null, Plex.PlexErrors.NotBuilt);
            // plex.tv lets a profile switch to itself with no PIN at all, so an empty one is never sent.
            if (password.Length == 0) throw JellyfinException.Explained(null, Plex.PlexErrors.PinNeeded);
            Plex.PlexAccountUser switched;
            try
            {
                switched = await Plex.PlexAccount.SwitchUserAsync(new Plex.PlexHomeUser { Uuid = uuid, Title = Session.UserName }, password, homeToken, ct).ConfigureAwait(false);
            }
            catch (JellyfinException refused) when (refused.StatusCode is 401 or 403)
            {
                throw JellyfinException.Explained(401, Plex.PlexErrors.WrongPin);
            }
            return Session with { Plex = link with { AccountToken = switched.AuthToken ?? link.AccountToken } };
        }
        var server = new JellyfinClient(Session.ServerUrl, kind: Session.Kind);
        var result = await server.AuthenticateAsync(Session.UserName, password, ct);
        return Session with { UserId = result.User.Id, UserName = result.User.Name, AccessToken = result.AccessToken };
    }
}

/// <summary>
/// When Aster was last left (its window put aside, or closed), for Lock
/// After, and the server it locked: kept in files, so neither is got past by
/// quitting.
/// </summary>
public static class AwayClock
{
    private static string FilePath => Path.Combine(AppPaths.Data, "left-at");
    private static string LockedPath => Path.Combine(AppPaths.Data, "locked-server");

    /// <summary>The saved server (<see cref="SavedServer.Id"/>) locked until its password is given.</summary>
    public static string? LockedServer
    {
        get
        {
            try { return File.Exists(LockedPath) ? File.ReadAllText(LockedPath) : null; }
            catch (IOException) { return null; }
        }
        set
        {
            try
            {
                if (value is null) File.Delete(LockedPath);
                else File.WriteAllText(LockedPath, value);
            }
            catch (IOException) { }
        }
    }

    /// <summary>When Aster was left, if it's away (or was when it closed).</summary>
    public static DateTimeOffset? LeftAt
    {
        get
        {
            try { return File.Exists(FilePath) && DateTimeOffset.TryParse(File.ReadAllText(FilePath), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var left) ? left : null; }
            catch (IOException) { return null; }
        }
    }

    /// <summary>The clock starts, unless it's already running (a closing app keeps when it was first left).</summary>
    public static void NoteLeft()
    {
        if (LeftAt is not null) return;
        try { File.WriteAllText(FilePath, DateTimeOffset.UtcNow.ToString("O")); }
        catch (IOException) { }
    }

    /// <summary>Back, and not locked (or unlocked): the clock stops.</summary>
    public static void Clear()
    {
        try { File.Delete(FilePath); }
        catch (IOException) { }
    }
}

/// <summary>
/// The servers signed in to, kept as the session in use is (beside it, in
/// Credential Manager, the Secret Service or the fallback file), so they
/// stay signed in to switch between.
/// </summary>
public static class SavedServers
{
    private const string Account = "servers";

    public static IReadOnlyList<SavedServer> Load()
    {
        var json = Secrets.Read(Account);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<SavedServer>>(json, JellyfinJson.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// When the list can't be kept (Credential Manager or the Secret Service
    /// turning it down), the list as it was stays; the session in use is kept
    /// on its own either way.
    /// </summary>
    public static void Save(IReadOnlyList<SavedServer> servers)
    {
        try { Secrets.Write(Account, JsonSerializer.Serialize(servers, JellyfinJson.Options)); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// The list, starting from the session in use when nothing's saved yet
    /// (signed in before servers were saved: that one's the first).
    /// </summary>
    public static IReadOnlyList<SavedServer> LoadOrStart(StoredSession? current)
    {
        var servers = Load();
        if (servers.Count > 0 || current is null) return servers;
        IReadOnlyList<SavedServer> first = [new SavedServer { Session = current }];
        Save(first);
        return first;
    }

    /// <summary>
    /// The list with <paramref name="session"/> added, or its entry brought up
    /// to date (a fresh sign-in to the same server and user), keeping its
    /// password and Lock After settings; not saved.
    /// </summary>
    public static IReadOnlyList<SavedServer> Remember(IReadOnlyList<SavedServer> servers, StoredSession session)
    {
        var id = SavedServer.IdOf(session);
        return servers.Any(s => s.Id == id)
            ? Change(servers, id, s => s with { Session = session })
            : [.. servers, new SavedServer { Session = session }];
    }

    /// <summary>The list with the server <paramref name="id"/> changed; not saved.</summary>
    public static IReadOnlyList<SavedServer> Change(IReadOnlyList<SavedServer> servers, string id, Func<SavedServer, SavedServer> change) =>
        servers.Select(s => s.Id == id ? change(s) : s).ToList();

    /// <summary>The list without the server <paramref name="id"/>; not saved.</summary>
    public static IReadOnlyList<SavedServer> Forget(IReadOnlyList<SavedServer> servers, string id) =>
        servers.Where(s => s.Id != id).ToList();
}

/// <summary>
/// Small secrets kept by name: in Windows Credential Manager, with the
/// Secret Service on Linux, else in a file only this user can read (as many
/// Linux apps keep their tokens), named after them.
/// </summary>
internal static class Secrets
{
    private static string FallbackFile(string account) => Path.Combine(AppPaths.Data, account + ".json");

    public static string? Read(string account)
    {
        if (OperatingSystem.IsWindows()) return CredentialStore.Load(account) is { } data ? System.Text.Encoding.UTF8.GetString(data) : null;
        if (OperatingSystem.IsLinux() && SecretServiceStore.IsAvailable)
        {
            try
            {
                if (SecretServiceStore.Load(account) is { } secret) return secret;
            }
            catch (InvalidOperationException) { }
        }
        try { return File.Exists(FallbackFile(account)) ? File.ReadAllText(FallbackFile(account)) : null; }
        catch (IOException) { return null; }
    }

    public static void Write(string account, string json)
    {
        if (OperatingSystem.IsWindows())
        {
            CredentialStore.Save(System.Text.Encoding.UTF8.GetBytes(json), account);
            return;
        }
        if (OperatingSystem.IsLinux() && SecretServiceStore.IsAvailable)
        {
            try
            {
                SecretServiceStore.Save(json, account);
                DeleteFallback(account);
                return;
            }
            catch (InvalidOperationException) { }
        }
        var file = FallbackFile(account);
        Directory.CreateDirectory(AppPaths.Data);
        File.WriteAllText(file, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(file, json);
    }

    public static void Delete(string account)
    {
        if (OperatingSystem.IsWindows())
        {
            CredentialStore.Delete(account);
            return;
        }
        if (OperatingSystem.IsLinux() && SecretServiceStore.IsAvailable)
        {
            try { SecretServiceStore.Delete(account); }
            catch (InvalidOperationException) { }
        }
        DeleteFallback(account);
    }

    private static void DeleteFallback(string account)
    {
        try { File.Delete(FallbackFile(account)); }
        catch (IOException) { }
    }
}
