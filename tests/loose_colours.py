"""Colour files taken from the game folder (1.9.8): fixture checks of the actual loader.

When the colour list of an outfit names a colour file that the game's PAKs no longer
have (Fluffy Mod Manager, or SlotsLoader Manager's PAK method, zeroed its entry and
installed the replacement as a loose file in the game's natives folder), the slot
takes that loose file. Without one, another colour of the outfit stands in. A colour
whose PAK entry is still there keeps the game's original, loose file or not.

Cases (each a synthetic game made by fluffy_coexistence.fixture, then changed):
  a  zeroed entry + loose colour      -> the slot has the loose bytes, logged
  b  zeroed entry, no loose file      -> another colour stands in (as in 1.9.7)
  c  PAK entry present + loose file   -> the slot has the PAK original
  d  the cache: unchanged -> "Up to date"; loose content changed (same size and
     date), removed, added back, the entry restored, zeroed again with a loose file
     present, an unrelated entry zeroed -> rebuilt or not as each requires
  e  a game folder of about 170 characters and a long colour file name: the loose
     path is past MAX_PATH

The game folders of a-d are 49 characters long, the length of the game folder
this was checked against (a Steam library on another drive), when --root is
short enough, else they are longer. Package and cache checks only: no game is run.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import struct
import sys
import tempfile

from fluffy_coexistence import (CCVD, CMD_MISSING, CMD_PRESENT, MESH, MODEL_ROOT, ROOT,
                                SAFE_COLOR, fixture, patch_file, path_hash, read_pak, run_loader)

REAL_GAME_LEN = 49   # characters in the path of the game folder this was checked against
ORIGINAL = b"ORIGINAL_PAK_COLOR_002__"            # the game's own colour 002 (24 bytes)
LOOSE_A = b"LOOSE_GAME_COLOR_A______"             # same size as LOOSE_B: a same-size edit
LOOSE_B = b"LOOSE_GAME_COLOR_B______"
LONG_CMD = MODEL_ROOT + "esf002_003_cmd_002_" + "long_colour_name_" * 6 + "x.user.2"


def ext(path: Path) -> str:
    """The path for Python's file calls, past MAX_PATH too."""
    s = str(path.resolve() if not str(path).startswith("\\\\?\\") else path)
    return s if s.startswith("\\\\?\\") or len(s) < 240 else "\\\\?\\" + s


def write_rows(path: Path, rows: list[tuple[int, bytes]]) -> None:
    offset = 16 + 48 * len(rows)
    table, payload = [], []
    for hashed, data in rows:
        table.append(struct.pack("<IIqqqqq", hashed & 0xFFFFFFFF, hashed >> 32,
                                 offset, len(data), len(data), 0, 0))
        payload.append(data)
        offset += len(data)
    Path(ext(path)).write_bytes(struct.pack("<IBBhII", 0x414B504B, 4, 0, 0, len(rows), 0)
                                + b"".join(table) + b"".join(payload))


def ccvd(names: list[str]) -> bytes:
    return ("\0" + "\0".join(n.removeprefix("natives/stm/").rsplit(".", 1)[0] for n in names)
            + "\0").encode("utf-16le")


class Game:
    """A fixture game whose base PAK has a colour entry that can be zeroed and restored."""

    def __init__(self, game: Path, colour: str = CMD_MISSING):
        self.game, self.colour = game, colour
        fixture(game)
        base = game / "re_chunk_000.pak"
        self.rows = list(read_pak(Path(ext(base))).items())
        if colour != CMD_MISSING:   # the colour list names the long colour file instead
            self.rows = [(h, ccvd([CMD_PRESENT, colour]) if h == path_hash(CCVD) else d)
                         for h, d in self.rows]
        self.rows.append((path_hash(colour), ORIGINAL))
        self.zeroed: set[int] = set()
        # fixture() leaves its own loose colour 002; each case puts its own
        self.set_loose(None, CMD_MISSING)
        self.write_base()

    @property
    def base(self) -> Path:
        return self.game / "re_chunk_000.pak"

    def loose_path(self, colour: str | None = None) -> Path:
        return Path(ext(self.game)) / (colour or self.colour)

    def write_base(self) -> None:
        write_rows(self.base, [(0 if h in self.zeroed else h, d) for h, d in self.rows])

    def zero(self, *paths: str) -> None:
        self.zeroed |= {path_hash(p) for p in paths}
        self.write_base()

    def restore(self, *paths: str) -> None:
        self.zeroed -= {path_hash(p) for p in paths}
        self.write_base()

    def set_loose(self, data: bytes | None, colour: str | None = None,
                  keep_time: bool = False) -> None:
        p = self.loose_path(colour)
        if data is None:
            if p.exists():
                p.unlink()
            return
        times = None
        if keep_time and p.exists():
            st = p.stat()
            times = (st.st_atime_ns, st.st_mtime_ns)
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
        if times:
            os.utime(p, ns=times)

    def run(self, exe: Path) -> tuple[str, dict[int, bytes], str]:
        log = run_loader(exe, self.game)
        pak = patch_file(Path(ext(self.game)))
        entries = read_pak(pak)
        reg = json.loads((Path(ext(self.game)) / "reframework/data/SF6_Costumes_Data/registry.json").read_text())
        folder = reg["slots"][0]["new_folder"] if reg["slots"] else None
        return log, entries, folder

    def slot_colour(self, entries: dict[int, bytes], folder: str | None) -> bytes | None:
        if folder is None:
            return None
        return entries.get(path_hash(self.colour.replace("/003/", f"/{folder}/")))


