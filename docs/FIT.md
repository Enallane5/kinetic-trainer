# FIT export — profile rows used

Encoder: `src/KineticTrainer.Recording/Fit/FitEncoder.cs`; constants: `FitProfile.cs`. Verified 2026-09-16 by decoding
generated files with **fitdecode 0.11.0** (SDK 21.171 tables) and **fitparse 1.2.0** (SDK 20.8 tables) with CRC checking on,
and by cross-checking every constant against both libraries' profile tables (identical rows). CRC-16 is the FIT nibble-table
algorithm = CRC-16/ARC (check value 0xBB3D for "123456789").

File: 14-byte header (size, protocol 0x20, profile 21.94, data size, ".FIT", header CRC) · messages · file CRC.
Timestamps: seconds since 1989-12-31T00:00:00Z, **truncated** (a 2 Hz stream gives exactly two records per second).

| Message (global #) | Fields written (field # · type · scale/unit) |
|---|---|
| file_id (0) | type 0 enum = activity(4) · manufacturer 1 uint16 = development(255) · product 2 uint16 · serial_number 3 uint32z · time_created 4 uint32 date_time · product_name 8 string |
| file_creator (49) | software_version 0 uint16 (unscaled) |
| device_info (23) | timestamp 253 · device_index 0 uint8 = creator(0) · manufacturer 2 · product 4 · software_version 5 uint16 ×100 · product_name 27 string |
| event (21) | timestamp 253 · event 0 enum = timer(0) · event_type 1 enum = start(0) / stop_all(4) · event_group 4 uint8 |
| record (20) | timestamp 253 · distance 5 uint32 ×100 m · speed 6 uint16 ×1000 m/s · power 7 uint16 W · cadence 4 uint8 rpm |
| lap (19) | timestamp 253 · message_index 254 · start_time 2 · total_elapsed_time 7 uint32 ×1000 s · total_timer_time 8 ×1000 s · total_distance 9 ×100 m · avg_speed 13 / max_speed 14 uint16 ×1000 m/s · **avg_cadence 17 / max_cadence 18** uint8 · avg_power 19 / max_power 20 uint16 · normalized_power 33 uint16 · event 0 = lap(9) · event_type 1 = stop(1) · lap_trigger 24 = session_end(7) · sport 25 = cycling(2) · sub_sport 39 = indoor_cycling(6) |
| session (18) | timestamp 253 · message_index 254 · start_time 2 · total_elapsed_time 7 · total_timer_time 8 · total_distance 9 · avg_speed 14 / max_speed 15 · avg_cadence 18 / max_cadence 19 · avg_power 20 / max_power 21 · normalized_power 34 · first_lap_index 25 · num_laps 26 · event 0 = session(8) · event_type 1 = stop(1) · sport 5 · sub_sport 6 · trigger 28 = activity_end(0) |
| activity (34) | timestamp 253 · total_timer_time 0 ×1000 s · local_timestamp 5 local_date_time · num_sessions 1 uint16 · type 2 = manual(0) · event 3 = activity(26) · event_type 4 = stop(1) |

Decisions: `total_timer_time` = `total_elapsed_time` (the ride timer never pauses; sensor drop-outs remain gaps in the
records and consumers compute moving time from speed). Missing values are written as the base type's invalid value.
`device_info.software_version` is scaled ×100 (10 = 0.10); `file_creator.software_version` is unscaled.
