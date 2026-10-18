# Slotsloader Manager 1.0.1

A Windows mod manager for your ZIP, RAR, 7Z and folder mod libraries. It is built for Street Fighter 6 (more games are planned), and installs SF6 mods two ways:
- as **extra outfits** through Slotsloader;
- by **replacing the original** files the way external mod managers do, including tool mods such as REFramework scripts, plugins and Slotsloader itself.

The title bar shows **Slotsloader Manager · 1.0.1**. Private builds before the public release were numbered 2.0.0 to 2.5.9 and called **SlotsLoader Manager** (2.1.0 to 2.5.8) and **SF6 SlotsLoader Installer** before that; see [docs/HISTORY.md](docs/HISTORY.md).

Open **Slotsloader Manager.exe** and keep its `.exe.config` beside it. Windows .NET Framework 4.8 runs it; RAR/7Z support uses your installed 7-Zip. No Blender, Node or Python is required. Game/library paths are in Settings. Your existing mod manager libraries are configured by default. `Data` beside the executable holds settings/cache/view preferences and the backups of replaced game files; preserve it during updates. The whole app folder (executables + `Data`) can be moved or renamed; see **Move the app folder** below.

## What changed

- 1.0.5:
  - **Rename** in the Mod library: right-click → Rename or **F2**, then type on the row. Enter or clicking away renames, Esc cancels. Installed outfits, Install all variants copies, replacements and history follow the new name. Options you were editing stay as they were.
  - The option's version (`v1.0`) shows at the right of its name.
  - `MenuPriority=` orders options as external mod managers do: higher first, an option without one counts as 0. 1.0.4 had it reversed.
  - Portrait art for several fighters counts as a UI mod. A portrait bundle no longer shows under every fighter.
  - Colour-only mods keep their fighter after you open them.
  - Reinstalling an Install all variants copy updates that file, even after the source was renamed.
  - Settings → Game files & replacements → **Try to fix character-swap mods broken by game updates** (off by default). On, Replace original and Slotsloader 1.10.4 apply known fixes to old character swaps: the Alex update's scene signature, and some Mai and Elena physics files rebuilt from the current game. Only files that match a verified older version are changed. Off, nothing changes and no swap warnings show.
  - Bundles **Slotsloader Fork 1.10.4**: the swap fixes above, and an archive briefly locked by a virus scanner is no longer skipped.

