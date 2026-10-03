using CallDock.App.Services;
using CallDock.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace CallDock.App.Views;

/// <summary>
/// The archive's player: plays one track along the session timeline, segment after segment, and reports where it is
/// so the transcript can follow. Times shown are session times — the same as in the transcript and bookmarks.
/// </summary>
public partial class PlayerBar : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<PlaybackEntry> playlist = [];
    private RecordingTrack? track;
    private int index = -1;
    private double pendingLocal;
    private bool playing;
    private bool dragging;
    /// <summary>The current file is open. Until then the player reports position 0, which must not move the transcript.</summary>
    private bool opened;
    private CancellationTokenSource? loading;

    public PlayerBar()
    {
        InitializeComponent();
        timer.Tick += (_, _) => Tick();
        Unloaded += (_, _) => Pause();
    }

    public AppHost? Host { get; set; }
    public string? SessionId { get; private set; }
    /// <summary>The track being played and the session time now.</summary>
    public event Action<string, double>? PositionChanged;

    public async Task LoadAsync(CallSession session, RecordingTrack target, double sessionSeconds)
    {
        if (Host is null) return;
        Visibility = Visibility.Visible;
        if (SessionId == session.Id && track?.Id == target.Id && playlist.Count > 0 && !playlist[0].Temporary)
        {
            Seek(sessionSeconds);
            Play();
            return;
        }
        Stop(keepVisible: true);
        loading = new CancellationTokenSource();
        var ct = loading.Token;
        SessionId = session.Id;
        track = target;
        TrackText.Text = target.Name;
        NoteText.Text = "Готовлю…";
        try
        {
            playlist = await Playback.PlanAsync(Host.Archive, session, target, sessionSeconds, video: false, ct);
            if (ct.IsCancellationRequested) return;
            NoteText.Text = playlist[0].Temporary ? "фрагмент 10 минут" : "";
            Position.Minimum = playlist[0].Start;
            Position.Maximum = playlist[^1].End;
            Seek(sessionSeconds);
            Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            NoteText.Text = "";
            Host.Notifier.Error(e, "Не удалось воспроизвести");
        }
    }

    private void Seek(double sessionSeconds)
    {
        if (playlist.Count == 0) return;
        var target = Math.Clamp(sessionSeconds, playlist[0].Start, Math.Max(playlist[0].Start, playlist[^1].End - 0.05));
        var at = Math.Max(0, IndexAt(target));
        var local = target - playlist[at].Start;
        Show(target);
        if (at == index && Media.Source is not null)
        {
            if (opened) Media.Position = TimeSpan.FromSeconds(local);
            else pendingLocal = local;
            return;
        }
        index = at;
        pendingLocal = local;
        opened = false;
        Media.Source = new Uri(playlist[at].File);
    }

    private void Show(double sessionSeconds)
    {
        TimeText.Text = Display.Duration(sessionSeconds);
        if (!dragging) Position.Value = Math.Clamp(sessionSeconds, Position.Minimum, Position.Maximum);
    }

    private int IndexAt(double seconds)
    {
        for (var i = playlist.Count - 1; i >= 0; i--) if (playlist[i].Start <= seconds) return i;
        return 0;
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        opened = true;
        Media.Position = TimeSpan.FromSeconds(pendingLocal);
        pendingLocal = 0;
        if (playing) Media.Play();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (index + 1 < playlist.Count)
        {
            index++;
            pendingLocal = 0;
            opened = false;
            Media.Source = new Uri(playlist[index].File);
            Media.Play();
            return;
        }
        Pause();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Pause();
        Host?.Notifier.Warning("Не удалось воспроизвести", e.ErrorException.Message);
    }

    private void Play()
    {
        if (Media.Source is null) return;
        playing = true;
        Media.Play();
        timer.Start();
        PlayIcon.Symbol = SymbolRegular.Pause24;
    }

    public void Pause()
    {
        playing = false;
        Media.Pause();
        timer.Stop();
        PlayIcon.Symbol = SymbolRegular.Play24;
    }

    public void Stop() => Stop(keepVisible: false);

    private void Stop(bool keepVisible)
    {
        loading?.Cancel();
        Pause();
        Media.Close();
        Media.Source = null;
        foreach (var entry in playlist.Where(p => p.Temporary))
        {
            try { File.Delete(entry.File); } catch (IOException) { }
        }
        playlist = [];
        index = -1;
        opened = false;
        track = null;
        SessionId = keepVisible ? SessionId : null;
        TimeText.Text = "00:00:00";
        Position.Value = Position.Minimum;
        if (!keepVisible) Visibility = Visibility.Collapsed;
    }

    private double Now => index >= 0 && index < playlist.Count ? playlist[index].Start + Media.Position.TotalSeconds : 0;

    private void Tick()
    {
        if (index < 0 || !opened) return;
        var now = Now;
        Show(now);
        if (track is not null) PositionChanged?.Invoke(track.Name, now);
    }

    private void TogglePlay(object sender, RoutedEventArgs e) { if (playing) Pause(); else Play(); }
    private void Back(object sender, RoutedEventArgs e) => Seek(Now - 10);
    private void Forward(object sender, RoutedEventArgs e) => Seek(Now + 10);
    private void Close(object sender, RoutedEventArgs e) => Stop();
    private void DragStarted(object sender, RoutedEventArgs e) => dragging = true;

    private void DragCompleted(object sender, RoutedEventArgs e)
    {
        dragging = false;
        Seek(Position.Value);
    }

    private void SliderClicked(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) Seek(Position.Value);
    }
}
