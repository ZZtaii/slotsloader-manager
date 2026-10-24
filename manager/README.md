# Slotsloader Manager 1.0.12

A Windows mod manager for Street Fighter 6. It installs mods from your ZIP, RAR, 7Z and folder libraries two ways:
- as **extra outfits** through Slotsloader;
- by **replacing the original** files, like other mod managers, including tool mods such as REFramework scripts and plugins.

**Start:** unzip anywhere and open **Slotsloader Manager.exe** (keep the other files beside it). Windows already has what it needs (.NET Framework 4.8); 7-Zip for RAR/7Z is downloaded by itself if you don't have it. The game and your mod manager's libraries are found for you; change them in Settings.

**Keep the `Data` folder** when you update: it holds your settings and the backups of replaced game files. The whole app folder can be moved or renamed; see [Move the app folder](#move-the-app-folder).

## What changed

- 1.0.12:
  - **Browsing is instant.** Opening a mod no longer shows a progress bar or "Reading…", and the library list keeps taking keys and clicks meanwhile: the mod you stop on opens, and the ones you pass over aren't read. Mods opened this session stay ready while unchanged, and the next RAR or 7Z in the list is read ahead, so arrowing through the library shows the options and preview at once.
  - **The health check waits until you stop on a mod** (about a second), or starts at once when you point at Install or press Space. The footer shows only the result; a check that takes longer shows "Checking…".
  - **Choose-one options:** when a mod's top-level options all replace the same files (lighting levels, for example), they are one choice with nothing preselected, and the queue skips the mod until you choose. An install made with all of them keeps the one that won.
  - **Search `pak`** finds PAK mods on Installed and in the library. With a fighter filter on, mods for that fighter alone come first.
  - **Faster changes with many replacements:** Enable, Disable, priority moves and installs are several times quicker when many files are installed (about 1,000 files in tests: 9.5 s → 1.8 s).
  - **Missing saved options are said, not skipped.** When a mod no longer has an option you saved, Reinstall and preset restore stop and ask you to review its options instead of installing without it.
  - The first start after updating reads each library archive once in the background ("READING n%") to find the PAK mods.
  - An empty Mod library says so, with a link to add your mods folder in Settings → Mod libraries.

- 1.0.11:
  - **RAR and 7Z mods work without installing 7-Zip.** When 7-Zip isn't installed, the Manager downloads its own (7-Zip 26.00, SHA-256 checked) the first time a RAR or 7Z needs it, into its `7-Zip` folder. An installed 7-Zip is still used first, also when it isn't in `C:\Program Files\7-Zip`, unless it is older than the Manager's copy (7-Zip before 15.06 can't open RAR5 archives). In Settings, the 7-Zip field can stay empty ("Automatic"); the line under it says which 7-Zip is used. A path you chose yourself still comes first; the `C:\Program Files\7-Zip\7z.exe` that earlier versions saved by default counts as Automatic.
  - **Credits:** Settings → About thanks BrewedVFX for testing and recommendations, with a link to their page.
  - **Mod libraries:** in Settings, "Libraries" is now "Mod libraries", with a short line under it saying what they are. The replacement store (Settings → Game files & replacements) has one too.
  - **Welcome:** at the first start (and once after updating), a short welcome covers what matters most: using the Manager beside your external mod manager, using it on its own, and what to do when something breaks. Open it again from Settings → General → **Show welcome**.
  - **Help:** gear → **Help** (or F1) opens the in-app Help: searchable articles on setting up, Replace original, extra outfits, the automatic fixes, what to do when something goes wrong (extra outfits don't show, the game won't start, after an SF6 update, leftover PAKs, RAR/7Z) and where the Manager keeps its files. The welcome links to it. The gear menu no longer has Welcome or About (About is the Settings → About tab).
  - **Leftover PAKs show up and can be removed.** A patch PAK (`re_chunk_000.pak.patch_NNN.pak`) that no mod manager lists, for example one the external mod manager left after you uninstalled everything there, still loads in game. The Installed page now lists each one as **Leftover PAK** ("not listed by any mod manager · 48.0 MB"). Select it and click **Remove PAK** to take it out; **Undo** puts it back. Where a higher patch PAK exists, an empty placeholder keeps its number so the PAKs after it still load. Nothing is removed without your click (a PAK you added by hand looks the same), and the removed PAK goes to the Recycle Bin once the Undo is gone.
  - **Restore game to vanilla keeps REFramework and Slotsloader** when this Manager installed them, as its confirmation always said. Before, it removed them with everything else, so extra outfits stopped loading.
  - **Why Replace original is off:** while the external mod manager has mods installed, Extra outfit mode now says above the toggle that Replace is off and how many mods it has. In Replace original, the footer reads "Replace original is off" with a short line on what to do, instead of a cut-off "Cannot install" message.
  - **Saving no longer fails on a busy file.** When another program (a virus scanner, for example) has one of the Manager's settings or record files open for a moment, saving now waits briefly and tries again instead of stopping with "Unable to remove the file to be replaced".
  - **No white flashes at start, and a quicker start.** The window appears once it is fully drawn, instead of showing white blocks for about half a second; any window whose controls are still drawing shows the dark background instead of white. The mod list no longer waits for the My filters counts when no filter is on Only or Hide. Listing a large library is about four times quicker (2,600 mods: 0.7 s → 0.17 s), and the window builds about 40% faster from the second start on.

