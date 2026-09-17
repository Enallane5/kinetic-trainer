using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KineticTrainer.App.Views;

public sealed partial class SensorPage : Page
{
    public SensorPage()
    {
        InitializeComponent();
    }

    public SensorViewModel ViewModel => App.Services.Sensor;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCalibrationsAsync();
    }
}
