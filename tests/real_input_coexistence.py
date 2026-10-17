"""Compare old/current native loaders on copied Leon ZIP and loader metadata.

The actual base PAK is opened read-only through --base-pak. All archive caches,
generated PAKs, logs and registries are confined to new workspace fixtures.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess

from fluffy_coexistence import ROOT, STRUCTURAL, path_hash


def digest(path: Path) -> str:
    hashed = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            hashed.update(block)
    return hashed.hexdigest()


def pak_index(path: Path) -> tuple[set[int], dict]:
    info = path.stat()
    with path.open("rb") as source:
        header = source.read(16)
        magic, major, _, _, count, _ = struct.unpack("<IBBhII", header)
        if magic != 0x414B504B or major != 4:
            raise ValueError(f"not a v4 PAK: {path}")
        table = source.read(48 * count)
    hashes = {struct.unpack_from("<Q", table, 48 * i)[0] for i in range(count)}
    hashes.discard(0)
    return hashes, {"bytes": info.st_size, "mtime_ns": info.st_mtime_ns,
                    "entries": count, "nonzero_hashes": len(hashes),
                    "header_table_sha256": hashlib.sha256(header + table).hexdigest()}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old-loader", type=Path, required=True)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--source-game", type=Path, required=True)
    parser.add_argument("--source-zip", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    out = args.output.resolve()
    if out.exists():
        parser.error("output must be a new directory")
    source_game = args.source_game.resolve()
    base = source_game / "re_chunk_000.pak"
    metadata = source_game / "reframework/data/SF6_Costumes_Data/loader"
    source_zip = args.source_zip.resolve()
    originals, base_before = pak_index(base)
    metadata_before = {str(path.relative_to(metadata)): digest(path)
                       for path in metadata.rglob("*") if path.is_file()}
    zip_before = digest(source_zip)
    indexed_names = {}
    files_section = False
    for line in (metadata / "vanilla_costume_index.tsv").read_text().splitlines():
        if line.startswith("#"):
            files_section = line.startswith("#FILES")
        elif files_section and line:
            cells = line.split("\t")
            if len(cells) >= 5:
                indexed_names[int(cells[0], 16)] = cells[4]
    originals.update(indexed_names)
    allowed_structural = {path_hash(path) for path in STRUCTURAL.values()}
    protected = originals - allowed_structural
    out.mkdir(parents=True)
    observations = []
    for label, exe in (("baseline", args.old_loader), ("fixed", args.loader)):
        print(f"Preparing {label} fixture", flush=True)
        game = out / (label + "-game")
        game.mkdir()
        shutil.copytree(metadata, game / "reframework/data/SF6_Costumes_Data/loader")
        mods = game / "reframework/costume_mods/Luke"
        mods.mkdir(parents=True)
        shutil.copy2(source_zip, mods / source_zip.name)
        print(f"Running {label} native loader using read-only base PAK", flush=True)
        result = subprocess.run([str(exe.resolve()), str(game), "--base-pak", str(base)],
                                capture_output=True, text=True, errors="replace", timeout=180)
        (out / (label + "-stdout.txt")).write_text(result.stdout + result.stderr, encoding="utf-8")
        if result.returncode:
            raise RuntimeError(f"{label} loader exited {result.returncode}: {result.stderr}")
        outfits = json.loads((game / "reframework/data/SF6_Costumes_Data/outfits.json").read_text())
        pak = game / outfits["pak"]
        output_hashes, output_info = pak_index(pak)
        collisions = sorted(output_hashes & protected)
        warnings = [line for line in result.stdout.splitlines() if "WARN" in line or "left out" in line]
        checks = re.findall(r"(\d+) slots checked, (\d+) left out", result.stdout)
        observation = {"build": label, "loader": str(exe.resolve()),
                       "pak": str(pak), "pak_info": output_info,
                       "kept_outfits": outfits.get("outfits", []),
                       "omitted_outfits": outfits.get("left_out", []),
                       "slot_check": checks[-1] if checks else None,
                       "original_asset_collision_count": len(collisions),
                       "original_asset_collisions": [{"hash": f"{hashed:016X}",
                           "indexed_path": indexed_names.get(hashed)} for hashed in collisions],
                       "structural_paths_present": [path for path in STRUCTURAL.values()
                           if path_hash(path) in output_hashes], "warnings": warnings}
        observations.append(observation)
        print(f"{label}: {len(observation['kept_outfits'])} outfits kept, "
              f"{len(collisions)} original-asset collisions, {output_info['bytes']} PAK bytes", flush=True)
    _, base_after = pak_index(base)
    metadata_after = {str(path.relative_to(metadata)): digest(path)
                      for path in metadata.rglob("*") if path.is_file()}
    source_unchanged = {"base_pak_header_table_length_mtime": base_before == base_after,
                        "source_zip_sha256": digest(source_zip) == zip_before,
                        "loader_metadata_sha256": metadata_before == metadata_after}
    baseline, fixed = observations
    kept_identity = lambda entries: {(item["fighter"], item["costume_no"], item["label"])
                                    for item in entries}
    checks = {"baseline_reproduces_original_asset_collisions": baseline["original_asset_collision_count"] > 0,
              "fixed_output_has_no_original_asset_collisions": fixed["original_asset_collision_count"] == 0,
              "all_five_global_structural_paths_retained": len(fixed["structural_paths_present"]) == 5,
              "kept_outfit_identities_unchanged": kept_identity(baseline["kept_outfits"]) == kept_identity(fixed["kept_outfits"]),
              "real_sources_unchanged": all(source_unchanged.values())}
    report = {"scope": "native package generation on copied real input; no SF6 rendering/runtime test",
              "source_game": str(source_game), "source_zip": str(source_zip),
              "source_zip_sha256": zip_before,
              "dlc_paks_omitted": [{"name": path.name, "bytes": path.stat().st_size}
                                   for path in (source_game / "dlc").glob("*.pak")],
              "base_before": base_before, "source_preservation": source_unchanged,
              "observations": observations, "checks": checks}
    (out / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(checks, indent=2))
    print("Report:", out / "results.json")
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
