using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KineticTrainer.App.Views;

public sealed partial class ProfilePage : Page
{
    public ProfilePage()
    {
        InitializeComponent();
        FtpChart.XLabel = seconds => seconds <= 0 ? "" : $"+{seconds / 86400:0} days";
        ViewModel.FtpSeriesLoaded += points =>
        {
            FtpChart.XRange = null;
            FtpChart.MinimumMax = 0;
            FtpChart.SetSeries(points);
        };
    }

    public ProfileViewModel ViewModel => App.Services.ProfileVm;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void OnBestClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is long id)
        {
            Frame.Navigate(typeof(RideDetailPage), id);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = $"Delete {item.Name}?",
            Content = "This permanently deletes the rider, all of their rides from history, their FTP history and their saved workouts. " +
                      "Exported FIT/TCX files are kept. This cannot be undone.",
            PrimaryButtonText = "Delete rider",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSelectedAsync();
        }
    }
}
