using CallDock.Core;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace CallDock.App.Services;

/// <summary>
/// Short messages to the person: a snackbar in the window, or a tray balloon while CallDock is hidden. Messages
/// raised before the window is ready wait for it; a failure to show one is logged and never becomes another error.
/// </summary>
public sealed class Notifier
{
    private readonly SnackbarService snackbar = new();
    private readonly List<Action> waiting = [];
    private Func<bool> windowVisible = () => true;
    private TrayService? tray;
    private bool attached;

    public void Attach(SnackbarPresenter presenter, Func<bool> visible, TrayService? trayService)
    {
        snackbar.SetSnackbarPresenter(presenter);
        windowVisible = visible;
        tray = trayService;
        attached = true;
        foreach (var show in waiting) show();
        waiting.Clear();
    }

    public void Info(string title, string message) => Show(title, message, ControlAppearance.Secondary, SymbolRegular.Info24, false);
    public void Success(string title, string message) => Show(title, message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24, false);
    public void Warning(string title, string message) => Show(title, message, ControlAppearance.Caution, SymbolRegular.Warning24, true);

    public void Error(Exception exception, string title = "Не получилось")
    {
        Log.Error(title, exception);
        Show(title, exception.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24, true);
    }

    private void Show(string title, string message, ControlAppearance appearance, SymbolRegular icon, bool important)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            void Present()
            {
                try
                {
                    if (!windowVisible() && tray is not null)
                    {
                        if (important) tray.Balloon(title, message, warning: true);
                        return;
                    }
                    snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(important ? 8 : 4));
                }
                catch (Exception e) { Log.Warn($"Notification not shown ({title}): {e.Message}"); }
            }
            if (attached) Present(); else waiting.Add(Present);
        });
    }
}
