# Phase 1 validation with the physical inRide 2

Everything in this list is what the app must demonstrate against real hardware before Phase 2 starts.
Do it once with the PC **offline** (Wi-Fi off) to prove the no-internet requirement at the same time.

## Setup

1. Bluetooth on (Windows Settings → Bluetooth & devices). Do **not** pair the inRide in Windows Settings — the
   app talks to it directly, no pairing needed.
2. Mount the bike, tyre on the roller, sensor on the trainer as with Kinetic Fit.
3. Run `KineticTrainer.exe`. Open the **Debug** page in a second window later if you like; it keeps logging
   whatever page is showing. Everything also goes to
   `%LOCALAPPDATA%\KineticTrainer\logs\kinetic-trainer-YYYYMMDD-HHMMSS.log`.

## Checklist (matches the Phase 1 acceptance test)

| # | Step | Pass looks like | Where to look |
|---|---|---|---|
| 1 | Sensor page → **Scan for sensors**, spin the roller to wake the sensor | A row with an `inRide` badge (name "inRide…" or the power service UUID advertised) | Sensor page |
| 2 | **Connect** | Header dot turns green, "Connected" | Header |
| 3 | GATT discovery | Debug events show `Power service characteristic E9410101/E9410102/E9410104 … props=…` and `Device Information characteristic 2A23 …` | Debug page → events |
| 4 | Configuration read | `Configuration (read): spindown=… rate=0x20 = 1 Hz …` and the Sensor page "Configuration" box is filled | Both |
| 5 | Power subscription | `Subscribed to Power notifications` | Debug events |
| 6 | Raw packets | Rows appear in "Power packets" with 40 hex chars in the *raw* column and 40 in *de-obfuscated* | Debug page |
| 7 | Live power | Pedal steadily: watts rise with speed, drop to **0 when you stop pedalling and coast** (Kinetic's coasting rule) | Ride page |
| 8 | Live cadence | Matches your actual pedalling rate within a few rpm after 3 packets (first packet after start reads ~half — SDK quirk) | Ride page / Debug |
| 9 | Roller RPM | Sanity: at 20 mph the roller turns ≈ 3,160 rpm (169.6 mm circumference) | Debug |
| 10 | Speed | Compare to the Kinetic Fit app / bike computer on the same ride; expect agreement to ~0.1 mph | Ride page |
| 11 | Continuous | Rate counter shows ≈ 1.0 Hz (or the rate you set) with no "rejected" growth | Debug stats line |
| 12 | Malformed packets | `Rejected` counter stays 0; if it ever increments, the event log names the reason and hex — the app keeps running | Debug |
| 13 | Disconnect | Walk the bike/sensor out of range or remove the sensor battery: header goes orange "Reconnecting…", Ride page dims and says "Sensor lost — reconnecting…", **no fake numbers** | Ride/Header |
| 14 | Reconnect | Bring it back and spin the roller: green again within a few seconds, telemetry resumes | Header |
| 15 | Remember | Sensor page shows "Remembered: inRide … last connected …" | Sensor page |
| 16 | Restart | Close the app, reopen: header goes yellow "Searching…" then green without touching anything (spin the roller to wake the sensor) | Header |
| 17 | Update rate | Sensor page → choose 2 Hz → **Apply to sensor**: "Sensor answered: Success", packet rate ≈ 2.0 Hz, configuration notification (or "Read now") shows `rate=0x10 = 2 Hz` | Sensor + Debug |
| 18 | TCP bridge | With the app running: `python tools/bridge-client.py` prints one JSON line per packet with the same power/cadence/speed | terminal |
| 19 | Offline | All of the above with Wi-Fi/Ethernet disabled | — |

## Things worth copying into the report

* The `Connected: name=… systemId=… (N bytes) fw=… hw=… mfr=…` line.
* The characteristic property lines (`props=…`) for all three inRide characteristics.
* A dozen consecutive raw packet rows while pedalling (Debug → **Copy packets**), and a few while coasting.
* One configuration line with its raw hex (especially the last 8 hex chars, `tail=`).
* Whether "Sensor answered: Success" appears after **Apply to sensor**.

## If something is off

| Symptom | Meaning | What to do |
|---|---|---|
| Rows rejected with `201` | System ID is not 6 bytes on this firmware | Settings → "Ignore System ID length validation", report the length shown in `Connected:` |
| Rows rejected with `202` | Notification is not 20 bytes | Report the hex; nothing else to try — the SDK cannot decode it either |
| Power always 0 while pedalling | Coasting rule firing: `alpha` in the detail column > 200 | Report packets; check `prev` ticks/revs look like the previous row's |
| Cadence 0 | `cadenceRaw` (in parentheses) is 0 in the packet | Sensor did not detect the crank magnet/pedal stroke — same as Kinetic Fit would show |
| No inRide in the list | Sensor asleep, or advertises without name/service UUID | Spin the roller; tick "Show all Bluetooth devices" and look for a new row that appears when you spin |
| "Subscribing to Power notifications failed" | CCCD write rejected | Report the status/ATT error from the event log |
