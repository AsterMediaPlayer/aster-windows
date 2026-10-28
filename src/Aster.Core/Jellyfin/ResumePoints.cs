// Keeps the server's resume point current while something plays. Jellyfin
// follows the progress reports sent every ten seconds, but saves the
// position as the user's resume point only when playback starts and when
// it's told playback stopped. When the app never gets to say it stopped
// (it crashed, or was ended from Task Manager) the resume point would fall
// back to where that viewing started. So the position is also saved
// directly: every 30 seconds, as soon as it's paused somewhere new, and
// when the app closes.
//
// A direct save skips the server's own rules, so they're followed here,
// with Jellyfin's defaults: no resume point in a title's first 5%, nor in
// one under five minutes long, and past 90% it's left to the server, which
// counts it watched.

using Aster.Core.Server;

namespace Aster.Core.Jellyfin;

public sealed class ResumePoints
{
    public static ResumePoints Shared { get; } = new();

    public enum Saving
    {
        WhenDue,
        Now,
        Never,
    }

    /// <summary>How often a playing item's resume point is saved.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Jellyfin's defaults for where a resume point is kept: from 5% in to
    /// 90%, in a title at least five minutes long.
    /// </summary>
    public const double MinimumPercent = 5.0;
    public const double MaximumPercent = 90.0;
    public const long MinimumRunTime = 5L * 60 * 10_000_000;

    /// <summary>
    /// Whether <paramref name="ticks"/> into a title <paramref name="runTime"/>
    /// long makes a resume point; without a running time, any position past
    /// the start does.
    /// </summary>
    public static bool KeepsResumePoint(long ticks, long? runTime)
    {
        if (ticks <= 0) return false;
        if (runTime is not > 0) return true;
        if (runTime < MinimumRunTime) return false;
        var percent = (double)ticks / runTime.Value * 100;
        return percent is >= MinimumPercent and <= MaximumPercent;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, (DateTimeOffset Date, long Ticks)> lastSaved = [];
    private readonly Dictionary<string, Task> inFlight = [];

    /// <summary>
    /// Saves the report's position as the resume point when it's due, or at
    /// once with <paramref name="force"/>. Live streams, which can't resume,
    /// are skipped, and so is a position outside the resume rules.
    /// </summary>
    public Task ProgressAsync(PlaybackReport report, IMediaServer client, bool force)
    {
        if (!report.CanSeek || !KeepsResumePoint(report.PositionTicks, report.RunTimeTicks)) return Task.CompletedTask;
        var itemId = report.ItemId;
        var ticks = report.PositionTicks;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (!force && lastSaved.TryGetValue(itemId, out var last))
            {
                var due = now - last.Date >= Interval;
                var pausedSomewhereNew = report.IsPaused && last.Ticks != ticks;
                if (!due && !pausedSomewhereNew) return Task.CompletedTask;
            }
            lastSaved[itemId] = (now, ticks);
            // Each save waits for the one before, so they land in order.
            var save = SaveAfter(inFlight.GetValueOrDefault(itemId), client, itemId, ticks);
            inFlight[itemId] = save;
            return save;
        }
    }

    private static async Task SaveAfter(Task? previous, IMediaServer client, string itemId, long ticks)
    {
        if (previous is not null) await previous.ConfigureAwait(false);
        try { await client.SaveResumePositionAsync(itemId, ticks).ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>
    /// Playback of the item is ending: waits for a save still on its way, so
    /// the stop report (which may mark it watched) is the last word.
    /// </summary>
    public async Task StoppingAsync(string itemId)
    {
        Task? pending;
        lock (gate)
        {
            pending = inFlight.GetValueOrDefault(itemId);
            inFlight.Remove(itemId);
            lastSaved.Remove(itemId);
        }
        if (pending is not null) await pending.ConfigureAwait(false);
    }
}
