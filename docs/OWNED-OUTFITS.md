# Extra slots only for owned outfits (native plugin)

Ported from upstream Slots Loader 1.11.0 (commit 6f1966b, "slots of outfits the player does not
own are hidden"). Code: `plugin/src/ownership.hpp` (pure decision logic) and
`plugin/src/costumes.cpp` (game calls). Unit tests: `plugin/tests/build_ownership_test.bat` (`marker_test` needs the opt-out's name in
the environment variable `SLOTS_OPTOUT_NAME`; the repository does not hold it).

## Behaviour (default)

- A slot made from a fighter's Outfit 2, 3 or 4 (the registry's `slots[].original_costume_no` =
  1, 2, 3) is shown only when the game lists that outfit as the player's. Slots made from Outfit 1
  (0) or DriveTech (4) are always shown and never asked about. A slot whose outfit is unknown (no
  `slots` entry) is shown.
- The game's answer: `app.InventoryManager.CostumeInventory.GetCostumeNoList(app.CHARA_ID)`
  (`List<UInt32>`), the list the character select screen and Battle Settings use. Whatever makes
  the game list an outfit counts: default, bought, DLC accepted by the game, an unlock-all script.
- A list without Outfit 1 is no answer (inventory not loaded, or a character on trial): nothing
  changes. Before the title screen is left only "owned" is taken. "Owned" is kept until the game
  closes, so nothing flips during or after an online session.
- Asked at boot, title screen left, character select, Battle Settings; never while the battle
  description (`app.bFlowManager` -> `m_flow_work._FlowMap.m_desc`) has `IsNetwork` set.
- Hidden means the slot's record is not given (`AddDlcSaveData`) or is taken back
  (`DeleteDlcSaveData`), so the game's own lists leave it out. The costume pak is unchanged: replays
  and saved settings naming a hidden slot still load.
- Battle Settings: a hidden slot's intent stays in `state.json`, the menu shows DriveTech, and the
  online alias skips it until the outfit is owned again.
- Characters without DriveTech (Arjun) keep the 1.9.6 rule: their slots are offline outfits, the
  save keeps Outfit 1, and they never block boot or the alias of other characters. Their slots
  made from Outfit 2 are hidden like any other when not owned.
- If `GetCostumeNoList` is missing (a game update), the check turns itself off and every slot shows.

Log (`reframework\data\SF6_CostumeSlots_data\native_log.txt`), one line per hidden slot:
`[F1] slot v05 (Outfit I, <mod>) hidden: made for Outfit 2, which the player does not own`, then
`costumes: ownership taken back from N hidden slots` when records were removed.

## Maintainer opt-out

A private opt-out for the maintainer turns the check off. It is not documented here and not
shipped. It is read once when the plugin starts. With it, every slot shows as before this change:
no `GetCostumeNoList` call, no online check, no probe, no ownership taken back (slots hidden in an
earlier session get their record back at the next ownership pass). The log then has one line at
start: `costumes: ownership filter inactive`. The switch file of 1.9.7 is no longer read
(1.9.8).

## Read-only probe

An empty `reframework\data\SF6_CostumeSlots_data\ownership_probe.txt` logs, at up to 8 ownership
passes a session, what each candidate method answers for the characters that have slots
(`probe F1 list=[0,1,4,...] | c1 r2: has 1 save 0 default 0 valid 1 dlc 1 ...`). Ignored while
the ownership filter is inactive.

## To check in game

1. Without the opt-out, with a slot made from an outfit the player does not own: one `hidden:` line,
   `ownership taken back` once; the select screen and Battle Settings no longer list that slot;
   Outfit 1 / DriveTech slots and slots of owned outfits still do.
2. With the opt-out: the `ownership filter inactive` line, no `hidden:` line, every slot listed.
3. A Battle Settings choice of a slot that later becomes hidden: the menu shows DriveTech,
   `state.json` keeps the slot, no `slot vN mounted` line in a DriveTech match.
4. Online with a hidden slot's character: DriveTech, as the opponent sees it, no communication
   error; the log shows `online battle, outfit ownership not asked` at most once per battle.
5. Arjun: `[F34] no DriveTech outfit ...`, `boot done`, his Outfit 1 slots listed.
6. The probe once on the current game version, to confirm the method names below answer.

The method signatures were checked against an `il2cpp_dump.json` from before the October 13, 2026
game update; the methods were not called on the current version yet.
