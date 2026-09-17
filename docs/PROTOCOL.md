# Trainer protocols — as implemented

## What the physical sensor actually speaks (validated 2026-09-15)

The unit on hand — advertised name `inRide 48343`, random static address `C7:98:57:0F:8B:81`, Device Information
Manufacturer **Kinetic**, Model **inRide**, Hardware **10**, Firmware **1.2** — does **not** expose Kinetic's custom
`E9410100` service or a System ID characteristic. It exposes the standard Bluetooth SIG **Fitness Machine Service
(0x1826)** plus Battery (0x180F), Device Information (0x180A) and a Nordic DFU service. Full dump: `tools/BleProbe`.

| Characteristic | UUID | Properties | Observed |
|---|---|---|---|
| Fitness Machine Feature | 0x2ACC | Read | `0240000000800000` → features 0x00004002 = Cadence + Power Measurement; targets 0x00008000 = **Spin Down Control** |
| Indoor Bike Data | 0x2AD2 | Notify | 8 bytes at ~2 Hz (two notifications a few ms apart, once a second): `4400 ssss cccc pppp` → flags 0x0044 = Instantaneous Speed (uint16, 0.01 km/h) + Instantaneous Cadence (uint16, 0.5 rpm) + Instantaneous Power (sint16, W) |
| Fitness Machine Status | 0x2ADA | Notify | spin-down progress (op 0x14: 1 requested, 2 success, 3 error, 4 stop pedalling) |
| Fitness Machine Control Point | 0x2AD9 | Write + Indicate | `00` Request Control → `80 00 01` Success; `07` Start/Resume → `80 07 02` Op Code Not Supported |

The app therefore implements **both** protocols and picks per connection: Kinetic custom if `E9410100` is present,
otherwise FTMS (`KineticTrainer.Protocol/Ftms`, `FtmsInterpreter`). FTMS reports bike speed directly, so the roller
circumference setting only affects the inRide 2 path. Spin-down (Phase 7) uses FTMS op 0x13 on this unit.

FTMS Indoor Bike Data wire order (flags uint16 LE, then only the announced fields, in this order): speed (bit 0 CLEAR
= present; uint16 0.01 km/h) · avg speed (bit 1) · cadence (bit 2, uint16 0.5 rpm) · avg cadence (bit 3) · total
distance (bit 4, uint24 m) · resistance (bit 5, sint16) · power (bit 6, sint16 W) · avg power (bit 7) · energy (bit 8:
uint16 kcal, uint16 kcal/h, uint8 kcal/min) · heart rate (bit 9, uint8) · MET (bit 10, uint8 ×0.1) · elapsed (bit 11,
uint16 s) · remaining (bit 12, uint16 s). Packets shorter than the flags require are rejected with the reason logged.

---

# Kinetic inRide 2 custom protocol — as ported

