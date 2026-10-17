#!/usr/bin/env python3
"""static_slots.py -- build the loader's static structural files without REasy.

The loader ships pre-built structural files (costumes/tools/README_STATIC.md).
make_static_structural.py produced them through REasy; this tool performs the
same operations with explicit, validated binary layouts and the standard
library only, so the reserved extra slots can be extended when the game adds a
fighter or a version-update scene.

Inputs are the CURRENT vanilla structural files, extracted read-only from the
base PAK into a natives tree (build/paktool.exe or REtool):
  natives/stm/product/charparam/esf/esf.scn.20
  natives/stm/product/flow/version_update/ver_02_0300/esf.scn.20  (and later versions)
  natives/stm/product/cfncontents/onlineshop/fightercostumeuserdata.user.2

Commands:
  verify  --vanilla <natives root> --static <shipped static dir>
          Rebuild every shipped scene/table from vanilla with the parameters of
          the shipped static_meta.json and require byte equality.
  build   --vanilla <natives root> --static <shipped static dir> --out <dir>
          [--add-fighter ID ...] [--add-scene ver_02_0500 ...]
          Write the scenes, the costume table and static_meta.json with the
          extra fighters/scenes; the message file is copied unchanged.

Every parser is strict: scenes must contain only via.Folder instances and
round-trip byte for byte, the costume table must match the layout checked by
costume_table_layout() in costumes/loader/patch.cpp. Anything else is refused.
"""

import argparse
import json
import os
import re
import shutil
import struct
import sys
import uuid

# ---------------------------------------------------------------------------
# Constants shared with make_static_structural.py / loader_core.cpp
# ---------------------------------------------------------------------------
ESF_ROOT = "natives/stm/product/charparam/esf/esf.scn.20"
VER_SCENE = "natives/stm/product/flow/version_update/{ver}/esf.scn.20"
COSTUME_TABLE = "natives/stm/product/cfncontents/onlineshop/fightercostumeuserdata.user.2"
COSTUME_MSG = "natives/stm/product/message/fgm/fighter/fightercostumemessage.msg.21"

T_FOLDER, CRC_FOLDER = 0xAEA2D9CC, 0x76400E51
T_ROOT = 0x2F581BD0
T_REC = 0x3BBF93AC
T_MSG = 0x9B851E9B
REC_SIZE = 40


def align(x, a):
    return (x + a - 1) // a * a


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def u64(b, o):
    return struct.unpack_from("<Q", b, o)[0]


class Refused(Exception):
    pass


# ---------------------------------------------------------------------------
# SCN (.scn.20) holding only via.Folder instances
# ---------------------------------------------------------------------------
def _read_str(b, base, pos):
    pos = align(pos, 4)
    n = u32(b, base + pos)
    raw = b[base + pos + 4: base + pos + 4 + 2 * n]
    if len(raw) != 2 * n:
        raise Refused("truncated string")
    return raw.decode("utf-16le"), pos + 4 + 2 * n


def _put_str(out, s):
    out += b"\0" * (align(len(out), 4) - len(out))
    out += struct.pack("<I", len(s)) + s.encode("utf-16le")


def parse_scn(b):
    if b[:4] != b"SCN\0":
        raise Refused("not a SCN file")
    _, gi, rc, fc, pc, uc, ft, rt, pt, ut, do = struct.unpack_from("<4s5I5Q", b, 0)
    if gi or rc or pc or uc:
        raise Refused("scene has game objects/resources/prefabs/user data")
    if ft != 64:
        raise Refused("unexpected folder table offset")
    folders = [struct.unpack_from("<ii", b, ft + 8 * i) for i in range(fc)]
    r = do
    if b[r:r + 4] != b"RSZ\0" or u32(b, r + 4) != 16:
        raise Refused("unsupported RSZ header")
    oc, ic, udc, resv = struct.unpack_from("<4I", b, r + 8)
    io, dof, uo = struct.unpack_from("<3Q", b, r + 24)
    if udc or resv:
        raise Refused("RSZ user data present")
    objects = list(struct.unpack_from("<%dI" % oc, b, r + 48))
    infos = [struct.unpack_from("<II", b, r + io + 8 * i) for i in range(ic)]
    if infos[0] != (0, 0) or any(t != (T_FOLDER, CRC_FOLDER) for t in infos[1:]):
        raise Refused("scene holds instances other than via.Folder")
    base = r + dof
    pos = 0
    inst = [None]
    for _ in range(1, ic):
        name, pos = _read_str(b, base, pos)
        tag, pos = _read_str(b, base, pos)
        flags = b[base + pos: base + pos + 3]
        pos += 3
        scene, pos = _read_str(b, base, pos)
        pos = align(pos, 8)
        offset = b[base + pos: base + pos + 24]
        pos += 24
        inst.append({"Name": name, "Tag": tag, "flags": flags, "ScenePath": scene, "offset": offset})
    if base + pos != len(b):
        raise Refused("trailing scene data")
    scn = {"folders": folders, "objects": objects, "instances": inst}
    if build_scn(scn) != b:
        raise Refused("scene does not round-trip")
    return scn


