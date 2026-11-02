"""Mod colour files brought to the current type signatures without Ryu's Outfit 1 colours (1.10.8).

The current signatures come from the game's esf001_001_cmd_000.user.2. Sakura as Ryu installed by
the external mod manager replaces it, which zeroes the hash of its entry in the base pak: nothing was
read, no mod colour file was brought up to date and Ingrid's extra outfits never finished loading.
Synthetic fixture game (fluffy_coexistence's) whose extra outfit ships a colour file with the old
MaterialData signature; actual loader.

  a  reference present                         -> brought up to date, as before
  b  reference turned off (sorted table)        -> read from its turned-off entry, brought up to date
  c  reference gone, another fighter's present  -> signatures from that one, brought up to date
  d  no game colour file at all                 -> a warning, the file is left as it is
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct
import tempfile

import fluffy_coexistence as support
from loose_colours import write_rows

ROOT = Path(__file__).resolve().parents[1]
MATERIAL, CLOTH = 0x9E32F1F4, 0x9F67268B
OLD, CURRENT = 0x4D532A61, 0x66C86E7A
REFERENCE = "natives/stm/product/model/esf/esf001/001/esf001_001_cmd_000.user.2"
OTHER = "natives/stm/product/model/esf/esf003/001/esf003_001_cmd_000.user.2"
UPGRADED = "1 mod files brought to the current type signatures"
TURNED_OFF = "read from its turned-off entry"
FALLBACK = "signatures read from " + OTHER
WARN = "WARN: no game colour file could be read"


def usr(*type_crc: int) -> bytes:
    """A user file holding only an instance table (ReplaceChecks.Usr)."""
    ic = len(type_crc) // 2 + 1
    r, inst = 48, 52
    data_off = (inst + 8 * ic + 15) // 16 * 16
    b = bytearray(r + data_off + 16)
    b[0:3] = b"USR"
    struct.pack_into("<Q", b, 32, r)
    b[r:r + 3] = b"RSZ"
    struct.pack_into("<III8xQQQI", b, r + 4, 16, 1, ic, inst, data_off, data_off, 1)
    for i in range(1, ic):
        struct.pack_into("<II", b, r + inst + 8 * i, type_crc[2 * (i - 1)], type_crc[2 * (i - 1) + 1])
    return bytes(b)


def make(game: Path, reference: str, other: bool) -> None:
    """reference: present, off (hash zeroed in a sorted table) or gone."""
    support.fixture(game)
    mod_cmd = game / "reframework/costume_mods/Luke/ManualFixture" / support.CMD_PRESENT
    mod_cmd.parent.mkdir(parents=True, exist_ok=True)
    mod_cmd.write_bytes(usr(MATERIAL, OLD, CLOTH, 0x11111111))
    base = game / "re_chunk_000.pak"
    rows = list(support.read_pak(base).items())
    ref = support.path_hash(REFERENCE)
    if reference != "gone":
        rows.append((ref, usr(MATERIAL, CURRENT, CLOTH, 0x11111111)))
    if other:
        rows.append((support.path_hash(OTHER), usr(MATERIAL, CURRENT, CLOTH, 0x11111111)))
    rows.sort(key=lambda r: r[0])
    write_rows(base, [(0 if reference == "off" and h == ref else h, d) for h, d in rows])


def run(game: Path, exe: Path, log_to: Path) -> tuple[str, list[bytes]]:
    log = support.run_loader(exe, game)
    log_to.write_text(log, encoding="utf-8")
    return log, list(support.read_pak(support.patch_file(game)).values())


def signature(entries: list[bytes], crc: int) -> bool:
    return any(d[:3] == b"USR" and struct.pack("<II", MATERIAL, crc) in d for d in entries)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    ap.add_argument("--old-loader", type=Path, help="previous build: cases b and c keep the old signature")
    args = ap.parse_args()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    run_dir = Path(tempfile.mkdtemp(prefix="colour-ref-", dir=runs))
    checks = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(ok), "detail": detail})
        print(f"{'PASS' if ok else 'FAIL'} {label}" + (f": {detail}" if detail else ""))

    cases = {"a": ("present", False), "b": ("off", False), "c": ("gone", True), "d": ("gone", False)}
    results = {}
    for name, (reference, other) in cases.items():
        game = run_dir / name
        make(game, reference, other)
        results[name] = run(game, args.loader.resolve(), run_dir / f"{name}.log")

    log, entries = results["a"]
    check("a: reference present: the mod colour file is brought up to date",
          UPGRADED in log and signature(entries, CURRENT) and not signature(entries, OLD))
    check("a: no fallback line", TURNED_OFF not in log and FALLBACK not in log and WARN not in log)
    log, entries = results["b"]
    check("b: reference turned off: read from its turned-off entry", TURNED_OFF in log and REFERENCE in log)
    check("b: the mod colour file is brought up to date",
          UPGRADED in log and signature(entries, CURRENT) and not signature(entries, OLD))
    log, entries = results["c"]
    check("c: reference gone: another fighter's colours give the signatures", FALLBACK in log)
    check("c: the mod colour file is brought up to date",
          UPGRADED in log and signature(entries, CURRENT) and not signature(entries, OLD))
    log, entries = results["d"]
    check("d: no game colour file: warned, the file keeps its signature",
          WARN in log and UPGRADED not in log and signature(entries, OLD))

    if args.old_loader:
        for name in ("b", "c"):
            game = run_dir / f"old-{name}"
            make(game, *cases[name])
            log, entries = run(game, args.old_loader.resolve(), run_dir / f"old-{name}.log")
            check(f"previous build: case {name} keeps the old signature",
                  UPGRADED not in log and signature(entries, OLD))

    (run_dir / "results.json").write_text(json.dumps(checks, indent=2))
    failed = [c for c in checks if not c["passed"]]
    print(f"\n{len(checks) - len(failed)}/{len(checks)} passed ({run_dir})")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
