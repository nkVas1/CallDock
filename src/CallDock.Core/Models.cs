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

/// <summary>
/// A phrase of the transcript. <paramref name="Source"/> is the speaker; <paramref name="Track"/> the track it was heard
/// on (absent in transcripts of versions before 1.3); <paramref name="Original"/> the text as recognized, kept while the
/// person's correction differs from it.
/// </summary>
public sealed record TranscriptSegment(double Start, double End, string Source, string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Track = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Original = null);
public sealed record Bookmark(double Seconds, string Text);
/// <summary>A pause of a recording: where it is on the timeline (seconds recorded before it) and how long it lasted.</summary>
public sealed record RecordingPause(double At, double Seconds);

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
    /// <summary>Where the track ends on the session timeline, in seconds; 0 when unknown (recordings of older versions).</summary>
    public double EndSeconds { get; set; }
    public string Format { get; set; } = "";
    public string Status { get; set; } = "Recording";
    public string? Error { get; set; }
    public bool HasAudio { get; set; } = true;
    public bool HasVideo { get; set; }
    /// <summary>The source this track recorded (<see cref="SourceSpec.Key"/>). A source switched off and on again during
    /// a recording gets a new track with the same name: the same speaker in the transcript.</summary>
    public string? SourceKey { get; set; }
    /// <summary>A screen or window video that also carries the mixed sound of the session (see <see cref="SessionMix"/>).
    /// That sound is not the track's own: it is not transcribed again.</summary>
    public bool HasMixedAudio { get; set; }
}

/// <summary>
/// The tracks of a recording mixed into one file: a screen video with everyone's sound, or the sound alone.
/// A screen or window video receives the mix in its own file (<see cref="TrackId"/>), so the picture is not stored twice;
/// otherwise the mix is a file of its own in the session folder (<see cref="File"/>).
/// </summary>
public sealed class SessionMix
{
    public string? TrackId { get; set; }
    public string? File { get; set; }
    /// <summary>Where the mix starts on the session timeline, in seconds.</summary>
    public double OffsetSeconds { get; set; }
    public bool HasVideo { get; set; }
    /// <summary>The tracks whose sound is in the mix.</summary>
    public List<string> Tracks { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CallSession
{
    /// <summary>3: sources can join and leave during a recording, tracks carry their source, recordings are mixed.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;
    public const int CurrentSchema = 3;
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
    public SessionMix? Mix { get; set; }
    /// <summary>The automatic mix was considered: made, not needed (one track, independent broadcasts) or failed.</summary>
    public bool MixChecked { get; set; }
    public string? MixError { get; set; }
    public string Folder { get; set; } = "";
    public List<RecordingTrack> Tracks { get; set; } = [];
    public List<TranscriptSegment> Transcript { get; set; } = [];
    /// <summary>The person changed the transcript: corrected phrases, renamed speakers, removed phrases. A new
    /// transcription replaces those changes, so it asks first.</summary>
    public bool TranscriptEdited { get; set; }
    public List<Bookmark> Bookmarks { get; set; } = [];
    /// <summary>The pauses of the recording, cut out of every track.</summary>
    public List<RecordingPause> Pauses { get; set; } = [];

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
    /// <summary>After a recording, mix its tracks into one file: the screen video gets everyone's sound.</summary>
    public bool AutoMix { get; set; } = true;
    /// <summary>What to do when another application starts using the microphone: off | ask | auto.</summary>
    public string CallDetection { get; set; } = "ask";
    /// <summary>Ctrl+Alt+R starts and stops recording, Ctrl+Alt+P pauses it, Ctrl+Alt+M sets a bookmark — from any application.</summary>
    public bool GlobalHotkeys { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>system | dark | light</summary>
    public string Theme { get; set; } = "system";
    public bool TrayHintShown { get; set; }
    /// <summary>Draw the window without the GPU: for old or broken video drivers that leave WPF windows blank.</summary>
    public bool SoftwareRendering { get; set; }
    public List<SourceSpec> SavedSources { get; set; } = [];
    /// <summary>Saved sources switched off on the console (<see cref="SourceSpec.Key"/>): kept, but not recorded.</summary>
    public List<string> DisabledSources { get; set; } = [];
    public string LastProject { get; set; } = "";
}

public static class Display
{
    public static string Duration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");

    /// <summary>A length the way people say it: «40 с», «5 мин», «1 ч 12 мин».</summary>
    public static string Span(double seconds)
    {
        var whole = (long)Math.Round(Math.Max(0, seconds));
        if (whole < 60) return $"{whole} с";
        var minutes = (whole + 30) / 60;
        if (minutes < 60) return $"{minutes} мин";
        return minutes % 60 == 0 ? $"{minutes / 60} ч" : $"{minutes / 60} ч {minutes % 60} мин";
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:F1} МБ",
        _ => $"{bytes / 1073741824.0:F2} ГБ"
    };
}
