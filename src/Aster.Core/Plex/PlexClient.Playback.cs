// Playing from Plex, answered in the shapes the player was written for: the
// file (Plex's "part") as the source that plays as it is, and Plex's
// universal transcoder as the converted stream. Progress goes to Plex's
// timeline, which also keeps the resume point and marks what's finished as
// watched. A port of the Apple app's PlexClient+Playback.swift; mpv reads
// every track inside the file itself, so only a converted stream needs
// Plex's help with subtitles (a sidecar file fetched, anything else burned in).

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;

namespace Aster.Core.Plex;

public sealed partial class PlexClient
{
    private const string LibraryIdentifier = "com.plexapp.plugins.library";

    /// <summary>The item's file and tracks, and the transcoder's stream for when it isn't played as it is.</summary>
    public async Task<PlaybackInfoResponse> PlaybackInfoAsync(string itemId, long startTicks, int maxBitrate, string? mediaSourceId = null, int? audioStreamIndex = null,
        int? subtitleStreamIndex = null, bool allowDirect = true, bool mostCompatible = false, DeviceProfile? profile = null, CancellationToken ct = default)
    {
        if (await MetadataAsync(itemId, ct).ConfigureAwait(false) is not { Media: [var media, ..] } || media.Part is not [var part, ..])
            throw JellyfinException.Explained(404, PlexErrors.Http(404));
        var session = Guid.NewGuid().ToString();
        var streams = part.Stream ?? [];
        var direct = allowDirect && !mostCompatible;

        // The transcoder plays the part's selected tracks: choose them first
        // when it will be used (Plex's own apps do the same, which also makes
        // them the title's default on the server).
        var burnIn = subtitleStreamIndex is { } wantedSubtitle
            ? streams.FirstOrDefault(s => s.StreamType == 3 && s.Index == wantedSubtitle && !IsFetchableText(s))
            : null;
        if (!direct && (audioStreamIndex is not null || subtitleStreamIndex is not null))
        {
            var audio = streams.FirstOrDefault(s => s.StreamType == 2 && s.Index == audioStreamIndex);
            var choice = new Dictionary<string, string?>
            {
                ["subtitleStreamID"] = burnIn is null ? "0" : IdText(burnIn.Id),
                ["audioStreamID"] = audio is null ? null : IdText(audio.Id),
            };
            try { await SendAsync(Request(Url($"/library/parts/{part.Id}", choice), HttpMethod.Put), ct).ConfigureAwait(false); }
            catch (JellyfinException) { }
        }

        var transcode = TranscodeQuery(itemId, session, startTicks, maxBitrate,
            keepsSurround: !allowDirect && !mostCompatible, mostCompatible, burnIn?.Index);
        // Plex refuses to start a conversion ("session lacking decision")
        // unless the same session first asked for its decision, with the same
        // settings, as Plex's own apps do.
        if (!direct)
        {
            try { await SendAsync(Request(Url("/video/:/transcode/universal/decision", transcode)), ct).ConfigureAwait(false); }
            catch (JellyfinException) { }
        }

        var source = new MediaSourceInfo
        {
            Id = IdText(part.Id),
            Container = (part.Container ?? media.Container ?? "").ToLowerInvariant(),
            RunTimeTicks = (part.Duration ?? media.Duration) * PlexMapper.TicksPerMillisecond,
            Bitrate = media.Bitrate * 1000,
            Size = part.Size,
            SupportsDirectPlay = direct,
            SupportsDirectStream = direct,
            SupportsTranscoding = true,
            MediaStreams = streams.Select((stream, position) => PlaybackStream(stream, position)).ToList(),
            // Plex's own choices for this account, as Jellyfin's defaults are its user's.
            DefaultAudioStreamIndex = (streams.FirstOrDefault(s => s.StreamType == 2 && s.Selected == true)
                ?? streams.FirstOrDefault(s => s.StreamType == 2 && s.Default == true)
                ?? streams.FirstOrDefault(s => s.StreamType == 2))?.Index,
            DefaultSubtitleStreamIndex = streams.Select((s, position) => (Stream: s, Index: PlaybackIndex(s, position)))
                .Where(s => s.Stream.StreamType == 3 && s.Stream.Selected == true)
                .Select(s => (int?)s.Index).FirstOrDefault() ?? -1,
            TranscodingUrl = Url("/video/:/transcode/universal/start.m3u8", transcode).AbsoluteUri,
            TranscodingContainer = "ts",
            TranscodingSubProtocol = "hls",
        };
        return new PlaybackInfoResponse { MediaSources = [source], PlaySessionId = session };
    }

