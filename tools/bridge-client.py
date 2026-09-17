"""Minimal example of a simulator-side client: prints the NDJSON telemetry stream from the app's loopback bridge.
Usage: python tools/bridge-client.py [port]   (default 47474)"""
import socket
import sys

port = int(sys.argv[1]) if len(sys.argv) > 1 else 47474
with socket.create_connection(("127.0.0.1", port)) as s:
    f = s.makefile("r", encoding="utf-8")
    for line in f:
        print(line.rstrip())
