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

/// <summary>A phrase of the transcript in the list: shown, played, corrected in place.</summary>
public sealed partial class TranscriptRowViewModel(TranscriptSegment segment) : ObservableObject
{
    /// <summary>The phrase as stored; replaced as a whole when it is corrected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Time), nameof(Source), nameof(Text), nameof(IsEdited), nameof(Recognized))]
    public partial TranscriptSegment Segment { get; set; } = segment;
    public string Time => Display.Duration(Segment.Start);
    public string Source => Segment.Source;
    public string Text => Segment.Text;
    /// <summary>The person corrected the text; <see cref="Recognized"/> says what was heard.</summary>
    public bool IsEdited => Segment.Original is not null;
    public string? Recognized => Segment.Original is { } original ? $"Распознано: «{original}»" : null;
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial bool IsEditing { get; set; }
    /// <summary>The text being typed while the phrase is corrected.</summary>
    [ObservableProperty] public partial string Draft { get; set; } = "";
    /// <summary>«пауза 5 мин» when the recording was paused right before this phrase.</summary>
    [ObservableProperty] public partial string? PauseBefore { get; set; }
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
    /// <summary>Phrases were removed or brought back: the page shows this one.</summary>
    public event Action<TranscriptRowViewModel>? RowShown;
    /// <summary>A new transcription would replace the person's corrections: the page asks first and calls
    /// <see cref="TranscribeConfirmed"/>.</summary>
    public event Action? RetranscribeRequested;

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Project { get; set; } = "";
    [ObservableProperty] public partial string Tags { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";
    [ObservableProperty] public partial string Meta { get; set; } = "";
    [ObservableProperty] public partial bool IsReadOnly { get; set; }
    [ObservableProperty] public partial string TranscriptFilter { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<TranscriptRowViewModel> TranscriptRows { get; set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTranscriptTools), nameof(ShowEditHint))]
    public partial bool HasTranscript { get; set; }
    /// <summary>Corrections are possible: not while recording, not while a new transcription is on its way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditHint))]
    public partial bool CanEditTranscript { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTranscriptTools))]
    public partial bool CanUndo { get; set; }
    [ObservableProperty] public partial string UndoHint { get; set; } = "";
    /// <summary>«"Стройк" есть ещё в 12 фразах…» — after a correction that can be made everywhere.</summary>
    [ObservableProperty] public partial string? Suggestion { get; set; }
    /// <summary>Search and «Отменить» stay when every phrase was removed: the removal can still be undone.</summary>
    public bool ShowTranscriptTools => HasTranscript || CanUndo;
    public bool ShowEditHint => HasTranscript && CanEditTranscript;
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
    /// <summary>The phrase being corrected now.</summary>
    private TranscriptRowViewModel? editing;
    /// <summary>The transcript before each correction, newest first: «Отменить» goes back one step at a time.</summary>
    private readonly Stack<TranscriptChange> undo = new();
    private (string From, string To)? suggested;
    private const int UndoDepth = 50;

    /// <summary>A correction as «Отменить» undoes it: what it was and the transcript and track names before it.</summary>
    private sealed record TranscriptChange(string What, List<TranscriptSegment> Transcript, Dictionary<string, string> TrackNames, bool Edited);

    /// <summary>A phrase is being corrected: a refresh of the page must not take it away.</summary>
    public bool IsEditingText => editing is not null;

    /// <summary>The speakers of this recording: those of the transcript in the order they speak, then the other sound tracks.</summary>
    public IReadOnlyList<string> Speakers =>
        Session.Transcript.Select(s => s.Source).Concat(Session.Tracks.Where(t => t.HasAudio).Select(t => t.Name)).Distinct().ToArray();

    public void Load(CallSession session)
    {
        loading = true;
        // The same transcript again (background work changed something else): the list, its scroll position and the
        // corrections that can be undone stay as they are.
        var sameTranscript = Session is not null && Session.Id == session.Id && Session.Transcript.SequenceEqual(session.Transcript);
        Session = session;
        Title = session.Title;
        Project = session.Project;
        Tags = session.Tags;
        Notes = session.Notes;
        IsReadOnly = session.IsRecording;
        CanEditTranscript = !session.IsRecording && session.TranscriptionStatus is not (TranscriptionStatus.Queued or TranscriptionStatus.Running);
        if (!sameTranscript)
        {
            editing = null;
            allRows = session.Transcript.Select(s => new TranscriptRowViewModel(s)).ToArray();
            currentRow = null;
            undo.Clear();
            DismissSuggestion();
            ApplyFilter();
        }
        MarkPauses();
        UpdateUndo();
        HasTranscript = allRows.Count > 0;
        Bookmarks = session.Bookmarks.OrderBy(b => b.Seconds).Select(b => new BookmarkRow(b)).ToArray();
        var transcribed = session.Transcript.Count > 0 || session.TranscriptionStatus == TranscriptionStatus.Done;
        TranscribeButtonText = transcribed ? "Расшифровать заново" : "Расшифровать";
        UpdateHint();
        UpdateBanner();
        UpdateMix(null);
        loading = false;
        _ = LoadSizesAsync(session);
    }

    private void UpdateHint()
    {
        var session = Session;
        TranscriptHint = session.TranscriptionStatus switch
        {
            TranscriptionStatus.Done when session.TranscriptEdited => "Все фразы удалены. Вернуть их — кнопкой «Отменить» или «Расшифровать заново».",
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
    }

    /// <summary>«пауза 5 мин» over the first phrase after each pause of the recording.</summary>
    private void MarkPauses()
    {
        var lengths = new Dictionary<TranscriptRowViewModel, double>();
        foreach (var pause in Session.Pauses)
            if (allRows.FirstOrDefault(r => r.Segment.Start >= pause.At - 0.5) is { } row)
                lengths[row] = lengths.GetValueOrDefault(row) + pause.Seconds;
        foreach (var row in allRows) row.PauseBefore = lengths.TryGetValue(row, out var seconds) ? $"пауза {Display.Span(seconds)}" : null;
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
        parts.Add($"{session.Tracks.Count} {Plural(session.Tracks.Count, "дорожка", "дорожки", "дорожек")}");
        if (session.Pauses.Count > 0)
            parts.Add($"{session.Pauses.Count} {Plural(session.Pauses.Count, "пауза", "паузы", "пауз")} · {Display.Span(session.Pauses.Sum(p => p.Seconds))} вырезано");
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

    /// <summary>Marks the phrase being played: the last one heard on that track (anyone's, in a mix) that has started,
    /// until a long pause follows it.</summary>
    public void Follow(PlaybackSource? source, double seconds)
    {
        var row = allRows.Where(r => Heard(r, source) && r.Segment.Start <= seconds && seconds < r.Segment.End + 2)
            .MaxBy(r => r.Segment.Start);
        if (row == currentRow) return;
        if (currentRow is not null) currentRow.IsCurrent = false;
        currentRow = row;
        if (row is null) return;
        row.IsCurrent = true;
        CurrentRowChanged?.Invoke(row);
    }

    /// <summary>A phrase belongs to what is playing: anyone's in a mix, the track's own otherwise — a phrase given to
    /// another speaker is still heard on its track.</summary>
    private static bool Heard(TranscriptRowViewModel row, PlaybackSource? source) =>
        source?.Speaker is null || (row.Segment.Track is { } track ? track == source.Id : row.Source == source.Speaker);

    /// <summary>A phrase plays from the mix when there is one — the conversation as it sounded — otherwise from the
    /// track it was heard on.</summary>
    [RelayCommand]
    private void PlayRow(TranscriptRowViewModel? row)
    {
        if (row is null) return;
        var source = Mix ?? Heard(row.Segment);
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

    private PlaybackSource? Heard(TranscriptSegment segment) =>
        TranscriptEditing.TrackOf(Session, segment) is { HasAudio: true } track ? PlaybackSource.Of(host.Archive, Session, track) : FirstSound();

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
        if (Session.TranscriptEdited) { RetranscribeRequested?.Invoke(); return; }
        TranscribeConfirmed();
    }

    /// <summary>Transcribes the recording again; the person agreed to lose their corrections, if there were any.</summary>
    public void TranscribeConfirmed()
    {
        if (editing is not null) CancelEdit(editing);
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
        if (Copy(ExportService.Transcript(host.Archive.Get(Session.Id) ?? Session, ".txt")))
            host.Notifier.Success("Скопировано", "Текст расшифровки в буфере обмена.");
    }

    /// <summary>The chosen phrases as lines of the text export: «[00:01:23] Я: …».</summary>
    public void CopyRows(IReadOnlyList<TranscriptRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        if (Copy(string.Join(Environment.NewLine, rows.OrderBy(r => r.Segment.Start).Select(r => $"[{r.Time}] {r.Source}: {r.Text}"))))
            host.Notifier.Success("Скопировано", rows.Count == 1 ? "Фраза в буфере обмена." : $"{rows.Count} {Plural(rows.Count, "фраза", "фразы", "фраз")} в буфере обмена.");
    }

    /// <summary>Another program may hold the clipboard for a moment.</summary>
    private bool Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.ExternalException e) { host.Notifier.Error(e, "Буфер обмена занят другой программой"); return false; }
    }

    // ----- Corrections of the transcript -----

    /// <summary>Opens a phrase for correction in place (double click, F2, the pencil). Another phrase being corrected is saved first.</summary>
    public bool BeginEdit(TranscriptRowViewModel row)
    {
        if (!EnsureEditable()) return false;
        if (editing is not null && editing != row) CommitEdit(editing);
        row.Draft = row.Text;
        row.IsEditing = true;
        editing = row;
        return true;
    }

    /// <summary>Saves the corrected phrase; offers to correct the same words in the other phrases.</summary>
    public void CommitEdit(TranscriptRowViewModel row)
    {
        if (!row.IsEditing) return;
        row.IsEditing = false;
        if (editing == row) editing = null;
        var text = TranscriptEditing.CleanText(row.Draft);
        if (text == row.Text) return;
        if (text.Length == 0)
        {
            host.Notifier.Info("Пустая фраза не сохранена", "Чтобы убрать фразу, нажмите Delete или выберите «Удалить фразу» в меню правой кнопки.");
            return;
        }
        var before = row.Text;
        var segment = row.Segment;
        if (Change($"правка фразы {row.Time}", s => Replace(s, segment, TranscriptEditing.WithText(segment, text)))) Suggest(before, text);
    }

    public void CancelEdit(TranscriptRowViewModel row)
    {
        row.IsEditing = false;
        if (editing == row) editing = null;
    }

    /// <summary>The phrase reads as it was recognized again.</summary>
    public void RestoreRecognized(TranscriptRowViewModel row)
    {
        if (!row.IsEdited || !EnsureEditable()) return;
        var segment = row.Segment;
        Change($"возврат распознанного текста {row.Time}", s => Replace(s, segment, TranscriptEditing.Recognized(segment)));
    }

    /// <summary>Renames a speaker in every phrase and track of this recording; a name already there joins the two.</summary>
    public void RenameSpeaker(string from, string to)
    {
        to = TranscriptEditing.CleanName(to);
        if (to.Length == 0 || to == from || !EnsureEditable()) return;
        if (Change($"переименование «{from}» → «{to}»", s => TranscriptEditing.RenameSpeaker(s, from, to)))
            _ = LoadSizesAsync(Session); // the track cards carry the name too
    }

    /// <summary>Gives phrases to another speaker — one of this recording or a new name.</summary>
    public void AssignSpeaker(IReadOnlyList<TranscriptRowViewModel> rows, string speaker)
    {
        speaker = TranscriptEditing.CleanName(speaker);
        var segments = rows.Select(r => r.Segment).Where(s => s.Source != speaker).ToArray();
        if (speaker.Length == 0 || segments.Length == 0 || !EnsureEditable()) return;
        var what = segments.Length == 1 ? $"смена говорящего у фразы {Display.Duration(segments[0].Start)}"
            : $"смена говорящего у {segments.Length} {Plural(segments.Length, "фразы", "фраз", "фраз")}";
        Change(what, s => { foreach (var segment in segments) Replace(s, segment, TranscriptEditing.Assign(s, segment, speaker)); });
    }

    /// <summary>Removes phrases — noise taken for words, chatter before the meeting. «Отменить» brings them back.</summary>
    public void DeleteRows(IReadOnlyList<TranscriptRowViewModel> rows)
    {
        if (rows.Count == 0 || !EnsureEditable()) return;
        var segments = rows.Select(r => r.Segment).ToArray();
        var after = allRows.FirstOrDefault(r => r.Segment.Start > segments.Max(s => s.Start) && !segments.Contains(r.Segment))?.Segment;
        var what = segments.Length == 1 ? $"удаление фразы {Display.Duration(segments[0].Start)}"
            : $"удаление {segments.Length} {Plural(segments.Length, "фразы", "фраз", "фраз")}";
        if (!Change(what, s => { foreach (var segment in segments) s.Transcript.RemoveAt(Index(s, segment)); })) return;
        host.Notifier.Info(segments.Length == 1 ? "Фраза удалена" : "Фразы удалены", "Вернуть — «Отменить» над расшифровкой или Ctrl+Z.");
        if ((allRows.FirstOrDefault(r => r.Segment == after) ?? allRows.LastOrDefault()) is { } next) RowShown?.Invoke(next);
    }

    /// <summary>The correction just made, made in every phrase where the same words are.</summary>
    [RelayCommand]
    private void ReplaceEverywhere()
    {
        if (suggested is not { } change) return;
        DismissSuggestion();
        if (!EnsureEditable()) return;
        var replaced = 0;
        var done = Change($"замена «{change.From}» → «{change.To}»", s =>
        {
            for (var i = 0; i < s.Transcript.Count; i++)
            {
                var text = TranscriptEditing.ReplaceWords(s.Transcript[i].Text, change.From, change.To);
                if (text == s.Transcript[i].Text) continue;
                s.Transcript[i] = TranscriptEditing.WithText(s.Transcript[i], text);
                replaced++;
            }
        });
        if (done) host.Notifier.Success("Заменено", $"«{change.From}» → «{change.To}» в {replaced} {(replaced % 10 == 1 && replaced % 100 != 11 ? "фразе" : "фразах")}. Вернуть — «Отменить».");
    }

    [RelayCommand]
    private void DismissSuggestion()
    {
        suggested = null;
        Suggestion = null;
    }

    [RelayCommand]
    private void Undo()
    {
        if (editing is not null) CancelEdit(editing);
        if (!undo.TryPop(out var change)) return;
        var renamed = Session.Tracks.Any(t => change.TrackNames.TryGetValue(t.Id, out var name) && name != t.Name);
        try
        {
            Session = host.Archive.Update(Session.Id, s =>
            {
                if (s.TranscriptionStatus is TranscriptionStatus.Queued or TranscriptionStatus.Running) throw new InvalidOperationException(Busy);
                s.Transcript = [.. change.Transcript];
                foreach (var track in s.Tracks)
                    if (change.TrackNames.TryGetValue(track.Id, out var name)) track.Name = name;
                s.TranscriptEdited = change.Edited;
            }) ?? throw new InvalidOperationException("Запись не найдена: возможно, её удалили.");
            DismissSuggestion();
            AfterChange();
            if (renamed) _ = LoadSizesAsync(Session);
            host.Notifier.Info("Отменено", Capitalize(change.What) + ".");
        }
        catch (Exception e)
        {
            undo.Push(change);
            UpdateUndo();
            host.Notifier.Error(e, "Не удалось отменить");
        }
    }

    private const string Busy = "Идёт новая расшифровка этой записи: она заменит текст. Править можно после неё.";

    private bool EnsureEditable()
    {
        if (CanEditTranscript) return true;
        host.Notifier.Info("Править пока нельзя", Session.IsRecording ? "Запись ещё идёт." : Busy);
        return false;
    }

    /// <summary>
    /// Applies a correction to the stored recording — on top of whatever background work wrote meanwhile — and keeps the
    /// transcript before it for «Отменить». The stored transcript must be the one shown: a correction never lands on a
    /// text the person has not seen.
    /// </summary>
    private bool Change(string what, Action<CallSession> edit)
    {
        var before = new TranscriptChange(what, [.. Session.Transcript], Session.Tracks.ToDictionary(t => t.Id, t => t.Name), Session.TranscriptEdited);
        try
        {
            Session = host.Archive.Update(Session.Id, s =>
            {
                if (s.TranscriptionStatus is TranscriptionStatus.Queued or TranscriptionStatus.Running) throw new InvalidOperationException(Busy);
                if (!s.Transcript.SequenceEqual(Session.Transcript))
                    throw new InvalidOperationException("Расшифровка только что изменилась. Откройте запись заново и повторите правку.");
                edit(s);
                s.TranscriptEdited = true;
            }) ?? throw new InvalidOperationException("Запись не найдена: возможно, её удалили.");
            undo.Push(before);
            if (undo.Count > UndoDepth) { var kept = undo.Take(UndoDepth).Reverse().ToArray(); undo.Clear(); foreach (var c in kept) undo.Push(c); }
            AfterChange();
            return true;
        }
        catch (Exception e)
        {
            host.Notifier.Error(e, "Правка не сохранилась");
            return false;
        }
    }

    private static int Index(CallSession session, TranscriptSegment segment)
    {
        var index = session.Transcript.IndexOf(segment);
        return index >= 0 ? index : throw new InvalidOperationException("Фраза не найдена: расшифровка только что изменилась.");
    }

    private static void Replace(CallSession session, TranscriptSegment segment, TranscriptSegment replacement) =>
        session.Transcript[Index(session, segment)] = replacement;

    /// <summary>The list follows the stored transcript. Corrections change rows in place, so the list keeps its scroll
    /// position; phrases removed or brought back rebuild it.</summary>
    private void AfterChange()
    {
        var transcript = Session.Transcript;
        if (transcript.Count == allRows.Count)
        {
            for (var i = 0; i < transcript.Count; i++)
                if (allRows[i].Segment != transcript[i]) allRows[i].Segment = transcript[i];
        }
        else
        {
            allRows = transcript.Select(s => new TranscriptRowViewModel(s)).ToArray();
            currentRow = null;
            ApplyFilter();
        }
        HasTranscript = allRows.Count > 0;
        MarkPauses();
        UpdateHint();
        UpdateUndo();
        OnPropertyChanged(nameof(Speakers));
        Saved?.Invoke(Session);
    }

    private void UpdateUndo()
    {
        CanUndo = undo.Count > 0;
        UndoHint = undo.TryPeek(out var last) ? $"Отменить: {last.What} · Ctrl+Z" : "";
    }

    /// <summary>After a correction of a word or a few: are the same words in other phrases?</summary>
    private void Suggest(string before, string after)
    {
        DismissSuggestion();
        if (TranscriptEditing.ChangedWords(before, after) is not { } change) return;
        var count = TranscriptEditing.CountReplacements(Session.Transcript, change.From, change.To);
        if (count == 0) return;
        suggested = change;
        Suggestion = $"«{change.From}» есть ещё в {count} {(count % 10 == 1 && count % 100 != 11 ? "фразе" : "фразах")}. Заменить везде на «{change.To}»?";
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    private static string Plural(int n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };

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