    /// <summary>The part's file, with the token in the address (players can't send headers).</summary>
    public Uri DirectStreamUrl(string itemId, string mediaSourceId, string? playSessionId) =>
        Url($"/library/parts/{mediaSourceId}/file", new Dictionary<string, string?>
        {
            ["X-Plex-Token"] = AccessToken,
            ["X-Plex-Client-Identifier"] = PlexDevice.ClientIdentifier,
            ["X-Plex-Session-Identifier"] = playSessionId,
        });

    /// <summary>Needs the title's file id, which this doesn't have; the openings it's for come later on Plex.</summary>
    public Uri VideoStreamUrl(string itemId) => throw new NotSupportedException(PlexErrors.NotBuilt);

    public Uri? AbsoluteUrl(string serverRelative)
    {
        if (serverRelative.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Uri.TryCreate(serverRelative, UriKind.Absolute, out var full) ? full : null;
        var root = BaseUrl.GetLeftPart(UriPartial.Authority);
        return Uri.TryCreate(root + (serverRelative.StartsWith('/') ? serverRelative : "/" + serverRelative), UriKind.Absolute, out var url) ? url : null;
    }

    public Task ReportStartAsync(PlaybackReport report) => TimelineAsync(report, report.IsPaused ? "paused" : "playing");

    public async Task ReportProgressAsync(PlaybackReport report, ResumePoints.Saving saving = ResumePoints.Saving.WhenDue)
    {
        await TimelineAsync(report, report.IsPaused ? "paused" : "playing").ConfigureAwait(false);
        if (saving == ResumePoints.Saving.Never) return;
        await ResumePoints.Shared.ProgressAsync(report, this, force: saving == ResumePoints.Saving.Now).ConfigureAwait(false);
    }

    public async Task ReportStoppedAsync(PlaybackReport report)
    {
        // A resume point still on its way mustn't land after the stop.
        await ResumePoints.Shared.StoppingAsync(report.ItemId).ConfigureAwait(false);
        await TimelineAsync(report, "stopped").ConfigureAwait(false);
    }

    /// <summary>Plex's own resume point, as a timeline report sets it.</summary>
    public async Task SaveResumePositionAsync(string itemId, long ticks, CancellationToken ct = default) =>
        await SendAsync(Request(Url("/:/progress", new Dictionary<string, string?>
        {
            ["key"] = itemId,
            ["identifier"] = LibraryIdentifier,
            ["time"] = IdText(ticks / PlexMapper.TicksPerMillisecond),
            ["state"] = "stopped",
        })), ct).ConfigureAwait(false);

    public async Task StopTranscodingAsync(string playSessionId)
    {
        try { await SendAsync(Request(Url("/video/:/transcode/universal/stop", new Dictionary<string, string?> { ["session"] = playSessionId })), default).ConfigureAwait(false); }
        catch (JellyfinException) { }
    }

    /// <summary>A subtitle file beside the video (tracks inside it Plex won't hand out).</summary>
    public async Task<IReadOnlyList<SubtitleCue>> SubtitleCuesAsync(string itemId, string mediaSourceId, int streamIndex, CancellationToken ct = default)
    {
        var metadata = await MetadataAsync(itemId, ct).ConfigureAwait(false);
        var streams = metadata?.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Stream ?? [];
        var stream = streams.Select((s, position) => (Stream: s, Index: PlaybackIndex(s, position)))
            .FirstOrDefault(s => s.Stream.StreamType == 3 && s.Index == streamIndex).Stream;
        if (stream?.Key is not { } key) throw JellyfinException.Explained(null, PlexErrors.NotBuilt);
        var data = await DataAsync(Url(key, new Dictionary<string, string?> { ["X-Plex-Token"] = AccessToken }), ct: ct).ConfigureAwait(false);
        return PlexSubtitles.Cues(PlexSubtitles.Text(data), stream.Codec);
    }

    /// <summary>Only sidecar files can be fetched, and their address comes with the track (<see cref="MediaStream.DeliveryUrl"/>).</summary>
    public Uri SubtitleFileUrl(string itemId, string mediaSourceId, int streamIndex, string format) => throw new NotSupportedException(PlexErrors.NotBuilt);

    /// <summary>Plex Pass's intro and credits markers; failing those, chapters named as an opening or ending.</summary>
    public async Task<IReadOnlyList<MediaSegment>> MediaSegmentsAsync(string itemId, CancellationToken ct = default)
    {
        if (await MetadataAsync(itemId, ct).ConfigureAwait(false) is not { } metadata) return [];
        return PlexSegments.Segments(metadata.Marker ?? [], metadata.Chapter ?? [], itemId, metadata.Duration);
    }

    public Uri TrickplayTileUrl(string itemId, int width, int index, string mediaSourceId) => throw new NotSupportedException(PlexErrors.NotBuilt);
    public Uri ChapterImageUrl(string itemId, int index, long? stamp) => throw new NotSupportedException(PlexErrors.NotBuilt);
    public Uri ChapterImageUrl(string itemId, int index, string tag) => throw new NotSupportedException(PlexErrors.NotBuilt);

    // ---- Helpers ----

    private static string IdText(long value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<PlexMetadata?> MetadataAsync(string id, CancellationToken ct) =>
        (await ContainerAsync($"/library/metadata/{id}", new Dictionary<string, string?>
        {
            ["includeMarkers"] = "1",
            ["includeChapters"] = "1",
        }, ct: ct).ConfigureAwait(false)).Metadata?.FirstOrDefault();

    private async Task TimelineAsync(PlaybackReport report, string state)
    {
        var request = Request(Url("/:/timeline", new Dictionary<string, string?>
        {
            ["ratingKey"] = report.ItemId,
            ["key"] = $"/library/metadata/{report.ItemId}",
            ["state"] = state,
            ["time"] = IdText(report.PositionTicks / PlexMapper.TicksPerMillisecond),
            ["duration"] = report.RunTimeTicks is { } length ? IdText(length / PlexMapper.TicksPerMillisecond) : null,
            ["identifier"] = LibraryIdentifier,
        }));
        if (report.PlaySessionId is { } session) request.Headers.TryAddWithoutValidation("X-Plex-Session-Identifier", session);
        try { await SendAsync(request, default).ConfigureAwait(false); }
        catch (JellyfinException) { }
    }

    /// <summary>
    /// The transcoder's settings, for its decision and its HLS stream.
    /// <paramref name="keepsSurround"/> lets audio come as E-AC-3;
    /// <paramref name="mostCompatible"/> is a plain H.264 + AAC encode. A
    /// picture subtitle (or one inside the file) is burned in.
    /// </summary>
    private Dictionary<string, string?> TranscodeQuery(string itemId, string session, long startTicks, int maxBitrate, bool keepsSurround, bool mostCompatible, int? burnIn)
    {
        var query = new Dictionary<string, string?>
        {
            ["path"] = $"/library/metadata/{itemId}",
            ["mediaIndex"] = "0",
            ["partIndex"] = "0",
            ["protocol"] = "hls",
            ["fastSeek"] = "1",
            ["directPlay"] = "0",
            ["directStream"] = mostCompatible ? "0" : "1",
            ["directStreamAudio"] = mostCompatible ? "0" : "1",
            ["videoQuality"] = "100",
            ["maxVideoBitrate"] = Number(Math.Max(1, maxBitrate / 1000)),
            ["offset"] = (startTicks / 10_000_000.0).ToString("0.###", CultureInfo.InvariantCulture),
            ["subtitles"] = burnIn is null ? "none" : "burn",
            ["location"] = IsLocal(BaseUrl) ? "lan" : "wan",
            ["session"] = session,
            ["X-Plex-Session-Identifier"] = session,
            ["X-Plex-Client-Identifier"] = PlexDevice.ClientIdentifier,
            ["X-Plex-Product"] = PlexDevice.Product,
            ["X-Plex-Platform"] = PlexDevice.Platform,
            ["X-Plex-Token"] = AccessToken,
        };
        if (keepsSurround)
            query["X-Plex-Client-Profile-Extra"] = "append-transcode-target-codec(type=videoProfile&context=streaming&protocol=hls&audioCodec=eac3)";
        if (burnIn is { } index)
        {
            query["SubtitleMethod"] = "Encode";
            query["SubtitleStreamIndex"] = Number(index);
        }
        return query;
    }

    /// <summary>
    /// Whether the server is on this network, so Plex holds a conversion to
    /// the local quality rather than its Remote Access limit: a plex.direct
    /// name, a private address, or a bare machine name.
    /// </summary>
    public static bool IsLocal(Uri server)
    {
        var host = server.Host;
        if (host.Contains("plex.direct", StringComparison.OrdinalIgnoreCase) || !host.Contains('.')) return true;
        if (!IPAddress.TryParse(host, out var address)) return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168));
    }

