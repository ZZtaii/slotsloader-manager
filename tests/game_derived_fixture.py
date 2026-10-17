"""Exercise the game-derived loader data on a synthetic future game update.

Everything is synthetic and stays under tests/.runs; no game is read. An "old"
game mirrors today's structural layout (18 fighters in the root scene, the DLC
fighters in ver_02_0300 with a v04, Yasmine in ver_02_0400, Arjun without a v04
in ver_02_0500, a non-fighter esf103). Its shipped loader data is made by the
loader's own code (build/game_derived.exe) and must equal what
costumes/tools/static_slots.py rebuilds from the same vanilla files ("verify").
The "new" game then adds, as an update would:
  - fighter 35 (not in the shipped tables, the vanilla index or the folder list)
    in a new version scene ver_03_0100, with no DriveTech folder;
  - an official Outfit 3 for Arjun (costume 2) declared in ver_02_0600.
The actual loader must take both in from the game: slots and records for
fighter 35 (8200..8299, outside the plugin's 5000..8199 ownership sweep), the
index completed with esf035 v00 and esf034 v02 (fighter 35 read by path scan),
a costume_mods folder named from the game's English name, a slot for a manual
outfit of fighter 35, ver_03_0100 published (exempt from protection) and the
unchanged scenes byte-identical to the shipped copies. The previous build makes
no slot for fighter 35. Cache: an unchanged launch and Fluffy's hash
invalidation of an unrelated row reuse the PAK. Fallback: with a shipped scene's
row invalidated (Fluffy-style) or the new scene unreadable, the loader logs
"structural files taken from the shipped copies" and publishes the shipped
copies byte for byte, keeping the other slots.
usage: python tests/game_derived_fixture.py [--loader <exe>] [--tool <exe>] [--old-loader <exe>] [--output <dir>]
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile

import fluffy_coexistence as support
from costume_table_fixture import TABLE_PATH, costume_records, costume_table
from new_costumes_data import scene_folders

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "costumes/tools"))
import static_slots  # noqa: E402

DATA = Path("reframework/data/SF6_Costumes_Data")
ESF_ROOT = "natives/stm/product/charparam/esf/esf.scn.20"
COSTUME_MSG = "natives/stm/product/message/fgm/fighter/fightercostumemessage.msg.21"
NAME_MSG = "natives/stm/product/message/cmn/001/wordlist/fighternamemessage.msg.21"
VER = "natives/stm/product/flow/version_update/ver_{}/esf.scn.20"
BASE_FIGHTERS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17, 18, 20, 21]
DLC_FIGHTERS = [13, 14, 19, 22, 25, 26, 27, 28, 29, 30, 31, 32]
OLD_FIGHTERS = sorted(BASE_FIGHTERS + DLC_FIGHTERS + [33, 34])
NEW_FIGHTER, NEW_NAME = 35, "Test Fighter"
# pak path -> loader/static name, in static_meta.json's order
STATIC_NAMES = {ESF_ROOT: "esf.scn.20"}
for v in ("02_0300", "02_0400", "02_0500"):
    STATIC_NAMES[VER.format(v)] = f"ver_{v}_esf.scn.20"
STATIC_NAMES[TABLE_PATH] = "fightercostumeuserdata.user.2"
STATIC_NAMES[COSTUME_MSG] = "fightercostumemessage.msg.21"
MODEL35 = "natives/stm/product/model/esf/esf035/001/01/"
MESH35 = MODEL35 + "esf035_001_01.mesh.230110883"
MDF35 = MODEL35 + "esf035_001_01_v00.mdf2.31"
TEX35 = MODEL35 + "esf035_001_01_body_albd.tex.241101895"
STREAM35 = TEX35.replace("natives/stm/product/", "natives/stm/streaming/product/")
SCENE35 = "natives/stm/product/charparam/esf/esf035/esf035v00.scn.20"
SCENE34_2 = "natives/stm/product/charparam/esf/esf034/esf034v02.scn.20"
KEY = bytes((0xCF, 0xCE, 0xFB, 0xF8, 0xEC, 0x0A, 0x33, 0x66, 0x93, 0xA9, 0x1D, 0x93, 0x50, 0x39, 0x5F, 0x09))


# ---- RE Engine GMSG (.msg.21), the encrypted by-hash layout structural_msg.cpp reads ----
def gmsg(entries: list[tuple[str, str]], languages=(0, 1)) -> bytes:
    """entries: (name, text); the text is used for every language."""
    n, nl = len(entries), len(languages)
    unknown = 72 + 8 * n
    lang = unknown + 8
    entry_at = (lang + 4 * nl + 7) & ~7
    esize = 40 + 8 * nl
    pool_at = (entry_at + n * esize + 7) & ~7
    pool = bytearray(b"\0\0")

    def string(s: str) -> int:
        if not s:
            return pool_at
        at = pool_at + len(pool)
        pool.extend(s.encode("utf-16le") + b"\0\0")
        return at
    body = bytearray(pool_at)
    struct.pack_into("<I4sQIIIIQQQQQ", body, 0, 21, b"GMSG", 16, n, 0, nl, 0,
                     pool_at, unknown, lang, entry_at, entry_at)
    for i, code in enumerate(languages):
        struct.pack_into("<I", body, lang + 4 * i, code)
    for i, (name, text) in enumerate(entries):
        eo = entry_at + i * esize
        struct.pack_into("<Q", body, 72 + 8 * i, eo)
        body[eo:eo + 16] = bytes((i + 1,)) * 16
        struct.pack_into("<IIQQ", body, eo + 16, 0, support.murmur3(name.encode("utf-16le")),
                         string(name), pool_at)
        for k in range(nl):
            struct.pack_into("<Q", body, eo + 40 + 8 * k, string(text))
    prev = 0
    for i in range(len(pool)):
        pool[i] ^= prev ^ KEY[i & 15]
        prev = pool[i]
    return bytes(body + pool)


# ---- via.Folder scenes, written by static_slots.build_scn ----
def folder(name: str, scene: bool) -> dict:
    path = f"Product/CharParam/esf/{name[:6]}/{name}.scn\0" if scene else "\0"
    return {"Name": name + "\0", "Tag": "\0", "flags": bytes((1, 1, 0 if scene else 1)),
            "ScenePath": path, "offset": bytes(24)}


def scene(groups: list[tuple[str, list[str]]], top: list[str] = ()) -> bytes:
    """groups: (parent folder, costume folder names under it); top: lone folders first."""
    scn = {"folders": [], "objects": [], "instances": [None]}

    def add(name, scene_folder, parent):
        scn["instances"].append(folder(name, scene_folder))
        scn["objects"].append(len(scn["instances"]) - 1)
        scn["folders"].append((len(scn["objects"]) - 1, parent))
        return len(scn["folders"]) - 1
    for name in top:
        add(name, False, -1)
    for parent, names in groups:
        p = add(parent, False, -1)
        for name in names:
            add(name, True, p)
    return static_slots.build_scn(scn)


def esf(f: int) -> str:
    return f"esf{f:03d}"


def game_files(new: bool) -> dict[str, bytes]:
    files = {}
    files[ESF_ROOT] = scene([(f"fighter{f:03d}", [f"{esf(f)}v00", f"{esf(f)}a00", f"{esf(f)}v01"])
                             for f in BASE_FIGHTERS], top=["common", "esfcmn000"])
    files[VER.format("02_0110")] = scene([])                      # a version scene without folders
    files[VER.format("02_0300")] = scene(
        [(f"fighter{f:03d}", [f"{esf(f)}v04"]) for f in BASE_FIGHTERS + DLC_FIGHTERS]
        + [("fighter103", ["esf103a00", "esf103v00"])])
    files[VER.format("02_0400")] = scene([("fighter033", ["esf033a00", "esf033v00", "esf033v01", "esf033wts00", "esf033v04"])])
    files[VER.format("02_0500")] = scene([("fighter034", ["esf034a00", "esf034v00", "esf034wts00", "esf034v01"]),
                                          ("fighter030", ["esf030v03"])])
    vanilla = [(i + 1, f, 0) for i, f in enumerate(OLD_FIGHTERS)]
    if new:
        files[VER.format("02_0600")] = scene([("fighter034", ["esf034v02"])])
        files[VER.format("03_0100")] = scene([("fighter035", ["esf035a00", "esf035v00", "esf035v01"])])
        vanilla += [(200, 34, 2), (201, NEW_FIGHTER, 0)]
    files[TABLE_PATH] = costume_table(vanilla=vanilla)
    files[COSTUME_MSG] = gmsg([(f"FighterCostumeMessage{i}", f"Costume {i}") for i in range(1, 6)])
    names = [(f"FighterNameMessage{f}", f"Fighter {f}") for f in OLD_FIGHTERS]
    if new:
        names.append((f"FighterNameMessage{NEW_FIGHTER}", NEW_NAME))
    files[NAME_MSG] = gmsg(names)
    if new:
        refs = [MESH35.removeprefix("natives/stm/").rsplit(".", 1)[0],
                MDF35.removeprefix("natives/stm/").rsplit(".", 1)[0]]
        files[SCENE35] = ("\0" + "\0".join(refs * 2) + "\0").encode("utf-16le")
        files[SCENE34_2] = "\0Arjun Outfit 3\0".encode("utf-16le")
        files[MESH35] = support.mesh("VANILLA35")
        files[MDF35] = support.mdf("VANILLA35")
        files[TEX35] = support.texture("VANILLA35")
        files[STREAM35] = support.texture("VANILLA35_STREAM")
    return files


def static_meta(files: dict[str, str]) -> str:
    """The shipped static_meta.json, in static_slots.py's format and key order."""
    meta = {"slot_range": [5, 105], "record_id_base": 5000, "msg_id_base": 5000, "sort_no_base": 101,
            "msg_guids": [f"a1b2c3d4-{so + 1:04x}-4000-8000-{5000 + so:012x}" for so in range(100)],
            "fighter_ids": OLD_FIGHTERS, "base_fighter_dirs": sorted(esf(f) for f in BASE_FIGHTERS),
            "dlc_fighter_dirs_0300": sorted(esf(f) for f in DLC_FIGHTERS + [103]),   # esf103: no record
            "dlc_fighter_dirs_0400": ["esf033"], "dlc_fighter_dirs_0500": ["esf034"],
            "files": [{"local": local, "pak_path": pak} for pak, local in files.items()]}
    meta["records"] = static_slots.meta_records(meta["fighter_ids"], meta["slot_range"], 5000)
    return static_slots.dump_meta(meta)


