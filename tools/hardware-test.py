"""
Automated Phase 1 hardware pass against the physical sensor, driven through the real app.

  python tools/hardware-test.py <path-to-KineticTrainer.exe> [--address C7:98:57:0F:8B:81] [--wait 240]
                                 [--sample 60] [--expect-motion]

Steps (each reported PASS/FAIL):
  1. launch app with --connect <address>, wait for "Connected"
  2. GATT: services enumerated, protocol chosen, data subscription, feature/config read
  3. packets: >= 10 accepted, 0 rejected, rate sane
  4. bridge: NDJSON lines with valid telemetry, simulated=false
  5. (--expect-motion) power > 0, cadence > 0, speed > 0 seen while the rider pedals
  6. remembered sensor saved
  7. restart app WITHOUT --connect -> auto-reconnect within the wait window
The rider must keep the sensor awake (spin the roller) and, for step 5, pedal during the sampling window.
"""
import argparse
import glob
import json
import os
import re
import socket
import subprocess
import sys
import time

LOG_DIR = os.path.expandvars(r"%LOCALAPPDATA%\KineticTrainer\logs")
results = []


def report(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"[{'PASS' if ok else 'FAIL'}] {name}{(' - ' + detail) if detail else ''}", flush=True)


def newest_log(after):
    files = [f for f in glob.glob(os.path.join(LOG_DIR, "kinetic-trainer-*.log")) if os.path.getmtime(f) >= after - 2]
    return max(files, key=os.path.getmtime) if files else None


