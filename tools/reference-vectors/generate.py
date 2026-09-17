"""
Generate golden reference vectors for the C# inRide protocol port.

Every expected value in the output file is produced by Kinetic's ORIGINAL C code
(inRide.c from github.com/kinetic-fit/sensors-swift-kinetic, commit 9335f10,
SDK 2.3.4) compiled into harness.exe - NOT by this script. This script only
chooses the inputs and, for realistic packets, applies the inverse of Kinetic's
de-obfuscation so the C code receives packets that decode to chosen values.

Usage (from this folder):
    python generate.py            # builds harness.exe if missing, writes vectors
Requires: `pip install ziglang` (provides a C compiler), and the Kinetic source in
./_kinetic-src (git clone https://github.com/kinetic-fit/sensors-swift-kinetic _kinetic-src).
"""
import json
import math
import os
import random
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "tests", "KineticTrainer.Tests", "TestVectors", "inride_reference_vectors.json")

# De-obfuscation tables, copied from inRide.c (inride_process_power_data)
INDICES = [
    [14, 15, 12, 16, 11, 5, 17, 3, 2, 1, 19, 13, 6, 4, 8, 9, 10, 18, 7],
    [12, 14, 8, 11, 16, 4, 7, 13, 18, 1, 3, 19, 6, 15, 9, 5, 10, 17, 2],
    [11, 5, 1, 9, 4, 18, 7, 15, 6, 2, 10, 12, 16, 3, 14, 13, 19, 17, 8],
    [13, 5, 18, 1, 3, 12, 15, 10, 14, 19, 16, 8, 6, 11, 2, 9, 4, 17, 7],
]
SENSOR_HZ = 32768
ROLLER_CIRCUMFERENCE_M = 0.00016964600329384882 * 1000.0  # KineticConstants.h


def obfuscate(plain):
    """Exact inverse of inride_process_power_data's de-obfuscation step."""
    assert len(plain) == 20
    rotate = (plain[0] & 0xC0) >> 6
    x1 = (rotate + 1) % 4
    x2 = (x1 + 1) % 4
    deob = [0] * 20
    deob[0] = plain[0]
    for j in range(19):
        deob[INDICES[rotate][j]] = plain[j + 1]
    data = [0] * 20
    data[0] = deob[0]
    for i in range(1, 20):
        data[i] = (deob[i] ^ ((INDICES[x1][i - 1] + INDICES[x2][i - 1]) & 0xFF)) & 0xFF
    return bytes(data)


def plain_packet(rotate, state, cmd, interval, ticks, revs, ticks_prev, revs_prev, cadence_raw, spindown_ticks):
    b = bytearray(20)
    b[0] = ((rotate & 3) << 6) | (state & 0x30) | (cmd & 0x0F)
    b[1:4] = (interval & 0xFFFFFF).to_bytes(3, "little")
    b[4:8] = (ticks & 0xFFFFFFFF).to_bytes(4, "little")
    b[8] = revs & 0xFF
    b[9:13] = (ticks_prev & 0xFFFFFFFF).to_bytes(4, "little")
    b[13] = revs_prev & 0xFF
    b[14:16] = (cadence_raw & 0xFFFF).to_bytes(2, "little")
    b[16:20] = (spindown_ticks & 0xFFFFFFFF).to_bytes(4, "little")
    return bytes(b)


def speed_to_ticks(kph, revs):
    if kph <= 0:
        return 0
    rps = (kph / 3.6) / ROLLER_CIRCUMFERENCE_M
    return int(round(revs / rps * SENSOR_HZ))


def cadence_to_raw(rpm):
    if rpm <= 0:
        return 0
    return max(1, int(round((rpm - 5.2617) / 0.8652)))


