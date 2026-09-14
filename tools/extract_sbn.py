#!/usr/bin/env python3
"""Parse a Sony Ericsson .SBN full-flash image and write a flat binary.

The .SBN is a *binary* variant of Motorola S-records (not the ASCII text form):
a 4-byte `S003` magic, then records made of an ASCII 'S', an ASCII type digit,
a binary count byte, a big-endian binary address, binary data, and a binary
checksum byte. The file ends with a bare 2-byte `S8` terminator.

Usage:
    python3 tools/extract_sbn.py <input.sbn> [output.bin]

Verifies every record checksum, prints the record census and memory map, and
(when output.bin is given) writes a flat image with gaps filled with 0xFF. In
the flat image, byte offset == flash address because the image starts at 0.
"""
import sys

ADDR_LEN = {"1": 2, "2": 3, "3": 4}


def parse(data):
    if data[:4] != b"S003":
        raise ValueError("missing S003 magic; not an SBN image")
    i = 4
    counts = {"1": 0, "2": 0, "3": 0}
    segments = []  # (address, payload_bytes)
    bad_checksums = 0
    while i < len(data):
        if len(data) - i < 3:
            # Trailing terminator, e.g. bare "S8".
            break
        if data[i] != ord("S"):
            raise ValueError(f"expected 'S' record marker at offset {i:#x}")
        rtype = chr(data[i + 1])
        if rtype not in ADDR_LEN:
            # A non-data record such as the "S8" tail; stop cleanly.
            break
        count = data[i + 2]
        body = data[i + 3 : i + 3 + count]
        if len(body) < count:
            raise ValueError(f"truncated record at offset {i:#x}")
        alen = ADDR_LEN[rtype]
        addr = int.from_bytes(body[:alen], "big")
        payload = body[alen:-1]
        checksum = body[-1]
        if (~(count + sum(body[:-1])) & 0xFF) != checksum:
            bad_checksums += 1
        counts[rtype] += 1
        segments.append((addr, payload))
        i += 3 + count
    return counts, segments, bad_checksums


def contiguous_regions(segments):
    segments = sorted(segments)
    regions = []
    start, end = segments[0][0], segments[0][0] + len(segments[0][1])
    for addr, payload in segments[1:]:
        if addr <= end:
            end = max(end, addr + len(payload))
        else:
            regions.append((start, end))
            start, end = addr, addr + len(payload)
    regions.append((start, end))
    return regions


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    data = open(argv[1], "rb").read()
    counts, segments, bad = parse(data)
    payload = sum(len(p) for _, p in segments)
    lo = min(a for a, _ in segments)
    hi = max(a + len(p) for a, p in segments)
    total = sum(counts.values())

    print(f"file size:      {len(data):,} bytes")
    print(f"records:        {total:,}  (" + ", ".join(f"S{t}={n:,}" for t, n in counts.items() if n) + ")")
    print(f"payload:        {payload:,} bytes")
    print(f"bad checksums:  {bad}")
    print(f"address range:  {lo:#08x} .. {hi:#08x}  (span {hi - lo:,} bytes)")
    regions = contiguous_regions(segments)
    print(f"regions:        {len(regions)} contiguous")
    for start, end in regions[:6]:
        print(f"  {start:#08x} - {end:#08x}  ({end - start:,} bytes)")
    if len(regions) > 6:
        print(f"  ... and {len(regions) - 6} more")

    if bad:
        print(f"WARNING: {bad} record(s) failed checksum", file=sys.stderr)

    if len(argv) >= 3:
        image = bytearray(b"\xff" * (hi - lo))
        for addr, p in segments:
            image[addr - lo : addr - lo + len(p)] = p
        open(argv[2], "wb").write(image)
        print(f"wrote {argv[2]}  ({len(image):,} bytes, gaps filled with 0xFF)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
