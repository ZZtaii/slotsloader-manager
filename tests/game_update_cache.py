"""Test update-aware native loader caching using generated workspace fixtures.

Optional --old-loader reproduces stale cache reuse by the previous loader for
each changed game/data input. No installed game, library or user mod is read.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import tempfile

import fluffy_coexistence as support


ROOT = Path(__file__).resolve().parents[1]
DATA = Path("reframework/data/SF6_Costumes_Data")
STATIC = DATA / "loader/static"
INVENTORY = DATA / "loader/vanilla_costume_index.tsv"


def signature(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def replace(path: Path, data: bytes, preserve_time: bool = False) -> None:
    stamp = path.stat()
    path.write_bytes(data)
    os.utime(path, ns=(stamp.st_atime_ns, stamp.st_mtime_ns
                      if preserve_time else stamp.st_mtime_ns + 2_000_000_000))


def fingerprint(game: Path) -> str:
    return json.loads((game / DATA / "registry.json").read_text())["fingerprint"]


def sources(game: Path) -> dict[str, str]:
    root = game / "reframework/costume_mods"
    return {str(p.relative_to(root)): signature(p) for p in root.rglob("*") if p.is_file()}


def setup(game: Path) -> None:
    support.fixture(game)
    # A synthetic EXE-shaped prefix; this file is fingerprinted, never executed.
    (game / "StreetFighter6.exe").write_bytes(b"MZ" + bytes(8190))
    original = (game / STATIC / "esf.scn.20").read_bytes()
    (game / STATIC / "new.scn.20").write_bytes(original.replace(b"global", b"UPDATE"))


def change_exe_date(game: Path) -> None:
    path = game / "StreetFighter6.exe"
    data = bytearray(path.read_bytes())
    data[5000] = 1  # outside the prefix digest: detected by file timestamp
    replace(path, data)


def change_exe_header(game: Path) -> None:
    path = game / "StreetFighter6.exe"
    data = bytearray(path.read_bytes())
    data[128] = 1
    replace(path, data, preserve_time=True)


def change_base_index(game: Path) -> None:
    path = game / "re_chunk_000.pak"
    data = bytearray(path.read_bytes())
    for i in range(struct.unpack_from("<I", data, 8)[0]):
        low, high, offset, size = struct.unpack_from("<IIqq", data, 16 + 48 * i)
        if (high << 32) | low == support.path_hash(support.CMD_PRESENT):
            payload = b"UPDATE_OFFICIAL_COLOR_VALUE!!"
            payload = (payload + bytes(size))[:size]
            data[offset:offset + size] = payload
            struct.pack_into("<Q", data, 16 + 48 * i + 40, 123456789)
            replace(path, data, preserve_time=True)
            return
    raise AssertionError("fixture CMD entry absent")


def change_base_size(game: Path) -> None:
    path = game / "re_chunk_000.pak"
    replace(path, path.read_bytes() + b"UPDATED_GAME_ARCHIVE_TRAILER", preserve_time=True)


def invalidate_fluffy(game: Path) -> None:
    path = game / "re_chunk_000.pak"
    data = bytearray(path.read_bytes())
    struct.pack_into("<II", data, 16, 0, 0)
    replace(path, data)


def restore_fluffy(game: Path) -> None:
    path = game / "re_chunk_000.pak"
    data = bytearray(path.read_bytes())
    hashed = support.path_hash(support.MESH)
    struct.pack_into("<II", data, 16, hashed & 0xFFFFFFFF, hashed >> 32)
    replace(path, data)


def change_inventory(game: Path) -> None:
    path = game / INVENTORY
    # The manual-only dependency becomes a known original: rebuild must omit it.
    row = f"{support.path_hash(support.DEPENDENCY):016X}\t2\t2\tfixture\t{support.DEPENDENCY}\n"
    replace(path, path.read_bytes() + row.encode(), preserve_time=True)


def change_static(game: Path) -> None:
    path = game / STATIC / "fightercostumemessage.msg.21"
    replace(path, path.read_bytes().replace(b"global", b"UPDATE"), preserve_time=True)


def change_meta(game: Path) -> None:
    path = game / STATIC / "static_meta.json"
    before = path.read_bytes()
    after = before.replace(b'"local": "esf.scn.20"', b'"local": "new.scn.20"')
    assert before != after and len(before) == len(after)
    replace(path, after, preserve_time=True)


def change_dlc(game: Path) -> None:
    path = game / "dlc/fixture_dlc.pak"
    data = bytearray(path.read_bytes())
    data[-1] ^= 1
    replace(path, data)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--old-loader", type=Path)
    parser.add_argument("--output", type=Path, help="new directory beneath tests/.runs")
    args = parser.parse_args()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    if args.output:
        run = args.output.resolve()
        if not run.is_relative_to(runs.resolve()) or run.exists():
            parser.error("--output must be a new directory beneath this worktree's tests/.runs")
        run.mkdir(parents=True)
    else:
        run = Path(tempfile.mkdtemp(prefix="game-update-", dir=runs))
    args.loader = args.loader.resolve()
    current, previous = run / "current", run / "previous"
    setup(current)
    before_sources = sources(current)
    checks = []

    def check(name: str, passed: bool) -> None:
        checks.append({"name": name, "passed": bool(passed)})
        print(f"{'PASS' if passed else 'FAIL'} {name}")

    def launch(exe: Path, game: Path, label: str) -> str:
        log = support.run_loader(exe, game)
        (run / f"{game.name}-{label}.txt").write_text(log, encoding="utf-8")
        return log

    if args.old_loader:
        args.old_loader = args.old_loader.resolve()
        setup(previous)
        launch(args.old_loader, previous, "seed")
        launch(args.old_loader, current, "old-seed")
    log = launch(args.loader, current, "seed")
    check("initial load builds the fixture PAK", "Up to date" not in log)
    check("manual outfit validates", "1 slots checked, 0 left out" in log)
    if args.old_loader:
        check("loader upgrade replaces the prior build cache",
              "2026-10-18-1.10.4-slotsloader-fork" in fingerprint(current))
    slot_before = json.loads((current / DATA / "registry.json").read_text())["slots"]

    stages = [
        ("unchanged", lambda g: None, False),
        ("exe-date", change_exe_date, True),
        ("exe-header-same-stat", change_exe_header, True),
        ("base-checksum-same-stat", change_base_index, True),
        ("base-size", change_base_size, True),
        ("fluffy-invalidate", invalidate_fluffy, False),
        ("fluffy-restore", restore_fluffy, False),
        ("inventory", change_inventory, True),
        ("static-bytes-same-stat", change_static, True),
        ("static-meta-same-stat", change_meta, True),
        ("dlc-same-size", change_dlc, True),
        ("unused-static", lambda g: (g / STATIC / "unused.bin").write_bytes(b"unused"), False),
    ]
    for label, mutation, rebuild in stages:
        output = support.patch_file(current)
        old_fp, old_bytes, old_date = fingerprint(current), output.read_bytes(), output.stat().st_mtime_ns
        mutation(current)
        log = launch(args.loader, current, label)
        check(label + ": correct cache decision", ("Up to date" not in log) == rebuild)
        check(label + ": correct fingerprint decision", (fingerprint(current) != old_fp) == rebuild)
        if not rebuild:
            output = support.patch_file(current)
            check(label + ": cached PAK bytes and timestamp untouched",
                  output.read_bytes() == old_bytes and output.stat().st_mtime_ns == old_date)
        if rebuild:
            log = launch(args.loader, current, label + "-repeat")
            check(label + ": unchanged repeat reuses rebuilt cache", "Up to date" in log)
        if args.old_loader:
            mutation(previous)
            log = launch(args.old_loader, previous, label)
            check(label + (": prior build reproduces stale reuse" if rebuild else ": prior cache reused"),
                  "Up to date" in log)
        entries = support.read_pak(support.patch_file(current))
        if label == "base-checksum-same-stat":
            folder = slot_before[0]["new_folder"]
            # 1.9.8: was the stand-in for CMD_MISSING; that colour now comes from the fixture's
            # loose game-folder copy, so the refreshed bytes are checked on CMD_PRESENT itself
            private_cmd = support.CMD_PRESENT.replace("/003/", f"/{folder}/")
            check("base index change refreshes packaged colour bytes",
                  entries[support.path_hash(private_cmd)].startswith(b"UPDATE_OFFICIAL_COLOR"))
        if label == "inventory":
            check("inventory change removes newly protected original asset",
                  support.path_hash(support.DEPENDENCY) not in entries)
        if label == "static-bytes-same-stat":
            name = "fightercostumemessage.msg.21"
            check("changed static bytes reach generated PAK",
                  entries[support.path_hash(support.STRUCTURAL[name])] == (current / STATIC / name).read_bytes())
        if label == "static-meta-same-stat":
            check("changed static file mapping reaches generated PAK",
                  entries[support.path_hash(support.STRUCTURAL["esf.scn.20"])]
                  == (current / STATIC / "new.scn.20").read_bytes())

    check("all manual source files preserved", sources(current) == before_sources)
    check("slot assignments preserved across all updates",
          json.loads((current / DATA / "registry.json").read_text())["slots"] == slot_before)
    result = {"loader": str(args.loader), "old_loader": str(args.old_loader) if args.old_loader else None,
              "scope": "native cache and package validation; synthetic workspace inputs; no game rendering test",
              "checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks)}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
