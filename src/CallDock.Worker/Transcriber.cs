using CallDock.Core;
using NAudio.Wave;
using System.Globalization;
using System.Text.RegularExpressions;
using Whisper.net;

namespace CallDock.Worker;

/// <summary>Progress of a transcription: the share of audio processed and what is being processed now.</summary>
public sealed record TranscriptionProgress(double Fraction, string Text);

public static partial class Transcriber
{
    private const double SliceSeconds = 300;

    public static async Task<IReadOnlyList<TranscriptSegment>> RunAsync(Archive archive, CallSession session,
        AppSettings settings, Action<TranscriptionProgress>? progress, CancellationToken ct)
    {
        var model = ModelManager.Find(settings.Model);
        if (!ModelManager.IsInstalled(model)) throw new InvalidOperationException("Сначала скачайте модель распознавания в настройках.");
        // Durations first: they give an honest percentage for the whole session.
        var plan = new List<(RecordingTrack Track, string File, double Offset, double Duration)>();
        foreach (var track in session.Tracks.Where(x => x.HasAudio))
        {
            var offset = track.OffsetSeconds;
            foreach (var file in MediaTools.TrackFiles(archive.TrackFolder(session, track)))
            {
                var duration = await MediaTools.DurationAsync(file, ct);
                plan.Add((track, file, offset, duration));
                offset += duration;
            }
        }
        var total = Math.Max(1, plan.Sum(x => x.Duration));
        double done = 0;

        // Whisper reports how far into the current slice it is: on a slow processor a five-minute slice takes minutes,
        // and the percentage must move meanwhile. The handler reads the slice it belongs to from these variables.
        double sliceStart = 0, sliceShare = 0, reported = -1;
        var sliceLabel = "";
        using var factory = WhisperFactory.FromPath(ModelManager.PathFor(model));
        var builder = factory.CreateBuilder().WithThreads(Math.Clamp(settings.CpuThreads, 1, 16)).WithNoSpeechThreshold(0.6f)
            .WithProgressHandler(percent =>
            {
                var fraction = (sliceStart + sliceShare * Math.Clamp(percent, 0, 100) / 100d) / total;
                if (fraction - reported < 0.005) return;
                reported = fraction;
                progress?.Invoke(new(fraction, sliceLabel));
            });
        if (settings.Language == "auto") builder.WithLanguageDetection(); else builder.WithLanguage(settings.Language);
        using var processor = builder.Build();

        var result = new List<TranscriptSegment>();
        var temporary = Path.Combine(archive.Folder(session), ".transcription");
        Directory.CreateDirectory(temporary);
        var wav = Path.Combine(temporary, "speech.wav");
        try
        {
            foreach (var (track, input, offset, duration) in plan)
            {
                // Bounded memory for long files: whisper.net reads the whole input into an array.
                for (double slice = 0; slice < duration; slice += SliceSeconds)
                {
                    ct.ThrowIfCancellationRequested();
                    var sliceLength = Math.Min(SliceSeconds, duration - slice);
                    (sliceStart, sliceShare, sliceLabel) = (done, sliceLength, $"{track.Name} · {Display.Duration(offset + slice)}");
                    progress?.Invoke(new(done / total, sliceLabel));
                    await MediaTools.RunAsync(AppPaths.Ffmpeg,
                        ["-hide_banner", "-loglevel", "error", "-y", "-ss", slice.ToString(CultureInfo.InvariantCulture), "-i", input,
                         "-t", SliceSeconds.ToString(CultureInfo.InvariantCulture), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav], ct);
                    if (ContainsSignal(wav))
                    {
                        await using var audio = File.OpenRead(wav);
                        await foreach (var segment in processor.ProcessAsync(audio, ct))
                        {
                            var text = segment.Text.Trim();
                            if (text.Length == 0 || IsHallucination(text)) continue;
                            var start = offset + slice + segment.Start.TotalSeconds;
                            result.Add(new(start, offset + slice + segment.End.TotalSeconds, track.Name, text));
                            sliceLabel = $"{track.Name} · {Display.Duration(start)}";
                        }
                    }
                    done += sliceLength;
                }
            }
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
            if (!Directory.EnumerateFileSystemEntries(temporary).Any()) Directory.Delete(temporary);
        }
        progress?.Invoke(new(1, "Готово"));
        return result.OrderBy(x => x.Start).ToArray();
    }

    /// <summary>True when the 16-bit mono WAV has at least ~0.1 s of audible signal — silence is not sent to the model,
    /// which otherwise "hears" phrases in it.</summary>
    public static bool ContainsSignal(string wav)
    {
        using var reader = new WaveFileReader(wav);
        var buffer = new byte[32000];
        int count;
        long voiced = 0;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i + 1 < count; i += 2)
                if (Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i))) > 160) voiced++;
            if (voiced >= 1600) return true;
        }
        return false;
    }

    /// <summary>Phrases Whisper is known to invent on noise or music: subtitle credits from its training data.
    /// Only unmistakable artefacts are dropped; ordinary speech such as «Спасибо за внимание» is kept.</summary>
    public static bool IsHallucination(string text) => Artefact().IsMatch(text);

    [GeneratedRegex(@"DimaTorzok|Субтитры (сделал|создавал|подогнал|делал)|Редактор субтитров|Корректор [А-Я]\.|^\s*Продолжение следует\.{0,3}\s*$|amara\.org|^\s*\[?(музыка|music)\]?\s*$|Thank you for watching",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Artefact();
}
