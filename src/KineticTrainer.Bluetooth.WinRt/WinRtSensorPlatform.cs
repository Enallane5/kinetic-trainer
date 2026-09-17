using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Bluetooth.Simulation;
using KineticTrainer.Core.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace KineticTrainer.Bluetooth.WinRt;

public sealed class WinRtSensorPlatform : ISensorPlatform
{
    private readonly IDiagnosticLog _log;

    public WinRtSensorPlatform(IDiagnosticLog log)
    {
        _log = log;
        Connector = new WinRtSensorConnector(log);
    }

    public string Name => "Windows Bluetooth LE";

    public ISensorConnector Connector { get; }

    public ISensorScanner CreateScanner() => new WinRtSensorScanner(_log);

    public bool Owns(ulong address) => address != SimulatedSensorPlatform.SimulatedAddress;

    /// <summary>Null when Bluetooth LE is usable; otherwise a message for the user.</summary>
    public static async Task<string?> CheckAdapterAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                return "No Bluetooth adapter was found on this PC.";
            }
            if (!adapter.IsLowEnergySupported)
            {
                return "The Bluetooth adapter does not support Bluetooth Low Energy.";
            }
            var radio = await adapter.GetRadioAsync();
            if (radio is not null && radio.State != RadioState.On)
            {
                return $"Bluetooth is turned {radio.State.ToString().ToLowerInvariant()}. Turn it on in Windows Settings.";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Bluetooth status could not be determined: {ex.Message}";
        }
    }
}
