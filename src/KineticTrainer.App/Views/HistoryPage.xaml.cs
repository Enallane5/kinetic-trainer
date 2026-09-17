using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KineticTrainer.App.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        ViewModel.OpenRequested += id => Frame.Navigate(typeof(RideDetailPage), id);
    }

    public HistoryViewModel ViewModel => App.Services.History;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RideRow row)
        {
            ViewModel.OpenCommand.Execute(row);
        }
    }

    private void OnFilesClick(object sender, RoutedEventArgs e) => ViewModel.ShowFilesCommand.Execute((sender as FrameworkElement)?.Tag as RideRow);

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RideRow row)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Delete this ride from history?",
            Content = $"{row.Title}, {row.DateText}: {row.DurationText}, {row.DistanceText}. The exported FIT/TCX files are not deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteCommand.ExecuteAsync(row);
        }
    }
}
