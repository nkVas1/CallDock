using CallDock.App.ViewModels;
using System.Windows.Controls;

namespace CallDock.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