def game_dir(root: Path, name: str, length: int) -> Path:
    """root/<name padded> with the whole path `length` characters long when possible."""
    base = str(root.resolve()) + "\\" + name
    pad = length - len(base)
    return Path(base + ("_" + "p" * (pad - 1) if pad > 1 else ""))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--old-loader", type=Path,
                        help="previous build: case a must give the stand-in colour there")
    parser.add_argument("--root", type=Path, help="new folder for the fixture games (default: tests/.runs)")
    parser.add_argument("--keep", action="store_true", help="keep the fixture games")
    args = parser.parse_args()
    exe = args.loader.resolve()
    if args.root:
        root = args.root.resolve()
        if root.exists():
            parser.error("--root must be a new folder")
        root.mkdir(parents=True)
    else:
        runs = ROOT / "tests/.runs"
        runs.mkdir(exist_ok=True)
        root = Path(tempfile.mkdtemp(prefix="loose-", dir=runs))
    checks, logs = [], []

    def check(label: str, condition: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(condition), "detail": detail})
        print(f"{'PASS' if condition else 'FAIL'} {label}" + (f": {detail}" if detail else ""))

    def logged(log: str, path: Path) -> bool:
        return any("taken from the game folder" in line and str(path).removeprefix("\\\\?\\") in line
                   for line in log.splitlines())

    lens = []
    # (a) zeroed entry + loose colour
    a = Game(game_dir(root, "a", REAL_GAME_LEN))
    lens.append(len(str(a.game)))
    a.zero(CMD_MISSING)
    a.set_loose(LOOSE_A)
    log, entries, folder = a.run(exe)
    logs.append(("a-initial", log))
    check("a: slot made", folder is not None)
    check("a: zeroed entry + loose colour -> slot has the loose bytes",
          a.slot_colour(entries, folder) == LOOSE_A, repr(a.slot_colour(entries, folder)))
    check("a: one log line names the loose file taken from the game folder",
          logged(log, a.loose_path()), next((l for l in log.splitlines() if "colour file" in l), ""))
    check("a: nothing written at the original colour path", path_hash(CMD_MISSING) not in entries)
    check("a: the loose source file is unchanged", a.loose_path().read_bytes() == LOOSE_A)
    tsv = Path(ext(a.game)) / "reframework/data/SF6_Costumes_Data/game_colours.tsv"
    check("a: the loose colour is recorded with its state", tsv.exists()
          and any(l.startswith("loose:") and l.endswith(CMD_MISSING) for l in tsv.read_text().splitlines()),
          tsv.read_text() if tsv.exists() else "missing")

    # (d) the cache, on game a
    def rerun(label: str, expect_rebuild: bool, expect: bytes | None) -> None:
        log, entries, folder = a.run(exe)
        logs.append(("d-" + label, log))
        up = "Up to date" in log
        check(f"d: {label} -> {'rebuilt' if expect_rebuild else 'Up to date'}",
              up != expect_rebuild, log.splitlines()[-1] if log else "")
        if expect is not None:
            got = a.slot_colour(entries, folder)
            check(f"d: {label} -> slot colour {expect.decode().strip('_')}", got == expect, repr(got))

    rerun("unchanged", False, LOOSE_A)
    a.set_loose(LOOSE_B, keep_time=True)
    rerun("loose content changed, same size and date", True, LOOSE_B)
    rerun("unchanged again", False, LOOSE_B)
    a.zero(MESH)
    rerun("unrelated entry zeroed", False, LOOSE_B)
    a.restore(MESH)
    rerun("unrelated entry restored", False, LOOSE_B)
    a.set_loose(None)
    rerun("loose file removed", True, SAFE_COLOR)
    a.set_loose(LOOSE_A)
    rerun("loose file added back", True, LOOSE_A)
    a.set_loose(None)
    a.restore(CMD_MISSING)
    rerun("Fluffy uninstall: entry restored, loose file removed", True, ORIGINAL)
    rerun("unchanged after uninstall", False, ORIGINAL)
    a.set_loose(LOOSE_B)
    a.zero(CMD_MISSING)
    rerun("Fluffy install after the build: entry zeroed, loose file added", True, LOOSE_B)

    # (b) zeroed entry, no loose file
    b = Game(game_dir(root, "b", REAL_GAME_LEN))
    lens.append(len(str(b.game)))
    b.zero(CMD_MISSING)
    log, entries, folder = b.run(exe)
    logs.append(("b-initial", log))
    check("b: zeroed entry, no loose file -> another colour stands in",
          b.slot_colour(entries, folder) == SAFE_COLOR, repr(b.slot_colour(entries, folder)))
    check("b: the stand-in is logged", "esf002_003_cmd_001.user.2 stands in for it" in log)
    b.set_loose(LOOSE_A)
    log, entries, folder = b.run(exe)
    logs.append(("b-loose-appears", log))
    check("b: a loose file that appears later -> rebuilt with it",
          "Up to date" not in log and b.slot_colour(entries, folder) == LOOSE_A,
          repr(b.slot_colour(entries, folder)))

    # (c) PAK entry present + loose file
    c = Game(game_dir(root, "c", REAL_GAME_LEN))
    lens.append(len(str(c.game)))
    c.set_loose(LOOSE_A)
    log, entries, folder = c.run(exe)
    logs.append(("c-initial", log))
    check("c: PAK entry present + loose file -> slot has the PAK original",
          c.slot_colour(entries, folder) == ORIGINAL, repr(c.slot_colour(entries, folder)))
    check("c: no colour taken from the game folder", "taken from the game folder" not in log)
    c.set_loose(LOOSE_B)
    log, entries, folder = c.run(exe)
    logs.append(("c-loose-changed", log))
    check("c: a loose file changing under a present entry does not rebuild",
          "Up to date" in log and c.slot_colour(entries, folder) == ORIGINAL)

    # (e) long game folder, long colour file name
    e = Game(game_dir(root, "e", 170), LONG_CMD)
    lens.append(len(str(e.game)))
    e.zero(LONG_CMD)
    e.set_loose(LOOSE_A)
    loose_len = len(str(e.loose_path()).removeprefix("\\\\?\\"))
    log, entries, folder = e.run(exe)
    logs.append(("e-initial", log))
    check(f"e: loose colour at a {loose_len}-character path -> slot has its bytes",
          loose_len > 260 and e.slot_colour(entries, folder) == LOOSE_A, repr(e.slot_colour(entries, folder)))
    check("e: logged with its long path", logged(log, e.loose_path()))
    log, entries, folder = e.run(exe)
    logs.append(("e-unchanged", log))
    check("e: unchanged -> Up to date", "Up to date" in log)
    e.set_loose(LOOSE_B, keep_time=True)
    log, entries, folder = e.run(exe)
    logs.append(("e-changed", log))
    check("e: long-path loose content changed -> rebuilt",
          "Up to date" not in log and e.slot_colour(entries, folder) == LOOSE_B)

    if args.old_loader:
        o = Game(game_dir(root, "o", REAL_GAME_LEN))
        o.zero(CMD_MISSING)
        o.set_loose(LOOSE_A)
        log, entries, folder = o.run(args.old_loader.resolve())
        logs.append(("old-loader", log))
        check("previous build: zeroed entry + loose colour -> stand-in colour",
              o.slot_colour(entries, folder) == SAFE_COLOR, repr(o.slot_colour(entries, folder)))

    for label, content in logs:
        name = "".join(ch if ch.isalnum() else "-" for ch in label.lower()).strip("-")
        (root / (name + ".txt")).write_text(content, encoding="utf-8")
    result = {"loader": str(exe), "root": str(root), "game_path_lengths": lens,
              "scope": "native loader package/cache logic; no game runtime validation",
              "checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks)}
    (root / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"game folder lengths {lens} (real game: {REAL_GAME_LEN})")
    print(f"{result['passed']}/{result['total']} passed; report: {root / 'results.json'}")
    if not args.keep:
        for g in root.iterdir():
            if g.is_dir():
                shutil.rmtree(ext(g))
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    sys.exit(main())
