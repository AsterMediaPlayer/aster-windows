using Aster.Core.Jellyfin;

namespace Aster.Core;

/// <summary>
/// Settings that follow the Jellyfin user from device to device, kept on
/// their server in the Apple app's own display preferences (see
/// <see cref="JellyfinClient.AppSettingsAsync"/>), under the same keys, so
/// they follow a user between the two apps. These are matters of taste: Up
/// Next, skipping intros, styled subtitles, song cards, theme music and
/// videos, disc menus. Settings tied to
/// the computer stay on it: streaming quality and full screen. Audio and
/// subtitle languages follow the user through their own Jellyfin settings.
///
/// The server's values are fetched at sign-in and when Settings opens. A
/// change to a synced setting is saved as it's made; the last change wins. A
/// setting the server doesn't have yet is sent up from the first device that
/// has chosen it, and a device that never chose one keeps the default rather
/// than pushing it over another device's choice.
/// </summary>
public static class SettingsSync
{
    private sealed record Setting(string Key, Func<AppSettings, bool> Get, Func<AppSettings, bool, AppSettings> With);

    private static readonly Setting[] Synced =
    [
        new("autoplayNext", s => s.AutoplayNext, (s, on) => s with { AutoplayNext = on }),
        new("autoSkipIntro", s => s.AutoSkipIntro, (s, on) => s with { AutoSkipIntro = on }),
        new("styledSubtitles", s => s.StyledSubtitles, (s, on) => s with { StyledSubtitles = on }),
        new("themeCards", s => s.ThemeCards, (s, on) => s with { ThemeCards = on }),
        new("themeMusic", s => s.ThemeMusic, (s, on) => s with { ThemeMusic = on }),
        new("themeVideos", s => s.ThemeVideos, (s, on) => s with { ThemeVideos = on }),
        new("openingSound", s => s.OpeningSound, (s, on) => s with { OpeningSound = on }),
        new("preferDiscMenu", s => s.PreferDiscMenu, (s, on) => s with { PreferDiscMenu = on }),
        new("discMenuSounds", s => s.DiscMenuSounds, (s, on) => s with { DiscMenuSounds = on }),
    ];

    private static readonly Lock Gate = new();

    /// <summary>What the server holds, as last fetched or saved, so a value just fetched isn't sent straight back.</summary>
    private static readonly Dictionary<string, string> ServerValues = [];

    /// <summary>
    /// Whose values <see cref="ServerValues"/> are (server and user), and
    /// whether they've been fetched; nothing is saved before they have, so a
    /// computer opening with old values can't overwrite newer ones.
    /// </summary>
    private static string? owner;
    private static bool hasPulled;

    /// <summary>Takes the server's values, and sends up any it hasn't got.</summary>
    public static async Task PullAsync(JellyfinClient client, CancellationToken ct = default)
    {
        Dictionary<string, string> remote;
        try { remote = await client.AppSettingsAsync(ct).ConfigureAwait(false); }
        catch (Exception error) when (IsServerError(error)) { return; }

        var settings = AppSettings.Current;
        var updated = settings;
        lock (Gate)
        {
            Follow(client);
            foreach (var setting in Synced)
            {
                if (remote.TryGetValue(setting.Key, out var value) && bool.TryParse(value, out var on))
                {
                    ServerValues[setting.Key] = value;
                    updated = setting.With(updated, on);
                }
            }
            hasPulled = true;
        }
        if (updated != settings) AppSettings.Save(updated);
        await SaveChangedAsync(client).ConfigureAwait(false);
    }

    /// <summary>Changes the settings here, and saves any synced one that changed on the server.</summary>
    public static Task ChangeAsync(JellyfinClient client, Func<AppSettings, AppSettings> change)
    {
        var before = AppSettings.Current;
        var after = change(before);
        var chosen = Synced.Where(s => s.Get(before) != s.Get(after)).Select(s => s.Key);
        AppSettings.Save(after with { Chosen = after.Chosen.Union(chosen).ToList() });
        return SaveChangedAsync(client);
    }

    /// <summary>Saves each setting chosen here that the server holds differently, once its values are known.</summary>
    private static async Task SaveChangedAsync(JellyfinClient client)
    {
        var settings = AppSettings.Current;
        Dictionary<string, string> changed;
        lock (Gate)
        {
            if (!hasPulled || owner != OwnerOf(client)) return;
            changed = Synced
                .Where(s => settings.Chosen.Contains(s.Key))
                .Select(s => (s.Key, Value: s.Get(settings) ? "true" : "false"))
                .Where(s => !string.Equals(ServerValues.GetValueOrDefault(s.Key), s.Value, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(s => s.Key, s => s.Value);
        }
        if (changed.Count == 0) return;
        try
        {
            await client.SaveAppSettingsAsync(changed).ConfigureAwait(false);
            lock (Gate)
            {
                foreach (var (key, value) in changed) ServerValues[key] = value;
            }
        }
        catch (Exception error) when (IsServerError(error)) { }
    }

    /// <summary>Follows <paramref name="client"/>'s user, forgetting another's values.</summary>
    private static void Follow(JellyfinClient client)
    {
        var id = OwnerOf(client);
        if (owner == id) return;
        owner = id;
        ServerValues.Clear();
        hasPulled = false;
    }

    private static string OwnerOf(JellyfinClient client) => $"{client.BaseUrl}|{client.UserId}";

    private static bool IsServerError(Exception error) =>
        error is JellyfinException or HttpRequestException or TimeoutException or OperationCanceledException;
}
