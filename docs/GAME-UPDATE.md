# October 13 compatibility candidate

SF6's updated vanilla costume table contains 137 records. The previous loader
bundle contains 134, so generating an extra-outfit PAK also removes three new
official records: fighter 34 costumes 0 and 1, and fighter 30 (C. Viper) costume 3.
Existing original records and the three supplied root scene prefixes still match.

The loader now combines the current base/DLC table's vanilla instances with the
bundle's generated extra-slot tail. It preserves vanilla fields and message
references, remaps appended references, and rejects unknown layouts, incompatible
CRCs or identity collisions before publishing replacement output. It then keeps
only the active extra records, as before. Both initial generation and rebuilds
use this operation.

## Loader data from the game (1.9.7)

Since 1.9.7 a game update that adds a fighter or an outfit needs no new loader
data. At each PAK build (only when something changed: the cache key already
covers the EXE, the base PAK index and the DLC PAKs) the loader:

- builds the six structural files from the game's current ones
  (costumes/loader/structural*.cpp, the rules of static_slots.py): the root
  fighter scene, every version scene from ver_02_0300 on that brings a fighter
  (a new ver_02_0600 or ver_03_0100 included), the costume table with 100
  records per fighter and the costume names. Log:
  `structural files built from the game: 6 files, slots for 32 fighters`, plus
  `not in the shipped tables: fighter N` for a new one;
- inventories from the base and DLC PAKs every costume the shipped
  vanilla_costume_index.tsv lacks (costumes/loader/vanilla_inventory.cpp, the
  rules of build_vanilla_inventory.py). Log:
  `vanilla index completed from the game: N costumes (...)`;
- creates `costume_mods\<Name>` for a fighter id the shipped folder list does
  not have, named from the game's English name.

The shipped vanilla_costume_index.tsv and loader/static/ stay in the package:
the index is the starting point, the static files the fallback. The built files
replace the shipped ones only if they declare every shipped slot record (same
record id) and every shipped file; otherwise, or if a game file cannot be read
(an external mod manager invalidated its row) or has an unknown layout, the shipped copies are
used and the reason is logged as `WARN: structural files taken from the shipped
copies (...)`. Record ids are fixed by fighter id (5000 + block * 100, blocks
1-22 -> 0-21, 25+ -> 22+), so saved choices and replays keep their slots.

Checked on the current game (tests/game_derived_real_input.py, read-only): the
six built files are byte-identical to the 1.9.6 static copies; the inventory of
all 176 costumes equals the shipped index byte for byte when the DLC PAKs are
read first as build_vanilla_inventory.py does (the loader reads the base PAK
first, as the game does; then JP, Dhalsim, Lily and Guile's Outfit 1 gain 75
colour rows, which matters only for a costume missing from the index). With the
1.9.5 data (no Arjun, no C. Viper costume 3) the loader completes both from the
game and writes the same PAK, byte for byte, as with the 1.9.6 data.

Limits: a 33rd fighter (35 and later) gets records 8200 and up, outside the
native plugin's 5000..8199 sweep of stale slot ownership (`kStaticMax` in
plugin/src/costumes.cpp); its slots are given ownership, but a removed slot's
ownership is not taken back until the plugin range grows. A new official costume
number 5 or higher would overlap the reserved slots and is refused as before.

The manual regeneration below (build_vanilla_inventory.py, inventory_to_tsv.py,
static_slots.py) remains the fallback and the reference the tests compare with.

## New official costumes (1.9.6)

The update adds C. Viper costume 3 (displayed Costume 4, model folder
esf030/004) and fighter 34, Arjun (costumes 0 and 1 in esf034/001 and 002, plus
action costume 100). Both are declared by a new version scene,
`natives/stm/product/flow/version_update/ver_02_0500/esf.scn.20` (folders
fighter034/esf034a00, v00, wts00, v01 and fighter030/esf030v03).

