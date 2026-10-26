# Unreleased

- A mod whose options are whole-outfit alternatives of its main files (each with its own
  copy of the same body mesh and "AddonFor" naming the main) gave only the main files once
  it had more than three options ("too many to combine"). Each option is now a variant,
  however many there are: Ingrid C1 Barefoot Stockings v2 gives its main and five
  "main + option" outfits. Add-ons that don't all replace the same files keep the limit
  of three. Needs a new build ID, or an outfit PAK built before stays as it was.

# 1.10.4 (2026-10-18)

Package `SF6_Slotsloader_Fork_v1.10.4.zip`, build ID 2026-10-18-1.10.4-slotsloader-fork
(the first launch rebuilds the outfit PAK once).

- Character-swap repairs, off unless Slotsloader Manager's Settings → **Try to fix
  character-swap mods broken by game updates** is on (an empty `swap_repairs.txt` in
  `reframework\data\SF6_Costumes_Data`). On: a swap scene from before the Alex update
  gets the current timeline type signature, and six known Mai C1, Elena C1 and Mai own-slot
  chain files are rebuilt from the current game file, only when both the old and the game
  file match verified fingerprints. Off, outfits are built exactly as in 1.10.3. Turning
  it on or off rebuilds the outfit PAK at the next launch.
- An archive whose extracted folder was briefly held by a virus scanner was skipped
  ("cache folder could not be finalised (error 5)"); the rename is now retried for up to 2 s.

# 1.10.3 (2026-10-17)

Package `SF6_Slotsloader_Fork_v1.10.3.zip`, build ID 2026-10-17-1.10.3-slotsloader-fork
(the first launch rebuilds the outfit PAK once).

- An extra outfit whose own scene puts it on another fighter's body is built from the
  mod's scene, as its Replace-original install is. Sakura as Ryu points Ryu's Outfit 1
  at Juri's costume prefab, body shape and joint constraints while keeping Ryu's number
  of model parts, so 1.10.1 still took it over part by part: the slot kept Ryu's prefab
  and shape, and Sakura came out on Ryu's skeleton.

# 1.10.1 (2026-10-17)

Slotsloader Fork: the loader Slotsloader Manager installs. The package is
`SF6_Slotsloader_Fork_v1.10.1.zip`; its mod-manager entry is named Slotsloader Fork
(by ZZtaii, Wael3rd). The build ID (2026-10-17-1.10.1-slotsloader-fork) makes the first
launch rebuild the outfit PAK once. With another mod manager, disable any older
Slotloader, Slotsloader or SF6 Slots Loader entry first.

- A missing number in the patch PAK row (left by another tool, an older loader or a
  mod manager) hides every PAK after it and can stop the game at a black screen.
  Each gap now gets a small placeholder PAK.
- A patch PAK that can't be read (locked or in use) still counts as another tool's:
  the outfit PAK never goes below it or deletes itself under it.
- A file whose PAK entry a Replace-original install switched off (PAK method) is read
  from the game folder's loose copy, so outfits that share such a texture are no
  longer left out as "texture missing".
- An extra outfit whose own scene lists a different number of model parts than the
  game's (SF5 Sakura over Ryu's Outfit 1) is built from the mod's scene. Before, it
  got the game's scene and lost its own hair and cloth physics, unless a Replace-original
  install of that outfit happened to switch the game's scene off.

# 1.10.0-custom-variant-choices (2026-10-15, local)

Variants of a mod can be turned off, so a dropped archive no longer has to give
every outfit it can make. Build ID 2026-10-15-1.10.0-custom-variant-choices.

- `reframework\data\SF6_Costumes_Data\variant_choices.json` (written by
  SlotsLoader Manager, may be absent):
  `{ "version": 1, "archives": { "<source>": { "<label>": true|false } } }`.
  `<source>` and `<label>` are outfits.json's `source` and `label`. Every
  variant is on unless its entry is `false`; unknown sources and labels are
  ignored. A file that cannot be read turns nothing off and logs one WARN; the
  loader never rewrites it.
- New outfits are listed last: a mod or variant seen for the first time (or
  turned back on) takes the number after the highest one in use for its
  fighter, not the gap a removed mod left. Gaps are filled only once the
  fighter's last reserved number is taken. Outfits already installed keep
  their numbers.
- A variant turned off is not built and keeps no registry record (its outfit
  number is free); the other variants keep their numbers. Its files stay
  out of the PAK unless a variant that stays on uses them. The log says
  `variants off (choice): N of M for <source>`.
- The variants turned off are part of the rebuild fingerprint: changing them
  rebuilds the PAK at the next launch. A file rewritten with the same choices,
  or one that only adds `true` entries, does not.
