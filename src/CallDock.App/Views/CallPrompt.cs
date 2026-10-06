using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CallDock.App.Views;

/// <summary>
/// A small note in the corner of the screen, above the taskbar: «Telegram использует микрофон — записать звонок?» with
/// two buttons. It never takes the focus from the call, stays on top, and goes away by itself — answering for the person
/// when <c>acceptOnTimeout</c> says what silence means.
/// </summary>
public sealed class CallPrompt : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock countdown = new() { FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    private readonly string countdownText;
    private int left;
    private bool answered;

    /// <summary>The person agreed, or the time ran out where silence means yes.</summary>
    public event Action? Accepted;
    /// <summary>The person declined, closed the note, or the time ran out where silence means no.</summary>
    public event Action? Declined;

    public CallPrompt(string title, string message, string accept, string decline, TimeSpan timeout, bool acceptOnTimeout, string countdownText)
    {
        this.countdownText = countdownText;
        left = (int)timeout.TotalSeconds;
        Title = "CallDock";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo-64.png")), Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var body = new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        countdown.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        countdown.Visibility = string.IsNullOrEmpty(countdownText) ? Visibility.Collapsed : Visibility.Visible;

        var yes = new Button { Content = accept, Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        var no = new Button { Content = decline, HorizontalAlignment = HorizontalAlignment.Stretch };
        yes.Click += (_, _) => Answer(true);
        no.Click += (_, _) => Answer(false);
        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(no, 1);
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        var text = new StackPanel();
        text.Children.Add(heading);
        text.Children.Add(body);
        text.Children.Add(countdown);
        text.Children.Add(buttons);
        var row = new DockPanel { Margin = new Thickness(16, 14, 16, 16) };
        DockPanel.SetDock(logo, Dock.Left);
        row.Children.Add(logo);
        row.Children.Add(text);
        Content = row;

        timer.Tick += (_, _) =>
        {
            if (--left <= 0) { Answer(acceptOnTimeout); return; }
            UpdateCountdown();
        };
        UpdateCountdown();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
            timer.Start();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            if (!answered) { answered = true; Declined?.Invoke(); }
        };
    }

    private void UpdateCountdown() => countdown.Text = string.Format(countdownText, left);

    private void Answer(bool yes)
    {
        if (answered) return;
        answered = true;
        timer.Stop();
        Close();
        if (yes) Accepted?.Invoke(); else Declined?.Invoke();
    }

    /// <summary>Goes away without an answer: the situation it asked about has changed.</summary>
    public void Withdraw()
    {
        answered = true;
        timer.Stop();
        Close();
    }
}
