using KineticTrainer.App.Services;
using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace KineticTrainer.App.Views;

public sealed partial class RideDetailPage : Page
{
    public RideDetailPage()
    {
        InitializeComponent();
        ViewModel = new RideDetailViewModel(App.Services);
        ViewModel.SeriesLoaded += () =>
        {
            PowerChart.Reference = ViewModel.Ftp;
            PowerChart.SetSeries(ViewModel.PowerSeries);
            CadenceChart.SetSeries(ViewModel.CadenceSeries);
            SpeedChart.Unit = ViewModel.SpeedUnit;
            SpeedChart.SetSeries(ViewModel.SpeedSeries);
            WorkoutChart.SetPower(ViewModel.PowerBySecond);
        };
        ViewModel.Deleted += GoBack;
        CadenceChart.Stroke = new SolidColorBrush(KineticColors.Cadence);
        SpeedChart.Stroke = new SolidColorBrush(KineticColors.Info);
    }

    public RideDetailViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is long id)
        {
            await ViewModel.LoadAsync(id);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(HistoryPage));
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete this ride from history?",
            Content = $"{ViewModel.DeleteDescription}. The exported FIT/TCX files are not deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteAsync();
        }
    }
}
