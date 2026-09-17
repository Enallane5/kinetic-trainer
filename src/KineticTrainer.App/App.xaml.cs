using System.Runtime.InteropServices;
using KineticTrainer.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace KineticTrainer.App;

public partial class App : Application
{
    private Window? _window;
    private bool _started;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Services?.Log.Error(DiagnosticCategory.App, $"Fatal unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services?.Log.Error(DiagnosticCategory.App, $"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>Composition root; set once at launch.</summary>
    public static AppServices Services { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = await AppServices.CreateAsync(DispatcherQueue.GetForCurrentThread());
            _window = new MainWindow();
            _window.Activate();
            _started = true;
            await Services.StartAsync();
        }
        catch (Exception ex)
        {
            FailStartup(ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (!_started)
        {
            e.Handled = true;
            FailStartup(e.Exception);
            return;
        }
        // Once the ride is running, keep it alive: log and swallow UI exceptions instead of crashing.
        Services?.Log.Error(DiagnosticCategory.App, $"Unhandled UI exception: {e.Exception}");
        e.Handled = true;
    }

    /// <summary>A start-up failure must be visible and must not leave a windowless process behind.</summary>
    private static void FailStartup(Exception ex)
    {
        try
        {
            Services?.Log.Error(DiagnosticCategory.App, $"Start-up failed: {ex}");
            var logHint = Services?.LogFilePath is { } f ? $"\n\nLog: {f}" : string.Empty;
            _ = MessageBoxW(IntPtr.Zero, $"Kinetic Trainer could not start.\n\n{ex.GetType().Name}: {ex.Message}{logHint}", "Kinetic Trainer", 0x10 /* MB_ICONERROR */);
        }
        finally
        {
            Environment.Exit(1);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
