"""Check the loader data shipped for the October 13 official costumes.

C. Viper costume 3 (model folder esf030/004) and fighter 34 (Arjun) costumes 0/1
(esf034/001, esf034/002) must be in vanilla_costume_index.tsv, and fighter 34 must
have reserved extra slots that never collide with the official records 137, 143
and 147 of the current game table.

Inputs are read only: a loader data folder (vanilla_costume_index.tsv + static/),
the current base PAK (index and three structural files, extracted with paktool
into --output), and build/costume_table_refresh.exe for the native rebase.

usage: python tests/new_costumes_data.py --metadata <loader dir> --base-pak <re_chunk_000.pak>
           --paktool build/paktool.exe --refresh build/costume_table_refresh.exe --output <new dir>
"""
import argparse
import json
import re
import struct
import subprocess
from pathlib import Path

from costume_table_fixture import TABLE_PATH, costume_records
from fluffy_coexistence import path_hash

VER_0500 = "natives/stm/product/flow/version_update/ver_02_0500/esf.scn.20"
NEW_COSTUMES = {  # (fighter, costume_no): (model folder, scene)
    (30, 3): ("natives/stm/product/model/esf/esf030/004",
              "natives/stm/product/charparam/esf/esf030/esf030v03.scn.20"),
    (34, 0): ("natives/stm/product/model/esf/esf034/001",
              "natives/stm/product/charparam/esf/esf034/esf034v00.scn.20"),
    (34, 1): ("natives/stm/product/model/esf/esf034/002",
              "natives/stm/product/charparam/esf/esf034/esf034v01.scn.20"),
}
OFFICIAL_NEW_IDS = {137: (34, 0), 143: (34, 1), 147: (30, 3)}

passed = 0
failed = []


def check(name, ok):
    global passed
    print(("PASS " if ok else "FAIL ") + name)
    if ok:
        passed += 1
    else:
        failed.append(name)


def pak_index(path):
    with path.open("rb") as f:
        header = f.read(16)
        assert header[:4] == b"KPKA" and header[4] == 4
        count = struct.unpack_from("<I", header, 8)[0]
        rows = f.read(48 * count)
    return {struct.unpack_from("<Q", rows, at)[0] for at in range(0, len(rows), 48)}


def read_tsv(path):
    sections, cur = {}, None
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("#"):
            cur = line
            sections[cur] = []
        elif line:
            sections[cur].append(line.split("\t"))
    return sections


