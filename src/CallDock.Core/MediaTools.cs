using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CallDock.Core;

public static class MediaTools
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("В сборке отсутствует медиадвижок. Распакуйте весь ZIP CallDock.", executable);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }

    public static async Task<string> RunAsync(string executable, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = StartInfo(executable, args) };
        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        var stderr = ReadTailAsync(process.StandardError);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Медиадвижок: {error}");
            return await stdout;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new Queue<string>();
        while (await reader.ReadLineAsync() is { } line) { tail.Enqueue(line); if (tail.Count > 20) tail.Dequeue(); }
        return string.Join('\n', tail);
    }

    private static readonly string[] MediaExtensions = [".wav", ".flac", ".mkv", ".mp4", ".webm", ".m4a"];

    /// <summary>The media segments of a track in recording order. If a segment exists both as WAV and FLAC
    /// (compression was interrupted before the original was removed), the original WAV wins.</summary>
    public static IReadOnlyList<string> TrackFiles(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder)
            .Where(x => MediaExtensions.Contains(Path.GetExtension(x).ToLowerInvariant()))
            .GroupBy(x => Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => Array.IndexOf(MediaExtensions, Path.GetExtension(x).ToLowerInvariant())).First())
            .Order(StringComparer.Ordinal).ToArray()
        : [];

    public static long FolderSize(string folder) => Directory.Exists(folder)
        ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    public static async Task<string> ConcatListAsync(IReadOnlyList<string> files, string directory)
    {
        if (files.Count == 0) throw new InvalidOperationException("У этой дорожки нет сохранённых данных.");
        var list = Path.Combine(directory, "concat-" + Guid.NewGuid().ToString("N") + ".txt");
        var content = "ffconcat version 1.0\n" + string.Join('\n', files.Select(f => "file '" + Path.GetFullPath(f).Replace('\\', '/').Replace("'", "'\\''") + "'"));
        await File.WriteAllTextAsync(list, content, new UTF8Encoding(false));
        return list;
    }

    /// <summary>The length of a media file in seconds. Files without a length in their header — Chrome's MediaRecorder
    /// writes WebM that way — are measured by reading them through without decoding.</summary>
    public static async Task<double> DurationAsync(string file, CancellationToken ct = default)
    {
        var declared = await DeclaredDurationAsync(file, ct);
        return declared > 0 ? declared : await MeasuredDurationAsync(file, ct);
    }

    /// <summary>The length written in the file's header, or 0 when the header has none.</summary>
    public static async Task<double> DeclaredDurationAsync(string file, CancellationToken ct = default)
    {
        var json = await RunAsync(AppPaths.Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "json", file], ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var value)
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ? duration : 0;
    }

    /// <summary>Reads the file through without decoding. The sound decides when there is sound: that is what is
    /// transcribed and played; a file without sound is measured by its picture.</summary>
    private static async Task<double> MeasuredDurationAsync(string file, CancellationToken ct)
    {
        try { return await MeasureStreamAsync(file, "0:a:0", ct); }
        catch (InvalidOperationException) { return await MeasureStreamAsync(file, "0:v:0", ct); }
    }

    private static async Task<double> MeasureStreamAsync(string file, string stream, CancellationToken ct)
    {
        var progress = await RunAsync(AppPaths.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostats", "-i", file, "-map", stream, "-c", "copy", "-f", "null", "-", "-progress", "pipe:1"], ct);
        double duration = 0;
        foreach (var line in progress.Split('\n'))
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us > 0)
                duration = us / 1_000_000d;
        return duration;
    }

    /// <summary>The lengths of the sound and the picture, from the DURATION tags FFmpeg writes into Matroska and WebM
    /// files; 0 where a file has no such stream or tag.</summary>
    public static async Task<(double Audio, double Video)> StreamDurationsAsync(string file, CancellationToken ct = default)
    {
        var json = await RunAsync(AppPaths.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type:stream_tags=DURATION", "-of", "json", file], ct);
        using var doc = JsonDocument.Parse(json);
        double audio = 0, video = 0;
        if (!doc.RootElement.TryGetProperty("streams", out var streams)) return (0, 0);
        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("DURATION", out var tag)) continue;
            var parts = (tag.GetString() ?? "").Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var hours)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var minutes)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) continue;
            var length = hours * 3600 + minutes * 60 + seconds;
            switch (stream.GetProperty("codec_type").GetString())
            {
                case "audio": audio = Math.Max(audio, length); break;
                case "video": video = Math.Max(video, length); break;
            }
        }
        return (audio, video);
    }

    public static string ValidateStreamUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Нужна прямая HTTP(S)-ссылка на медиапоток, например .m3u8 или .mpd.");
        return uri.AbsoluteUri;
    }
}