def pak_rows(path: Path) -> list[int]:
    data = path.read_bytes()
    return [struct.unpack_from("<Q", data, 16 + 48 * i)[0] for i in range(struct.unpack_from("<I", data, 8)[0])]


def invalidate(path: Path, name: str) -> None:
    """Fluffy's invalidation: both path-hash fields of the row zeroed, payload untouched."""
    data = bytearray(path.read_bytes())
    for i in range(struct.unpack_from("<I", data, 8)[0]):
        if struct.unpack_from("<Q", data, 16 + 48 * i)[0] == support.path_hash(name):
            struct.pack_into("<Q", data, 16 + 48 * i, 0)
            path.write_bytes(data)
            return
    raise AssertionError("row not found: " + name)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--tool", type=Path, default=ROOT / "build/game_derived.exe")
    parser.add_argument("--old-loader", type=Path, help="previous build: no slot for the new fighter")
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
        run = Path(tempfile.mkdtemp(prefix="game-derived-", dir=runs))
    checks = []

    def check(name: str, passed: bool, detail: str = "") -> None:
        checks.append({"name": name, "passed": bool(passed), "detail": detail})
        print(f"{'PASS' if passed else 'FAIL'} {name}" + (f": {detail}" if detail and not passed else ""))

    def tool(*a) -> subprocess.CompletedProcess:
        return subprocess.run([str(args.tool.resolve()), *map(str, a)], capture_output=True, text=True)

    # ---- the old game and its shipped loader data, made by the loader's code ----
    old_game = game_files(new=False)
    old_pak = run / "old-game.pak"
    support.write_pak(old_pak, old_game)
    shipped = run / "shipped-static"
    shipped.mkdir()
    r = tool("structural", shipped, old_pak)
    check("old game: structural files build", r.returncode == 0, r.stdout + r.stderr)
    for name in ("records.tsv", "summary.txt"):
        (shipped / name).rename(run / ("old-" + name))
    (shipped / "static_meta.json").write_text(static_meta(STATIC_NAMES), encoding="utf-8", newline="")
    natives = run / "old-vanilla"
    for pak in STATIC_NAMES:   # the vanilla tree static_slots.py reads
        out = natives / pak
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(old_game[pak])
    v = subprocess.run([sys.executable, str(ROOT / "costumes/tools/static_slots.py"), "verify",
                        "--vanilla", str(natives), "--static", str(shipped)], capture_output=True, text=True)
    (run / "static_slots-verify.txt").write_text(v.stdout + v.stderr, encoding="utf-8")
    check("static_slots.py rebuilds the loader-made copies byte for byte (v04 and highest-vNN templates)",
          v.returncode == 0 and v.stdout.count("IDENTICAL") == 7, v.stdout + v.stderr)
    old_records = (run / "old-records.tsv").read_text().splitlines()
    check("old game: 32 fighters x 100 records, ids 5000..8199 as static_meta.json",
          len(old_records) == 3200 and old_records[0] == "1\t5\t5000" and old_records[-1] == "34\t104\t8199")

    # ---- the new game, as the loader's code sees it ----
    new_game = game_files(new=True)
    derived = run / "new-derived"
    derived.mkdir()
    new_pak = run / "new-game.pak"
    support.write_pak(new_pak, new_game)
    r = tool("structural", derived, new_pak)
    summary = r.stdout
    check("new game: structural files build", r.returncode == 0, summary + r.stderr)
    for local in ("esf.scn.20", "ver_02_0300_esf.scn.20", "ver_02_0400_esf.scn.20", "ver_02_0500_esf.scn.20",
                  "fightercostumemessage.msg.21"):
        check(f"new game: {local} unchanged, byte-identical to the shipped copy",
              (derived / local).exists() and (derived / local).read_bytes() == (shipped / local).read_bytes())
    check("new game: ver_02_0600 (no new fighter) is not replaced", "ver_02_0600" not in summary)
    new_scene = derived / "ver_03_0100_esf.scn.20"
    folders = scene_folders(new_scene.read_bytes()) if new_scene.exists() else []
    parent = next((i for i, f in enumerate(folders) if f[0] == "fighter035"), None)
    check("new game: ver_03_0100 keeps its folders and adds esf035v05..v104 under fighter035 (no v04)",
          len(folders) == 104 and parent is not None
          and folders[4:] == [(f"esf035v{c:02d}", f"Product/CharParam/esf/esf035/esf035v{c:02d}.scn", parent)
                              for c in range(5, 105)])
    recs = (derived / "records.tsv").read_text().splitlines()
    check("new game: fighter 35 gets records 8200..8299, every shipped record unchanged",
          recs[:3200] == old_records and recs[3200:] == [f"35\t{c}\t{8200 + c - 5}" for c in range(5, 105)])
    table = costume_records((derived / "fightercostumeuserdata.user.2").read_bytes())
    check("new game: table keeps the official records 200 (Arjun costume 2) and 201 (fighter 35)",
          table.get(200, [0, 0, 0, 0, 0])[2:5:2] == [34, 2] and table.get(201, [0, 0, 0, 0, 0])[2:5:2] == [35, 0])

    # ---- the loader on the new game ----
    def make_game(where: Path, pak_files: dict[str, bytes]) -> Path:
        game = run / where
        loader = game / DATA / "loader"
        shutil.copytree(shipped, loader / "static")
        lines = ["#SUFFIXES", "mdf2\t31", "mesh\t230110883", "scn\t20", "tex\t241101895", "user\t2", "#COSTUMES",
                 f"2\tesf002\t0\tproduct/model/esf/esf002/001\t{support.SCENE1}",
                 f"2\tesf002\t2\tproduct/model/esf/esf002/003\t{support.SCENE}", "#FILES"]
        base = dict(pak_files)
        base.update({support.MESH: support.mesh("VANILLA"), support.MDF: support.mdf("VANILLA"),
                     support.TEX: support.texture("VANILLA"), support.STREAM: support.texture("VANILLA_STREAM"),
                     support.SCENE: ("\0" + "\0".join([x.removeprefix("natives/stm/").rsplit(".", 1)[0]
                                                        for x in (support.MESH, support.MDF)] * 2) + "\0").encode("utf-16le"),
                     support.SCENE1: "\0Fixture Outfit 1\0".encode("utf-16le")})
        for name in (support.MESH, support.MDF, support.TEX, support.SCENE, support.SCENE1):
            lines.append(f"{support.path_hash(name):016X}\t2\t{0 if name == support.SCENE1 else 2}\tfixture\t{name}")
        loader.mkdir(parents=True, exist_ok=True)
        (loader / "vanilla_costume_index.tsv").write_text("\n".join(lines) + "\n")
        support.write_pak(game / "re_chunk_000.pak", base)
        (game / "StreetFighter6.exe").write_bytes(b"MZ" + bytes(8190))
        for char, files in (("Luke/Manual Luke", {support.MESH: support.mesh("MANUAL"), support.MDF: support.mdf("MANUAL"),
                                                  support.TEX: support.texture("MANUAL"),
                                                  support.STREAM: support.texture("MANUAL_STREAM")}),
                            ("Downloads/Manual Fighter 35", {MESH35: support.mesh("MANUAL35"), MDF35: support.mdf("MANUAL35"),
                                                             TEX35: support.texture("MANUAL35"),
                                                             STREAM35: support.texture("MANUAL35_STREAM")})):
            for name, data in files.items():
                dst = game / "reframework/costume_mods" / char / name
                dst.parent.mkdir(parents=True, exist_ok=True)
                dst.write_bytes(data)
        return game

    def registry(game: Path) -> dict:
        return json.loads((game / DATA / "registry.json").read_text())

    game = make_game("new-game", new_game)
    log = support.run_loader(args.loader.resolve(), game)
    (run / "new-game.txt").write_text(log, encoding="utf-8")
    check("loader: structural files built from the game, fighter 35 added",
          "structural files built from the game: 7 files, slots for 33 fighters; not in the shipped tables: fighter 35\n" in log)
    check("loader: vanilla index completed with esf034 v02 and esf035 v00 (fighter 35 by path scan, logged)",
          "vanilla index completed from the game: 2 costumes (esf034 v02, esf035 v00)" in log
          and "WARN: esf035 v00: layout not recognised" in log)
    check("loader: costume_mods folder named from the game for fighter 35",
          f"costume_mods folders for fighters of the game not in the shipped list: {NEW_NAME} (fighter 35)" in log
          and (game / "reframework/costume_mods" / NEW_NAME).is_dir()
          and not (game / "reframework/costume_mods/Fighter 1").exists())
    slots = {(s["fighter"], s["new_costume_no"]): s for s in registry(game)["slots"]}
    possession = {(p["fighter"], p["costume_no"]): p for p in registry(game)["possession"]}
    check("loader: the fighter 35 outfit and the Luke outfit get slots, none left out",
          set(slots) == {(2, 5), (35, 5)} and "2 slots checked, 0 left out" in log)
    check("loader: fighter 35 slot uses record 8200 and a model folder after its vanilla 001",
          possession.get((35, 5), {}).get("record_id") == 8200 and slots.get((35, 5), {}).get("new_folder") == "002")
    pak = support.patch_file(game)
    entries = support.read_pak(pak)
    check("loader: ver_03_0100 published (exempt) and equal to the derived one",
          entries.get(support.path_hash(VER.format("03_0100"))) == new_scene.read_bytes())
    check("loader: the unchanged structural files are the shipped bytes",
          all(entries.get(support.path_hash(p)) == (shipped / local).read_bytes()
              for p, local in STATIC_NAMES.items() if p != TABLE_PATH))
    out_table = costume_records(entries[support.path_hash(TABLE_PATH)])
    check("loader: table keeps every official record and adds exactly 5100 and 8200",
          sorted(i for i in out_table if i >= 5000) == [5100, 8200]
          and all(out_table.get(i) == v for i, v in costume_records(new_game[TABLE_PATH]).items()))
    check("loader: unused fighter 35 numbers get its Outfit 1 scene",
          all(entries.get(support.path_hash(f"natives/stm/product/charparam/esf/esf035/esf035v{c:02d}.scn.20"))
              == new_game[SCENE35] for c in range(6, 105)))
    check("loader: original assets stay protected (fighter 35 and Luke vanilla paths, ver_02_0600)",
          all(support.path_hash(p) not in entries
              for p in (MESH35, MDF35, TEX35, STREAM35, SCENE35, SCENE34_2, VER.format("02_0600"),
                        support.MESH, support.MDF, support.TEX)))

    before, stamp = pak.read_bytes(), pak.stat().st_mtime_ns
    log = support.run_loader(args.loader.resolve(), game)
    check("cache: unchanged launch reuses the PAK", "Up to date" in log and pak.read_bytes() == before)
    base = game / "re_chunk_000.pak"
    original = base.read_bytes()
    invalidate(base, support.MESH)
    log = support.run_loader(args.loader.resolve(), game)
    check("cache: Fluffy invalidation of an unrelated row reuses the PAK",
          "Up to date" in log and pak.read_bytes() == before and pak.stat().st_mtime_ns == stamp)
    base.write_bytes(original)

    if args.old_loader:
        old = make_game("new-game-previous-build", new_game)
        olog = support.run_loader(args.old_loader.resolve(), old)
        (run / "new-game-previous-build.txt").write_text(olog, encoding="utf-8")
        oslots = {(s["fighter"], s["new_costume_no"]) for s in registry(old)["slots"]}
        check("previous build: no slot for the fighter 35 outfit (only Luke's)", oslots == {(2, 5)})

    # ---- fallback to the shipped copies ----
    for label, change in (("ver_02_0400 invalidated by Fluffy",
                           lambda g: invalidate(g / "re_chunk_000.pak", VER.format("02_0400"))),
                          ("new scene unreadable", None)):
        files = dict(new_game)
        if change is None:
            files[VER.format("03_0100")] = b"SCN\0 not a scene any more"
        fb = make_game("fallback-" + label.split()[0], files)
        if change:
            change(fb)
        flog = support.run_loader(args.loader.resolve(), fb)
        (run / f"fallback-{label.split()[0]}.txt").write_text(flog, encoding="utf-8")
        fentries = support.read_pak(support.patch_file(fb))
        check(f"fallback ({label}): logged", "WARN: structural files taken from the shipped copies (" in flog)
        check(f"fallback ({label}): the shipped scenes and names published byte for byte",
              all(fentries.get(support.path_hash(p)) == (shipped / local).read_bytes()
                  for p, local in STATIC_NAMES.items() if p != TABLE_PATH)
              and support.path_hash(VER.format("03_0100")) not in fentries)
        fslots = {(s["fighter"], s["new_costume_no"]) for s in registry(fb)["slots"]}
        check(f"fallback ({label}): the Luke slot still made, none for fighter 35 (no shipped record)",
              fslots == {(2, 5)} and sorted(i for i in costume_records(fentries[support.path_hash(TABLE_PATH)])
                                            if i >= 5000) == [5100])

    result = {"loader": str(args.loader.resolve()), "tool": str(args.tool.resolve()),
              "old_loader": str(args.old_loader.resolve()) if args.old_loader else None,
              "scope": "native loader game-derived data; synthetic workspace data; no SF6 runtime test",
              "checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks)}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