def build_scn(scn):
    folders, objects, inst = scn["folders"], scn["objects"], scn["instances"]
    fc, oc, ic = len(folders), len(objects), len(inst)
    tables = align(64 + 8 * fc, 16)
    out = bytearray(struct.pack("<4s5I5Q", b"SCN\0", 0, 0, fc, 0, 0, 64, tables, tables, tables, tables))
    for f in folders:
        out += struct.pack("<ii", *f)
    out += b"\0" * (tables - len(out))
    io = 48 + 4 * oc
    dof = align(io + 8 * ic, 16)
    rsz = bytearray(struct.pack("<4sIIIII3Q", b"RSZ\0", 16, oc, ic, 0, 0, io, dof, dof))
    rsz += struct.pack("<%dI" % oc, *objects)
    rsz += struct.pack("<II", 0, 0)
    rsz += struct.pack("<II", T_FOLDER, CRC_FOLDER) * (ic - 1)
    rsz += b"\0" * (dof - len(rsz))
    data = bytearray()
    for it in inst[1:]:
        _put_str(data, it["Name"])
        _put_str(data, it["Tag"])
        data += it["flags"]
        _put_str(data, it["ScenePath"])
        data += b"\0" * (align(len(data), 8) - len(data))
        data += it["offset"]
    return bytes(out + rsz + data)


def scn_names(scn):
    """folder index -> (name without NUL, instance index)"""
    res = []
    for i, (oid, _parent) in enumerate(scn["folders"]):
        iid = scn["objects"][oid]
        res.append((scn["instances"][iid]["Name"].rstrip("\0"), iid))
    return res


def scn_fighter_dirs(scn, pattern):
    dirs = set()
    for name, _ in scn_names(scn):
        m = re.match(pattern, name)
        if m:
            dirs.add(m.group(1))
    return dirs


def add_scene_folders(scn, fighter_dirs, slot_range, template_re):
    """Append one via.Folder per (fighter, slot) exactly as make_static_structural.py:
    a copy of the fighter's template folder, renamed esfNNNvSS with its ScenePath,
    under the template's parent; instances and objects appended in order."""
    templates = {}
    for i, (oid, parent) in enumerate(scn["folders"]):
        iid = scn["objects"][oid]
        m = re.match(template_re, scn["instances"][iid]["Name"].rstrip("\0"))
        if m and m.group(1) in fighter_dirs:
            templates[m.group(1)] = (parent, iid)
    added = 0
    for fd in sorted(fighter_dirs):
        if fd not in templates:
            raise Refused(f"no template folder for {fd}")
        parent, tiid = templates[fd]
        t = scn["instances"][tiid]
        for cno in slot_range:
            name = f"{fd}v{cno:02d}"
            nm_sfx = "\0" if t["Name"].endswith("\0") else ""
            sp_sfx = "\0" if t["ScenePath"].endswith("\0") else ""
            scn["instances"].append({
                "Name": name + nm_sfx, "Tag": t["Tag"], "flags": t["flags"],
                "ScenePath": f"Product/CharParam/esf/{fd}/{name}.scn" + sp_sfx,
                "offset": t["offset"]})
            scn["objects"].append(len(scn["instances"]) - 1)
            scn["folders"].append((len(scn["objects"]) - 1, parent))
            added += 1
    return added


def dlc_template_re(scn, fighter_dirs):
    """v04 (DriveTech) is the historical template. A fighter without it (fighter 34
    at release) uses its highest-numbered vNN folder; all of them carry identical
    Tag/flags/offset in the vanilla scenes, which is checked here."""
    names = scn_names(scn)
    for fd in fighter_dirs:
        own = [(n, iid) for n, iid in names if re.fullmatch(fd + r"v\d{2}", n)]
        if not own:
            raise Refused(f"{fd}: no costume folder to use as template")
        sig = {(scn["instances"][iid]["Tag"], scn["instances"][iid]["flags"],
                scn["instances"][iid]["offset"]) for _, iid in own}
        if len(sig) != 1:
            raise Refused(f"{fd}: costume folders differ in Tag/flags/offset")
    return None


