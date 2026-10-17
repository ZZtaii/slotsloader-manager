"""Compare what the loader builds from the game's own files with the shipped loader data.

Read-only on the game. build/game_derived.exe runs the loader's game-derived code
on the base PAK (with the DLC PAKs read over it when given):
  - the six structural files (root scene, ver_02_0300/0400/0500, costume table,
    costume names) must equal the shipped loader/static copies byte for byte, and
    the slot records must equal static_meta.json;
  - the vanilla inventory of every costume must equal vanilla_costume_index.tsv
    (with --dlc: byte for byte when the DLC PAKs are read first, as
    build_vanilla_inventory.py does; with the loader's base-first order the
    costumes it differs on must only gain rows);
  - every fighter of the costume table must have an English name.
Then the actual loader runs in fixture game folders (--base-pak override, DLC
omitted) on outfits copied from the October 13 costumes, as
new_costumes_real_input.py does:
  - with the current data, its PAK must be byte-identical to the previous
    build's (--old-loader, which only uses the shipped copies);
  - with data that predates the update (--old-metadata, e.g. the 1.9.5 data
    without Arjun and C. Viper costume 3) it must complete the vanilla index and
    the slot tables from the game: the same slots and a PAK byte-identical to the
    run with the current data, while the previous build makes no slot.
usage: python tests/game_derived_real_input.py --loader build/costume_loader.exe
           --tool build/game_derived.exe --paktool build/paktool.exe --metadata <loader dir>
           --base-pak <re_chunk_000.pak> [--dlc <pak> ...] --output <new dir>
           [--old-loader <exe>] [--old-metadata <loader dir>]
This checks package/loader behaviour only; it is not a rendered SF6 test.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess

from new_costumes_real_input import OUTFITS, VER_0500, pak_index, run, tree, tsv

STATIC = ["esf.scn.20", "ver_02_0300_esf.scn.20", "ver_02_0400_esf.scn.20", "ver_02_0500_esf.scn.20",
          "fightercostumeuserdata.user.2", "fightercostumemessage.msg.21"]


def sections(text):
    out, cur = {}, None
    for line in text.splitlines():
        if line.startswith("#"):
            cur = line
            out[cur] = []
        elif line:
            out[cur].append(line)
    return out


def by_costume(rows):
    d = {}
    for r in rows:
        p = r.split("\t")
        d.setdefault((int(p[1]), int(p[2])), []).append(r)
    return d


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    for name in ("loader", "tool", "paktool", "metadata", "base-pak", "output"):
        p.add_argument("--" + name, type=Path, required=True)
    p.add_argument("--dlc", type=Path, action="append", default=[])
    p.add_argument("--old-loader", type=Path)
    p.add_argument("--old-metadata", type=Path)
    a = p.parse_args()
    for name in ("loader", "tool", "paktool", "metadata", "base_pak", "output", "old_loader", "old_metadata"):
        if getattr(a, name):
            setattr(a, name, Path(os.path.abspath(getattr(a, name))))
    if a.output.exists():
        p.error("choose a new output directory")
    a.output.mkdir(parents=True)
    paks = [a.base_pak] + [Path(os.path.abspath(d)) for d in a.dlc]
    _, base_before = pak_index(a.base_pak)
    meta_before = tree(a.metadata)
    checks = {}

    def check(name, ok):
        checks[name] = bool(ok)
        print(("PASS " if ok else "FAIL ") + name)

    # ---- structural files and records, built by the loader's code ----
    out = a.output / "structural"
    out.mkdir()
    r = subprocess.run([str(a.tool), "structural", str(out)] + [str(x) for x in paks],
                       capture_output=True, text=True)
    (a.output / "structural.txt").write_text(r.stdout + r.stderr, encoding="utf-8")
    check("structural files build from the game", r.returncode == 0)
    built = sorted(f.name for f in out.iterdir() if f.name not in ("records.tsv", "summary.txt"))
    check("exactly the six shipped structural files are built", built == sorted(STATIC))
    for name in STATIC:
        mine, shipped = out / name, a.metadata / "static" / name
        check(f"{name} equals the shipped copy byte for byte",
              mine.exists() and mine.read_bytes() == shipped.read_bytes())
    meta = json.loads((a.metadata / "static/static_meta.json").read_text(encoding="utf-8"))
    records = [tuple(int(v) for v in line.split("\t")) for line in (out / "records.tsv").read_text().splitlines()]
    check("slot records equal static_meta.json (fighter, costume, record id = ManageId)",
          records == [(m["fighter"], m["costume_no"], m["record_id"]) for m in meta["records"]]
          and all(m["record_id"] == m["manage_id"] for m in meta["records"]))

    # ---- vanilla inventory ----
    shipped_tsv = (a.metadata / "vanilla_costume_index.tsv").read_text(encoding="utf-8")
    inv = a.output / "inventory-loader-order.tsv"
    r = subprocess.run([str(a.tool), "inventory", str(inv)] + [str(x) for x in paks], capture_output=True, text=True)
    (a.output / "inventory.txt").write_text(r.stdout + r.stderr, encoding="utf-8")
    check("every costume of the game inventoried by its layout (no failure, no path scan)",
          r.returncode == 0 and " 0 failed, 0 by path scan" in r.stdout)
    mine, theirs = sections(inv.read_text(encoding="utf-8")), sections(shipped_tsv)
    check("inventory: same suffixes, same costume rows in the same order",
          mine.get("#SUFFIXES") == theirs.get("#SUFFIXES") and mine.get("#COSTUMES") == theirs.get("#COSTUMES"))
    mf, tf = by_costume(mine.get("#FILES", [])), by_costume(theirs.get("#FILES", []))
    differ = sorted(k for k in set(mf) | set(tf) if mf.get(k) != tf.get(k))
    gain_only = all(set(tf.get(k, [])) <= set(mf.get(k, []))
                    and [x for x in mf.get(k, []) if x in set(tf.get(k, []))] == tf.get(k, []) for k in differ)
    inventory_report = {"costumes": len(mf), "identical": len(mf) - len(differ),
                        "differ_in_loader_order": [f"{k[0]}:{k[1]}" for k in differ],
                        "rows_gained": sum(len(mf[k]) - len(tf.get(k, [])) for k in differ)}
    if a.dlc:
        dlc_first = a.output / "inventory-dlc-first.tsv"
        r = subprocess.run([str(a.tool), "inventory", str(dlc_first)] + [str(x) for x in reversed(paks)],
                           capture_output=True, text=True)
        check("inventory with the DLC PAKs read first (build_vanilla_inventory.py's order) equals the TSV byte for byte",
              r.returncode == 0 and dlc_first.read_text(encoding="utf-8") == shipped_tsv)
        check("in the loader's base-first order a costume differs only by gaining rows, order kept",
              gain_only)
    else:
        check("inventory equals the TSV for every costume (no DLC given)", not differ)

    # ---- fighter names ----
    r = subprocess.run([str(a.tool), "names"] + [str(x) for x in paks], capture_output=True, text=True)
    names = {int(line.split("\t")[0]): line.split("\t")[1] for line in r.stdout.splitlines() if "\t" in line}
    fighters = sorted({f for f, _, _ in records})
    check("every fighter of the costume table has an English name in the game", all(names.get(f) for f in fighters))

    # ---- the loader in fixture game folders ----
    index = tsv(a.metadata / "vanilla_costume_index.tsv")
    mods = a.output / "outfits"
    for char, name, fighter, cno, model in OUTFITS:
        rows = [x for x in index["#FILES"] if int(x[1]) == fighter and int(x[2]) == cno
                and x[3] in ("mesh", "mdf2") and x[4].startswith(model)]
        assert rows, f"no indexed mesh/material for {model}"
        for x in rows:
            dst = mods / char / name / x[4]
            dst.parent.mkdir(parents=True, exist_ok=True)
            run([a.paktool, "extract", a.base_pak, x[4], dst])
    mods_before = tree(mods)

    def launch(label, exe, metadata):
        game = a.output / (label + "-game")
        shutil.copytree(metadata, game / "reframework/data/SF6_Costumes_Data/loader")
        shutil.copytree(mods, game / "reframework/costume_mods")
        log = run([exe, game, "--base-pak", a.base_pak], check=False)
        (a.output / (label + ".log")).write_text(log, encoding="utf-8")
        data = game / "reframework/data/SF6_Costumes_Data"
        o = {"log": log, "outfits": []}
        if (data / "outfits.json").exists():
            outfits = json.loads((data / "outfits.json").read_text())
            o["outfits"] = sorted((x["fighter"], x["costume_no"]) for x in outfits.get("outfits", []))
            pak = game / outfits["pak"]
            if pak.exists():
                o["pak"] = pak.read_bytes()
                o["ver_0500"] = a.output / (label + "-ver_02_0500.scn.20")
                run([a.paktool, "extract", pak, VER_0500, o["ver_0500"]], check=False)
                o["repeat"] = run([exe, game, "--base-pak", a.base_pak], check=False)
        return o

    cur = launch("current-data", a.loader, a.metadata)
    log = cur["log"]
    check("current data: the loader builds the structural files from the game",
          "structural files built from the game: 6 files, slots for 32 fighters\n" in log
          and "taken from the shipped copies" not in log)
    check("current data: nothing missing from the shipped index, no new fighter folder",
          "vanilla index completed" not in log and "costume_mods folders for fighters" not in log)
    check("current data: three slots, none left out",
          "3 slots checked, 0 left out" in log and cur["outfits"] == [(30, 5), (34, 5), (34, 6)])
    check("current data: unchanged repeat launch reuses the cache", "Up to date" in cur.get("repeat", ""))
    if a.old_loader:
        prev = launch("current-data-previous-build", a.old_loader, a.metadata)
        check("current data: PAK byte-identical to the previous build's (shipped copies only)",
              "pak" in cur and cur["pak"] == prev.get("pak"))
    if a.old_metadata:
        old = launch("old-data", a.loader, a.old_metadata)
        olog = old["log"]
        check("old data: the vanilla index is completed from the game with the four new costumes",
              "vanilla index completed from the game: 4 costumes (esf030 v03, esf034 v00, esf034 v01, esf034 a00)" in olog)
        check("old data: slot tables built from the game, fighter 34 added",
              "slots for 32 fighters; not in the shipped tables: fighter 34\n" in olog)
        check("old data: C. Viper costume 4 and Arjun outfits recognised",
              "-> esf030 costume 3 (folder 004)" in olog and "-> esf034 costume 0 (folder 001)" in olog
              and "-> esf034 costume 1 (folder 002)" in olog)
        check("old data: the same three slots", old["outfits"] == cur["outfits"] and "3 slots checked, 0 left out" in olog)
        check("old data: ver_02_0500 built from the game equals the 1.9.6 static copy byte for byte",
              "ver_0500" in old and old["ver_0500"].exists()
              and old["ver_0500"].read_bytes() == (a.metadata / "static/ver_02_0500_esf.scn.20").read_bytes())
        check("old data: PAK byte-identical to the one made with the current data",
              "pak" in old and old["pak"] == cur.get("pak"))
        check("old data: unchanged repeat launch reuses the cache", "Up to date" in old.get("repeat", ""))
        if a.old_loader:
            prev_old = launch("old-data-previous-build", a.old_loader, a.old_metadata)
            check("old data: the previous build makes no slot from the new official costumes",
                  not prev_old["outfits"] and "esf034 costume" not in prev_old["log"])
    check("input metadata preserved", tree(a.metadata) == meta_before)
    check("outfit sources preserved", tree(mods) == mods_before)
    check("game PAK index preserved", pak_index(a.base_pak)[1] == base_before)

    report = {"scope": "loader game-derived data vs shipped data, current game read-only; fixture runs omit DLC; "
                       "no rendered game test",
              "inventory": inventory_report, "checks": checks,
              "passed": sum(checks.values()), "total": len(checks)}
    (a.output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"{report['passed']}/{report['total']} passed; report: {a.output / 'results.json'}")
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
