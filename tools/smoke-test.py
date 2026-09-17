"""
End-to-end smoke test without hardware:
  1. start KineticTrainer.exe --simulate (connects to the built-in simulated inRide)
  2. connect to the localhost telemetry bridge and read NDJSON lines
  3. check that valid power/cadence/speed telemetry arrives
  4. close the app and print the tail of its log file

Usage: python tools/smoke-test.py <path-to-KineticTrainer.exe> [port]
"""
import glob
import json
import os
import socket
import subprocess
import sys
import time

exe = sys.argv[1]
port = int(sys.argv[2]) if len(sys.argv) > 2 else 47474
proc = subprocess.Popen([exe, "--simulate"])
ok = False
try:
    deadline = time.time() + 30
    sock = None
    while time.time() < deadline:
        try:
            sock = socket.create_connection(("127.0.0.1", port), timeout=1)
            break
        except OSError:
            time.sleep(0.5)
    if sock is None:
        raise SystemExit("bridge port never opened")
    sock.settimeout(15)
    f = sock.makefile("r", encoding="utf-8")
    hello = json.loads(f.readline())
    assert hello["type"] == "hello", hello
    print("hello:", hello)
    valid = 0
    for _ in range(40):
        line = f.readline()
        if not line:
            break
        msg = json.loads(line)
        print(line.strip())
        if msg.get("valid") and msg.get("power") is not None and msg.get("cadence") and msg.get("speedMps"):
            valid += 1
            if valid >= 5:
                break
    assert valid >= 5, "did not receive 5 valid telemetry lines"
    assert msg["simulated"] is True
    ok = True
    sock.close()
finally:
    proc.terminate()
    try:
        proc.wait(10)
    except subprocess.TimeoutExpired:
        proc.kill()
    logs = sorted(glob.glob(os.path.expandvars(r"%LOCALAPPDATA%\KineticTrainer\logs\kinetic-trainer-*.log")), key=os.path.getmtime)
    if logs:
        print("\n--- tail of", logs[-1])
        with open(logs[-1], encoding="utf-8") as lf:
            lines = lf.readlines()
        print("".join(l for l in lines if "PowerPacket" not in l)[-4000:])
        print("power packet lines in log:", sum("PowerPacket" in l for l in lines))
print("SMOKE TEST", "PASSED" if ok else "FAILED")
sys.exit(0 if ok else 1)
