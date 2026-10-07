using CallDock.Core;
using System.Diagnostics;

namespace CallDock.App.Services;

/// <summary>
/// One recording session at a time. Every source is its own track, placed on the session timeline where it really
/// started: sources can join and leave while the recording goes on, and Chrome tabs join through the browser bridge
/// (or start a session of their own). The timeline is the time recorded: a pause stops it, and every source leaves the
/// pause out of its track. The session is checkpointed to disk while it runs and stopped as a whole.
/// </summary>
public sealed class SessionController(Archive archive, AppSettings settings)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, IRecording> bySource = [];
    private readonly List<IRecording> recordings = [];
    /// <summary>The time of the current recording; null between recordings.</summary>
    private RecordingClock? clock;
    /// <summary>The session start by the wall clock (Unix milliseconds): Chrome reports by it when a tab really started.</summary>
    private long sessionStartedUnixMs;
    /// <summary>The current pause: where it is on the timeline and when it began (a timestamp).</summary>
    private double pausedAt;
    private long pauseStartedAt;

    /// <summary>Raised on a background thread when the session starts, stops, gains or loses a track.</summary>
    public event Action? Changed;
    /// <summary>Raised when the last tab of a session made only of Chrome tabs has finished: nothing records any more.</summary>
    public event Action? LastTabFinished;
    /// <summary>A source CallDock tried to add on its own (with a Chrome tab) did not start: its name and the reason.</summary>
    public event Action<string, string>? SourceFailed;
    public CallSession? Current { get; private set; }
    public bool IsStopping { get; private set; }
    public bool IsRecording => Current is not null;
    /// <summary>Seconds recorded: the clock of the recording, standing still during a pause.</summary>
    public double Elapsed => clock?.Elapsed ?? 0;
    public bool IsPaused => clock?.IsPaused == true;
    /// <summary>How long the current pause has lasted, in seconds.</summary>
    public double PauseSeconds => IsPaused ? Stopwatch.GetElapsedTime(pauseStartedAt).TotalSeconds : 0;
    /// <summary>The sources recording now (a Chrome tab that has finished stays until the session ends).</summary>
    public IReadOnlyList<IRecording> Recordings { get { lock (recordings) return recordings.ToArray(); } }
    /// <summary>How many sources are still delivering: finished tabs do not count.</summary>
    public int ActiveCount => Recordings.Count(r => r is not BrowserRecording { Closed: true });

    /// <summary>The live recording of a source, if it is being recorded now.</summary>
    public IRecording? Find(SourceSpec spec) { lock (recordings) return bySource.GetValueOrDefault(spec.Key); }

    public async Task StartAsync(IReadOnlyList<SourceSpec> specs, string title, string project, string tags)
    {
        if (specs.Count == 0) throw new InvalidOperationException("Включите хотя бы один источник.");
        await gate.WaitAsync();
        try
        {
            if (Current is not null) throw new InvalidOperationException("Запись уже идёт.");
            Begin(archive.Create(title, project, tags));
            Log.Info($"Recording started: {Current!.Id}, {specs.Count} sources");
            foreach (var spec in specs) await StartTrackAsync(spec);
            archive.Save(Current);
            if (Recordings.Count == 0)
            {
                // Nothing records: say why, and leave no empty entry in the archive.
                var failed = Current;
                failed.Status = SessionStatus.Partial;
                Current = null;
                clock = null;
                archive.Delete(failed, toRecycleBin: false);
                throw new InvalidOperationException("Ни один источник не начал запись. " +
                    string.Join(" ", failed.Tracks.Select(t => $"{t.Name}: {t.Error}")));
            }
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    private void Begin(CallSession session)
    {
        Current = session;
        clock = RecordingClock.StartNew();
        sessionStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        (pausedAt, pauseStartedAt) = (0, 0);
    }

    /// <summary>Starts a source as a new track of the current session. A failure stays on the track and is returned.</summary>
    private async Task<(RecordingTrack Track, Exception? Failure)> StartTrackAsync(SourceSpec spec)
    {
        var track = NewTrack(spec.DisplayName, spec.Name, spec.Kind, spec.Key);
        try
        {
            var recording = await StartSourceAsync(spec, track, archive.TrackFolder(Current!, track));
            // Where the track begins on the timeline: the moment its first sound or frame was taken, not the request.
            track.OffsetSeconds = recording switch
            {
                AudioCapture audio => clock!.SecondsAt(audio.CaptureStartedAt),
                VideoCapture video => clock!.SecondsAt(video.StartedAt),
                _ => track.OffsetSeconds
            };
            lock (recordings) { recordings.Add(recording); bySource[spec.Key] = recording; }
            return (track, null);
        }
        catch (Exception e)
        {
            track.Status = "Failed";
            track.Error = e.Message;
            Log.Error($"Source failed to start: {spec.Kind} {spec.Name}", e);
            return (track, e);
        }
    }

    private async Task<IRecording> StartSourceAsync(SourceSpec spec, RecordingTrack track, string folder)
    {
        switch (spec.Kind)
        {
            case SourceKind.Microphone or SourceKind.SystemAudio or SourceKind.Application:
                if (spec.Kind == SourceKind.Application && !Capabilities.ApplicationAudio)
                    throw new PlatformNotSupportedException(Capabilities.ApplicationAudioHint);
                return await AudioCapture.StartAsync(spec, track, folder, clock);
            case SourceKind.Window or SourceKind.Screen:
                var video = new VideoCapture(spec, track, folder, settings.VideoFps);
                try { await video.WaitStartedAsync(); }
                catch { await video.DisposeAsync(); throw; }
                // Switched on during a pause: the video waits for the recording to go on (a frame at most is taken).
                if (IsPaused) video.Pause();
                return video;
            case SourceKind.Stream:
                return new StreamCapture(spec, track, folder, paused: IsPaused);
            default:
                throw new InvalidOperationException("Вкладки Chrome добавляются из расширения CallDock.");
        }
    }

    /// <summary>
    /// A new track of the current session. Names are unique within it — the transcript and the player tell tracks apart
    /// by name, so four tabs with the same title become «Трансляция», «Трансляция (2)»… — except that a source switched
    /// off and on again keeps its name: it is the same speaker.
    /// </summary>
    private RecordingTrack NewTrack(string name, string device, SourceKind kind, string? sourceKey)
    {
        var unique = name;
        var again = sourceKey is not null && Current!.Tracks.Any(t => t.SourceKey == sourceKey && t.Name == name);
        if (!again)
            for (var n = 2; Current!.Tracks.Any(t => string.Equals(t.Name, unique, StringComparison.CurrentCultureIgnoreCase)); n++) unique = $"{name} ({n})";
        var track = new RecordingTrack { Name = unique, Device = device, Kind = kind, SourceKey = sourceKey, OffsetSeconds = Elapsed };
        track.Directory = Path.Combine("tracks", track.Id);
        Current!.Tracks.Add(track);
        archive.Save(Current);
        return track;
    }

    /// <summary>A source joins the running recording: a new track that starts now. One that fails to start leaves no track.</summary>
    public async Task AddSourceAsync(SourceSpec spec)
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null || IsStopping) throw new InvalidOperationException("Запись не идёт.");
            if (Find(spec) is not null) throw new InvalidOperationException("Этот источник уже записывается.");
            var (track, failure) = await StartTrackAsync(spec);
            if (failure is not null)
            {
                Current.Tracks.Remove(track);
                try { if (Directory.Exists(archive.TrackFolder(Current, track))) Directory.Delete(archive.TrackFolder(Current, track), true); }
                catch (IOException) { }
                archive.Save(Current);
                throw new InvalidOperationException(failure.Message, failure);
            }
            archive.Save(Current);
            Log.Info($"Source joined the recording at {Display.Duration(track.OffsetSeconds)}: {spec.Kind}");
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    /// <summary>A source leaves the running recording: its track ends here, the others go on.</summary>
    public async Task StopSourceAsync(SourceSpec spec)
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null || IsStopping) return;
            IRecording? recording;
            lock (recordings)
            {
                if (!bySource.Remove(spec.Key, out recording)) return;
                recordings.Remove(recording);
            }
            recording.Track.EndSeconds = Elapsed;
            try { await recording.StopAsync(); }
            catch (Exception e) { recording.Track.Error = e.Message; recording.Track.Status = "Failed"; }
            if (recording is VideoCapture video) PlaceByEnd(video);
            archive.Save(Current);
            Log.Info($"Source left the recording at {Display.Duration(Elapsed)}: {spec.Kind}");
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    /// <summary>
    /// A Chrome tab asks to record: it joins the running session or starts a new one titled after the tab. With
    /// <paramref name="withSources"/> a new session also starts the sources switched on in CallDock — a microphone, the system sound.
    /// <paramref name="canPause"/>: the extension pauses the tab with the recording (1.2 and later).
    /// </summary>
    public async Task<BrowserRecording> AddBrowserAsync(string title, bool video = true, bool withSources = false, bool canPause = false)
    {
        var started = false;
        BrowserRecording recording;
        await gate.WaitAsync();
        try
        {
            if (IsStopping) throw new InvalidOperationException("Дождитесь остановки записи.");
            if (Recordings.OfType<BrowserRecording>().Count(x => !x.Closed) >= BrowserBridge.MaxTabs)
                throw new InvalidOperationException($"Одновременно записывается не больше {BrowserBridge.MaxTabs} вкладок.");
            if (IsPaused && !canPause)
                throw new InvalidOperationException("Запись CallDock на паузе, а эта версия расширения не умеет ставить вкладку на паузу. " +
                    "Продолжите запись в CallDock или обновите расширение: chrome://extensions → ↻ у CallDock.");
            if (Current is null)
            {
                Begin(archive.Create(title, settings.LastProject, "трансляция"));
                started = true;
            }
            var track = NewTrack(title, "Вкладка Chrome", SourceKind.BrowserTab, sourceKey: null);
            recording = new BrowserRecording(track, archive.TrackFolder(Current!, track), video, canPause);
            lock (recordings) { recordings.Add(recording); bySource[$"{SourceKind.BrowserTab}:{track.Id}"] = recording; }
            archive.Save(Current!);
            Log.Info(started ? $"Recording started from Chrome: {Current!.Id}{(withSources ? " with CallDock sources" : "")}" : "Chrome tab joined the recording");
        }
        finally { gate.Release(); }
        Changed?.Invoke();
        // Not inside the tab's request: a screen capture may take seconds to start, and Chrome waits for the answer.
        if (started && withSources) _ = JoinSavedSourcesAsync();
        return recording;
    }

    /// <summary>The sources switched on in CallDock join a recording that Chrome started.</summary>
    private async Task JoinSavedSourcesAsync()
    {
        foreach (var spec in settings.SavedSources.Where(s => !settings.DisabledSources.Contains(s.Key)).ToArray())
        {
            try { await AddSourceAsync(spec); }
            catch (Exception e)
            {
                Log.Warn($"Source did not join the Chrome recording: {spec.Kind}: {e.Message}");
                SourceFailed?.Invoke(spec.DisplayName, e.Message);
            }
        }
    }

    /// <summary>Chrome reports when a tab's recorder really started (wall clock): the track is placed there.</summary>
    public async Task TabStartedAsync(BrowserRecording tab, long unixMs)
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null || clock is null || !Current.Tracks.Contains(tab.Track)) return;
            var offset = clock.SecondsAt(clock.StartedAt + (long)((unixMs - sessionStartedUnixMs) / 1000d * Stopwatch.Frequency));
            // A report far from the request (a wrong clock, a stale message) is not trusted.
            if (Math.Abs(offset - tab.Track.OffsetSeconds) > 10) return;
            tab.Track.OffsetSeconds = Math.Max(0, offset);
            archive.Save(Current);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// A screen or window video is placed on the timeline by its end when its reported start disagrees: the moment it was
    /// stopped and the length of the file are exact, while the recorder announces its start late on some computers — and a
    /// video placed seconds off would be out of step with everyone's sound in the mix.
    /// </summary>
    private void PlaceByEnd(VideoCapture video)
    {
        var (stoppedAt, duration) = video.Ended;
        if (stoppedAt == 0 || duration <= 0 || clock is null) return;
        var start = clock.SecondsAt(stoppedAt) - duration;
        if (start < 0 || Math.Abs(start - video.Track.OffsetSeconds) <= 0.5) return;
        Log.Info($"Screen video placed by its end: {video.Track.OffsetSeconds:F2} s → {start:F2} s");
        video.Track.OffsetSeconds = start;
    }

    /// <summary>A Chrome tab has finished. A session made only of tabs ends with its last one; a session with a microphone,
    /// system sound or the screen goes on until it is stopped in CallDock.</summary>
    public void TabFinished()
    {
        var all = Recordings;
        foreach (var tab in all.OfType<BrowserRecording>().Where(t => t.Closed && t.Track.EndSeconds <= 0)) tab.Track.EndSeconds = Elapsed;
        Changed?.Invoke();
        if (!IsStopping && Current is not null && all.Count > 0 && all.All(r => r is BrowserRecording { Closed: true }))
            LastTabFinished?.Invoke();
    }

    /// <summary>
    /// Pauses the recording. Its time stands still, and every source leaves the pause out of its track: sound taken
    /// meanwhile is dropped, the screen video and Chrome tabs pause themselves, a network stream closes and continues the
    /// same track afterwards. The tracks stay in step, and nothing said during a pause is kept anywhere.
    /// </summary>
    public async Task PauseAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null || IsStopping || clock is null || clock.IsPaused) return;
            // A tab recorded by an extension before 1.2 would go on recording: rather no pause than a pause with a hole.
            if (Recordings.OfType<BrowserRecording>().FirstOrDefault(t => !t.Closed && !t.CanPause) is { } old)
                throw new InvalidOperationException($"Вкладку «{old.Track.Name}» записывает старая версия расширения CallDock, она не умеет " +
                    "вставать на паузу. Обновите расширение (chrome://extensions → ↻ у CallDock) — в следующих записях вкладок пауза заработает.");
            var now = Stopwatch.GetTimestamp();
            pausedAt = clock.SecondsAt(now);
            pauseStartedAt = now;
            clock.Pause(now);
            foreach (var recording in Recordings)
            {
                if (recording is VideoCapture video) video.Pause();
                else if (recording is StreamCapture stream) Observe(stream.PauseAsync(), "Stream pause");
            }
            Log.Info($"Recording paused at {Display.Duration(pausedAt)}");
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    /// <summary>The recording goes on from where it was paused; the pause is remembered with the session.</summary>
    public async Task ResumeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null || IsStopping || clock is null || !clock.IsPaused) return;
            var now = Stopwatch.GetTimestamp();
            clock.Resume(now);
            var length = Stopwatch.GetElapsedTime(pauseStartedAt, now).TotalSeconds;
            Current.Pauses.Add(new RecordingPause(pausedAt, length));
            foreach (var recording in Recordings)
            {
                if (recording is VideoCapture video) video.Resume();
                else if (recording is StreamCapture stream) Observe(stream.ResumeAsync(), "Stream resume");
            }
            archive.Save(Current);
            Log.Info($"Recording resumed after a pause of {Display.Duration(length)}");
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    /// <summary>A stream closes and reopens its reader in the background; a failure there is logged, the stream reports it.</summary>
    private static void Observe(Task task, string what) =>
        task.ContinueWith(t => Log.Error(what, t.Exception!.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>The title, project and tags can be corrected while the call is still going.</summary>
    public async Task UpdateDetailsAsync(string title, string project, string tags)
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null) return;
            if (!string.IsNullOrWhiteSpace(title)) Current.Title = title.Trim();
            Current.Project = project.Trim();
            Current.Tags = tags.Trim();
            archive.Save(Current);
        }
        finally { gate.Release(); }
    }

    /// <summary>Every few seconds: duration and source errors go to disk, so a crash loses at most this much.</summary>
    public async Task CheckpointAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (Current is null || IsStopping) return;
            Current.DurationSeconds = Elapsed;
            foreach (var capture in Recordings) if (capture.Error is not null) capture.Track.Error = capture.Error;
            archive.Save(Current);
            AppPaths.EnsureSpace(archive.Root, 200L * 1024 * 1024);
        }
        finally { gate.Release(); }
    }

    public async Task<Bookmark?> BookmarkAsync(string? text)
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null) return null;
            var bookmark = new Bookmark(Elapsed, string.IsNullOrWhiteSpace(text) ? $"Метка {Current.Bookmarks.Count + 1}" : text.Trim());
            Current.Bookmarks.Add(bookmark);
            archive.Save(Current);
            return bookmark;
        }
        finally { gate.Release(); }
    }

    public async Task<CallSession?> StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (Current is null) return null;
            IsStopping = true;
            var stoppedAt = Elapsed;
            foreach (var recording in Recordings.Where(r => r.Track.EndSeconds <= 0)) recording.Track.EndSeconds = stoppedAt;
            Changed?.Invoke();
            // Independent sources stop together; the Chrome bridge keeps accepting the tabs' final chunks meanwhile.
            await Task.WhenAll(Recordings.Select(async r =>
            {
                try { await r.StopAsync(); }
                catch (Exception e) { r.Track.Error = e.Message; r.Track.Status = "Failed"; }
            }));
            foreach (var video in Recordings.OfType<VideoCapture>()) PlaceByEnd(video);
            var session = Current;
            session.DurationSeconds = stoppedAt;
            session.Status = session.Tracks.Count == 0 || session.Tracks.Any(t => t.Error is not null) ? SessionStatus.Partial : SessionStatus.Done;
            archive.Save(session);
            lock (recordings) { recordings.Clear(); bySource.Clear(); }
            Current = null;
            clock = null;
            Log.Info($"Recording stopped: {session.Id}, {Display.Duration(stoppedAt)}, status {session.Status}");
            return session;
        }
        finally
        {
            IsStopping = false;
            gate.Release();
            Changed?.Invoke();
        }
    }
}
