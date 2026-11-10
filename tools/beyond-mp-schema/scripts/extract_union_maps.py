#!/usr/bin/env python3
"""Extract Beyond MemoryPack tagged-union maps from GameAssembly Formatter jumptables.

Endfield does not ship classic [MemoryPackUnion] CustomAttributes. Tags live in
ForMemoryPackFormatter.Deserialize jumptables inside GameAssembly.dll.

Deps: pip install capstone (see /workspace/venv-mp)
"""
from __future__ import annotations

import argparse
import json
import re
import struct
from pathlib import Path

from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_MEM, X86_REG_RIP

IMAGE_BASE = 0x180000000
DEFAULT_MR_VA = 0x18DA60E20

# Formatter.Deserialize VA ranges from zmd-il2/out/cs/il2cpp.cs
# (start inclusive, end exclusive-ish as dumped)
UNIONS = {
    # name -> (start_va, end_va, follow_jmp_stubs, prefer_full_names)
    "AbilityActionData": (0x185CBDEA0, 0x185CCB000, False, False),
    "ActionBase": (0x184A325E0, 0x184A9F560, True, True),
    "ActionHeader": (0x1863D6E50, 0x1863DDBF0, True, True),
    "PureGetter": (0x184A9F560, 0x184AF7DE0, True, True),
    "AbstractBehaviorData": (0x185CBC240, 0x185CBC9F0, True, True),
    "DamageProcessorBase": (0x1865A84C0, 0x1865A8A70, True, True),
    "ConditionRuntimeBase": (0x18605EB40, 0x186062F70, True, True),
}

# Alias: bare nested AbilityAction `Data` uses the same jumptable as AbilityActionData.
# Prefer full wrapper names so ResolveNamed is not ambiguous across 459 Data leaves.
DATA_ALIAS_OF = "AbilityActionData"


def parse_sections(ga: bytes):
    e_lfanew = int.from_bytes(ga[0x3C:0x40], "little")
    opt_size = int.from_bytes(ga[e_lfanew + 0x14 : e_lfanew + 0x16], "little")
    sect = e_lfanew + 0x18 + opt_size
    sections = []
    for i in range(int.from_bytes(ga[e_lfanew + 6 : e_lfanew + 8], "little")):
        off = sect + i * 40
        va = int.from_bytes(ga[off + 12 : off + 16], "little")
        rawsize = int.from_bytes(ga[off + 16 : off + 20], "little")
        rawptr = int.from_bytes(ga[off + 20 : off + 24], "little")
        vsize = int.from_bytes(ga[off + 8 : off + 12], "little")
        sections.append((va, rawptr, rawsize, vsize))
    return sections


