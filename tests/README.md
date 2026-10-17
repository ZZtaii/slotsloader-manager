# Mod-manager coexistence regression fixture

## Colours from the game folder (1.9.8)

After `build.bat`, run `python tests/loose_colours.py [--root <new short folder>]
[--old-loader <previous exe>]`. Synthetic games (fluffy_coexistence's fixture)
whose colour list names colour 002: (a) its PAK entry zeroed and a loose copy in
the game's natives folder: the slot has the loose bytes, one log line names the
file, nothing is written at the original path, game_colours.tsv records it; (b)
zeroed, no loose file: colour 001 stands in, as before, and a loose file that
appears later rebuilds; (c) entry present and a loose file: the PAK original,
and a change of that loose file does not rebuild; (d) on game a: unchanged
launches are "Up to date", a same-size same-date content change, removing and
adding back the loose file, the entry restored (mod-manager uninstall) and zeroed
again with a loose file (mod-manager install after the build) all rebuild with the
expected colour, an unrelated entry zeroed or restored does not; (e) a game
folder of 170 characters and a long colour name (a 341-character loose path).
With `--root C:\SF6T\<new>` the game folders of a-d are 49 characters long, as
the game folder of a Steam library on another drive. With
`--old-loader` (1.9.7) case a gives the stand-in. The game folders are deleted
afterwards (`--keep` keeps them); logs and results.json stay in the root.

## Add-on options naming an option (1.9.8)

After `build.bat`, run `python tests/option_addons.py [--old-loader <previous exe>]`.
On nested_modular's fixture game, one ZIP with "00 Main" (name "Fixture Main"),
"01 Ribbon" (AddonFor "fixture main  ", own body mesh) and "02 Other" (AddonFor
naming nothing): three outfits, the main (its body, hair, texture), "Fixture Main
+ Ribbon" (Ribbon's body, the main's hair and texture) and "Other", no Ribbon
alone. With `--old-loader`, the previous build makes Ribbon alone without the
main's files.

## Joint constraints (1.9.8)

After `build.bat`, run `python tests/joint_constraints.py [--old-loader <previous
exe>]`. Synthetic games (fluffy_coexistence's fixture) whose scene names two
jcs.user files that name the game's `drv_RPY.jcns` and `drv_BS1.jcns`. A mod
that ships the `drv_rpy.jcns`: the slot's copy of each jcs.user names the
slot's `.jcns` and keeps `drv_BS1` the game's, the scene names those copies,
nothing is written at an original path, the log names the jcs.user, a repeat
launch is "Up to date". A mod that also ships its jcs.user: that file,
repointed, is the slot's. A `.jcns` nothing names: one WARN naming it, the slot
kept. With `--old-loader`, a mod without `.jcns` gives a PAK byte-identical to
the previous build's.

## Loader data from the game (1.9.7)

After `build.bat`, run `tests/build_game_derived.bat`: build/game_derived.exe runs
the loader's own game-derived code (structural.cpp, vanilla_inventory.cpp,
fighter_names.cpp) on given PAKs, read-only.

`python tests/game_derived_real_input.py --loader build/costume_loader.exe --tool
build/game_derived.exe --paktool build/paktool.exe --metadata <loader dir>
--base-pak <re_chunk_000.pak> [--dlc <pak> ...] --output <new dir> [--old-loader
<previous exe>] [--old-metadata <older loader dir>]` compares, on the current
game: the six built structural files with the shipped static copies (byte for
byte) and the slot records with static_meta.json; the inventory of every costume
with vanilla_costume_index.tsv (byte for byte with the DLC PAKs read first, as
build_vanilla_inventory.py does; in the loader's base-first order only JP,
Dhalsim, Lily and Guile's Outfit 1 differ, by 75 added colour rows); the English
fighter names. It then runs the loader on outfits copied from the October 13
costumes (DLC omitted): with the current data its PAK must be byte-identical to
the previous build's; with data that predates the update (`--old-metadata`, the
1.9.5 data) it must complete the index (four costumes) and the slot tables
(fighter 34) from the game and write the same PAK, while the previous build
makes no slot.

`python tests/game_derived_fixture.py [--old-loader <previous exe>]` needs no game:
a synthetic game with today's structural layout gets shipped data made by
game_derived.exe, which `static_slots.py verify` must reproduce byte for byte.
A synthetic update then adds fighter 35 in a new ver_03_0100 scene (no v04) and
an Arjun Outfit 3 in ver_02_0600. The loader must give fighter 35 records
8200..8299, a costume_mods folder named from the game, index rows (by path scan)
and a slot for its outfit, publish ver_03_0100 and keep every other structural
file at its shipped bytes, reuse the cache when unchanged or when an external
mod manager invalidates an unrelated row, and fall back to the shipped copies, logged, when a
shipped scene's row is invalidated or the new scene is unreadable. The previous
build makes no slot for fighter 35.

`costume_table_refresh.exe` also checks add_costume_records against its
independent table writer (same bytes, refresh is then the identity), its
refusals without mutation, costume_slot_index and the message GUID bytes.

## Nested mod-manager menus (modular mods)

After `build.bat`, run `python tests/nested_modular.py [--old-loader <previous exe>]`.
Synthetic fixture games (20 reserved slots) get mod-manager modular mods: a ZIP whose
options hang from sub-groups of DummyMod menu folders ("02 Hair" > "Long Hair" >
option), with an "Optional accessories." group and a root name that sorts after
every group; a 5 x 10 and a 3 x 17 combination mod (limit 50); the old flat
"1) Main files / 2) skin / 3) outfit" layout without menu folders; a single menu
level like the A.K.I bundle; and the nested ZIP in a game folder whose path is
about 175 characters long. The checks read the slot meshes of the output PAK:
one costume per hair x outfit option named after the mod, main head kept, hair
and body from their options, the optional group left out, 50 combinations made
and 51 reduced to the largest group, flat and single-level outfits as before,
and the same outfits from the long path. With `--old-loader`, the flat and
single-level cases must give the previous build's outfits, slots and PAK entries,
while the nested mod differs and the long-path archive is skipped.

## Patch PAK row

After `build.bat`, run `python tests/patch_row.py [--old-loader <previous exe>]`.
The actual loader runs on the synthetic coexistence fixture while synthetic mod PAKs
and stage-slot PAKs (real and marker-only) are added and removed around its own
PAK: a mod PAK above it, a second one, each removed again, a stage PAK above it,
the outfit and then the whole costume_mods folder removed while a mod PAK sits
above. After every launch the row of `re_chunk_000.pak.patch_NNN.pak` must be
unbroken from 001, the real costume PAK must sit above every mod and stage PAK,
placeholders (the marker alone) may only fill numbers below a PAK that stays, and
outfits.json must name the costume PAK. The previous build must leave a hole
once a mod PAK sits above its PAK. Fixtures stay under `tests/.runs`.

## New official costumes (October 13: C. Viper costume 3, Arjun)

`tests/new_costumes_data.py --metadata <loader dir> --base-pak <re_chunk_000.pak>
--paktool build/paktool.exe --refresh build/costume_table_refresh.exe --output <new dir>`
checks a loader data folder against the current game, read-only: index rows of
C. Viper costume 3 (esf030/004) and fighter 34 costumes 0/1/100, every row hash,
fighter 34 reserved records 8100..8199 with no id or costume overlap with the
official table (137/143/147 included), the static table's native rebase, and the
static ver_02_0500 scene (vanilla folders kept first, esf034v05..v104 added).

`tests/new_costumes_real_input.py --loader build/costume_loader.exe --paktool
build/paktool.exe --metadata <loader dir> --base-pak <re_chunk_000.pak> --output
<new dir> [--old-loader <exe> --old-metadata <dir>]` extracts the vanilla meshes
and materials of those three costumes as manual outfits and runs the loader in
fixture game folders: recognition, slot numbers and folders, kept official
records, the ver_02_0500 scene in the output PAK, fallback scenes for every
reserved number, no original asset collision, cached repeat and unchanged inputs.
With the previous build and data, no slot is made from these outfits.

`costume_table_refresh.exe` adds fighter 34 rebase/trim checks and rejects
reserved records on official fighter 34 costume 1, C. Viper costume 3, or id 143.
The coexistence fixture covers the sixth structural file (ver_02_0500), so a build
without its exemption fails "all six exact global structural replacements".
Its metadata reserves a single slot, so since 1.9.7 the loader logs that it takes
the shipped copies ("slot range is not 100 slots") and that check still applies.

`game_update_real_input.py` assumes static metadata that predates the new
official records; with 1.9.5+ metadata its "previous build loses new vanilla
records" check cannot apply and reports false (the other seven still apply).

## Game update cache invalidation

After `build.bat`, run `python tests/game_update_cache.py`. With
`--old-loader path/to/previous/costume_loader.exe`, the generated fixture also
reproduces the previous build's stale cache reuse after each changed game input.
It covers the game executable's date and header, base PAK size and index
checksums, inventory content, static bytes and metadata mappings (including
same-size changes with preserved dates), and same-size DLC replacement.
Unchanged launches and the mod manager's base-PAK path-hash invalidation/restoration must
still reuse the cache. It verifies changed output bytes, original-asset
protection, stable slot assignments and preserved manual sources.

The base identity reads the complete v4 index with path hashes normalized to
zero, plus the header and file size. It deliberately ignores base PAK mtime and
does not hash its payload: same-size payload-only edits with unchanged index
checksums require an EXE identity change to be detected. All fixture inputs and
reports stay under `tests/.runs`; this is not an SF6 rendering test.

## Physics reference relocation

After `build.bat`, run `python tests/chain_relocation.py`. The native fixture
checks supplied and fallback physics USER files, repeated scene/USER references,
USER relays and direct chain references against actual emitted private PAK keys.
It covers Havok compound suffixes, case/backslash variants, exact external
weather-chain filenames and a chain-only model-parts relay. A wrong-version
private chain must not redirect the engine away from its current-version original.
It retains semantic source-costume filename stems, keeps references to unprovided
vanilla chains unchanged, preserves physics payloads and sources, and checks
cached reuse. Supply `--old-loader path/to/previous/costume_loader.exe` to reproduce
the mismatch and verify that the new build regenerates its cached references.
All generated files stay under `tests/.runs`; no game physics are simulated.

Run after a native `build.bat` build on Windows:

```powershell
python tests/fluffy_coexistence.py
```

The harness executes the actual `costume_loader.exe` in a new directory under
`tests/.runs`. Its base PAK, inventory, loader metadata, manual outfit, and loose
mod-manager files are entirely synthetic. It neither reads nor changes an installed
game, a mod-manager library, or any user outfit. Every fixture run and its JSON report
is retained for inspection.

The fixture exercises slot assignment, relocated mesh and streaming texture
output, retention of the five exact production structural paths, texture
row-pitch repair, original-path protection, an indexed vanilla
file absent from the base PAK, retained mod-only dependencies, cached launches
after loose-file installation/replacement/uninstallation, unchanged source
assets, and generated-PAK cleanup when the manual outfit is removed.
The lifecycle stages zero both source hash fields in the synthetic base PAK
on install/replacement and restore them on uninstall, matching an external mod
manager's PAK invalidation. Resolution uses patch PAK, base PAK, then loose natives to model
Loose File Loader being off.
Since 1.9.8 a loose mod-manager colour whose PAK entry is missing goes into the
manual outfit's private slot (upstream's fallback; 1.9.4-1.9.7 checked that it
never did and that the packaged colour stood in), never under its original
path, and its log line names the file. It also checks that the missing-texture repair branch
cannot publish an indexed original path missing from the base PAK.
Original-asset protection also covers base-only and DLC-only keys absent from
the inventory, and an unavailable streaming twin inferred from an indexed TEX.

