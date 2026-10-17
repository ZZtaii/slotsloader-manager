"""Small, valid SF6 costume tables for native loader regression fixtures."""
import struct
import uuid

TABLE_PATH = "natives/stm/product/cfncontents/onlineshop/fightercostumeuserdata.user.2"
ROOT_TYPE, RECORD_TYPE, MESSAGE_TYPE = 0x2F581BD0, 0x3BBF93AC, 0x9B851E9B
CRCS = {ROOT_TYPE: 0x71C6B1B3, RECORD_TYPE: 0x88653730, MESSAGE_TYPE: 0x74C329FA}


def costume_table(vanilla=((1, 2, 0), (2, 2, 2)), generated=()):
    """Records are (record ID, fighter ID, costume number); preserve real ordering."""
    instances = [(0, None)]
    vanilla_refs = []
    for index, (rid, fighter, costume) in enumerate(vanilla):
        msg = len(instances)
        instances.append((MESSAGE_TYPE, (costume + 1, uuid.UUID(int=costume + 1).bytes_le)))
        vanilla_refs.append(len(instances))
        instances.append((RECORD_TYPE, (rid, rid, fighter, 1, costume, msg, 1, index + 1, costume, 0)))
    root = len(instances)
    instances.append((ROOT_TYPE, vanilla_refs))
    for index, (rid, fighter, costume) in enumerate(generated):
        msg = len(instances)
        instances.append((MESSAGE_TYPE, (5000 + index, uuid.UUID(int=5000 + index).bytes_le)))
        vanilla_refs.append(len(instances))
        instances.append((RECORD_TYPE, (rid, rid, fighter, 0, costume, msg, 1, 101 + index, costume, 0)))
    payload = bytearray()
    for typ, value in instances[1:]:
        payload.extend(b"\0" * (-len(payload) % 4))
        if typ == MESSAGE_TYPE:
            payload.extend(struct.pack("<I", value[0]))
            payload.extend(b"\0" * (-len(payload) % 8))
            payload.extend(value[1])
        elif typ == RECORD_TYPE:
            payload.extend(struct.pack("<10I", *value))
        else:
            payload.extend(struct.pack("<I", len(value)))
            payload.extend(struct.pack("<" + "I" * len(value), *value))
    rsz = 48
    data_off = (52 + 8 * len(instances) + 15) & ~15
    out = bytearray(rsz + data_off)
    out[:4] = b"USR\0"
    struct.pack_into("<QQ", out, 16, rsz, rsz)
    struct.pack_into("<Q", out, 32, rsz)
    struct.pack_into("<4sIIIIIQQQ", out, rsz, b"RSZ\0", 16, 1, len(instances), 0, 0,
                     52, data_off, data_off)
    struct.pack_into("<I", out, rsz + 48, root)
    for index, (typ, _) in enumerate(instances):
        struct.pack_into("<II", out, rsz + 52 + 8 * index, typ, CRCS.get(typ, 0))
    return bytes(out + payload)


def costume_records(blob):
    """Independent semantic view for comparing vanilla records after reindexing."""
    assert blob[:4] == b"USR\0"
    rsz = struct.unpack_from("<Q", blob, 32)[0]
    assert blob[rsz:rsz + 4] == b"RSZ\0"
    count = struct.unpack_from("<I", blob, rsz + 12)[0]
    infos, data = struct.unpack_from("<QQ", blob, rsz + 24)
    root = struct.unpack_from("<I", blob, rsz + 48)[0]
    values = {}
    offset = 0
    for index in range(1, count):
        typ = struct.unpack_from("<I", blob, rsz + infos + 8 * index)[0]
        offset = (offset + 3) & ~3
        at = rsz + data + offset
        if typ == MESSAGE_TYPE:
            msgid = struct.unpack_from("<I", blob, at)[0]
            guid_at = (offset + 4 + 7) & ~7
            values[index] = (msgid, blob[rsz + data + guid_at:rsz + data + guid_at + 16].hex())
            offset = guid_at + 16
        elif typ == RECORD_TYPE:
            values[index] = list(struct.unpack_from("<10I", blob, at))
            offset += 40
        elif typ == ROOT_TYPE:
            n = struct.unpack_from("<I", blob, at)[0]
            values[index] = struct.unpack_from("<" + "I" * n, blob, at + 4)
            offset += 4 + 4 * n
        else:
            raise AssertionError(f"Unexpected costume table type {typ:08x}")
    assert rsz + data + offset == len(blob)
    records = {}
    for index in values[root]:
        record = list(values[index])
        record[5] = values[record[5]]
        assert record[0] not in records
        records[record[0]] = record
    return records
