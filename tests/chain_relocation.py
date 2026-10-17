"""Check chain reference relocation with the actual native costume loader.

All assets are synthetic workspace fixtures. This verifies SCN/USER resource
references and emitted PAK keys, not renderable assets or in-game physics.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import tempfile

import fluffy_coexistence as support


ROOT = Path(__file__).resolve().parents[1]
CHAIN = support.MODEL_ROOT + "esf002_003_01_chain.chain.52"
PART_CHAIN = support.MODEL + "esf002_003_01_sub_chain.chain.52"
USER = support.MODEL_ROOT + "esf002_003_01_chain.user.2"
SHAPE = support.MODEL_ROOT + "esf002_003_01_shape.user.2"
UNPROVIDED = support.MODEL_ROOT + "esf002_003_99_chain.chain.52"
HAVOK = support.MODEL_ROOT + "esf002_003_01.havokcloth.1.x64"
HAVOK_USER = support.MODEL_ROOT + "esf002_003_01_havok.user.2"
UNPROVIDED_HAVOK = support.MODEL_ROOT + "esf002_003_99.havokcloth.1.x64"
WEATHER = "natives/stm/product/weather/wind/model/002/esf002_003_01_chain_battlesetting.chain.52"
MODEL_PARTS = "natives/stm/product/charparam/esf/esf002/battle_ud/esf002v02_model_parts.user.2"
WRONG_VERSION = support.MODEL_ROOT + "esf002_003_98_chain.chain.53"
WRONG_ENGINE_REF = WRONG_VERSION.removesuffix(".53") + ".52"
FOLDER5 = "natives/stm/product/model/esf/esf002/005/01/original.tex.241101895"
PHYSICS = (CHAIN, PART_CHAIN, USER, SHAPE, UNPROVIDED, HAVOK, HAVOK_USER,
           UNPROVIDED_HAVOK, WEATHER, MODEL_PARTS, WRONG_VERSION, WRONG_ENGINE_REF)


def resource(path: str) -> str:
    if ".havokcloth." in path:
        return path.removeprefix("natives/stm/").split(".havokcloth.")[0] + ".havokcloth"
    return path.removeprefix("natives/stm/").rsplit(".", 1)[0]


def private(path: str, folder: str) -> str:
    if path == WEATHER:
        return path.replace("esf002_003_", f"esf002_{folder}_")
    if path == MODEL_PARTS:
        return path.replace("/battle_ud/", f"/battle{folder}/")
    # Chain and physics USER filenames keep their semantic source-costume stem.
    return path.replace("/esf002/003/", f"/esf002/{folder}/")


def variant(path: str) -> str:
    """One engine-equivalent spelling with a marker, capitals and separators."""
    return "@" + resource(path).upper().replace("/", "\\")


def utf16(refs: list[str]) -> bytes:
    return ("\0" + "\0".join(refs) + "\0").encode("utf-16le")


def resource_refs(data: bytes) -> list[str]:
    result = []
    current = ""
    for at in range(0, len(data) - 1, 2):
        char = data[at] | data[at + 1] << 8
        if 0x20 <= char < 0x7F:
            current += chr(char)
            continue
        normalized = current.lstrip("@ ").lower().replace("\\", "/")
        if normalized.startswith("product/"):
            extension = normalized.rsplit(".", 1)[-1]
            version = {"chain": "52", "user": "2", "havokcloth": "1.x64"}.get(extension)
            if version:
                result.append("natives/stm/" + normalized + "." + version)
        current = ""
    return result


def augment_pak(path: Path, additions: dict[str, bytes]) -> None:
    files = support.read_pak(path)
    files.update({support.path_hash(name): data for name, data in additions.items()})
    offset = 16 + len(files) * 48
    rows, blobs = [], []
    for hashed, data in files.items():
        rows.append(struct.pack("<IIqqqqq", hashed & 0xFFFFFFFF, hashed >> 32,
                                offset, len(data), len(data), 0, 0))
        blobs.append(data)
        offset += len(data)
    path.write_bytes(struct.pack("<IBBhII", 0x414B504B, 4, 0, 0, len(files), 0)
                     + b"".join(rows) + b"".join(blobs))


def make_fixture(game: Path, supplied_user: bool) -> dict[str, bytes]:
    supplied = support.fixture(game)
    base_pak = game / "re_chunk_000.pak"
    scene = support.read_pak(base_pak)[support.path_hash(support.SCENE)]
    scene += utf16([resource(USER), variant(USER), resource(SHAPE), variant(SHAPE),
                    resource(CHAIN), variant(CHAIN), resource(PART_CHAIN),
                    resource(UNPROVIDED), resource(HAVOK_USER), variant(HAVOK_USER),
                    resource(HAVOK), variant(HAVOK), resource(UNPROVIDED_HAVOK),
                    resource(WEATHER), variant(WEATHER),
                    resource(MODEL_PARTS), resource(MODEL_PARTS), resource(WRONG_ENGINE_REF)])
    refs = [resource(CHAIN), variant(CHAIN), resource(PART_CHAIN), resource(UNPROVIDED),
            resource(WEATHER), variant(WEATHER), resource(WRONG_ENGINE_REF)]
    havok_refs = [resource(HAVOK), variant(HAVOK), resource(UNPROVIDED_HAVOK),
                  resource(WEATHER), variant(WEATHER), resource(WRONG_ENGINE_REF)]
    relay_refs = [resource(CHAIN), variant(CHAIN), resource(UNPROVIDED),
                  resource(HAVOK), variant(HAVOK), resource(UNPROVIDED_HAVOK),
                  resource(WEATHER), variant(WEATHER), resource(WRONG_ENGINE_REF)]
    base = {
        support.SCENE: scene,
        CHAIN: b"VANILLA_ROOT_CHAIN_SENTINEL",
        PART_CHAIN: b"VANILLA_PART_CHAIN_SENTINEL",
        USER: utf16(refs),
        SHAPE: utf16(relay_refs),
        UNPROVIDED: b"UNPROVIDED_VANILLA_CHAIN_SENTINEL",
        HAVOK: b"VANILLA_HAVOK_ARBITRARY_VERSION_SENTINEL",
        HAVOK_USER: utf16(havok_refs),
        UNPROVIDED_HAVOK: b"UNPROVIDED_VANILLA_HAVOK_SENTINEL",
        WEATHER: b"VANILLA_EXTERNAL_WEATHER_CHAIN_SENTINEL",
        # No mesh references: cloning this relay requires its chain references
        # to change, independently of the existing mesh/material patch pass.
        MODEL_PARTS: utf16([resource(CHAIN), variant(CHAIN), resource(WEATHER)]),
        WRONG_ENGINE_REF: b"VANILLA_ENGINE_VERSION_52_SENTINEL",
        # Force the first free private folder to 006, as in the reported asset.
        FOLDER5: support.texture("VANILLA_FOLDER_005"),
    }
    augment_pak(base_pak, base)
    inventory = game / "reframework/data/SF6_Costumes_Data/loader/vanilla_costume_index.tsv"
    with inventory.open("a", encoding="utf-8") as handle:
        for name in base:
            if name == support.SCENE:
                continue
            handle.write(f"{support.path_hash(name):016X}\t2\t2\tfixture\t{name}\n")
    inventory_text = inventory.read_text(encoding="utf-8").replace(
        "#SUFFIXES\n", "#SUFFIXES\nhavokcloth\t1.x64\n", 1)
    inventory.write_text(inventory_text, encoding="utf-8")
    supplied.update({
        CHAIN: b"MANUAL_ROOT_CHAIN_SENTINEL",
        PART_CHAIN: b"MANUAL_PART_CHAIN_SENTINEL",
        SHAPE: utf16(relay_refs),
        HAVOK: b"MANUAL_HAVOK_ARBITRARY_VERSION_SENTINEL",
        WEATHER: b"MANUAL_EXTERNAL_WEATHER_CHAIN_SENTINEL",
        WRONG_VERSION: b"MANUAL_WRONG_CHAIN_VERSION_53_SENTINEL",
    })
    if supplied_user:
        supplied[USER] = utf16(refs)
        supplied[HAVOK_USER] = utf16(havok_refs)
    manual = game / "reframework/costume_mods/Luke/ManualFixture"
    for name, data in supplied.items():
        target = manual / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return supplied


def inspect(game: Path) -> tuple[dict[int, bytes], dict, list[str], list[str], list[str]]:
    entries = support.read_pak(support.patch_file(game))
    registry = json.loads((game / "reframework/data/SF6_Costumes_Data/registry.json").read_text())
    if len(registry["slots"]) != 1:
        raise AssertionError(f"expected one manual slot, got {registry['slots']}")
    slot = registry["slots"][0]
    scene = "natives/stm/product/charparam/esf/esf002/" + slot["scene_name"] + ".scn.20"
    folder = slot["new_folder"]
    scene_refs = [ref for ref in resource_refs(entries[support.path_hash(scene)])
                  if any(token in ref for token in
                         ("_chain.", "_shape.user.", ".havokcloth.", "_havok.user.",
                          "_chain_battlesetting.chain.", "_model_parts.user."))]
    return (entries, slot, scene_refs,
            resource_refs(entries.get(support.path_hash(private(USER, folder)), b"")),
            resource_refs(entries.get(support.path_hash(private(SHAPE, folder)), b"")))


def hashes(paths: dict[str, bytes]) -> dict[str, str]:
    return {name: hashlib.sha256(data).hexdigest() for name, data in paths.items()}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    parser.add_argument("--old-loader", type=Path,
                        help="optional baseline native loader; its wrong refs must reproduce first")
    parser.add_argument("--output", type=Path, help="new directory beneath tests/.runs")
    args = parser.parse_args()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    if args.output:
        run = args.output.resolve()
        if not run.is_relative_to(runs.resolve()) or run.exists():
            parser.error("--output must be a new directory beneath tests/.runs")
        run.mkdir(parents=True)
    else:
        run = Path(tempfile.mkdtemp(prefix="chain-", dir=runs))
    checks, cases = [], []

    def check(label: str, ok: bool, detail=None) -> None:
        checks.append({"name": label, "passed": bool(ok), "detail": detail})
        print(("PASS " if ok else "FAIL ") + label)

    for supplied_user in (True, False):
        case = "supplied-user" if supplied_user else "base-user"
        game = run / case / "game"
        supplied = make_fixture(game, supplied_user)
        original_sources = hashes(supplied)
        original_base = hashlib.sha256((game / "re_chunk_000.pak").read_bytes()).hexdigest()
        baseline = None
        if args.old_loader:
            old_log = support.run_loader(args.old_loader.resolve(), game)
            (run / case / "baseline-log.txt").write_text(old_log, encoding="utf-8")
            old_entries, old_slot, old_scene, old_user, old_shape = inspect(game)
            old_folder = old_slot["new_folder"]
            desired_root = private(CHAIN, old_folder)
            desired_part = private(PART_CHAIN, old_folder)
            baseline = {"scene_refs": old_scene, "user_refs": old_user, "shape_refs": old_shape,
                        "warnings": [line for line in old_log.splitlines() if "WARN:" in line]}
            check(case + ": baseline emits private chains but references the wrong paths",
                  support.path_hash(desired_root) in old_entries
                  and support.path_hash(desired_part) in old_entries
                  and (old_scene.count(desired_root) != 2 or old_user.count(desired_root) != 2
                       or old_shape.count(desired_root) != 2))
            if supplied_user:
                check(case + ": baseline reports unused mod chain references",
                      any("the slot loads" in line and "chain.chain" in line
                          for line in baseline["warnings"]))

        log = support.run_loader(args.loader.resolve(), game)
        (run / case / "current-log.txt").write_text(log, encoding="utf-8")
        entries, slot, scene_refs, user_refs, shape_refs = inspect(game)
        folder = slot["new_folder"]
        desired_root, desired_part = private(CHAIN, folder), private(PART_CHAIN, folder)
        desired_havok, desired_havok_user = private(HAVOK, folder), private(HAVOK_USER, folder)
        desired_weather, desired_parts = private(WEATHER, folder), private(MODEL_PARTS, folder)
        havok_refs = resource_refs(entries.get(support.path_hash(desired_havok_user), b""))
        parts_refs = resource_refs(entries.get(support.path_hash(desired_parts), b""))
        check(case + ": slot remains validated in private folder 006",
              folder == "006" and "1 slots checked, 0 left out" in log)
        check(case + ": mod chain payloads retain their source semantic filename stems",
              entries.get(support.path_hash(desired_root)) == supplied[CHAIN]
              and entries.get(support.path_hash(desired_part)) == supplied[PART_CHAIN])
        check(case + ": both scene USER occurrences point to emitted private USER",
              scene_refs.count(private(USER, folder)) == 2
              and support.path_hash(private(USER, folder)) in entries)
        check(case + ": both direct scene chain occurrences point to emitted private chain",
              scene_refs.count(desired_root) == 2 and scene_refs.count(desired_part) == 1)
        check(case + ": both USER chain occurrences and part chain follow emitted keys",
              user_refs.count(desired_root) == 2 and user_refs.count(desired_part) == 1)
        check(case + ": physics USER relay references follow the emitted chain",
              scene_refs.count(private(SHAPE, folder)) == 2
              and shape_refs.count(desired_root) == 2)
        check(case + ": unprovided vanilla chain references remain original",
              scene_refs.count(UNPROVIDED) == 1 and user_refs.count(UNPROVIDED) == 1
              and shape_refs.count(UNPROVIDED) == 1
              and support.path_hash(private(UNPROVIDED, folder)) not in entries)
        expected_physics = {desired_root, desired_part, private(USER, folder), private(SHAPE, folder),
                            UNPROVIDED, desired_havok, desired_havok_user, UNPROVIDED_HAVOK,
                            desired_weather, desired_parts, WRONG_ENGINE_REF}
        check(case + ": every scene and USER physics reference resolves to its expected key",
              all(ref in expected_physics for ref in scene_refs + user_refs + shape_refs + havok_refs + parts_refs)
              and all(support.path_hash(ref) in entries
                      for ref in expected_physics - {UNPROVIDED, UNPROVIDED_HAVOK, WRONG_ENGINE_REF}))
        check(case + ": no wrong renamed-filename chain keys or original collisions",
              all(support.path_hash(name) not in entries for name in PHYSICS)
              and all("esf002_006_" not in ref for ref in scene_refs + user_refs + shape_refs + havok_refs
                      if "/product/model/" in ref))
        check(case + ": arbitrary-version Havok payload and private USER are emitted",
              entries.get(support.path_hash(desired_havok)) == supplied[HAVOK]
              and support.path_hash(desired_havok_user) in entries)
        check(case + ": repeated case/slash-normalized Havok USER scene refs are private",
              scene_refs.count(desired_havok_user) == 2)
        check(case + ": supplied or fallback Havok USER points to both emitted cloth refs",
              havok_refs.count(desired_havok) == 2 and scene_refs.count(desired_havok) == 2)
        check(case + ": USER relay Havok references are patched after targets are emitted",
              shape_refs.count(desired_havok) == 2)
        check(case + ": unprovided vanilla Havok references remain byte-identical",
              scene_refs.count(UNPROVIDED_HAVOK) == 1 and havok_refs.count(UNPROVIDED_HAVOK) == 1
              and shape_refs.count(UNPROVIDED_HAVOK) == 1
              and (resource(UNPROVIDED_HAVOK) + "\0").encode("utf-16le")
                  in entries.get(support.path_hash(desired_havok_user), b""))
        check(case + ": external weather chain uses its exact renamed emitted stem",
              entries.get(support.path_hash(desired_weather)) == supplied[WEATHER]
              and scene_refs.count(desired_weather) == 2 and user_refs.count(desired_weather) == 2
              and havok_refs.count(desired_weather) == 2 and shape_refs.count(desired_weather) == 2)
        check(case + ": chain-only model_parts relay is cloned into its private namespace",
              support.path_hash(desired_parts) in entries and scene_refs.count(desired_parts) == 2
              and parts_refs.count(desired_root) == 2 and parts_refs.count(desired_weather) == 1)
        check(case + ": wrong-version chain cannot create a nonexisting private engine target",
              entries.get(support.path_hash(private(WRONG_VERSION, folder))) == supplied[WRONG_VERSION]
              and support.path_hash(private(WRONG_ENGINE_REF, folder)) not in entries
              and scene_refs.count(WRONG_ENGINE_REF) == 1 and user_refs.count(WRONG_ENGINE_REF) == 1
              and havok_refs.count(WRONG_ENGINE_REF) == 1 and shape_refs.count(WRONG_ENGINE_REF) == 1)
        check(case + ": chain references no longer produce outside-mod warnings",
              not any("the slot loads" in line and "chain.chain" in line
                      for line in log.splitlines()))
        manual = game / "reframework/costume_mods/Luke/ManualFixture"
        after_sources = hashes({name: (manual / name).read_bytes() for name in supplied})
        check(case + ": manual source payloads and base archive are unchanged",
              after_sources == original_sources
              and hashlib.sha256((game / "re_chunk_000.pak").read_bytes()).hexdigest() == original_base)
        if args.old_loader:
            check(case + ": current build regenerates the older cached references", "Up to date" not in log)
        before = support.patch_file(game).read_bytes()
        repeat_log = support.run_loader(args.loader.resolve(), game)
        check(case + ": repeat launch reuses the corrected PAK",
              "Up to date" in repeat_log and support.patch_file(game).read_bytes() == before)
        cases.append({"name": case, "game": str(game), "slot": slot,
                      "scene_refs": scene_refs, "user_refs": user_refs, "shape_refs": shape_refs,
                      "havok_refs": havok_refs, "model_parts_refs": parts_refs,
                      "baseline": baseline})

    result = {"loader": str(args.loader.resolve()), "old_loader": str(args.old_loader.resolve()) if args.old_loader else None,
              "scope": "native SCN/USER reference packaging; synthetic workspace data; no SF6 runtime/physics test",
              "checks": checks, "cases": cases,
              "passed": sum(check["passed"] for check in checks), "total": len(checks)}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