Minimal asset headers contain the fields the loader validates; they are not
renderable game assets. These checks establish package and cache behavior only.
They do not claim a successful launch, rendered outfit, or online match in SF6.

To validate a one-time upgrade rebuild against an earlier loader binary:

```powershell
python tests/fluffy_coexistence.py --old-loader path/to/previous/costume_loader.exe
```

The previous build must reproduce the original-path masking regression. The
current build must regenerate its cached output without modifying manual sources.

`real_input_coexistence.py` performs a separate comparison against a supplied
Leon ZIP and copies of the game's loader metadata. It accepts `--old-loader`,
`--loader`, `--source-game`, `--source-zip` and a new `--output` directory under
the dated workspace output. The actual base PAK is used through the CLI's
read-only `--base-pak` override; all generated files stay in the fixture trees.
The driver compares kept outfit identities and original-asset hash collisions,
checks the five global structural replacements, and records source-preservation
evidence. Large installed DLC archives are omitted and listed in the report.
This is still package validation, not an in-game outfit test.

## Native PAK writer exclusions

After `build.bat`, run `tests/build_pak_exclusions.bat`, then:

```powershell
build/pak_exclusions.exe tests/.runs/new-writer-fixture.pak
```

Choose a new output path for each run. The test uses the real writer, reader and
decompression code to check exclusions before indexing/deduplication, compressed
and stored aliases, replacements, configure-before-writing, custom dependencies,
and the serialized roundtrip.
