"""Fluffy modular mods with nested menu groups become the outfits Fluffy installs by default.

The actual native loader runs on synthetic fixture games (one per case, the base PAK,
inventory and loader metadata of fluffy_coexistence.py with 20 reserved slots):

- nested: a ZIP whose options hang from sub-groups of the menu ("02 Hair" > "Long Hair"
  > option), as DummyMod folders that only hold a modinfo.ini. Each top-level group is one
  choice; the main files are merged first (the root's name sorts last, so the 1.9.7 order
  let them overwrite the hair); a group whose description says "Optional" is left out;
  costumes are named after the mod. 3 hair x 3 outfits = 9 outfits.
- cap: 5 x 10 = 50 combinations are all made; 3 x 17 = 51 fall back to the largest group.
- flat: the old "1) Main files / 2) skin / 3) outfit" layout without menu folders.
- single: one menu level (main files, "2) Variants" and its options), like the A.K.I bundle.
- long: the nested ZIP in a game folder whose path is about 175 characters long, so the
  extracted options pass MAX_PATH: same outfits as the short path.

With --old-loader the previous build runs the same cases: flat and single must give the
same outfits, slots and PAK entries; nested must differ, long must be skipped.
Fixtures stay under tests/.runs. This is not an SF6 rendering test.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import tempfile
import zipfile

import fluffy_coexistence as support
from costume_table_fixture import TABLE_PATH, costume_table

ROOT = Path(__file__).resolve().parents[1]
DATA = Path("reframework/data/SF6_Costumes_Data")
FD, FOLDER = "esf002", "003"
MODEL = f"natives/stm/product/model/esf/{FD}/{FOLDER}/"
SLOTS = range(5, 25)


def part_mesh(part: str) -> str:
    return f"{MODEL}{part}/{FD}_{FOLDER}_{part}.mesh.230110883"


def part_mdf(part: str) -> str:
    return f"{MODEL}{part}/{FD}_{FOLDER}_{part}_v00.mdf2.31"


def part_tex(part: str) -> str:
    return f"{MODEL}{part}/{FD}_{FOLDER}_{part}_body_albd.tex.241101895"


def files_of(part: str, tag: str, tex: bool = False, mesh: bool = True) -> dict[str, bytes]:
    out = {}
    if mesh:
        out[part_mesh(part)] = support.mesh(tag)
        out[part_mdf(part)] = support.mdf(tag)
    if tex:
        out[part_tex(part)] = support.texture(tag)
    return out


def fixture(game: Path) -> None:
    """A synthetic game: costume esf002/003 with parts 00 (head), 01 (body), 02 (hair)."""
    loader = game / DATA / "loader"
    static = loader / "static"
    static.mkdir(parents=True)
    scene_path = f"natives/stm/product/charparam/esf/{FD}/{FD}v02.scn.20"
    scene1 = f"natives/stm/product/charparam/esf/{FD}/{FD}v00.scn.20"
    base = {}
    for part in ("00", "01", "02"):
        base.update(files_of(part, "VANILLA_" + part, tex=part == "01"))
    refs = [p.removeprefix("natives/stm/").rsplit(".", 1)[0] for p in base if ".tex." not in p]
    base[scene_path] = ("\0" + "\0".join(refs * 2) + "\0").encode("utf-16le")
    base[scene1] = "\0Fixture Outfit 1\0".encode("utf-16le")
    for local, path in support.STRUCTURAL.items():
        base[path] = ("fixture global static: " + local).encode("ascii")
    base[TABLE_PATH] = costume_table()
    support.write_pak(game / "re_chunk_000.pak", base)
    lines = ["#SUFFIXES", "mesh\t230110883", "mdf2\t31", "tex\t241101895", "scn\t20", "user\t2",
             "chain\t52", "#COSTUMES",
             f"2\t{FD}\t0\tproduct/model/esf/{FD}/001\t{scene1}",
             f"2\t{FD}\t2\tproduct/model/esf/{FD}/{FOLDER}\t{scene_path}", "#FILES"]
    for name in base:
        lines.append(f"{support.path_hash(name):016X}\t2\t2\tfixture\t{name}")
    (loader / "vanilla_costume_index.tsv").write_text("\n".join(lines) + "\n")
    records = [(500 + k, 2, c) for k, c in enumerate(SLOTS)]
    meta = {"slot_range": [SLOTS.start, SLOTS.stop],
            "files": [{"local": local, "pak_path": path} for local, path in support.STRUCTURAL.items()],
            "records": [{"fighter": 2, "costume_no": c, "record_id": r, "manage_id": r}
                        for r, _, c in records]}
    (static / "static_meta.json").write_text(json.dumps(meta))
    for local, path in support.STRUCTURAL.items():
        (static / local).write_bytes(base[path])
    (static / "fightercostumeuserdata.user.2").write_bytes(costume_table(generated=tuple(records)))
    (game / "reframework/costume_mods/Luke").mkdir(parents=True)


def modinfo(name: str, addonfor: str = "", description: str = "", dummy: bool = False) -> str:
    text = f"name={name}\nauthor=Regression test\n"
    if description:
        text += f"description={description}\n"
    if addonfor:
        text += f"AddonFor={addonfor}\n"
    if dummy:
        text += "DummyMod=True\n"
    return text


Layout = dict[str, tuple[str, dict[str, bytes]]]   # folder -> (modinfo.ini, files)


def nested_layout(root: str, hair: dict[str, list[str]], outfits: dict[str, list[str]],
                  accessory: bool = True) -> Layout:
    """hair/outfits: sub-group name -> option names (each option gets its own tag)"""
    lay: Layout = {
        "00 Menu": (modinfo(root, dummy=True), {}),
        "01 Main files": (modinfo("01 Main files", root, "Required files."),
                          {**files_of("00", "MAIN_HEAD"), **files_of("02", "MAIN_HAIR")}),
        "02 Hair": (modinfo("02 Hair", root, "Choose one hair.", dummy=True), {}),
        "03 Outfit": (modinfo("03 Outfit", root, "Choose one outfit.", dummy=True), {}),
    }
    for top, groups, part in (("02", hair, "02"), ("03", outfits, "01")):
        for g, (sub, names) in enumerate(groups.items()):
            letter = chr(ord("A") + g)
            lay[f"{top}{letter} {sub}"] = (modinfo(sub, "02 Hair" if top == "02" else "03 Outfit", dummy=True), {})
            for n, name in enumerate(names, 1):
                tag = f"{'HAIR' if top == '02' else 'BODY'}_{letter}{n}"
                lay[f"{top}{letter}{n} {name} - {sub}"] = (
                    modinfo(name, sub), files_of(part, tag, tex=part == "01"))
    if accessory:
        lay["04 Accessories"] = (modinfo("04 Accessories", root, "Optional accessories.", dummy=True), {})
        lay["04A No Monocle"] = (modinfo("No Monocle", "04 Accessories"), files_of("00", "NO_MONOCLE"))
    return lay


def write_layout(where: Path, lay: Layout) -> None:
    for folder, (ini, files) in lay.items():
        d = where / folder
        d.mkdir(parents=True)
        (d / "modinfo.ini").write_text(ini)
        for name, data in files.items():
            (d / name).parent.mkdir(parents=True, exist_ok=True)
            (d / name).write_bytes(data)


def write_zip(path: Path, top: str, lay: Layout) -> None:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as z:
        for folder, (ini, files) in lay.items():
            z.writestr(f"{top}/{folder}/modinfo.ini", ini)
            for name, data in files.items():
                z.writestr(f"{top}/{folder}/{name}", data)


def run_case(exe: Path, game: Path, log_name: Path) -> dict:
    log = support.run_loader(exe, game)
    log_name.write_text(log, encoding="utf-8")
    outfits_path = game / DATA / "outfits.json"
    outfits = json.loads(outfits_path.read_text(encoding="utf-8")) if outfits_path.exists() else {"outfits": []}
    reg = json.loads((game / DATA / "registry.json").read_text())
    paks = [p for p in game.glob("re_chunk_000.pak.patch_*.pak")
            if support.path_hash(support.MARKER) in support.read_pak(p)]
    entries = support.read_pak(paks[0]) if paks else {}
    by_no = {s["new_costume_no"]: s for s in reg["slots"]}
    slots = {}
    for o in outfits["outfits"]:
        s = by_no.get(o["costume_no"])
        if not s:
            continue
        nf = s["new_folder"]
        parts = {}
        for part in ("00", "01", "02"):
            p = part_mesh(part).replace(f"/{FOLDER}/", f"/{nf}/").replace(f"{FD}_{FOLDER}_", f"{FD}_{nf}_")
            data = entries.get(support.path_hash(p), b"")
            parts[part] = data[192:224].rstrip(b"\0").decode("ascii", "replace") if data else None
        slots[o["label"]] = parts
    digest = {f"{k:016X}": hashlib.sha256(v).hexdigest() for k, v in entries.items()}
    return {"log": log, "outfits": outfits["outfits"], "left_out": outfits.get("left_out", []),
            "slots": slots, "registry": reg["slots"], "digest": digest}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--old-loader", type=Path, help="optional previous build")
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
        run = Path(tempfile.mkdtemp(prefix="nested-", dir=runs))
    checks = []

    def check(name: str, passed: bool, detail: str = "") -> None:
        checks.append({"name": name, "passed": bool(passed), "detail": detail})
        print(f"{'PASS' if passed else 'FAIL'} {name}" + (f": {detail}" if detail and not passed else ""))

    root = "Zeta Nested Bundle"   # sorts after every group name: 1.9.7 merged the main files last
    hair = {"Short Hair": ["Hair One"], "Long Hair": ["Hair One", "Hair Two"]}
    outfits = {"Neck A": ["Shoes", "Barefoot"], "Neck B": ["Shoes"]}
    nested = nested_layout(root, hair, outfits)
    flat: Layout = {
        "1) Main files": (modinfo("1) Main files - Flat Bundle", "(Flat Bundle)"),
                          {**files_of("00", "FLAT_HEAD"), **files_of("02", "FLAT_HAIR")}),
        "2) Skin A": (modinfo("Skin A", "2) Skin - Flat Bundle"), files_of("01", "SKIN_A", tex=True, mesh=False)),
        "2) Skin B": (modinfo("Skin B", "2) Skin - Flat Bundle"), files_of("01", "SKIN_B", tex=True, mesh=False)),
        "3) Outfit X": (modinfo("Outfit X", "3) Outfit - Flat Bundle"), files_of("01", "FLAT_BODY_X")),
        "3) Outfit Y": (modinfo("Outfit Y", "3) Outfit - Flat Bundle"), files_of("01", "FLAT_BODY_Y")),
    }
    single: Layout = {
        "00 Menu": (modinfo("Solo Bundle", dummy=True), {}),
        "01 Main files": (modinfo("1) Main files - Solo Bundle", "Solo Bundle"),
                          {**files_of("00", "SOLO_HEAD"), **files_of("02", "SOLO_HAIR")}),
        "02 Variants menu": (modinfo("2) Variants - Solo Bundle", "Solo Bundle", dummy=True), {}),
        "03 Solo Legs": (modinfo("Solo Legs", "2) Variants - Solo Bundle"), files_of("01", "SOLO_LEGS")),
        "04 Solo Barefoot": (modinfo("Solo Barefoot", "2) Variants - Solo Bundle"), files_of("01", "SOLO_BAREFOOT")),
        "05 Solo Sandals": (modinfo("Solo Sandals", "2) Variants - Solo Bundle"), files_of("01", "SOLO_SANDALS")),
    }

    def make(name: str, base: Path | None = None) -> Path:
        game = (base or run) / name
        fixture(game)
        return game

    def cases(exe: Path, tag: str) -> dict:
        out = {}
        g = make(f"{tag}-nested")
        write_zip(g / "reframework/costume_mods/Luke/Nested Bundle.zip", root, nested)
        out["nested"] = run_case(exe, g, run / f"{tag}-nested.txt")
        g = make(f"{tag}-flat")
        write_layout(g / "reframework/costume_mods/Luke/Flat Bundle", flat)
        out["flat"] = run_case(exe, g, run / f"{tag}-flat.txt")
        g = make(f"{tag}-single")
        write_zip(g / "reframework/costume_mods/Luke/Solo Bundle.zip", "Solo Bundle", single)
        out["single"] = run_case(exe, g, run / f"{tag}-single.txt")
        deep = run / f"{tag}-deep"
        pad = 175 - len(str(deep)) - 2
        g = make("L" * max(pad, 1), deep)
        write_zip(g / "reframework/costume_mods/Luke/Nested Bundle.zip", root, nested)
        out["long"] = run_case(exe, g, run / f"{tag}-long.txt")
        out["long_base"] = len(str(g))
        for name, h, o in (("cap50", 5, 10), ("cap51", 3, 17)):
            g = make(f"{tag}-{name}")
            lay = nested_layout(f"Cap Bundle {name}", {"Cut": [f"Hair {i}" for i in range(h)]},
                                {"Style": [f"Body {i}" for i in range(o)]}, accessory=False)
            write_zip(g / "reframework/costume_mods/Luke/Cap Bundle.zip", "Cap Bundle", lay)
            out[name] = run_case(exe, g, run / f"{tag}-{name}.txt")
        return out

    new = cases(args.loader.resolve(), "new")

    # nested
    n = new["nested"]
    expected = {}
    for hg, (hsub, hnames) in enumerate(hair.items()):
        for hn, hname in enumerate(hnames, 1):
            for og, (osub, onames) in enumerate(outfits.items()):
                for on, oname in enumerate(onames, 1):
                    label = f"{root} - {hsub} - {hname} - {osub} - {oname}"
                    expected[label] = {"00": "MAIN_HEAD", "02": f"HAIR_{chr(65 + hg)}{hn}",
                                       "01": f"BODY_{chr(65 + og)}{on}"}
    check("nested: one costume per hair x outfit option (top-level groups are one choice each)",
          f"modular mod {root}: 9 costumes" in n["log"], n["log"][-3000:])
    check("nested: every combination gets a slot", len(n["slots"]) == 9, str(list(n["slots"])))
    check("nested: costumes are named after the mod, with the option's sub-group",
          set(n["slots"]) == set(expected), str(sorted(n["slots"])))
    check("nested: main files first, the hair option replaces the main files' hair",
          all(n["slots"].get(k, {}).get("02") == v["02"] for k, v in expected.items()))
    check("nested: each outfit option's body in its own costume",
          all(n["slots"].get(k, {}).get("01") == v["01"] for k, v in expected.items()))
    check("nested: optional group (by its description) left out, the main head kept",
          all(s["00"] == "MAIN_HEAD" for s in n["slots"].values()) and "group 04 Accessories: optional, left out" in n["log"])
    # cap
    c50, c51 = new["cap50"], new["cap51"]
    check("cap: 50 combinations are all made", "modular mod Cap Bundle cap50: 50 costumes" in c50["log"]
          and c50["log"].count("\n  variant: ") == 50)
    check("cap: 51 combinations fall back to the largest group (17), the other group's first option",
          "modular mod Cap Bundle cap51: 17 costumes" in c51["log"] and c51["log"].count("\n  variant: ") == 17
          and "3 options, the first one kept (over 50 combinations)" in c51["log"])
    skipped = c50["log"].count("has no free outfit slot left")
    check("cap: past the 20 reserved slots the other costumes are logged as not installed",
          len(c50["slots"]) == len(SLOTS) and skipped == 50 - len(SLOTS),
          f"{len(c50['slots'])} slots, {skipped} logged")
    # flat
    f = new["flat"]
    check("flat: one costume per outfit option, first skin, labels from the (root) group",
          set(f["slots"]) == {"Flat Bundle - Outfit X", "Flat Bundle - Outfit Y"}, str(list(f["slots"])))
    check("flat: parts as before (head and hair from the main files, body from the outfit)",
          all(s == {"00": "FLAT_HEAD", "01": "FLAT_BODY_" + k[-1], "02": "FLAT_HAIR"} for k, s in f["slots"].items()),
          str(f["slots"]))
    # single menu level
    s = new["single"]
    check("single menu level: one costume per variant with the 1.9.7 path-based names",
          sorted(s["slots"]) == sorted(f"Luke/Solo Bundle.zip/Solo Bundle/01 Main files - {v}"
                                       for v in ("Solo Legs", "Solo Barefoot", "Solo Sandals")), str(list(s["slots"])))
    # long path
    lg = new["long"]
    check(f"long base path ({new['long_base']} characters): archive options are found",
          "no natives folder, .pak or usable archive found" not in lg["log"] and new["long_base"] >= 170, lg["log"][-2000:])
    check("long base path: same outfits and parts as the short path",
          lg["slots"] == n["slots"] and [o["label"] for o in lg["outfits"]] == [o["label"] for o in n["outfits"]])

    if args.old_loader:
        old = cases(args.old_loader.resolve(), "old")
        for name in ("flat", "single"):
            check(f"{name}: same outfits, labels and slots as the previous build",
                  old[name]["outfits"] == new[name]["outfits"] and old[name]["registry"] == new[name]["registry"])
            check(f"{name}: same PAK entries as the previous build", old[name]["digest"] == new[name]["digest"],
                  f"{len(set(old[name]['digest'].items()) ^ set(new[name]['digest'].items()))} differ")
        check("previous build: nested mod not made as Fluffy installs it",
              set(old["nested"]["slots"]) != set(expected)
              or any(v.get("02") == "MAIN_HAIR" for v in old["nested"]["slots"].values()))
        check("previous build: long base path archive skipped",
              "no natives folder, .pak or usable archive found" in old["long"]["log"])

    for v in new.values():
        if isinstance(v, dict):
            v.pop("digest", None)
    result = {"loader": str(args.loader.resolve()),
              "old_loader": str(args.old_loader.resolve()) if args.old_loader else None,
              "scope": "native loader modular-mod resolution; synthetic data; no SF6 runtime test",
              "slots": {k: v["slots"] for k, v in new.items() if isinstance(v, dict)},
              "checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks)}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