- 1.0.10:
  - **Presets copy less.** An archive you dropped into costume_mods yourself that is identical to an archive in your libraries is now saved as that library mod instead of being copied. Only archives of the same size are compared, so saving stays quick. Restore puts it back from the library under its own file name, and skips it while it is still installed.
  - **The Presets window says what it copies and why.** After **Save**, the status line says how many items were copied and their size ("1 copied (12 MB), listed below"), and the new preset is selected. Its details start with "N items have no library mod and are copied (size):" and one line per item: **manual drop**, or **not found at** the path where its mod was when it was installed (renamed or moved outside the Manager). Presets saved by 1.0.8 and 1.0.9 show the list too.
  - **Renamed mods are found again.** When you save a preset, a replacement or extra outfit whose mod was renamed or moved outside the Manager (for example `Mod - v1.0.zip` renamed to `Mod.zip`) is linked to the library mod that holds exactly the files it installed. It is then listed instead of copied, and History shows it as renamed. Only mods with a similar name are opened, and their files are compared byte for byte. Files the Manager fixed are fixed again the same way and must then match too. When no mod or more than one matches, nothing changes and the item is still copied: the Manager doesn't guess.
  - **Reapply is hard to miss.** When the game files no longer match your replacements (an SF6 update, a Steam verify), the notice now shows on every page, not only on Installed: a box at the bottom right with **Reapply**, **Show details** and ✕ (back if something else changes). **Launch SF6** says so first; click it again within 15 seconds to start anyway. Nothing is reapplied without your click.
  - **The REFramework line updates by itself.** When REFramework changes outside the Manager (you copy in a new `dinput8.dll`, or another mod manager installs it), the Extra outfit line and Settings show the new build within a couple of seconds. Before, an "out of date" line could stay until the Manager refreshed for another reason, such as the external mod manager closing.
  - **REFramework installs while an external mod manager has mods installed**, like Slotsloader. Only plain REFramework (`dinput8.dll` and its revision note) is let through; a mod that bundles `dinput8.dll` with scripts or plugins still waits. If the external mod manager installed REFramework itself, the line says to update it there and offers no download.

- 1.0.9:
  - **Other costume loaders can see your replacements.** After every install, uninstall, enable, disable or priority change, and at start-up, the Manager writes `reframework\data\SF6_Costumes_Data\manager_installs.json`. It lists each replacement and tool mod in priority order, with its files and patch PAKs. A costume loader that reads it can leave those files alone instead of turning them into extra outfits, without searching your PC for the Manager's Data folder. Slotsloader doesn't need it. The file is only written where a costume loader keeps its data.
  - **Smoother with big libraries and bundles.** Review files builds its rows only when you open it, even for a mod with thousands of files. MY FILTERS match in the background on large libraries, previews you have already seen show at once, and the list refreshes at most five times a second while the library is being read.

