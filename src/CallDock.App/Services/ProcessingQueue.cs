using CallDock.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CallDock.App.Services;

public enum ProcessingStage { None, Prepare, Compress, Mix, Transcribe }

/// <summary>What the background work is doing now, for the window.</summary>
public sealed record ProcessingState(string? SessionId, ProcessingStage Stage, string Text, double? Fraction)
{
    public static readonly ProcessingState Idle = new(null, ProcessingStage.None, "", null);
    public bool Busy => SessionId is not null;
}

/// <summary>A mix asked for in the archive: the sound of these tracks, over this picture (or none).</summary>
public sealed record MixRequest(IReadOnlyList<string> AudioTrackIds, string? VideoTrackId);

/// <summary>
/// Work after a recording, one session at a time: a seek index for Chrome tab files, lossless compression of the audio,
/// the mix of the tracks, then transcription in the separate worker process (a crash in native inference cannot take
/// the recorder down).
/// No new work starts while a recording is going; work already running continues at below-normal priority, so the
/// recording keeps the processor first.
/// </summary>
public sealed class ProcessingQueue(Archive archive, AppSettings settings, Func<bool> recordingActive) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<(string Id, bool Transcribe)> pending = [];
    private readonly Dictionary<string, MixRequest> mixes = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim signal = new(0);
    private CancellationTokenSource? current;
    private Task? loop;

    public ProcessingState State { get; private set; } = ProcessingState.Idle;
    /// <summary>Raised on a background thread whenever the state or a session's processing status changes.</summary>
    public event Action? Changed;

    public void Start() => loop ??= Task.Run(LoopAsync);

    /// <summary>Queue a finished session: prepare its files and, if asked, transcribe it.</summary>
    public void Enqueue(CallSession session, bool transcribe)
    {
        if (transcribe)
            archive.Update(session.Id, s => { s.TranscriptionStatus = TranscriptionStatus.Queued; s.TranscriptionError = null; });
        lock (gate)
        {
            var at = pending.FindIndex(x => x.Id == session.Id);
            if (at >= 0) pending[at] = (session.Id, pending[at].Transcribe || transcribe);
            else pending.Add((session.Id, transcribe));
        }
        Wake();
        Changed?.Invoke();
    }

    /// <summary>Mix a session again with the tracks chosen in the archive; replaces its previous mix.</summary>
    public void RequestMix(CallSession session, MixRequest request)
    {
        lock (gate) mixes[session.Id] = request;
        Enqueue(session, transcribe: false);
    }

    /// <summary>After start-up: transcriptions that were queued or interrupted, and audio not yet compressed or mixed.</summary>
    public void Resume(IEnumerable<CallSession> sessions)
    {
        foreach (var s in sessions.Where(s => !s.IsRecording))
        {
            var transcribe = s.TranscriptionStatus == TranscriptionStatus.Queued;
            if (transcribe || NeedsPreparation(s)) Enqueue(s, transcribe);
        }
    }

    /// <summary>Something that blocked work has changed: a model was downloaded, a recording ended.</summary>
    public void Wake() { if (signal.CurrentCount == 0) signal.Release(); }

    public void CancelCurrent() => current?.Cancel();

    public bool IsQueued(string sessionId) { lock (gate) return pending.Any(x => x.Id == sessionId); }

    private bool NeedsCompression(CallSession s) =>
        settings.CompressAudio && !s.AudioCompressed && s.Tracks.Any(t => t.HasAudio && !t.HasVideo);

    private static bool NeedsIndexing(CallSession s) => !s.TabsIndexed && s.Tracks.Any(t => t.Kind == SourceKind.BrowserTab);

    /// <summary>The automatic mix is made for recordings of this version on: an update does not reprocess the whole archive.</summary>
    private bool NeedsMix(CallSession s) => settings.AutoMix && !s.MixChecked && s.SchemaVersion >= CallSession.CurrentSchema;

    private bool MixRequested(CallSession s) { lock (gate) return mixes.ContainsKey(s.Id); }

    private bool NeedsPreparation(CallSession s) => NeedsIndexing(s) || NeedsCompression(s) || NeedsMix(s) || MixRequested(s);

    private async Task LoopAsync()
    {
        var token = lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var job = recordingActive() ? null : Next();
                if (job is null)
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(3), token);
                    continue;
                }
                await RunAsync(job.Value.Id, job.Value.Transcribe, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Error("Processing loop", e); }
        }
    }

    /// <summary>The first job that can run now. A transcription waits for its model; preparing files never waits.</summary>
    private (string Id, bool Transcribe)? Next()
    {
        var modelReady = ModelManager.IsInstalled(ModelManager.Find(settings.Model));
        lock (gate)
        {
            foreach (var job in pending)
            {
                var session = archive.Get(job.Id);
                if (session is null) { pending.Remove(job); return Next(); }
                if (NeedsPreparation(session) || (job.Transcribe && modelReady))
                {
                    pending.Remove(job);
                    return job;
                }
                if (!job.Transcribe) { pending.Remove(job); return Next(); }
            }
            return null;
        }
    }

    private async Task RunAsync(string id, bool transcribe, CancellationToken token)
    {
        current = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = current.Token;
        try
        {
            var session = archive.Get(id);
            if (session is null) return;
            if (NeedsIndexing(session)) await IndexAsync(session, ct);
            if (NeedsCompression(session)) await CompressAsync(session, ct);
            MixRequest? request;
            lock (gate) mixes.Remove(id, out request);
            if (request is not null) await MixAsync(session, Manual(session, request), ct);
            else if (NeedsMix(session)) await MixAsync(session, MixPlan.Automatic(session, t => MediaTools.TrackFiles(archive.TrackFolder(session, t)).Count > 0), ct);
            if (transcribe)
            {
                if (ModelManager.IsInstalled(ModelManager.Find(settings.Model))) await TranscribeAsync(id, ct);
                else lock (gate) pending.Add((id, true)); // the model is not there yet: wait for it
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            archive.Update(id, s => { if (s.TranscriptionStatus == TranscriptionStatus.Running) s.TranscriptionStatus = TranscriptionStatus.Cancelled; });
            Log.Info($"Processing cancelled: {id}");
        }
        finally
        {
            current.Dispose();
            current = null;
            Report(ProcessingState.Idle);
        }
    }

    private async Task IndexAsync(CallSession session, CancellationToken ct)
    {
        Report(new(session.Id, ProcessingStage.Prepare, $"Подготовка «{session.Title}»", null));
        try
        {
            var count = await TabIndexer.IndexAsync(archive, session, new Progress<string>(text => Report(new(session.Id, ProcessingStage.Prepare, text, null))), ct);
            Log.Info($"Indexed {count} Chrome tab files of {session.Id}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The files stay as Chrome wrote them: they play and transcribe, only scrolling in other players suffers.
            Log.Error($"Indexing failed for {session.Id}", e);
        }
        archive.Update(session.Id, s => { s.TabsIndexed = true; s.Tracks = session.Tracks; });
    }

    private async Task CompressAsync(CallSession session, CancellationToken ct)
    {
        Report(new(session.Id, ProcessingStage.Compress, $"Сжатие «{session.Title}»", null));
        try
        {
            var saved = await AudioCompactor.CompactAsync(archive, session, new Progress<string>(text => Report(new(session.Id, ProcessingStage.Compress, text, null))), ct);
            archive.Update(session.Id, s => { s.AudioCompressed = true; s.Tracks = session.Tracks; });
            Log.Info($"Compressed {session.Id}: saved {Display.Size(saved)}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The originals stay as they are; the recording is fine, it just keeps taking more space.
            archive.Update(session.Id, s => s.AudioCompressed = true);
            Log.Error($"Compression failed for {session.Id}", e);
        }
    }

    private static MixPlan Manual(CallSession session, MixRequest request) => new(
        session.Tracks.Where(t => t.HasAudio && request.AudioTrackIds.Contains(t.Id)).ToArray(),
        session.Tracks.FirstOrDefault(t => t.HasVideo && t.Id == request.VideoTrackId));

    private async Task MixAsync(CallSession session, MixPlan? plan, CancellationToken ct)
    {
        if (plan is null)
        {
            archive.Update(session.Id, s => s.MixChecked = true); // nothing worth mixing
            return;
        }
        var text = $"Сведение «{session.Title}»";
        Report(new(session.Id, ProcessingStage.Mix, text, 0));
        try
        {
            var mix = await Mixer.MixAsync(archive, session, plan, new Progress<double>(f => Report(new(session.Id, ProcessingStage.Mix, text, f))), ct);
            archive.Update(session.Id, s => { s.Mix = mix; s.MixChecked = true; s.MixError = null; s.Tracks = session.Tracks; });
            Log.Info($"Mixed {session.Id}: {plan.Audio.Count} sound tracks{(plan.Video is null ? "" : " over " + plan.Video.Kind)}");
        }
        catch (OperationCanceledException)
        {
            archive.Update(session.Id, s => { s.MixChecked = true; s.Tracks = session.Tracks; s.Mix = session.Mix; });
            throw;
        }
        catch (Exception e)
        {
            // The tracks stay as they were recorded; the mix can be made again from the archive.
            archive.Update(session.Id, s => { s.MixChecked = true; s.MixError = e.Message; s.Tracks = session.Tracks; s.Mix = session.Mix; });
            Log.Error($"Mix failed for {session.Id}", e);
        }
    }

    private async Task TranscribeAsync(string id, CancellationToken ct)
    {
        var session = archive.Update(id, s => { s.TranscriptionStatus = TranscriptionStatus.Running; s.TranscriptionError = null; });
        if (session is null) return;
        Report(new(id, ProcessingStage.Transcribe, $"Расшифровка «{session.Title}»", 0));
        var folder = archive.Folder(session);
        var configPath = Path.Combine(folder, ".transcription-settings.json");
        var resultPath = Path.Combine(folder, "transcript-result.json");
        try
        {
            AppPaths.AtomicJson(configPath, new AppSettings
            {
                Model = settings.Model, Language = settings.Language, CpuThreads = settings.CpuThreads, ArchiveRoot = archive.Root
            });
            var worker = Path.Combine(AppContext.BaseDirectory, "CallDock.Worker.exe");
            using var process = new Process { StartInfo = MediaTools.StartInfo(worker, ["transcribe", archive.Root, id, configPath]) };
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdout = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    var parts = line.Split(' ', 3);
                    if (parts is ["progress", var fraction, var text] && double.TryParse(fraction, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        Report(new(id, ProcessingStage.Transcribe, $"Расшифровка «{session.Title}» · {text}", f));
                }
            });
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            finally { await stdout; }
            var error = (await stderr).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrEmpty(error) ? $"Модуль распознавания завершился с кодом {process.ExitCode}." : error[^Math.Min(error.Length, 600)..]);
            var transcript = JsonSerializer.Deserialize<List<TranscriptSegment>>(await File.ReadAllTextAsync(resultPath, ct), AppPaths.Json) ?? [];
            archive.Update(id, s => { s.Transcript = transcript; s.TranscriptionStatus = TranscriptionStatus.Done; s.TranscriptionError = null; });
            Log.Info($"Transcribed {id}: {transcript.Count} segments");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            archive.Update(id, s => { s.TranscriptionStatus = TranscriptionStatus.Failed; s.TranscriptionError = e.Message; });
            Log.Error($"Transcription failed for {id}", e);
        }
        finally
        {
            File.Delete(configPath);
            File.Delete(resultPath);
        }
    }

    private void Report(ProcessingState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        current?.Cancel();
        if (loop is not null)
        {
            try { await loop; } catch (OperationCanceledException) { }
        }
        lifetime.Dispose();
    }
}
