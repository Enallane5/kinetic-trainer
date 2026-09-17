using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace KineticTrainer.App.Views;

public sealed partial class RidePage : Page
{
    public RidePage()
    {
        InitializeComponent();
        LiveChart.XLabel = x => x >= -0.5 ? "now" : $"−{Format.Duration(-x)}";
        ViewModel.LiveGraphUpdated += (points, ftp) =>
        {
            LiveChart.XRange = (-ViewModel.GraphMinutes * 60, 0);
            LiveChart.Reference = ftp;
            LiveChart.SetSeries(points);
        };
        ViewModel.WorkoutPowerUpdated += power => ProfileChart.SetPower(power);
    }

    public RideViewModel ViewModel => App.Services.Ride;
}
