using KineticTrainer.App.Services;
using KineticTrainer.App.ViewModels;
using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Bluetooth.Pipeline;
using KineticTrainer.Bluetooth.Session;
using KineticTrainer.Bluetooth.Simulation;
using KineticTrainer.Bluetooth.WinRt;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Profiles;
using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Core.Zones;
using KineticTrainer.Persistence;
using KineticTrainer.Recording;
using KineticTrainer.SimulatorBridge;
using Microsoft.UI.Dispatching;

namespace KineticTrainer.App;

/// <summary>Wires the layers together. Everything the pages need hangs off this object.</summary>
public sealed class AppServices
{
    /// <summary>Targets need an FTP; without one a conservative guess is used and the rider is told.</summary>
    public const int FallbackFtp = 150;

    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private DiagnosticFileWriter? _fileWriter;

    private AppServices(DispatcherQueue dispatcher, DiagnosticLog log, LocalDatabase? db, ISettingsStore? settingsStore, AppSettings settings, RiderProfile profile)
    {
        Dispatcher = dispatcher;
        Log = log;
        Database = db;
        SettingsStore = settingsStore;
        Settings = settings;
        ActiveProfile = profile;

        Telemetry = new TelemetryHub();
        Pipeline = new TrainerTelemetryPipeline(Telemetry, log)
        {
            RollerCircumferenceMeters = settings.RollerCircumferenceMeters,
            IgnoreSystemIdValidation = settings.IgnoreSystemIdValidation,
        };
        WinRt = new WinRtSensorPlatform(log);
        Simulated = new SimulatedSensorPlatform(log);
        Simulated.Profile.Protocol = TrainerProtocolKind.FitnessMachine; // matches the physical Kinetic inRide (FW 1.2)
        Session = new SensorSession([Simulated, WinRt], Pipeline, Telemetry, log)
        {
            AutoReconnect = settings.AutoReconnect,
            PreferredUpdateRate = settings.PreferredUpdateRate,
        };
        Session.SensorConnected += OnSensorConnected;
        Bridge = new SimulatorBridgeServer(Telemetry, log);
        Recorder = new RideRecorder(Telemetry);
        Recorder.Fault += m => log.Warn(DiagnosticCategory.App, m);
        Cues = new CuePlayer(log) { TonesEnabled = settings.AudioCuesEnabled, SpeechEnabled = settings.SpokenCuesEnabled };
        if (db is not null)
        {
            Rides = new RideRepository(db);
            Profiles = new ProfileRepository(db);
            FtpHistory = new FtpHistoryRepository(db);
            Workouts = new WorkoutRepository(db);
        }

        Status = new StatusViewModel(this);
        Ride = new RideViewModel(this);
        Sensor = new SensorViewModel(this);
        Debug = new DebugViewModel(this);
        SettingsVm = new SettingsViewModel(this);
        History = new HistoryViewModel(this);
        WorkoutsVm = new WorkoutsViewModel(this);
        ProfileVm = new ProfileViewModel(this);
        Trends = new TrendsViewModel(this);
    }

    /// <summary>Ride history store; null when the local database could not be opened.</summary>
    public IRideRepository? Rides { get; }

    public IProfileRepository? Profiles { get; }

    public IFtpHistoryRepository? FtpHistory { get; }

    public IWorkoutRepository? Workouts { get; }

    /// <summary>The rider whose rides, FTP and workouts are shown. Id 0 when the database is unavailable.</summary>
    public RiderProfile ActiveProfile { get; private set; }

    /// <summary>FTP used for targets: the rider's, or <see cref="FallbackFtp"/> (see <see cref="HasFtp"/>).</summary>
    public int EffectiveFtp => ActiveProfile.Ftp ?? FallbackFtp;

    public bool HasFtp => PowerZones.IsValidFtp(ActiveProfile.Ftp);

    public DispatcherQueue Dispatcher { get; }

    public DiagnosticLog Log { get; }

    public LocalDatabase? Database { get; }

    public ISettingsStore? SettingsStore { get; }

    public AppSettings Settings { get; private set; }

    public TelemetryHub Telemetry { get; }

    public TrainerTelemetryPipeline Pipeline { get; }

    public WinRtSensorPlatform WinRt { get; }

    public SimulatedSensorPlatform Simulated { get; }

    public SensorSession Session { get; }

