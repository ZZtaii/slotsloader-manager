"""Exercise the actual Windows costume loader using tiny workspace-only fixtures.

The synthetic assets contain only the fields inspected by loader_core.cpp. They
exercise loader packaging and cache decisions, not rendering in Street Fighter 6.
No game installation, Fluffy library, or user outfit is read or modified.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
from costume_table_fixture import TABLE_PATH, costume_table


ROOT = Path(__file__).resolve().parents[1]
MODEL = "natives/stm/product/model/esf/esf002/003/01/"
MODEL_ROOT = "natives/stm/product/model/esf/esf002/003/"
MESH = MODEL + "esf002_003_01.mesh.230110883"
MDF = MODEL + "esf002_003_01_v00.mdf2.31"
TEX = MODEL + "esf002_003_01_body_albd.tex.241101895"
STREAM = TEX.replace("natives/stm/product/", "natives/stm/streaming/product/")
SCENE = "natives/stm/product/charparam/esf/esf002/esf002v02.scn.20"
SCENE1 = "natives/stm/product/charparam/esf/esf002/esf002v00.scn.20"
DEPENDENCY = "natives/stm/product/model/esf/esf002/001/01/manual_unique.tex.241101895"
INVALIDATED = "natives/stm/product/model/esf/esf002/001/01/indexed_missing.tex.241101895"
INVALIDATED_STREAM = INVALIDATED.replace("natives/stm/product/", "natives/stm/streaming/product/")
BASE_ONLY = "natives/stm/product/model/esf/esf002/001/01/base_only_original.tex.241101895"
DLC_ONLY = "natives/stm/product/model/esf/esf002/001/01/dlc_only_original.tex.241101895"
CCVD = MODEL_ROOT + "esf002_003_ccvd.user.2"
CMD_PRESENT = MODEL_ROOT + "esf002_003_cmd_001.user.2"
CMD_MISSING = MODEL_ROOT + "esf002_003_cmd_002.user.2"
# A loose colour in the game folder whose PAK entry is gone (Fluffy installed it). Since 1.9.8 a
# slot whose colour list names that colour takes it (upstream behaviour); 1.9.4-1.9.7 refused it.
LOOSE_COLOR = b"LOOSE_FLUFFY_COLOR_SENTINEL"
SAFE_COLOR = b"SAFE_OFFICIAL_COLOR_SENTINEL"
MARKER = "natives/stm/sf6_costume_slots.marker"
PROTECTED = (MESH, MDF, TEX, STREAM, INVALIDATED, INVALIDATED_STREAM,
             BASE_ONLY, DLC_ONLY, CCVD, CMD_PRESENT, CMD_MISSING)
STRUCTURAL = {
    "esf.scn.20": "natives/stm/product/charparam/esf/esf.scn.20",
    "fightercostumeuserdata.user.2": "natives/stm/product/cfncontents/onlineshop/fightercostumeuserdata.user.2",
    "fightercostumemessage.msg.21": "natives/stm/product/message/fgm/fighter/fightercostumemessage.msg.21",
    "ver_02_0300_esf.scn.20": "natives/stm/product/flow/version_update/ver_02_0300/esf.scn.20",
    "ver_02_0400_esf.scn.20": "natives/stm/product/flow/version_update/ver_02_0400/esf.scn.20",
    "ver_02_0500_esf.scn.20": "natives/stm/product/flow/version_update/ver_02_0500/esf.scn.20",
}


def rotl32(value: int, count: int) -> int:
    return ((value << count) | (value >> (32 - count))) & 0xFFFFFFFF


def murmur3(data: bytes) -> int:
    h = 0xFFFFFFFF
    for at in range(0, len(data) // 4 * 4, 4):
        k = struct.unpack_from("<I", data, at)[0]
        k = rotl32(k * 0xCC9E2D51 & 0xFFFFFFFF, 15)
        h ^= k * 0x1B873593 & 0xFFFFFFFF
        h = (rotl32(h, 13) * 5 + 0xE6546B64) & 0xFFFFFFFF
    tail = data[len(data) // 4 * 4 :]
    k = sum(value << (8 * i) for i, value in enumerate(tail))
    if tail:
        k = rotl32(k * 0xCC9E2D51 & 0xFFFFFFFF, 15)
        h ^= k * 0x1B873593 & 0xFFFFFFFF
    h ^= len(data)
    h ^= h >> 16
    h = h * 0x85EBCA6B & 0xFFFFFFFF
    h ^= h >> 13
    h = h * 0xC2B2AE35 & 0xFFFFFFFF
    h ^= h >> 16
    return h


def path_hash(path: str) -> int:
    path = path.strip().replace("\\", "/")
    while "//" in path:
        path = path.replace("//", "/")
    return (murmur3(path.upper().encode("utf-16le")) << 32) | murmur3(
        path.lower().encode("utf-16le")
    )


def write_pak(path: Path, files: dict[str, bytes]) -> None:
    offset = 16 + 48 * len(files)
    rows, payload = [], []
    for name, data in files.items():
        hashed = path_hash(name)
        rows.append(struct.pack("<IIqqqqq", hashed & 0xFFFFFFFF, hashed >> 32,
                                offset, len(data), len(data), 0, 0))
        payload.append(data)
        offset += len(data)
    path.write_bytes(struct.pack("<IBBhII", 0x414B504B, 4, 0, 0, len(files), 0)
                     + b"".join(rows) + b"".join(payload))


def read_pak(path: Path) -> dict[int, bytes]:
    data = path.read_bytes()
    magic, major, _, _, count, _ = struct.unpack_from("<IBBhII", data)
    if magic != 0x414B504B or major != 4:
        raise AssertionError("generated file is not a v4 PAK")
    result = {}
    for i in range(count):
        low, high, offset, cs, ds, attributes, _ = struct.unpack_from(
            "<IIqqqqq", data, 16 + 48 * i)
        if attributes & 0xFF or cs != ds:
            raise AssertionError("synthetic fixture unexpectedly contains compressed entries")
        result[(high << 32) | low] = data[offset:offset + cs]
    return result


def mesh(tag: str) -> bytes:
    data = bytearray(224)
    data[:4] = b"MESH"
    struct.pack_into("<h", data, 20, 1)
    struct.pack_into("<q", data, 32, 152)
    struct.pack_into("<q", data, 112, 160)
    struct.pack_into("<q", data, 144, 168)
    data[153] = 1
    struct.pack_into("<h", data, 160, 0)
    struct.pack_into("<q", data, 168, 176)
    data[176:188] = b"fixture_mat\0"
    marker = tag.encode("ascii")
    data[192:192 + len(marker)] = marker
    return bytes(data)


def mdf(tag: str) -> bytes:
    data = bytearray(160)
    data[:4] = b"MDF\0"
    struct.pack_into("<h", data, 6, 1)
    struct.pack_into("<q", data, 16, 116)
    name = "fixture_mat\0".encode("utf-16le")
    data[116:116 + len(name)] = name
    data.extend(("\0" + tag + "\0").encode("utf-16le"))
    return bytes(data)


def texture(tag: str, bad_pitch: bool = False) -> bytes:
    # A single 4x4 BC1 mip: 8 bytes, with intentionally repairable row pitch.
    data = bytearray(64)
    data[:4] = b"TEX\0"
    struct.pack_into("<IHHHBBI", data, 4, 241101895, 4, 4, 1, 1, 16, 71)
    struct.pack_into("<Qii", data, 40, 56, 16 if bad_pitch else 8, 8)
    data[56:64] = hashlib.sha256(tag.encode()).digest()[:8]
    return bytes(data)


def fixture(game: Path) -> dict[str, bytes]:
    game.mkdir(parents=True)
    loader = game / "reframework/data/SF6_Costumes_Data/loader"
    static = loader / "static"
    static.mkdir(parents=True)
    scene_refs = [MESH.removeprefix("natives/stm/").rsplit(".", 1)[0],
                  MDF.removeprefix("natives/stm/").rsplit(".", 1)[0],
                  CCVD.removeprefix("natives/stm/").rsplit(".", 1)[0]]
    scene = ("\0" + "\0".join(scene_refs * 2) + "\0").encode("utf-16le")
    base = {MESH: mesh("VANILLA"), MDF: mdf("VANILLA"), TEX: texture("VANILLA"),
            STREAM: texture("VANILLA_STREAM"), SCENE: scene,
            SCENE1: "\0Fixture Outfit 1\0".encode("utf-16le"),
            CCVD: ("\0" + "\0".join(name.removeprefix("natives/stm/").rsplit(".", 1)[0]
                   for name in (CMD_PRESENT, CMD_MISSING)) + "\0").encode("utf-16le"),
            CMD_PRESENT: SAFE_COLOR, BASE_ONLY: texture("BASE_ONLY_ORIGINAL")}
    for local, path in STRUCTURAL.items():
        base[path] = ("fixture global static: " + local).encode("ascii")
    base[TABLE_PATH] = costume_table()
    write_pak(game / "re_chunk_000.pak", base)
    (game / "dlc").mkdir()
    write_pak(game / "dlc/fixture_dlc.pak", {DLC_ONLY: texture("DLC_ONLY_ORIGINAL")})
    # INVALIDATED is known by the index but absent from the current base PAK,
    # reproducing the distinction between vanilla paths and available entries.
    # BASE_ONLY and DLC_ONLY deliberately never appear in the inventory, and
    # INVALIDATED_STREAM is inferred from its indexed low-resolution twin.
    inventory = [name for name in base if name != BASE_ONLY] + [INVALIDATED, CMD_MISSING]
    lines = ["#SUFFIXES", "mesh\t230110883", "mdf2\t31", "tex\t241101895",
             "scn\t20", "user\t2", "chain\t52", "#COSTUMES",
             f"2\tesf002\t0\tproduct/model/esf/esf002/001\t{SCENE1}",
             f"2\tesf002\t2\tproduct/model/esf/esf002/003\t{SCENE}", "#FILES"]
    for name in inventory:
        lines.append(f"{path_hash(name):016X}\t2\t2\tfixture\t{name}")
    (loader / "vanilla_costume_index.tsv").write_text("\n".join(lines) + "\n")
    metadata = {"slot_range": [5, 6], "files": [{"local": local, "pak_path": path}
                 for local, path in STRUCTURAL.items()], "records": [
                 {"fighter": 2, "costume_no": 5, "record_id": 500, "manage_id": 500}]}
    (static / "static_meta.json").write_text(json.dumps(metadata))
    for local, path in STRUCTURAL.items():
        (static / local).write_bytes(base[path])
    (static / "fightercostumeuserdata.user.2").write_bytes(
        costume_table(generated=((500, 2, 5),)))
    manual = game / "reframework/costume_mods/Luke/ManualFixture"
    manual.mkdir(parents=True)
    (manual / "modinfo.ini").write_text("name=Manual Fixture\nauthor=Regression test\n")
    supplied = {MESH: mesh("MANUAL"), MDF: mdf("MANUAL"),
                TEX: texture("MANUAL", bad_pitch=True), STREAM: texture("MANUAL_STREAM"),
                DEPENDENCY: texture("MANUAL_ONLY_DEPENDENCY"),
                INVALIDATED: texture("INDEXED_ORIGINAL_DEPENDENCY"),
                INVALIDATED_STREAM: texture("UNINDEXED_MISSING_ORIGINAL_STREAM"),
                BASE_ONLY: texture("SUPPLIED_BASE_ONLY_ORIGINAL"),
                DLC_ONLY: texture("SUPPLIED_DLC_ONLY_ORIGINAL")}
    for name, data in supplied.items():
        destination = manual / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    loose_color = game / CMD_MISSING
    loose_color.parent.mkdir(parents=True, exist_ok=True)
    loose_color.write_bytes(LOOSE_COLOR)
    return supplied


def run_loader(exe: Path, game: Path) -> str:
    env = dict(os.environ)
    env.pop("SF6_COSTUME_LOADER_BASE_PAK", None)
    env.pop("SF6_COSTUME_SWATCH_TEST", None)
    result = subprocess.run([str(exe), str(game)], capture_output=True, text=True,
                            errors="replace", timeout=60, env=env)
    if result.returncode:
        raise AssertionError(f"loader exited {result.returncode}:\n{result.stdout}\n{result.stderr}")
    return result.stdout + result.stderr


def patch_file(game: Path) -> Path:
    candidates = []
    for path in game.glob("re_chunk_000.pak.patch_*.pak"):
        if path_hash(MARKER) in read_pak(path):
            candidates.append(path)
    if len(candidates) != 1:
        raise AssertionError(f"expected one generated costume PAK, found {len(candidates)}")
    return candidates[0]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--old-loader", type=Path,
                        help="optional prior build to seed its cached PAK before running current build")
    parser.add_argument("--output", type=Path, help="new fixture directory, restricted to tests/.runs")
    args = parser.parse_args()
    args.loader = args.loader.resolve()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    if args.output:
        run = args.output.resolve()
        if not run.is_relative_to(runs.resolve()) or run.exists():
            parser.error("--output must be a new directory beneath this worktree's tests/.runs")
        run.mkdir(parents=True)
    else:
        run = Path(tempfile.mkdtemp(prefix="fluffy-", dir=runs))
    game = run / "game"
    supplied = fixture(game)
    initial_manual_hashes = {name: hashlib.sha256(data).hexdigest() for name, data in supplied.items()}
    checks, logs = [], []

    def check(label: str, condition: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(condition), "detail": detail})
        print(f"{'PASS' if condition else 'FAIL'} {label}" + (f": {detail}" if detail else ""))

    if args.old_loader:
        logs.append(("old-loader", run_loader(args.old_loader.resolve(), game)))
        old_pak = patch_file(game)
        old_data = old_pak.read_bytes()
        check("prior build reproduces original-path masking",
              path_hash(MESH) in read_pak(old_pak))
        old_time = old_pak.stat().st_mtime_ns
    else:
        old_data, old_time = None, None

    logs.append(("initial", run_loader(args.loader, game)))
    output = patch_file(game)
    entries = read_pak(output)
    registry_path = game / "reframework/data/SF6_Costumes_Data/registry.json"
    registry = json.loads(registry_path.read_text())
    slot = registry["slots"][0] if registry["slots"] else None
    check("manual outfit remains assigned", slot is not None and len(registry["slots"]) == 1)
    check("manual outfit survives loader validation", "1 slots checked, 0 left out" in logs[-1][1])
    check("generated PAK does not mask original vanilla paths",
          all(path_hash(name) not in entries for name in PROTECTED),
          ", ".join(name for name in PROTECTED if path_hash(name) in entries))
    check("unindexed missing streaming twin of indexed original is protected",
          path_hash(INVALIDATED_STREAM) not in entries)
    check("base-only original hash absent from inventory is protected", path_hash(BASE_ONLY) not in entries)
    check("DLC-only original hash absent from inventory is protected", path_hash(DLC_ONLY) not in entries)
    check("all six exact global structural replacements are retained",
          all(entries.get(path_hash(path)) == (costume_table(generated=((500, 2, 5),))
              if path == TABLE_PATH else ("fixture global static: " + local).encode("ascii"))
              for local, path in STRUCTURAL.items()))
    check("mod-only dependency remains available", entries.get(path_hash(DEPENDENCY)) == supplied[DEPENDENCY])
    # 1.9.8: flipped from "manual output does not import unrelated loose Fluffy colors"
    check("loose colour imported only into the private slot, never under its original path",
          path_hash(CMD_MISSING) not in entries
          and sum(LOOSE_COLOR in data for data in entries.values()) == 1)
    check("colour taken from the game folder is logged with its file",
          any("taken from the game folder" in line and str(game / CMD_MISSING).replace("/", "\\") in line
              for line in logs[-1][1].splitlines()))
    if slot:
        folder = slot["new_folder"]
        relocated_mesh = MESH.replace("/003/", f"/{folder}/").replace("esf002_003_", f"esf002_{folder}_")
        relocated_texture = TEX.replace("/003/", f"/{folder}/").replace("esf002_003_", f"esf002_{folder}_")
        relocated_stream = STREAM.replace("/003/", f"/{folder}/").replace("esf002_003_", f"esf002_{folder}_")
        check("manual mesh stored in its private slot", entries.get(path_hash(relocated_mesh)) == supplied[MESH])
        repaired_tex = entries.get(path_hash(relocated_texture), b"")
        check("repairable texture row pitch repaired in private slot",
              len(repaired_tex) >= 56 and struct.unpack_from("<i", repaired_tex, 48)[0] == 8)
        check("manual streaming twin stored in private slot",
              entries.get(path_hash(relocated_stream)) == supplied[STREAM])
        relocated_cmd = CMD_MISSING.replace("/003/", f"/{folder}/")
        # 1.9.8: flipped from "missing official color uses packaged color fallback" (SAFE_COLOR)
        check("missing official color uses the loose game-folder copy",
              entries.get(path_hash(relocated_cmd)) == LOOSE_COLOR)
    if old_data is not None:
        check("current build invalidates prior build's cached PAK",
              "Up to date" not in logs[-1][1] and output.read_bytes() != old_data
              and output.stat().st_mtime_ns != old_time)

    saved_bytes, saved_time = output.read_bytes(), output.stat().st_mtime_ns
    base_path = game / "re_chunk_000.pak"
    original_base = base_path.read_bytes()
    for state, payload in (("Fluffy install", mesh("FLUFFY_A")),
                           ("Fluffy replacement", mesh("FLUFFY_B")),
                           ("Fluffy uninstall", None)):
        loose = game / MESH
        if payload is None:
            loose.unlink()
        else:
            loose.parent.mkdir(parents=True, exist_ok=True)
            loose.write_bytes(payload)
        # Fluffy invalidates replaced PAK entries by zeroing both hash fields.
        # Restore those fields on uninstall, without changing payload bytes.
        base_data = bytearray(original_base)
        if payload is not None:
            count = struct.unpack_from("<I", base_data, 8)[0]
            for i in range(count):
                at = 16 + 48 * i
                hashed = struct.unpack_from("<Q", base_data, at)[0]
                if hashed in (path_hash(MESH), path_hash(MDF)):
                    struct.pack_into("<Q", base_data, at, 0)
        base_path.write_bytes(base_data)
        base_entries = read_pak(base_path)
        check(f"{state} models Fluffy PAK hash invalidation",
              (path_hash(MESH) in base_entries) == (payload is None)
              and (path_hash(MDF) in base_entries) == (payload is None))
        logs.append((state, run_loader(args.loader, game)))
        current = read_pak(output)
        check(f"{state} uses cached manual output safely", "Up to date" in logs[-1][1]
              and output.read_bytes() == saved_bytes and output.stat().st_mtime_ns == saved_time)
        # With Loose File Loader off: patch PAK, then base PAK, then natives.
        # Fluffy invalidation is what makes the native replacement reachable.
        hashed_mesh = path_hash(MESH)
        resolved = current.get(hashed_mesh, base_entries.get(hashed_mesh,
                               loose.read_bytes() if loose.exists() else b""))
        expected = payload if payload is not None else mesh("VANILLA")
        check(f"{state} leaves original costume resolution independent", resolved == expected)

    manual = game / "reframework/costume_mods/Luke/ManualFixture"
    after_manual_hashes = {name: hashlib.sha256((manual / name).read_bytes()).hexdigest()
                           for name in supplied}
    check("supplied manual source assets are unchanged", after_manual_hashes == initial_manual_hashes)
    check("loose Fluffy color source is unchanged", (game / CMD_MISSING).read_bytes() == LOOSE_COLOR)
    # Rename outside the scan tree; never delete the fixture source outfit.
    manual.rename(game / "retained-manual-source")
    logs.append(("manual uninstall", run_loader(args.loader, game)))
    check("removing manual outfit removes its generated PAK", not output.exists())
    check("manual uninstall clears slot registry", not json.loads(registry_path.read_text())["slots"])

    # Trigger the material validator's missing-texture repair branch on an
    # indexed original path absent from the base PAK. A safety guard must not
    # reintroduce that path even if the supplied mod has replacement bytes.
    repair_game = run / "repair-game"
    fixture(repair_game)
    repair_manual = repair_game / "reframework/costume_mods/Luke/ManualFixture"
    original_ref = INVALIDATED.removeprefix("natives/stm/").rsplit(".", 1)[0]
    repair_mdf = mdf("MANUAL") + ("\0" + original_ref + "\0").encode("utf-16le")
    (repair_manual / MDF).write_bytes(repair_mdf)
    logs.append(("original texture repair", run_loader(args.loader, repair_game)))
    repaired_entries = read_pak(patch_file(repair_game))
    check("missing-texture repair cannot restore protected original path",
          path_hash(INVALIDATED) not in repaired_entries)
    check("missing-texture repair cannot restore protected inferred stream twin",
          path_hash(INVALIDATED_STREAM) not in repaired_entries)
    for label, content in logs:
        (run / (label.lower().replace(" ", "-") + ".txt")).write_text(content, encoding="utf-8")
    result = {"loader": str(args.loader), "fixture": str(game),
              "scope": "native loader package/cache logic; no game runtime validation",
              "checks": checks, "passed": sum(item["passed"] for item in checks),
              "total": len(checks)}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    sys.exit(main())
