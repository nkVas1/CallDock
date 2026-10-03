using System.Diagnostics;
using System.Globalization;

namespace CallDock.Core;

public sealed class StreamCapture : IRecording
{
    private readonly Process process;
    private readonly string folder;
    private readonly Task stderrTask;
    private readonly Task stdoutTask;
    private readonly Task meterTask;
    private readonly CancellationTokenSource meterCancellation = new();
    private readonly Queue<string> tail = new();
    private bool stopping;
    private Task? stopTask;
    private float peak;
    private string? error;
    private int exitCode;
    private bool ended;
    public RecordingTrack Track { get; }
    public float Peak => Volatile.Read(ref peak);
    public long BytesWritten => Directory.EnumerateFiles(folder, "*.mkv").Sum(f => new FileInfo(f).Length);
    public string? Error => error ?? (ended && !stopping ? $"Поток завершился (код {exitCode}). Сохранённые части доступны." : null);

    public StreamCapture(SourceSpec spec, RecordingTrack track, string folder)
    {
        Track = track;
        this.folder = folder;
        Directory.CreateDirectory(folder);
        track.Format = "Оригинальные кодеки · MKV · без перекодирования";
        track.HasVideo = true;
        var url = MediaTools.ValidateStreamUrl(spec.Target);
        // One network reader: original packets go to disk, a cheap mono decode feeds a true audio meter.
        var args = new[] {
            "-hide_banner", "-loglevel", "warning", "-rw_timeout", "15000000",
            "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5",
            "-protocol_whitelist", "http,https,tcp,tls,crypto,data", "-i", url,
            "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-f", "segment",
            "-segment_time", "300", "-reset_timestamps", "1", Path.Combine(folder, "%05d.mkv"),
            "-map", "0:a:0?", "-vn", "-ac", "1", "-ar", "8000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"
        };
        process = new Process { StartInfo = MediaTools.StartInfo(AppPaths.Ffmpeg, args), EnableRaisingEvents = true };
        process.Exited += (_, _) => { exitCode = process.ExitCode; ended = true; };
        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            { lock (tail) { tail.Enqueue(line); if (tail.Count > 12) tail.Dequeue(); } }
        });
        stdoutTask = Task.Run(async () =>
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
            catch (IOException e) { if (!stopping) error = e.Message; }
        });
        meterTask = Task.Run(async () =>
        {
            try { while (true) { await Task.Delay(250, meterCancellation.Token); Volatile.Write(ref peak, peak * 0.65f); } }
            catch (OperationCanceledException) { }
        });
    }

    public Task StopAsync() => stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        var wasEnded = ended;
        stopping = true;
        if (!process.HasExited)
        {
            try { await process.StandardInput.WriteLineAsync("q"); await process.StandardInput.FlushAsync(); }
            catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); error = "Источник не ответил на остановку. Части сохранены."; }
        }
        await Task.WhenAll(stderrTask, stdoutTask);
        meterCancellation.Cancel();
        await meterTask;
        if (process.ExitCode != 0 || wasEnded)
            error ??= process.ExitCode == 0 ? "Поток закончился до остановки сессии." : "Ошибка потока; проверьте прямую ссылку и доступ к трансляции.";
        // Do not persist stderr: upstream URLs may contain bearer tokens.
        Track.Status = error is null ? "Done" : "Failed";
        Track.Error = error;
        process.Dispose();
        meterCancellation.Dispose();
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
