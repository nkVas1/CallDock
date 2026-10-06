using CallDock.Core;
using System.Diagnostics;

namespace CallDock.App.Services;

/// <summary>
/// One recording session at a time. Every source is its own track, placed on the session timeline where it really
/// started: sources can join and leave while the recording goes on, and Chrome tabs join through the browser bridge
/// (or start a session of their own). The session is checkpointed to disk while it runs and stopped as a whole.
/// </summary>
public sealed class SessionController(Archive archive, AppSettings settings)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, IRecording> bySource = [];
    private readonly List<IRecording> recordings = [];
    private readonly Stopwatch clock = new();
    private long sessionStartedAt;
    /// <summary>The session start by the wall clock (Unix milliseconds): Chrome reports by it when a tab really started.</summary>
    private long sessionStartedUnixMs;

    /// <summary>Raised on a background thread when the session starts, stops, gains or loses a track.</summary>
    public event Action? Changed;
    /// <summary>Raised when the last tab of a session made only of Chrome tabs has finished: nothing records any more.</summary>
    public event Action? LastTabFinished;
    /// <summary>A source CallDock tried to add on its own (with a Chrome tab) did not start: its name and the reason.</summary>
    public event Action<string, string>? SourceFailed;
    public CallSession? Current { get; private set; }
    public bool IsStopping { get; private set; }
    public bool IsRecording => Current is not null;
    public double Elapsed => clock.Elapsed.TotalSeconds;
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
                clock.Reset();
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
        clock.Restart();
        sessionStartedAt = Stopwatch.GetTimestamp();
        sessionStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                AudioCapture audio => Stopwatch.GetElapsedTime(sessionStartedAt, audio.CaptureStartedAt).TotalSeconds,
                VideoCapture video => Stopwatch.GetElapsedTime(sessionStartedAt, video.StartedAt).TotalSeconds,
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
                return await AudioCapture.StartAsync(spec, track, folder);
            case SourceKind.Window or SourceKind.Screen:
                var video = new VideoCapture(spec, track, folder, settings.VideoFps);
                try { await video.WaitStartedAsync(); }
                catch { await video.DisposeAsync(); throw; }
                return video;
            case SourceKind.Stream:
                return new StreamCapture(spec, track, folder);
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
        var track = new RecordingTrack { Name = unique, Device = device, Kind = kind, SourceKey = sourceKey, OffsetSeconds = clock.Elapsed.TotalSeconds };
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
    /// </summary>
    public async Task<BrowserRecording> AddBrowserAsync(string title, bool video = true, bool withSources = false)
    {
        var started = false;
        BrowserRecording recording;
        await gate.WaitAsync();
        try
        {
            if (IsStopping) throw new InvalidOperationException("Дождитесь остановки записи.");
            if (Recordings.OfType<BrowserRecording>().Count(x => !x.Closed) >= BrowserBridge.MaxTabs)
                throw new InvalidOperationException($"Одновременно записывается не больше {BrowserBridge.MaxTabs} вкладок.");
            if (Current is null)
            {
                Begin(archive.Create(title, settings.LastProject, "трансляция"));
                started = true;
            }
            var track = NewTrack(title, "Вкладка Chrome", SourceKind.BrowserTab, sourceKey: null);
            recording = new BrowserRecording(track, archive.TrackFolder(Current!, track), video);
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
            if (Current is null || !Current.Tracks.Contains(tab.Track)) return;
            var offset = (unixMs - sessionStartedUnixMs) / 1000d;
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
        if (stoppedAt == 0 || duration <= 0) return;
        var start = Stopwatch.GetElapsedTime(sessionStartedAt, stoppedAt).TotalSeconds - duration;
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
            clock.Stop();
            foreach (var video in Recordings.OfType<VideoCapture>()) PlaceByEnd(video);
            var session = Current;
            session.DurationSeconds = stoppedAt;
            session.Status = session.Tracks.Count == 0 || session.Tracks.Any(t => t.Error is not null) ? SessionStatus.Partial : SessionStatus.Done;
            archive.Save(session);
            lock (recordings) { recordings.Clear(); bySource.Clear(); }
            Current = null;
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
