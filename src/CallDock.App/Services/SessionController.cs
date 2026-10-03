using CallDock.Core;
using System.Diagnostics;

namespace CallDock.App.Services;

/// <summary>
/// One recording session at a time: starts every chosen source as its own track, checkpoints the session
/// to disk while it runs, and stops all sources together. Chrome tabs join the running session (or start one)
/// through the browser bridge.
/// </summary>
public sealed class SessionController(Archive archive, AppSettings settings)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, IRecording> bySource = [];
    private readonly List<IRecording> recordings = [];
    private readonly Stopwatch clock = new();
    private long sessionStartedAt;

    /// <summary>Raised on a background thread when the session starts, stops or gains a track.</summary>
    public event Action? Changed;
    /// <summary>Raised when the last tab of a session made only of Chrome tabs has finished: nothing records any more.</summary>
    public event Action? LastTabFinished;
    public CallSession? Current { get; private set; }
    public bool IsStopping { get; private set; }
    public bool IsRecording => Current is not null;
    public double Elapsed => clock.Elapsed.TotalSeconds;
    public IReadOnlyList<IRecording> Recordings { get { lock (recordings) return recordings.ToArray(); } }

    /// <summary>The live recording of a source, if it is being recorded now.</summary>
    public IRecording? Find(SourceSpec spec) { lock (recordings) return bySource.GetValueOrDefault(spec.Key); }

    public async Task StartAsync(IReadOnlyList<SourceSpec> specs, string title, string project, string tags)
    {
        if (specs.Count == 0) throw new InvalidOperationException("Включите хотя бы один источник.");
        await gate.WaitAsync();
        try
        {
            if (Current is not null) throw new InvalidOperationException("Запись уже идёт.");
            Current = archive.Create(title, project, tags);
            clock.Restart();
            sessionStartedAt = Stopwatch.GetTimestamp();
            Log.Info($"Recording started: {Current.Id}, {specs.Count} sources");
            foreach (var spec in specs)
            {
                var track = NewTrack(spec.DisplayName, spec.Name, spec.Kind);
                try
                {
                    var recording = await StartSourceAsync(spec, track, archive.TrackFolder(Current, track));
                    if (recording is AudioCapture audio)
                        track.OffsetSeconds = Stopwatch.GetElapsedTime(sessionStartedAt, audio.CaptureStartedAt).TotalSeconds;
                    lock (recordings) { recordings.Add(recording); bySource[spec.Key] = recording; }
                }
                catch (Exception e)
                {
                    track.Status = "Failed";
                    track.Error = e.Message;
                    Log.Error($"Source failed to start: {spec.Kind} {spec.Name}", e);
                }
            }
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

    /// <summary>A new track of the current session. Names are unique within it: the transcript and the player tell tracks
    /// apart by name, so four tabs with the same title become «Трансляция», «Трансляция (2)» and so on.</summary>
    private RecordingTrack NewTrack(string name, string device, SourceKind kind)
    {
        var unique = name;
        for (var n = 2; Current!.Tracks.Any(t => string.Equals(t.Name, unique, StringComparison.CurrentCultureIgnoreCase)); n++) unique = $"{name} ({n})";
        var track = new RecordingTrack { Name = unique, Device = device, Kind = kind, OffsetSeconds = clock.Elapsed.TotalSeconds };
        track.Directory = Path.Combine("tracks", track.Id);
        Current!.Tracks.Add(track);
        archive.Save(Current);
        return track;
    }

    /// <summary>A Chrome tab asks to record: it joins the running session or starts a new one titled after the tab.</summary>
    public async Task<BrowserRecording> AddBrowserAsync(string title, bool video = true)
    {
        bool started = false;
        BrowserRecording recording;
        await gate.WaitAsync();
        try
        {
            if (IsStopping) throw new InvalidOperationException("Дождитесь остановки записи.");
            if (Current is null)
            {
                Current = archive.Create(title, settings.LastProject, "трансляция");
                clock.Restart();
                sessionStartedAt = Stopwatch.GetTimestamp();
                started = true;
            }
            if (Recordings.OfType<BrowserRecording>().Count(x => !x.Closed) >= BrowserBridge.MaxTabs)
                throw new InvalidOperationException($"Одновременно записывается не больше {BrowserBridge.MaxTabs} вкладок.");
            var track = NewTrack(title, "Вкладка Chrome", SourceKind.BrowserTab);
            recording = new BrowserRecording(track, archive.TrackFolder(Current, track), video);
            lock (recordings) { recordings.Add(recording); bySource[$"{SourceKind.BrowserTab}:{track.Id}"] = recording; }
            archive.Save(Current);
            Log.Info(started ? $"Recording started from Chrome: {Current.Id}" : "Chrome tab joined the recording");
        }
        finally { gate.Release(); }
        Changed?.Invoke();
        return recording;
    }

    /// <summary>A Chrome tab has finished. A session made only of tabs ends with its last one; a session with a microphone,
    /// system sound or the screen goes on until it is stopped in CallDock.</summary>
    public void TabFinished()
    {
        var all = Recordings;
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
            Changed?.Invoke();
            // Independent sources stop together; the Chrome bridge keeps accepting the tabs' final chunks meanwhile.
            await Task.WhenAll(Recordings.Select(async r =>
            {
                try { await r.StopAsync(); }
                catch (Exception e) { r.Track.Error = e.Message; r.Track.Status = "Failed"; }
            }));
            clock.Stop();
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
