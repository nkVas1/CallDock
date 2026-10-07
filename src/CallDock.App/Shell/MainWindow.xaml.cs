using CallDock.App.Services;
using CallDock.App.ViewModels;
using CallDock.App.Views;
using CallDock.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace CallDock.App.Shell;

/// <summary>
/// The window: a rail with «Запись», «Архив», «Настройки», the pages, notifications and dialogs. Closing hides it
/// to the tray (a recording keeps going); «Выход» in the tray menu quits.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly AppHost host;
    private readonly RecorderViewModel recorder;
    private readonly ArchiveViewModel archive;
    private readonly SettingsViewModel settings;
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly DispatcherTimer pill = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ContentDialogService dialogs = new();
    private TrayService? tray;
    private HotkeyService? hotkeys;
    private CallWatcher? calls;
    private bool quitting;

    public MainWindow(AppHost host)
    {
        this.host = host;
        recorder = new RecorderViewModel(host);
        archive = new ArchiveViewModel(host);
        settings = new SettingsViewModel(host);
        InitializeComponent();
        // Mica exists only in Windows 11; on Windows 10 the window keeps the theme's solid background.
        WindowBackdropType = Backdrop;
        dialogs.SetDialogHost(DialogHost);
        ApplyTheme();
        settings.AppearanceChanged += ApplyTheme;
        settings.HotkeysChanged += ApplyHotkeys;
        settings.CallsChanged += () => calls?.Apply();
        settings.RestartRequested += () => _ = RestartAsync();
        host.Recorder.Changed += () => Dispatcher.BeginInvoke(UpdateRecordingIndicators);
        pill.Tick += (_, _) => UpdateRecordingIndicators();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public IContentDialogService Dialogs => dialogs;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        tray = new TrayService(ShowFromTray, () => _ = recorder.ToggleRecordingCommand.ExecuteAsync(null),
            () => _ = recorder.TogglePauseCommand.ExecuteAsync(null), () => _ = recorder.AddBookmarkCommand.ExecuteAsync(null),
            () => _ = QuitAsync(), () => _ = RestartAsync(toggleCompatibleDrawing: true), host.Settings.SoftwareRendering);
        host.Notifier.Attach(Snackbar, () => IsVisible && WindowState != WindowState.Minimized, tray);
        ApplyTheme();
        ApplyHotkeys();
        ListenForActivation();
        NavRecord.IsChecked = true;
        try { await recorder.LoadAsync(); }
        catch (Exception ex) { host.Notifier.Error(ex, "Не удалось загрузить источники"); }
        archive.Refresh();
        calls = new CallWatcher(host, recorder);
        foreach (var message in host.Recovered) host.Notifier.Warning("Восстановлено", message);
        if (host.StartupWarning is not null) host.Notifier.Warning("Архив", host.StartupWarning);
        if (host.BridgeError is not null) host.Notifier.Warning("Chrome", host.BridgeError);
        if (host.Settings.CheckForUpdates) _ = CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        await host.Updates.CheckAsync();
        if (host.Updates.PendingVersion is { } version)
            host.Notifier.Info($"Скачана версия {version}", "Она установится при следующем запуске CallDock. Что нового — в настройках, раздел «Обновления».");
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;
        archive.Detail?.SaveDetails();
        if (!pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "record" => new RecorderView(recorder, dialogs),
                "archive" => new ArchiveView(archive, dialogs, host),
                _ => new SettingsView(settings)
            };
            pages[key] = page;
        }
        Page.Content = page;
        if (key == "archive") archive.Refresh();
        if (key == "settings") { settings.ShowHotkeyConflicts(hotkeys?.Conflicts ?? []); _ = settings.LoadArchiveSizeAsync(); }
    }

    private void ShowRecorder(object sender, RoutedEventArgs e) => NavRecord.IsChecked = true;

    /// <summary>Opens the archive on a recording (after a stop, from a notification).</summary>
    public void ShowSession(string sessionId)
    {
        NavArchive.IsChecked = true;
        archive.Select(sessionId);
    }

    private void UpdateRecordingIndicators()
    {
        var recording = host.Recorder.IsRecording;
        var paused = recording && host.Recorder.IsPaused;
        var elapsed = Display.Duration(host.Recorder.Elapsed);
        RecordingPill.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        NavRecordDot.Visibility = RecordingPill.Visibility;
        // A pause is amber everywhere: the pill, the dot on the rail, the tray icon.
        var dot = (System.Windows.Media.Brush)FindResource(paused ? "WarningBrush" : "RecordBrush");
        RecordingPillDot.Fill = dot;
        NavRecordDot.Fill = dot;
        RecordingPillText.Text = host.Recorder.IsStopping ? "Сохраняю…" : paused ? $"Пауза · {elapsed}" : elapsed;
        RecordingPill.ToolTip = paused ? "Запись на паузе — открыть пульт" : "Идёт запись — открыть пульт";
        System.Windows.Automation.AutomationProperties.SetName(RecordingPill, paused ? "Запись на паузе" : "Идёт запись");
        tray?.SetRecording(recording, elapsed, paused);
        Title = paused ? $"❚❚ {elapsed} — CallDock" : recording ? $"● {elapsed} — CallDock" : "CallDock";
        if (recording && !pill.IsEnabled) pill.Start();
        if (!recording) pill.Stop();
    }

    private bool watching;
    private static WindowBackdropType Backdrop => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? WindowBackdropType.Mica : WindowBackdropType.None;

    /// <summary>Applies the chosen theme; «Как в Windows» also follows the system once the window is loaded
    /// (the watcher needs a loaded window).</summary>
    private void ApplyTheme()
    {
        var choice = host.Settings.Theme;
        if (watching) { SystemThemeWatcher.UnWatch(this); watching = false; }
        var theme = choice switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark
        };
        ApplicationThemeManager.Apply(theme, Backdrop, true);
        ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(0x1F, 0xA3, 0x86), theme, false, false);
        if (choice == "system" && IsLoaded) { SystemThemeWatcher.Watch(this, Backdrop, false); watching = true; }
    }

    private void ApplyHotkeys()
    {
        hotkeys ??= new HotkeyService(
            () => Dispatcher.BeginInvoke(() => _ = recorder.ToggleRecordingCommand.ExecuteAsync(null)),
            () => Dispatcher.BeginInvoke(() => _ = recorder.TogglePauseCommand.ExecuteAsync(null)),
            () => Dispatcher.BeginInvoke(() => _ = recorder.AddBookmarkCommand.ExecuteAsync(null)));
        hotkeys.Disable();
        if (host.Settings.GlobalHotkeys) hotkeys.Enable(new WindowInteropHelper(this).EnsureHandle());
        settings.ShowHotkeyConflicts(hotkeys.Conflicts);
    }

    /// <summary>A second launch of CallDock signals this one to come forward.</summary>
    private void ListenForActivation()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.BeginInvoke(ShowFromTray), null, Timeout.Infinite, false);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        e.Cancel = true;
        archive.Detail?.SaveDetails();
        Hide();
        if (!host.Settings.TrayHintShown)
        {
            tray?.Balloon("CallDock работает в трее", "Запись и расшифровка продолжаются. Открыть или выйти — через значок в трее.");
            host.Settings.TrayHintShown = true;
            host.SaveSettings();
        }
    }

    /// <summary>Starts a new CallDock once this one has exited (after the settings that need it).</summary>
    public async Task RestartAsync(bool toggleCompatibleDrawing = false)
    {
        if (host.Recorder.IsRecording)
        {
            host.Notifier.Warning("Идёт запись", "Перезапуск возможен после её завершения.");
            return;
        }
        if (toggleCompatibleDrawing)
        {
            host.Settings.SoftwareRendering = !host.Settings.SoftwareRendering;
            host.SaveSettings();
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, ArgumentList = { "--after", Environment.ProcessId.ToString() }
        });
        await QuitAsync();
    }

    public async Task QuitAsync()
    {
        if (quitting) return;
        if (host.Recorder.IsRecording)
        {
            ShowFromTray();
            var answer = await dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
            {
                Title = "Идёт запись",
                Content = "Завершить запись, сохранить её и выйти из CallDock?",
                PrimaryButtonText = "Завершить и выйти",
                CloseButtonText = "Продолжить запись"
            });
            if (answer != ContentDialogResult.Primary) return;
            await recorder.StopAsync();
        }
        quitting = true;
        pill.Stop();
        archive.Detail?.SaveDetails();
        recorder.SaveSources();
        hotkeys?.Dispose();
        calls?.Dispose();
        tray?.Dispose();
        try { await host.DisposeAsync(); }
        catch (Exception e) { Log.Error("Shutdown", e); }
        host.Updates.ApplyOnExit();
        Log.Info("CallDock closed");
        Application.Current.Shutdown();
    }
}