# ---------------------------------------------------------------------------
# Costume table (fightercostumeuserdata.user.2), layout of patch.cpp
# ---------------------------------------------------------------------------
def parse_table(b):
    if len(b) < 104 or b[:4] != b"USR\0" or any(struct.unpack_from("<3I", b, 4)) \
            or struct.unpack_from("<3Q", b, 16) != (48, 48, 48):
        raise Refused("unsupported costume table USR header")
    r = 48
    if b[r:r + 4] != b"RSZ\0" or u32(b, r + 4) != 16 or u32(b, r + 8) != 1 \
            or u32(b, r + 16) != 0 or u64(b, r + 24) != 52:
        raise Refused("unsupported costume table RSZ header")
    count = u32(b, r + 12)
    root = u32(b, r + 48)
    d = u64(b, r + 32)
    if d != u64(b, r + 40) or d != align(52 + 8 * count, 16):
        raise Refused("costume table offsets")
    infos = [struct.unpack_from("<II", b, r + 52 + 8 * i) for i in range(count)]
    base = r + d
    pos = 0
    inst = [None]
    for i in range(1, count):
        t, crc = infos[i]
        s = align(pos, 4)
        if t == T_MSG:
            g = align(s + 4, 8)
            inst.append(("msg", crc, u32(b, base + s), b[base + g: base + g + 16]))
            pos = g + 16
        elif t == T_REC:
            inst.append(("rec", crc, list(struct.unpack_from("<10I", b, base + s))))
            pos = s + REC_SIZE
        elif t == T_ROOT and i == root:
            n = u32(b, base + s)
            inst.append(("root", crc, list(struct.unpack_from("<%dI" % n, b, base + s + 4))))
            pos = s + 4 + 4 * n
        else:
            raise Refused("unsupported costume table instance")
    if base + pos != len(b):
        raise Refused("costume table trailing data")
    tbl = {"root": root, "inst": inst}
    if build_table(tbl) != b:
        raise Refused("costume table does not round-trip")
    return tbl


def build_table(tbl):
    inst = tbl["inst"]
    count = len(inst)
    d = align(52 + 8 * count, 16)
    out = bytearray(b"USR\0" + bytes(12) + struct.pack("<4Q", 48, 48, 48, 0))
    out += struct.pack("<4sIIIII3QI", b"RSZ\0", 16, 1, count, 0, 0, 52, d, d, tbl["root"])
    out += struct.pack("<II", 0, 0)
    for it in inst[1:]:
        t = {"msg": T_MSG, "rec": T_REC, "root": T_ROOT}[it[0]]
        out += struct.pack("<II", t, it[1])
    out += b"\0" * (48 + d - len(out))
    data = bytearray()
    for it in inst[1:]:
        data += b"\0" * (align(len(data), 4) - len(data))
        if it[0] == "msg":
            data += struct.pack("<I", it[2])
            data += b"\0" * (align(len(data), 8) - len(data))
            data += it[3]
        elif it[0] == "rec":
            data += struct.pack("<10I", *it[2])
        else:
            data += struct.pack("<I", len(it[2])) + struct.pack("<%dI" % len(it[2]), *it[2])
    return bytes(out + data)


def table_records(tbl):
    return [(i, it[2]) for i, it in enumerate(tbl["inst"]) if it and it[0] == "rec"]


def build_reserved_table(vanilla, meta):
    """Current vanilla table + 100 shared messages + reserved records per fighter,
    the operations of make_static_structural.py section 4."""
    tbl = parse_table(vanilla)
    root = tbl["root"]
    if root != len(tbl["inst"]) - 1:
        raise Refused("vanilla table has instances after its root")
    recs = table_records(tbl)
    tmpl = next((r for _, r in recs if r[2] == 1 and r[4] == 0), None)
    if tmpl is None:
        raise Refused("no fighter 1 costume 0 template record")
    msg_crc = next(it[1] for it in tbl["inst"][1:] if it[0] == "msg")
    rec_crc = next(it[1] for it in tbl["inst"][1:] if it[0] == "rec")
    lo, hi = meta["slot_range"]
    slots = list(range(lo, hi))
    msg_index = []
    for so in range(len(slots)):
        guid = uuid.UUID(meta["msg_guids"][so]).bytes_le
        tbl["inst"].append(("msg", msg_crc, meta["msg_id_base"] + so, guid))
        msg_index.append(len(tbl["inst"]) - 1)
    sort_no = meta["sort_no_base"]
    by_key = {(r["fighter"], r["costume_no"]): r for r in meta["records"]}
    array = tbl["inst"][root][2]
    for fid in meta["fighter_ids"]:
        for so, cno in enumerate(slots):
            m = by_key[(fid, cno)]
            rec = list(tmpl)
            rec[0], rec[1], rec[2] = m["record_id"], m["manage_id"], fid
            rec[3] = 0                      # isDefault = false (padding bytes stay zero)
            rec[4], rec[5] = cno, msg_index[so]
            rec[6] = tmpl[6] & 0xFF         # isShowName copied from the template
            rec[7], rec[8], rec[9] = sort_no, cno, 0
            tbl["inst"].append(("rec", rec_crc, rec))
            array.append(len(tbl["inst"]) - 1)
            sort_no += 1
    return build_table(tbl)


