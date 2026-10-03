using System.Windows;
using Forms = System.Windows.Forms;

namespace CallDock.App.Services;

/// <summary>
/// CallDock lives in the tray while it records: the icon turns red, the menu starts and stops a recording,
/// sets a bookmark and opens the window. Closing the window only hides it.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon idle = Load("tray-idle.ico");
    private readonly System.Drawing.Icon recording = Load("tray-recording.ico");
    private readonly Forms.ToolStripMenuItem toggle;
    private readonly Forms.ToolStripMenuItem bookmark;

    public TrayService(Action open, Action toggleRecording, Action addBookmark, Action quit, Action toggleCompatibleDrawing, bool compatibleDrawing)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        toggle = new Forms.ToolStripMenuItem("Начать запись", null, (_, _) => toggleRecording());
        bookmark = new Forms.ToolStripMenuItem("Поставить метку", null, (_, _) => addBookmark()) { Enabled = false };
        menu.Items.Add(new Forms.ToolStripMenuItem("Открыть CallDock", null, (_, _) => open()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(toggle);
        menu.Items.Add(bookmark);
        menu.Items.Add(new Forms.ToolStripSeparator());
        // The tray menu is drawn by Windows itself: it works even when old video drivers leave the window blank.
        menu.Items.Add(new Forms.ToolStripMenuItem(compatibleDrawing ? "Вернуть обычную отрисовку окна" : "Окно пустое? Совместимая отрисовка", null,
            (_, _) => toggleCompatibleDrawing()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Выход", null, (_, _) => quit()));
        icon = new Forms.NotifyIcon { Icon = idle, Text = "CallDock", Visible = true, ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void SetRecording(bool active, string? elapsed = null)
    {
        icon.Icon = active ? recording : idle;
        var text = active ? $"CallDock — идёт запись {elapsed}".TrimEnd() : "CallDock — готов к записи";
        icon.Text = text.Length > 63 ? text[..63] : text;
        toggle.Text = active ? "Завершить запись" : "Начать запись";
        bookmark.Enabled = active;
    }

    public void Balloon(string title, string message, bool warning = false) =>
        icon.ShowBalloonTip(4000, title, message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    private static System.Drawing.Icon Load(string name)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))
            ?? throw new InvalidOperationException($"Missing resource {name}");
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        idle.Dispose();
        recording.Dispose();
    }
}
