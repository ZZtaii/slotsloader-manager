"""Joint constraints (.jcns) a mod ships, with the actual native costume loader (1.9.8).

The game's scene does not name .jcns files: <fd>_<folder>_<part>_jcs.user does. Fluffy replaces the
game's .jcns in place, so the outfit uses the mod's. A slot must do the same through its own copy of
the jcs.user. Synthetic workspace fixtures (fluffy_coexistence's game); no SF6 runtime test.

Cases:
  jcns-only     the mod ships the .jcns at the game's path: the slot gets private copies of both
                jcs.user files naming it, pointing at the slot's .jcns; the scene points at them;
                a .jcns the mod does not ship stays the game's
  own-jcs-user  the mod also ships its jcs.user: that file, repointed, is the slot's
  unreferenced  a .jcns nothing names: WARN naming it, the slot is kept
  --old-loader  a mod without .jcns gives a PAK byte-identical to the previous build's
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import tempfile

import fluffy_coexistence as support
from chain_relocation import augment_pak, utf16

ROOT = Path(__file__).resolve().parents[1]
MR = support.MODEL_ROOT                                  # .../esf002/003/
JCNS = MR + "esf002_003_01_drv_rpy.jcns.22"
JCNS_OTHER = MR + "esf002_003_01_drv_bs1.jcns.22"         # the mod does not ship it
JCS = MR + "esf002_003_01_jcs.user.2"
JCS0 = MR + "esf002_003_00_jcs.user.2"
EXTRA = MR + "esf002_003_07_unused_rpy.jcns.22"           # named by nothing


def res(path: str, upper_tail: bool = False) -> str:
    r = path.removeprefix("natives/stm/").rsplit(".", 1)[0]
    if upper_tail:   # the game's own spelling: Product/... and drv_RPY
        r = "Product" + r.removeprefix("product").replace("drv_rpy", "drv_RPY")
    return r


def refs(data: bytes) -> list[str]:
    out, cur = [], ""
    for at in range(0, len(data) - 1, 2):
        ch = data[at] | data[at + 1] << 8
        if 0x20 <= ch < 0x7F:
            cur += chr(ch)
            continue
        n = cur.lstrip("@ ").lower()
        if n.startswith("product/"):
            out.append(n)
        cur = ""
    return out


def make(game: Path, supplied_extra: dict[str, bytes]) -> dict[str, bytes]:
    supplied = support.fixture(game)
    base_pak = game / "re_chunk_000.pak"
    scene = support.read_pak(base_pak)[support.path_hash(support.SCENE)]
    scene += utf16(["@" + res(JCS, True), res(JCS0, True)])
    jcs_refs = [res(JCNS, True), res(JCNS_OTHER, True)]
    base = {support.SCENE: scene, JCNS: b"VANILLA_DRV_RPY_JCNS", JCNS_OTHER: b"VANILLA_DRV_BS1_JCNS",
            JCS: utf16(jcs_refs), JCS0: utf16(jcs_refs)}
    augment_pak(base_pak, base)
    inventory = game / "reframework/data/SF6_Costumes_Data/loader/vanilla_costume_index.tsv"
    text = inventory.read_text(encoding="utf-8").replace("#SUFFIXES\n", "#SUFFIXES\njcns\t22\n", 1)
    for name in base:
        if name != support.SCENE:
            text += f"{support.path_hash(name):016X}\t2\t2\tfixture\t{name}\n"
    inventory.write_text(text, encoding="utf-8")
    manual = game / "reframework/costume_mods/Luke/ManualFixture"
    for name, data in supplied_extra.items():
        target = manual / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    supplied.update(supplied_extra)
    return supplied


def inspect(game: Path):
    entries = support.read_pak(support.patch_file(game))
    reg = json.loads((game / "reframework/data/SF6_Costumes_Data/registry.json").read_text())
    slot = reg["slots"][0]
    f = slot["new_folder"]
    scene = entries.get(support.path_hash(
        "natives/stm/product/charparam/esf/esf002/" + slot["scene_name"] + ".scn.20"), b"")
    return entries, f, scene


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--loader", type=Path, default=ROOT / "build/costume_loader.exe")
    ap.add_argument("--old-loader", type=Path, help="previous build: a mod without .jcns must give the same PAK")
    args = ap.parse_args()
    exe = args.loader.resolve()
    runs = ROOT / "tests/.runs"
    runs.mkdir(exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="jcns-", dir=runs))
    checks = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        checks.append({"name": label, "passed": bool(ok), "detail": detail})
        print(("PASS " if ok else "FAIL ") + label + (": " + detail if detail and not ok else ""))

    def priv(path: str, f: str, rename: bool) -> str:
        p = path.replace("/esf002/003/", f"/esf002/{f}/")
        return p.replace("esf002_003_", f"esf002_{f}_") if rename else p

    # ---- jcns-only ----
    game = run / "jcns-only" / "game"
    make(game, {JCNS: b"MOD_DRV_RPY_JCNS"})
    log = support.run_loader(exe, game)
    (run / "jcns-only-log.txt").write_text(log, encoding="utf-8")
    entries, f, scene = inspect(game)
    sj, pj, pj0 = priv(JCNS, f, True), priv(JCS, f, False), priv(JCS0, f, False)
    check("jcns-only: slot validated", "1 slots checked, 0 left out" in log)
    check("jcns-only: the mod's .jcns is the slot's", entries.get(support.path_hash(sj)) == b"MOD_DRV_RPY_JCNS")
    for label, p in (("01", pj), ("00", pj0)):
        r = refs(entries.get(support.path_hash(p), b""))
        check(f"jcns-only: private {label}_jcs.user names the slot's .jcns",
              res(sj) in r and res(JCNS) not in r, repr(r))
        check(f"jcns-only: private {label}_jcs.user keeps the game's .jcns the mod does not ship",
              res(JCNS_OTHER) in r, repr(r))
    sr = refs(scene)
    check("jcns-only: the scene points at both private jcs.user files",
          res(pj) in sr and res(pj0) in sr and res(JCS) not in sr and res(JCS0) not in sr, repr([x for x in sr if "jcs" in x]))
    check("jcns-only: nothing written at the original paths",
          all(support.path_hash(p) not in entries for p in (JCNS, JCNS_OTHER, JCS, JCS0)))
    check("jcns-only: logged", "joint constraints: the mod's .jcns, through the slot's own copy of esf002_003_01_jcs.user" in log)
    check("jcns-only: no unreferenced-file WARN", "referenced by none of the slot's" not in log)
    before = support.patch_file(game).read_bytes()
    log2 = support.run_loader(exe, game)
    check("jcns-only: repeat launch reuses the PAK", "Up to date" in log2 and support.patch_file(game).read_bytes() == before)

    # ---- own-jcs-user ----
    game = run / "own-jcs-user" / "game"
    own = utf16([res(JCNS, True), "MOD_OWN_JCS_MARKER"])
    make(game, {JCNS: b"MOD_DRV_RPY_JCNS", JCS: own})
    log = support.run_loader(exe, game)
    (run / "own-jcs-user-log.txt").write_text(log, encoding="utf-8")
    entries, f, scene = inspect(game)
    sj, pj, pj0 = priv(JCNS, f, True), priv(JCS, f, False), priv(JCS0, f, False)
    data = entries.get(support.path_hash(pj), b"")
    r = refs(data)
    check("own-jcs-user: the slot's jcs.user is the mod's", "MOD_OWN_JCS_MARKER".encode("utf-16le") in data)
    check("own-jcs-user: the mod's jcs.user names the slot's .jcns", res(sj) in r and res(JCNS) not in r, repr(r))
    check("own-jcs-user: the game's 00_jcs.user gets its own copy too",
          res(sj) in refs(entries.get(support.path_hash(pj0), b"")))
    sr = refs(scene)
    check("own-jcs-user: the scene points at the slot's jcs.user files", res(pj) in sr and res(pj0) in sr and res(JCS) not in sr)
    check("own-jcs-user: nothing written at the original paths",
          all(support.path_hash(p) not in entries for p in (JCNS, JCS, JCS0)))

    # ---- unreferenced ----
    game = run / "unreferenced" / "game"
    make(game, {EXTRA: b"MOD_UNUSED_JCNS"})
    log = support.run_loader(exe, game)
    (run / "unreferenced-log.txt").write_text(log, encoding="utf-8")
    entries, f, scene = inspect(game)
    warn = [l for l in log.splitlines() if "WARN:" in l and EXTRA in l]
    check("unreferenced: one WARN naming the file", len(warn) == 1, "\n".join(warn))
    check("unreferenced: the slot is kept", "1 slots checked, 0 left out" in log)
    check("unreferenced: no private jcs.user (nothing to repoint)",
          support.path_hash(priv(JCS, f, False)) not in entries)

    # ---- without .jcns: same PAK as the previous build ----
    if args.old_loader:
        outs = []
        for label, loader in (("old", args.old_loader.resolve()), ("new", exe)):
            game = run / f"no-jcns-{label}" / "game"
            make(game, {})
            support.run_loader(loader, game)
            outs.append(support.patch_file(game).read_bytes())
        check("no .jcns in the mod: PAK byte-identical to the previous build's", outs[0] == outs[1])

    result = {"loader": str(exe), "checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks),
              "scope": "native SCN/USER reference packaging; synthetic data; no SF6 runtime test"}
    (run / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{result['passed']}/{result['total']} passed; report: {run / 'results.json'}")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
