using CallDock.App.Services;
using CallDock.App.ViewModels;
using CallDock.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using MenuItem = System.Windows.Controls.MenuItem;

namespace CallDock.App.Views;

public partial class ArchiveView : UserControl
{
    private readonly ArchiveViewModel vm;
    private readonly IContentDialogService dialogs;
    private SessionDetailViewModel? attached;

    public ArchiveView(ArchiveViewModel vm, IContentDialogService dialogs, AppHost host)
    {
        this.vm = vm;
        this.dialogs = dialogs;
        DataContext = vm;
        InitializeComponent();
        Player.Host = host;
        Player.PositionChanged += (speaker, seconds) => vm.Detail?.Follow(speaker, seconds);
        vm.PropertyChanged += OnViewModelChanged;
        Attach(vm.Detail);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchiveViewModel.Detail)) Attach(vm.Detail);
    }

    /// <summary>The open recording talks to the player and asks for confirmations through this page.</summary>
    private void Attach(SessionDetailViewModel? detail)
    {
        if (attached is not null)
        {
            attached.PlayRequested -= Play;
            attached.WatchRequested -= Watch;
            attached.DeleteRequested -= ConfirmDelete;
            attached.CurrentRowChanged -= ShowRow;
            attached.MixRequested -= ChooseMix;
        }
        attached = detail;
        if (detail is null) { Player.Stop(); return; }
        detail.PlayRequested += Play;
        detail.WatchRequested += Watch;
        detail.DeleteRequested += ConfirmDelete;
        detail.CurrentRowChanged += ShowRow;
        detail.MixRequested += ChooseMix;
        if (Player.SessionId != detail.Session.Id) Player.Stop();
    }

    private void ShowRow(TranscriptRowViewModel row) => TranscriptList.ScrollIntoView(row);

    private void Play(PlaybackSource source, double seconds)
    {
        if (attached is null) return;
        Player.Visibility = Visibility.Visible;
        _ = Player.LoadAsync(attached.Session, source, seconds);
    }

    private void Watch(PlaybackSource source, double seconds)
    {
        if (attached is null) return;
        Player.Pause();
        _ = VideoWindow.OpenAsync(Window.GetWindow(this), Player.Host!, attached.Session, source, seconds);
    }

    /// <summary>What to mix: the sound tracks to hear and the picture to show (or none). Starts from the current mix,
    /// or from the automatic choice.</summary>
    private async void ChooseMix()
    {
        if (attached is null) return;
        var detail = attached;
        var session = detail.Session;
        var sounds = session.Tracks.Where(t => t.HasAudio).ToArray();
        var pictures = session.Tracks.Where(t => t.HasVideo).ToArray();
        if (sounds.Length == 0) { vm.Notify("Нечего сводить", "В этой записи нет звуковых дорожек."); return; }
        var chosen = session.Mix?.Tracks.ToHashSet() ?? sounds.Where(t => !MixPlan.IsBroadcast(t) || sounds.Count(MixPlan.IsBroadcast) < 2).Select(t => t.Id).ToHashSet();
        var picture = session.Mix is { } mix ? (mix.TrackId ?? (mix.HasVideo ? MixPlan.Picture(pictures)?.Id : null)) : MixPlan.Picture(pictures)?.Id;

        var panel = new StackPanel { MinWidth = 420 };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Сведение — один файл со звуком выбранных дорожек, каждая на своём месте во времени. Исходные дорожки не меняются; прежнее сведение заменится.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 14)
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Звук", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var boxes = sounds.Select(t => new CheckBox { Content = Label(t), IsChecked = chosen.Contains(t.Id), Tag = t.Id, Margin = new Thickness(0, 2, 0, 2) }).ToArray();
        foreach (var box in boxes) panel.Children.Add(box);
        var radios = new List<RadioButton>();
        if (pictures.Length > 0)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Картинка", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
            radios.Add(new RadioButton { Content = "Без видео — только звук (M4A)", GroupName = "MixPicture", IsChecked = picture is null, Margin = new Thickness(0, 2, 0, 2) });
            radios.AddRange(pictures.Select(t => new RadioButton
            {
                Content = Label(t), GroupName = "MixPicture", IsChecked = t.Id == picture, Tag = t.Id, Margin = new Thickness(0, 2, 0, 2)
            }));
            foreach (var radio in radios) panel.Children.Add(radio);
        }
        var dialog = new ContentDialog
        {
            Title = "Свести дорожки", Content = panel, PrimaryButtonText = "Свести", CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary, DialogMaxWidth = 620
        };
        if (await dialogs.ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary) return;
        var audio = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToArray();
        if (audio.Length == 0) { vm.Notify("Не выбран звук", "Отметьте хотя бы одну дорожку со звуком."); return; }
        var video = radios.FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
        detail.RequestMix(new MixRequest(audio, video));
    }

    /// <summary>«Я · микрофон · 00:00:05–00:00:13»: a source switched off and on has several tracks with one name.</summary>
    private static string Label(RecordingTrack track) => $"{track.Name} · {Kind(track)}" +
        (track.EndSeconds > 0 ? $" · {Display.Duration(track.OffsetSeconds)}–{Display.Duration(track.EndSeconds)}" : "");

    private static string Kind(RecordingTrack track) => track.Kind switch
    {
        SourceKind.Microphone => "микрофон",
        SourceKind.SystemAudio => "системный звук",
        SourceKind.Application => "звук приложения",
        SourceKind.Screen => "экран",
        SourceKind.Window => "окно",
        SourceKind.BrowserTab => "вкладка Chrome",
        _ => "поток"
    };

    private async void ConfirmDelete()
    {
        if (attached is null) return;
        var detail = attached;
        var answer = await dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Удалить запись?",
            Content = $"«{detail.Session.Title}» со всеми дорожками и расшифровкой переместится в Корзину Windows. Вернуть её можно оттуда.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена"
        });
        if (answer != ContentDialogResult.Primary) return;
        Player.Stop();
        detail.DeleteConfirmed();
    }

    private void OpenExportMenu(object sender, RoutedEventArgs e)
    {
        if (attached is null) return;
        var detail = attached;
        var menu = new ContextMenu { PlacementTarget = ExportButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Item("Текст расшифровки — TXT, MD, SRT, VTT…", SymbolRegular.DocumentText24, detail.ExportTextCommand, detail.HasTranscript));
        if (detail.HasMix)
            menu.Items.Add(Item(detail.MixHasVideo ? "Сведение — видео со звуком (MP4)…" : "Сведение — все голоса (M4A)…", SymbolRegular.PersonVoice24,
                detail.ExportMixFileCommand, true));
        menu.Items.Add(Item("Все голоса одной дорожкой — MP3, M4A, FLAC, WAV…", SymbolRegular.PersonVoice24, detail.ExportMixCommand,
            detail.Session.Tracks.Any(t => t.HasAudio)));
        menu.Items.Add(new Separator());
        foreach (var track in detail.Tracks)
            menu.Items.Add(Item($"Дорожка «{track.Name}»…", track.Icon, detail.ExportTrackCommand, true, track));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string title, SymbolRegular icon, System.Windows.Input.ICommand command, bool enabled, object? parameter = null) =>
        new() { Header = title, Icon = new SymbolIcon(icon), Command = command, CommandParameter = parameter, IsEnabled = enabled };
}