def scene_folders(b):
    """(name, scene path, parent) of every folder of a via.Folder-only scene (independent reader)."""
    fc, ft, do = struct.unpack_from("<I", b, 12)[0], struct.unpack_from("<Q", b, 24)[0], struct.unpack_from("<Q", b, 56)[0]
    folders = [struct.unpack_from("<ii", b, ft + 8 * i) for i in range(fc)]
    oc, ic = struct.unpack_from("<II", b, do + 8)
    objects = struct.unpack_from("<%dI" % oc, b, do + 48)
    data = do + struct.unpack_from("<Q", b, do + 32)[0]
    pos, inst = 0, [None]

    def string(p):
        p = (p + 3) & ~3
        n = struct.unpack_from("<I", b, data + p)[0]
        return b[data + p + 4: data + p + 4 + 2 * n].decode("utf-16le").rstrip("\0"), p + 4 + 2 * n
    for _ in range(1, ic):
        name, pos = string(pos)
        _, pos = string(pos)
        pos += 3
        scene, pos = string(pos)
        pos = ((pos + 7) & ~7) + 24
        inst.append((name, scene))
    assert data + pos == len(b), "scene data length"
    return [(inst[objects[oid]][0], inst[objects[oid]][1], parent) for oid, parent in folders]


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    for name in ("metadata", "base-pak", "paktool", "refresh", "output"):
        ap.add_argument("--" + name, type=Path, required=True)
    a = ap.parse_args()
    if a.output.exists():
        ap.error("choose a new output directory")
    a.output.mkdir(parents=True)

    def extract(path, name):
        out = a.output / name
        subprocess.run([str(a.paktool), "extract", str(a.base_pak), path, str(out)],
                       check=True, capture_output=True)
        return out.read_bytes()

    base = pak_index(a.base_pak)
    tsv = read_tsv(a.metadata / "vanilla_costume_index.tsv")
    costumes = {(int(r[0]), int(r[2])): r for r in tsv["#COSTUMES"]}
    files = {}
    for r in tsv["#FILES"]:
        files.setdefault((int(r[1]), int(r[2])), []).append(r)

    # ---- vanilla_costume_index.tsv ----
    for key, (model, scene) in NEW_COSTUMES.items():
        row = costumes.get(key)
        check(f"index lists fighter {key[0]} costume {key[1]} with model {model[-10:]} and its scene",
              row is not None and row[3] == model and row[4] == scene)
        rows = files.get(key, [])
        check(f"index has file rows for fighter {key[0]} costume {key[1]} including its scene and meshes",
              any(r[4] == scene and r[3] == "scene" for r in rows)
              and any(r[3] == "mesh" and r[4].startswith(model + "/") for r in rows))
        check(f"every indexed file of fighter {key[0]} costume {key[1]} is in the current base PAK",
              bool(rows) and all(int(r[0], 16) in base for r in rows))
    check("fighter 34 action costume (100) indexed without a model folder",
          (34, 100) in costumes and costumes[(34, 100)][3] == "")
    check("every index row hash equals the murmur3 hash of its path",
          all(int(r[0], 16) == path_hash(r[4]) for r in tsv["#FILES"]))
    viper_folders = {int(m.group(1)) for r in files.get((30, 3), [])
                     for m in [re.search(r"/model/esf/esf030/(\d{3})/", r[4])] if m}
    check("C. Viper folder 004 is a vanilla model folder in the index", 4 in viper_folders)
    check("index keeps the 172 previous costumes and adds exactly the four new ones",
          len(costumes) == 176 and {k for k in costumes if k[0] == 34} == {(34, 0), (34, 1), (34, 100)})

    # ---- static_meta.json ----
    meta = json.loads((a.metadata / "static/static_meta.json").read_text(encoding="utf-8"))
    recs = meta["records"]
    arjun = [r for r in recs if r["fighter"] == 34]
    check("fighter 34 has 100 reserved records, ids 8100..8199, costumes 5..104",
          [r["record_id"] for r in arjun] == list(range(8100, 8200))
          and [r["costume_no"] for r in arjun] == list(range(5, 105))
          and all(r["manage_id"] == r["record_id"] for r in arjun))
    check("previous 31 fighters keep their reserved record ids",
          [(r["fighter"], r["costume_no"], r["record_id"]) for r in recs if r["fighter"] != 34]
          == [(f, c, 5000 + i * 100 + c - 5) for i, f in enumerate(meta["fighter_ids"][:31]) for c in range(5, 105)])
    check("reserved record ids stay inside the plugin sweep 5000..8199 and are unique",
          len({r["record_id"] for r in recs}) == len(recs) == 3200
          and min(r["record_id"] for r in recs) == 5000 and max(r["record_id"] for r in recs) == 8199)
    check("ver_02_0500 declares fighter 34 and is a static file",
          meta.get("dlc_fighter_dirs_0500") == ["esf034"]
          and {"local": "ver_02_0500_esf.scn.20", "pak_path": VER_0500} in meta["files"])

    # ---- current vanilla table vs reserved records ----
    current = costume_records(extract(TABLE_PATH, "current-vanilla-table.user.2"))
    check("current game table has official records 137/143/147 as expected",
          all(current.get(i) and (current[i][2], current[i][4]) == k for i, k in OFFICIAL_NEW_IDS.items()))
    official_keys = {(r[2], r[4]) for r in current.values()}
    check("no reserved record id equals an official record id (incl. 137/143/147)",
          not ({r["record_id"] for r in recs} & set(current)))
    check("no reserved slot overlaps an official costume (incl. fighter 34 costumes 0/1, C. Viper costume 3)",
          not ({(r["fighter"], r["costume_no"]) for r in recs} & official_keys))

    # ---- static costume table ----
    static_table = (a.metadata / "static/fightercostumeuserdata.user.2").read_bytes()
    shipped = costume_records(static_table)
    check("static table holds every current official record unchanged",
          all(shipped.get(i) == v for i, v in current.items()))
    check("static table reserved records equal static_meta.json",
          sorted((v[0], v[2], v[4]) for i, v in shipped.items() if i not in current)
          == sorted((r["record_id"], r["fighter"], r["costume_no"]) for r in recs))
    refreshed = a.output / "refreshed-static-table.user.2"
    run = subprocess.run([str(a.refresh), "--refresh", str(a.metadata / "static/fightercostumeuserdata.user.2"),
                          str(a.output / "current-vanilla-table.user.2"), str(refreshed)],
                         capture_output=True, text=True)
    check("native refresh accepts the static table against the current game table",
          run.returncode == 0 and refreshed.exists())
    check("native refresh of the static table is byte-identical (built from the current table)",
          refreshed.exists() and refreshed.read_bytes() == static_table)

    # ---- ver_02_0500 scene ----
    vanilla_scene = scene_folders(extract(VER_0500, "current-ver_02_0500.scn.20"))
    static_scene = scene_folders((a.metadata / "static/ver_02_0500_esf.scn.20").read_bytes())
    check("static ver_02_0500 keeps the 7 vanilla folders first (fighter034 v00/v01/a00/wts00, esf030v03)",
          static_scene[:len(vanilla_scene)] == vanilla_scene and len(vanilla_scene) == 7
          and ("esf030v03", "Product/CharParam/esf/esf030/esf030v03.scn", 5) in vanilla_scene)
    parent = next(i for i, f in enumerate(static_scene) if f[0] == "fighter034")
    check("static ver_02_0500 adds esf034v05..v104 under the fighter034 folder",
          static_scene[len(vanilla_scene):] == [(f"esf034v{c:02d}", f"Product/CharParam/esf/esf034/esf034v{c:02d}.scn", parent)
                                                 for c in range(5, 105)])
    for name in ("esf.scn.20", "ver_02_0300_esf.scn.20", "ver_02_0400_esf.scn.20"):
        pak = "natives/stm/product/charparam/esf/esf.scn.20" if name == "esf.scn.20" else \
            "natives/stm/product/flow/version_update/%s/esf.scn.20" % name[:11]
        v = scene_folders(extract(pak, "current-" + name))
        s = scene_folders((a.metadata / "static" / name).read_bytes())
        check(f"static {name} keeps every current vanilla folder first", s[:len(v)] == v)

    report = {"passed": passed, "failed": failed, "total": passed + len(failed)}
    (a.output / "results.json").write_text(json.dumps(report, indent=2))
    print(f"{passed}/{passed + len(failed)} passed; report: {a.output / 'results.json'}")
    return 0 if not failed else 1


if __name__ == "__main__":
    raise SystemExit(main())
