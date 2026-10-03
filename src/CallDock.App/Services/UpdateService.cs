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
    private bool checking;

    public bool IsInstalled => manager.IsInstalled;
    public string? PendingVersion => ready?.TargetFullRelease.Version.ToString();

    /// <summary>What the downloaded version brings: its CHANGELOG section, as plain text.</summary>
    public string? PendingNotes => ready?.TargetFullRelease.NotesMarkdown is { Length: > 0 } notes ? PlainText(notes) : null;

    /// <summary>Headings lose their marks and list items get bullets; the rest stays as written.</summary>
    private static string PlainText(string markdown) => string.Join('\n', markdown.Replace("\r", "").Split('\n')
        .Select(line => line.StartsWith('#') ? line.TrimStart('#', ' ') : line.StartsWith("- ", StringComparison.Ordinal) ? "• " + line[2..] : line)).Trim();

    public string Status { get; private set; } = "";
    public event Action? Changed;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!manager.IsInstalled)
        {
            Set("Обновления работают в установленной версии CallDock.");
            return;
        }
        if (checking || ready is not null) return;
        checking = true;
        try
        {
            Set("Проверяю обновления…");
            // A proxy that accepts the connection and never answers would keep the check going forever.
            var info = await manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromMinutes(1), ct);
            if (info is null)
            {
                Set($"У вас последняя версия · {AppInfo.Version}");
                return;
            }
            var version = info.TargetFullRelease.Version.ToString();
            Set($"Загружаю версию {version}…");
            await manager.DownloadUpdatesAsync(info, p => Set($"Загружаю версию {version} · {p}%"), ct).WaitAsync(TimeSpan.FromMinutes(30), ct);
            ready = info;
            Set($"Версия {version} загружена и установится при следующем запуске.");
            Log.Info($"Update {version} downloaded");
        }
        catch (OperationCanceledException) { Set(""); }
        catch (TimeoutException)
        {
            Set("GitHub не ответил — проверю обновления при следующем запуске. Проверьте интернет или прокси.");
            Log.Warn("Update check timed out");
        }
        catch (Exception e)
        {
            Set("Не удалось проверить обновления: " + e.Message);
            Log.Warn("Update check failed: " + e.Message);
        }
        finally { checking = false; }
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
