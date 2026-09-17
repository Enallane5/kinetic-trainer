using KineticTrainer.App.ViewModels;
using KineticTrainer.Workouts.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KineticTrainer.App.Views;

public sealed partial class WorkoutEditorPage : Page
{
    public WorkoutEditorPage()
    {
        ViewModel = new WorkoutEditorViewModel(App.Services);
        InitializeComponent();
        ViewModel.Closed += _ =>
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                Frame.Navigate(typeof(WorkoutsPage));
            }
        };
    }

    public WorkoutEditorViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load(e.Parameter as WorkoutEntry);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => ViewModel.MoveUpCommand.Execute((sender as FrameworkElement)?.Tag as StepRow);

    private void OnMoveDown(object sender, RoutedEventArgs e) => ViewModel.MoveDownCommand.Execute((sender as FrameworkElement)?.Tag as StepRow);

    private void OnRemove(object sender, RoutedEventArgs e) => ViewModel.RemoveStepCommand.Execute((sender as FrameworkElement)?.Tag as StepRow);
}
