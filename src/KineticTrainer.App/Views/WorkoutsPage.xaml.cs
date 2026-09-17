using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KineticTrainer.App.Views;

public sealed partial class WorkoutsPage : Page
{
    public WorkoutsPage()
    {
        InitializeComponent();
        ViewModel.EditRequested += entry => Frame.Navigate(typeof(WorkoutEditorPage), entry);
    }

    public WorkoutsViewModel ViewModel => App.Services.WorkoutsVm;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.EnsureLoadedAsync();
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
            Content = "Rides you already did with this workout keep their copy of it.",
            PrimaryButtonText = "Delete",
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
