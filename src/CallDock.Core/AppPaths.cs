using System.Text.Json;

namespace CallDock.Core;

public static class AppPaths
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static string? Override => Environment.GetEnvironmentVariable("CALLDOCK_DATA");

    /// <summary>Settings and the Chrome pairing code. They live apart from the program: the installer owns
    /// %LOCALAPPDATA%\CallDock and removes it on uninstall, while settings (the archive folder among them) must outlive a reinstall.</summary>
    public static string SettingsFolder => Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CallDock");

    /// <summary>Speech models, logs and playback fragments: large or disposable, they stay on this computer.</summary>
    public static string Data => Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CallDock");
    public static string Models => Path.Combine(Data, "models");

    /// <summary>Documents\CallDock; with CALLDOCK_DATA set (tests, a second copy) the archive lives in that folder too.</summary>
    public static string DefaultArchiveRoot => Override is { } folder
        ? Path.Combine(folder, "archive")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CallDock");
    public static string SettingsFile => SettingsPath("settings.json");
    public static string BrowserTokenFile => SettingsPath("browser-token.txt");

    /// <summary>A file of the settings folder. Versions before 1.0 kept it with the models; it is copied over once.</summary>
    private static string SettingsPath(string name)
    {
        var path = Path.Combine(SettingsFolder, name);
        var legacy = Path.Combine(Data, name);
        if (!File.Exists(path) && File.Exists(legacy) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(legacy), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                File.Copy(legacy, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return path;
    }
    public static string Ffmpeg => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");

    public static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, Json);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsFile)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), Json) ?? new(); }
        catch (JsonException) { File.Copy(SettingsFile, SettingsFile + ".invalid", true); return new(); }
    }

    public static void EnsureSpace(string path, long minimumBytes = 512L * 1024 * 1024)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive && drive.AvailableFreeSpace < minimumBytes)
            throw new IOException("Недостаточно места на диске. Освободите не менее 512 МБ.");
    }

    public static string Within(string root, string relative)
    {
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы архива.");
        return path;
    }
}
