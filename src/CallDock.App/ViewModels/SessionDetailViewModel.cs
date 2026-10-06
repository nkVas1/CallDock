using CallDock.App.Services;
using CallDock.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace CallDock.App.ViewModels;

public sealed partial class TranscriptRowViewModel(TranscriptSegment segment) : ObservableObject
{
    public TranscriptSegment Segment { get; } = segment;
    public string Time => Display.Duration(Segment.Start);
    public string Source => Segment.Source;
    public string Text => Segment.Text;
    [ObservableProperty] public partial bool IsCurrent { get; set; }
}

public sealed class TrackItemViewModel(RecordingTrack track, long size)
{
    public RecordingTrack Track { get; } = track;
    public string Name => Track.Name;
    public string Details => string.Join(" · ", new[] { Track.Device, Span, Track.Format, Sound, Display.Size(size) }.Where(x => !string.IsNullOrWhiteSpace(x)));
    /// <summary>«00:00:05–00:00:13» for a source that joined or left during the recording.</summary>
    private string? Span => Track.EndSeconds > 0 ? $"{Display.Duration(Track.OffsetSeconds)}–{Display.Duration(Track.EndSeconds)}" : null;
    private string? Sound => Track is { HasVideo: true, HasAudio: false } ? Track.HasMixedAudio ? "со звуком сведения" : "без звука" : null;
    public string? Error => Track.Error;
    public bool CanListen => Track.HasAudio || Track.HasMixedAudio;
    public bool CanWatch => Track.HasVideo;
    public SymbolRegular Icon => Track.Kind switch
    {
        SourceKind.Microphone => SymbolRegular.Mic24,
        SourceKind.SystemAudio => SymbolRegular.Speaker224,
        SourceKind.Application => SymbolRegular.AppGeneric24,
        SourceKind.Screen => SymbolRegular.Desktop24,
        SourceKind.Window => SymbolRegular.Window24,
        SourceKind.BrowserTab => SymbolRegular.Tab24,
        _ => SymbolRegular.Live24
    };
}

public sealed record BookmarkRow(Bookmark Bookmark)
{
    public string Time => Display.Duration(Bookmark.Seconds);
    public string Text => Bookmark.Text;
}

