"""A mod's own colour list (CCVD) and colours in an extra slot (unreleased fix).

M. Bison C2 Cammy Officer v4 ships esf026_002_ccvd.user.2 and its CMD files. In its slots the
scene named the slot's copy of the list under the slot's file name (esf026_006_ccvd), which
still listed the original folder's colours: the slot showed Bison's colours on the mod's
materials. Since 1.10.3 the scene's third pass followed any reference to a file the slot has
under the renamed stem, the list too, past the copy that phase 2 points at the slot's colours.
Synthetic fixture game (fluffy_coexistence's ManualFixture plus the list and two colours);
actual loader.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import tempfile

import fluffy_coexistence as support

ROOT = Path(__file__).resolve().parents[1]
MOD = "reframework/costume_mods/Luke/ManualFixture"
COLOURS = {support.CMD_PRESENT: b"MOD_COLOUR_ONE_SENTINEL", support.CMD_MISSING: b"MOD_COLOUR_TWO_SENTINEL"}


def ref(path: str) -> str:
    """"natives/stm/product/model/.../esf002_003_cmd_001.user.2" as a list names it, in mixed case"""
    s = path.removeprefix("natives/stm/").rsplit(".", 1)[0]
    s = s.replace("product/model/", "Product/Model/").replace("_ccvd", "_CCVD").replace("_cmd_", "_CMD_")
    return s


def strings(data: bytes) -> list[str]:
    return re.findall(r"[\x20-\x7e]{6,}", data.decode("utf-16-le", "ignore"))


def make(game: Path) -> None:
    support.fixture(game)
    mod = game / MOD
    names = [ref(p) for p in COLOURS]
    (mod / support.CCVD).write_bytes(("\0" + "\0".join(names * 2) + "\0").encode("utf-16le"))
    for p, data in COLOURS.items():
        (mod / p).write_bytes(data)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    ap.add_argument("--old-loader", type=Path, help="previous build: the scene names a list of the original colours")
    args = ap.parse_args()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="colour-list-", dir=runs))
    checks = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(ok), "detail": detail})
        print(f"{'PASS' if ok else 'FAIL'} {label}" + (f": {detail}" if detail else ""))

    def case(tag: str, exe: Path):
        game = run / tag
        make(game)
        log = support.run_loader(exe.resolve(), game)
        (run / f"{tag}.log").write_text(log, encoding="utf-8")
        reg = json.loads((game / "reframework/data/SF6_Costumes_Data/registry.json").read_text())
        entries = support.read_pak(support.patch_file(game))
        slot = reg["slots"][0]
        scene = entries.get(support.path_hash(f"natives/stm/product/charparam/esf/esf002/{slot['scene_name']}.scn.20"), b"")
        lists = sorted({s.lstrip("@ ") for s in strings(scene) if "ccvd" in s.lower()})
        found = []   # (list named by the scene, colour it names, bytes the slot has there)
        for name in lists:
            data = entries.get(support.path_hash("natives/stm/" + name.lower() + ".2"), b"")
            for c in strings(data):
                if "cmd" in c.lower():
                    found.append((name, c, entries.get(support.path_hash("natives/stm/" + c.lower() + ".2"))))
        return log, slot["new_folder"], lists, found

    log, folder, lists, found = case("new", args.loader)
    check("the scene names the slot's colour list", bool(lists) and all(f"/esf002/{folder}/" in n.lower() for n in lists),
          ", ".join(lists))
    check("every colour that list names is in the slot's folder",
          bool(found) and all(f"/esf002/{folder}/" in c.lower() for _, c, _ in found),
          ", ".join(sorted({c for _, c, _ in found})))
    check("and holds the mod's colour", bool(found) and all(d in COLOURS.values() for _, _, d in found),
          ", ".join(sorted({repr(d)[:30] for _, _, d in found})))
    check("no colour of the mod left unused", "from the game, not the mod's version" not in log)

    if args.old_loader:
        log, folder, lists, found = case("old", args.old_loader)
        check("previous build: the list the scene names points at the original folder",
              any("/esf002/003/" in c.lower() for _, c, _ in found) and "from the game, not the mod's version" in log)

    (run / "results.json").write_text(json.dumps(checks, indent=2))
    failed = [c for c in checks if not c["passed"]]
    print(f"\n{len(checks) - len(failed)}/{len(checks)} passed ({run})")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