def build_inputs(rng):
    cases = []  # list of (group, name, [command lines])

    # 1) Random raw 20-byte power packets, each after a cadence reset.
    lines = []
    for _ in range(400):
        lines.append("R")
        lines.append("P %s %.3f" % (bytes(rng.getrandbits(8) for _ in range(20)).hex(), 1000.0 + rng.random() * 10))
    cases.append(("power_random", "random raw packets (independent, cadence buffer reset before each)", lines))

    # 2) Edge packets: all zeros, all 0xFF, each rotation with zeroed payload.
    lines = ["R", "P " + "00" * 20 + " 1.0", "R", "P " + "ff" * 20 + " 1.0"]
    for rot in range(4):
        for st in (0x00, 0x10, 0x20, 0x30):
            lines.append("R")
            lines.append("P %s 1.0" % obfuscate(plain_packet(rot, st, 0, 0, 0, 0, 0, 0, 0, 0)).hex())
    cases.append(("power_edges", "edge packets", lines))

    # 3) Realistic ride sequences (stateful cadence smoothing across packets).
    spindowns = {
        "never_calibrated": 0.0, "normal_1_8s": 1.8, "normal_min_1_5s": 1.5, "normal_max_2_0s": 2.0,
        "middle_3s": 3.0, "pro_5_5s": 5.5, "too_fast_1_2s": 1.2, "too_slow_7s": 7.0,
    }
    for sd_name, sd in spindowns.items():
        for rate_hz in (1, 2, 4):
            lines = ["R"]
            t = 5000.0
            prev_ticks, prev_revs = 0, 0
            kph = 0.0
            for n in range(90):
                phase = n / 90.0
                if phase < 0.4:
                    kph = 45.0 * phase / 0.4
                elif phase < 0.7:
                    kph = 45.0 + 3.0 * math.sin(n)
                else:
                    kph = max(0.0, kph - 2.5)  # coast down
                cad = 0 if phase >= 0.7 else 55 + 40 * phase + rng.uniform(-3, 3)
                revs = max(1, min(255, int(round((kph / 3.6) / ROLLER_CIRCUMFERENCE_M / rate_hz)))) if kph > 0 else 0
                ticks = speed_to_ticks(kph, revs) if revs else 0
                rot = rng.randrange(4)
                p = plain_packet(rot, 0, 0, SENSOR_HZ // rate_hz, ticks, revs, prev_ticks, prev_revs,
                                 cadence_to_raw(cad), int(sd * SENSOR_HZ))
                lines.append("P %s %.4f" % (obfuscate(p).hex(), t))
                prev_ticks, prev_revs = ticks, revs
                t += 1.0 / rate_hz
                if n == 60:
                    t += 2.5  # gap > 2 s resets the SDK cadence buffer
            cases.append(("power_sequence", "ride %s @ %dHz" % (sd_name, rate_hz), lines))

    # 4) Calibration states and command results.
    lines = ["R"]
    t = 100.0
    for st in (0x10, 0x20, 0x30, 0x00):
        for cmd in (0x0, 0x1, 0x2, 0x3, 0xA, 0xF, 0x5):
            p = plain_packet(rng.randrange(4), st, cmd, 8192, speed_to_ticks(30.0, 12), 12,
                             speed_to_ticks(31.0, 12), 12, cadence_to_raw(85), int(1.73 * SENSOR_HZ))
            lines.append("P %s %.2f" % (obfuscate(p).hex(), t))
            t += 0.25
    cases.append(("power_states", "spindown states x command results", lines))

    # 5) Configuration packets.
    lines = []
    for _ in range(150):
        lines.append("C " + bytes(rng.getrandbits(8) for _ in range(20)).hex())
    for sd in (0.0, 1.5, 1.75, 2.0, 3.0, 4.7, 5.5, 6.5, 7.0):
        for rate in (0x20, 0x10, 0x08):
            b = bytearray(20)
            b[0:2] = (602).to_bytes(2, "little"); b[2:4] = (655).to_bytes(2, "little")
            b[4:6] = (950).to_bytes(2, "little"); b[6:8] = (327).to_bytes(2, "little")
            b[8:12] = int(sd * SENSOR_HZ).to_bytes(4, "little")
            b[12:14] = rate.to_bytes(2, "little"); b[14:16] = (0x08).to_bytes(2, "little")
            lines.append("C " + bytes(b).hex())
    cases.append(("config", "configuration packets", lines))

    # 6) Control-point commands.
    lines = []
    sysids = ["000000000000", "ffffffffffff", "0123456789ab", "c7a2f1003e5d"] + \
             [bytes(rng.getrandbits(8) for _ in range(6)).hex() for _ in range(40)]
    for s in sysids:
        for rate in (32, 16, 8):
            lines.append("K %s %d" % (s, rate))
        lines.append("S " + s)
        lines.append("T " + s)
        for secs in (0.0, 1.5, 1.75, 2.0, 5.5, 6.5):
            lines.append("D %s %.17g" % (s, secs))
    cases.append(("commands", "control point commands", lines))
    return cases


def main():
    exe = os.path.join(HERE, "harness.exe")
    if not os.path.exists(exe):
        zig = [sys.executable, "-m", "ziglang"]
        src = os.path.join(HERE, "_kinetic-src", "Sources", "KineticSensors")
        subprocess.check_call(zig + ["cc", "-O1", "-std=c11", "-ffp-contract=off", "-target", "x86_64-windows-gnu",
                                     "-mcpu=x86_64", "-I", src, "-o", exe, os.path.join(HERE, "harness.c")])
    rng = random.Random(20260915)
    out = {
        "source": "kinetic-fit/sensors-swift-kinetic @ 9335f102797f52e285054275cadcd1b3e15ae2ae (SDK 2.3.4), inRide.c compiled unmodified",
        "generator": "tools/reference-vectors/generate.py + harness.c",
        "groups": [],
    }
    for group, name, lines in build_inputs(rng):
        res = subprocess.run([exe], input="\n".join(lines) + "\n", capture_output=True, text=True, check=True)
        results = [json.loads(l) for l in res.stdout.splitlines() if l.strip()]
        out["groups"].append({"group": group, "name": name, "steps": results})
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", newline="\n") as f:
        json.dump(out, f, indent=0)
    print("wrote", os.path.normpath(OUT), sum(len(g["steps"]) for g in out["groups"]), "steps")


if __name__ == "__main__":
    main()