- `variants.json` (same folder), written at every scan:
  `{ "generated", "loader", "archives": [ { "source", "fighter_dir",
  "variants": [ { "label", "enabled", "slot" } ] } ] }`: every variant of
  every mod, on and off, in the loader's order, with the outfit it became
  ("Outfit V") or null. Written to a temporary file, then renamed. When the
  PAK is up to date but variants.json is missing, the mods are scanned again
  to write it.

# 1.9.9-custom-luke-variants (2026-10-15, local)

Four Leon-on-Luke mods (C1 and C3, each a main option plus hair/muscle add-ons)
gave 1 slot of 10 in the game. Build ID 2026-10-15-1.9.9-custom-luke-variants.

- The slot check reads the textures a material file binds (each material's
  texture table), not every `.tex` string of the file. Material files can keep
  strings no material uses: these mods keep 30 paths of a "LeonPort" folder
  they never shipped. The check took them for missing textures, and either
  rebuilt the part from the original outfit (the slot showed Luke's own body or
  hair: 1.9.2-1.9.8) or, when that failed, left the slot out. A file whose
  layout cannot be read is checked as before.
- When the game's scene of the original outfit cannot be read (its PAK entry
  turned off, as an external mod manager does when it installs a replacement that
  ships that scene), the slot is built from the mod's own scene of that outfit
  instead of being left out ("slot scene missing").
- A mod's scene that moves a part (the hair add-ons move the hair from 000/02/
  to 001/02/) now also moves that part's physics USER reference (the hair's
  `chain.user`) when the mod ships it: the mod's hair had the game's hair chain.
- Add-ons whose AddonFor names nothing of their mod (no option, no menu entry),
  beside a single complete option with no AddonFor, are add-ons of that option
  (one outfit per add-on) instead of a modular mod that left them out as
  optional. Each is named in a WARN: mod managers do not link them either, and
  the mod should be fixed.
- Tests only: `SF6_COSTUME_TEST_TURNED_OFF=<file of paths>` makes the loader
  read those game files as turned off in the PAKs.

# 1.9.8-custom-loose-colours (2026-10-15)

