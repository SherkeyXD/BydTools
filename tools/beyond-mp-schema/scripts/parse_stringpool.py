#!/usr/bin/env python3
"""Resolve Beyond USPC StringPool*.byte ids to strings.

Global id = block_index * len(StringPool0) + local_index.
`-1` is empty and `-2` is null on the MemoryPack wire; those are not pool rows.

The Rust CLI does the same thing (`beyond-mp-schema decode --string-pool`).
This script is a small standalone checker.
"""

from __future__ import annotations

import json
import re
import struct
import sys
from pathlib import Path


def lz4_decode(src: bytes, dst_size: int) -> bytes:
    dst = bytearray(dst_size)
    ip = op = 0
    n = len(src)
    while ip < n:
        token = src[ip]
        ip += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[ip]
                ip += 1
                lit += b
                if b != 255:
                    break
        dst[op : op + lit] = src[ip : ip + lit]
        ip += lit
        op += lit
        if ip >= n or op >= dst_size:
            break
        off = src[ip] | (src[ip + 1] << 8)
        ip += 2
        ml = (token & 0xF) + 4
        if (token & 0xF) == 15:
            while True:
                b = src[ip]
                ip += 1
                ml += b
                if b != 255:
                    break
        mp = op - off
        for i in range(ml):
            dst[op + i] = dst[mp + i]
        op += ml
    return bytes(dst[:op])


def parse_uspc(data: bytes) -> list[str]:
    if data[:4] != b"USPC" and data[4:8] == b"USPC":
        data = data[4:]
    if data[:4] != b"USPC":
        raise SystemExit("missing USPC magic")
    version, flags, count, raw_len, stored = struct.unpack_from("<iiiii", data, 4)
    if version != 1:
        raise SystemExit(f"unsupported USPC version {version}")
    payload = data[24 : 24 + stored]
    raw = lz4_decode(payload, raw_len) if flags & 1 else payload[:raw_len]
    inner = struct.unpack_from("<i", raw, 0)[0]
    if inner != count:
        raise SystemExit(f"count mismatch {count} vs {inner}")
    base = 4 + 4 * count
    strings = []
    for i in range(count):
        off = struct.unpack_from("<i", raw, 4 + 4 * i)[0]
        ln = struct.unpack_from("<i", raw, base + off)[0]
        start = base + off + 4
        strings.append(raw[start : start + ln].decode("utf-8"))
    return strings


def load_dir(sp_dir: Path) -> dict[int, str]:
    blocks: dict[int, list[str]] = {}
    for path in sorted(sp_dir.glob("StringPool*.byte")):
        match = re.fullmatch(r"StringPool(\d+)\.byte", path.name)
        if not match:
            continue
        blocks[int(match.group(1))] = parse_uspc(path.read_bytes())
    if 0 not in blocks:
        raise SystemExit(f"StringPool0.byte not found in {sp_dir}")
    stride = len(blocks[0])
    out: dict[int, str] = {}
    for block, strings in blocks.items():
        for local, text in enumerate(strings):
            out[block * stride + local] = text
    return out


def main() -> None:
    if len(sys.argv) < 2:
        print("usage: parse_stringpool.py <StringPool dir> [id ...]", file=sys.stderr)
        raise SystemExit(2)
    table = load_dir(Path(sys.argv[1]))
    print(
        json.dumps(
            {
                "strings": len(table),
                "min_id": min(table) if table else None,
                "max_id": max(table) if table else None,
            }
        )
    )
    for arg in sys.argv[2:]:
        i = int(arg)
        print(i, table.get(i, "<missing>"))


if __name__ == "__main__":
    main()
