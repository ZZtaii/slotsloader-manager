"""An option whose Fluffy "addonfor" names another option of the same mod (1.9.8).

A.K.I. C2 - Bare Legs Bundle: "00 Main" (Name "A.K.I. C2 - Bare Legs") and "01 Ribbon" (Name
"Ribbon", AddonFor "A.K.I. C2 - Bare Legs", its own mesh). Fluffy installs Ribbon over the main,
both active. The loader must give the main alone and "main + Ribbon" (the main's files, Ribbon's
winning where both have one), not Ribbon alone without the main's files. An option whose addonfor
names nothing keeps its own outfit. Synthetic fixture game (fluffy_coexistence's); actual loader.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import tempfile

import fluffy_coexistence as support
import nested_modular as nm

ROOT = Path(__file__).resolve().parents[1]
MAIN = "Fixture Main"


def make(game: Path) -> None:
    nm.fixture(game)   # 20 reserved slots; costume esf002/003 with parts 00, 01, 02
    lay = {
        "00 Main": (nm.modinfo(MAIN), {**nm.files_of("01", "MAIN_BODY", tex=True), **nm.files_of("02", "MAIN_HAIR")}),
        # trimmed, case-insensitive match of the main's modinfo name
        "01 Ribbon": (nm.modinfo("Ribbon", MAIN.lower() + "  "), nm.files_of("01", "RIBBON_BODY")),
        "02 Other": (nm.modinfo("Other", "Nothing Of This Mod"), nm.files_of("01", "OTHER_BODY")),
    }
    nm.write_zip(game / "reframework/costume_mods/Luke/Fixture Bundle.zip", "Fixture Bundle", lay)


def slots(game: Path, exe: Path, log: Path):
    r = nm.run_case(exe, game, log)
    entries = support.read_pak(support.patch_file(game))
    by_no = {s["new_costume_no"]: s for s in r["registry"]}
    tex = {}
    for o in r["outfits"]:
        f = by_no[o["costume_no"]]["new_folder"]
        p = nm.part_tex("01").replace("/003/", f"/{f}/").replace("esf002_003_", f"esf002_{f}_")
        tex[o["label"]] = entries.get(support.path_hash(p))
    return r, tex

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    ap.add_argument("--old-loader", type=Path, help="previous build: Ribbon alone, without the main's files")
    args = ap.parse_args()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="addons-", dir=runs))
    checks = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(ok), "detail": detail})
        print(("PASS " if ok else "FAIL ") + label + (": " + detail if detail and not ok else ""))

    game = run / "game"
    make(game)
    r, tex = slots(game, args.loader.resolve(), run / "log.txt")
    s, log = r["slots"], r["log"]
    main_tex = support.texture("MAIN_BODY")
    combo = MAIN + " + Ribbon"
    check("three outfits: the main, main + Ribbon, the unmatched option",
          sorted(s) == sorted([MAIN, combo, "Other"]), repr(sorted(s)))
    check("no outfit of Ribbon alone", "Ribbon" not in s)
    check("all slots validated", "3 slots checked, 0 left out" in log)
    check("main slot: the main's body and hair", s.get(MAIN, {}).get("01") == "MAIN_BODY"
          and s.get(MAIN, {}).get("02") == "MAIN_HAIR" and tex.get(MAIN) == main_tex, repr(s.get(MAIN)))
    check("main + Ribbon: Ribbon's body wins", s.get(combo, {}).get("01") == "RIBBON_BODY", repr(s.get(combo)))
    check("main + Ribbon: the main's hair and body texture are in the slot",
          s.get(combo, {}).get("02") == "MAIN_HAIR" and tex.get(combo) == main_tex, repr(s.get(combo)))
    check("unmatched addonfor: its own outfit as before", s.get("Other", {}).get("01") == "OTHER_BODY")
    check("logged as an add-on of the main", f"Ribbon: add-on of {MAIN} (its addonfor), made with it" in log)

    if args.old_loader:
        g2 = run / "old-game"
        make(g2)
        ro, to = slots(g2, args.old_loader.resolve(), run / "old-log.txt")
        so = ro["slots"]
        check("previous build: Ribbon alone, without the main's hair or texture",
              "Ribbon" in so and so["Ribbon"].get("02") != "MAIN_HAIR" and to.get("Ribbon") != main_tex,
              repr(so))
    result = {"checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks),
              "scope": "native packaging of modular options; synthetic data; no SF6 runtime test"}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