Source of truth: [kinetic-fit/sensors-swift-kinetic](https://github.com/kinetic-fit/sensors-swift-kinetic),
commit `9335f102797f52e285054275cadcd1b3e15ae2ae` (SDK 2.3.4, May 2020). Files inspected:
`Sources/KineticSensors/inRide.c`, `inRide.h`, `KineticInRide.m`, `include/KineticInRide.h`,
`include/KineticConstants.h`, `KineticSDK.m`, `README`. The older Java port
([kinetic-fit/kinetic-sdk-java](https://github.com/kinetic-fit/kinetic-sdk-java), Dec 2017) was read for
cross-reference only.

Nothing below was inferred. Every rule is a translation of a specific function, and the C# code comments
name that function. The C source was compiled unmodified (`tools/reference-vectors/`) to generate 3,710
golden test vectors that the C# port must match bit-for-bit (`tests/.../inride_reference_vectors.json`).

## Port map

| Kinetic source | C# |
|---|---|
| `inRide.h` UUID constants, `KineticInRide.m` `KineticInRide*UUID` | `InRideUuids` |
| `inRide.h` `inride_sensor_state`, `inride_calibration_result`, `inride_command_result`, `inride_update_rate` | `InRideSensorState`, `InRideCalibrationResult`, `InRideCommandResult`, `InRideUpdateRate` |
| `KineticInRide.m` `KineticInRideSensorCommand`, `KineticInRideErrorCode` | `InRideCommand`, `InRideErrorCode` |
| `inRide.c` `#define`s, `KineticConstants.h` `RollerCircumferenceKM` | `InRideConstants` |
| `inRide.c` `inride_process_power_data` (de-obfuscation half) | `InRideDeobfuscator.Deobfuscate` |
| `inRide.c` `inride_process_power_data` (field parsing + derived values) | `InRidePowerParser.Parse` |
| `inRide.c` `inride_speed_for_ticks`, `inride_ticks_to_seconds`, `inride_has_pro_flywheel` | `InRidePowerParser.SpeedKphForTicks`, `.TicksToSeconds`, `.HasProFlywheel` |
| `inRide.c` `alpha` | `InRidePowerParser.Alpha` |
| `inRide.c` `power_for_speed` | `InRidePowerParser.PowerForSpeed` |
| `inRide.c` `result_for_spindown` | `InRidePowerParser.ResultForSpindown` |
| `inRide.c` `inride_process_config_data` | `InRideConfigurationParser.Parse` |
| `inRide.c` `command_key` | `InRideCommands.CommandKey` |
| `inRide.c` `inride_create_config_sensor_command_data` | `InRideCommands.ConfigureSensor` |
| `inRide.c` `inride_create_start/stop_calibration_command_data` | `InRideCommands.StartCalibration` / `.StopCalibration` |
| `inRide.c` `inride_create_set_spindown_time_command_data` | `InRideCommands.SetSpindownTime` |
| `KineticInRide.m` `setPeripheralNameCommandData:name:error:` | `InRideCommands.SetPeripheralName` |
| `KineticInRide.m` `adjustCadence:timestamp:` (+ the `cadenceRaw` uint16 truncation in `processPowerData`) | `InRideCadenceSmoother` |
| `KineticInRide.m` `processPowerData:systemId:error:`, `processConfigurationData:error:`, `*CommandData:` wrappers, `validateSystemId:` | `InRideProtocol` |
| `KineticInRide.m` `calibrationReadySpeedKPH`, `speedForTicks:revs:` | `InRideProtocol.CalibrationReadySpeedKph` |
| `KineticSDK.m` `systemIdToString:` | `InRideProtocol.SystemIdToString` |

## GATT

| Item | UUID | Properties (per README/`inRide.h`) |
|---|---|---|
| Power service | `E9410100-B434-446B-B5CC-36592FC4C724` | |
| Power characteristic | `E9410101-…` | Notify, 20 bytes |
| Control Point | `E9410102-…` | Write with response |
| Configuration | `E9410104-…` | Read / Notify, 20 bytes |
| Device Information service | `0x180A` | |
| System ID | `0x2A23` | Read, 6 bytes; "tied to the MAC address of the BLE Nordic chip" |

The System ID is required to build every command (command key). `inRide.h` says it is also "used in the
de-obfuscation process of the power data", but `inride_process_power_data` never reads it; the ObjC wrapper
only *validates* its length (error 201) before parsing. The port does the same, with a diagnostic override.

## Power packet (20 bytes, notify)

### 1. De-obfuscation (`inride_process_power_data`, first half)

```
rotate  = (data[0] & 0xC0) >> 6            // 0..3, selects the permutation table
xorIdx1 = (rotate + 1) % 4
xorIdx2 = (xorIdx1 + 1) % 4
for i in 1..19: deob[i] = data[i] XOR (indices[xorIdx1][i-1] + indices[xorIdx2][i-1])
plain[0] = deob[0]
for i in 0..18: plain[i+1] = deob[indices[rotate][i]]
```
`indices[4][19]` is copied verbatim from `inRide.c`. Byte 0 is never scrambled.

### 2. Plain layout (little-endian)

| Bytes | Field | Type | Notes |
|---|---|---|---|
| 0 | header | | bits 7-6 rotation; bits 5-4 sensor state (`& 0x30`); bits 3-0 command result (`& 0x0F`) |
| 1-3 | interval | uint24 | read but unused by the SDK maths |
| 4-7 | ticks | uint32 | sensor ticks (1/32768 s) spanned by `revs` roller revolutions |
| 8 | revs | uint8 | |
| 9-12 | ticksPrevious | uint32 | previous reading |
| 13 | revsPrevious | uint8 | |
| 14-15 | cadenceRaw | uint16 | |
| 16-19 | spindownTicks | uint32 | last spindown result |

### 3. Derived values

* `state = header & 0x30` → Normal 0x00, SpindownIdle 0x10, SpindownReady 0x20, SpindownActive 0x30.
* `commandResult = header & 0x0F` → None 0, Success 1, NotSupported 2, InvalidRequest 3, CalibrationResult 0x0A,
  UnknownError 0x0F. `inRide.h`: "Use Command Results for debug purposes only… not guaranteed". The ObjC code
  casts the raw nibble to the enum without mapping, so undefined values are preserved as-is.
* `seconds = ticks / 32768.0`
* `speedKPH = 20012.256849 * revs / ticks` (0 if either is 0). 20012.256849 = `RollerCircumferenceKM` ×
  32768 × 3600 ⇒ **roller circumference 169.646 mm** (54 mm nominal diameter, `KineticConstants.h`).
* `rollerRPM = revs / seconds * 60`
* `cadence (calibrated) = raw == 0 ? 0 : 0.8652 * raw + 5.2617`
* `lastSpindownResultTime = spindownTicks / 32768.0`
* Applied spindown time: default `(1.5 + 2.0) / 2 = 1.75` s; if the last result is within [1.5, 2.0] it is used
  (normal flywheel); if within [4.7, 6.5] it is used and `proFlywheel = true`.
* `rollerResistance = 1 - (spindown - min) / (max - min)` using the window that applies.
* Coasting (`alpha`): only when `ticks > 0 && ticksPrevious > 0` and ticks-per-rev grew (`dtpr > 0`):
  `alpha = (speedPrev - speed) * dtpr`; coasting if `alpha > 200` (normal) or `alpha > 20` (pro).
* Power (`power_for_speed`): `mph = kph * 0.621371`; `raw = 5.244820*mph + 0.019168*mph³`;
  if `spindown > 0 && raw > 0`: `drag = k1*spindownMs*raw*1e-5 + k2*spindownMs + k3` with
  (k1,k2,k3) = (4.55, −0.1425, 236.20) normal or (2.62, −0.021, 104.97) pro; `power = max(0, raw + drag)`
  truncated to int. **Power is 0 while coasting.**
* `calibrationResult` (`result_for_spindown`): [1.5,2.0] or [4.7,6.5] → Success; < 1.5 → TooFast (loosen roller);
  > 6.5 → TooSlow (tighten); otherwise Middle.

### 4. SDK cadence adjustment (`KineticInRide.m adjustCadence:timestamp:`)

The calibrated cadence is truncated to a `uint16_t` (`powerData.cadenceRaw = cData.cadenceRPM`) and passed
through a weighted rolling average: buffer of 3, newest weighted ×2, divisor 4. The buffer count resets when
cadence is 0 or the newest sample is > 2 s older than the current one. Two quirks reproduced on purpose:

1. The average always sums slots 1 and 2 even after a reset, so the first sample after start-up reads at
   **half** value (`2c/4`), the second at `(2c₂ + c₁)/4`, and only the third onwards is the full average.
   After a reset the stale pre-reset values are summed instead of zeros.
2. Kinetic keeps this as process-global static state; the port keeps it per connection.

## Configuration packet (20 bytes, read/notify, not obfuscated)

| Bytes | Field | Type |
|---|---|---|
| 0-1 | calibrationReady | uint16 ticks (default 602) |
| 2-3 | calibrationStart | uint16 (655) |
| 4-5 | calibrationEnd | uint16 (950) |
| 6-7 | calibrationDebounce | uint16 (327) |
| 8-11 | currentSpindownTicks | uint32 → seconds = /32768 |
| 12-13 | updateRateDefault | uint16 (0x20 / 0x10 / 0x08) |
| 14-15 | updateRateCalibration | uint16 (0x08) |
| 16-19 | — | not read by the SDK; logged as `tail` |

`proFlywheel = 4.7 ≤ currentSpindownTime ≤ 6.5`.

## Control point commands

`commandKey = systemId[systemId[3] % 6] | systemId[systemId[5] % 6] << 8` (2 bytes LE), then the opcode.

| Command | Opcode | Bytes | Payload after opcode |
|---|---|---|---|
| Configure sensor / set spindown params | 0x01 | 15 | calReady 602, calStart 655, calEnd 950, calDebounce 327, updateRateDefault, updateRateFast 0x08 (all uint16 LE) |
| Set name | 0x02 | 3 + n | UTF-8 name, 3-8 chars |
| Start calibration | 0x03 | 3 | |
| Stop calibration | 0x04 | 3 | |
| Set spindown time | 0x05 | 7 | uint32 LE ticks = `(uint32)(seconds * 32768)` |

Update rate values are the interval in 1/32 s: `0x20` = 1 Hz, `0x10` = 2 Hz, `0x08` = 4 Hz
(`inRide.h`: "The fastest update that can be set while maintaining data integrity is 8hz. Do not try to set it
faster."). The result of a command arrives in the low nibble of the **next power packet**, not as a write
response.

Calibration speeds implied by the interval constants (`speedForTicks:revs:` with revs = 1):
ready 602 ticks → 33.2 km/h (20.7 mph); start 655 → 30.6 km/h (19.0 mph); end 950 → 21.1 km/h (13.1 mph).

## Documentation vs. source discrepancies

| Topic | README / headers say | Source does | Port follows |
|---|---|---|---|
| Control point size | README: "3 – 11 bytes of command data" | configure command struct is **15** bytes | source (15) |
| Roller circumference | `inRide.h` comment: 169.328702435836 mm | `KineticConstants.h` 169.646 mm and the speed constant 20012.256849 agree with 169.646; the Java SDK's 19974.826517 matches the 169.329 comment | source constant (169.646 mm); configurable in Settings |
| Spindown end speed | README: "~19mph to ~11mph" | 950 ticks/rev = 13.1 mph | source |
| System ID and decoding | `inRide.h`: "used in the de-obfuscation process" | decoding never touches it; only length-validated | source; validation can be bypassed for diagnostics |
| Command result / state enum | Java maps unknown values to None/Normal | ObjC/C keep raw bits | ObjC/C |
| Config parsing | Java version sign-extends bytes (bug) | C masks correctly | C |
| Cadence buffer contents | Java stores the double, ObjC stores uint16 | | ObjC (primary reference) |
| `(int)power` overflow | C: undefined behaviour | | saturates to `int.MaxValue` (physically unreachable) |

## Assumptions to confirm on the physical inRide 2

1. The Power characteristic really delivers 20-byte notifications (anything else is rejected with error 202 and logged).
2. System ID (0x2A23) is exactly 6 bytes. If not, packets are rejected with error 201; the Settings page has an
   override and the Debug page shows the length actually read.
3. Configuration (E9410104) supports Notify on this firmware; if not, the app reads it on demand only.
4. Bytes 16-19 of the configuration packet — logged, meaning unknown.
5. The sensor advertises the power service UUID and/or a name containing "inRide"/"Kinetic". If neither, use
   "Show all Bluetooth devices" on the Sensor page.
6. The update-rate command is acknowledged with `Success` in the next packet and the packet rate changes
   (visible in the Debug page counters).
