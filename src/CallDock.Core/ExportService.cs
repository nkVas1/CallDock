using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CallDock.Core;

public static class ExportService
{
    public static string Transcript(CallSession session, string extension)
    {
        var segments = session.Transcript.OrderBy(x => x.Start).ToArray();
        string time(double seconds, bool comma = false)
        {
            var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
            return $"{ms / 3600000:D2}:{ms / 60000 % 60:D2}:{ms / 1000 % 60:D2}{(comma ? ',' : '.')}{ms % 1000:D3}";
        }
        return extension.ToLowerInvariant() switch
        {
            ".json" => JsonSerializer.Serialize(session, AppPaths.Json),
            ".srt" => string.Join("\n\n", segments.Select((s, i) => $"{i + 1}\n{time(s.Start, true)} --> {time(Math.Max(s.Start + 0.1, s.End), true)}\n[{s.Source}] {s.Text}")) + "\n",
            ".vtt" => "WEBVTT\n\n" + string.Join("\n\n", segments.Select(s => $"{time(s.Start)} --> {time(Math.Max(s.Start + 0.1, s.End))}\n[{s.Source}] {s.Text.Replace("-->", "→")}")) + "\n",
            ".md" => $"# {session.Title}\n\n{session.DateLabel} · {session.Project}\n\n{session.Notes}\n\n" +
                string.Join("\n\n", segments.Select(s => $"**{time(s.Start)} · {s.Source}**  \n{s.Text}")),
            ".txt" => $"{session.Title}\n{session.DateLabel} · {session.Project}\n{session.Notes}\n\n" +
                string.Join('\n', segments.Select(s => $"[{time(s.Start)}] {s.Source}: {s.Text}")),
            _ => throw new ArgumentException("Формат текста: TXT, MD, JSON, SRT или VTT.")
        };
    }

    public static async Task ExportTrackAsync(Archive archive, CallSession session, RecordingTrack track, string destination, CancellationToken ct)
    {
        var files = MediaTools.TrackFiles(archive.TrackFolder(session, track));
        var dir = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(dir);
        var list = await MediaTools.ConcatListAsync(files, dir);
        var ext = Path.GetExtension(destination).ToLowerInvariant();
        var temporary = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ext);
        try
        {
            List<string> args = ["-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list];
            args.AddRange(ext switch
            {
                ".wav" => ["-vn", "-c:a", "pcm_f32le", "-rf64", "auto"],
                ".flac" => ["-vn", "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24"],
                ".mp3" => ["-vn", "-c:a", "libmp3lame", "-b:a", "192k"],
                ".m4a" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
                ".mkv" => ["-map", "0", "-c", "copy"],
                ".mp4" when track.Kind == SourceKind.BrowserTab => ["-c:v", "h264_mf", "-b:v", "6000k", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
                ".mp4" => ["-map", "0", "-c", "copy", "-movflags", "+faststart"],
                _ => throw new ArgumentException("Неподдерживаемый формат экспорта.")
            });
            args.Add(temporary);
            await MediaTools.RunAsync(AppPaths.Ffmpeg, args, ct);
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(list); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>All audio tracks mixed on one timeline (each shifted by its start offset): WAV, FLAC, MP3 or M4A.</summary>
    public static async Task ExportMixAsync(Archive archive, CallSession session, string destination, CancellationToken ct)
    {
        var tracks = session.Tracks.Where(t => t.HasAudio && MediaTools.TrackFiles(archive.TrackFolder(session, t)).Count > 0).ToArray();
        if (tracks.Length == 0) throw new InvalidOperationException("Нет звуковых дорожек.");
        var temporaryLists = new List<string>();
        var dir = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(dir);
        var ext = Path.GetExtension(destination).ToLowerInvariant();
        string[] codec = ext switch
        {
            ".wav" => ["-c:a", "pcm_f32le", "-rf64", "auto"],
            ".flac" => ["-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24"],
            ".mp3" => ["-c:a", "libmp3lame", "-b:a", "192k"],
            ".m4a" => ["-c:a", "aac", "-b:a", "192k"],
            _ => throw new ArgumentException("Общая дорожка сохраняется в WAV, FLAC, MP3 или M4A.")
        };
        var temp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ext);
        try
        {
            List<string> args = ["-hide_banner", "-loglevel", "error"];
            foreach (var track in tracks)
            {
                var list = await MediaTools.ConcatListAsync(MediaTools.TrackFiles(archive.TrackFolder(session, track)), dir);
                temporaryLists.Add(list);
                args.AddRange(["-f", "concat", "-safe", "0", "-i", list]);
            }
            var filters = tracks.Select((t, i) => $"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay={Math.Round(t.OffsetSeconds * 1000).ToString(CultureInfo.InvariantCulture)}:all=1[a{i}]");
            var inputs = string.Concat(Enumerable.Range(0, tracks.Length).Select(i => $"[a{i}]"));
            var filter = string.Join(';', filters) + ";" + inputs + $"amix=inputs={tracks.Length}:duration=longest:normalize=1[out]";
            args.AddRange(["-filter_complex", filter, "-map", "[out]", .. codec, temp]);
            await MediaTools.RunAsync(AppPaths.Ffmpeg, args, ct);
            File.Move(temp, destination, true);
        }
        finally { foreach (var list in temporaryLists) File.Delete(list); if (File.Exists(temp)) File.Delete(temp); }
    }
}
