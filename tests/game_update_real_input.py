"""Compare native loader output against current vanilla records, using copied inputs.

The large installed PAK is opened read-only via --base-pak. No game launches or
writes to the installation occur. DLC archives are deliberately omitted.
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


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def inventory(root):
    return {p.relative_to(root).as_posix(): sha(p) for p in root.rglob("*") if p.is_file()}


def pak_index(path):
    stat = path.stat()
    with path.open("rb") as f:
        header = f.read(16)
        assert header[:4] == b"KPKA" and header[4] == 4
        count = struct.unpack_from("<I", header, 8)[0]
        rows = f.read(48 * count)
    assert len(rows) == 48 * count
    return {struct.unpack_from("<Q", rows, at)[0] for at in range(0, len(rows), 48)} - {0}, {
        "size": stat.st_size, "mtime_ns": stat.st_mtime_ns,
        "header_table_sha256": hashlib.sha256(header + rows).hexdigest()}


def run(args):
    result = subprocess.run([str(a) for a in args], capture_output=True, text=True,
                            errors="replace", timeout=180)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout + result.stderr


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ("loader", "old-loader", "paktool", "metadata", "base-pak", "mod", "output"):
        p.add_argument("--" + name, type=Path, required=True)
    a = p.parse_args()
    for name in ("loader", "old_loader", "paktool", "metadata", "base_pak", "mod", "output"):
        setattr(a, name, Path(os.path.abspath(getattr(a, name))))
    if a.output.exists():
        p.error("Choose a new output directory")
    a.output.mkdir(parents=True)
    originals, before_base = pak_index(a.base_pak)
    mod_hashes = lambda: inventory(a.mod) if a.mod.is_dir() else sha(a.mod)
    before_meta, before_mod = inventory(a.metadata), mod_hashes()
    vanilla_path = a.output / "current-vanilla-table.user.2"
    run([a.paktool, "extract", a.base_pak, TABLE_PATH, vanilla_path])
    expected = costume_records(vanilla_path.read_bytes())
    old_static = costume_records((a.metadata / "static/fightercostumeuserdata.user.2").read_bytes())
    added = sorted(set(expected) - set(old_static))
    observations = []
    allowed = {path_hash(path) for path in STRUCTURAL.values()}
    for label, exe in (("previous", a.old_loader), ("updated", a.loader)):
        game = a.output / (label + "-game")
        data = game / "reframework/data/SF6_Costumes_Data"
        shutil.copytree(a.metadata, data / "loader")
        mods = game / "reframework/costume_mods/Fixture"
        mods.mkdir(parents=True)
        if a.mod.is_dir():
            shutil.copytree(a.mod, mods / a.mod.name)
        else:
            shutil.copy2(a.mod, mods / a.mod.name)
        log = run([exe, game, "--base-pak", a.base_pak])
        (a.output / (label + ".log")).write_text(log, encoding="utf-8")
        outfits = json.loads((data / "outfits.json").read_text())
        pak = game / outfits["pak"]
        table_path = a.output / (label + "-table.user.2")
        run([a.paktool, "extract", pak, TABLE_PATH, table_path])
        records = costume_records(table_path.read_bytes())
        output_hashes, output_meta = pak_index(pak)
        repeat = run([exe, game, "--base-pak", a.base_pak])
        (a.output / (label + "-repeat.log")).write_text(repeat, encoding="utf-8")
        observations.append({"label": label, "output": str(pak), "pak": output_meta,
            "outfits": outfits.get("outfits", []), "record_count": len(records),
            "missing_vanilla_ids": sorted(set(expected) - set(records)),
            "changed_vanilla_ids": [rid for rid in expected if rid in records and expected[rid] != records[rid]],
            "original_asset_collisions": [f"{h:016x}" for h in sorted(output_hashes & (originals - allowed))],
            "cached_repeat": "Up to date" in repeat,
            "cached_bytes_unchanged": pak_index(pak)[1] == output_meta})
    previous, updated = observations
    checks = {
        "previous_build_loses_new_vanilla_records": bool(added) and previous["missing_vanilla_ids"] == added,
        "updated_preserves_every_vanilla_record": not updated["missing_vanilla_ids"] and not updated["changed_vanilla_ids"],
        "same_manual_outfits_retained": bool(updated["outfits"]) and previous["outfits"] == updated["outfits"],
        "no_original_asset_collisions": not updated["original_asset_collisions"],
        "unchanged_repeat_is_cached": updated["cached_repeat"] and updated["cached_bytes_unchanged"],
        "input_metadata_preserved": inventory(a.metadata) == before_meta,
        "input_mod_preserved": mod_hashes() == before_mod,
        "game_pak_index_preserved": pak_index(a.base_pak)[1] == before_base,
    }
    report = {"scope": "Actual native loader and current base PAK; DLC omitted; no rendered game test",
              "source_mod": str(a.mod), "current_vanilla_records": len(expected), "new_vanilla_ids": added,
              "observations": observations, "checks": checks, "passed": sum(checks.values()), "total": len(checks)}
    (a.output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "total": report["total"], "checks": checks}, indent=2))
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
