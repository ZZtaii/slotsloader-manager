"""Run the native loader on outfits made from the October 13 official costumes.

Three manual outfits are made by extracting, read-only with paktool, the vanilla
meshes and materials of C. Viper costume 3 (esf030/004) and fighter 34 (Arjun)
costumes 0 and 1 (esf034/001, esf034/002) from the current base PAK. The loader
runs on copied metadata in new fixture game folders (--base-pak override); DLC
archives are omitted. With --old-loader/--old-metadata the previous build and data
run on the same outfits for comparison. Nothing is written to the installation.

usage: python tests/new_costumes_real_input.py --loader build/costume_loader.exe
           --paktool build/paktool.exe --metadata <loader dir> --base-pak <re_chunk_000.pak>
           --output <new dir> [--old-loader <exe> --old-metadata <loader dir>]
This checks package/loader behavior only; it is not a rendered SF6 test.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess

from costume_table_fixture import TABLE_PATH, costume_records
from fluffy_coexistence import STRUCTURAL, path_hash

VER_0500 = "natives/stm/product/flow/version_update/ver_02_0500/esf.scn.20"
OUTFITS = [  # (character folder, outfit name, fighter, costume_no, model folder)
    ("C. Viper", "Costume 4 copy", 30, 3, "natives/stm/product/model/esf/esf030/004/"),
    ("Arjun", "Costume 1 copy", 34, 0, "natives/stm/product/model/esf/esf034/001/"),
    ("Arjun", "Costume 2 copy", 34, 1, "natives/stm/product/model/esf/esf034/002/"),
]


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def tree(root):
    return {p.relative_to(root).as_posix(): sha(p) for p in root.rglob("*") if p.is_file()}


def pak_index(path):
    stat = path.stat()
    with path.open("rb") as f:
        header = f.read(16)
        assert header[:4] == b"KPKA" and header[4] == 4
        count = struct.unpack_from("<I", header, 8)[0]
        rows = f.read(48 * count)
    return ({struct.unpack_from("<Q", rows, at)[0] for at in range(0, len(rows), 48)} - {0},
            {"size": stat.st_size, "mtime_ns": stat.st_mtime_ns,
             "header_table_sha256": hashlib.sha256(header + rows).hexdigest()})


def run(args, check=True):
    r = subprocess.run([str(a) for a in args], capture_output=True, text=True, errors="replace", timeout=600)
    if check and r.returncode:
        raise RuntimeError(r.stdout + r.stderr)
    return r.stdout + r.stderr


def tsv(path):
    sec, out = None, {"#COSTUMES": [], "#FILES": []}
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("#"):
            sec = line
        elif line and sec in out:
            out[sec].append(line.split("\t"))
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    for name in ("loader", "paktool", "metadata", "base-pak", "output"):
        p.add_argument("--" + name, type=Path, required=True)
    p.add_argument("--old-loader", type=Path)
    p.add_argument("--old-metadata", type=Path)
    a = p.parse_args()
    for name in ("loader", "paktool", "metadata", "base_pak", "output", "old_loader", "old_metadata"):
        if getattr(a, name):
            setattr(a, name, Path(os.path.abspath(getattr(a, name))))
    if a.output.exists():
        p.error("choose a new output directory")
    a.output.mkdir(parents=True)
    originals, base_before = pak_index(a.base_pak)
    meta_before = tree(a.metadata)
    index = tsv(a.metadata / "vanilla_costume_index.tsv")

    # Outfits: the vanilla meshes and materials of each new costume, extracted read-only
    mods = a.output / "outfits"
    for char, name, fighter, cno, model in OUTFITS:
        rows = [r for r in index["#FILES"] if int(r[1]) == fighter and int(r[2]) == cno
                and r[3] in ("mesh", "mdf2") and r[4].startswith(model)]
        assert rows, f"no indexed mesh/material for {model}"
        for r in rows:
            dst = mods / char / name / r[4]
            dst.parent.mkdir(parents=True, exist_ok=True)
            run([a.paktool, "extract", a.base_pak, r[4], dst])
    mods_before = tree(mods)
    vanilla_table = a.output / "current-vanilla-table.user.2"
    run([a.paktool, "extract", a.base_pak, TABLE_PATH, vanilla_table])
    official = costume_records(vanilla_table.read_bytes())
    vanilla_folders = {}
    for r in index["#FILES"]:
        parts = r[4].split("/")
        if len(parts) > 7 and parts[3] == "model" and parts[5].startswith("esf") and parts[6].isdigit():
            vanilla_folders.setdefault(int(r[1]), set()).add(int(parts[6]))

    runs = [("updated", a.loader, a.metadata)]
    if a.old_loader and a.old_metadata:
        runs.insert(0, ("previous", a.old_loader, a.old_metadata))
    allowed = {path_hash(path) for path in STRUCTURAL.values()} | {path_hash(VER_0500)}
    obs = {}
    for label, exe, metadata in runs:
        game = a.output / (label + "-game")
        data = game / "reframework/data/SF6_Costumes_Data"
        shutil.copytree(metadata, data / "loader")
        shutil.copytree(mods, game / "reframework/costume_mods")
        log = run([exe, game, "--base-pak", a.base_pak], check=False)
        (a.output / (label + ".log")).write_text(log, encoding="utf-8")
        o = {"log": log}
        outfits_json = data / "outfits.json"
        if outfits_json.exists() and (game / json.loads(outfits_json.read_text())["pak"]).exists():
            outfits = json.loads(outfits_json.read_text())
            pak = game / outfits["pak"]
            o["outfits"] = outfits.get("outfits", [])
            o["registry"] = json.loads((data / "registry.json").read_text())
            hashes, o["pak"] = pak_index(pak)
            o["hashes"] = hashes
            table = a.output / (label + "-table.user.2")
            run([a.paktool, "extract", pak, TABLE_PATH, table])
            o["records"] = costume_records(table.read_bytes())
            scene = a.output / (label + "-ver_02_0500.scn.20")
            out = run([a.paktool, "extract", pak, VER_0500, scene], check=False)
            o["ver_0500"] = scene.read_bytes() if scene.exists() else None
            o["repeat"] = run([exe, game, "--base-pak", a.base_pak], check=False)
            (a.output / (label + "-repeat.log")).write_text(o["repeat"], encoding="utf-8")
            o["pak_after_repeat"] = pak_index(pak)[1]
        obs[label] = o

    u = obs["updated"]
    log = u["log"]
    outfits = u.get("outfits", [])
    slots = u.get("registry", {}).get("slots", [])
    recs = u.get("records", {})
    by_based = {(o["fighter"], o.get("based_on")): o for o in outfits}
    checks = {
        "C. Viper costume 4 outfit is recognised as costume 3 (folder 004)":
            "-> esf030 costume 3 (folder 004)" in log,
        "Arjun outfits are recognised as costumes 0 (folder 001) and 1 (folder 002)":
            "-> esf034 costume 0 (folder 001)" in log and "-> esf034 costume 1 (folder 002)" in log,
        "three slots created and none left out": "3 slots checked, 0 left out" in log and len(outfits) == 3,
        "slots use reserved numbers 5+ and their reserved record ids":
            all(o["costume_no"] >= 5 for o in outfits)
            and {(o["fighter"], o["costume_no"]) for o in outfits} == {(30, 5), (34, 5), (34, 6)}
            and sorted(s["fighter"] for s in slots) == [30, 34, 34],
        "slot model folders are new, never a vanilla folder (C. Viper 004, Arjun 001/002)":
            bool(slots) and all(int(s["new_folder"]) not in vanilla_folders.get(s["fighter"], set())
                                and int(s["new_folder"]) > max(vanilla_folders.get(s["fighter"], {0})) for s in slots),
        "output table keeps every current official record unchanged (incl. 137/143/147)":
            bool(recs) and all(recs.get(i) == v for i, v in official.items()),
        "output table adds exactly the slot records 7700, 8100, 8101":
            sorted(set(recs) - set(official)) == [7700, 8100, 8101]
            and {(recs[i][2], recs[i][4]) for i in (7700, 8100, 8101)} == {(30, 5), (34, 5), (34, 6)},
        "output PAK carries the static ver_02_0500 scene byte for byte":
            u.get("ver_0500") == (a.metadata / "static/ver_02_0500_esf.scn.20").read_bytes(),
        "every reserved Arjun and C. Viper slot number has a scene in the output":
            "hashes" in u and all(path_hash(f"natives/stm/product/charparam/esf/esf0{f}/esf0{f}v{c:02d}.scn.20") in u["hashes"]
                                  for f in (30, 34) for c in range(5, 105)),
        "no original asset collisions": "hashes" in u and not (u["hashes"] & (originals - allowed)),
        "unchanged repeat launch reuses the cache": "Up to date" in u.get("repeat", "")
            and u.get("pak_after_repeat") == u.get("pak"),
        "input metadata preserved": tree(a.metadata) == meta_before,
        "outfit sources preserved": tree(mods) == mods_before,
        "game PAK index preserved": pak_index(a.base_pak)[1] == base_before,
    }
    if "previous" in obs:
        prev = obs["previous"]
        checks["previous build and data create no slot from the new official costumes"] = (
            "esf034 costume" not in prev["log"] and "esf030 costume 3" not in prev["log"]
            and not prev.get("outfits"))
    report = {"scope": "Native loader + current base PAK, outfits copied from vanilla files; DLC omitted; no rendered game test",
              "outfits": outfits, "slots": slots,
              "observations": {k: {kk: vv for kk, vv in v.items() if kk not in ("log", "repeat", "hashes", "records", "ver_0500")}
                               for k, v in obs.items()},
              "checks": checks, "passed": sum(checks.values()), "total": len(checks)}
    (a.output / "results.json").write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")
    for k, v in checks.items():
        print(("PASS " if v else "FAIL ") + k)
    print(f"{report['passed']}/{report['total']} passed; report: {a.output / 'results.json'}")
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
