using CallDock.App.Services;
using CallDock.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;

namespace CallDock.App.ViewModels;

/// <summary>A speech model in the settings: installed or not, being downloaded, chosen.</summary>
public sealed partial class ModelItemViewModel(SpeechModel model, SettingsViewModel owner) : ObservableObject
{
    public SpeechModel Model { get; } = model;
    public string Title => Model.Title;
    public string Description => Model.Description;
    public string Size => Model.SizeLabel;
    public bool IsRecommended => Model.Id == ModelManager.Recommended.Id;
    [ObservableProperty] public partial bool IsInstalled { get; set; } = ModelManager.IsInstalled(model);
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string State { get; set; } = "";

    [RelayCommand] private Task DownloadAsync() => owner.DownloadAsync(this);
    [RelayCommand] private void Cancel() => owner.CancelDownload();
    [RelayCommand] private void Delete() => owner.DeleteModel(this);
    [RelayCommand] private void Select() => owner.SelectModel(this);
}

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost host;
    private CancellationTokenSource? download;
    private bool loading = true;

    public SettingsViewModel(AppHost host)
    {
        this.host = host;
        var s = host.Settings;
        Models = ModelManager.Models.Select(m => new ModelItemViewModel(m, this)).ToArray();
        foreach (var m in Models) { m.IsSelected = m.Model.Id == s.Model; m.State = StateOf(m); }
        Language = Languages.FirstOrDefault(l => l.Value == s.Language) ?? Languages[0];
        Threads = Math.Clamp(s.CpuThreads, 1, MaxThreads);
        AutoTranscribe = s.AutoTranscribe;
        CompressAudio = s.CompressAudio;
        AutoMix = s.AutoMix;
        GlobalHotkeys = s.GlobalHotkeys;
        CheckForUpdates = s.CheckForUpdates;
        Theme = Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Themes[0];
        Fps = FpsOptions.FirstOrDefault(f => f.Value == s.VideoFps) ?? FpsOptions[1];
        Calls = CallOptions.FirstOrDefault(c => c.Value == s.CallDetection) ?? CallOptions[0];
        SoftwareRendering = s.SoftwareRendering;
        ArchiveRoot = host.Archive.Root;
        host.Updates.Changed += () => Dispatch(() =>
        {
            UpdateStatus = host.Updates.Status;
            CanRestart = host.Updates.PendingVersion is not null;
            UpdateNotes = host.Updates.PendingNotes ?? "";
        });
        UpdateStatus = host.Updates.Status;
        loading = false;
    }

    public IReadOnlyList<ModelItemViewModel> Models { get; }
    public IReadOnlyList<Option<string>> Languages { get; } =
        [new("auto", "Автоматически"), new("ru", "Русский"), new("en", "Английский"), new("de", "Немецкий"), new("uk", "Украинский")];
    public IReadOnlyList<Option<string>> Themes { get; } = [new("system", "Как в Windows"), new("dark", "Тёмная"), new("light", "Светлая")];
    public IReadOnlyList<Option<string>> CallOptions { get; } =
    [
        new(Services.CallWatcher.Ask, "Предлагать записать"),
        new(Services.CallWatcher.Auto, "Записывать сразу"),
        new(Services.CallWatcher.Off, "Ничего не делать")
    ];
    public IReadOnlyList<Option<int>> FpsOptions { get; } = [new(15, "15 кадров/с — экономно"), new(24, "24 кадра/с"), new(30, "30 кадров/с — плавно")];
    public int MaxThreads { get; } = Math.Max(1, Math.Min(16, Environment.ProcessorCount));
    public IReadOnlyList<int> ThreadOptions => Enumerable.Range(1, MaxThreads).ToArray();

    public string Version => AppInfo.Version;
    public string HotkeyToggle => HotkeyService.ToggleGesture;
    public string HotkeyBookmark => HotkeyService.BookmarkGesture;
    public string BridgeStatus => host.BridgeError ?? (host.Bridge.IsRunning ? "CallDock принимает вкладки Chrome (порт 47831, только этот компьютер)." : "Подключение Chrome не запущено.");
    public bool ApplicationAudioSupported => Capabilities.ApplicationAudio;

    [ObservableProperty] public partial Option<string> Language { get; set; }
    [ObservableProperty] public partial int Threads { get; set; }
    [ObservableProperty] public partial bool AutoTranscribe { get; set; }
    [ObservableProperty] public partial bool CompressAudio { get; set; }
    [ObservableProperty] public partial bool AutoMix { get; set; }
    [ObservableProperty] public partial Option<string> Calls { get; set; } = null!;
    [ObservableProperty] public partial bool GlobalHotkeys { get; set; }
    [ObservableProperty] public partial string HotkeyConflicts { get; set; } = "";
    [ObservableProperty] public partial bool CheckForUpdates { get; set; }
    [ObservableProperty] public partial Option<string> Theme { get; set; }
    [ObservableProperty] public partial Option<int> Fps { get; set; }
    [ObservableProperty] public partial string ArchiveRoot { get; set; }
    [ObservableProperty] public partial string ArchiveSize { get; set; } = "";
    [ObservableProperty] public partial string? ArchiveRestartNote { get; set; }
    [ObservableProperty] public partial bool SoftwareRendering { get; set; }
    [ObservableProperty] public partial bool NeedsRestart { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenDisplay))]
    public partial bool TokenVisible { get; set; }
    /// <summary>The pairing code is a key to this computer's recorder: hidden until asked for.</summary>
    public string TokenDisplay => TokenVisible ? host.Bridge.Token : new string('•', 32);
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    [ObservableProperty] public partial string UpdateNotes { get; set; } = "";
    [ObservableProperty] public partial bool CanRestart { get; set; }

    /// <summary>Raised when the theme or the hotkeys must be re-applied by the window.</summary>
    public event Action? AppearanceChanged;
    public event Action? HotkeysChanged;
    /// <summary>Raised when the reaction to calls changed.</summary>
    public event Action? CallsChanged;

    partial void OnLanguageChanged(Option<string> value) => Save(s => s.Language = value.Value);
    partial void OnThreadsChanged(int value) => Save(s => s.CpuThreads = value);
    partial void OnAutoTranscribeChanged(bool value) => Save(s => s.AutoTranscribe = value);
    partial void OnCompressAudioChanged(bool value) { Save(s => s.CompressAudio = value); if (value) host.Processing.Resume(host.Archive.Search(limit: 500)); }
    partial void OnAutoMixChanged(bool value) => Save(s => s.AutoMix = value);
    partial void OnCallsChanged(Option<string> value) { Save(s => s.CallDetection = value.Value); if (!loading) CallsChanged?.Invoke(); }
    partial void OnGlobalHotkeysChanged(bool value) { Save(s => s.GlobalHotkeys = value); if (!loading) HotkeysChanged?.Invoke(); }
    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s.CheckForUpdates = value);
    partial void OnThemeChanged(Option<string> value) { Save(s => s.Theme = value.Value); if (!loading) AppearanceChanged?.Invoke(); }
    partial void OnFpsChanged(Option<int> value) => Save(s => s.VideoFps = value.Value);
    partial void OnSoftwareRenderingChanged(bool value) { Save(s => s.SoftwareRendering = value); if (!loading) NeedsRestart = true; }

    /// <summary>Raised to restart CallDock (the window does it, after checking nothing is recording).</summary>
    public event Action? RestartRequested;
    [RelayCommand] private void Restart() => RestartRequested?.Invoke();

    private void Save(Action<AppSettings> change)
    {
        if (loading) return;
        change(host.Settings);
        host.SaveSettings();
    }

    public void ShowHotkeyConflicts(IReadOnlyList<string> conflicts) =>
        HotkeyConflicts = conflicts.Count == 0 ? "" : $"Занято другой программой: {string.Join(", ", conflicts)}.";

    public async Task LoadArchiveSizeAsync()
    {
        var size = await Task.Run(() => MediaTools.FolderSize(host.Archive.Root));
        ArchiveSize = $"Занято записями: {Display.Size(size)}";
    }

    // ── speech models ──────────────────────────────────────────────────────
    private static string StateOf(ModelItemViewModel m)
    {
        if (m.IsInstalled) return m.IsSelected ? "Установлена · используется" : "Установлена";
        var partial = ModelManager.PartialBytes(m.Model);
        if (partial > 0) return $"Скачано {(double)partial / m.Model.Size:P0} — нажмите «Скачать», чтобы продолжить";
        return m.IsSelected ? "Выбрана, но не скачана" : "Не скачана";
    }

    public void SelectModel(ModelItemViewModel item)
    {
        foreach (var m in Models) { m.IsSelected = m == item; m.State = StateOf(m); }
        Save(s => s.Model = item.Model.Id);
        if (item.IsInstalled) host.Processing.Wake();
    }

    public async Task DownloadAsync(ModelItemViewModel item)
    {
        if (download is not null) return;
        download = new CancellationTokenSource();
        item.IsDownloading = true;
        try
        {
            item.State = "Скачиваю…";
            await ModelManager.DownloadAsync(item.Model, new Progress<double>(p =>
            {
                item.Progress = p * 100;
                item.State = $"Скачиваю · {p:P0} из {item.Size}";
            }), download.Token);
            item.IsInstalled = true;
            SelectModel(item);
            host.Notifier.Success("Модель установлена", $"«{item.Title}» работает без интернета. Записи в очереди расшифруются сами.");
        }
        catch (OperationCanceledException) { item.State = "Загрузка отменена"; }
        catch (Exception e)
        {
            item.State = "Не удалось скачать";
            host.Notifier.Error(e, "Модель не скачалась");
        }
        finally
        {
            item.IsDownloading = false;
            item.Progress = 0;
            download.Dispose();
            download = null;
            if (!item.IsInstalled) item.State = StateOf(item);
        }
    }

    public void CancelDownload() => download?.Cancel();

    public void DeleteModel(ModelItemViewModel item)
    {
        try
        {
            ModelManager.Delete(item.Model);
            item.IsInstalled = false;
            item.State = StateOf(item);
        }
        catch (Exception e) { host.Notifier.Error(e, "Не удалось удалить модель"); }
    }

    // ── storage ────────────────────────────────────────────────────────────
    [RelayCommand]
    private void ChooseArchive()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Папка для записей CallDock", UseDescriptionForTitle = true, SelectedPath = host.Settings.ArchiveRoot
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Save(s => s.ArchiveRoot = dialog.SelectedPath);
        ArchiveRestartNote = "Новая папка начнёт работать после перезапуска CallDock. Прежние записи останутся в старой папке — чтобы они были в архиве, скопируйте её содержимое целиком.";
        NeedsRestart = true;
    }

    [RelayCommand] private void OpenArchive() => Open(host.Archive.Root);
    [RelayCommand] private void OpenLogs() { Directory.CreateDirectory(Log.Folder); Open(Log.Folder); }

    // ── Chrome ─────────────────────────────────────────────────────────────
    [RelayCommand]
    private void CopyToken()
    {
        System.Windows.Clipboard.SetText(host.Bridge.Token);
        host.Notifier.Success("Код скопирован", "Вставьте его в расширение CallDock в Chrome.");
    }

    [RelayCommand] private void ToggleToken() => TokenVisible = !TokenVisible;
    [RelayCommand] private void OpenExtension() => Open(Path.Combine(AppContext.BaseDirectory, "extension"));
    [RelayCommand] private void OpenChromeExtensions() => CopyAndHint("chrome://extensions", "Адрес скопирован: вставьте его в адресную строку Chrome.");

    // ── updates & about ────────────────────────────────────────────────────
    [RelayCommand] private Task CheckUpdatesAsync() => host.Updates.CheckAsync();

    [RelayCommand]
    private void RestartToUpdate()
    {
        if (host.Recorder.IsRecording) { host.Notifier.Warning("Идёт запись", "Обновление установится после её завершения."); return; }
        host.Updates.RestartNow();
    }

    [RelayCommand] private void OpenRepository() => Open(AppInfo.RepositoryUrl);
    [RelayCommand] private void OpenNotices() => Open(Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md"));

    private void CopyAndHint(string text, string hint)
    {
        System.Windows.Clipboard.SetText(text);
        host.Notifier.Info("Скопировано", hint);
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { host.Notifier.Error(e, "Не удалось открыть"); }
    }

    private static void Dispatch(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}
