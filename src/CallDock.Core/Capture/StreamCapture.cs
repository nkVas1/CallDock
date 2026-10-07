using System.Diagnostics;
using System.Globalization;

namespace CallDock.Core;

/// <summary>
/// A network stream recorded with FFmpeg as it comes — original packets in five-minute MKV segments, no re-encoding.
/// A live stream cannot be paused: during a pause of the recording the reader is closed, and a new one continues the
/// same track with the next segment when the recording goes on.
/// </summary>
public sealed class StreamCapture : IRecording
{
    private readonly string url;
    private readonly string folder;
    private readonly Task meterTask;
    private readonly CancellationTokenSource meterCancellation = new();
    private readonly Queue<string> tail = new();
    /// <summary>Pause, resume and stop one after another: a reader is never started while the previous one closes.</summary>
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private Reader? reader;
    private bool paused;
    private bool stopping;
    private Task? stopTask;
    private float peak;
    private string? error;
    public RecordingTrack Track { get; }
    public float Peak => Volatile.Read(ref peak);
    public long BytesWritten => Directory.EnumerateFiles(folder, "*.mkv").Sum(f => new FileInfo(f).Length);
    public string? Error => error ?? (reader is { Ended: true } ended && !stopping && !paused
        ? $"Поток завершился (код {ended.ExitCode}). Сохранённые части доступны." : null);

    /// <summary>One FFmpeg process reading the stream.</summary>
    private sealed class Reader
    {
        public required Process Process { get; init; }
        public Task Output { get; set; } = Task.CompletedTask;
        public volatile bool Ended;
        public int ExitCode;
    }

    /// <param name="paused">The recording is paused: the reader starts when it goes on.</param>
    public StreamCapture(SourceSpec spec, RecordingTrack track, string folder, bool paused = false)
    {
        Track = track;
        this.folder = folder;
        this.paused = paused;
        Directory.CreateDirectory(folder);
        track.Format = "Оригинальные кодеки · MKV · без перекодирования";
        track.HasVideo = true;
        url = MediaTools.ValidateStreamUrl(spec.Target);
        if (!paused) reader = Launch(0);
        meterTask = Task.Run(async () =>
        {
            try { while (true) { await Task.Delay(250, meterCancellation.Token); Volatile.Write(ref peak, peak * 0.65f); } }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Starts a reader that writes segments from <paramref name="firstSegment"/> on.</summary>
    private Reader Launch(int firstSegment)
    {
        // One network reader: original packets go to disk, a cheap mono decode feeds a true audio meter.
        var args = new[] {
            "-hide_banner", "-loglevel", "warning", "-rw_timeout", "15000000",
            "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5",
            "-protocol_whitelist", "http,https,tcp,tls,crypto,data", "-i", url,
            "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-f", "segment",
            "-segment_time", "300", "-segment_start_number", firstSegment.ToString(CultureInfo.InvariantCulture),
            "-reset_timestamps", "1", Path.Combine(folder, "%05d.mkv"),
            "-map", "0:a:0?", "-vn", "-ac", "1", "-ar", "8000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"
        };
        var process = new Process { StartInfo = MediaTools.StartInfo(AppPaths.Ffmpeg, args), EnableRaisingEvents = true };
        var started = new Reader { Process = process };
        process.Exited += (_, _) => { started.ExitCode = process.ExitCode; started.Ended = true; };
        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            { lock (tail) { tail.Enqueue(line); if (tail.Count > 12) tail.Dequeue(); } }
        });
        var stdout = Task.Run(async () =>
        {
            var buffer = new byte[3200];
            try
            {
                while (true)
                {
                    var count = await process.StandardOutput.BaseStream.ReadAsync(buffer);
                    if (count == 0) break;
                    Volatile.Write(ref peak, AudioCapture.PcmPeak(buffer.AsSpan(0, count), NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(8000, 1)));
                }
            }
            catch (IOException e) { if (!stopping && !paused) error = e.Message; }
        });
        started.Output = Task.WhenAll(stderr, stdout);
        return started;
    }

    /// <summary>Asks the reader to finish its segment and quit; it is killed if it does not within 20 seconds.</summary>
    private async Task CloseAsync(Reader current)
    {
        var process = current.Process;
        if (!process.HasExited)
        {
            try { await process.StandardInput.WriteLineAsync("q"); await process.StandardInput.FlushAsync(); }
            catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); error = "Источник не ответил на остановку. Части сохранены."; }
        }
        await current.Output;
    }

    /// <summary>The recording pauses: the reader closes, what is already saved stays in the track.</summary>
    public async Task PauseAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (paused || stopping) return;
            paused = true;
            if (reader is { Ended: false } current) await CloseAsync(current);
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>The recording goes on: a new reader continues the track with the next segment.</summary>
    public async Task ResumeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (!paused || stopping) return;
            reader?.Process.Dispose();
            reader = Launch(Directory.EnumerateFiles(folder, "*.mkv").Select(Number).DefaultIfEmpty(-1).Max() + 1);
            paused = false;
        }
        finally { lifecycle.Release(); }
    }

    private static int Number(string file) => int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

    public Task StopAsync() => stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            // A stream that ended by itself before the stop is a failure; one closed for a pause is not.
            var endedEarly = reader is { Ended: true } && !paused;
            stopping = true;
            if (reader is { } current)
            {
                await CloseAsync(current);
                if (current.Process.ExitCode != 0 || endedEarly)
                    error ??= current.Process.ExitCode == 0 ? "Поток закончился до остановки сессии." : "Ошибка потока; проверьте прямую ссылку и доступ к трансляции.";
                current.Process.Dispose();
            }
            meterCancellation.Cancel();
            await meterTask;
            // Do not persist stderr: upstream URLs may contain bearer tokens.
            Track.Status = error is null ? "Done" : "Failed";
            Track.Error = error;
            meterCancellation.Dispose();
        }
        finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
