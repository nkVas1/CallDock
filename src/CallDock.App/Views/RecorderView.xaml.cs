using CallDock.App.ViewModels;
using CallDock.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

namespace CallDock.App.Views;

public partial class RecorderView : UserControl
{
    private readonly RecorderViewModel vm;
    private readonly IContentDialogService dialogs;

    public RecorderView(RecorderViewModel vm, IContentDialogService dialogs)
    {
        this.vm = vm;
        this.dialogs = dialogs;
        DataContext = vm;
        InitializeComponent();
    }

    /// <summary>The «Добавить источник» menu, rebuilt on every open so new devices and windows show up.</summary>
    private async void OpenAddMenu(object sender, RoutedEventArgs e)
    {
        AddSourceButton.IsEnabled = false;
        try { await vm.RefreshSourcesCommand.ExecuteAsync(null); }
        finally { AddSourceButton.IsEnabled = vm.CanEditSources; }

        var menu = new ContextMenu { PlacementTarget = AddSourceButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Group("Микрофон", SymbolRegular.Mic24, vm.Microphones, "Микрофонов не найдено"));
        menu.Items.Add(Group("Системный звук", SymbolRegular.Speaker224, vm.Outputs, "Устройств вывода не найдено"));
        var apps = Group("Звук приложения", SymbolRegular.AppGeneric24, vm.Applications, "Нет окон приложений");
        if (!vm.ApplicationAudioSupported) { apps.IsEnabled = false; apps.ToolTip = vm.ApplicationAudioHint; ToolTipService.SetShowOnDisabled(apps, true); }
        menu.Items.Add(apps);
        menu.Items.Add(Group("Экран", SymbolRegular.Desktop24, vm.Screens, "Экранов не найдено"));
        menu.Items.Add(Group("Окно", SymbolRegular.Window24, vm.Windows, "Открытых окон не найдено"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Поток по ссылке…", SymbolRegular.Live24, async () => await AddStreamAsync()));
        menu.Items.Add(Item("Вкладки Chrome — из расширения", SymbolRegular.Tab24, () =>
            vm.Notify("Вкладки Chrome", "Откройте вкладку с трансляцией и нажмите «Записывать» в расширении CallDock. Как подключить — в настройках, раздел «Chrome».")));
        menu.IsOpen = true;
    }

    private MenuItem Group(string title, SymbolRegular icon, IReadOnlyList<SourceSpec> sources, string empty)
    {
        var group = new MenuItem { Header = title, Icon = new SymbolIcon(icon) };
        if (sources.Count == 0) group.Items.Add(new MenuItem { Header = empty, IsEnabled = false });
        foreach (var source in sources)
        {
            var taken = vm.Sources.Any(s => s.Spec.Key == source.Key);
            group.Items.Add(new MenuItem
            {
                Header = source.Name.Length > 90 ? source.Name[..90] + "…" : source.Name,
                IsEnabled = !taken,
                ToolTip = taken ? "Уже добавлен" : source.Name,
                Command = vm.AddSourceCommand,
                CommandParameter = source
            });
        }
        return group;
    }

    private static MenuItem Item(string title, SymbolRegular icon, Action click)
    {
        var item = new MenuItem { Header = title, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => click();
        return item;
    }

    private async Task AddStreamAsync()
    {
        var name = new Wpf.Ui.Controls.TextBox { Text = "Трансляция", PlaceholderText = "Например: Главная сцена", MaxLength = 80, Margin = new Thickness(0, 6, 0, 14) };
        var url = new Wpf.Ui.Controls.TextBox { PlaceholderText = "https://…/index.m3u8", MaxLength = 8192, Margin = new Thickness(0, 6, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Прямая ссылка на медиапоток (HLS .m3u8, DASH .mpd или видеофайл), а не на страницу с плеером. Поток сохраняется как есть, без перекодирования. Для обычной страницы используйте вкладку Chrome.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), Opacity = 0.8
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Подпись" });
        panel.Children.Add(name);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Ссылка на поток" });
        panel.Children.Add(url);
        var dialog = new ContentDialog
        {
            Title = "Поток по ссылке", Content = panel, PrimaryButtonText = "Добавить", CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary, DialogMaxWidth = 560
        };
        if (await dialogs.ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary) return;
        try
        {
            var link = MediaTools.ValidateStreamUrl(url.Text);
            var label = string.IsNullOrWhiteSpace(name.Text) ? "Трансляция" : name.Text.Trim();
            await vm.AddSourceAsync(new SourceSpec(SourceKind.Stream, new Uri(link).Host, link, label));
        }
        catch (ArgumentException ex) { vm.Notify("Ссылка не подходит", ex.Message); }
    }

    private void LabelLostFocus(object sender, RoutedEventArgs e) => vm.SaveSources();

    private void LabelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private async void BookmarkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await vm.AddBookmarkCommand.ExecuteAsync(null);
    }
}