- Colour files from the game folder again, as upstream does. When an outfit's
  colour list names a colour file that the outfit does not supply and the
  game's PAKs no longer have (an external mod manager, or SlotsLoader Manager's PAK
  method, zeroed its entry in re_chunk_000.pak and installed the replacement as
  a loose file in the game's `natives` folder), the slot takes that loose file.
  Only without one does another colour of the outfit stand in (the first
  numbered one, cmd_000 last), as in 1.9.4-1.9.7; a stand-in that is itself
  only a loose file is taken from the game folder too. 1.9.4 had removed this
  (stand-in only).
  - A colour whose PAK entry is still there keeps the game's original, loose
    file or not: with REFramework's loose file loader the entries stay, so
    those slots are unchanged.
  - The copy goes to the slot's own path only. The original-asset exclusions
    and "never restore vanilla over originals" from 1.9.4 are unchanged.
  - Each colour taken from the game folder is logged with its file:
    `check: colour file <pak path> is not in the game's paks: taken from the
    game folder (a loose file, as an external mod manager installs them): <file>`.
  - Long-path aware (wide calls with the `\\?\` prefix past 240 characters).
- The cache follows those colour files. The pak build records, for every
  colour a slot took from the game instead of its outfit, whether
  re_chunk_000.pak has it, only a DLC pak has it, it is a loose file (size,
  date and content hash) or it is nowhere, in
  `reframework\data\SF6_Costumes_Data\game_colours.tsv`; the saved fingerprint
  ends with their digest. At launch they are checked again, so the PAK is
  rebuilt when such an entry is zeroed or restored, or such a loose file
  appears, changes or goes away (the base-PAK fingerprint ignores zeroed path
  hashes on purpose, so this was not covered). Other zeroed entries still reuse
  the cache. The log names how many colour files are watched.
- Native plugin: the switch file of 1.9.7 that turned the owned-outfit check
  off is no longer read. A private maintainer opt-out replaces it (not shipped,
  not documented); with it the log says `costumes: ownership filter inactive`.
  The `hidden:` lines are unchanged. New unit test plugin/tests/marker_test.cpp.
- Joint constraints (`.jcns`) a mod ships are used by its slot, as an external mod manager
  makes the outfit use them (it replaces the game's file in place). The scene
  names the outfit's `<fd>_<folder>_<part>_jcs.user` files, which name the
  `.jcns`. When the mod ships a `.jcns` that such a jcs.user names, the slot
  gets its own copy of that jcs.user (in the slot folder, same name) pointing
  at the slot's copy of the `.jcns`, and the scene points at that copy. A
  jcs.user the mod ships itself is the slot's, repointed the same way.
  Constraint files the mod does not ship stay the game's, and nothing is
  written under an original path. Log:
  `joint constraints: the mod's .jcns, through the slot's own copy of
  <name>_jcs.user (N references)`. Before, the slot drove the mod's mesh
  with the game's constraints and shipped the mod's `.jcns` unused (A.K.I. C2
  and C3 bare-legs mods with added C4 toe-helper bones).
- WARN when a joint constraint or physics file of the mod (`.jcns`, `.chain`,
  `.havokcloth`, `_jcs/_chain/_shape/_aogeo/_havok.user`) is referenced by
  none of the slot's scene, model parts or physics USER files: `WARN: slot
  <fd>/vNN (<outfit>, from <mod>): the mod's <path> is referenced by none of
  the slot's scene, model parts or physics files (kept, unused)`. The slot is
  kept.
- An option whose `AddonFor` (mod-manager metadata) names another option of the same mod
  (its modinfo name, case-insensitive, trimmed) and that has its own mesh is
  an add-on of that option, as mod managers install it: the mod gives the option
  alone and `<option> + <add-on>` (both options' files, the add-on's winning
  where both have one), not the add-on alone without the option's files.
  A.K.I. C2 Bare Legs/Barefoot Bundles: "Ribbon" was an outfit of its own
  without the main's `.jcns` and foot textures; now "A.K.I. C2 - Bare Legs +
  Ribbon" and "A.K.I. C2 - Barefoot + Ribbon". Log: `<add-on>: add-on of
  <option> (its addonfor), made with it`. Nested mod-manager menus, the 50
  combination cap and add-ons whose AddonFor names no option are unchanged
  (nested_modular PAKs byte-identical). Slot numbers of such bundles may move
  once.
- New build ID `2026-10-15-1.9.8-custom-loose-colours-addons` (it was
  `2026-10-15-1.9.8-custom-loose-colours-jcns`): the first launch
  rebuilds the costume PAK once. Plugin log string 1.9.8.
- Tests: new `tests/option_addons.py` (main + an add-on naming it, an
  unmatched AddonFor); new `tests/joint_constraints.py` (a mod `.jcns` at the game's path,
  a mod with its own jcs.user, an unreferenced `.jcns` WARN, and a mod without
  `.jcns` byte-identical to the previous build); new `tests/loose_colours.py` (zeroed entry with and without a loose
  colour, entry present with a loose file, cache cases, a 341-character loose
  path); `fluffy_coexistence.py` and `game_update_cache.py` expectations
  updated to the new fallback order.

# 1.9.7-custom-game-derived (2026-10-14)

- Loader data from the game's own files (port of upstream 1.11.0, by behaviour).
  A game update that adds a fighter or an outfit no longer needs a regenerated
  vanilla_costume_index.tsv or static files:
  - At each PAK build the six structural files (root fighter scene, version
    scenes, costume table, costume names) are built from the game's current
    files with the rules of static_slots.py: v00 template in the root scene;
    in each version scene from ver_02_0300 on (probed up to ver_09_9999), the
    fighters it brings first get folders from their v04, else their highest
    vNN; 100 records per fighter, ids 5000 + slot block * 100 (Arjun 8100..,
    a fighter 35 would get 8200..; the native plugin's stale-ownership sweep
    now covers 5000..8999). For today's game
    the six files are byte-identical to the shipped 1.9.6 copies and the slot
    records equal static_meta.json.
  - They are used only when they declare at least every shipped slot record
    (same id) and every shipped file; otherwise, or when a game file cannot be
    read or parsed, the shipped copies are used as before and the reason is
    logged (`WARN: structural files taken from the shipped copies (...)`). The
    table still goes through the current-game rebase and the trim; derived
    version scenes are the only names added to the original-asset exemption.
  - Costumes of the game missing from the shipped index are inventoried from
    the base and DLC PAKs (`vanilla index completed from the game: ...`), so
    mods made for them are recognised. With the 1.9.5 data on today's game this
    recreates C. Viper costume 3 and Arjun, and the generated PAK (base PAK,
    DLC omitted) is byte-identical to the one made with the regenerated 1.9.6
    data.
  - A fighter the shipped folder list does not have (matched by fighter id)
    gets a `costume_mods\<Name>` folder named from the game's English name.
  - A costume without a reserved record (a fighter the shipped tables do not
    know, when the files could not be built from the game) is no longer given
    a slot with record 0; it is logged and skipped.
- The cache identity is unchanged: the game inputs (EXE, base PAK index, DLC)
  already cover everything read from the game, and the external mod manager's reversible hash
  invalidation still reuses the cache. New build ID: the PAK is rebuilt once.
- Native plugin: a slot made from a fighter's Outfit 2, 3 or 4 shows only when
  the game lists that outfit as owned (default, bought, DLC the game accepts,
  unlock scripts included); Outfit 1 and DriveTech slots always show. Checked
  offline and kept until the game closes; nothing changes during an online
  session. Hidden slots stay in the PAK (replays and saved settings still load)
  and get one `hidden:` line in native_log.txt. Characters without DriveTech
  (Arjun) keep the 1.9.6 handling. Port of upstream 1.11.0. A technical
  opt-out is described in docs/OWNED-OUTFITS.md.
- Not ported: mod-manager discovery, the Color Mod, SlotsClean, the ArchiveList
  warning and grouped add-ons.
- New tests: tests/game_derived_real_input.py (current game, read-only),
  tests/game_derived_fixture.py (synthetic game update with a new fighter,
  fallbacks), build/game_derived.exe, eight more costume table checks.
- No more hole in the patch PAK row (port of upstream 1.11.0). The game reads
  `re_chunk_000.pak.patch_NNN.pak` as an unbroken row from patch_001. When a mod
  PAK was added above the costume PAK, the loader deleted its old PAK and wrote
  the new one above the mod's, leaving a hole (black screen). The old PAK is now
  replaced in place by a marker-only placeholder when a mod or stage PAK sits
  above it, and the placeholder is deleted once nothing is above it. Placeholders
  are never taken for the real PAK (up-to-date check, move, stage PAK number); the
  move path leaves one where the PAK came from. The audit script already ignores
  game-folder PAKs. New test: tests/patch_row.py.
- Mod-manager modular mods with nested menu groups are made as the mod manager installs them
  by default. The menu folders that only hold a modinfo.ini (DummyMod) are read,
  and each option's AddonFor is followed up to its group right under the mod's
  root: "03 Outfit" > "Lower Neckline" > "Shoes" is one choice among every
  outfit of "03 Outfit". The main files are merged first, then the groups in
  their folder order (later ones win; before, the alphabetical AddonFor order
  could let the main files overwrite a hair or neckline option); a group whose
  name or description says "optional" is left out (the mod manager installs nothing there
  unless ticked); costumes are named after the mod ("Grace Noir as C. Viper
  Bundle - Grace Hair - Pale Skin - Lower Neckline - Shoes"). The Grace Noir
  bundle gave one outfit; it now gives 32 (4 hair and skin x 8 outfits), each
  with the head of the main files (monocle kept). Mods with one menu level (the
  A.K.I bare legs bundle) or none keep their outfits, names and slots; only
  their main files are merged first when the menu names them. The combination
  limit of one modular mod goes from 24 to 50 (half a character's 100 slots);
  above it only the largest group still varies, the others keep their first
  option.
- Archives unpacked under a long game path: the options of an archive are
  listed with the wide API and the `\\?\` prefix, so options past MAX_PATH are
  found (the whole archive was skipped as "no natives folder, .pak or usable
  archive found" under a game path of about 175 characters). New build ID: the
  PAK is rebuilt once. New test: tests/nested_modular.py.
- Folder mods with paths past MAX_PATH: the cache fingerprint walks them with the
  wide API too. Their deeper files were missing from it, so a changed file there
  kept an outdated costume PAK ("Up to date"), and each entry went through a
  fixed 512-byte buffer that a long enough path overflowed. The fingerprint of
  shorter paths is unchanged.

# 1.9.6-custom-new-costumes (2026-10-13)

- Vanilla index regenerated from the current game with the original
  build_vanilla_inventory.py pipeline (REasy, base + DLC PAKs, read-only) and the
  new inventory_to_tsv.py converter. Adds C. Viper costume 3 (folder esf030/004)
  and fighter 34 (Arjun) costumes 0, 1 (esf034/001, 002) and 100. Mods for those
  outfits are now recognised. The 126 unchanged costumes are byte-identical to
  the 1.9.5 index; 46 others gain the colour files the update added (new
  `cmd_dx_008` / `cmd_ex_*` USER files, emote override changes).
- Fighter 34 gets 100 reserved extra slots (records 8100..8199, costumes 5..104)
  in the static costume table and a static copy of
  `flow/version_update/ver_02_0500/esf.scn.20`, which keeps its seven vanilla
  folders (Arjun's outfits and C. Viper's costume 3) and adds esf034v05..v104.
  The new scene is exempt from original-asset protection like the other
  structural files. `costume_mods/Arjun` is created and packaged.
- New standard-library static_slots.py builds the static files; its `verify`
  command reproduces every 1.9.5 static file byte for byte from the current
  vanilla files before anything is extended.
- Native plugin: a character with slots and no DriveTech record (Arjun) no longer
  blocks boot completion or the online alias of the other characters. Its slots
  stay offline outfits; Battle Settings and the save keep Outfit 1 for it.
- New tests: tests/new_costumes_data.py (shipped data against the current game)
  and tests/new_costumes_real_input.py (loader run on outfits copied from the new
  vanilla costumes), five more native costume-table checks, and the
  coexistence fixture now covers the sixth structural file.

# 1.9.5-custom-game-update (2026-10-13)

- Rebase the reserved extra-slot table on the current game's official costume
  records before trimming unused slots. The October 13 game has three records
  missing from the earlier snapshot, including C. Viper's fourth costume.
- Reject incompatible table layouts or colliding generated records instead of
  publishing every reserved costume record after a failed table reduction.
- Rebuild cached output when the game executable, base PAK index, DLC identity,
  inventory or consumed static metadata changes. Preserve cache reuse across
  the external mod manager's reversible PAK path-hash invalidation.
- Retain the private chain/cloth reference repair and mod-manager coexistence fixes.
- The genuine AMD library and its forwarding API are unchanged.

This is a compatibility candidate. Native package tests do not establish that
the reported in-battle rendering problem is fully resolved; game testing is pending.

# 1.9.4-custom-coexistence (2026-10-11)

- Stop restoring original costume assets into the generated patch PAK. Manual
  sources already have their own extra slots and do not replace the originals.
- Reserve original hashes from the costume inventory and available base/DLC
  indexes, including indexed streaming twins. All writer/repair routes obey
  this protection; only the five required global slot structures are exempt.
- Keep genuinely new unrelocated dependencies and their streaming twins.
- Stop importing unrelated loose mod-manager colors during missing-color fallback.
- Change the build ID so old cached costume PAKs rebuild once.
- Add native synthetic install/uninstall/cache and PAK exclusion regressions.

Built with MSVC and exercised outside SF6. Game runtime validation is pending.
Partial extra outfits can still reference public assets; full asset isolation
and recovery of invalidated base assets are outside this change.

# 1.9.4-custom (2026-10-10)

- Restrict extra costume-slot creation to costume_mods.
- Remove mod-manager discovery and installed.ini/archive integration.
- Stop automatic slots from game-folder PAKs and loose natives.
- Keep patch PAKs as dependency sources and preserve stage handling.
- Force cache rebuild and update audit instructions.
- Retain the other 1.9.3 fixes. Cross-compiled; in-game testing pending.

# Changelog

## 1.9.3 beta (2026-10-04)

A beta: the changes below are tested on one installation only.

### Added
- **Each costume mod installed with an external mod manager gets a slot of its own**, even two mods of the same
  outfit. The mod manager copies the mods it installs into the game, so two mods of one outfit ended up as one; mods
  installed as loose files (with REFramework's loose file loader on) got no slot at all. The loader now
  finds the mod manager, reads its list of installed mods and takes each one from its archive in the mod manager's
  `Games\SF6\Mods` folder. Unticking a mod removes its slot at the next launch. If the mod manager is not found,
  its folder can be given in `reframework\costume_mods\settings.ini`. The outfit a mod-manager mod replaces
  still shows that mod. See [docs/MOD-MANAGERS.md](docs/MOD-MANAGERS.md).

### Fixed
- **A slot no longer stalls the select screen, or shows white colours, when the game's pak lacks a colour
  file of its outfit** (seen on an installation whose pak had no Mai Outfit 1 colours 2 to 5, 7, 8 and 10).
  The loader takes such a file from the game folder when it is there (where an external mod manager puts the
  files it installs), otherwise gives the slot another colour of the outfit.
- **A mod's texture in the shared body folder keeps its own high-resolution version.** The slot got the
  mod's texture with the game's high-resolution one, and the game waited for ever when the outfit loaded.

### Changed
- The slot check reports cloth that cannot move: a mod made for an outfit with cloth simulation on an
  outfit without, and a cloth simulation that uses another character's file (`WARN` lines in
  `SF6_CostumeLoader.log`).
- Mods with exactly the same files and sizes (variants of one design) are told apart in the log.
- `SF6_CostumeAudit.bat` lists the mods ticked in the external mod manager with the slots made from them.

## 1.9.2 (2026-10-04)

### Fixed
- **A slot shows its colours and its cloth physics in Battle Settings.** Opened from the main menu, Battle
  Settings showed a slot white and with its cloth frozen: the slot's colour list and cloth settings were
  loaded too late for that screen. A slot's scene now loads them with the scene, like the game's own outfits.
- **Slots made from Outfit 1 of JP, Dhalsim, Lily or Guile are no longer white** (since 1.9.0). The DLC paks
  hold an older colour list for these four outfits, which the game cannot read; the slots were built from it.
  The DLC paks now only complete the base pak.
- **Dhalsim's Outfit 4 keeps its own body when Dhalsim has two slots or more.** That outfit takes a part from
  a model folder the loader believed free; a slot was placed there and replaced it. Every folder the game uses
  is now taken, and a slot already placed in one moves to a free folder (it keeps its number and its name).
- **A mod's joint constraints, shapes and AO geometry are used by its slot** (`_jcs.user`, `_shape.user`,
  `_AOgeo.user`, and the `.jcns` files a scene names): the slot kept the original outfit's.
- **The online alias dresses your own fighter only.** An opponent playing, in DriveTech, a character you had
  chosen a slot for was shown with your slot. Nothing is aliased any more while Battle Settings is open
  either (previewing DriveTech there mounted the slot over it).
- **Starting the game twice in a row no longer breaks the paks.** The first launch after a change of mods
  rebuilds the costume pak for about a minute without a window; a second launch during that time numbered
  the paks 003 and 004 with no 001, which the game does not read (black screen). The second one now waits.
- **A stage pak that no longer matches its table is rebuilt** instead of being trusted (a selected stage
  variant never finished loading).

### Changed
- The slot check no longer warns about a colour list a mod ships itself, and reports a material file that has
  materials its mesh lacks (`WARN` lines in `SF6_CostumeLoader.log`).
- Mod archives unpacked under very long paths are found.

The costume pak and the stage pak are rebuilt once at the first launch (about a minute: launch the game once
and wait).

## 1.9.1 (2026-10-02)

### Fixed
- **A Training Room variant of Stage Lighting Overhaul no longer hangs the game on its way to Fighting
  Ground.** Its stage pictures (the preview, 1920x480 with 9 levels) declare a wrong row size for their three
  smallest levels; the game waits forever for such a texture, and the Fighting Ground menu loads Training
  Room's pictures. The stage loader now checks every texture of a stage mod and repairs its level table the
  way the costume loader repairs costume textures; a texture it cannot repair is left out of the variant (the
  game's own is used). The stage pak is rebuilt once at the first launch.

## 1.9.0 (2026-09-30)

### Changed
- **No more Lua scripts.** Everything in game is done by the native plugin `SF6_CostumeSlotsNative.dll`: the
  costume menus, ownership, colours and select screen data, the online alias, the stage select screen and the
  VS screen. It is driven by the game's own events (a menu shown or closed, an outfit folder mounted, the stage
  select screen shown, UP / DOWN) instead of looking at the game every frame: during a fight it does nothing.
  An outfit mount is seen when the game creates the outfit's visual manifest; the online alias then checks
  for two seconds, during the loading screen, and stops. Played online on 2026-09-30.
  An update made by hand leaves the old scripts in `reframework/autorun/`: the plugin renames them to
  `.lua.old`.
- The loader's stage watcher sleeps until the stage choice file changes (a change notification) instead of
  looking at it four times a second, and the stage redirection no longer counts the files it sees.

### Added
- **Stage slots (experimental).** Stage mods dropped as downloaded in `reframework/stage_mods/` (`.zip`,
  `.7z`, `.rar`, a folder with a `natives` tree, a `.pak`) become variants of the stage they change. On the
  stage select screen of Fighting Ground > Versus, UP / DOWN cycles a stage through its original look and
  each mod; the name and a preview made from the mod's screenshot follow, the VS screen shows the choice, the
  battle loads it, and the choice is kept per stage. A pack covering many stages gives one variant per stage
  and option. The mod files go into a patch pak of their own, below the costume pak, and are served in place
  of the original files only while selected, through one hook on the engine's path hashing that is installed
  only once a variant is selected. The choice is local and applies to every load of the stage, online too;
  online play has not been tested. Details and limits: [stages/README.md](stages/README.md).
- `SF6_CostumeLoader.log` names every file of a mod that its slot does not show
  (`WARN: the slot loads ... from the game`): what to send when a part of a mod is missing.

### Repository
- **Renamed SF6-SlotsLoader** (was SF6-CostumeSlotsLoader; the old links lead here). It now also holds the
  stage slots (`stages/`): on the stage select screen, UP / DOWN cycles a stage through its mods. They ship in
  the same `amd_ags_x64.dll` (experimental, see *Added*). Layout: `costumes/`,
  `stages/`, `common/` (pak and archive code, third party), `proxy/`, one `build.bat` at the root that builds
  everything into `build/`, including the costume loader alone (`build/costumes_only/`) for the releases.

### Fixed
- **The stage slots' pak is not taken for a mod pak.** It sits right below the costume pak; adding or removing
  a stage mod rebuilt the costume pak (about 50 s with many mods). It is now recognised by its marker and left
  out, the costume pak stays above it, and when only its presence changed the costume pak is moved instead of
  rebuilt. The first launch after updating rebuilds the costumes once.
- `outfits.json` (read by the audit) follows the costume pak when it changes number.
- **A mod's own Havok cloth is used by its slot.** A slot kept the game's cloth file of the original outfit on
  the mod's body (Lily Kenyan Summer C2); it now gets its own copy pointing at the mod's cloth, as for chain
  physics. The model folder's havok references are no longer renamed into files that exist nowhere.
- **Weapons show on the character select screen.** The slot's weapon list was loaded late: the first player to
  pick the outfit had no weapons there (Lily Kenyan Summer C2). Both of the scene's references now point at the
  slot's list.
- **Parts a mod retextures without their material are shown.** A material of the outfit that the mod does not
  ship, but that uses textures the mod ships, kept the game's textures: C. Viper Lace lingerie C2's head,
  the hood of Lily Haruka Hoodie (her hair material uses the body's cloth), Oni Juri's eyes (shared `000/`
  folder). The slot now gets its own copy of that material, bound to the mod's textures. The mod's files stay
  out of the original outfit, which stays as the game made it.
- **A mod's wind settings are used by its slot** (`weather/wind/.../*_chain_BattleSetting.chain`: Swimsuit
  C. Viper, C. Viper Concept Outfit). The scene now follows every file the slot has under its own name.
- **The DLC paks are read.** The game reads them over `re_chunk_000.pak`, for the players who own them: they
  hold newer colour files of the Outfit 1 of JP, Dhalsim, Lily and Guile. The slots were built from the older
  ones; they are now built from what the game reads, and buying or removing a DLC rebuilds them.

## 1.8.1 (2026-09-30)

Found by testing with a large set of Cammy, C. Viper and Ingrid mods installed together.

### Fixed
- **A slot no longer takes files from another mod.** The colour files and the colour-variation data a mod does
  not ship, and the high-resolution (streaming) version of a texture, were looked up among the files of all
  installed mods. Several mods ship textures of the same name for the same outfit (five for Cammy's Outfit 1),
  so an outfit got the colours or the pictures of another one, and which one depended on the other mods
  installed: Escape from Shadaloo lost its green, Alt Bare Legs its jacket and glove colours. A slot now uses
  its own mod's files, else the game's, as when the mod is installed alone.
- **Texture references written with doubled slashes (`002/01//Knit_CMASK.tex`) or backslashes
  (`product\model\...`)** now follow the texture to its slot. Before, the outfit waited forever for a texture
  that had moved (Camo Cammy showed the original outfit, then froze; C. Viper Trench Coat froze the select
  screen).
- **Files placed directly in `product/model/`** (Dance Outfit for Cammy names a `1.tex` there) are taken from
  folder mods; the game keeps none there, so nothing can be overridden.
- **Material files made for an older layout** are rebuilt on the game's file for that part: when every
  material has the game's name and master material but exactly the parameters the game had minus those it
  gained since, the game's file is used, with the mod's texture bindings, rendering flags and parameter
  values (Dance Outfit for Cammy showed a dark body and opaque fabric; it shows the sheer dancer). Measured on
  41 material files of a large collection: only that one matches.
- **A part hidden on purpose stays hidden.** A mod that hides a face or hair with a reduced mesh naming a
  material no file defines (AoD replace Ingrid) no longer gets the original part back, and such an outfit is
  no longer left out.
- **A partial add-on brings its parts to a costume put in the same sub-folder**, even when it was made for
  another outfit's folder (BellyHair for Dance Outfit: hair in Outfit 1's folder, costume of Outfit 2). Only the
  combined outfit is made when both are in one sub-folder of the character; archive bundles keep the base and
  each variant. Note: Ken SFV with Ken SFV Hair in one sub-folder now gives only the combined outfit.

### Script
- The script does nothing while a fight is running (match, replay, training outside the pause) when the
  native plugin is there. The signal is the fight clock (`gBattle.Game.stage_timer`) moving; it stops in
  pause, menus and loading, and the checks come back at once. Before, the save, the costume ownership and
  the colour clean-up were polled every frame, about 3 fps on a CPU-bound laptop during a replay; now 8 us
  per frame. Never while the Battle Settings menu is open (its closing must be handled), and without the
  native plugin the script keeps its in-match role.

## 1.8.0 (2026-09-29)

- A part the mod made for an older layout of the game is taken over when the outfit's scene takes that part
  from another costume's folder (Aria for Ingrid: face and hair). The slot gets the mod's part with the
  original material file of the place it replaces; a material the part names that no file defines is
  reported in the log and kept as the mod made it. To confirm in game.
- A mod with a real costume (ten files or more) and a minor entry next to it (two files or less, no
  texture) no longer gives that entry as an outfit (Athena for Ingrid gave a second, almost original
  Drive Tech outfit).
- Other slots are unchanged.

## 1.7.0 (2026-09-29)

### Colour swatches (experimental)
- The two squares shown next to each colour in the costume menu are computed for every modded outfit: the
  tints of its two largest colour zones, from the mod's own colour files and colour masks, instead of the
  original outfit's squares. Capcom picks its squares by hand, so the result looks like the outfit without
  matching what Capcom would have chosen. This is a trial and may be removed. Outfits whose materials do
  not use the game's colour system keep the original squares.

### Compatibility
- A mod that ships its own costume scene and points a part at another folder gets that part: Mummy Dhalsim
  showed the original head. Followed only when the file exists and the mod's scene matches the game's
  (same model references, same path length); otherwise the log says it was not followed.
- Textures whose small mip levels were sized with fractional blocks are repaired as a whole (Feixue for
  Mai lost its body and face). The mip table is rewritten only when the one computed from the format
  fills the file exactly.
- A part hidden behind a stripped mesh of another part keeps that mesh, paired with a material file that
  defines its materials, instead of getting the original part back (Mai's hair on Feixue and SuiSui) or
  being left out (Changli).
- Physics files kept next to the parts, where the game keeps them, are followed: the slot's physics
  settings point at the mod's files (Chique Casual Ingrid's hair). Every mod laid out that way now uses its
  own physics.

### Building
- `build.bat` finds Visual Studio or the Build Tools wherever they are installed.
- `loader/third_party/bcdec.h` (MIT or public domain) decodes the colour masks.

## 1.6.1 (2026-09-27)

- `reframework\costume_mods\SF6_CostumeAudit.bat`: lists what is installed (loader, REFramework, script,
  plugin), what the last launch did, the costume mods found with where they come from (mod-manager pak,
  archive, folder) and the outfits each one gave. Read-only; the report is saved next to it.
- The loader writes `reframework\data\SF6_Costumes_Data\outfits.json` at each generation, the list the
  audit reads.
- Notes or other files dropped in `costume_mods` no longer make the next launch rebuild everything; only
  archives, `.pak` files and folders count.
- A mod without a name in its `modinfo.ini` is named after its archive or folder in the log.
- Documented: a costume and an add-on from two archives are combined when put in one sub-folder of the
  character.

## 1.6.0 (2026-09-27)

### Installing mods
- Costume mods are read as downloaded: `.zip`, `.7z` and `.rar` files in
  `reframework\costume_mods\<Character>\`, unpacked once into a hidden cache.
- Bundles give one outfit slot per option; add-ons (no gloves, other hair, a weapon) are combined with
  the outfit they complete: up to three add-ons, one variant each, plus one with all of them when they
  do not replace the same files.
- Identical files shared by several variants are stored once in the generated pak.

### Mods made for older versions of the game
- Colour files whose type signature predates a game update are brought up to date
  (DOA4 Christie, TFD Racer showed up white).
- Colour files in the 2023 layout are converted to the current one (Ken SFV style).
- Textures whose mip table declares a wrong row pitch are repaired (Vegeta Majin).
- Material references are renamed together with the textures they point at (white Specter Zangief).
- A part shipped without a material gets the original material of that part only (see-through Lily).
- Weapons and props listed in `model_parts` follow the slot (Lily's clubs).
- A model folder shared by two outfits is mapped to the right one (Mummy Dhalsim).

### Loading safety
- Every slot is read back the way the game will load it before the pak is written: files present,
  every mesh material defined by its material file, every texture present and consistent. Broken parts
  are rebuilt from the mod's shared part or the original outfit; a slot that still fails is left out
  and named in the log.
- Files only a mod has, stored in another outfit's folder, are kept (Juri stalled with two mods).
- Only the slots in use are declared to the game (a character on trial listed 100 empty outfits).
- The pak is really written or nothing changes: a game still closing no longer leaves an old pak
  marked up to date.
- Removing a mod no longer stalls the character select: every unused slot number shows the
  character's Outfit 1, for a choice saved by the game or a replay that still names it.

### Modular mods and variants
- Modular mods built for an external mod manager are installed the way it does by default: one outfit per shape
  option, first texture option, optional parts left out (C. Viper went from 134 entries to 18).
- Options that replace the same files no longer share a slot; identical outfits are installed once.
- A character that runs out of its 100 slots is named in the log.
- A mod that only changes a part of an original outfit (glasses, earrings) gives that outfit with
  the change, in a slot of its own, instead of being ignored.

### Names
- Outfit names follow each other per character (Outfit I, II, III) after a mod is removed; each slot
  keeps its number behind the name, so saved choices and replays stay valid.

### Log
- Each slot line shows its outfit name (BrewedVFX).
