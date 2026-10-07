using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CallDock.App.Services;

/// <summary>
/// System-wide shortcuts, so a recording can be started, paused, stopped and marked from a game or a call window:
/// Ctrl+Alt+R, Ctrl+Alt+P and Ctrl+Alt+M. A shortcut another program already holds is reported, not stolen.
/// </summary>
public sealed partial class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    private const int ToggleId = 0xCD01, BookmarkId = 0xCD02, PauseId = 0xCD03;
    private HwndSource? source;
    private readonly Action toggle;
    private readonly Action pause;
    private readonly Action bookmark;

    public const string ToggleGesture = "Ctrl+Alt+R";
    public const string PauseGesture = "Ctrl+Alt+P";
    public const string BookmarkGesture = "Ctrl+Alt+M";
    /// <summary>Shortcuts that could not be registered because another program uses them.</summary>
    public IReadOnlyList<string> Conflicts { get; private set; } = [];
    public bool Enabled => source is not null;

    public HotkeyService(Action toggle, Action pause, Action bookmark)
    {
        this.toggle = toggle;
        this.pause = pause;
        this.bookmark = bookmark;
    }

    public void Enable(IntPtr window)
    {
        if (source is not null) return;
        source = HwndSource.FromHwnd(window);
        source?.AddHook(Hook);
        var conflicts = new List<string>();
        if (!RegisterHotKey(window, ToggleId, ModControl | ModAlt | ModNoRepeat, 0x52)) conflicts.Add(ToggleGesture); // R
        if (!RegisterHotKey(window, PauseId, ModControl | ModAlt | ModNoRepeat, 0x50)) conflicts.Add(PauseGesture); // P
        if (!RegisterHotKey(window, BookmarkId, ModControl | ModAlt | ModNoRepeat, 0x4D)) conflicts.Add(BookmarkGesture); // M
        Conflicts = conflicts;
    }

    public void Disable()
    {
        if (source is null) return;
        UnregisterHotKey(source.Handle, ToggleId);
        UnregisterHotKey(source.Handle, PauseId);
        UnregisterHotKey(source.Handle, BookmarkId);
        source.RemoveHook(Hook);
        source = null;
        Conflicts = [];
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case ToggleId: toggle(); handled = true; break;
            case PauseId: pause(); handled = true; break;
            case BookmarkId: bookmark(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Disable();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
