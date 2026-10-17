"""Actual native loader routing/cache checks, using isolated workspace fixture games.

Scenes/models are generated. Optional USER bytes are read only from the retained
audit evidence copies (never from a live library/game). No rendering is claimed.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import tempfile
import zipfile

import nested_modular as nm
import fluffy_coexistence as support
from chain_relocation import augment_pak

ROOT = Path(__file__).resolve().parents[1]
SCENE = f"natives/stm/product/charparam/esf/{nm.FD}/{nm.FD}v02.scn.20"
OLD = 0xADA9B6AE
NEW = 0xCD9A2E7B


def scene(refs: list[str], crc: int, body: str) -> bytes:
    # Real files need not align the RSZ header itself to 16; instance data does.
    r = 70
    data = ((r + 80 + 15) & ~15) - r
    b = bytearray(r + data + 32)
    b[:4] = b"SCN\0"
    struct.pack_into("<Q", b, 56, r)
    struct.pack_into("<4sIIIIIQQQ", b, r, b"RSZ\0", 16, 1, 3, 0, 0, 52, data, data)
    struct.pack_into("<I", b, r + 48, 1)
    struct.pack_into("<IIII", b, r + 60, 0x63515A59, crc, 0x11223344, OLD)
    struct.pack_into("<I", b, r + data + 8, OLD)
    for ref in refs + [f"Product/CharParam/esf/{body}/prefab/{body}v00.pfb"]:
        b += (ref + "\0").encode("utf-16le")
    return bytes(b)


def rewrite_prefix(data: bytes, old: str, new: str) -> bytes:
    # Independent expected-output preparation for the documented copied USER.
    assert len(old) == len(new)
    pattern = re.compile(re.escape((old + "/").encode("utf-16le")), re.I)
    result, n = pattern.subn((new + "/").encode("utf-16le"), data)
    assert n
    return result


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    ap.add_argument("--evidence", type=Path)
    args = ap.parse_args()
    loader = args.loader.resolve()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="swap-routing-", dir=runs))
    results = []
    donor_path = "natives/stm/product/model/esf/esf028/001/esf028_001_01_chain.user.2"
    user_path = f"natives/stm/product/model/esf/{nm.FD}/{nm.FOLDER}/esf028_001_01_chain.user.2"
    user_old = user_current = None
    if args.evidence:
        profiles = json.loads((args.evidence / "candidate-user-profiles.json").read_text(encoding="utf-8-sig"))
        p = next(p for p in profiles if p["Basename"] == "esf028_001_01_chain.user.2")
        e = p["Evidence"][0]
        user_old = rewrite_prefix(Path(e["Sample"]).read_bytes(), f"Product/Model/esf/{e['TargetEsf']}/{e['TargetSlot']}", f"Product/Model/esf/{nm.FD}/{nm.FOLDER}")
        user_current = (args.evidence / "current-official" / donor_path.replace("/", "__")).read_bytes()
        assert hashlib.sha256(user_current).hexdigest() == p.get("CurrentRawSHA256", None) or hashlib.sha256(user_current).hexdigest() == "1447e93a3804f3d679830b5ab1ff9d961d96d8ab0f14072d11844d98cbbaff01"
    for route in ("folder", "zip", "pak"):
        for scenario in ("follow_current", "own_body", "own_parts"):
            game = run / f"{route}-{scenario}"
            nm.fixture(game)
            marker = game / nm.DATA / "swap_repairs.txt"   # the Manager's setting (off by default)
            marker.write_bytes(b"")
            refs = [p.removeprefix("natives/stm/").rsplit(".", 1)[0]
                    for part in ("00", "01", "02") for p in (nm.part_mesh(part), nm.part_mdf(part))]
            vanilla = scene(refs, NEW, nm.FD)
            additions = {SCENE: vanilla}
            files = nm.files_of("01", "SWAP_BODY", tex=True)
            modrefs = refs[2:] if scenario == "own_parts" else list(refs)
            if user_old and scenario == "own_body":
                additions[donor_path] = user_current
                files[user_path] = user_old
                chain_path = user_path.replace("_chain.user.2", "_chain.chain.52")
                files[chain_path] = b"SUPPLIED_PRIVATE_CHAIN"
                additions[chain_path] = b"VANILLA_CHAIN_PLACEHOLDER"
                inv = game / nm.DATA / "loader/vanilla_costume_index.tsv"
                with inv.open("a", encoding="utf-8") as f:
                    f.write(f"{support.path_hash(chain_path):016X}\t2\t2\tchain\t{chain_path}\n")
                modrefs.append(user_path.removeprefix("natives/stm/").removesuffix(".2"))
            files[SCENE] = scene(modrefs, OLD, "esf016" if scenario == "own_body" else nm.FD)
            augment_pak(game / "re_chunk_000.pak", additions)
            before_base = hashlib.sha256((game / "re_chunk_000.pak").read_bytes()).hexdigest()
            src = game / "reframework/costume_mods/Luke"
            if route == "folder":
                src /= "Swap"
                for path, b in files.items():
                    dest = src / path
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(b)
            elif route == "zip":
                src /= "Swap.zip"
                with zipfile.ZipFile(src, "w", zipfile.ZIP_DEFLATED) as z:
                    for path, b in files.items(): z.writestr("Swap/" + path, b)
            else:
                src /= "Swap.pak"
                support.write_pak(src, files)
            source_hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (src.rglob("*") if src.is_dir() else [src]) if p.is_file()}
            log = support.run_loader(loader, game)
            (game / "first.log").write_text(log, encoding="utf-8")
            regpath = game / nm.DATA / "registry.json"
            registry = json.loads(regpath.read_text(encoding="utf-8"))
            assert len(registry["slots"]) == 1, log
            slot = registry["slots"][0]
            output = support.read_pak(support.patch_file(game))
            scnpath = f"natives/stm/product/charparam/esf/{nm.FD}/{slot['scene_name']}.scn.20"
            final_scene = output[support.path_hash(scnpath)]
            r = struct.unpack_from("<Q", final_scene, 56)[0]
            assert struct.unpack_from("<I", final_scene, r + 64)[0] == NEW, log
            assert struct.unpack_from("<I", final_scene, r + 72)[0] == OLD
            assert "Alex scene type signature updated" in log
            if scenario == "own_body":
                assert "another body" in log and "esf016v00.pfb".encode("utf-16le") in final_scene, log
                if user_old:
                    final_user = user_path.replace(f"/{nm.FOLDER}/", f"/{slot['new_folder']}/")
                    expected = rewrite_prefix(user_current, "Product/Model/esf/esf028/001", f"Product/Model/esf/{nm.FD}/{slot['new_folder']}")
                    assert output[support.path_hash(final_user)] == expected, log
                    assert "documented swap USER rebuilt" in log
            if scenario == "own_parts": assert "4 model parts against 6" in log, log
            again = support.run_loader(loader, game)
            assert "Up to date (fingerprint match" in again, again
            # A pre-repair-build cache identity must force regeneration even with identical inputs.
            current_id = re.search(r"loader=([^;|\"]+)", registry["fingerprint"]).group(1)
            assert current_id != "2026-10-17-1.10.3-slotsloader-fork"
            registry["fingerprint"] = registry["fingerprint"].replace(current_id, "2026-10-17-1.10.3-slotsloader-fork")
            regpath.write_text(json.dumps(registry), encoding="utf-8")
            refreshed = support.run_loader(loader, game)
            assert "Up to date (fingerprint match" not in refreshed and "Alex scene type signature updated" in refreshed, refreshed
            assert "Up to date (fingerprint match" in support.run_loader(loader, game)
            # The setting turned off: a rebuild with no swap repair (the mod's own scene keeps its old signature).
            marker.unlink()
            off = support.run_loader(loader, game)
            assert "Up to date (fingerprint match" not in off and "swap repair" not in off and "swap repairs: on" not in off, off
            if scenario != "follow_current":
                off_output = support.read_pak(support.patch_file(game))
                off_slot = json.loads(regpath.read_text(encoding="utf-8"))["slots"][0]
                off_scene = off_output[support.path_hash(f"natives/stm/product/charparam/esf/{nm.FD}/{off_slot['scene_name']}.scn.20")]
                assert struct.unpack_from("<I", off_scene, struct.unpack_from("<Q", off_scene, 56)[0] + 64)[0] == OLD, off
            assert "Up to date (fingerprint match" in support.run_loader(loader, game)
            assert hashlib.sha256((game / "re_chunk_000.pak").read_bytes()).hexdigest() == before_base
            assert all(hashlib.sha256(Path(p).read_bytes()).hexdigest() == h for p, h in source_hashes.items())
            results.append({"route": route, "scene": scenario, "user_repair": bool(user_old and scenario == "own_body"), "cache_rebuild_then_reuse": True, "off_rebuilds_unrepaired": True, "sources_unchanged": True})
    (run / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(f"PASS {len(results)} native folder/ZIP/PAK routing and cache cases: {run}")


if __name__ == "__main__": main()
