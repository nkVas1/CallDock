using System.Globalization;

namespace CallDock.Core;

/// <summary>
/// Chrome's MediaRecorder streams WebM without a length or a seek index: such a file plays from the start but cannot
/// be scrolled in most players. After the recording each tab file is rewritten as it is (no re-encoding) with both,
/// checked, and only then replaces the original. A tab whose sound came out shorter than its picture is marked:
/// that happens when the computer could not keep up with encoding video, and the person should know.
/// </summary>
public static class TabIndexer
{
    /// <summary>Sound shorter than the picture by more than this means seconds of sound were lost.</summary>
    private const double LostSoundSeconds = 2;

    public static async Task<int> IndexAsync(Archive archive, CallSession session, IProgress<string>? progress, CancellationToken ct)
    {
        var indexed = 0;
        foreach (var track in session.Tracks.Where(t => t.Kind == SourceKind.BrowserTab))
        {
            var folder = archive.TrackFolder(session, track);
            if (!Directory.Exists(folder)) continue;
            double lost = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.webm").Order(StringComparer.Ordinal).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(file).Length == 0) continue;
                try
                {
                    if (await MediaTools.DeclaredDurationAsync(file, ct) > 0) continue;
                    progress?.Report($"Подготовка «{track.Name}» к перемотке");
                    lost += await IndexFileAsync(file, ct);
                    indexed++;
                }
                catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException)
                {
                    // A damaged file stays as it is; the other files of the session are still indexed.
                    Log.Warn($"Tab file not indexed ({file}): {e.Message}");
                }
            }
            if (track.HasVideo && lost > LostSoundSeconds && track.Error is null)
                track.Error = string.Create(CultureInfo.GetCultureInfo("ru-RU"),
                    $"Звук короче видео на {lost:F0} с: компьютер не успевал кодировать видео. Для нескольких трансляций сразу выбирайте в расширении «Без видео».");
        }
        session.TabsIndexed = true;
        return indexed;
    }

    /// <summary>Rewrites one file with a length and a seek index; returns how many seconds of sound are missing at its end.</summary>
    private static async Task<double> IndexFileAsync(string file, CancellationToken ct)
    {
        var part = file + ".part";
        try
        {
            await MediaTools.RunAsync(AppPaths.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", file, "-map", "0", "-c", "copy", "-f", "webm", part], ct);
            // Copying streams cannot change them, so the check is that the new file is whole: it declares a length
            // and is not shorter than the original sound measured by reading it through.
            var measured = await MediaTools.DurationAsync(file, ct);
            var declared = await MediaTools.DeclaredDurationAsync(part, ct);
            if (declared <= 0 || declared + 0.5 < measured)
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"Rewritten file is incomplete ({declared:F1} s, the original sound is {measured:F1} s)."));
            var (audio, video) = await MediaTools.StreamDurationsAsync(part, ct);
            File.Move(part, file, true);
            return audio > 0 && video > audio ? video - audio : 0;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
}
