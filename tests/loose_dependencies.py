"""Mod-only dependencies and loose game files (1.10.9): fixture checks of the actual loader.

An extra outfit's file at a shared custom path (not its slot's folder, not a vanilla asset:
Sakura as Ryu's DriveTech mesh esf001/005/00, 04/11) used to be written into the generated
PAK at that path even when a replacement had put the same path in the game folder as a
loose file. The generated PAK then covered the replacement for as long as the outfit was
installed, and SlotsLoader Manager refused every change of that replacement. Since 1.10.9
such a path is left to the loose file, and its state is probed so a replacement installed,
changed or removed there rebuilds the PAK.

Cases (fluffy_coexistence.fixture: its manual outfit ships DEPENDENCY, a mod-only texture):
  a  no loose file                      -> the PAK has the outfit's copy, probed as "none"
  b  the replacement installed after it -> rebuilt, the PAK leaves the path to the loose file
  c  unchanged / loose content changed (same size and date) / removed / added back
                                         -> Up to date / rebuilt / outfit's copy back / left again
  d  the loose file there before the first build -> never in the PAK
  outfits.json says loose_files_kept (SlotsLoader Manager reads it); the slot is kept throughout.
With --old-loader (1.10.8) case b keeps the outfit's copy and stays "Up to date".
Package and cache checks only: no game is run.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile

from fluffy_coexistence import DEPENDENCY, ROOT, fixture, patch_file, path_hash, read_pak, run_loader

REPLACEMENT_A = b"LOOSE_REPLACEMENT_COPY_A_" * 4    # same size as B: a same-size edit
REPLACEMENT_B = b"LOOSE_REPLACEMENT_COPY_B_" * 4


class Game:
    def __init__(self, game: Path):
        self.game = game
        self.supplied = fixture(game)

    @property
    def loose(self) -> Path:
        return self.game / DEPENDENCY

    def set_loose(self, data: bytes | None, keep_time: bool = False) -> None:
        if data is None:
            if self.loose.exists():
                self.loose.unlink()
            return
        times = None
        if keep_time and self.loose.exists():
            st = self.loose.stat()
            times = (st.st_atime_ns, st.st_mtime_ns)
        self.loose.parent.mkdir(parents=True, exist_ok=True)
        self.loose.write_bytes(data)
        if times:
            os.utime(self.loose, ns=times)

    def run(self, exe: Path) -> tuple[str, dict[int, bytes]]:
        log = run_loader(exe, self.game)
        return log, read_pak(patch_file(self.game))

    def data(self, name: str) -> Path:
        return self.game / "reframework/data/SF6_Costumes_Data" / name


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--old-loader", type=Path, help="previous build: case b keeps the outfit's copy there")
    parser.add_argument("--keep", action="store_true", help="keep the fixture games")
    args = parser.parse_args()
    exe = args.loader.resolve()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="deps-", dir=runs))
    checks, logs = [], []

    def check(label: str, condition: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(condition), "detail": detail})
        print(f"{'PASS' if condition else 'FAIL'} {label}" + (f": {detail}" if detail else ""))

    dep = path_hash(DEPENDENCY)
    a = Game(root / "a")
    outfit_copy = a.supplied[DEPENDENCY]

    def rerun(label: str, rebuilt: bool, expect_in_pak: bool) -> None:
        log, entries = a.run(exe)
        logs.append((label, log))
        check(f"{label} -> {'rebuilt' if rebuilt else 'Up to date'}", ("Up to date" in log) != rebuilt,
              log.splitlines()[-1] if log else "")
        got = entries.get(dep)
        check(f"{label} -> {'outfit copy in the PAK' if expect_in_pak else 'path left to the loose file'}",
              got == outfit_copy if expect_in_pak else got is None, repr(got)[:60])
        check(f"{label} -> the slot is kept", "0 left out" in log or "Up to date" in log)

    # (a) no loose file
    log, entries = a.run(exe)
    logs.append(("a-initial", log))
    check("a: no loose file -> the outfit's copy is in the PAK", entries.get(dep) == outfit_copy)
    probes = a.data("game_colours.tsv").read_text() if a.data("game_colours.tsv").exists() else ""
    check("a: the dependency is probed as none", f"none\t{DEPENDENCY}" in probes.splitlines(), probes)
    report = json.loads(a.data("outfits.json").read_text())
    check("a: outfits.json says loose_files_kept", report.get("loose_files_kept") is True, str(report.get("loader")))
    # (b) a replacement puts the path in the game folder after the build
    a.set_loose(REPLACEMENT_A)
    rerun("b: replacement installed after the build", True, False)
    check("b: the log names the path left to the loose file",
          any("left to the game folder's loose file" in l and DEPENDENCY in l for l in logs[-1][1].splitlines()))
    check("b: the loose file is unchanged", a.loose.read_bytes() == REPLACEMENT_A)
    # (c) the cache
    rerun("c: unchanged", False, False)
    a.set_loose(REPLACEMENT_B, keep_time=True)
    rerun("c: replacement updated (same size and date)", True, False)
    rerun("c: unchanged again", False, False)
    a.set_loose(None)
    rerun("c: replacement uninstalled", True, True)
    rerun("c: unchanged after uninstall", False, True)
    a.set_loose(REPLACEMENT_A)
    rerun("c: replacement installed again", True, False)

    # (d) the loose file is there before the first build
    d = Game(root / "d")
    d.set_loose(REPLACEMENT_A)
    log, entries = d.run(exe)
    logs.append(("d-initial", log))
    check("d: loose file before the first build -> never in the PAK", dep not in entries and "0 left out" in log)

    if args.old_loader:
        o = Game(root / "o")
        log, entries = o.run(args.old_loader.resolve())
        o.set_loose(REPLACEMENT_A)
        log, entries = o.run(args.old_loader.resolve())
        logs.append(("old-loader", log))
        check("previous build: the outfit's copy covers the replacement and the cache is not rebuilt",
              entries.get(dep) == outfit_copy and "Up to date" in log)

    for label, content in logs:
        name = "".join(ch if ch.isalnum() else "-" for ch in label.lower()).strip("-")
        (root / (name + ".txt")).write_text(content, encoding="utf-8")
    result = {"loader": str(exe), "root": str(root), "scope": "native loader package/cache logic; no game runtime validation",
              "checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks)}
    (root / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {root / 'results.json'}")
    if not args.keep:
        for g in root.iterdir():
            if g.is_dir():
                shutil.rmtree(g)
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    sys.exit(main())