    /// <summary>A subtitle file beside the video, in a text format the app reads.</summary>
    public static bool IsFetchableText(PlexStream stream) => stream.Key is not null && PlexSubtitles.IsText(stream.Codec);

    /// <summary>
    /// A track's number for the player: its place in the file, which mpv
    /// knows it by; a file beside the video has none, so it's numbered past
    /// anything a file holds.
    /// </summary>
    public static int PlaybackIndex(PlexStream stream, int position) => stream.Index ?? 1000 + position;

    /// <summary>
    /// A track for the player. Subtitle files beside the video come with
    /// their address; tracks inside the file, which Plex won't hand out, are
    /// read by mpv from the file, or burned in when the stream is converted.
    /// </summary>
    public MediaStream PlaybackStream(PlexStream stream, int position)
    {
        var text = stream.StreamType == 3 && IsFetchableText(stream);
        return new MediaStream
        {
            Type = stream.StreamType switch
            {
                1 => "Video",
                2 => "Audio",
                3 => "Subtitle",
                _ => null,
            },
            Index = PlaybackIndex(stream, position),
            Codec = stream.Codec,
            Language = stream.LanguageCode,
            DisplayTitle = stream.DisplayTitle,
            Title = stream.Title,
            IsDefault = stream.Default,
            IsForced = stream.Forced,
            IsExternal = stream.Key is not null,
            Width = stream.Width,
            Height = stream.Height,
            Channels = stream.Channels,
            IsTextSubtitleStream = stream.StreamType == 3 ? text : null,
            DeliveryMethod = stream.StreamType == 3 ? (text ? "External" : "Encode") : null,
            DeliveryUrl = text ? Url(stream.Key!, new Dictionary<string, string?> { ["X-Plex-Token"] = AccessToken }).AbsoluteUri : null,
        };
    }
}