    public SimulatorBridgeServer Bridge { get; }

    public RideRecorder Recorder { get; }

    public CuePlayer Cues { get; }

    public string ExportFolder => string.IsNullOrWhiteSpace(Settings.ExportFolder) ? RideExporter.DefaultFolder : Settings.ExportFolder;

    public StatusViewModel Status { get; }

    public RideViewModel Ride { get; }

    public SensorViewModel Sensor { get; }

    public DebugViewModel Debug { get; }

    public SettingsViewModel SettingsVm { get; }

    public HistoryViewModel History { get; }

    public WorkoutsViewModel WorkoutsVm { get; }

    public ProfileViewModel ProfileVm { get; }

    public TrendsViewModel Trends { get; }

    public string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KineticTrainer", "logs");

    public string? LogFilePath => _fileWriter?.CurrentFile;

    public event Action<AppSettings>? SettingsChanged;

    /// <summary>Raised on the UI thread when the active rider changes or their profile (FTP, weight...) is edited.</summary>
    public event Action<RiderProfile>? ActiveProfileChanged;

    /// <summary>Raised after a ride is saved to history (UI thread).</summary>
    public event Action<long>? RideSaved;

    /// <summary>Asks the main window to show a page ("ride", "workouts", ...), optionally with a parameter.</summary>
    public event Action<string, object?>? NavigationRequested;

    public void Navigate(string page, object? parameter = null) => NavigationRequested?.Invoke(page, parameter);

    public void NotifyRideSaved(long id) => RideSaved?.Invoke(id);

    public static async Task<AppServices> CreateAsync(DispatcherQueue dispatcher)
    {
        var log = new DiagnosticLog();
        log.Info(DiagnosticCategory.App, $"Kinetic Trainer starting (v{typeof(AppServices).Assembly.GetName().Version}) on {Environment.OSVersion}, " +
                                         $"exe {Environment.ProcessPath}");

        // Start the file log before anything that can fail, so a start-up problem is always captured on disk.
        DiagnosticFileWriter? fileWriter = null;
        try
        {
            fileWriter = new DiagnosticFileWriter(log, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KineticTrainer", "logs"));
            fileWriter.Start();
        }
        catch (Exception ex)
        {
            log.Warn(DiagnosticCategory.App, $"File logging unavailable: {ex.Message}");
        }

        LocalDatabase? db = null;
        ISettingsStore? store = null;
        var settings = new AppSettings();
        var profile = new RiderProfile { Name = "Rider" };
        try
        {
            db = new LocalDatabase(DatabasePathFromArgs() ?? LocalDatabase.DefaultPath);
            // A previous instance that is still closing can briefly hold the file; retry before giving up.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await db.InitializeAsync();
                    break;
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (attempt < 8)
                {
                    log.Warn(DiagnosticCategory.Settings, $"Database busy (attempt {attempt}): {ex.Message}");
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    await Task.Delay(500 * attempt);
                }
            }
            store = new SettingsStore(db);
            settings = await store.LoadAsync();
            log.Info(DiagnosticCategory.Settings, $"Settings loaded from {db.Path} (schema v{await db.GetSchemaVersionAsync()})");
            profile = await new ProfileRepository(db).EnsureDefaultAsync(settings.ActiveProfileId);
            if (settings.ActiveProfileId != profile.Id)
            {
                settings = settings with { ActiveProfileId = profile.Id };
                await store.SaveAsync(settings);
            }
            if (ArgValue("--set-ftp") is { } ftpArg && int.TryParse(ftpArg, out var testFtp) && DatabasePathFromArgs() is not null)
            {
                // Test aid (only with --data, never on the real database).
                var repo = new ProfileRepository(db);
                await repo.UpdateAsync(profile with { Ftp = testFtp });
                profile = (await repo.GetAsync(profile.Id))!;
            }
            log.Info(DiagnosticCategory.Settings, $"Active rider: {profile.Name} (FTP {profile.Ftp?.ToString() ?? "not set"})");
        }
        catch (Exception ex)
        {
            log.Error(DiagnosticCategory.Settings, $"Local database unavailable, using defaults for this session: {ex.Message}");
            db = null;
            store = null;
        }

        return new AppServices(dispatcher, log, db, store, settings, profile) { _fileWriter = fileWriter };
    }

