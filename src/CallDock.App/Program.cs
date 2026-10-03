using Velopack;

namespace CallDock.App;

/// <summary>
/// Entry point. Velopack first (it handles install, update and uninstall hooks and may exit the process),
/// then a single instance: a second launch brings the running CallDock to the front instead of starting
/// another recorder that would fight over devices and the Chrome bridge port.
/// </summary>
public static class Program
{
    private const string InstanceName = @"Local\CallDock.Desktop";
    public const string ActivateEventName = @"Local\CallDock.Activate";

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        // A restart (new settings, compatible drawing): wait until the previous CallDock has gone.
        if (args is ["--after", var pidText, ..] && int.TryParse(pidText, out var pid))
        {
            try { using var previous = System.Diagnostics.Process.GetProcessById(pid); previous.WaitForExit(30_000); }
            catch (ArgumentException) { }
        }

        Mutex? instance = null;
        if (args is not ["--verify-hardware", _])
        {
            instance = new Mutex(true, InstanceName, out var created);
            if (!created)
            {
                instance.Dispose();
                try { using var activate = EventWaitHandle.OpenExisting(ActivateEventName); activate.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
        }
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        finally
        {
            instance?.ReleaseMutex();
            instance?.Dispose();
        }
    }
}
