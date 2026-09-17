# Kinetic Trainer

Offline Windows desktop app for a Kurt Kinetic fluid trainer with an **inRide** Bluetooth sensor, built as a
replacement for the discontinued Kinetic Fit app. It has no online accounts, no cloud, no analytics and needs no
internet. The look follows Kinetic Fit's dark theme and green accent (#78C957, taken from the app's own colour
resources).

The physical unit ("inRide 48343", HW 10 / FW 1.2, green inRide 3) speaks the standard Bluetooth **Fitness Machine
Service (FTMS)**. The app also implements Kinetic's custom inRide 2 protocol and picks the right one automatically.
See [docs/PROTOCOL.md](docs/PROTOCOL.md) and [docs/PHASE1-VALIDATION.md](docs/PHASE1-VALIDATION.md).

## What it does

- **Ride**: live power (zone-coloured, % of FTP, 3 s and 10 s averages), cadence, speed, time, distance, work and a
  rolling power graph (1, 5, 10 or 20 minutes). Free rides and structured workouts. Spin-down calibration with
  Kinetic Fit's prompts.
- **Workouts**: 21 built-in workouts (recovery to sprints) including three **FTP tests**: 20-minute (95 %),
  ramp (75 % of best minute) and 8-minute (90 %). Import Zwift `.zwo` files, build your own, export to `.zwo`.
  During a workout it shows interval time left, target, next step, on/under/over target, a zone-coloured profile
  with your power over it, interval pop-ups, and tones plus spoken cues. The voice says "watts" and can be switched
  off with the **Voice** button on the Ride screen or in Settings. Intensity can be nudged ±5 %, and
  segments skipped or paused. Ramp tests end automatically when you can no longer hold the target.
- **Riders**: several local profiles, each with name, FTP, weight, height, birth year, sex, heart rates, records
  (max power, best 5 s … 60 min power, totals) and FTP history. An FTP test result can be saved to the profile.
- **History**: every ride is saved per rider with FIT and TCX files, NP, IF, TSS, time in zones, best efforts,
  workout compliance and charts. Rides can be renamed or deleted.
- **Trends**: weekly/monthly time, distance and training load (TSS), plus fitness, fatigue and form.
- **Unity bridge**: telemetry as newline-delimited JSON on `127.0.0.1` only.

Strava upload is designed for (per-rider linked accounts and an upload table exist in the database) and will be
the last feature, optional and off by default.

## Run it

Run `KineticTrainer-Setup.exe`, or copy the `KineticTrainer-win-x64` folder anywhere and run `KineticTrainer.exe`.
It is self-contained: no .NET or Windows App SDK installation is required. Windows 10 1809+ / Windows 11, 64-bit,
with a Bluetooth LE adapter.

Data lives in `%LOCALAPPDATA%\KineticTrainer\` (database and logs). Ride files go to
`Documents\KineticTrainer\Rides` unless changed in Settings.

Launch flags (testing aids):

| Flag | Effect |
|---|---|
| `--simulate` | Connect to the built-in simulated sensor. |
| `--connect <address>` | Connect to a sensor by Bluetooth address. |
| `--page <name>` | Open on ride, workouts, history, trends, profile, sensor, debug or settings. |
| `--data <folder>` | Use a separate database (keeps test rides out of your history). |
| `--set-ftp <watts>` | With `--data` only: set the test rider's FTP. |
| `--workout <name> [--start-workout]` | Load (and start) a built-in workout. |

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). No Visual Studio needed.

```powershell
dotnet test tests\KineticTrainer.Tests\KineticTrainer.Tests.csproj
.\build.ps1
.\publish.ps1
```

`publish.ps1` runs the tests, then writes `dist\KineticTrainer-win-x64\`, a zip, and `dist\KineticTrainer-Setup.exe`
when Inno Setup 6 is installed.

## Solution layout

| Project | Purpose |
|---|---|
| `KineticTrainer.Core` | Telemetry model and hub, diagnostics, settings, units, statistics (NP, IF, TSS, power curve, training load), power zones, rider profiles. No platform code. |
| `KineticTrainer.Protocol` | FTMS parsing and control point; C# port of Kinetic's inRide 2 code. |
| `KineticTrainer.Bluetooth` | Connection state machine, bytes-to-telemetry pipeline, spin-down calibration, simulated sensor. |
| `KineticTrainer.Bluetooth.WinRt` | Windows Bluetooth LE implementation. |
| `KineticTrainer.Recording` | Ride recorder, FIT and TCX export. |
| `KineticTrainer.Persistence` | SQLite with versioned migrations: settings, riders, rides, FTP history, workouts, calibrations, linked accounts. |
| `KineticTrainer.Workouts` | Workout model, ZWO import/export, timeline, planned IF/TSS, FTP test estimates, workout player and cue speech. |
| `KineticTrainer.SimulatorBridge` | Loopback-only TCP telemetry server. |
| `KineticTrainer.App` | WinUI 3 app: Ride, Workouts, History, Trends, Profile, Sensor, Debug, Settings. |
| `KineticTrainer.Tests` | xUnit tests. |

## Simulator bridge wire format

TCP `127.0.0.1:47474` (port configurable, loopback only). First line `{"type":"hello",...}`, then one line per
sensor packet or connection change:

```json
{"type":"telemetry","seq":42,"timestamp":1789500000000,"connection":"connected","valid":true,"power":210,"cadence":88.0,"speedMps":8.42,"rollerRpm":2978.2,"coasting":false,"simulated":false}
```

`valid:false` lines carry no sensor values. `timestamp` is Unix milliseconds; `speedMps` is roller circumference ×
roller rpm.