    private static string? ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Test aid: "--data &lt;folder&gt;" keeps a test session's rides out of the real history.</summary>
    private static string? DatabasePathFromArgs() =>
        ArgValue("--data") is { } dir ? Path.Combine(Path.GetFullPath(dir), "kinetic-trainer.db") : null;

    /// <summary>Test aid: "--workout &lt;name&gt; [--start-workout]" loads (and starts) a built-in workout.</summary>
    private void LoadWorkoutFromArgs()
    {
        if (ArgValue("--workout") is not { } name)
        {
            return;
        }
        var entry = KineticTrainer.Workouts.Library.WorkoutLibrary.BuiltIn.FirstOrDefault(e =>
            e.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || e.Key.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Log.Warn(DiagnosticCategory.App, $"--workout: no built-in workout matches '{name}'");
            return;
        }
        Dispatcher.TryEnqueue(() =>
        {
            Ride.LoadWorkout(entry);
            if (Environment.GetCommandLineArgs().Contains("--start-workout", StringComparer.OrdinalIgnoreCase))
            {
                Ride.StartWorkoutCommand.Execute(null);
            }
        });
    }

    public async Task StartAsync()
    {
        if (Settings.WriteDiagnosticLogFile)
        {
            StartFileLog();
        }
        else if (_fileWriter is not null)
        {
            await _fileWriter.DisposeAsync();
            _fileWriter = null;
        }
        if (Settings.SimulatorBridgeEnabled)
        {
            Bridge.Start(Settings.SimulatorBridgePort);
        }
        Sensor.Start();
        Debug.Start();
        Status.Start();
        Ride.Start();
        LoadWorkoutFromArgs();

        // Test aid: "KineticTrainer.exe --connect C7:98:57:0F:8B:81" connects to that sensor without any clicks.
        var args = Environment.GetCommandLineArgs();
        var connectIndex = Array.FindIndex(args, a => a.Equals("--connect", StringComparison.OrdinalIgnoreCase));
        if (connectIndex >= 0 && connectIndex + 1 < args.Length &&
            ulong.TryParse(args[connectIndex + 1].Replace(":", "").Replace("-", ""), System.Globalization.NumberStyles.HexNumber, null, out var connectAddress))
        {
            Log.Info(DiagnosticCategory.App, $"--connect: connecting to {DiscoveredSensor.FormatAddress(connectAddress)}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Session.ConnectAsync(connectAddress, SensorAddressType.Unspecified, null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Sensor asleep or out of range: keep looking for it like a remembered sensor instead of giving up.
                    Log.Warn(DiagnosticCategory.App, $"--connect failed ({ex.Message}); searching until it appears");
                    await Session.ReconnectToSavedAsync(new SavedSensor(connectAddress, null, null, DateTimeOffset.UtcNow));
                }
            });
            return;
        }

        // Development aid: "KineticTrainer.exe --simulate" connects to the built-in simulated sensor immediately.
        if (args.Contains("--simulate", StringComparer.OrdinalIgnoreCase))
        {
            Log.Info(DiagnosticCategory.App, "--simulate: connecting to the simulated sensor");
            if (!Settings.ShowSimulatedSensor)
            {
                await UpdateSettingsAsync(Settings with { ShowSimulatedSensor = true });
            }
            _ = Session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, "inRide SIM", CancellationToken.None);
            return;
        }

        if (Settings.AutoReconnect && Settings.LastSensor is { } saved)
        {
            await Session.ReconnectToSavedAsync(saved);
        }
    }

    /// <summary>Makes another rider active (UI thread).</summary>
    public async Task SwitchProfileAsync(long profileId)
    {
        if (Profiles is null || profileId == ActiveProfile.Id)
        {
            return;
        }
        var profile = await Profiles.GetAsync(profileId) ?? throw new InvalidOperationException("That rider no longer exists.");
        await Profiles.TouchAsync(profileId);
        ActiveProfile = profile;
        await UpdateSettingsAsync(Settings with { ActiveProfileId = profileId });
        Log.Info(DiagnosticCategory.App, $"Switched rider to {profile.Name}");
        ActiveProfileChanged?.Invoke(profile);
    }

    /// <summary>Saves a profile edit; if it is the active rider, everyone is told (UI thread).</summary>
    public async Task<RiderProfile> SaveProfileAsync(RiderProfile profile, string ftpSource = "manual", string? ftpNote = null)
    {
        if (Profiles is null)
        {
            throw new InvalidOperationException("Profiles are unavailable because the local database could not be opened.");
        }
        RiderProfile saved;
        if (profile.Id == 0)
        {
            saved = await Profiles.CreateAsync(profile);
        }
        else
        {
            await Profiles.UpdateAsync(profile, ftpSource, ftpNote);
            saved = await Profiles.GetAsync(profile.Id) ?? profile;
        }
        if (saved.Id == ActiveProfile.Id)
        {
            ActiveProfile = saved;
            ActiveProfileChanged?.Invoke(saved);
        }
        return saved;
    }

    /// <summary>Deletes a rider and all their data. The active rider cannot be deleted.</summary>
    public async Task DeleteProfileAsync(long profileId)
    {
        if (Profiles is null)
        {
            return;
        }
        if (profileId == ActiveProfile.Id)
        {
            throw new InvalidOperationException("Switch to another rider before deleting this one.");
        }
        await Profiles.DeleteAsync(profileId);
        Log.Info(DiagnosticCategory.App, $"Rider {profileId} deleted");
    }

    private void StartFileLog()
    {
        if (_fileWriter is not null)
        {
            return;
        }
        _fileWriter = new DiagnosticFileWriter(Log, LogDirectory);
        _fileWriter.Start();
    }

    public async Task UpdateSettingsAsync(AppSettings updated)
    {
        await _settingsGate.WaitAsync();
        try
        {
            var previous = Settings;
            Settings = updated;

            Pipeline.RollerCircumferenceMeters = updated.RollerCircumferenceMeters;
            Pipeline.IgnoreSystemIdValidation = updated.IgnoreSystemIdValidation;
            Session.AutoReconnect = updated.AutoReconnect;
            Session.PreferredUpdateRate = updated.PreferredUpdateRate;
            Cues.TonesEnabled = updated.AudioCuesEnabled;
            Cues.SpeechEnabled = updated.SpokenCuesEnabled;

            if (updated.SimulatorBridgeEnabled)
            {
                Bridge.Start(updated.SimulatorBridgePort);
            }
            else if (Bridge.IsRunning)
            {
                Bridge.Stop();
            }

            if (updated.WriteDiagnosticLogFile && _fileWriter is null)
            {
                StartFileLog();
            }
            else if (!updated.WriteDiagnosticLogFile && _fileWriter is not null)
            {
                await _fileWriter.DisposeAsync();
                _fileWriter = null;
            }

            if (SettingsStore is not null)
            {
                try
                {
                    await SettingsStore.SaveAsync(updated);
                    if (!ReferenceEquals(previous, updated))
                    {
                        Log.Info(DiagnosticCategory.Settings, "Settings saved");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(DiagnosticCategory.Settings, $"Saving settings failed: {ex.Message}");
                }
            }
            SettingsChanged?.Invoke(updated);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private void OnSensorConnected(SavedSensor saved)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (saved.BluetoothAddress == SimulatedSensorPlatform.SimulatedAddress)
                {
                    return; // never remember the simulator as the real trainer
                }
                await UpdateSettingsAsync(Settings with { LastSensor = saved });
                Log.Info(DiagnosticCategory.Settings, $"Remembered sensor {saved.Name} ({DiscoveredSensor.FormatAddress(saved.BluetoothAddress)})");
            }
            catch (Exception ex)
            {
                Log.Error(DiagnosticCategory.Settings, $"Could not remember sensor: {ex.Message}");
            }
        });
    }

    public async Task ShutdownAsync()
    {
        Log.Info(DiagnosticCategory.App, "Shutting down");
        try { await Ride.SaveUnfinishedRideAsync(); } catch (Exception ex) { Log.Error(DiagnosticCategory.App, $"Saving the ride on exit failed: {ex.Message}"); }
        try { await Session.DisposeAsync(); } catch { /* ignored */ }
        try { await Bridge.DisposeAsync(); } catch { /* ignored */ }
        Sensor.Stop();
        Cues.Dispose();
        try
        {
            // Close pooled connections so SQLite checkpoints the WAL and releases the files before the process ends.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch
        {
            // ignored
        }
        Log.Info(DiagnosticCategory.App, "Shutdown complete");
        if (_fileWriter is not null)
        {
            await _fileWriter.DisposeAsync();
        }
    }
}
