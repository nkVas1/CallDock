using CallDock.Core;
using ScreenRecorderLib;

namespace CallDock.App.Services;

public sealed class VideoCapture : IRecording
{
    private readonly Recorder recorder;
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string file;
    private string? error;
    private Task? stopTask;
    public RecordingTrack Track { get; }
    public float Peak => 0;
    public long BytesWritten => File.Exists(file) ? new FileInfo(file).Length : 0;
    public string? Error => error;
    /// <summary>When the recorder reported that it records (<see cref="System.Diagnostics.Stopwatch"/> ticks): where the video
    /// starts on the timeline, unless its end says otherwise (see <see cref="Ended"/>).</summary>
    public long StartedAt { get; private set; }
    /// <summary>When the capture was stopped (ticks) and how long the finished file is: the end of the video is known
    /// exactly, while the start is reported late by the recorder on some computers.</summary>
    public (long StoppedAt, double Duration) Ended { get; private set; }

    public static IReadOnlyList<SourceSpec> List() => Recorder.GetDisplays()
        .Select(x => new SourceSpec(SourceKind.Screen, x.FriendlyName, x.DeviceName))
        .Concat(Recorder.GetWindows().Where(x => x.Pid != Environment.ProcessId)
            .Select(x => new SourceSpec(SourceKind.Window, x.Title, x.Handle.ToInt64().ToString()))).ToArray();

    public VideoCapture(SourceSpec spec, RecordingTrack track, string folder, int fps)
    {
        Track = track;
        track.HasVideo = true;
        track.HasAudio = false;
        track.Format = $"H.264 · {fps} fps";
        Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "00000.mp4");
        RecordingSourceBase source = spec.Kind == SourceKind.Window
            ? new WindowRecordingSource(new IntPtr(long.Parse(spec.Target)))
            : new DisplayRecordingSource(spec.Target);
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [source] },
            OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video, OutputFrameSize = new ScreenSize(1920, 1080), Stretch = StretchMode.Uniform },
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = fps, Bitrate = 6_000_000, IsHardwareEncodingEnabled = true,
                // A fixed frame rate keeps the video in real time: with a variable one, frames that a busy computer
                // captures late are still stamped by the nominal rate, the video runs fast and drifts from the sound.
                IsFragmentedMp4Enabled = true, IsMp4FastStartEnabled = false, IsFixedFramerate = true
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = true, IsMouseClicksDetected = false }
        };
        recorder = Recorder.CreateRecorder(options);
        recorder.OnStatusChanged += (_, e) =>
        {
            if (e.Status != RecorderStatus.Recording || started.Task.IsCompleted) return;
            StartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            started.TrySetResult();
        };
        recorder.OnRecordingComplete += (_, _) => completed.TrySetResult();
        recorder.OnRecordingFailed += (_, e) => { error = e.Error; started.TrySetException(new InvalidOperationException(e.Error)); completed.TrySetResult(); };
        recorder.Record(file);
    }

    public async Task WaitStartedAsync() => await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
    public Task StopAsync() => stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        var stoppedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        recorder.Stop();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
        catch (TimeoutException) { error = "Видеодвижок не завершил запись вовремя. Проверьте сохранённый MP4."; }
        finally { recorder.Dispose(); }
        Track.Status = error is null ? "Done" : "Failed";
        Track.Error = error;
        try { if (File.Exists(file)) Ended = (stoppedAt, await MediaTools.DurationAsync(file)); }
        catch (Exception e) { Log.Warn("Screen video length unknown: " + e.Message); } // the start stays as reported
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