- vanilla_costume_index.tsv was regenerated from the current base and DLC PAKs
  with build_vanilla_inventory.py and converted by inventory_to_tsv.py. All 172
  previous costumes keep their order; 126 are byte-identical to the 1.9.5 index
  and 46 only gain update-added colour files. Without these rows a mod for the
  new outfits was ignored ("no costume it can complete").
- Fighter 34 has reserved records 8100..8199 (costumes 5..104). The static
  costume table is rebuilt from the current game table (137 official records),
  so the runtime rebase is a no-op on the current game.
- The static ver_02_0500 scene keeps its seven vanilla folders and adds
  esf034v05..v104 under fighter034 (template: Arjun's v01 folder, since he has no
  DriveTech v04). The loader exempts it from original-asset protection like the
  other structural files. C. Viper's extra slots stay on costumes 5..104, so they
  never touch the official costume 3; slot model folders start after the highest
  vanilla folder (006 for C. Viper, 003 for Arjun).
- Arjun has no DriveTech costume. The native plugin now notices a character with
  slots but no DriveTech record, keeps its slots offline and stores Outfit 1 in
  Battle Settings for it. Without that change (code reading, not observed in
  game), one such character would keep the plugin's boot sequence pending for ten
  minutes, disable the online alias for every character and write the absent
  DriveTech number into Arjun's Battle Settings entry.

Because ver_02_0500 is now replaced by a static copy, a future game update that
changes that scene must be followed by `static_slots.py verify` (and a rebuild)
before shipping; otherwise new official folders in it would be hidden. Since
1.9.7 the loader builds that scene from the game's own at each PAK build, so the
static copy is only used when that build falls back (see above).

The cache also tracks the EXE's size/date/header, the base PAK's size and index,
the inventory, consumed static files and metadata, and DLC size/date. Base index
hash fields and the base PAK timestamp are excluded because external mod managers change those
when enabling/disabling ordinary replacements. Payload offsets, lengths, flags
and checksums remain part of the key. The multi-gigabyte PAK payload is not hashed
at launch; same-size payload changes with unchanged checksums and an unchanged
EXE identity are outside this check.

The genuine AMD DLL from the current game is byte-for-byte identical to the one
in the previous package. Its 38 exports and the current EXE's seven AGS imports
match the proxy. Replacing it with a different AMD release is unsupported by this
evidence. This build preserves the forwarders, costumes-only scope, mod-manager
coexistence and private chain/cloth resource fixes.

## Install and verify

Close SF6. Disable the old SlotsLoader entry in your mod manager, refresh its mod list, and
install the new uniquely named candidate ZIP. Keep personal costume_mods content.
The package's build ID triggers regeneration. Both loader DLLs belong together.

Check a normal outfit and stage, C. Viper's fourth official costume and Arjun's
two official costumes, then one known extra outfit in a battle, then a C. Viper
Costume 4 mod and an Arjun mod as extra outfits (offline for Arjun). Inspect the loader log for the line
`current game records preserved before adding extra slots`. If rendering still
breaks, disable this candidate and retain that log for the next isolation step.

To roll back, close SF6, disable this package, and reinstall the prior package.
Do not delete arbitrary patch PAKs: other mods may own them. A refresh error does
not replace existing generated PAKs; keep the loader disabled if the error says
the current table is incompatible.

## Verification scope

Native synthetic tests cover table rebasing, malformed input, collisions, cache
updates, unchanged reuse, mod-manager coexistence and private physics references. A
separate real-input comparison uses copied metadata/mods and opens the current
base PAK read-only. It checks all current vanilla records, retained extra outfits
and absence of original asset collisions. DLCs are omitted from that real-input
comparison and reported explicitly. These checks do not render SF6 or establish
that every reported mesh/stage symptom is resolved.

On 2026-10-13, 1.9.6 was confirmed in game: two C. Viper Costume 4 mods load as
extra outfits (Outfit I/II; the log reports 176 costumes). An Arjun extra outfit,
the no-DriveTech plugin path and online play are still untested.
