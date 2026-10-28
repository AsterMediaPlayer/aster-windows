using System.Runtime.Versioning;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

[SupportedOSPlatform("windows")]
public class CredentialStoreTests
{
    [Fact]
    public void SavesLoadsAndDeletes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(CredentialStore.Load(account));
            CredentialStore.Save([1, 2, 3], account);
            CredentialStore.Save([4, 5, 6, 7], account);
            Assert.Equal([4, 5, 6, 7], CredentialStore.Load(account));
        }
        finally
        {
            CredentialStore.Delete(account);
        }
        Assert.Null(CredentialStore.Load(account));
        // Deleting what isn't there is fine.
        CredentialStore.Delete(account);
    }

    /// <summary>A secret longer than one credential holds (a Plex sign-in) goes in parts, and comes back whole.</summary>
    [Fact]
    public void LongSecretsGoInParts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");
        var longer = Enumerable.Range(0, CredentialStore.PartSize * 2 + 100).Select(i => (byte)(i % 251)).ToArray();
        try
        {
            CredentialStore.Save(longer, account);
            Assert.Equal(longer, CredentialStore.Load(account));
            // Shorter again: the old parts don't come back after it.
            CredentialStore.Save([9, 8, 7], account);
            Assert.Equal([9, 8, 7], CredentialStore.Load(account));
            CredentialStore.Save(longer, account);
        }
        finally
        {
            CredentialStore.Delete(account);
        }
        Assert.Null(CredentialStore.Load(account));
        Assert.Null(CredentialStore.Load(account + "/2"));
    }
}