/// <summary>Plex's markers and chapters as the app's skip segments.</summary>
public static class PlexSegments
{
    /// <summary>
    /// The longest a chapter can be and still be taken for an opening or
    /// ending: some files name their whole first half "Intro".
    /// </summary>
    public const long LongestThemeMilliseconds = 4 * 60 * 1000;

    public static IReadOnlyList<MediaSegment> Segments(IReadOnlyList<PlexMarker> markers, IReadOnlyList<PlexChapter> chapters, string itemId, long? durationMilliseconds)
    {
        static long Ticks(long ms) => ms * PlexMapper.TicksPerMillisecond;
        var found = new List<MediaSegment>();
        foreach (var marker in markers.Where(m => m.EndTimeOffset > m.StartTimeOffset))
        {
            var type = marker.Type switch
            {
                "intro" => "Intro",
                "credits" => "Outro",
                _ => null,
            };
            if (type is null) continue;
            found.Add(new MediaSegment { Id = $"{itemId}-{marker.Type}", ItemId = itemId, Type = type, StartTicks = Ticks(marker.StartTimeOffset), EndTicks = Ticks(marker.EndTimeOffset) });
        }
        if (found.Count > 0) return found;

        // Chapters named for what they are, ending where the next begins.
        var sorted = chapters.OrderBy(c => c.StartTimeOffset).ToList();
        for (var index = 0; index < sorted.Count; index++)
        {
            var chapter = sorted[index];
            var type = (chapter.Tag ?? "").Trim().ToLowerInvariant() switch
            {
                "opening" or "op" or "intro" or "opening credits" => "Intro",
                "ending" or "ed" or "credits" or "end credits" or "outro" => "Outro",
                _ => null,
            };
            if (type is null || found.Any(s => s.Type == type)) continue;
            var end = chapter.EndTimeOffset ?? (index + 1 < sorted.Count ? sorted[index + 1].StartTimeOffset : durationMilliseconds);
            if (end is not { } stop || stop <= chapter.StartTimeOffset || stop - chapter.StartTimeOffset > LongestThemeMilliseconds) continue;
            found.Add(new MediaSegment { Id = $"{itemId}-{type.ToLowerInvariant()}", ItemId = itemId, Type = type, StartTicks = Ticks(chapter.StartTimeOffset), EndTicks = Ticks(stop) });
        }
        return found;
    }
}

