using CallDock.App.Services;
using CallDock.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace CallDock.App.ViewModels;

/// <summary>The recording console: which sources to record, live levels, start, stop and bookmarks.</summary>
public sealed partial class RecorderViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly Dictionary<SourceItemViewModel, AudioPreview> previews = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private int ticks;

    public RecorderViewModel(AppHost host)
    {
        this.host = host;
        Project = host.Settings.LastProject;
        host.Recorder.Changed += () => Dispatch(OnRecorderChanged);
        host.Recorder.LastTabFinished += () => Dispatch(() => _ = StopAsync());
        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();
    }

    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];
    public bool ApplicationAudioSupported => Capabilities.ApplicationAudio;
    public string ApplicationAudioHint => Capabilities.ApplicationAudioHint;

    [ObservableProperty] public partial IReadOnlyList<SourceSpec> Microphones { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SourceSpec> Outputs { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SourceSpec> Applications { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SourceSpec> Screens { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SourceSpec> Windows { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<string> ProjectSuggestions { get; set; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Project { get; set; } = "";
    [ObservableProperty] public partial string Tags { get; set; } = "";
    [ObservableProperty] public partial string BookmarkText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSources), nameof(RecordButtonText), nameof(TitlePlaceholder))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSources))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial bool IsPreviewing { get; set; }
    [ObservableProperty] public partial string Elapsed { get; set; } = "00:00:00";
    [ObservableProperty] public partial string Status { get; set; } = "Выберите источники и начните запись";
    [ObservableProperty] public partial bool HasSourceError { get; set; }

    public bool CanEditSources => !IsRecording && !IsBusy;
    public string RecordButtonText => IsRecording ? "Завершить" : "Начать запись";
    public string TitlePlaceholder => $"Звонок {DateTime.Now:dd.MM HH:mm}";

    /// <summary>First start: the saved sources, or the default microphone («Я») and speakers («Собеседники»).</summary>
    public async Task LoadAsync()
    {
        await RefreshSourcesAsync();
        var saved = host.Settings.SavedSources;
        var initial = saved.Count > 0 ? saved : await Task.Run(AudioDevices.Defaults);
        foreach (var spec in initial) Sources.Add(new SourceItemViewModel(spec));
        ProjectSuggestions = host.Archive.Projects();
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        try
        {
            var (audio, video) = await Task.Run(() => (AudioDevices.List(), VideoCapture.List()));
            Microphones = audio.Where(s => s.Kind == SourceKind.Microphone).ToArray();
            Outputs = audio.Where(s => s.Kind == SourceKind.SystemAudio).ToArray();
            Applications = audio.Where(s => s.Kind == SourceKind.Application).ToArray();
            Screens = video.Where(s => s.Kind == SourceKind.Screen).ToArray();
            Windows = video.Where(s => s.Kind == SourceKind.Window).ToArray();
        }
        catch (Exception e) { host.Notifier.Error(e, "Не удалось получить список устройств"); }
    }

    [RelayCommand]
    public async Task AddSourceAsync(SourceSpec spec)
    {
        if (Sources.Any(s => s.Spec.Key == spec.Key))
        {
            host.Notifier.Info("Уже добавлен", spec.DisplayName);
            return;
        }
        await StopPreviewsAsync();
        var label = spec.Label ?? spec.Kind switch
        {
            SourceKind.Microphone when !Sources.Any(s => s.Spec.Kind == SourceKind.Microphone) => "Я",
            SourceKind.SystemAudio when !Sources.Any(s => s.Spec.Kind == SourceKind.SystemAudio) => "Собеседники",
            _ => null
        };
        Sources.Add(new SourceItemViewModel(spec with { Label = label }));
        SaveSources();
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(SourceItemViewModel item)
    {
        await StopPreviewsAsync();
        Sources.Remove(item);
        SaveSources();
    }

    /// <summary>Stream links and windows are not remembered: links may carry access tokens, windows close.</summary>
    public void SaveSources()
    {
        host.Settings.SavedSources = Sources
            .Where(s => s.Spec.Kind is SourceKind.Microphone or SourceKind.SystemAudio or SourceKind.Screen or SourceKind.Application)
            .Where(s => s.Spec.Kind != SourceKind.Application || s.Enabled)
            .Select(s => s.Spec).ToList();
        host.Settings.LastProject = Project;
        host.SaveSettings();
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsBusy) return;
        if (host.Recorder.IsRecording) await StopAsync();
        else await StartAsync();
    }

    public async Task StartAsync()
    {
        if (IsBusy || host.Recorder.IsRecording) return;
        var chosen = Sources.Where(s => s.Enabled && s.Spec.Kind != SourceKind.BrowserTab).Select(s => s.Spec).ToArray();
        if (chosen.Length == 0)
        {
            host.Notifier.Warning("Нечего записывать", "Включите хотя бы один источник или добавьте новый.");
            return;
        }
        IsBusy = true;
        try
        {
            await StopPreviewsAsync();
            SaveSources();
            await host.Recorder.StartAsync(chosen, Title, Project, Tags);
            var failed = host.Recorder.Current?.Tracks.Where(t => t.Error is not null).ToArray() ?? [];
            if (failed.Length > 0)
                host.Notifier.Warning("Запись идёт, но не все источники", string.Join("; ", failed.Select(t => $"{t.Name}: {t.Error}")));
        }
        catch (Exception e) { host.Notifier.Error(e, "Запись не началась"); }
        finally { IsBusy = false; }
    }

    public async Task StopAsync()
    {
        if (IsBusy || !host.Recorder.IsRecording) return;
        IsBusy = true;
        try
        {
            await host.Recorder.UpdateDetailsAsync(Title, Project, Tags);
            var session = await host.Recorder.StopAsync();
            if (session is null) return;
            var transcribe = host.Settings.AutoTranscribe && session.Tracks.Any(t => t.HasAudio);
            host.Processing.Enqueue(session, transcribe);
            var model = ModelManager.Find(host.Settings.Model);
            host.Notifier.Success("Запись сохранена", transcribe && !ModelManager.IsInstalled(model)
                ? $"«{session.Title}» · {session.DurationLabel}. Для расшифровки скачайте модель в настройках."
                : $"«{session.Title}» · {session.DurationLabel}");
            Title = "";
            Tags = "";
            ProjectSuggestions = host.Archive.Projects();
        }
        catch (Exception e) { host.Notifier.Error(e, "Не удалось завершить запись"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task AddBookmarkAsync()
    {
        if (!host.Recorder.IsRecording) return;
        try
        {
            var bookmark = await host.Recorder.BookmarkAsync(BookmarkText);
            if (bookmark is null) return;
            host.Notifier.Info("Метка поставлена", $"{Display.Duration(bookmark.Seconds)} · {bookmark.Text}");
            BookmarkText = "";
        }
        catch (Exception e) { host.Notifier.Error(e); }
    }

    [RelayCommand]
    private async Task TogglePreviewAsync()
    {
        if (IsPreviewing) { await StopPreviewsAsync(); return; }
        foreach (var item in Sources.Where(s => s.Enabled && s.Spec.Kind is SourceKind.Microphone or SourceKind.SystemAudio or SourceKind.Application))
        {
            try { previews[item] = await AudioPreview.StartAsync(item.Spec); }
            catch (Exception e) { item.Update(0, e.Message, false); }
        }
        IsPreviewing = previews.Count > 0;
    }

    private async Task StopPreviewsAsync()
    {
        foreach (var preview in previews.Values) await preview.DisposeAsync();
        previews.Clear();
        IsPreviewing = false;
    }

    private void OnRecorderChanged()
    {
        IsRecording = host.Recorder.IsRecording;
        if (IsRecording && host.Recorder.Current is { } session && string.IsNullOrWhiteSpace(Title))
            Title = session.Title;
        // Chrome tabs appear as cards while they record and leave when the session ends.
        var tabs = host.Recorder.Recordings.OfType<BrowserRecording>().ToArray();
        foreach (var tab in tabs)
            if (!Sources.Any(s => s.Spec.Kind == SourceKind.BrowserTab && s.Spec.Target == tab.Track.Id))
                Sources.Add(new SourceItemViewModel(new(SourceKind.BrowserTab, tab.Track.Name, tab.Track.Id)) { Editable = false });
        if (!IsRecording)
        {
            foreach (var item in Sources.Where(s => s.Spec.Kind == SourceKind.BrowserTab).ToArray()) Sources.Remove(item);
            Elapsed = "00:00:00";
            Status = "Готово к следующей встрече";
            HasSourceError = false;
        }
        foreach (var item in Sources) item.Editable = !IsRecording && item.Spec.Kind != SourceKind.BrowserTab;
    }

    private async Task TickAsync()
    {
        var recorder = host.Recorder;
        foreach (var item in Sources)
        {
            var live = recorder.Find(item.Spec);
            if (live is BrowserRecording { Closed: true } tab) item.Update(0, tab.Error, false, $"Вкладка закрыта · {Display.Size(tab.BytesWritten)}");
            else if (live is not null)
            {
                if (live.Track.HasAudio) item.Update(live.Peak, live.Error, true);
                else item.Update(0, live.Error, true, $"Видео · {Display.Size(live.BytesWritten)}");
            }
            else if (previews.TryGetValue(item, out var preview)) item.Update(preview.Peak, preview.Error, true);
            else if (recorder.IsRecording) item.Absent();
            else if (item.IsLive || item.HasError || item.IsAbsent) item.Idle();
        }
        if (!recorder.IsRecording)
        {
            if (++ticks % 300 == 0) OnPropertyChanged(nameof(TitlePlaceholder)); // «Звонок 03.10 14:30» keeps the current time
            return;
        }
        Elapsed = Display.Duration(recorder.Elapsed);
        if (++ticks % 30 != 0 || recorder.IsStopping) return;
        try { await recorder.CheckpointAsync(); }
        catch (Exception e)
        {
            host.Notifier.Error(e, "Запись остановлена");
            await StopAsync();
            return;
        }
        var captures = recorder.Recordings;
        HasSourceError = captures.Any(r => r.Error is not null);
        Status = HasSourceError
            ? "Ошибка одного из источников — остальные продолжают запись"
            : $"Идёт запись · {captures.Count} {Plural(captures.Count, "источник", "источника", "источников")} · {Display.Size(captures.Sum(r => r.BytesWritten))}";
    }

    public void Notify(string title, string message) => host.Notifier.Info(title, message);

    private static string Plural(int n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };

    private static void Dispatch(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}
