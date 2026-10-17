"""Keep the row of patch PAKs unbroken while mod PAKs come and go above the costume PAK.

The game reads re_chunk_000.pak.patch_NNN.pak as an unbroken row from patch_001:
a missing number hides every PAK above it (a black screen). The actual native
loader runs on the synthetic fixture of fluffy_coexistence.py while synthetic mod
PAKs and stage-slot PAKs are added and removed around its own PAK. After every
launch the row must have no hole, the real costume PAK must sit above every mod
and stage PAK, and the loader's marker-only placeholders must only fill numbers
below a PAK that stays. With --old-loader the previous build runs the first steps
on a separate fixture and must reproduce the hole. No installed game is read.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct
import tempfile

import fluffy_coexistence as support


ROOT = Path(__file__).resolve().parents[1]
DATA = Path("reframework/data/SF6_Costumes_Data")
STAGE_MARKER = "natives/stm/sf6_stage_slots.marker"
PREFIX = "re_chunk_000.pak.patch_"


def pak_name(num: int) -> str:
    return f"{PREFIX}{num:03d}.pak"


def add_mod(game: Path, num: int) -> None:
    # A Fluffy-style mod PAK: unrelated content, no loader marker
    support.write_pak(game / pak_name(num),
                      {f"natives/stm/product/ui/fixture_mod_{num:03d}.user.2": b"MOD PAK %d" % num})


def add_stage(game: Path, num: int, placeholder: bool = False) -> None:
    files = {STAGE_MARKER: b"SF6_StageSlots v1\n\0\0\0"}
    if not placeholder:
        files["natives/stm/product/stage/fixture_stage.user.2"] = b"STAGE VARIANT"
    support.write_pak(game / pak_name(num), files)


def row(game: Path) -> dict[int, str]:
    """number -> costume | stub | stage | stage-stub | mod, by marker and entry count"""
    out = {}
    marker, stage = support.path_hash(support.MARKER), support.path_hash(STAGE_MARKER)
    for path in game.glob(PREFIX + "*.pak"):
        data = path.read_bytes()
        count = struct.unpack_from("<I", data, 8)[0]
        hashes = {struct.unpack_from("<Q", data, 16 + 48 * i)[0] for i in range(count)}
        if marker in hashes:
            kind = "stub" if count == 1 else "costume"
        elif stage in hashes:
            kind = "stage-stub" if count == 1 else "stage"
        else:
            kind = "mod"
        out[int(path.name[len(PREFIX):-4])] = kind
    return dict(sorted(out.items()))


def unbroken(r: dict[int, str]) -> bool:
    return list(r) == list(range(1, len(r) + 1))


def costume_on_top(r: dict[int, str]) -> bool:
    real = [n for n, k in r.items() if k == "costume"]
    others = [n for n, k in r.items() if k in ("mod", "stage")]
    return len(real) <= 1 and (not real or not others or real[0] > max(others))


def outfits_pak(game: Path) -> str | None:
    path = game / DATA / "outfits.json"
    return json.loads(path.read_text(encoding="utf-8")).get("pak") if path.exists() else None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--old-loader", type=Path, help="optional previous build; it must leave a hole")
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
        run = Path(tempfile.mkdtemp(prefix="patch-row-", dir=runs))
    checks, steps = [], []

    def check(name: str, passed: bool, detail: str = "") -> None:
        checks.append({"name": name, "passed": bool(passed), "detail": detail})
        print(f"{'PASS' if passed else 'FAIL'} {name}" + (f": {detail}" if detail and not passed else ""))

    game = run / "game"
    support.fixture(game)
    mods_dir = game / "reframework/costume_mods"
    outfit = mods_dir / "Luke/ManualFixture"
    parked = run / "parked"
    parked.mkdir()

    def launch(exe: Path, where: Path, label: str) -> str:
        log = support.run_loader(exe, where)
        (run / f"{where.name}-{label}.txt").write_text(log, encoding="utf-8")
        return log

    C, S, M, ST, SS = "costume", "stub", "mod", "stage", "stage-stub"
    # (label, change before the launch, expected row, text expected in the log)
    scenario = [
        ("initial build", lambda: None, {1: C}, ""),
        ("mod PAK added above the costume PAK", lambda: add_mod(game, 2),
         {1: S, 2: M, 3: C}, "old pak replaced by a marker-only pak"),
        ("unchanged launch", lambda: None, {1: S, 2: M, 3: C}, "Up to date"),
        ("second mod PAK added above", lambda: add_mod(game, 4),
         {1: S, 2: M, 3: S, 4: M, 5: C}, "old pak replaced by a marker-only pak"),
        ("upper mod PAK removed", lambda: (game / pak_name(4)).unlink(),
         {1: S, 2: M, 3: C}, "removed placeholder"),
        ("lower mod PAK removed", lambda: (game / pak_name(2)).unlink(), {1: C}, "removed placeholder"),
        ("stage slots PAK appears above (costume PAK moved, placeholder left)",
         lambda: add_stage(game, 2), {1: S, 2: ST, 3: C}, "moved to " + pak_name(3)),
        ("stage PAK becomes a stage placeholder (not a stage PAK)",
         lambda: add_stage(game, 2, placeholder=True), {1: C, 2: SS}, "moved to " + pak_name(1)),
        ("stage placeholder removed", lambda: (game / pak_name(2)).unlink(), {1: C}, "Up to date"),
        ("mod PAK added again", lambda: add_mod(game, 2), {1: S, 2: M, 3: C}, ""),
        ("outfit removed while a new mod PAK sits above (no-mods cleanup)",
         lambda: (add_mod(game, 4), outfit.rename(parked / outfit.name)),
         {1: S, 2: M, 3: S, 4: M}, "replaced"),
        ("outfit restored", lambda: (parked / outfit.name).rename(outfit),
         {1: S, 2: M, 3: S, 4: M, 5: C}, ""),
        ("costume_mods folder absent while a new mod PAK sits above",
         lambda: (add_mod(game, 6), mods_dir.rename(parked / "costume_mods")),
         {1: S, 2: M, 3: S, 4: M, 5: S, 6: M}, "costume_mods folder absent"),
        ("costume_mods restored and every mod PAK removed",
         lambda: ((parked / "costume_mods").rename(mods_dir),
                  [(game / pak_name(n)).unlink() for n in (2, 4, 6)]),
         {1: C}, "removed placeholder"),
    ]
    for label, change, expected, needle in scenario:
        change()
        log = launch(args.loader.resolve(), game, label.split(" (")[0].replace(" ", "-"))
        r = row(game)
        steps.append({"step": label, "row": {f"{n:03d}": k for n, k in r.items()}})
        check(f"{label}: row unbroken from patch_001", unbroken(r), str(r))
        check(f"{label}: costume PAK above every mod and stage PAK", costume_on_top(r), str(r))
        check(f"{label}: expected row", r == expected, f"{r} != {expected}")
        if needle:
            check(f"{label}: log says '{needle}'", needle in log)
        real = [n for n, k in r.items() if k == C]
        if real:
            check(f"{label}: outfits.json names the costume PAK", outfits_pak(game) == pak_name(real[0]))
    check("manual outfit kept its slot through every step",
          len(json.loads((game / DATA / "registry.json").read_text())["slots"]) == 1)

    old_steps = []
    if args.old_loader:
        old = run / "old-game"
        support.fixture(old)
        broken = []
        for label, change in (("initial build", lambda: None),
                              ("mod PAK added above", lambda: add_mod(old, 2)),
                              ("second mod PAK added above", lambda: add_mod(old, 4))):
            change()
            launch(args.old_loader.resolve(), old, "old-" + label.replace(" ", "-"))
            r = row(old)
            old_steps.append({"step": label, "row": {f"{n:03d}": k for n, k in r.items()}})
            if not unbroken(r):
                broken.append(label)
        check("previous build leaves a hole in the row once a mod PAK sits above", bool(broken),
              ", ".join(broken))

    result = {"loader": str(args.loader.resolve()),
              "old_loader": str(args.old_loader.resolve()) if args.old_loader else None,
              "scope": "native loader PAK row handling; synthetic workspace data; no SF6 runtime test",
              "steps": steps, "old_steps": old_steps, "checks": checks,
              "passed": sum(c["passed"] for c in checks), "total": len(checks)}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