/// <summary>Subtitle files beside a video: SRT, WebVTT, or ASS/SSA as plain text.</summary>
public static partial class PlexSubtitles
{
    public static bool IsText(string? codec) =>
        (codec ?? "").ToLowerInvariant() is "srt" or "subrip" or "vtt" or "webvtt" or "ass" or "ssa" or "mov_text" or "text";

    /// <summary>UTF-8, else Latin-1 (older SRT files).</summary>
    public static string Text(byte[] data)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(data); }
    }

    public static IReadOnlyList<SubtitleCue> Cues(string text, string? codec)
    {
        var kind = (codec ?? "").ToLowerInvariant();
        return kind is "ass" or "ssa" || text.Contains("[Events]", StringComparison.Ordinal) ? AssCues(text) : TimedBlocks(text);
    }

    /// <summary>SRT and WebVTT: blocks of a time line and text, apart by blank lines.</summary>
    public static IReadOnlyList<SubtitleCue> TimedBlocks(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var cues = new List<SubtitleCue>();
        foreach (var block in normalized.Split("\n\n"))
        {
            var lines = block.Split('\n');
            var timeIndex = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timeIndex < 0) continue;
            var parts = lines[timeIndex].Split("-->");
            if (parts.Length != 2 || Seconds(parts[0]) is not { } start || Seconds(parts[1].Trim().Split(' ')[0]) is not { } end) continue;
            var clean = Tags().Replace(string.Join("\n", lines[(timeIndex + 1)..]), "").Trim();
            if (clean.Length > 0) cues.Add(new SubtitleCue(start, end, clean));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    /// <summary>ASS/SSA: each Dialogue line's times and text, styling taken out.</summary>
    public static IReadOnlyList<SubtitleCue> AssCues(string text)
    {
        var cues = new List<SubtitleCue>();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)))
        {
            var fields = line["Dialogue:".Length..].Split(',', 10);
            if (fields.Length != 10 || Seconds(fields[1]) is not { } start || Seconds(fields[2]) is not { } end) continue;
            var body = AssOverrides().Replace(fields[9].Replace("\\N", "\n").Replace("\\n", "\n"), "");
            var clean = body.Trim();
            if (clean.Length > 0) cues.Add(new SubtitleCue(start, end, clean));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    /// <summary>"01:02:03,456", "01:02:03.456", "02:03.456" or ASS's "1:02:03.45".</summary>
    public static double? Seconds(string text)
    {
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is not (2 or 3)) return null;
        var total = 0.0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total;
    }

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssOverrides();
}
