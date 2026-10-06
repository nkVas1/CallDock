using System.Globalization;

namespace CallDock.Core;

/// <summary>
/// Turns the WAV segments of a finished recording into lossless FLAC (24-bit), about a third of the size.
/// Recording itself stays WAV: a WAV written up to the moment of a crash can be repaired, a half-written FLAC cannot.
/// Each segment is encoded next to the original, checked against its duration and only then replaces it.
/// </summary>
public static class AudioCompactor
{
    public static async Task<long> CompactAsync(Archive archive, CallSession session, IProgress<string>? progress, CancellationToken ct)
    {
        long saved = 0;
        foreach (var track in session.Tracks.Where(t => t.HasAudio && !t.HasVideo))
        {
            var folder = archive.TrackFolder(session, track);
            if (!Directory.Exists(folder)) continue;
            var segments = Directory.EnumerateFiles(folder, "*.wav").Order(StringComparer.Ordinal).ToArray();
            for (var i = 0; i < segments.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Сжатие «{track.Name}» · часть {i + 1} из {segments.Length}");
                saved += await CompactSegmentAsync(segments[i], ct);
            }
            if (segments.Length > 0 && !track.Format.Contains("FLAC", StringComparison.Ordinal))
                track.Format = string.IsNullOrEmpty(track.Format) ? "FLAC 24 бит без потерь"
                    : track.Format.EndsWith(" · WAV", StringComparison.Ordinal) ? track.Format[..^" · WAV".Length] + " · FLAC 24 бит без потерь"
                    : track.Format + " · хранится как FLAC 24 бит";
        }
        session.AudioCompressed = true;
        return saved;
    }

    private static async Task<long> CompactSegmentAsync(string wav, CancellationToken ct)
    {
        var flac = Path.ChangeExtension(wav, ".flac");
        var part = flac + ".part";
        try
        {
            await MediaTools.RunAsync(AppPaths.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", wav, "-map", "0:a:0", "-c:a", "flac",
                 "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-compression_level", "5", "-f", "flac", part], ct);
            var expected = await MediaTools.DurationAsync(wav, ct);
            var actual = await MediaTools.DurationAsync(part, ct);
            if (Math.Abs(expected - actual) > 0.05)
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"Сжатый файл короче исходного ({actual:F2} с вместо {expected:F2} с); оригинал сохранён."));
            var before = new FileInfo(wav).Length;
            File.Move(part, flac, true);
            File.Delete(wav);
            return before - new FileInfo(flac).Length;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
}
