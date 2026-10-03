using System.Reflection;

namespace CallDock.Core;

public static class AppInfo
{
    public const string Name = "CallDock";
    public const string RepositoryUrl = "https://github.com/nkVas1/CallDock";

    /// <summary>The product version from the build (1.2.3), without the source revision suffix.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

/// <summary>What this Windows can do. Per-application audio capture (process loopback) needs Windows 10 build 20348+,
/// in practice Windows 11.</summary>
public static class Capabilities
{
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows10.0.20348")]
    public static bool ApplicationAudio { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public const string ApplicationAudioHint =
        "Звук отдельного приложения пишется в Windows 11. В этой версии Windows выберите «Системный звук» или вкладку Chrome.";
}

/// <summary>A small daily log in %LOCALAPPDATA%\CallDock\logs, kept for two weeks — for diagnosing problems on a team PC.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static bool cleaned;

    public static string Folder => Path.Combine(AppPaths.Data, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                if (!cleaned) { Cleanup(); cleaned = true; }
                File.AppendAllText(Path.Combine(Folder, $"calldock-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Cleanup()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "calldock-*.log"))
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14)) File.Delete(file);
    }
}
