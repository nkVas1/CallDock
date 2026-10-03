using System.Text.Json.Serialization;

namespace CallDock.Core;

public enum SourceKind { Microphone, SystemAudio, Application, Screen, Window, Stream, BrowserTab }

/// <summary>A capture source. <paramref name="Name"/> is the device, window or stream name as the system reports it;
/// <paramref name="Label"/> is what the person calls it («Я», «Собеседники») — it becomes the track and speaker name.</summary>
public sealed record SourceSpec(SourceKind Kind, string Name, string Target, string? Label = null)
{
    /// <summary>The source identity: two specs with the same key record the same thing.</summary>
    [JsonIgnore] public string Key => $"{Kind}:{Target}";
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : Label;
    [JsonIgnore] public bool HasAudio => Kind is SourceKind.Microphone or SourceKind.SystemAudio or SourceKind.Application
        or SourceKind.Stream or SourceKind.BrowserTab;
}

public sealed record TranscriptSegment(double Start, double End, string Source, string Text);
public sealed record Bookmark(double Seconds, string Text);

public sealed class RecordingTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The speaker or source label shown in the archive and in the transcript.</summary>
    public string Name { get; set; } = "";
    /// <summary>The device, window or stream behind the track.</summary>
    public string Device { get; set; } = "";
    public SourceKind Kind { get; set; }
    public string Directory { get; set; } = "";
    public double OffsetSeconds { get; set; }
    public string Format { get; set; } = "";
    public string Status { get; set; } = "Recording";
    public string? Error { get; set; }
    public bool HasAudio { get; set; } = true;
    public bool HasVideo { get; set; }
}

public sealed class CallSession
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Project { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public double DurationSeconds { get; set; }
    public string Status { get; set; } = SessionStatus.Recording;
    public string TranscriptionStatus { get; set; } = Core.TranscriptionStatus.NotStarted;
    public string? TranscriptionError { get; set; }
    /// <summary>Audio tracks were converted from WAV to lossless FLAC after recording.</summary>
    public bool AudioCompressed { get; set; }
    /// <summary>Chrome tab recordings were rewritten with a length and a seek index, which MediaRecorder does not write.</summary>
    public bool TabsIndexed { get; set; }
    public string Folder { get; set; } = "";
    public List<RecordingTrack> Tracks { get; set; } = [];
    public List<TranscriptSegment> Transcript { get; set; } = [];
    public List<Bookmark> Bookmarks { get; set; } = [];

    [JsonIgnore] public bool IsRecording => Status == SessionStatus.Recording;
    [JsonIgnore] public string DateLabel => StartedAt.ToLocalTime().ToString("dd.MM.yyyy · HH:mm");
    [JsonIgnore] public string DurationLabel => Display.Duration(DurationSeconds);
    [JsonIgnore] public string StatusLabel => Status switch
    {
        SessionStatus.Recording => "Идёт запись",
        SessionStatus.Interrupted => "Восстановлена после сбоя",
        SessionStatus.Partial => "Ошибка одного из источников",
        _ => TranscriptionStatus switch
        {
            Core.TranscriptionStatus.Done => Transcript.Count > 0 ? "Расшифровано" : "Речь не найдена",
            Core.TranscriptionStatus.Running => "Расшифровка…",
            Core.TranscriptionStatus.Queued => "В очереди на расшифровку",
            Core.TranscriptionStatus.Failed => "Ошибка расшифровки",
            Core.TranscriptionStatus.Cancelled => "Расшифровка отменена",
            _ => "Сохранено"
        }
    };
}

public static class SessionStatus
{
    public const string Recording = "Recording";
    public const string Done = "Done";
    public const string Partial = "Partial";
    public const string Interrupted = "Interrupted";
}

public static class TranscriptionStatus
{
    public const string NotStarted = "NotStarted";
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed class AppSettings
{
    public string ArchiveRoot { get; set; } = AppPaths.DefaultArchiveRoot;
    public string Model { get; set; } = ModelManager.Recommended.Id;
    public string Language { get; set; } = "auto";
    public bool AutoTranscribe { get; set; } = true;
    public int CpuThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
    public int VideoFps { get; set; } = 24;
    /// <summary>Convert finished WAV tracks to lossless FLAC: about a third of the size, same sound.</summary>
    public bool CompressAudio { get; set; } = true;
    /// <summary>Ctrl+Alt+R starts and stops recording, Ctrl+Alt+M sets a bookmark — from any application.</summary>
    public bool GlobalHotkeys { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>system | dark | light</summary>
    public string Theme { get; set; } = "system";
    public bool TrayHintShown { get; set; }
    /// <summary>Draw the window without the GPU: for old or broken video drivers that leave WPF windows blank.</summary>
    public bool SoftwareRendering { get; set; }
    public List<SourceSpec> SavedSources { get; set; } = [];
    public string LastProject { get; set; } = "";
}

public static class Display
{
    public static string Duration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:F1} МБ",
        _ => $"{bytes / 1073741824.0:F2} ГБ"
    };
}