- 1.0.8:
  - **MY FILTERS** in the Filters panel: filters you make from keywords. A filter matches a mod whose name or any option name or folder contains one of its keywords (case, spaces and dashes don't matter). Click a filter to show only its mods, again to hide them, a third time to turn it off; right-click for the same choices and **Edit filter**. **+ New filter** and Edit open a small editor under the chips: while you type keywords the list shows what they match. Filters are saved in the Manager's Data and their on/hide state is remembered like the other filters.
  - A ready-made **NSFW** filter (nsfw, nude, naked, topless, bottomless, pubic, futa, see through, …), picked from real mod and option names. Edit or delete it like your own.
  - **Presets are now a list**, not copies: each enabled extra outfit and replacement is saved by its library mod, options, outfit name and order, so a preset takes a few KB instead of gigabytes. Restore installs them again from your libraries, one by one: what is already installed is skipped, and a mod no longer in your libraries, a different outfit in its place or a failed install is listed under the preset while the rest goes on. Only things with no library mod (manual drops) are still copied. Presets saved by older versions keep working; save a new one and delete the old to free the space.
  - **REFramework is checked too.** Slotsloader runs inside REFramework, so the Extra outfit line now says when REFramework is missing, when SF6 hasn't been started with it yet, or when it is older than the build Slotsloader is tested with (REFramework nightly 01432, 11 Oct 2026). Click it to download that build from the REFramework-nightly releases on GitHub (checked against its SHA-256); it opens in Replace original, and **Install** puts it in the game folder. The REFramework it replaces is backed up and comes back if you uninstall it, and a newer pinned build later updates the same install. Settings → Game files & replacements shows the installed build and has the same button. Upscaler builds are recognised and never called old.
  - The mod details column stays compact so INSTALL AS is always in view: REPLACES notes show their headline, and **Scroll to expand** at the right of REPLACES shows they continue. Scrolling down over the column (or clicking that hint) shows everything, each note as a headline with its details under it; the column then scrolls, with a thin scroll bar that shows only while you scroll. Opening another mod makes it compact again.
  - The Installed page's Refresh icon is a clockwise arrow (it looked like a power button).

- 1.0.7:
  - **Faster start.** The Mod library lists every mod at once (2,554 mods in under half a second here) and reads the ones it doesn't know yet in the background while you use the app; the list caption shows `· READING n%`. Fighters, outfits and option folders come first; the names from modinfo.ini (the Name view, option names for Deep search, authors) follow, shown as `· NAMES n%`. A mod not read yet shows **Reading…** on its row.
  - Updating from 1.0.5 no longer reads every archive again: what the library already knows stays, and only the names are read.
  - Fixed: since 1.0.6, a library scan (including the one at start) didn't refresh Replace original installs and "source changed" marks until you opened the Installed page or installed something.

- 1.0.6:
  - **Name · File** under the library search: list mods by their own name (from modinfo.ini) or their file name. **Ctrl+Shift+N** switches. A mod without a name shows its file name in both. The first refresh after updating reads each archive once more to learn the names.
  - Search matches both names, word by word in any order: `viper barefoot`, `barefoot viper` and `cviper` all find `C-Viper-Barefoot.zip`. `juri, barefoot`, `juri,barefoot` and `juri barefoot` give the same mods, and `juri c1` works without a comma.
  - **Deep search** (check box beside the switch) also searches a mod's variant folders and option names: `barefoot` finds a bundle whose Barefoot variant is a sub-folder.
  - Changing a filter scrolls the list back to the top, like a search.
  - **Several mods at once**: Ctrl+click, Shift+click or Ctrl+A in the Mod library opens a queue with **Install N** and **Delete N**, and a row per mod with its result. Each mod installs with its default options; a bundle gets its main files and the default or first choice of each required group, optional variants stay off. A mod that can't install is skipped with the reason, and the rest go on. Delete moves them to the Recycle Bin.
  - **Add to outfit…** (Extra outfit mode, beside Install all variants): copy the options you ticked, for example one hair option of a bundle, into an extra outfit you already installed. Exactly those files are added, replacing the outfit's files with the same names; nothing else changes. Edit options and reinstalls of that outfit keep them. Files made for another outfit slot can't go on it.
  - **Rename** (F2) follows the switch: with **Name** on it changes only the name the list shows (kept in the Manager's Data; the file and its modinfo.ini stay as they are, and an empty name gives the mod's own name back); with **File** on it renames the archive or folder as before. The rename box is now the size of the row's name.
  - The destination path line under the outfit name is gone.
  - **Refresh** button on the Installed page.

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
  - While an external mod manager has any mod installed, only Extra outfit and Slotsloader install here (and REFramework since 1.0.10), so two managers never change the same game files.
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

After a game update, the Installed page shows a banner, and every other page a notice at the bottom right (1.0.10). Click **Reapply**, or **Restore original game files** to remove every replacement and tool mod this app installed; **Undo** puts them back. The same action is in gear → Restore → **Remove this Manager's mods** and in Settings → Game files & replacements. Extra outfits and external mod manager installs are not touched. To go further, gear → Restore → **Restore game to vanilla…** also removes the external mod manager's installs, every patch PAK and the `natives` folder (no Undo). It leaves REFramework, Slotsloader and your extra outfits alone (1.0.11: also when this Manager installed REFramework and Slotsloader; the external mod manager's REFramework goes with its mods).

If another tool replaced or deleted a file this app installed, the banner says which one. Uninstall still works: choose **Restore original** or **Keep current file** (see What changed, 2.2.0). The originals kept behind such files and the other tools' files that were replaced stay in the Manager's Data folder.

## Switching from another mod manager

- **While the external mod manager has any mod installed, only Extra outfit, Slotsloader and REFramework install here.** Replace original, PAK mods and other tool mods wait until it is empty, so two managers never change the same game files. REFramework that the external mod manager installed is updated there. Uninstalling, disabling and Restore original game files still work for this app's own mods.
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

1. Open **Installed → Presets…**, enter a name, choose what to include (Extra outfits, Replacements or Both) and click **Save**. This saves everything enabled of that kind, regardless of your search, filters or selected rows. Disabled outfits are excluded.
2. A preset is a list: for each outfit and replacement, the library mod it came from, its options, its outfit name and its order. Only an outfit or replacement with no library mod (a manual drop, a mod you deleted, renamed or moved outside the Manager) is copied into the preset. A manual drop that is identical to an archive in your libraries counts as that library mod (1.0.10; archives of the same size are compared byte for byte). A mod renamed or moved outside the Manager is found again by its files when you save, if exactly one library mod with a similar name holds them. After Save, the status line says how many items were copied and their size, and the preset's details list each one with the reason.
3. Later, select the preset and click **Restore**. Everything missing is installed again from your libraries; what is already installed is skipped. Restored replacements go back into their saved priority order. A replacement installed under the same name from another archive or version is skipped and named in the result. A mod no longer in your libraries, a different outfit in the same place or an install that fails is listed under the preset, and the rest still goes ahead. A mod that changed since you saved installs its current version (also listed). A manual drop saved as its library mod comes back under its own file name. Restart SF6 afterward.

Presets live in `reframework/.slotsloader-installer-presets`; keep that folder if you move your game. Presets saved before 1.0.8 hold full copies and still restore as before (they show "full copy" and their size); save a new preset and delete the old one to free that space. **Delete** removes a preset after confirmation. Slot numbers still come from Slotsloader's next run.

## Filter the library

**Filters** (Ctrl+Shift+F) opens the panel beside the list: fighters, Tool mods, Misc (with its kinds), Installed and, with several libraries, one tile per library. **MY FILTERS** holds keyword filters you make. Click one to show only its mods, again to hide its mods, once more to turn it off; right-click offers the same and **Edit filter** (F2 on a focused chip). **+ New filter** opens the editor: a name and keywords separated by commas or new lines. A keyword matches a mod whose file name, own name, the name you typed for it, or any option name or folder contains it, ignoring case, spaces and dashes (`see through` also finds `See-Through`); one- and two-letter keywords must start a word. While you edit, the list shows what the keywords match. The **NSFW** filter comes ready-made; edit or delete it like your own. **Clear** turns every filter off.

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

Source: this folder (`src/`). Use `build.ps1` to build both executables and `test.ps1` for isolated fixtures (`-RealArchives` adds the real-library checks, `-Only <names>` runs selected checks; see docs/DEVELOPMENT.md). See **VALIDATION.txt** for this build's checks. The owned native loader repository is [ZZtaii/slotsloader-manager](https://github.com/ZZtaii/slotsloader-manager).

## License and third-party notices

Slotsloader Manager is source-available for personal, non-commercial use: see [LICENSE](LICENSE). Parts adapted from other projects (code ported from Slotsloader, the bcdec texture decoder, the Octicons GitHub mark) keep their own licenses, listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). The `7-Zip` folder (downloaded when needed) holds unmodified 7-Zip by Igor Pavlov under the GNU LGPL with the unRAR license restriction; its license is `7-Zip\License.txt`, and its source is at [7-zip.org](https://www.7-zip.org).

Fighter portraits and stage names are read at run time from your own installed game and cached in `Data\portraits`; they are not part of this app.