def read_log(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def wait_for(path_getter, pattern, timeout, label):
    deadline = time.time() + timeout
    while time.time() < deadline:
        p = path_getter()
        if p:
            text = read_log(p)
            m = re.search(pattern, text)
            if m:
                return text, m
        time.sleep(0.5)
    return read_log(path_getter() or "") if path_getter() else "", None


def kill_all():
    # Close gracefully first so the app releases its GATT handles (a force-kill leaves Windows holding the
    # sensor's services for a while, which shows up as AccessDenied on the next connect).
    subprocess.run(["taskkill", "/IM", "KineticTrainer.exe"], capture_output=True)
    for _ in range(20):
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KineticTrainer.exe"], capture_output=True, text=True)
        if "KineticTrainer.exe" not in r.stdout:
            break
        time.sleep(0.5)
    subprocess.run(["taskkill", "/F", "/IM", "KineticTrainer.exe"], capture_output=True)
    time.sleep(2)


def close(proc):
    subprocess.run(["taskkill", "/PID", str(proc.pid)], capture_output=True)
    try:
        proc.wait(10)
    except subprocess.TimeoutExpired:
        proc.kill()
    time.sleep(2)


def launch(exe, extra):
    return subprocess.Popen([exe] + extra)


def read_bridge(seconds, port=47474):
    lines = []
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5) as s:
            s.settimeout(seconds + 5)
            f = s.makefile("r", encoding="utf-8")
            hello = f.readline()
            end = time.time() + seconds
            while time.time() < end:
                line = f.readline()
                if not line:
                    break
                try:
                    lines.append(json.loads(line))
                except json.JSONDecodeError:
                    pass
    except OSError as e:
        print("bridge error:", e)
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("exe")
    ap.add_argument("--address", default="C7:98:57:0F:8B:81")
    ap.add_argument("--wait", type=int, default=240)
    ap.add_argument("--sample", type=int, default=60)
    ap.add_argument("--expect-motion", action="store_true")
    ap.add_argument("--skip-restart", action="store_true")
    a = ap.parse_args()
    exe = os.path.abspath(a.exe)

    kill_all()
    t0 = time.time()
    proc = launch(exe, ["--connect", a.address, "--page", "ride"])
    get = lambda: newest_log(t0)

    # 1. connect (the session retries by itself only when reconnecting; --connect is one attempt, so retry here)
    text, m = wait_for(get, r"Connected \((\w+)\): (.*)", 45, "connect")
    attempts = 1
    while not m and time.time() - t0 < a.wait:
        print(f"  not connected yet (attempt {attempts}); spin the roller... relaunching", flush=True)
        close(proc); kill_all()
        t1 = time.time()
        proc = launch(exe, ["--connect", a.address, "--page", "ride"])
        get = lambda t1=t1: newest_log(t1)
        text, m = wait_for(get, r"Connected \((\w+)\): (.*)", 45, "connect")
        attempts += 1
    if not m:
        report("1. connect", False, "never connected: " + (re.findall(r"Connect to .* failed: (.*)", text) or ["no error logged"])[-1][:200])
        close(proc)
        return finish()
    protocol, info = m.group(1), m.group(2)
    report("1. connect", True, f"protocol={protocol} {info[:160]}")

    # 2. GATT details
    services = re.findall(r"SERVICE ([0-9a-f-]+) \(([^)]*)\)", text)
    report("2a. service discovery", len(services) > 0, ", ".join(s[1] for s in services))
    report("2b. protocol selection", protocol in ("FitnessMachine", "InRideCustom"), protocol)
    sub = re.search(r"Subscribed to (Indoor Bike Data|Power) \((\w+)\)", text)
    report("2c. data subscription", bool(sub), sub.group(0) if sub else "no subscription line")
    text, feat = wait_for(get, r"(Fitness Machine Feature|Configuration): (.*)\(read\)", 15, "feature")
    report("2d. feature/config read", bool(feat), (feat.group(0)[:200] if feat else "not read"))
    spin = "SpinDownControl" in text
    report("2e. spin-down capability declared", spin, "TargetSettings includes SpinDownControl" if spin else "not declared")

    # 3 + 4 + 5. packets & bridge, sampled together
    print(f"  sampling {a.sample}s of telemetry via the bridge (pedal now if --expect-motion)...", flush=True)
    lines = read_bridge(a.sample)
    text = read_log(get())
    accepted = len(re.findall(r"\tDebug\tPowerPacket\t", text))
    rejected = len(re.findall(r"\tPacketRejected\t", text))
    report("3a. packets accepted", accepted >= 10, f"{accepted} accepted in log")
    report("3b. packets rejected", rejected == 0, f"{rejected} rejected" + ("" if rejected == 0 else " - see log"))
    valid = [l for l in lines if l.get("valid")]
    if len(valid) >= 2:
        span = (valid[-1]["timestamp"] - valid[0]["timestamp"]) / 1000.0
        rate = (len(valid) - 1) / span if span > 0 else 0
    else:
        rate = 0
    report("3c. packet rate", 0.5 <= rate <= 5.0, f"{rate:.2f} Hz over {len(valid)} valid samples")
    report("4a. bridge streams telemetry", len(valid) >= 5, f"{len(lines)} lines, {len(valid)} valid")
    report("4b. bridge marks real sensor", all(not l.get("simulated") for l in valid) and len(valid) > 0, "simulated=false")
    if valid:
        pw = [l.get("power") for l in valid if l.get("power") is not None]
        cad = [l.get("cadence") for l in valid if l.get("cadence") is not None]
        spd = [l.get("speedMps") for l in valid if l.get("speedMps") is not None]
        summary = f"power {min(pw) if pw else '-'}..{max(pw) if pw else '-'} W, cadence {min(cad) if cad else '-'}..{max(cad) if cad else '-'} rpm, speed {min(spd) if spd else '-'}..{max(spd) if spd else '-'} m/s"
        report("4c. fields present", bool(pw) and bool(cad) and bool(spd), summary)
        if a.expect_motion:
            moving = any(p > 0 for p in pw) and any(c > 0 for c in cad) and any(s > 0 for s in spd)
            report("5. motion registered while pedalling", moving, summary)
            # coasting: a sample with speed > 0 and power == 0 after a moving one indicates the stop-pedalling behaviour
        else:
            report("5. motion registered while pedalling", None, "skipped (run with --expect-motion while pedalling)")

    # 6. remembered
    text = read_log(get())
    remembered = re.search(r"Remembered sensor (.*)", text)
    report("6. sensor remembered", bool(remembered), remembered.group(1) if remembered else "no 'Remembered sensor' line")

    close(proc)
    kill_all()

    # 7. restart without --connect: auto-reconnect
    if a.skip_restart:
        report("7. restart auto-reconnect", None, "skipped")
        return finish()
    t2 = time.time()
    proc = launch(exe, ["--page", "ride"])
    get2 = lambda: newest_log(t2)
    text, m = wait_for(get2, r"Looking for remembered sensor", 20, "searching")
    report("7a. searches for remembered sensor on start", bool(m))
    text, m = wait_for(get2, r"Reconnected|Connected \(", 90, "auto-reconnect")
    report("7b. auto-reconnected", bool(m), "spin the roller if this failed - the sensor may have slept")
    time.sleep(3)
    close(proc)
    kill_all()
    return finish()


def finish():
    print("\n==== HARDWARE PASS SUMMARY ====")
    fails = 0
    for name, ok, detail in results:
        status = "SKIP" if ok is None else ("PASS" if ok else "FAIL")
        if ok is False:
            fails += 1
        print(f"{status:4}  {name}  {detail}")
    print("==== ", "ALL PASSED" if fails == 0 else f"{fails} FAILED")
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