# ---------------------------------------------------------------------------
# Metadata
# ---------------------------------------------------------------------------
def meta_records(fighter_ids, slot_range, record_base):
    n = slot_range[1] - slot_range[0]
    return [{"fighter": fid, "costume_no": cno,
             "record_id": record_base + fi * n + so, "manage_id": record_base + fi * n + so}
            for fi, fid in enumerate(fighter_ids)
            for so, cno in enumerate(range(*slot_range))]


def dump_meta(meta):
    # make_static_structural.py wrote it with json.dump(indent=2) in Windows text mode
    return json.dumps(meta, indent=2).replace("\n", "\r\n")


# ---------------------------------------------------------------------------
# Scenes from vanilla + metadata
# ---------------------------------------------------------------------------
def read(path):
    with open(path, "rb") as fh:
        return fh.read()


def version_keys(meta):
    return [k[len("dlc_fighter_dirs_"):] for k in meta if k.startswith("dlc_fighter_dirs_")]


def build_scenes(vanilla_root, meta, log):
    slot_range = range(*meta["slot_range"])
    out = {}
    esf = parse_scn(read(os.path.join(vanilla_root, *ESF_ROOT.split("/"))))
    base_dirs = scn_fighter_dirs(esf, r"(esf\d{3})v00$")
    if sorted(base_dirs) != meta["base_fighter_dirs"]:
        raise Refused(f"base fighters differ: {sorted(base_dirs)}")
    n = add_scene_folders(esf, base_dirs, slot_range, r"(esf\d{3})v00$")
    out["esf.scn.20"] = build_scn(esf)
    log(f"esf.scn.20: {n} folders for {len(base_dirs)} base fighters")
    seen = set(base_dirs)
    for key in version_keys(meta):
        ver = "ver_02_" + key
        scn = parse_scn(read(os.path.join(vanilla_root, *VER_SCENE.format(ver=ver).split("/"))))
        dirs = scn_fighter_dirs(scn, r"(esf\d{3})v\d{2}$") - seen
        if sorted(dirs) != meta["dlc_fighter_dirs_" + key]:
            raise Refused(f"{ver}: fighters differ: {sorted(dirs)}")
        seen |= dirs
        with_v04 = {fd for fd in dirs
                    if any(n == fd + "v04" for n, _ in scn_names(scn))}
        n = 0
        if with_v04:
            n += add_scene_folders(scn, with_v04, slot_range, r"(esf\d{3})v04$")
        # A folder without v04 got no reserved folders before (esf103: no records).
        # A fighter that has reserved records takes its highest vNN as template.
        recorded = {f"esf{fid:03d}" for fid in meta["fighter_ids"]}
        skipped = sorted((dirs - with_v04) - recorded)
        if skipped:
            log(f"{ver}: no v04 and no reserved records, no folders for {skipped}")
        rest = (dirs - with_v04) & recorded
        if rest:
            dlc_template_re(scn, rest)
            for fd in sorted(rest):
                last = max(int(nm[-2:]) for nm, _ in scn_names(scn)
                           if re.fullmatch(fd + r"v\d{2}", nm))
                n += add_scene_folders(scn, {fd}, slot_range, r"(esf\d{3})v%02d$" % last)
        out[f"{ver}_esf.scn.20"] = build_scn(scn)
        log(f"{ver}: {n} folders for {sorted(with_v04 | rest)}"
            + (f" (template: highest vNN for {sorted(rest)})" if rest else ""))
    return out


