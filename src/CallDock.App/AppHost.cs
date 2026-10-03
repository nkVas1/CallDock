using CallDock.App.Services;
using CallDock.Core;

namespace CallDock.App;

/// <summary>Everything CallDock runs on, created once at start-up and shared by the pages.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppSettings Settings { get; } = AppPaths.LoadSettings();
    public Archive Archive { get; }
    public SessionController Recorder { get; }
    public ProcessingQueue Processing { get; }
    public BrowserBridge Bridge { get; }
    public UpdateService Updates { get; } = new();
    public Notifier Notifier { get; } = new();
    /// <summary>Messages from start-up recovery (recordings finished after a crash).</summary>
    public IReadOnlyList<string> Recovered { get; }
    public string? BridgeError { get; private set; }
    public string? StartupWarning { get; }
    /// <summary>Raised when settings that the pages show have changed (theme, archive folder, model).</summary>
    public event Action? SettingsChanged;

    public AppHost()
    {
        try { Archive = new Archive(Settings.ArchiveRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The archive folder is on a drive that is gone (a disconnected disk): record into the default folder.
            Log.Error($"Archive folder unavailable: {Settings.ArchiveRoot}", e);
            StartupWarning = $"Папка архива недоступна ({Settings.ArchiveRoot}). Пока она не вернётся, записи сохраняются в папку по умолчанию.";
            Archive = new Archive(new AppSettings().ArchiveRoot);
        }
        Recovered = Archive.Recover();
        Recorder = new SessionController(Archive, Settings);
        Processing = new ProcessingQueue(Archive, Settings, () => Recorder.IsRecording);
        Bridge = new BrowserBridge(Recorder);
        Recorder.Changed += () => { if (!Recorder.IsRecording) Processing.Wake(); };
    }

    public async Task StartAsync()
    {
        try { await Bridge.StartAsync(); }
        catch (Exception e)
        {
            BridgeError = "Порт 47831 занят другой программой — запись вкладок Chrome недоступна.";
            Log.Error("Chrome bridge failed to start", e);
        }
        Processing.Resume(Archive.Search(limit: 500));
        Processing.Start();
    }

    public void SaveSettings()
    {
        AppPaths.AtomicJson(AppPaths.SettingsFile, Settings);
        SettingsChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (Recorder.IsRecording) await Recorder.StopAsync();
        await Processing.DisposeAsync();
        await Bridge.DisposeAsync();
    }
}
