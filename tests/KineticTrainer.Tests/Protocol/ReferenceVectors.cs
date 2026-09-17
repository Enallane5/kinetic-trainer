using System.Text.Json;
using System.Text.Json.Serialization;

namespace KineticTrainer.Tests.Protocol;

/// <summary>
/// Golden vectors produced by Kinetic's original inRide.c compiled unmodified
/// (tools/reference-vectors/harness.c + generate.py). See TestVectors/inride_reference_vectors.json "source".
/// </summary>
public sealed class ReferenceVectors
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("groups")] public List<Group> Groups { get; set; } = [];

    public sealed class Group
    {
        [JsonPropertyName("group")] public string Kind { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("steps")] public List<Step> Steps { get; set; } = [];
    }

    public sealed class Step
    {
        [JsonPropertyName("op")] public string Op { get; set; } = "";
        [JsonPropertyName("in")] public string? In { get; set; }
        [JsonPropertyName("t")] public double T { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }

        // power
        [JsonPropertyName("state")] public int State { get; set; }
        [JsonPropertyName("commandResult")] public int CommandResult { get; set; }
        [JsonPropertyName("power")] public int Power { get; set; }
        [JsonPropertyName("speedKPH")] public double SpeedKph { get; set; }
        [JsonPropertyName("rollerRPM")] public double RollerRpm { get; set; }
        [JsonPropertyName("cadenceC")] public double CadenceCalibrated { get; set; }
        [JsonPropertyName("cadenceRaw")] public int CadenceRaw { get; set; }
        [JsonPropertyName("cadenceRPM")] public double CadenceRpm { get; set; }
        [JsonPropertyName("coasting")] public bool Coasting { get; set; }
        [JsonPropertyName("spindownTime")] public double SpindownTime { get; set; }
        [JsonPropertyName("lastSpindownResultTime")] public double LastSpindownResultTime { get; set; }
        [JsonPropertyName("rollerResistance")] public double RollerResistance { get; set; }
        [JsonPropertyName("proFlywheel")] public bool ProFlywheel { get; set; }
        [JsonPropertyName("calibrationResult")] public int CalibrationResult { get; set; }

        // config
        [JsonPropertyName("calibrationReady")] public int CalibrationReady { get; set; }
        [JsonPropertyName("calibrationStart")] public int CalibrationStart { get; set; }
        [JsonPropertyName("calibrationEnd")] public int CalibrationEnd { get; set; }
        [JsonPropertyName("calibrationDebounce")] public int CalibrationDebounce { get; set; }
        [JsonPropertyName("currentSpindownTime")] public double CurrentSpindownTime { get; set; }
        [JsonPropertyName("updateRateDefault")] public int UpdateRateDefault { get; set; }
        [JsonPropertyName("updateRateCalibration")] public int UpdateRateCalibration { get; set; }

        // commands
        [JsonPropertyName("systemId")] public string? SystemId { get; set; }
        [JsonPropertyName("arg")] public double Arg { get; set; }
        [JsonPropertyName("out")] public string? Out { get; set; }
    }

    private static readonly Lazy<ReferenceVectors> Cached = new(Load);

    public static ReferenceVectors Instance => Cached.Value;

    private static ReferenceVectors Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "inride_reference_vectors.json");
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ReferenceVectors>(stream) ?? throw new InvalidOperationException("empty vectors");
    }
}
