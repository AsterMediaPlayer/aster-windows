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

    public static void Save(byte[] data, string account)
    {
        var blob = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, blob, data.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetPrefix + account,
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

    public static byte[]? Load(string account)
    {
        if (!CredRead(TargetPrefix + account, CredTypeGeneric, 0, out var pointer)) return null;
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

    public static void Delete(string account)
    {
        if (!CredDelete(TargetPrefix + account, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
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

    /// <summary>Jellyfin or Emby; sessions saved before Emby was supported have none, and are Jellyfin.</summary>
    public ServerKind? ServerKind { get; init; }

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

/// <summary>
/// A server signed in to, kept so the app can switch back to it without
/// signing in again. The Apple app's SavedServer.
/// </summary>
public sealed record SavedServer
{
    public required StoredSession Session { get; init; }

    /// <summary>One per server and user.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id => IdOf(Session.ServerUrl, Session.UserId);

    public static string IdOf(Uri url, string userId) => url.AbsoluteUri + "|" + userId;

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

    /// <summary>Whether coming back at <paramref name="now"/>, having left at <paramref name="leftAt"/>, asks for the password.</summary>
    public bool LocksAfter(DateTimeOffset? leftAt, DateTimeOffset now) =>
        LockAfterMinutes is { } minutes && leftAt is { } left && now - left >= TimeSpan.FromMinutes(minutes);

    /// <summary>
    /// Checks the password with the server itself, for switching to it or
    /// unlocking it: the fresh sign-in to carry on with (a new sign-in on the
    /// same device replaces the old one's token on Jellyfin).
    /// </summary>
    public async Task<StoredSession> SignInAgainAsync(string password, CancellationToken ct = default)
    {
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
    /// Credential Manager keeps 2.5 KB a credential, room for about eight
    /// servers; past that, the list as it was stays (the session in use is
    /// kept on its own either way).
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
        var id = SavedServer.IdOf(session.ServerUrl, session.UserId);
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
