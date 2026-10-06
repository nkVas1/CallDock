
using Microsoft.Win32;
using System.Diagnostics;

namespace CallDock.Core;

/// <summary>An application using the microphone: its record in the Windows privacy store and a readable name.</summary>
public sealed record MicrophoneUser(string Id, string Name);

/// <summary>
/// Notices calls. Windows keeps a record of which applications use the microphone — the same record behind the
/// microphone icon in the taskbar. When an application other than CallDock starts using it, a call has most likely
/// begun (Zoom, Telegram, Teams, a meeting in the browser); when it lets go, the call is over. The record is read from
/// the registry every two seconds; nothing leaves the computer.
/// </summary>
public sealed class CallDetector : IDisposable
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private readonly System.Threading.Timer timer;
    private readonly object gate = new();
    private Dictionary<string, MicrophoneUser> users = [];
    private bool disposed;

    /// <summary>Raised on a background thread when an application starts using the microphone.</summary>
    public event Action<MicrophoneUser>? Started;
    /// <summary>Raised on a background thread when an application stops using the microphone.</summary>
    public event Action<MicrophoneUser>? Stopped;

    public CallDetector()
    {
        users = Read().ToDictionary(u => u.Id);
        timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>The applications using the microphone now, CallDock itself excluded.</summary>
    public IReadOnlyList<MicrophoneUser> Current { get { lock (gate) return users.Values.ToArray(); } }

    private void Poll()
    {
        List<MicrophoneUser> started, stopped;
        lock (gate)
        {
            if (disposed) return;
            var now = Read().ToDictionary(u => u.Id);
            started = now.Values.Where(u => !users.ContainsKey(u.Id)).ToList();
            stopped = users.Values.Where(u => !now.ContainsKey(u.Id)).ToList();
            users = now;
        }
        foreach (var user in stopped) Stopped?.Invoke(user);
        foreach (var user in started) Started?.Invoke(user);
    }

    /// <summary>Every application whose last use of the microphone has a start and no end yet.</summary>
    public static IReadOnlyList<MicrophoneUser> Read()
    {
        var result = new List<MicrophoneUser>();
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(Store);
            if (store is null) return result;
            foreach (var name in store.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var desktop = store.OpenSubKey(name);
                    if (desktop is null) continue;
                    foreach (var path in desktop.GetSubKeyNames())
                    {
                        using var key = desktop.OpenSubKey(path);
                        if (key is not null && InUse(key) && !IsCallDock(path)) result.Add(new(path, DesktopName(path)));
                    }
                    continue;
                }
                using var packaged = store.OpenSubKey(name);
                if (packaged is not null && InUse(packaged)) result.Add(new(name, PackagedName(name)));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn("Microphone usage unreadable: " + e.Message);
        }
        return result;
    }

    private static bool InUse(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start > 0 && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    /// <summary>CallDock records the microphone itself; any copy of it (installed, portable, a build) is not a call.</summary>
    public static bool IsCallDock(string key) =>
        key.EndsWith("#CallDock.exe", StringComparison.OrdinalIgnoreCase) || key.EndsWith("#CallDock.Worker.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>«C:#Program Files#Zoom#bin#Zoom.exe» → the program's own description («Zoom Meetings»), or its file name.</summary>
    public static string DesktopName(string key)
    {
        var path = key.Replace('#', '\\');
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) return description.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>«5319275A.WhatsAppDesktop_cv1g1gvanyjgm» → «WhatsApp», «MSTeams_8wekyb3d8bbwe» → «Microsoft Teams».</summary>
    public static string PackagedName(string family)
    {
        var name = family.Split('_')[0];
        name = name[(name.LastIndexOf('.') + 1)..];
        if (name.Equals("MSTeams", StringComparison.OrdinalIgnoreCase)) return "Microsoft Teams";
        foreach (var suffix in new[] { "Desktop", "App" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        return name;
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        timer.Dispose();
    }
}
