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

    /// <summary>
    /// Where a desktop without Credential Manager or a Secret Service keeps it:
    /// a file only this user can read, as many Linux apps keep their tokens.
    /// </summary>
    private static string FallbackFile => Path.Combine(AppPaths.Data, "session.json");

    public static StoredSession? Load()
    {
        var json = Read();
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<StoredSession>(json, JellyfinJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>The saved session as JSON: Credential Manager on Windows, the Secret Service on Linux, else the fallback file.</summary>
    private static string? Read()
    {
        if (OperatingSystem.IsWindows()) return CredentialStore.Load(Account) is { } data ? System.Text.Encoding.UTF8.GetString(data) : null;
        if (OperatingSystem.IsLinux() && SecretServiceStore.IsAvailable)
        {
            try
            {
                if (SecretServiceStore.Load(Account) is { } secret) return secret;
            }
            catch (InvalidOperationException) { }
        }
        try { return File.Exists(FallbackFile) ? File.ReadAllText(FallbackFile) : null; }
        catch (IOException) { return null; }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JellyfinJson.Options);
        if (OperatingSystem.IsWindows())
        {
            CredentialStore.Save(System.Text.Encoding.UTF8.GetBytes(json), Account);
            return;
        }
        if (OperatingSystem.IsLinux() && SecretServiceStore.IsAvailable)
        {
            try
            {
                SecretServiceStore.Save(json, Account);
                DeleteFallback();
                return;
            }
            catch (InvalidOperationException) { }
        }
        Directory.CreateDirectory(AppPaths.Data);
        File.WriteAllText(FallbackFile, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FallbackFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(FallbackFile, json);
    }

    public static void Clear()
    {
        if (OperatingSystem.IsWindows())
        {
            CredentialStore.Delete(Account);
            return;
        }
        if (OperatingSystem.IsLinux() && SecretServiceStore.IsAvailable)
        {
            try { SecretServiceStore.Delete(Account); }
            catch (InvalidOperationException) { }
        }
        DeleteFallback();
    }

    private static void DeleteFallback()
    {
        try { File.Delete(FallbackFile); }
        catch (IOException) { }
    }
}
