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
        Player.PositionChanged += (track, seconds) => vm.Detail?.Follow(track, seconds);
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
        }
        attached = detail;
        if (detail is null) { Player.Stop(); return; }
        detail.PlayRequested += Play;
        detail.WatchRequested += Watch;
        detail.DeleteRequested += ConfirmDelete;
        detail.CurrentRowChanged += ShowRow;
        if (Player.SessionId != detail.Session.Id) Player.Stop();
    }

    private void ShowRow(TranscriptRowViewModel row) => TranscriptList.ScrollIntoView(row);

    private void Play(RecordingTrack track, double seconds)
    {
        if (attached is null) return;
        Player.Visibility = Visibility.Visible;
        _ = Player.LoadAsync(attached.Session, track, seconds);
    }

    private void Watch(RecordingTrack track, double seconds)
    {
        if (attached is null) return;
        Player.Pause();
        _ = VideoWindow.OpenAsync(Window.GetWindow(this), Player.Host!, attached.Session, track, seconds);
    }

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
