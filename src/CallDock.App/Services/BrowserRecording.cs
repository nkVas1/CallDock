using CallDock.Core;

namespace CallDock.App.Services;

public sealed class BrowserRecording : IRecording
{
    private readonly FileStream file;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long sequence;
    private long bytes;
    private float peak;
    private DateTime lastSeen = DateTime.UtcNow;
    private string? error;
    public bool StopRequested { get; private set; }
    public bool Closed { get; private set; }
    /// <summary>The extension that records the tab pauses it with the recording (extension 1.2 and later).</summary>
    public bool CanPause { get; }
    public RecordingTrack Track { get; }
    public float Peak => DateTime.UtcNow - lastSeen < TimeSpan.FromSeconds(2) ? peak : 0;
    public long BytesWritten => Interlocked.Read(ref bytes);
    public string? Error => error ?? (!Closed && DateTime.UtcNow - lastSeen > TimeSpan.FromSeconds(12) ? "Нет связи с вкладкой Chrome более 12 секунд." : null);

    public BrowserRecording(RecordingTrack track, string folder, bool video = true, bool canPause = false)
    {
        Track = track;
        CanPause = canPause;
        track.HasVideo = video;
        track.Format = video ? "Вкладка · VP8 + Opus · WebM" : "Вкладка · только звук · Opus · WebM";
        Directory.CreateDirectory(folder);
        file = new FileStream(Path.Combine(folder, "00000.webm"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 131072, true);
    }

    public async Task AppendAsync(long nextSequence, Stream input, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Closed) throw new InvalidOperationException("Запись вкладки уже закрыта.");
            if (nextSequence < sequence) return; // Retry of an acknowledged chunk: do not duplicate bytes.
            if (nextSequence != sequence) throw new InvalidOperationException("Нарушен порядок данных вкладки.");
            // Commit a whole chunk only after it has arrived; disconnected HTTP requests cannot leave half a chunk.
            using var chunk = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (chunk.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("Слишком большой пакет вкладки.");
                chunk.Write(buffer, 0, count);
            }
            chunk.Position = 0;
            await chunk.CopyToAsync(file, CancellationToken.None);
            await file.FlushAsync(CancellationToken.None);
            Interlocked.Add(ref bytes, chunk.Length);
            sequence++;
            lastSeen = DateTime.UtcNow;
        }
        finally { gate.Release(); }
    }

    public void UpdateMeter(float value) { peak = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0; lastSeen = DateTime.UtcNow; }
    public async Task FinishAsync(string? failure = null)
    {
        await gate.WaitAsync();
        try
        {
            if (Closed) return;
            Closed = true;
            error = failure;
            await file.DisposeAsync();
            Track.Status = failure is null ? "Done" : "Failed";
            Track.Error = failure;
            finished.TrySetResult();
        }
        finally { gate.Release(); }
    }
    public async Task StopAsync()
    {
        StopRequested = true;
        try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { await FinishAsync("Chrome не подтвердил завершение. Полученные данные сохранены."); }
    }
    public async ValueTask DisposeAsync() { if (!Closed) await StopAsync(); }
}
