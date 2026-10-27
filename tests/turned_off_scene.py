"""A mod without a scene over an outfit whose game scene a replacement turned off (1.10.6).

Ingrid C1 with its own scene installed as a replacement (Fluffy, or Slotsloader Manager's PAK
method, zeroes the hash of the game's entry) and a no-SCN Ingrid C1 as an extra outfit: the slot
was left out ("slot scene missing"). The game's tables are sorted by hash and a turned-off entry
keeps its row and its data, so the loader reads the game's scene from it. Synthetic fixture game
(fluffy_coexistence's, whose ManualFixture has no scene); actual loader.

  a  sorted table, scene zeroed                        -> slot built from the game's scene
  b  sorted, a zeroed decoy of another folder beside it -> still the game's scene
  c  sorted, a zeroed decoy of the same folder beside it -> ambiguous: left out, no guess
  d  table not sorted, scene zeroed                     -> left out, no guess
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import tempfile

import fluffy_coexistence as support
from loose_colours import write_rows

ROOT = Path(__file__).resolve().parents[1]
LEFT_OUT = "slot scene missing"
RECOVERED = "read from its turned-off entry"


def scene_of(folder: str) -> bytes:
    refs = [f"product/model/esf/esf002/{folder}/01/esf002_{folder}_01.mesh",
            f"product/model/esf/esf002/{folder}/01/esf002_{folder}_01_v00.mdf2"]
    return ("\0" + "\0".join(refs * 2) + "\0").encode("utf-16le")


def make(game: Path, decoy: str | None = None, ordered: bool = True) -> list[tuple[int, bytes]]:
    support.fixture(game)
    # the game's index names model folders from natives/ (fixture(): from product/)
    tsv = game / "reframework/data/SF6_Costumes_Data/loader/vanilla_costume_index.tsv"
    tsv.write_text(tsv.read_text().replace("\tproduct/model/", "\tnatives/stm/product/model/"))
    base = game / "re_chunk_000.pak"
    scene = support.path_hash(support.SCENE)
    rows = list(support.read_pak(base).items())
    if decoy:   # an entry whose hash sorts right after the scene's: the same gap once both are zeroed
        rows.append((scene + 1, scene_of(decoy)))
    rows.sort(key=lambda r: r[0], reverse=not ordered)
    write_rows(base, [(0 if h in (scene, scene + 1) else h, d) for h, d in rows])
    return rows


def run(game: Path, exe: Path, log_to: Path):
    log = support.run_loader(exe, game)
    log_to.write_text(log, encoding="utf-8")
    reg = json.loads((game / "reframework/data/SF6_Costumes_Data/registry.json").read_text())
    entries = support.read_pak(support.patch_file(game))
    return log, reg["slots"], entries


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    ap.add_argument("--old-loader", type=Path, help="previous build: case a is left out")
    args = ap.parse_args()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    run_dir = Path(tempfile.mkdtemp(prefix="scene-off-", dir=runs))
    checks = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(ok), "detail": detail})
        print(f"{'PASS' if ok else 'FAIL'} {label}" + (f": {detail}" if detail else ""))

    # a: the scene alone turned off
    game = run_dir / "a"
    rows = make(game)
    vanilla = dict(rows)[support.path_hash(support.SCENE)]
    log, slots, entries = run(game, args.loader.resolve(), run_dir / "a.log")
    check("a: the game's scene is read from its turned-off entry", RECOVERED in log)
    check("a: the slot is built, nothing left out", len(slots) == 1 and "1 slots checked, 0 left out" in log
          and LEFT_OUT not in log)
    if slots:
        s = slots[0]
        p = f"natives/stm/product/charparam/esf/esf002/{s['scene_name']}.scn.20"
        data = entries.get(support.path_hash(p), b"")
        f = s["new_folder"]
        check("a: the slot's scene is the game's, pointed at the slot's files",
              f"esf002/{f}/01/esf002_{f}_01.mesh".encode("utf-16le") in data
              and len(data) == len(vanilla), p)
    check("a: nothing is written at the game's scene path", support.path_hash(support.SCENE) not in entries)

    # b: a zeroed entry of another folder in the same gap
    game = run_dir / "b"
    make(game, decoy="001")
    log, slots, _ = run(game, args.loader.resolve(), run_dir / "b.log")
    check("b: a turned-off neighbour of another folder is passed over", RECOVERED in log and len(slots) == 1
          and LEFT_OUT not in log)

    # c: two scenes of the outfit's folder in the gap: no guess
    game = run_dir / "c"
    make(game, decoy="003")
    log, slots, _ = run(game, args.loader.resolve(), run_dir / "c.log")
    check("c: two candidates of the same folder: left out as before", RECOVERED not in log and LEFT_OUT in log)

    # d: table not sorted
    game = run_dir / "d"
    make(game, ordered=False)
    log, slots, _ = run(game, args.loader.resolve(), run_dir / "d.log")
    check("d: a table not sorted by hash: left out as before", RECOVERED not in log and LEFT_OUT in log)

    if args.old_loader:
        game = run_dir / "old"
        make(game)
        log, slots, _ = run(game, args.old_loader.resolve(), run_dir / "old.log")
        check("previous build: case a is left out", LEFT_OUT in log and "1 slots checked, 1 left out" in log)

    (run_dir / "results.json").write_text(json.dumps(checks, indent=2))
    failed = [c for c in checks if not c["passed"]]
    print(f"\n{len(checks) - len(failed)}/{len(checks)} passed ({run_dir})")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