/// <summary>One recording opened in the archive: its details (saved as you type), transcript, tracks and bookmarks.</summary>
public sealed partial class SessionDetailViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool loading;

    public SessionDetailViewModel(AppHost host, CallSession session)
    {
        this.host = host;
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveDetails(); };
        Load(session);
    }

    public CallSession Session { get; private set; } = null!;
    /// <summary>Asks the page to play a track or the mix from a moment of the session (seconds from its start).</summary>
    public event Action<PlaybackSource, double>? PlayRequested;
    public event Action<PlaybackSource, double>? WatchRequested;
    /// <summary>Asks the page to let the person choose what to mix; the page calls <see cref="RequestMix"/>.</summary>
    public event Action? MixRequested;
    /// <summary>The phrase being played changed: the page keeps it in view.</summary>
    public event Action<TranscriptRowViewModel>? CurrentRowChanged;

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Project { get; set; } = "";
    [ObservableProperty] public partial string Tags { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";
    [ObservableProperty] public partial string Meta { get; set; } = "";
    [ObservableProperty] public partial bool IsReadOnly { get; set; }
    [ObservableProperty] public partial string TranscriptFilter { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<TranscriptRowViewModel> TranscriptRows { get; set; } = [];
    [ObservableProperty] public partial bool HasTranscript { get; set; }
    [ObservableProperty] public partial string TranscriptHint { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<TrackItemViewModel> Tracks { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<BookmarkRow> Bookmarks { get; set; } = [];
    [ObservableProperty] public partial string? Banner { get; set; }
    [ObservableProperty] public partial InfoBarSeverity BannerSeverity { get; set; }
    [ObservableProperty] public partial bool IsProcessing { get; set; }
    [ObservableProperty] public partial string ProcessingText { get; set; } = "";
    [ObservableProperty] public partial double ProcessingFraction { get; set; }
    [ObservableProperty] public partial bool ProcessingIndeterminate { get; set; }
    [ObservableProperty] public partial string TranscribeButtonText { get; set; } = "Расшифровать";
    [ObservableProperty] public partial bool HasMix { get; set; }
    [ObservableProperty] public partial bool MixHasVideo { get; set; }
    [ObservableProperty] public partial string MixTitle { get; set; } = "";
    [ObservableProperty] public partial string MixDetails { get; set; } = "";
    [ObservableProperty] public partial string MixButtonText { get; set; } = "Свести дорожки…";

    /// <summary>The mix of this recording, if it has one on disk.</summary>
    private PlaybackSource? Mix => PlaybackSource.MixOf(host.Archive, Session);

    private IReadOnlyList<TranscriptRowViewModel> allRows = [];
    private TranscriptRowViewModel? currentRow;

    public void Load(CallSession session)
    {
        loading = true;
        Session = session;
        Title = session.Title;
        Project = session.Project;
        Tags = session.Tags;
        Notes = session.Notes;
        IsReadOnly = session.IsRecording;
        allRows = session.Transcript.Select(s => new TranscriptRowViewModel(s)).ToArray();
        currentRow = null;
        HasTranscript = allRows.Count > 0;
        ApplyFilter();
        Bookmarks = session.Bookmarks.OrderBy(b => b.Seconds).Select(b => new BookmarkRow(b)).ToArray();
        var transcribed = session.Transcript.Count > 0 || session.TranscriptionStatus == TranscriptionStatus.Done;
        TranscribeButtonText = transcribed ? "Расшифровать заново" : "Расшифровать";
        TranscriptHint = session.TranscriptionStatus switch
        {
            TranscriptionStatus.Done => "Речь не найдена: в дорожках нет разборчивой речи. Если она там есть, выберите в настройках модель «Высокое качество» и расшифруйте заново.",
            TranscriptionStatus.Queued => !ModelManager.IsInstalled(ModelManager.Find(host.Settings.Model))
                ? "Расшифровка начнётся, когда будет скачана модель: Настройки → Распознавание речи."
                : host.Recorder.IsRecording
                    ? "Расшифровка в очереди: начнётся, когда закончится запись."
                    : "Расшифровка в очереди: начнётся после текущей.",
            TranscriptionStatus.Running => "Идёт расшифровка…",
            TranscriptionStatus.Failed => "Расшифровка не удалась. Нажмите «Расшифровать», чтобы попробовать снова.",
            _ => "Текста пока нет. Нажмите «Расшифровать» — речь распознаётся на этом компьютере, аудио никуда не уходит."
        };
        UpdateBanner();
        UpdateMix(null);
        loading = false;
        _ = LoadSizesAsync(session);
    }

    private async Task LoadSizesAsync(CallSession session)
    {
        var (tracks, total) = await Task.Run(() =>
        {
            var items = session.Tracks.Select(t => new TrackItemViewModel(t, MediaTools.FolderSize(host.Archive.TrackFolder(session, t)))).ToArray();
            return (items, MediaTools.FolderSize(host.Archive.Folder(session)));
        });
        if (Session.Id != session.Id) return;
        Tracks = tracks;
        UpdateMix(await Task.Run(() => Mix?.Files.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0)));
        var parts = new List<string> { session.DateLabel, session.DurationLabel, Display.Size(total) };
        parts.Add($"{session.Tracks.Count} {(session.Tracks.Count == 1 ? "дорожка" : session.Tracks.Count is >= 2 and <= 4 ? "дорожки" : "дорожек")}");
        Meta = string.Join(" · ", parts);
    }

    /// <summary>The «Сведение» card: what the mix is, or how to make one.</summary>
    private void UpdateMix(long? size)
    {
        var mix = Session.Mix is not null ? Mix : null;
        HasMix = mix is not null;
        MixHasVideo = mix?.HasVideo == true;
        MixButtonText = HasMix ? "Свести заново…" : "Свести дорожки…";
        if (mix is null)
        {
            MixTitle = "Сведения нет";
            MixDetails = Session.MixError is { } error
                ? "Сведение не получилось: " + error
                : "Соберите дорожки в один файл: видео со звуком всех участников или общий звук.";
            return;
        }
        var voices = Session.Mix!.Tracks.Count;
        MixTitle = MixHasVideo ? "Сведение · видео со звуком" : "Сведение · все голоса";
        MixDetails = string.Join(" · ", new[]
        {
            $"{voices} {(voices == 1 ? "дорожка звука" : voices is >= 2 and <= 4 ? "дорожки звука" : "дорожек звука")}",
            Path.GetExtension(mix.Files[0]).TrimStart('.').ToUpperInvariant(),
            size is > 0 ? Display.Size(size.Value) : null
        }.Where(x => x is not null));
    }

    private void UpdateBanner()
    {
        var errors = Session.Tracks.Where(t => t.Error is not null).Select(t => $"{t.Name}: {t.Error}").ToArray();
        (Banner, BannerSeverity) = Session switch
        {
            { IsRecording: true } => ("Идёт запись. Изменить название и теги можно на странице «Запись».", InfoBarSeverity.Informational),
            { Status: SessionStatus.Interrupted } => ("Запись прервалась (сбой или выключение компьютера) и была восстановлена. Сохранено всё до момента сбоя.", InfoBarSeverity.Warning),
            { TranscriptionStatus: TranscriptionStatus.Failed } => ("Расшифровка не удалась: " + Session.TranscriptionError, InfoBarSeverity.Error),
            _ when errors.Length > 0 => (string.Join(" · ", errors), InfoBarSeverity.Warning),
            { MixError: { } mixError } => ("Сведение не получилось: " + mixError + " Дорожки целы; свести можно заново на вкладке «Дорожки».", InfoBarSeverity.Warning),
            _ => ((string?)null, InfoBarSeverity.Informational)
        };
    }

    public void ApplyProcessing(ProcessingState state)
    {
        IsProcessing = state.SessionId == Session.Id;
        if (!IsProcessing) return;
        ProcessingText = state.Text;
        ProcessingIndeterminate = state.Fraction is null;
        ProcessingFraction = (state.Fraction ?? 0) * 100;
    }

    partial void OnTitleChanged(string value) => ScheduleSave();
    partial void OnProjectChanged(string value) => ScheduleSave();
    partial void OnTagsChanged(string value) => ScheduleSave();
    partial void OnNotesChanged(string value) => ScheduleSave();
    partial void OnTranscriptFilterChanged(string value) => ApplyFilter();

    private void ScheduleSave()
    {
        if (loading || IsReadOnly) return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>Saves only the fields this page edits, on top of whatever background work wrote meanwhile.</summary>
    public void SaveDetails()
    {
        if (loading || IsReadOnly) return;
        saveTimer.Stop();
        try
        {
            var updated = host.Archive.Update(Session.Id, s =>
            {
                if (s.IsRecording) return;
                if (!string.IsNullOrWhiteSpace(Title)) s.Title = Title.Trim();
                s.Project = Project.Trim();
                s.Tags = Tags.Trim();
                s.Notes = Notes;
            });
            if (updated is not null) Session = updated;
            Saved?.Invoke(Session);
        }
        catch (Exception e) { host.Notifier.Error(e, "Изменения не сохранились"); }
    }

    /// <summary>Raised after the details were saved, so the list row can follow.</summary>
    public event Action<CallSession>? Saved;

    private void ApplyFilter()
    {
        var q = TranscriptFilter.Trim();
        TranscriptRows = q.Length == 0 ? allRows
            : allRows.Where(r => r.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Source.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    /// <summary>Marks the phrase being played: the last one of that speaker (anyone's, in a mix) that has started,
    /// until a long pause follows it.</summary>
    public void Follow(string? speaker, double seconds)
    {
        var row = allRows.Where(r => (speaker is null || r.Source == speaker) && r.Segment.Start <= seconds && seconds < r.Segment.End + 2)
            .MaxBy(r => r.Segment.Start);
        if (row == currentRow) return;
        if (currentRow is not null) currentRow.IsCurrent = false;
        currentRow = row;
        if (row is null) return;
        row.IsCurrent = true;
        CurrentRowChanged?.Invoke(row);
    }

    /// <summary>A phrase plays from the mix when there is one — the conversation as it sounded — otherwise from the
    /// track of its speaker (when a source was switched off and on, the track that covers that moment).</summary>
    [RelayCommand]
    private void PlayRow(TranscriptRowViewModel? row)
    {
        if (row is null) return;
        var source = Mix ?? Speaker(row.Source, row.Segment.Start);
        if (source is not null) PlayRequested?.Invoke(source, row.Segment.Start);
    }

    [RelayCommand]
    private void PlayBookmark(BookmarkRow? row)
    {
        if (row is null) return;
        var source = Mix ?? FirstSound();
        if (source is not null) PlayRequested?.Invoke(source, row.Bookmark.Seconds);
    }

    /// <summary>«Слушать»: a track, or — from the toolbar — the whole recording: its mix, or its first sound track.</summary>
    [RelayCommand]
    private void Listen(TrackItemViewModel? item)
    {
        var source = item is not null ? PlaybackSource.Of(host.Archive, Session, item.Track) : Mix ?? FirstSound();
        if (source is not null) PlayRequested?.Invoke(source, source.OffsetSeconds);
    }

    [RelayCommand]
    private void Watch(TrackItemViewModel? item)
    {
        if (item is not null) WatchRequested?.Invoke(PlaybackSource.Of(host.Archive, Session, item.Track), item.Track.OffsetSeconds);
    }

    [RelayCommand]
    private void ListenMix() { if (Mix is { } mix) PlayRequested?.Invoke(mix, mix.OffsetSeconds); }

    [RelayCommand]
    private void WatchMix() { if (Mix is { HasVideo: true } mix) WatchRequested?.Invoke(mix, mix.OffsetSeconds); }

    private PlaybackSource? FirstSound() => Session.Tracks.FirstOrDefault(t => t.HasAudio) is { } track ? PlaybackSource.Of(host.Archive, Session, track) : null;

    private PlaybackSource? Speaker(string name, double seconds)
    {
        var tracks = Session.Tracks.Where(t => t.Name == name && t.HasAudio).OrderBy(t => t.OffsetSeconds).ToArray();
        var track = tracks.LastOrDefault(t => t.OffsetSeconds <= seconds + 0.5) ?? tracks.FirstOrDefault();
        return track is null ? FirstSound() : PlaybackSource.Of(host.Archive, Session, track);
    }

    [RelayCommand]
    private void Remix()
    {
        if (Session.IsRecording) { host.Notifier.Info("Запись ещё идёт", "Свести дорожки можно после её завершения."); return; }
        MixRequested?.Invoke();
    }

    /// <summary>The person chose what to mix: the mix is made in the background and replaces the previous one.</summary>
    public void RequestMix(MixRequest request)
    {
        host.Processing.RequestMix(Session, request);
        host.Notifier.Info("Сведение в очереди", "Готовый файл появится на вкладке «Дорожки». Исходные дорожки не меняются.");
    }

    /// <summary>A copy of the mix file, under a readable name, wherever the person wants it.</summary>
    [RelayCommand]
    private async Task ExportMixFileAsync()
    {
        if (Mix is not { } mix) return;
        var extension = Path.GetExtension(mix.Files[0]).ToLowerInvariant();
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить сведение", FileName = SafeName(Session.Title) + extension, DefaultExt = extension,
            Filter = mix.HasVideo ? "Видео MP4|*.mp4" : "Звук M4A (AAC)|*.m4a"
        };
        if (dialog.ShowDialog() != true) return;
        await RunAsync("Сохраняю сведение…", async ct =>
        {
            await using var input = File.OpenRead(mix.Files[0]);
            await using var output = File.Create(dialog.FileName);
            await input.CopyToAsync(output, ct);
        }, dialog.FileName);
    }

    [RelayCommand]
    private void Transcribe()
    {
        if (Session.IsRecording) { host.Notifier.Info("Запись ещё идёт", "Расшифровка начнётся после её завершения."); return; }
        if (!Session.Tracks.Any(t => t.HasAudio)) { host.Notifier.Info("Нет звука", "В этой записи только видео."); return; }
        host.Processing.Enqueue(Session, transcribe: true);
        var installed = ModelManager.IsInstalled(ModelManager.Find(host.Settings.Model));
        host.Notifier.Info("Расшифровка в очереди", installed ? "Текст появится здесь, когда она закончится." : "Сначала скачайте модель распознавания в настройках.");
    }

    [RelayCommand]
    private void CancelProcessing() => host.Processing.CancelCurrent();

    [RelayCommand]
    private void CopyTranscript()
    {
        if (Session.Transcript.Count == 0) return;
        System.Windows.Clipboard.SetText(ExportService.Transcript(host.Archive.Get(Session.Id) ?? Session, ".txt"));
        host.Notifier.Success("Скопировано", "Текст расшифровки в буфере обмена.");
    }

    [RelayCommand]
    private async Task ExportTextAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить расшифровку", FileName = SafeName(Session.Title), DefaultExt = ".txt",
            Filter = "Текст (TXT)|*.txt|Markdown (MD)|*.md|Субтитры SRT|*.srt|Субтитры WebVTT|*.vtt|Данные JSON|*.json"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, ExportService.Transcript(host.Archive.Get(Session.Id) ?? Session, Path.GetExtension(dialog.FileName)));
            host.Notifier.Success("Сохранено", dialog.FileName);
        }
        catch (Exception e) { host.Notifier.Error(e, "Не удалось сохранить"); }
    }

    [RelayCommand]
    private async Task ExportTrackAsync(TrackItemViewModel? item)
    {
        if (item is null) return;
        var track = item.Track;
        var filter = track.HasVideo
            ? "Видео MKV — без перекодирования|*.mkv|Видео MP4|*.mp4" + (track.HasAudio ? "|Только звук · FLAC|*.flac|Только звук · MP3|*.mp3" : "")
            : "FLAC — без потерь, компактно|*.flac|WAV — без потерь|*.wav|MP3|*.mp3|M4A (AAC)|*.m4a";
        var dialog = new SaveFileDialog { Title = "Сохранить дорожку", FileName = SafeName($"{Session.Title} — {track.Name}"), Filter = filter };
        if (dialog.ShowDialog() != true) return;
        var session = Session;
        await RunAsync("Сохраняю дорожку…", ct => ExportService.ExportTrackAsync(host.Archive, session, track, dialog.FileName, ct), dialog.FileName);
    }

    [RelayCommand]
    private async Task ExportMixAsync()
    {
        if (!Session.Tracks.Any(t => t.HasAudio)) return;
        var dialog = new SaveFileDialog
        {
            Title = "Общая дорожка: все голоса вместе", FileName = SafeName(Session.Title + " — все голоса"),
            Filter = "MP3 — для отправки|*.mp3|M4A (AAC)|*.m4a|FLAC — без потерь|*.flac|WAV — без потерь|*.wav"
        };
        if (dialog.ShowDialog() != true) return;
        var session = Session;
        await RunAsync("Свожу дорожки в одну…", ct => ExportService.ExportMixAsync(host.Archive, session, dialog.FileName, ct), dialog.FileName);
    }

    [RelayCommand]
    private void OpenFolder() => Process.Start(new ProcessStartInfo(host.Archive.Folder(Session)) { UseShellExecute = true });

    /// <summary>Raised to let the page ask for confirmation; the page calls <see cref="DeleteConfirmed"/>.</summary>
    public event Action? DeleteRequested;
    [RelayCommand]
    private void Delete() => DeleteRequested?.Invoke();

    public event Action<string>? Deleted;
    public void DeleteConfirmed()
    {
        try
        {
            host.Archive.Delete(Session);
            host.Notifier.Info("Запись в Корзине", $"«{Session.Title}» можно вернуть из Корзины Windows.");
            Deleted?.Invoke(Session.Id);
        }
        catch (Exception e) { host.Notifier.Error(e, "Не удалось удалить"); }
    }

    [ObservableProperty] public partial bool IsExporting { get; set; }
    [ObservableProperty] public partial string ExportText { get; set; } = "";
    private CancellationTokenSource? export;

    [RelayCommand]
    private void CancelExport() => export?.Cancel();

    private async Task RunAsync(string title, Func<CancellationToken, Task> work, string destination)
    {
        if (IsExporting) return;
        export = new CancellationTokenSource();
        IsExporting = true;
        ExportText = title;
        try
        {
            await work(export.Token);
            host.Notifier.Success("Готово", destination);
        }
        catch (OperationCanceledException) { host.Notifier.Info("Отменено", "Исходные дорожки не изменены."); }
        catch (Exception e) { host.Notifier.Error(e, "Не получилось"); }
        finally
        {
            IsExporting = false;
            export.Dispose();
            export = null;
        }
    }

    private static string SafeName(string value)
    {
        var clean = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return clean.Length > 140 ? clean[..140] : clean;
    }
}
