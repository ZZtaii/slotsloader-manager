#!/usr/bin/env python3
"""inventory_to_tsv.py -- convert build_vanilla_inventory.py output to the loader index.

Input:  vanilla_costume_paths.json (from build_vanilla_inventory.py)
Output: vanilla_costume_index.tsv, read by VanillaIndex::load in loader_core.cpp

Format (LF line endings, UTF-8, one trailing newline):
  #SUFFIXES   ext <TAB> version                      (sorted by extension)
  #COSTUMES   fighter <TAB> esfNNN <TAB> costume_no <TAB> model_dir <TAB> scene
  #FILES      HASH16 <TAB> fighter <TAB> costume_no <TAB> kind <TAB> pak path

Costumes and files keep the inventory order (probe order: fighter, v00..v09,
a00..a09; per costume the scene, its resources, user data, MDF2 textures and
CCVD CMD references). HASH16 is the upper-case murmur3 UTF-16 path hash.

The conversion is strict: an inventory with unresolved entries, files missing
from the archives or a zero hash is rejected instead of producing a partial
index. Use --compare to diff the result against a previously shipped index.
"""

import argparse
import json
import sys


def convert(inv):
    problems = []
    if inv.get("unresolved"):
        problems.append(f"{len(inv['unresolved'])} unresolved inventory entries: "
                        + ", ".join(list(inv["unresolved"])[:5]))
    lines = ["#SUFFIXES"]
    for ext, full in sorted(inv["version_suffixes"].items()):
        prefix = f".{ext}."
        if not full.startswith(prefix):
            problems.append(f"bad suffix entry {ext}: {full}")
            continue
        lines.append(f"{ext}\t{full[len(prefix):]}")
    lines.append("#COSTUMES")
    for c in inv["costumes"]:
        lines.append(f"{c['fighter']}\t{c['fighter_dir']}\t{c['costume_no']}\t"
                     f"{c['model_dir']}\t{c['scene']}")
    lines.append("#FILES")
    for c in inv["costumes"]:
        for f in c["files"]:
            if not f.get("in_pak") or not f.get("hash"):
                problems.append(f"{c['fighter_dir']} costume {c['costume_no']}: "
                                f"not in archives: {f['path']}")
                continue
            for field in (f["path"], f["kind"]):
                if "\t" in field or "\n" in field or " " in field:
                    problems.append(f"unsafe field: {field!r}")
            lines.append(f"{f['hash']:016X}\t{c['fighter']}\t{c['costume_no']}\t"
                         f"{f['kind']}\t{f['path']}")
    return "\n".join(lines) + "\n", problems


def sections(text):
    out = {}
    cur = None
    for line in text.splitlines():
        if line.startswith("#"):
            cur = line
            out[cur] = []
        elif line and cur:
            out[cur].append(line)
    return out


def compare(new_text, old_text):
    """Return a report dict: per-costume equality between two indexes."""
    new, old = sections(new_text), sections(old_text)

    def by_costume(rows):
        d = {}
        for r in rows:
            p = r.split("\t")
            d.setdefault((int(p[1]), int(p[2])), []).append(r)
        return d

    nf, of = by_costume(new.get("#FILES", [])), by_costume(old.get("#FILES", []))
    nc = {(int(r.split("\t")[0]), int(r.split("\t")[2])): r for r in new.get("#COSTUMES", [])}
    oc = {(int(r.split("\t")[0]), int(r.split("\t")[2])): r for r in old.get("#COSTUMES", [])}
    rep = {
        "suffixes_equal": new.get("#SUFFIXES") == old.get("#SUFFIXES"),
        "added_costumes": sorted(k for k in nc if k not in oc),
        "removed_costumes": sorted(k for k in oc if k not in nc),
        "costume_row_changed": sorted(k for k in nc if k in oc and nc[k] != oc[k]),
        "files_identical": [], "files_changed": {},
    }
    for k in sorted(set(nc) & set(oc)):
        a, b = nf.get(k, []), of.get(k, [])
        if a == b:
            rep["files_identical"].append(k)
        else:
            sa, sb = set(a), set(b)
            rep["files_changed"][f"{k[0]}:{k[1]}"] = {
                "same_set_different_order": sa == sb,
                "added": sorted(sa - sb), "removed": sorted(sb - sa),
            }
    new_seq = [k for k in nc if k in oc]
    old_seq = [k for k in oc if k in nc]
    rep["common_costume_order_equal"] = new_seq == old_seq
    return rep


def main():
    ap = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    ap.add_argument("inventory", help="vanilla_costume_paths.json")
    ap.add_argument("output", help="vanilla_costume_index.tsv to write")
    ap.add_argument("--compare", help="previous index to diff against")
    ap.add_argument("--report", help="write the comparison as JSON here")
    args = ap.parse_args()

    with open(args.inventory, encoding="utf-8") as fh:
        inv = json.load(fh)
    text, problems = convert(inv)
    if problems:
        for p in problems[:20]:
            print("ERROR:", p, file=sys.stderr)
        print(f"refusing to write index: {len(problems)} problem(s)", file=sys.stderr)
        return 1
    with open(args.output, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    nfiles = sum(len(c["files"]) for c in inv["costumes"])
    print(f"wrote {args.output}: {len(inv['costumes'])} costumes, {nfiles} files")

    if args.compare:
        with open(args.compare, encoding="utf-8", newline="") as fh:
            old_text = fh.read()
        rep = compare(text, old_text)
        rep["identical_bytes"] = text == old_text
        print(f"suffixes equal: {rep['suffixes_equal']}")
        print(f"common costume order equal: {rep['common_costume_order_equal']}")
        print(f"costumes with identical file rows: {len(rep['files_identical'])}")
        print(f"costumes with changed file rows: {len(rep['files_changed'])} "
              f"{sorted(rep['files_changed'])}")
        print(f"costume rows changed: {rep['costume_row_changed']}")
        print(f"added costumes: {rep['added_costumes']}")
        print(f"removed costumes: {rep['removed_costumes']}")
        if args.report:
            with open(args.report, "w", encoding="utf-8") as fh:
                json.dump(rep, fh, indent=2, default=str)
    return 0


if __name__ == "__main__":
    sys.exit(main())
