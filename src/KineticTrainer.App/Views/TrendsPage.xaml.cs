using KineticTrainer.App.Services;
using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace KineticTrainer.App.Views;

public sealed partial class TrendsPage : Page
{
    public TrendsPage()
    {
        InitializeComponent();
        HoursChart.BarColor = KineticColors.Accent;
        DistanceChart.BarColor = KineticColors.Info;
        TssChart.BarColor = KineticColors.Power;
        FitnessChart.Stroke = new SolidColorBrush(KineticColors.Accent);
        FitnessChart.XLabel = s => s <= 0 ? "start" : $"+{s / 86400:0} d";
        ViewModel.ChartsLoaded += (hours, distance, tss, unit) =>
        {
            HoursChart.SetBars(hours);
            DistanceChart.Title = $"DISTANCE ({unit.ToUpperInvariant()})";
            DistanceChart.SetBars(distance);
            TssChart.SetBars(tss);
        };
        ViewModel.LoadCurveLoaded += (fitness, fatigue) =>
        {
            FitnessChart.SetSeries(fitness);
            FitnessChart.SetSecondSeries(fatigue, new SolidColorBrush(KineticColors.Danger));
        };
    }

    public TrendsViewModel ViewModel => App.Services.Trends;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }
}