def va_to_off(sections, va: int) -> int:
    rva = va - IMAGE_BASE
    for v, rp, rs, vs in sections:
        if v <= rva < v + max(rs, vs) and rva - v < rs:
            return rp + (rva - v)
    raise KeyError(hex(va))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--gameassembly", default="/home/box/Games/endfield/GameAssembly.dll")
    ap.add_argument(
        "--type-index",
        default="/workspace/beyond-mp-schema/out/memorypack-endfield/type_index.json",
    )
    ap.add_argument("--out-dir", default="/workspace/union-maps")
    ap.add_argument("--mr-va", type=lambda s: int(s, 0), default=DEFAULT_MR_VA)
    ap.add_argument(
        "--only",
        nargs="*",
        default=None,
        help="Optional subset of union keys to extract",
    )
    args = ap.parse_args()

    ga = Path(args.gameassembly).read_bytes()
    sections = parse_sections(ga)

    def ru64(va: int) -> int:
        return struct.unpack_from("<Q", ga, va_to_off(sections, va))[0]

    idx = json.loads(Path(args.type_index).read_text())
    td_to_target, td_to_wrapper, target_counts = {}, {}, {}
    by_basename = idx.get("by_basename") or {}
    for target, info in idx["by_type"].items():
        target_counts[target] = len(info.get("entries") or [])
        for e in info.get("entries") or []:
            td = e.get("typedef_index")
            if td is None:
                continue
            td_to_target[td] = target
            td_to_wrapper[td] = e.get("wrapper_type")

    types_ptr = ru64(args.mr_va + 0x38)

    def typedef_from_type_index(ti: int) -> int:
        return int(ru64(ru64(types_ptr + ti * 8)))

    def decode_token(enc: int):
        if enc & 1 == 0:
            return None
        return ((enc >> 29) & 7, (enc >> 1) & 0xFFFFFFF)

    def map_name(td: int, prefer_full: bool):
        target = td_to_target.get(td)
        wrapper = td_to_wrapper.get(td)
        if target is None:
            return None
        if prefer_full:
            # Prefer FQCN target; fall back to wrapper when target is the shared leaf "Data"
            if target == "Data" or (target in by_basename and len(by_basename.get(target, [])) > 1):
                return wrapper or target
            if target_counts.get(target, 0) > 1:
                return wrapper or target
            return target
        if target_counts.get(target, 0) == 1:
            leaf = target.rsplit(".", 1)[-1]
            if target == leaf or leaf in idx["by_type"] or (
                leaf in by_basename and len(by_basename[leaf]) == 1
            ):
                return leaf
            return target
        return wrapper or target

    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True

    def follow_jmps(va: int, limit: int = 5) -> int:
        for _ in range(limit):
            off = va_to_off(sections, va)
            ins = next(md.disasm(ga[off : off + 16], va))
            if ins.mnemonic == "jmp" and ins.op_str.startswith("0x"):
                va = int(ins.op_str, 16)
                continue
            return va
        return va

    def find_typeinfo_index(case_va: int, scan: int = 256):
        off = va_to_off(sections, case_va)
        for insn in md.disasm(ga[off : off + scan], case_va):
            if insn.mnemonic != "mov" or len(insn.operands) < 2:
                continue
            if insn.operands[1].type != X86_OP_MEM:
                continue
            mem = insn.operands[1].mem
            if mem.base != X86_REG_RIP:
                continue
            slot = insn.address + insn.size + mem.disp
            try:
                enc = ru64(slot)
            except KeyError:
                continue
            dec = decode_token(enc)
            if dec and dec[0] == 1:
                return dec[1]
        return None

    def extract(start: int, end: int, follow: bool, prefer_full: bool):
        code = ga[va_to_off(sections, start) : va_to_off(sections, end)]
        insns = list(md.disasm(code, start))
        max_tag = table_disp = None
        for i, ins in enumerate(insns):
            if ins.mnemonic != "cmp" or ", 0x" not in ins.op_str:
                continue
            window = insns[i : i + 12]
            joined = " | ".join(f"{x.mnemonic} {x.op_str}" for x in window)
            if "jmp" not in joined:
                continue
            try:
                imm = int(ins.op_str.split(",")[1].strip(), 16)
            except ValueError:
                continue
            for w in window:
                if w.mnemonic == "mov" and "dword ptr" in w.op_str and "*4" in w.op_str:
                    m = re.search(r"\+ (0x[0-9a-f]+|\d+)\]", w.op_str)
                    if m:
                        disp = m.group(1)
                        table_disp = int(disp, 16) if disp.startswith("0x") else int(disp)
                        max_tag = imm
                        break
            if max_tag is not None:
                break
        if max_tag is None or table_disp is None:
            raise RuntimeError(f"jumptable not found in {start:#x}-{end:#x}")
        table_va = IMAGE_BASE + table_disp
        entries = [
            struct.unpack_from("<I", ga, va_to_off(sections, table_va) + i * 4)[0]
            for i in range(max_tag + 1)
        ]
        out = {}
        evidence = {}
        skipped = []
        for tag, rel in enumerate(entries):
            cva = IMAGE_BASE + rel
            if follow:
                cva = follow_jmps(cva)
            ti = find_typeinfo_index(cva)
            if ti is None:
                skipped.append(tag)
                continue
            td = typedef_from_type_index(ti)
            name = map_name(td, prefer_full=prefer_full)
            if name:
                out[str(tag)] = name
                evidence[str(tag)] = {
                    "name": name,
                    "target": td_to_target.get(td),
                    "wrapper": td_to_wrapper.get(td),
                    "typedef": td,
                    "type_index": ti,
                    "case_va": hex(cva),
                }
            else:
                skipped.append(tag)
        return out, max_tag, evidence, skipped

    selected = args.only or list(UNIONS.keys())
    combined = {}
    notes = {}
    full = {}
    for name in selected:
        if name not in UNIONS:
            raise SystemExit(f"unknown union {name}; known={list(UNIONS)}")
        start, end, follow, prefer_full = UNIONS[name]
        mapping, max_tag, evidence, skipped = extract(start, end, follow, prefer_full)
        combined[name] = mapping
        notes[name] = {
            "max_tag": max_tag,
            "resolved": len(mapping),
            "skipped": skipped,
            "follow_jmps": follow,
            "prefer_full": prefer_full,
            "range": [hex(start), hex(end)],
        }
        full[name] = evidence
        print(f"{name}: {len(mapping)}/{max_tag+1} skipped={len(skipped)}")

    # Data alias with prefer_full names from AbilityActionData jumptable
    if DATA_ALIAS_OF in combined or DATA_ALIAS_OF in UNIONS:
        start, end, follow, _ = UNIONS[DATA_ALIAS_OF]
        data_map, max_tag, evidence, skipped = extract(start, end, follow, prefer_full=True)
        combined["Data"] = data_map
        notes["Data"] = {
            "max_tag": max_tag,
            "resolved": len(data_map),
            "skipped": skipped,
            "follow_jmps": follow,
            "prefer_full": True,
            "alias_of": DATA_ALIAS_OF,
            "range": [hex(start), hex(end)],
        }
        full["Data"] = evidence
        print(f"Data (alias {DATA_ALIAS_OF}, prefer_full): {len(data_map)}/{max_tag+1}")

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "union-map.json").write_text(
        json.dumps(combined, indent=2, ensure_ascii=False) + "\n"
    )
    (out_dir / "union-map.full.json").write_text(
        json.dumps(full, indent=2, ensure_ascii=False) + "\n"
    )
    (out_dir / "NOTES.json").write_text(json.dumps(notes, indent=2, ensure_ascii=False) + "\n")
    # Keep split files for AbilityAction / ActionBase
    if "AbilityActionData" in combined:
        (out_dir / "ability-action-union-map.json").write_text(
            json.dumps({"AbilityActionData": combined["AbilityActionData"]}, indent=2, ensure_ascii=False)
            + "\n"
        )
    if "ActionBase" in combined:
        (out_dir / "action-base-union-map.json").write_text(
            json.dumps({"ActionBase": combined["ActionBase"]}, indent=2, ensure_ascii=False) + "\n"
        )
    print(f"wrote {out_dir / 'union-map.json'} keys={list(combined)}")


if __name__ == "__main__":
    main()
