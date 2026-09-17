"""Generates src/KineticTrainer.App/Assets/kinetic-trainer.ico (a simple roller/wheel glyph) with only the stdlib."""
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "KineticTrainer.App", "Assets", "kinetic-trainer.ico")


def render(size):
    px = bytearray()
    c = (size - 1) / 2
    for y in range(size):
        for x in range(size):
            dx, dy = x - c, y - c
            r = math.hypot(dx, dy) / (size / 2)
            # rounded square background
            inside = abs(dx) < size * 0.46 and abs(dy) < size * 0.46 and (abs(dx) < size * 0.36 or abs(dy) < size * 0.36 or math.hypot(abs(dx) - size * 0.36, abs(dy) - size * 0.36) < size * 0.10)
            if not inside:
                px += b"\x00\x00\x00\x00"
                continue
            bg = (30, 120, 60)
            ring = 0.78 > r > 0.58
            hub = r < 0.16
            spoke = abs(math.atan2(dy, dx) % (math.pi / 2)) < 0.12 and r < 0.6
            col = (250, 250, 250) if (ring or hub or spoke) else bg
            px += bytes(col) + b"\xff"
    return bytes(px)


def png(size, rgba):
    raw = b"".join(b"\x00" + rgba[y * size * 4:(y + 1) * size * 4] for y in range(size))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    sizes = [16, 32, 48, 256]
    images = [png(s, render(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, img in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(img), offset)
        offset += len(img)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(header + entries + b"".join(images))
    print("wrote", os.path.normpath(OUT))


if __name__ == "__main__":
    main()