- 1.0.4:
  - Clearer option order. In each list: the mod's `MenuPriority=` (lower first) when it sets one; the author's numbering when names start with numbers; otherwise the default option first, marked **Default** (its files are identical to the main files'), single options before groups, small add-ons right after the option they change, then by name. Main files no longer sit between two groups. The order is display only: which files win still follows the order you pick options in.

- 1.0.3:
  - Opening an installed mod in the Mod library ticks the options it is installed with, not the defaults. With **Replace original** you see the replacement's options, with **Extra outfit** the newest extra outfit's (else the other one's). A mod installed with Install all variants still opens with the defaults.

- 1.0.2:
  - Bundles **Slotsloader Fork 1.10.3**. An extra outfit that puts a fighter on another body (Sakura as Ryu uses Juri's body) now gets that body, as its Replace-original install does. Before, it came out on the original fighter's skeleton.
  - Folders that sort mods are listed mod by mod. `Mods\Cammy\<mods>` used to show as one mod with every mod as an option. A folder counts as a group when nothing at its top makes it a mod (no `natives`, `reframework`, `modinfo.ini`, `.pak` or `.dll`), and it holds ZIP/RAR/7Z files or is named after a fighter (`Cammy`, `Chun-Li mods`). Up to two group levels are read. A mod with options in subfolders and its own name (`Gym Cammy\Option 1\natives`) is still one mod.

- 1.0.1:
  - The loader is now called **Slotsloader** too, so the app and the loader share one spelling. The release bundles **Slotsloader Fork 1.10.1** in a `Slotsloader` folder beside the exe. It keeps the patch PAK row free of gaps that could stop the game at a black screen, and treats a patch PAK it can't read as another tool's. It also reads a texture that a Replace-original install switched off from its loose copy. An extra outfit whose own scene lists a different number of parts than the game's (SF5 Sakura over Ryu) keeps its own hair and cloth physics.
  - **Uninstall means uninstall.** Uninstalling keeps no copy: reinstall from the Mod library. Outfits with no library source (manual drops) go to the Recycle Bin.
  - Gear → **Restore** has two choices. **Remove this Manager's mods** removes every replacement and tool mod this app installed, with Undo. Its copies are kept only while that Undo is available. **Restore game to vanilla…** removes those mods and also the external mod manager's installs, every patch PAK (other tools' too) and the `natives` folder. It asks first and has no Undo.
  - While an external mod manager has any mod installed, only Extra outfit and Slotsloader install here, so two managers never change the same game files.
  - **INSTALL AS** remembers your last choice. PAK and tool mods switch to Replace original by themselves, and **Tab** in the Mod library switches between the two.
  - Installed page:
    - an **All · Extra · Installed** type toggle;
    - a character filter with fighter portraits;
    - newest-first sorting, with a short "installed today" on each row;
    - History and Presets as icon buttons.
  - Mods are checked and installed faster. Archives are unpacked in parallel and checksums are faster; for example, a 370 MB health check went from 2.7 s to 0.8 s.
  - A file inside one fighter's folder no longer makes a mod count as "Multiple characters".
  - From 1.0.0, update by downloading 1.0.1 from Releases once: 1.0.0's updater expects the old folder name. Later updates work in the app again.

- 1.0.0 (first public release):
  - Slotsloader Manager is public, together with the loader it installs: the release zip carries Slotsloader 1.10.0, so **Install Slotsloader** works offline.
  - The private development versions before it (internal 2.0.0 to 2.5.9) are summarized in [docs/HISTORY.md](docs/HISTORY.md).

## Compose an outfit

1. Select a library mod (Ctrl+F, type, Enter opens the top match), use Open archive / Open folder, or drop an archive or folder on the window.
2. Click a main-file or option row to toggle it. Hover changes only the side preview. Enter toggles the focused option (2.4.1; Space now installs); arrow keys navigate/expand groups. Later checked options win file conflicts, with main files applied first.
3. Choose the matching character and an outfit name. This app does not retarget assets to another fighter.
4. Groups marked **Required** need one choice; the footer lists them and jumps to the first. The merged files are composed automatically; Review files / Merged files shows the winning files and any required changes.
5. Click **Install** (or press Space), then restart SF6 for Slotsloader to assign its in-game outfit numbers.

The result is `reframework/costume_mods/<Character>/<Outfit name>/natives/...`, with the app's provenance record (`.slots-installer.json`). Sources are preserved. You can install several named combinations from one bundle.

For Goth Juri C1, Main files + Default skin + Regular full outfit barefoot yields 49 files. Ingrid C.C. Barefoot C1 is a single component with 29 files.

## Replace originals

1. Choose a mod, then click **Replace original** under the preview (or press **Tab** in the Mod library to switch between Extra outfit and Replace original). PAK and tool mods switch to it automatically.
2. The health check runs by itself. The inspector and footer show what the mod replaces and any other installed mod for the same outfit; **Health** shows everything, including the fixes it applies. Another variant of a mod you already installed replaces it in place.
3. Click **Install** (or press Space). Start SF6 with **Launch SF6**. With the replacement method on Automatic, the app uses REFramework's loose file loader when it is on and otherwise the PAK method, so the files always show in game.
4. On **Installed**, set the type toggle to **Installed** (replacements and tool mods). From there you can disable, reorder, edit or uninstall it. Uninstall restores the original files exactly and keeps no copy: reinstall it from the Mod library. A mod whose source is gone from your libraries goes to the Recycle Bin instead.

After a game update, the Installed page shows a banner. Click **Reapply**, or **Restore original game files** to remove every replacement and tool mod this app installed; **Undo** puts them back. The same action is in gear → Restore → **Remove this Manager's mods** and in Settings → Game files & replacements. Extra outfits and external mod manager installs are not touched. To go further, gear → Restore → **Restore game to vanilla…** also removes the external mod manager's installs, every patch PAK and the `natives` folder (no Undo). It leaves REFramework and your extra outfits alone.

If another tool replaced or deleted a file this app installed, the banner says which one. Uninstall still works: choose **Restore original** or **Keep current file** (see What changed, 2.2.0). The originals kept behind such files and the other tools' files that were replaced stay in the Manager's Data folder.

## Switching from another mod manager

- **While the external mod manager has any mod installed, only Extra outfit and Slotsloader install here.** Replace original, PAK mods and other tool mods wait until it is empty, so two managers never change the same game files. Uninstalling, disabling and Restore original game files still work for this app's own mods.
- **Moving over**: uninstall your mods in the external mod manager (this app no longer uninstalls them), then install them here with Replace original, or import its preset.
- Close the external mod manager before changing game files here. Don't install the same mod in both managers.
- External mod manager features this app does not reproduce:
  - the `pak_mods` folder (a PAK mod installs here as the next patch PAK instead);
  - replacing game executables;
  - the mod categories view (the Filters panel has fighters, Tool mods and Misc).

## Ask the Assistant

1. In **Settings → Assistant**, paste your Anthropic API key and choose **Save key**. The default model is **Claude Haiku 5.5**; **Claude Sonnet 5.5** is the more capable, more expensive choice. **Test key** sends one tiny request, costing about $0.0002.
2. Open **Assistant** (Ctrl+3), type a request and press Enter. Shift+Enter adds a new line, and **Stop** cancels a reply in progress.
3. Review the **Plan**. Untick anything you do not want, then **Apply selected**. Changes run in order through the same install steps and stop at the first failure. Restart SF6 afterwards.

What is sent to Anthropic:
- Your messages.
- Mod, option and outfit names and descriptions.
- No images: since 2.0.2 preview images are never sent (the option was removed).

Local folder paths are not sent. Mod text comes from downloaded files and is treated as data, never as instructions. Claude cannot change settings, delete presets or backups, or open folders. Uninstalls delete (manual outfits with no source go to the Recycle Bin).
## Manage and recover

Installed includes manual directories and ZIP/RAR/7Z/PAK entries. Actual mappings come from Slotsloader's **last recorded run**, matched by exact source path; one archive may provide several numbers. New/changed outfits show "Restart SF6 to load". Missing/unreadable results remain explicit rather than guessing a slot.

On **Installed**, Ctrl-click or Shift-click to select several rows, or use **Select all** (Ctrl+A). **Uninstall selection** deletes them at once; reinstall from the Mod library. Manual directories and ZIP/RAR/7Z/PAK files, which have no source there, go to the Recycle Bin. An archive row may contain several outfit numbers; removing it removes the whole archive. Search and filters limit Select all.

**Slots of one mod:** two or more extra outfits installed by the Manager from the same mod (and character) show as one row, "<mod> · N slots". Click the arrow (or press Right/Left, or double-click) to expand it; each slot shows its options and Slotsloader's slot. With the row selected, **Uninstall N slots** removes them all in one click; select a slot to remove just that one.

**Variants of a dropped mod:** when Slotsloader splits a manual archive or folder into several variants, its inspector shows **VARIANTS** with one row each: on/off and, for a built one, its slot (Outfit V). A click writes the choice at once; the status says "Blue off · applies at next launch" with **Undo**. While SF6 runs it still saves and says "applies after SF6 restarts". The choices live in `reframework\data\SF6_Costumes_Data\variant_choices.json`; Slotsloader reports what it built in `variants.json` beside it. Outfits the Manager installed are one variant each and have no toggles.

App-managed outfits can also be edited, enabled or disabled. The library's Uninstall outfit action remains limited to managed copies linked to that source. Disabled outfits are kept outside the loader scan tree in the sibling `.slotsloader-installer-disabled` folder. Since 1.0.1 uninstalls and variant switches keep no copy; `.slotsloader-installer-trash` and `.slotsloader-installer-backups` only hold copies from older versions.

The **character** filter opens the fighter portraits, as in the library; pick one or more fighters, and each card counts that fighter's installed outfits.

## Save and restore presets

1. Open **Installed → Presets…**, enter a name, and choose **Save enabled outfits**. This saves every enabled installed entry, including manual archives, regardless of your search, filters or selected rows. Disabled outfits are excluded.
2. Close Presets and uninstall the outfits you no longer want enabled; the preset keeps its own copy of them.
3. Later, select the named preset and choose **Restore outfits**. Missing entries return to their original paths; exact file/structure duplicates there are skipped. Different contents at an existing name stop the whole restore before any change. Other installed outfits remain in place. Restart SF6 afterward.

Presets are reusable and contain actual files, so restoration does not require the source mod library. Keep the sibling `reframework/.slotsloader-installer-presets` folder if moving your game (`.slotsloader-installer-removed-sets` only holds copies from older versions). Each preset uses disk space for a full copy; **Delete saved copy** removes only that retained set after confirmation. Slot numbers still come from Slotsloader's next run.

## Move the app folder

Close the app (and any `Slotsloader CLI.exe` run) first. Then move the whole folder, `Data` included, for example from `<old Manager folder>` to `<Manager folder>`. **Move** it (or copy it and then delete or rename the old folder): while the old folder still exists, the records keep using the files there. Do not move `Data` on its own or leave parts behind.

When it next starts, the app finds the recorded backups and stored copies in the moved `Data`, writes one line about it to `Data\log.txt` and saves the new locations (the window does this at start-up; the CLI with its next change). To check first, run `Slotsloader CLI.exe replace-status` from the new folder: it changes no record and should show `Backups: N recorded, 0 missing on disk`, `Relocation:` naming the previous folder, and `Status: OK`. A backup that is really missing is reported with its path and is never silently dropped. A custom replacement store outside `Data` (Settings) stays where it is. External mod manager folders, the game folder and your mod libraries are not affected by the move.

## Check without opening the app

```powershell
& '.\Slotsloader CLI.exe' list-options '<library>\My mod.zip'
& '.\Slotsloader CLI.exe' test-mod '<library>\My mod.zip' --json '.\new-check.json'
& '.\Slotsloader CLI.exe' replace-plan '<library>\My mod.zip' --game '<game folder>'
& '.\Slotsloader CLI.exe' replace-status --game '<game folder>'
```

Bundles need `--select '<ID>'` options or `--selection '<JSON file>'`. `test-loader` additionally takes an explicit native loader, installed game root and **new separate sandbox**. See **CLI.md** for the full command and report scope. Tests never need to install into the live game.

Extra outfit mode composes loose native costumes. Replace mode installs a PAK-only source as the next patch PAK. Lua and DLL tool mods install in Replace original mode. A composition/integrity pass does not prove appearance in-game. Native loader tests also omit DLC PAKs and report that limitation. No loader/game update is included.

Source: this folder (`src/`). Use `build.ps1` to build both executables and `test.ps1` for isolated fixtures (`-RealArchives` adds the real-library checks, `-Only <names>` runs selected checks, `-Dialogs` the visible dialog check; see docs/DEVELOPMENT.md). See **VALIDATION.txt** for this build's checks. The owned native loader repository is [ZZtaii/slotsloader-manager](https://github.com/ZZtaii/slotsloader-manager).

## License and third-party notices

Slotsloader Manager is source-available for personal, non-commercial use: see [LICENSE](LICENSE). Parts adapted from other projects (code ported from Slotsloader, the bcdec texture decoder, the Octicons GitHub mark) keep their own licenses, listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Fighter portraits and stage names are read at run time from your own installed game and cached in `Data\portraits`; they are not part of this app.
