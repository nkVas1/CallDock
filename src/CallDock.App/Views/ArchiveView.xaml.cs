using CallDock.App.Services;
using CallDock.App.ViewModels;
using CallDock.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

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
        Player.PositionChanged += (source, seconds) => vm.Detail?.Follow(source, seconds);
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
            attached.RowShown -= RevealRow;
            attached.RetranscribeRequested -= ConfirmRetranscribe;
        }
        attached = detail;
        if (detail is null) { Player.Stop(); return; }
        detail.PlayRequested += Play;
        detail.WatchRequested += Watch;
        detail.DeleteRequested += ConfirmDelete;
        detail.CurrentRowChanged += ShowRow;
        detail.MixRequested += ChooseMix;
        detail.RowShown += RevealRow;
        detail.RetranscribeRequested += ConfirmRetranscribe;
        if (Player.SessionId != detail.Session.Id) Player.Stop();
    }

    /// <summary>The phrase being played stays in view — unless the person is correcting one: the list must not run away.</summary>
    private void ShowRow(TranscriptRowViewModel row)
    {
        if (attached?.IsEditingText != true) TranscriptList.ScrollIntoView(row);
    }

    private void RevealRow(TranscriptRowViewModel row)
    {
        TranscriptList.SelectedItem = row;
        TranscriptList.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() => Container(row)?.Focus(), DispatcherPriority.Background);
    }

    // ----- The transcript: corrections in place, speakers, a menu on the right button -----

    /// <summary>Where the caret goes when a phrase opens for correction: on the word that was double-clicked.</summary>
    private int pendingCaret = -1;

    private ListBoxItem? Container(TranscriptRowViewModel row) => TranscriptList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;

    /// <summary>The phrases an action applies to: the selection when the phrase is part of it, otherwise the phrase alone.</summary>
    private IReadOnlyList<TranscriptRowViewModel> Chosen(TranscriptRowViewModel row) =>
        TranscriptList.SelectedItems.Count > 1 && TranscriptList.SelectedItems.Contains(row)
            ? TranscriptList.SelectedItems.Cast<TranscriptRowViewModel>().OrderBy(r => r.Segment.Start).ToArray()
            : [row];

    /// <summary>The phrase the keyboard is on: the focused row, or the selected one.</summary>
    private TranscriptRowViewModel? FocusedRow() =>
        (Keyboard.FocusedElement as FrameworkElement)?.DataContext as TranscriptRowViewModel ?? TranscriptList.SelectedItem as TranscriptRowViewModel;

    private void Edit(TranscriptRowViewModel row, int caret = -1)
    {
        if (attached is null) return;
        pendingCaret = caret;
        if (!attached.BeginEdit(row)) pendingCaret = -1;
    }

    /// <summary>A double click on a phrase opens it for correction with the clicked word selected: type the right one.</summary>
    private void PhraseMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not TextBlock { DataContext: TranscriptRowViewModel row } block) return;
        var position = block.GetPositionFromPoint(e.GetPosition(block), snapToText: true);
        Edit(row, position is null ? -1 : new TextRange(block.ContentStart, position).Text.Length);
        e.Handled = true;
    }

    private void EditClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranscriptRowViewModel row }) Edit(row);
    }

    /// <summary>The box appears: it takes the focus, with the clicked word selected or the caret at the end.</summary>
    private void EditShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box) return;
        var caret = pendingCaret;
        pendingCaret = -1;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            if (caret >= 0 && caret <= box.Text.Length) SelectWord(box, caret);
            else box.CaretIndex = box.Text.Length;
        }, DispatcherPriority.Input);
    }

    private static void SelectWord(TextBox box, int index)
    {
        var text = box.Text;
        int start = index, end = index;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1])) start--;
        while (end < text.Length && char.IsLetterOrDigit(text[end])) end++;
        if (end > start) box.Select(start, end - start);
        else box.CaretIndex = index;
    }

    private void EditKeyDown(object sender, KeyEventArgs e)
    {
        if (attached is null || sender is not TextBox { DataContext: TranscriptRowViewModel row }) return;
        if (e.Key == Key.Enter) attached.CommitEdit(row);
        else if (e.Key == Key.Escape) attached.CancelEdit(row);
        else return;
        e.Handled = true;
        Container(row)?.Focus();
    }

    /// <summary>Leaving the box saves the phrase, as renaming a file in Explorer does; Esc is the way out without saving.</summary>
    private void EditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (attached is not null && sender is TextBox { DataContext: TranscriptRowViewModel { IsEditing: true } row }) attached.CommitEdit(row);
    }

    /// <summary>F2 corrects, Delete removes, Enter plays, Ctrl+C copies the chosen phrases, Ctrl+Z undoes a correction.</summary>
    private void TranscriptKeyDown(object sender, KeyEventArgs e)
    {
        if (attached is null || e.OriginalSource is TextBox) return;
        var detail = attached;
        var row = FocusedRow();
        var control = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.F2 when row is not null: Edit(row); break;
            case Key.Delete when row is not null: detail.DeleteRows(Chosen(row)); break;
            case Key.Enter when row is not null: detail.PlayRowCommand.Execute(row); break;
            case Key.C when control && row is not null: detail.CopyRows(Chosen(row)); break;
            case Key.Z when control: detail.UndoCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private void TranscriptMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as TranscriptRowViewModel;
        if (attached is null || row is null || TranscriptList.ContextMenu is not { } menu) { e.Handled = true; return; }
        // A right click chooses the phrase, as in Explorer, unless it is part of a selection already.
        if (!TranscriptList.SelectedItems.Contains(row)) TranscriptList.SelectedItem = row;
        var detail = attached;
        var rows = Chosen(row);
        var editable = detail.CanEditTranscript;
        menu.Items.Clear();
        if (rows.Count == 1)
            menu.Items.Add(Action("Исправить текст", SymbolRegular.Edit24, () => Edit(row), editable, "F2"));
        menu.Items.Add(Action("Слушать с этого места", SymbolRegular.Play24, () => detail.PlayRowCommand.Execute(row), true, "Enter"));
        menu.Items.Add(new Separator());
        menu.Items.Add(SpeakerChoice(detail, rows, editable));
        menu.Items.Add(Action($"Переименовать «{row.Source}» во всей записи…", SymbolRegular.Rename24, () => _ = RenameSpeakerAsync(detail, row.Source), editable));
        if (rows.Count == 1 && row.IsEdited)
            menu.Items.Add(Action("Вернуть распознанный текст", SymbolRegular.ArrowReset24, () => detail.RestoreRecognized(row), editable));
        menu.Items.Add(new Separator());
        menu.Items.Add(Action(rows.Count == 1 ? "Копировать фразу" : $"Копировать фразы ({rows.Count})", SymbolRegular.Copy24, () => detail.CopyRows(rows), true, "Ctrl+C"));
        menu.Items.Add(Action(rows.Count == 1 ? "Удалить фразу" : $"Удалить фразы ({rows.Count})", SymbolRegular.Delete24, () => detail.DeleteRows(rows), editable, "Delete"));
    }

    /// <summary>A click on the speaker over a phrase: rename the speaker, or give the phrase to someone else.</summary>
    private void OpenSpeakerMenu(object sender, RoutedEventArgs e)
    {
        if (attached is null || sender is not FrameworkElement { DataContext: TranscriptRowViewModel row } button) return;
        var detail = attached;
        var editable = detail.CanEditTranscript;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(Action($"Переименовать «{row.Source}» во всей записи…", SymbolRegular.Rename24, () => _ = RenameSpeakerAsync(detail, row.Source), editable));
        menu.Items.Add(SpeakerChoice(detail, Chosen(row), editable));
        menu.IsOpen = true;
    }

    /// <summary>«Сменить говорящего»: the speakers of the recording, the current one ticked, and a new name.</summary>
    private MenuItem SpeakerChoice(SessionDetailViewModel detail, IReadOnlyList<TranscriptRowViewModel> rows, bool editable)
    {
        var choice = new MenuItem
        {
            Header = rows.Count == 1 ? "Сменить говорящего" : $"Сменить говорящего ({rows.Count} {Plural(rows.Count, "фраза", "фразы", "фраз")})",
            Icon = new SymbolIcon(SymbolRegular.PersonSwap24), IsEnabled = editable
        };
        var current = rows.Select(r => r.Source).Distinct().ToArray();
        foreach (var name in detail.Speakers)
        {
            var speaker = name;
            // The current speaker carries a tick; a checkable item would draw empty boxes beside the others, as if several
            // could be chosen. The others get an invisible tick, so all names start in one column.
            var option = new MenuItem
            {
                Header = Literal(speaker),
                Icon = new SymbolIcon(SymbolRegular.Checkmark24) { Opacity = current is [var only] && only == speaker ? 1 : 0 }
            };
            option.Click += (_, _) => detail.AssignSpeaker(rows, speaker);
            choice.Items.Add(option);
        }
        choice.Items.Add(new Separator());
        var other = new MenuItem { Header = "Другое имя…", Icon = new SymbolIcon(SymbolRegular.PersonAdd24) };
        other.Click += async (_, _) =>
        {
            var name = await AskNameAsync(rows.Count == 1 ? "Кто это сказал?" : "Кто сказал эти фразы?",
                "Фразы останутся на своей дорожке — их можно слушать, как раньше; в расшифровке и в сохранённом тексте будет это имя.",
                "", "Готово", detail.Speakers);
            if (name is not null) detail.AssignSpeaker(rows, name);
        };
        choice.Items.Add(other);
        return choice;
    }

    private async Task RenameSpeakerAsync(SessionDetailViewModel detail, string name)
    {
        var phrases = detail.Session.Transcript.Count(s => s.Source == name);
        var renamed = await AskNameAsync("Переименовать говорящего",
            $"«{name}» сменится на новое имя во всех его фразах ({phrases}) и у дорожки этой записи — новая расшифровка тоже возьмёт новое имя. Подпись источника на пульте записи не изменится.",
            name, "Переименовать", detail.Speakers.Where(s => s != name).ToArray());
        if (renamed is not null) detail.RenameSpeaker(name, renamed);
    }

    /// <summary>Asks for a speaker's name; says so when the name joins a speaker who is already there.</summary>
    private async Task<string?> AskNameAsync(string title, string message, string initial, string primary, IReadOnlyCollection<string> existing)
    {
        var box = new Wpf.Ui.Controls.TextBox { Text = initial, MaxLength = TranscriptEditing.MaxName, PlaceholderText = "Имя говорящего" };
        System.Windows.Automation.AutomationProperties.SetName(box, "Имя говорящего");
        var join = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        join.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        box.TextChanged += (_, _) =>
        {
            var name = TranscriptEditing.CleanName(box.Text);
            join.Visibility = existing.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
            join.Text = $"«{name}» уже есть в этой записи — фразы окажутся под одним именем.";
        };
        var panel = new StackPanel { MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(box);
        panel.Children.Add(join);
        var dialog = new ContentDialog
        {
            Title = title, Content = panel, PrimaryButtonText = primary, CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary, DialogMaxWidth = 560
        };
        box.Loaded += (_, _) => Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; dialog.Hide(ContentDialogResult.Primary); } };
        if (await dialogs.ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary) return null;
        var answer = TranscriptEditing.CleanName(box.Text);
        return answer.Length == 0 ? null : answer;
    }

    private async void ConfirmRetranscribe()
    {
        if (attached is null) return;
        var detail = attached;
        var answer = await dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Расшифровать заново?",
            Content = "Текст распознается заново, и правки пропадут: исправленные и удалённые фразы, смена говорящего у фраз. " +
                "Переименованные говорящие останутся под новыми именами.",
            PrimaryButtonText = "Расшифровать заново",
            CloseButtonText = "Отмена"
        });
        if (answer == ContentDialogResult.Primary) detail.TranscribeConfirmed();
    }

    /// <summary>A menu item that runs an action; an underscore in a name is a letter, not a shortcut.</summary>
    private static MenuItem Action(string title, SymbolRegular icon, System.Action run, bool enabled, string? gesture = null)
    {
        var item = new MenuItem { Header = Literal(title), Icon = new SymbolIcon(icon), IsEnabled = enabled, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => run();
        return item;
    }

    private static string Literal(string text) => text.Replace("_", "__");

    private static string Plural(int n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };

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
