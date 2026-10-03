using CallDock.App.Services;
using CallDock.App.Shell;
using CallDock.Core;
using System.Windows;
using System.Windows.Threading;

namespace CallDock.App;

public partial class App : Application
{
    private AppHost? host;
    private bool reporting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--verify-hardware", var folder])
        {
            try { await HardwareVerification.RunAsync(folder); Shutdown(0); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "failure.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }

        // Old or broken video drivers can leave WPF windows blank: CALLDOCK_SOFTWARE_RENDERING=1 draws without the GPU.
        if (Environment.GetEnvironmentVariable("CALLDOCK_SOFTWARE_RENDERING") == "1" || AppPaths.LoadSettings().SoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task", args.Exception); args.SetObserved(); };
        Log.Info($"CallDock {AppInfo.Version} starting on Windows {Environment.OSVersion.Version}");
        Playback.CleanPreviews();

        try
        {
            host = new AppHost();
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Start-up failed", ex);
            System.Windows.MessageBox.Show($"CallDock не смог запуститься: {ex.Message}\n\nПодробности — в журнале: {Log.Folder}", "CallDock",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(host);
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        Log.Error("UI error", args.Exception);
        if (reporting) return;
        reporting = true;
        try { host?.Notifier.Error(args.Exception, "Что-то пошло не так"); }
        finally { reporting = false; }
    }
}
