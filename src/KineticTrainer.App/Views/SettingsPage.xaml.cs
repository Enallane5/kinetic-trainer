using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace KineticTrainer.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsViewModel ViewModel => App.Services.SettingsVm;
}