def cmd_verify(args):
    meta = json.loads(read(os.path.join(args.static, "static_meta.json")))
    ok = True
    def log(s): print("  " + s)
    scenes = build_scenes(args.vanilla, meta, log)
    for name, data in scenes.items():
        shipped = read(os.path.join(args.static, name))
        same = data == shipped
        ok &= same
        print(f"{name}: {'IDENTICAL' if same else 'DIFFERENT'} ({len(data)} vs {len(shipped)} bytes)")
        # the shipped scene itself must parse and round-trip
        parse_scn(shipped)
    table = build_reserved_table(read(os.path.join(args.vanilla, *COSTUME_TABLE.split("/"))), meta)
    shipped = read(os.path.join(args.static, "fightercostumeuserdata.user.2"))
    same = table == shipped
    ok &= same
    print(f"fightercostumeuserdata.user.2: {'IDENTICAL' if same else 'DIFFERENT'} "
          f"({len(table)} vs {len(shipped)} bytes)")
    recs = meta_records(meta["fighter_ids"], meta["slot_range"], meta["record_id_base"])
    same = recs == meta["records"]
    ok &= same
    print(f"static_meta.json records: {'IDENTICAL' if same else 'DIFFERENT'}")
    raw = read(os.path.join(args.static, "static_meta.json")).decode("utf-8")
    same = dump_meta(meta) == raw
    ok &= same
    print(f"static_meta.json serialization: {'IDENTICAL' if same else 'DIFFERENT'}")
    return 0 if ok else 1


def cmd_build(args):
    meta = json.loads(read(os.path.join(args.static, "static_meta.json")))
    for fid in args.add_fighter or []:
        if fid in meta["fighter_ids"]:
            raise Refused(f"fighter {fid} already has reserved records")
        meta["fighter_ids"].append(fid)
    for ver in args.add_scene or []:
        m = re.fullmatch(r"ver_02_(\d{4})", ver)
        if not m:
            raise Refused(f"bad version scene name {ver}")
        key = m.group(1)
        if "dlc_fighter_dirs_" + key in meta:
            raise Refused(f"{ver} already present")
        scn = parse_scn(read(os.path.join(args.vanilla, *VER_SCENE.format(ver=ver).split("/"))))
        earlier = set(meta["base_fighter_dirs"])
        for k in version_keys(meta):
            earlier |= set(meta["dlc_fighter_dirs_" + k])
        dirs = sorted(scn_fighter_dirs(scn, r"(esf\d{3})v\d{2}$") - earlier)
        # keep key order: insert after the last dlc_fighter_dirs_* key
        items = list(meta.items())
        last = max(i for i, (k, _) in enumerate(items) if k.startswith("dlc_fighter_dirs_"))
        items.insert(last + 1, ("dlc_fighter_dirs_" + key, dirs))
        meta = dict(items)
        meta["files"].insert(
            max(i for i, f in enumerate(meta["files"]) if f["local"].startswith("ver_02_")) + 1,
            {"local": f"{ver}_esf.scn.20", "pak_path": VER_SCENE.format(ver=ver)})
    covered = set(meta["base_fighter_dirs"])
    for k in version_keys(meta):
        covered |= set(meta["dlc_fighter_dirs_" + k])
    for fid in meta["fighter_ids"]:
        if f"esf{fid:03d}" not in covered:
            raise Refused(f"fighter {fid} has records but no scene declares its folders")
    meta["records"] = meta_records(meta["fighter_ids"], meta["slot_range"], meta["record_id_base"])
    ids = [r["record_id"] for r in meta["records"]]
    if len(set(ids)) != len(ids) or max(ids) > 8199:
        raise Refused("reserved record ids collide or exceed the plugin's 5000..8199 range")
    os.makedirs(args.out, exist_ok=True)
    def log(s): print("  " + s)
    scenes = build_scenes(args.vanilla, meta, log)
    table = build_reserved_table(read(os.path.join(args.vanilla, *COSTUME_TABLE.split("/"))), meta)
    for name, data in list(scenes.items()) + [("fightercostumeuserdata.user.2", table)]:
        with open(os.path.join(args.out, name), "wb") as fh:
            fh.write(data)
        print(f"wrote {name} ({len(data)} bytes)")
    shutil.copyfile(os.path.join(args.static, "fightercostumemessage.msg.21"),
                    os.path.join(args.out, "fightercostumemessage.msg.21"))
    print("copied fightercostumemessage.msg.21 (unchanged)")
    with open(os.path.join(args.out, "static_meta.json"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(dump_meta(meta))
    print(f"wrote static_meta.json ({len(meta['fighter_ids'])} fighters, {len(meta['records'])} records)")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    v = sub.add_parser("verify")
    v.add_argument("--vanilla", required=True)
    v.add_argument("--static", required=True)
    b = sub.add_parser("build")
    b.add_argument("--vanilla", required=True)
    b.add_argument("--static", required=True)
    b.add_argument("--out", required=True)
    b.add_argument("--add-fighter", type=int, action="append")
    b.add_argument("--add-scene", action="append")
    args = ap.parse_args()
    try:
        return cmd_verify(args) if args.cmd == "verify" else cmd_build(args)
    except Refused as exc:
        print(f"REFUSED: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
