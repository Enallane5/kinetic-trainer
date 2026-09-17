using KineticTrainer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace KineticTrainer.App.Views;

public sealed partial class DebugPage : Page
{
    public DebugPage()
    {
        InitializeComponent();
    }

    public DebugViewModel ViewModel => App.Services.Debug;
}
