using CallDock.Core;
using System.Globalization;

namespace CallDock.App.Services;

/// <summary>A piece of a track to play: a file and where it sits on the session timeline (seconds).</summary>
public sealed record PlaybackEntry(string File, double Start, double Duration, bool Temporary)
{
    public double End => Start + Duration;
}

/// <summary>
/// Turns a track into something Windows can play from a given moment. WAV, FLAC and MP4 play as they are, segment
/// after segment. WebM (Chrome tabs) and MKV (streams) are not supported by the Windows player, so a fragment from
/// the requested moment is converted on the fly: ten minutes of sound, or two minutes of video.
/// </summary>
public static class Playback
{
    private static readonly string[] Native = [".wav", ".flac", ".mp4", ".m4a", ".mp3"];
    private const double AudioFragment = 600, VideoFragment = 120;

    public static string PreviewFolder => Path.Combine(AppPaths.Data, "preview");

    public static async Task<IReadOnlyList<PlaybackEntry>> PlanAsync(Archive archive, CallSession session, RecordingTrack track,
        double seconds, bool video, CancellationToken ct)
    {
        var files = MediaTools.TrackFiles(archive.TrackFolder(session, track));
        if (files.Count == 0) throw new InvalidOperationException("У этой дорожки нет сохранённых данных.");
        var durations = await Task.WhenAll(files.Select(f => MediaTools.DurationAsync(f, ct)));
        var entries = new List<PlaybackEntry>();
        var start = track.OffsetSeconds;
        for (var i = 0; i < files.Count; i++)
        {
            entries.Add(new(files[i], start, durations[i], false));
            start += durations[i];
        }
        var at = entries.FindLastIndex(e => e.Start <= seconds);
        var current = entries[Math.Max(0, at)];
        if (Native.Contains(Path.GetExtension(current.File).ToLowerInvariant()) && (!video || current.File.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
            return entries;

        // Not playable natively: one converted fragment from the requested moment.
        if (current.Duration <= 0) throw new InvalidOperationException("В этой части дорожки нет данных для воспроизведения.");
        var local = Math.Clamp(seconds - current.Start, 0, Math.Max(0, current.Duration - 1));
        var length = Math.Min(video ? VideoFragment : AudioFragment, current.Duration - local);
        Directory.CreateDirectory(PreviewFolder);
        var output = Path.Combine(PreviewFolder, Guid.NewGuid().ToString("N") + (video ? ".mp4" : ".m4a"));
        List<string> args = ["-hide_banner", "-loglevel", "error", "-ss", local.ToString(CultureInfo.InvariantCulture), "-i", current.File,
            "-t", length.ToString(CultureInfo.InvariantCulture)];
        args.AddRange(video
            ? ["-vf", "scale='min(1280,iw)':-2", "-c:v", "h264_mf", "-b:v", "4000k", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output]
            : ["-vn", "-c:a", "aac", "-b:a", "160k", output]);
        try { await MediaTools.RunAsync(AppPaths.Ffmpeg, args, ct); }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
        return [new(output, current.Start + local, length, true)];
    }

    /// <summary>Removes converted fragments left by an earlier run.</summary>
    public static void CleanPreviews()
    {
        try
        {
            if (!Directory.Exists(PreviewFolder)) return;
            foreach (var file in Directory.EnumerateFiles(PreviewFolder))
                if (File.GetCreationTime(file) < DateTime.Now.AddHours(-12)) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
