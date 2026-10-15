using KineticTrainer.App.Services;
using KineticTrainer.App.ViewModels;
using KineticTrainer.App.Views;
using KineticTrainer.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KineticTrainer.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["ride"] = typeof(RidePage),
        ["workouts"] = typeof(WorkoutsPage),
        ["history"] = typeof(HistoryPage),
        ["trends"] = typeof(TrendsPage),
        ["profile"] = typeof(ProfilePage),
        ["sensor"] = typeof(SensorPage),
        ["debug"] = typeof(DebugPage),
        ["settings"] = typeof(SettingsPage),
    };

    private bool _navigatingInCode;
    private bool _shutdownStarted;
    private bool _shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        WindowServices.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        App.Services.NavigationRequested += (page, parameter) => App.Services.Dispatcher.TryEnqueue(() => Show(page, parameter));
        ContentFrame.Navigated += (_, e) => SyncSelection(e.SourcePageType);
        AppWindow.Closing += OnClosing;
    }

    /// <summary>
    /// Holds the window open until shutdown has finished, so a ride in progress is saved and the database is closed
    /// cleanly before the process exits.
    /// </summary>
    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_shutdownDone)
        {
            return;
        }
        args.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }
        _shutdownStarted = true;
        try
        {
            AppWindow.Hide();
            await App.Services.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch
        {
            // Shutdown problems are logged by ShutdownAsync; never block closing.
        }
        _shutdownDone = true;
        Close();
    }

    public StatusViewModel Status => App.Services.Status;

    /// <summary>Opens at up to 1400 x 900, but never larger than the screen's work area (laptops, scaled displays).</summary>
    private void FitToScreen()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            // Test aid: "--size 1100x620" opens at a fixed size to check the layout at small windows.
            var args = Environment.GetCommandLineArgs();
            var sizeIndex = Array.FindIndex(args, a => a.Equals("--size", StringComparison.OrdinalIgnoreCase));
            if (sizeIndex >= 0 && sizeIndex + 1 < args.Length && args[sizeIndex + 1].Split('x') is [var ws, var hs]
                && int.TryParse(ws, out var fixedW) && int.TryParse(hs, out var fixedH))
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + 40, area.Y + 40, fixedW, fixedH));
                return;
            }
            var w = Math.Min(1400, (int)(area.Width * 0.96));
            var h = Math.Min(900, (int)(area.Height * 0.96));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
        }
        catch
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        }
    }

    private void Nav_Loaded(object sender, RoutedEventArgs e)
    {
        // Development aid: "--page workouts|history|trends|profile|sensor|debug|settings|ride" opens on that page.
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        var page = index >= 0 && index + 1 < args.Length ? args[index + 1].ToLowerInvariant() : "ride";
        Show(Pages.ContainsKey(page) ? page : "ride", null);
    }

    private void Show(string page, object? parameter)
    {
        if (page == "ride-detail")
        {
            ContentFrame.Navigate(typeof(RideDetailPage), parameter);
            return;
        }
        if (!Pages.TryGetValue(page, out var type))
        {
            return;
        }
        if (ContentFrame.CurrentSourcePageType != type || parameter is not null)
        {
            ContentFrame.Navigate(type, parameter);
        }
        SyncSelection(type);
    }

    private void SyncSelection(Type pageType)
    {
        var tag = pageType == typeof(RideDetailPage) ? "history"
            : pageType == typeof(WorkoutEditorPage) ? "workouts"
            : Pages.FirstOrDefault(p => p.Value == pageType).Key;
        object? item = tag == "settings"
            ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null && !ReferenceEquals(Nav.SelectedItem, item))
        {
            _navigatingInCode = true;
            Nav.SelectedItem = item;
            _navigatingInCode = false;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navigatingInCode)
        {
            return;
        }
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "ride";
        if (Pages.TryGetValue(tag, out var type) && ContentFrame.CurrentSourcePageType != type)
        {
            ContentFrame.Navigate(type);
        }
    }

    private async void RiderMenu_Opening(object sender, object e)
    {
        RiderMenu.Items.Clear();
        var services = App.Services;
        if (services.Profiles is { } repo)
        {
            try
            {
                foreach (var p in await repo.ListAsync())
                {
                    var item = new ToggleMenuFlyoutItem { Text = p.Ftp is { } f ? $"{p.Name}  ({f} W)" : p.Name, IsChecked = p.Id == services.ActiveProfile.Id, Tag = p.Id };
                    item.Click += async (_, _) =>
                    {
                        try
                        {
                            await services.SwitchProfileAsync((long)item.Tag);
                        }
                        catch (Exception ex)
                        {
                            services.Log.Error(DiagnosticCategory.App, $"Switching rider failed: {ex.Message}");
                        }
                    };
                    RiderMenu.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                RiderMenu.Items.Add(new MenuFlyoutItem { Text = $"Could not list riders: {ex.Message}", IsEnabled = false });
            }
            RiderMenu.Items.Add(new MenuFlyoutSeparator());
        }
        var manage = new MenuFlyoutItem { Text = "Manage riders…" };
        manage.Click += (_, _) => Show("profile", null);
        RiderMenu.Items.Add(manage);
    }
}
