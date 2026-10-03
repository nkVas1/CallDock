using CallDock.App.Services;
using CallDock.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace CallDock.App.Views;

/// <summary>A video track in its own window: screen and window recordings play as they are, Chrome tabs and streams
/// as a converted two-minute fragment from the chosen moment.</summary>
public sealed class VideoWindow : FluentWindow
{
    private readonly MediaElement media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true };
    private readonly Slider slider = new() { VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private readonly System.Windows.Controls.TextBlock time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), FontFamily = new("Cascadia Mono, Consolas") };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly IReadOnlyList<PlaybackEntry> playlist;
    private int index;
    private double pending;
    private bool playing = true;

    public static async Task OpenAsync(Window? owner, AppHost host, CallSession session, RecordingTrack track, double seconds)
    {
        try
        {
            var playlist = await Playback.PlanAsync(host.Archive, session, track, seconds, video: true, CancellationToken.None);
            var window = new VideoWindow(playlist, seconds, $"{track.Name} — {session.Title}") { Owner = owner };
            window.Show();
        }
        catch (Exception e) { host.Notifier.Error(e, "Не удалось открыть видео"); }
    }

    private VideoWindow(IReadOnlyList<PlaybackEntry> playlist, double seconds, string title)
    {
        this.playlist = playlist;
        Title = title;
        Width = 960; Height = 640; MinWidth = 520; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TitleBar { Title = title });
        var stage = new Border { Background = System.Windows.Media.Brushes.Black, Margin = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(6), Child = media };
        Grid.SetRow(stage, 1);
        root.Children.Add(stage);
        var controls = new DockPanel { Margin = new Thickness(12) };
        var play = new Wpf.Ui.Controls.Button { Icon = new SymbolIcon(SymbolRegular.Pause24), Appearance = ControlAppearance.Primary, Padding = new Thickness(10, 6, 10, 6) };
        play.Click += (_, _) =>
        {
            playing = !playing;
            if (playing) media.Play(); else media.Pause();
            play.Icon = new SymbolIcon(playing ? SymbolRegular.Pause24 : SymbolRegular.Play24);
        };
        DockPanel.SetDock(play, Dock.Left);
        DockPanel.SetDock(time, Dock.Left);
        controls.Children.Add(play);
        controls.Children.Add(time);
        if (playlist[0].Temporary)
        {
            var note = new System.Windows.Controls.TextBlock { Text = "фрагмент 2 минуты", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(note, Dock.Right);
            controls.Children.Add(note);
        }
        controls.Children.Add(slider);
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);
        Content = root;

        slider.Minimum = playlist[0].Start;
        slider.Maximum = playlist[^1].End;
        slider.PreviewMouseLeftButtonUp += (_, _) => Seek(slider.Value);
        media.MediaOpened += (_, _) => { media.Position = TimeSpan.FromSeconds(pending); if (playing) media.Play(); };
        media.MediaEnded += (_, _) => { if (index + 1 < playlist.Count) Open(index + 1, 0); };
        media.MediaFailed += (_, e) => time.Text = "Не удалось воспроизвести: " + e.ErrorException.Message;
        timer.Tick += (_, _) =>
        {
            var now = playlist[index].Start + media.Position.TotalSeconds;
            time.Text = Display.Duration(now);
            if (!slider.IsMouseCaptureWithin) slider.Value = Math.Clamp(now, slider.Minimum, slider.Maximum);
        };
        Loaded += (_, _) => { Seek(seconds); timer.Start(); };
        Closed += (_, _) =>
        {
            timer.Stop();
            media.Close();
            foreach (var entry in playlist.Where(p => p.Temporary)) { try { File.Delete(entry.File); } catch (IOException) { } }
        };
    }

    private void Seek(double seconds)
    {
        var target = Math.Clamp(seconds, playlist[0].Start, playlist[^1].End - 0.05);
        var at = 0;
        for (var i = playlist.Count - 1; i >= 0; i--) if (playlist[i].Start <= target) { at = i; break; }
        if (at == index && media.Source is not null) media.Position = TimeSpan.FromSeconds(target - playlist[at].Start);
        else Open(at, target - playlist[at].Start);
    }

    private void Open(int at, double local)
    {
        index = at;
        pending = local;
        media.Source = new Uri(playlist[at].File);
        media.Play();
    }
}
