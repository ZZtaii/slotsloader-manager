# Static Structural Files -- Costume Slot Loader

## Purpose

These pre-built binary files contain the RSZ/MSG data the costume loader needs
to declare extra outfit slots. At generation it rebases the costume table onto
the current game's table (`refresh_costume_table`) and keeps only the records of
slots in use (`trim_costume_table`).

Since 1.9.7 the loader builds the same files from the game's own at each PAK
build (costumes/loader/structural.cpp, structural_scene.cpp, structural_msg.cpp,
add_costume_records in patch.cpp), with the rules of static_slots.py below, and
puts those in its output PAK. The shipped copies are its fallback: they are used
when the files cannot be built from the game, or when what was built lacks a
shipped slot record or file (logged as `WARN: structural files taken from the
shipped copies`). static_meta.json's records are then the ones in use; otherwise
the records are those built (identical for every shipped fighter). For the game
below, the loader's files are byte-identical to these copies
(tests/game_derived_real_input.py), and static_slots.py `verify` reproduces the
loader's output on a synthetic game (tests/game_derived_fixture.py).

One deliberate difference: the loader probes every version scene name from
ver_02_0300 up to ver_09_9999, and only fighters of the game's costume table get
folders (static_slots.py adds folders for any v04 fighter of a listed scene; no
such non-table fighter exists).

## Files (1.9.6-custom, built from the October 13 game, Steam build 25071972)

| File | Size | Content |
|------|------|---------|
| `esf.scn.20` | 322864 B | Vanilla 81 folders + 1800 `via.Folder` (18 base fighters x v05..v104, v00 template) |
| `ver_02_0300_esf.scn.20` | 216752 B | Vanilla 71 folders + 1200 (12 DLC fighters, v04 template; esf103 has no v04 and no records) |
| `ver_02_0400_esf.scn.20` | 18320 B | Vanilla 6 folders + 100 (esf033 Yasmine, v04 template) |
| `ver_02_0500_esf.scn.20` | 18424 B | Vanilla 7 folders (fighter034 a00/v00/wts00/v01, fighter030 esf030v03) + 100 (esf034 Arjun, v01 template: no v04) |
| `fightercostumeuserdata.user.2` | 181240 B | Current 137 official records + 100 shared messages + 3200 reserved records (32 fighters x 100) |
| `fightercostumemessage.msg.21` | 118644 B | Vanilla 5 messages + 100 ("Outfit I" .. "Outfit C"), unchanged since 1.9.4 |
| `static_meta.json` | 367163 B | ID map and file list for the C++ loader (CRLF, `json.dump(indent=2)`) |

## ID allocation

| Range | Values | Purpose |
|-------|--------|---------|
| Costume record id / ManageId | 5000..8199 | `5000 + fighter_index*100 + (costumeNo-5)`, costumeNo 5..104 |
| FighterCostumeMessage id | 5000..5099 | 100 shared messages, one per slot offset |
| sortNo | 101..3300 | Ascending, one per record |
| Message GUIDs | `a1b2c3d4-NNNN-4000-8000-0000000013NN` | Fixed, deterministic (`static_meta.json` msg_guids) |

The native plugin sweeps stale ownership over exactly 5000..8199, so a 33rd
fighter needs a plugin change (`kStaticMax`) as well as new records. The loader
gives such a fighter records from the game (fighter 35: 8200..8299); until the
plugin range grows, a removed slot of it keeps its ownership in the save.

Official records of the current game: ids 1..147 (137 records), including the
October 13 additions 137 (fighter 34 costume 0), 143 (fighter 34 costume 1) and
147 (C. Viper costume 3). Reserved costume numbers start at 5, so they never
overlap an official costume 0..4 or action costume 100+.

## fighter_index mapping

```
Index  FighterID  Name        Index  FighterID  Name
  0        1      Ryu           16       17      Marisa
  1        2      Luke          17       18      Guile
  2        3      Kimberly      18       19      Ed
  3        4      Chun-Li       19       20      E.Honda
  4        5      Manon         20       21      Jamie
  5        6      Zangief       21       22      Akuma
  6        7      JP            22       25      Sagat
  7        8      Dhalsim       23       26      Bison
  8        9      Cammy         24       27      Terry
  9       10      Ken           25       28      Mai
 10       11      DeeJay        26       29      Elena
 11       12      Lily          27       30      C.Viper
 12       13      AKI           28       31      Alex
 13       14      Rashid        29       32      Ingrid
 14       15      Blanka        30       33      Yasmine
 15       16      Juri          31       34      Arjun
```

New fighters are appended (never inserted) so existing record ids, saved
choices and replays keep their meaning.

## Regeneration

`static_slots.py` (standard library only) works on the current vanilla files,
extracted read-only from the base PAK into a natives tree, e.g. with
`build/paktool.exe extract <re_chunk_000.pak> <path> <out>`:

```
python static_slots.py verify --vanilla <natives root> --static <shipped static dir>
python static_slots.py build  --vanilla <natives root> --static <shipped static dir> --out <dir> \
       [--add-fighter 34] [--add-scene ver_02_0500]
```

`verify` rebuilds every scene, the costume table and `static_meta.json` from the
vanilla files with the shipped parameters and requires byte equality; it
reproduced all 1.9.5 files exactly before fighter 34 was added. Since 1.9.7 the
loader does this at each PAK build, so a stale copy no longer hides official
folders unless the loader falls back to it; regenerating after an update keeps
the fallback current. `static_slots.py build` refuses record ids above 8199 (the
plugin sweep), which the loader's own build does not.

`make_static_structural.py` is the original REasy generator, kept for reference.
The message file is carried over unchanged; its five vanilla message GUIDs are
the ones the current table's 137 records reference.
