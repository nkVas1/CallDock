using CallDock.Core;
using Velopack;
using Velopack.Sources;

namespace CallDock.App.Services;

/// <summary>
/// Updates from the GitHub releases of CallDock (Velopack). A new version downloads in the background and
/// replaces the old one when CallDock next starts — never in the middle of a recording. Copies run from a
/// build folder (not installed) do not update.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager manager = new(new GithubSource(AppInfo.RepositoryUrl, null, false));
    private UpdateInfo? ready;

    public bool IsInstalled => manager.IsInstalled;
    public string? PendingVersion => ready?.TargetFullRelease.Version.ToString();
    public string Status { get; private set; } = "";
    public event Action? Changed;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!manager.IsInstalled)
        {
            Set("Обновления работают в установленной версии CallDock.");
            return;
        }
        try
        {
            Set("Проверяю обновления…");
            var info = await manager.CheckForUpdatesAsync();
            if (info is null)
            {
                Set($"У вас последняя версия · {AppInfo.Version}");
                return;
            }
            var version = info.TargetFullRelease.Version.ToString();
            Set($"Загружаю версию {version}…");
            await manager.DownloadUpdatesAsync(info, p => Set($"Загружаю версию {version} · {p}%"), ct);
            ready = info;
            Set($"Версия {version} загружена и установится при следующем запуске.");
            Log.Info($"Update {version} downloaded");
        }
        catch (OperationCanceledException) { Set(""); }
        catch (Exception e)
        {
            Set("Не удалось проверить обновления: " + e.Message);
            Log.Warn("Update check failed: " + e.Message);
        }
    }

    /// <summary>Restarts into the downloaded version right away (the caller makes sure nothing is recording).</summary>
    public void RestartNow()
    {
        if (ready is not null) manager.ApplyUpdatesAndRestart(ready.TargetFullRelease);
    }

    /// <summary>On exit: the downloaded version replaces this one after the process ends.</summary>
    public void ApplyOnExit()
    {
        if (ready is null) return;
        try { manager.WaitExitThenApplyUpdates(ready.TargetFullRelease, true, false, []); }
        catch (Exception e) { Log.Warn("Deferred update failed: " + e.Message); }
    }

    private void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
