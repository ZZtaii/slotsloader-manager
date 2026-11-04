"""Shared custom paths the slot check resolves (1.10.9, from the PR #26 review): fixture checks of the loader.

The outfit's material file binds a texture at a shared custom path (not its slot's folder, not a
vanilla asset). 1.10.9 says such a path is never written over a loose game file and is probed so a
replacement installed or removed there rebuilds the PAK.

  s  short game folder, the outfit ships the texture, a loose file is there -> never in the PAK
  l  the same with a long game folder (path past MAX_PATH)                 -> never in the PAK
  p  the outfit does not ship the texture, a loose file supplies it at the first build, then it is
     removed                                                                -> rebuilt
  q  the same, no loose file at first (probed as missing), then added, then removed -> rebuilt each time
Package and cache checks only: no game is run.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import struct
import sys
import tempfile

from fluffy_coexistence import DEPENDENCY, MDF, ROOT, fixture, patch_file, path_hash, read_pak, run_loader, texture

SHARED_ONLY = "natives/stm/product/model/esf/esf002/001/01/shared_only.tex.241101895"
LOOSE = texture("LOOSE_REPLACEMENT_COPY")


def ext(path: Path) -> str:
    s = str(path.resolve() if not str(path).startswith("\\\\?\\") else path)
    return s if s.startswith("\\\\?\\") or len(s) < 240 else "\\\\?\\" + s


def mdf_binding(tex_pak_path: str) -> bytes:
    """fluffy_coexistence.mdf() with one bound texture (mdf2_texture_paths layout)."""
    ref = tex_pak_path.removeprefix("natives/stm/").rsplit(".", 1)[0]
    data = bytearray(160)
    data[:4] = b"MDF\0"
    struct.pack_into("<h", data, 6, 1)
    struct.pack_into("<q", data, 16, 116)
    name = "fixture_mat\0".encode("utf-16le")
    data[116:116 + len(name)] = name
    table = len(data)
    data.extend(bytes(32))
    string_at = len(data)
    data.extend((ref + "\0").encode("utf-16le"))
    struct.pack_into("<i", data, 16 + 20, 1)            # texture count
    struct.pack_into("<Q", data, 16 + 60, table)        # texture table
    struct.pack_into("<Q", data, table + 16, string_at)  # path of texture 0
    return bytes(data)


def game_dir(root: Path, name: str, length: int) -> Path:
    base = str(root.resolve()) + "\\" + name
    pad = length - len(base)
    return Path(base + ("_" + "p" * (pad - 1) if pad > 1 else ""))


class Game:
    def __init__(self, game: Path, tex: str, ship: bool):
        self.game, self.tex = game, tex
        supplied = fixture(game)
        manual = Path(ext(game / "reframework/costume_mods/Luke/ManualFixture"))
        (manual / MDF).write_bytes(mdf_binding(tex))
        if not ship and tex in supplied:
            (manual / tex).unlink()
        self.outfit_copy = supplied.get(tex)

    @property
    def loose(self) -> Path:
        return Path(ext(self.game)) / self.tex

    def set_loose(self, data: bytes | None) -> None:
        if data is None:
            if self.loose.exists():
                self.loose.unlink()
            return
        self.loose.parent.mkdir(parents=True, exist_ok=True)
        self.loose.write_bytes(data)

    def run(self, exe: Path) -> tuple[str, dict[int, bytes]]:
        log = run_loader(exe, self.game)
        return log, read_pak(patch_file(Path(ext(self.game))))

    def probes(self) -> str:
        p = Path(ext(self.game)) / "reframework/data/SF6_Costumes_Data/game_colours.tsv"
        return p.read_text() if p.exists() else ""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--keep", action="store_true")
    args = parser.parse_args()
    exe = args.loader.resolve()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="shared-", dir=runs))
    checks, logs = [], []

    def check(label: str, condition: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(condition), "detail": detail})
        print(f"{'PASS' if condition else 'FAIL'} {label}" + (f": {detail}" if detail else ""))

    # 190: the loader still reads its index (game folder + 68 characters), the loose texture is at 262
    for name, length in (("s", 49), ("l", 190)):
        g = Game(game_dir(root, name, length), DEPENDENCY, True)
        g.set_loose(LOOSE)
        log, entries = g.run(exe)
        logs.append((name, log))
        full = len(str(g.loose).removeprefix("\\\\?\\"))
        got = entries.get(path_hash(DEPENDENCY))
        check(f"{name}: game folder {len(str(g.game))} chars, loose file path {full} chars -> the outfit's copy is not in the PAK",
              got is None, "PAK has " + ("nothing" if got is None else "the outfit's copy" if got == g.outfit_copy else repr(got[:16])))
        check(f"{name}: the slot is kept", "0 left out" in log)
        check(f"{name}: the loose file is unchanged", g.loose.read_bytes() == LOOSE)

    p = Game(game_dir(root, "p", 49), SHARED_ONLY, False)
    p.set_loose(LOOSE)
    log, entries = p.run(exe)
    logs.append(("p-initial", log))
    check("p: a loose file supplies the shared texture -> the slot is kept", "0 left out" in log)
    check("p: the shared texture is probed", SHARED_ONLY in p.probes(), p.probes().replace("\n", " | "))
    p.set_loose(None)
    log, entries = p.run(exe)
    logs.append(("p-removed", log))
    check("p: the loose texture removed -> rebuilt", "Up to date" not in log, log.splitlines()[-1] if log else "")

    # (q) the same texture, no loose file at first: probed while missing, dropped once a loose file supplies it
    q = Game(game_dir(root, "q", 49), SHARED_ONLY, False)
    log, _ = q.run(exe)
    logs.append(("q-initial", log))
    check("q: missing shared texture -> probed", f"none\t{SHARED_ONLY}" in q.probes().splitlines())
    q.set_loose(LOOSE)
    log, _ = q.run(exe)
    logs.append(("q-added", log))
    check("q: a loose file supplies it -> rebuilt", "Up to date" not in log)
    check("q: still probed after that build", SHARED_ONLY in q.probes(), "probes: " + q.probes().replace("\n", " | "))
    q.set_loose(None)
    log, _ = q.run(exe)
    logs.append(("q-removed", log))
    check("q: the loose file removed again -> rebuilt", "Up to date" not in log, log.splitlines()[-1] if log else "")

    for label, content in logs:
        (root / (label + ".txt")).write_text(content, encoding="utf-8")
    result = {"loader": str(exe), "root": str(root), "checks": checks,
              "passed": sum(c["passed"] for c in checks), "total": len(checks)}
    (root / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {root / 'results.json'}")
    if not args.keep:
        for g in root.iterdir():
            if g.is_dir():
                shutil.rmtree(ext(g))
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    sys.exit(main())
