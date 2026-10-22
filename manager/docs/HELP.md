<!--
In-app Help (gear → Help, F1). Parsed by src/HelpDocument.cs and shown by the Help page; the Assistant's knowledge
pack reads it too. Format:
  # Group            a group in the topic list
  ## Topic           an article
  > Lead.            one line right after the topic title, shown muted under it
  ### Heading        a heading inside the article
  - Item             a list item
  !! TITLE: text     an accent note card
  plain lines        paragraphs (a blank line ends one)
Inline: `code` for files, folders, commands and setting names; **bold** for UI labels and menu paths;
[[Topic]] links to another topic, [[Topic#Heading]] to a heading in it, [[Topic|shown text]] with other text.
-->


# Start

## Getting started
> What the Manager does, and your first install in a few clicks.

The Manager installs Street Fighter 6 mods from your own mod folders: ZIP, RAR and 7Z archives or plain folders. You choose how each mod goes in under **INSTALL AS**, beside the mod's preview:

- **Extra outfit** adds the mod as a new costume slot through Slotsloader. The original outfit stays in the game.
- **Replace original** overwrites the game's own files, the way external mod managers do. Tool mods (REFramework scripts, plugins, Slotsloader itself) always install this way.

### First start
- On the first start the Manager looks for SF6 through Steam and adds your external mod manager's mod folder as a library when it finds one. If it can't, set them in **Settings → General**: the **Game folder**, and your **Mod libraries** with **Add…**, then click **Save settings**.
- Add every folder where you keep downloaded mods as a mod library.
- A short welcome shows once. Open it again with **Settings → General → Show welcome**. This Help opens any time with F1 or **Gear → Help**.

### Your first extra outfit
- Open **Mod library** and pick a mod. Ctrl+F searches, and Enter opens the top match. You can also drop an archive or folder on the window.
- Tick the options you want. Groups marked **Required** need one choice; the footer lists what is still missing.
- Under **INSTALL AS**, choose **Extra outfit**. If the line under it says REFramework or Slotsloader is missing, click that line to install it. If your external mod manager installed REFramework, install or update it there.
- Check **CHARACTER** and **OUTFIT NAME**, then click **Install** (or press Space).
- Click **Launch SF6**. New outfits show on the character select screen once SF6 has started.

!! GOOD TO KNOW: The Manager never moves a mod onto another fighter. Pick the character the mod was made for.

Using an external mod manager too? Read [[Using it with your mod manager]] next.

## Using it with your mod manager
> Keep your external mod manager for replacements and use the Manager for extra outfits.

This is the "companion" setup from the welcome. The Manager works beside your external mod manager and leaves that manager's mods alone.

### While your mod manager has mods installed
- **Extra outfit** installs work as usual.
- Slotsloader and REFramework install here too. If your mod manager installed REFramework, update it there instead.
- **Replace original** is off. PAK mods and other tool mods wait too, so two managers never change the same game files. In Extra outfit mode, the **INSTALL AS** line says "Replace off" and how many mods your mod manager has.
- The Installed page lists its mods as **External mod manager** rows, marked "uninstall it there". The Manager doesn't uninstall them one by one; use your mod manager for that. Only **Restore game to vanilla** removes them all.

### While your mod manager is open
The header shows "● Mod manager running". While your replacements use the PAK method, PAK installs and every change on the Installed page wait until you close it ("Close the external mod manager first…"). Extra outfits, loose-file replacements and new tool-mod installs still go ahead. Removing a leftover PAK and Restore game to vanilla always wait.

### Tips
- Don't install the same mod in both managers.
- Your mod manager's mod folder can be one of your **Mod libraries**, so the same downloads show here.
- **Settings → Game files & replacements → Import external preset (.prt)…** brings over a preset from your mod manager.

### Moving everything to the Manager
- Uninstall every mod in your external mod manager first. Replace original stays off until that manager lists no mods.
- Install them again here with **Replace original**, or import your mod manager's preset.
- If an uninstalled mod still shows in game after that, see [[Leftover PAKs]].

# In depth

## Replace original
> Overwrite the game's own files, with a health check first and an exact restore on uninstall.

Pick a mod and choose **Replace original** under **INSTALL AS** (Tab switches modes in the Mod library). PAK mods and tool mods switch to it by themselves.

### PAK method or loose file loader
Choose in **Settings → Game files & replacements → Replacement method**:

- **Automatic** (the default) uses the loose file loader if it is on when your first replacement goes in, otherwise the PAK method. All replacements share one method.
- **REFramework loose file loader** puts the files in the game's `natives` folder and changes no PAK. SF6 only reads them while the loader is on. If the loader is off when you install, the Manager uses the PAK method instead and says so.
- **PAK method** works without REFramework. It invalidates the game's PAK entries for the replaced files and puts them back exactly on uninstall.
- A mod that is only a `.pak` file is added as the next patch PAK.

Changing the method converts installed replacements; **Undo** converts them back.

### Priority and conflicts
- On the Installed page, the **Installed** type lists replacements and tool mods in priority order. Higher in the list wins files that several mods share. A new install goes to the top; **Move up** and **Move down** change the order.
- Before you install, **REPLACES** in the inspector and the footer name any installed mod for the same outfit. The link **Uninstall 'name'** removes that other mod.
- The **N shared files** chip on the Installed page shows which mods share files and which one wins.
- Installing another option set of a mod you already installed replaces it in place.

### The Health report
The check runs by itself, and the footer says "Ready to install" or "Cannot install" with the first problem. Click **Health** for the full report: problems, other mods for the same outfit, warnings, fixes and notes. **Re-check** runs it again.

### Reapply and uninstall
**Reapply all** (Installed page, or **Settings → Game files & replacements**) copies every replacement back after an SF6 update or a Steam file check. Uninstall restores the original files exactly and keeps no copy, so reinstall from the Mod library to get the mod back.

## Automatic fixes
> What the Manager repairs in older mods as they install, and the one fix you turn on yourself.

Mods are made for the game as it was on the day they were published. Every **Replace original** install gets these fixes in the copy it installs. The mod in your library is never changed.

- Colour files with a type signature from before a game update get the current signature.
- Colour files in the 2023 layout are converted to the current layout.
- Textures with the old `.tex.143230113` suffix, and meshes with an older suffix, are renamed to the current version.
- Texture mip tables with a wrong row pitch are repaired.
- Outdated select-screen colour swatch files are rebuilt from the game's current file, keeping the mod's colours.

### Where you see them
The footer counts them ("3 auto-fixed"). The Health report lists them under **Fixed automatically** and per file. On the Installed page, a replacement's details list each file with its fix.

### When a file can't be fixed
The Health report shows a warning instead, such as "Broken texture mip table…; could not be repaired, may not load" or "Older layout of … left as it is; its colours may not load". Warnings don't block the install. They tell you which part may not show in game.

### Extra outfits
Slotsloader repairs extra outfits itself, at every SF6 start where something changed, in the files it writes to its own PAK. The Manager also rebuilds outdated select-screen colour swatches in the copy it installs. The mod in your library is never changed.

The Manager doesn't change mods your external mod manager installs; any fixes they get come from that manager. The colour swatch rebuild and the character-swap repair below are only in SLM.

### Character-swap repair (off by default)
**Settings → Game files & replacements → Try to fix character-swap mods broken by game updates** applies known fixes to old character swaps: the scene signature the Alex update changed, and some Mai and Elena physics files, rebuilt from the current game. Only files that match a verified older version are changed. Replace original and Slotsloader both follow this setting. Turn it on, then install (or reinstall) the swap mod. While it is off, nothing is changed and no swap warnings show.

## Extra outfits
> New costume slots through Slotsloader, with the original outfits left as they are.

### Before you start
Extra outfits need REFramework and Slotsloader in the game folder. In Extra outfit mode, the line under **INSTALL AS** says when either one is missing or out of date. Click the line to install it; if your external mod manager installed REFramework, the line says to install or update it there. **Settings → Game files & replacements → REFramework and Slotsloader** shows both as well.

### Slots and the outfit name
- **CHARACTER** is the fighter the mod is for. The Manager doesn't move a mod to another fighter.
- **OUTFIT NAME** is the slot. Each name is one folder in `reframework\costume_mods\<Character>\<Outfit name>`.
- Installing under a name that fighter already has updates that slot. A new name adds a slot, so several variants of one mod can sit side by side. Hover over the name or **Install** to see which ("Updates this slot." or "Adds a new slot.").
- The Manager suggests a name from the mod and your ticked options. Type your own to keep it.
- **Reset options** puts the mod's default ticks back.
- Slotsloader numbers new slots the next time SF6 starts. Until then the Installed row says "Restart SF6 to load".
- Slotsloader shows up to 100 extra outfits per fighter.

### Install all variants
For an archive with several options, **Install all variants** copies the whole archive into `reframework\costume_mods\<Character>\`, as if you dropped it there yourself. Slotsloader then builds usually one outfit per combination (up to 50 per mod). Mods made for an external mod manager get one per shape option.

### Add to outfit
**Add to outfit…** copies only the ticked options (one hair option of a bundle, for example) into an extra outfit the Manager already installed. Files with the same names are replaced and nothing else changes. Files made for another outfit slot can't go on it, so those outfits can't be picked.

### Edit options
On the Installed page, select an outfit and click **Edit options**. It opens in the Mod library with its options ticked. Change them and click **Install** to update that slot. Two or more slots from one mod show as one row with "N slots"; click its arrow to see each slot.

## Presets
> Save your whole setup as a list and bring it back later.

On the Installed page, click the **Presets** icon. Type a name, choose **Extra outfits**, **Replacements** or **Both**, and click **Save**. A preset saves everything enabled of that kind, whatever your search, filters or selection. Disabled outfits are left out.

### What a preset holds
- A list, not copies: for each outfit and replacement, the library mod it came from, its options, its outfit name and its order. Most presets take a few KB.
- Only items with no library mod are copied into the preset: a manual drop, or a mod you deleted, renamed or moved outside the Manager. After **Save**, the status line says how many items were copied and their size, and the preset's details list each one with the reason ("manual drop", "not found at …").
- A manual drop identical to an archive in your libraries counts as that library mod. A mod you renamed outside the Manager is found again by its files, when exactly one library mod with a similar name holds them.

### Restore
Select the preset and click **Restore**. What is missing is installed again from your libraries, and what is already installed is skipped. Replacements go back in their saved order. Anything that can't come back (a mod no longer in your libraries, a different outfit in its place, a failed install) is listed under the preset, and the rest goes ahead. Restoring replacements needs SF6 closed. Restart SF6 afterwards.

**Delete** removes a preset after one question. It can't be undone.

Presets live in `reframework\.slotsloader-installer-presets` in the game folder. Keep that folder if you move the game.

!! OLDER PRESETS: Presets saved before 1.0.8 are full copies and show "full copy" with their size. Save a new preset and delete the old one to free the space.

## Updates
> How the Manager and Slotsloader stay up to date.

Once a day the Manager checks its official GitHub releases for a new Manager or Slotsloader. When there is one, a notice appears at the bottom right with **Update**, **What's new**, **Skip this version** and ✕. Nothing installs until you click **Update**.

- **Manager update:** downloads the release, checks its SHA-256, replaces the program files and restarts. Your settings and records in `Data` stay. The previous version is kept, and `Slotsloader CLI.exe update-rollback` puts it back.
- **Slotsloader update:** offered for a Slotsloader the Manager installed. It goes through the health check and installs over the old one at the same priority. Close SF6 first.

**Settings → Updates** has **Check automatically (daily)**, **Include test builds** (pre-releases) and **Check now**.

# When things go wrong

## Extra outfits don't show
> Work down this list. Most cases are a missing tool or a game that hasn't restarted.

### Check the Installed page first
Select the outfit. Its row says what Slotsloader did with it:

- "Restart SF6 to load" or "Start SF6 to load": Slotsloader builds outfits when SF6 starts. Close SF6 completely and start it again.
- "Loaded" with an outfit number: it is in the game. Look for that number on the character select screen.
- "Not loaded by Slotsloader": Slotsloader skipped it. Open `SF6_CostumeLoader.log` in the game folder. Lines starting with `WARN` or `ERROR` name the mod and the reason.
- "Disabled": select it and click **Enable outfit**.
- "Slotsloader result unreadable": start SF6 once more. If it stays, ask for help and share the log.

### Common causes
- REFramework is missing, out of date, or SF6 hasn't started with it yet. The line under **INSTALL AS** (Extra outfit mode) says so. Click it to install or update. If your external mod manager installed REFramework, the line says to install or update it there.
- Slotsloader isn't installed: the line reads "Slotsloader isn't installed, so extra outfits won't load". Click it.
- You used **Gear → Restore → Remove this Manager's mods**. It also removes the REFramework and Slotsloader the Manager installed. **Undo** brings them back, but only while your external mod manager has no mods installed. Otherwise click the line under **INSTALL AS** to install them again. See [[Restore and undo#Undo]].
- Slots built on Outfit 2, 3 or 4 only show if you own that outfit. Outfit 1 and Drive Tech (C5) slots always show.
- The wrong **CHARACTER** was picked. The Manager doesn't move a mod to another fighter.
- A variant is turned off. An archive you dropped into `costume_mods` with several variants has a **VARIANTS** list in its details on the Installed page.
- A fighter has more than 100 outfits. Those past the 100th are left out and named in the log.
- Colour-only mods, and mods made only of textures with no model, give no outfit. They change an original outfit, which is what **Replace original** does.

## Game won't start or a black screen
> Start with the smallest fix you can undo, and go further only if you need to.

### Step 1: remove this Manager's mods
**Gear → Restore → Remove this Manager's mods** takes out every replacement and tool mod the Manager installed and restores the original files. **Undo** in the status bar puts them all back. If SF6 starts now, press **Undo**, then on the Installed page **Disable** the replacements one at a time until you find the one that breaks it. Each Disable has its own Undo.

Keep the Manager open and make no other change before you press Undo: the Undo ends at your next change or when you close the Manager. While your external mod manager has mods installed, Undo waits ("Can't undo yet: …").

This also removes REFramework and Slotsloader if the Manager installed them, so extra outfits don't load until you press **Undo**.

### Usual suspects
- A PAK mod made for an older game version can stop SF6 from starting. Uninstalling it puts the game back.
- A **Leftover PAK** row on the Installed page: a patch PAK no mod manager lists, which SF6 still loads. See [[Leftover PAKs]].
- A missing number in the patch PAKs (`re_chunk_000.pak.patch_NNN.pak`) can stop the game at a black screen. The Manager fills such a gap with an empty placeholder at start-up and on refresh, while SF6 and the external mod manager are closed, and says so in the status line.
- Loading never finishes on the character select screen: an extra outfit may be the cause. Select it on the Installed page, click **Disable outfit**, and check `SF6_CostumeLoader.log` in the game folder.
- SF6 was just updated: see [[After an SF6 update]] first.

### Step 2: restore the game to vanilla
If it still won't start, **Gear → Restore → Restore game to vanilla…** removes all mods (your external mod manager's too), every patch PAK and the `natives` folder. Extra outfits stay, and so do REFramework and Slotsloader if the Manager installed them. A REFramework or Slotsloader your external mod manager installed goes with its mods; install them again from the line under **INSTALL AS**. It has no Undo. See [[Restore and undo#Restore game to vanilla]].

### Step 3: verify the game files
If the Manager says some game entries "couldn't be proven", or the game still fails, verify SF6's files in Steam. Then **Reapply** your replacements.

## After an SF6 update
> Reapply your replacements when the game files change.

An SF6 update or a Steam file check puts the game's original files back, so your replacements stop showing in game. Extra outfits work differently: Slotsloader rebuilds its data from the game's own files at the next start.

### What you'll see
- The Installed page shows a banner: "Game files changed — reapply replacements", or "SF6 was updated since replacements were applied — run a health check on them".
- Every other page shows the same notice at the bottom right, with **Reapply**, **Show details** and ✕. ✕ hides it until something else changes.
- **Launch SF6** warns you first. Click it again within 15 seconds to start anyway.

### What to do
- Close SF6. While it runs, the notice says "Close SF6 to reapply your replacements…". With the PAK method, close your external mod manager too.
- Click **Reapply**. It copies your replacements back into the game files.
- Nothing is reapplied until you click.
- If a mod looks wrong after the update, open it in the Mod library with **Replace original**: the [[Replace original#The Health report|health check]] runs again against the new game files.

### Other notices
- "N game entries left invalidated by another tool" with **Repair**: game entries that no installed mod owns were left invalidated. **Repair** restores the ones it can prove. For the rest, verify SF6's files in Steam.
- Character swaps broken by the update: try **Try to fix character-swap mods broken by game updates** (see [[Automatic fixes#Character-swap repair (off by default)|Character-swap repair]]).
- The game won't start after the update: see [[Game won't start or a black screen]].

## Replace original is off
> Why the Manager won't replace files right now, and how to turn it back on.

### Your external mod manager has mods installed
The footer reads **Replace original is off**, with "The external mod manager has N mods installed · uninstall them there or use Extra outfit". The Health report adds "…so only Extra outfit, Slotsloader and REFramework install here." In Extra outfit mode, the **INSTALL AS** line shows "Replace off" before you even switch.

This keeps two managers from changing the same game files. While it lasts:

- Extra outfits, Slotsloader and REFramework still install. REFramework that your mod manager installed is updated there.
- Replace original, PAK mods and other tool mods wait. So do enabling, moving and **Reapply**.
- Disabling, uninstalling and **Remove this Manager's mods** still work for the Manager's own mods, but the Undo of Remove this Manager's mods waits until that manager's list is empty.

To turn it back on, uninstall every mod in your external mod manager. The Manager doesn't uninstall them one by one (only **Restore game to vanilla** removes them all). Once that manager's list is empty, Replace original comes back on its own. If a mod still shows in game afterwards, see [[Leftover PAKs]].

### Your external mod manager is open
The header shows "● Mod manager running". While your replacements use the PAK method, PAK installs and every change on the Installed page wait: "Close the external mod manager first: with the PAK method, both apps edit the same PAK." Extra outfits, loose-file replacements and new tool-mod installs go ahead. Close it and try again.

### SF6 is running
The header shows "● Game running". In Replace original mode the footer says "Close SF6 to install": replacements and tool mods wait until SF6 is closed, except loose-file changes to the `natives` folder. Extra outfits and variant choices can change while it runs; they load at the next start.

## Leftover PAKs
> Patch PAKs that no mod manager lists, and how to remove them safely.

A patch PAK is a file named `re_chunk_000.pak.patch_NNN.pak` in the game folder, and SF6 loads every one of them. Sometimes one stays behind after you uninstall everything in your external mod manager, so the "uninstalled" mod still shows in game.

### How to spot one
The Installed page lists each one as **Leftover PAK**, with "not listed by any mod manager", its size and its file name. It shows under **All** and **Installed**, and under the **Manual** filter.

### Remove it
- Close SF6 and your external mod manager.
- Select the row and click **Remove PAK**. There is no question: the click is enough.
- The status says "✓ Removed leftover PAK …" with **Undo**.
- If a higher-numbered patch PAK exists, an empty placeholder keeps the removed PAK's number, so the PAKs after it still load. Other PAKs are never renumbered.

### Undo
**Undo** puts the PAK back while its number is still free or held by the placeholder. If another PAK has taken the number meanwhile, it can't go back. When the Undo ends unused (your next change, or closing the Manager), the removed PAK goes to the Recycle Bin.

!! CHECK FIRST: A PAK you copied into the game folder by hand looks exactly the same. Nothing is removed without your click, so remove only PAKs you don't want.

If your external mod manager's list can't be read, nothing counts as leftover.

## RAR or 7Z mods won't open
> How the Manager finds 7-Zip, and what to do when an archive won't read.

ZIP files and folders work without 7-Zip. RAR and 7Z archives are read with 7-Zip, and the Manager comes with its own copy in the `7-Zip` folder beside its executables.

### Which 7-Zip is used
**Settings → General → 7-Zip (RAR and 7Z)** can stay empty ("Automatic"). The line under it says which 7-Zip runs:

- A path you chose yourself always comes first.
- Then 7-Zip installed with its installer, found through its registry entry or `Program Files\7-Zip` ("Using your installed 7-Zip"). Choose a portable copy in Settings.
- An installed 7-Zip older than the Manager's copy gives way to it, because 7-Zip before 15.06 can't open RAR5 archives.
- Otherwise the copy that comes with the Manager ("Using the 7-Zip that comes with the Manager").

### Error messages
- "RAR and 7Z archives need 7-Zip, and none was found…": the `7-Zip` folder is missing. Extract the release zip again and keep `7-Zip` (with `7z.exe`, `7z.dll` and `License.txt`) beside the executables, or choose `7z.exe` in Settings.
- "Not found at that path · clear the field to find 7-Zip automatically": the path in Settings is wrong. Clear the field.
- "Compressed ZIP data is corrupt…" or a size mismatch: the download is damaged. Download the mod again.

### Other cases
- An archive that needs a password can't be opened. Extract it yourself and put the folder in one of your mod libraries.
- While the library is being read, a row shows **Reading…**. Search `is:unreadable` to list the mods the Manager couldn't read.

## Restore and undo
> What each undo and restore option does, from the lightest to the full reset.

### Undo
Many actions happen at once and show **Undo** in the status bar: **Disable** and **Enable** of a replacement or tool mod, **Move up** and **Move down**, **Remove this Manager's mods**, changing the replacement method, turning the loose file loader on or off, **Remove PAK**, and turning a variant on or off. An Undo lasts until your next change or until you close the Manager. If it can't run yet, it says why ("Can't undo yet: …") and stays.

**Uninstall has no Undo.** It keeps no copy, so reinstall the mod from the Mod library. An outfit or replacement with no library source (a manual drop) goes to the Recycle Bin instead. If the Recycle Bin can't take it, an outfit stays installed and a replacement's files are deleted; the status line says which. Deleting a mod in the Mod library also moves it to the Recycle Bin.

### Remove this Manager's mods
**Gear → Restore → Remove this Manager's mods** removes every replacement and tool mod the Manager installed and restores the original files. That includes REFramework and Slotsloader if the Manager installed them, so extra outfits stop loading until you press **Undo**, which puts everything back in its order. Undo waits while your external mod manager has mods installed, and ends at your next change or when you close the Manager. Your extra outfits and your external mod manager's mods are not touched. The same action is **Restore original game files** on the Installed page banner and in **Settings → Game files & replacements**.

### Restore game to vanilla
**Gear → Restore → Restore game to vanilla…** asks first and has no Undo. Close SF6 and your external mod manager first. It removes:

- every mod this Manager installed, except its REFramework and Slotsloader;
- every mod your external mod manager installed (its list is cleared);
- all patch PAKs (`re_chunk_000.pak.patch_NNN.pak`) and the `natives` folder.

It then puts back the game entries mods invalidated. Your extra outfits stay, and your mods stay in your libraries to reinstall. A REFramework or Slotsloader your external mod manager installed is removed with its mods, so extra outfits need them installed again.

!! NO UNDO: Restore game to vanilla can't be undone. Try [[Restore and undo#Remove this Manager's mods|Remove this Manager's mods]] first.

### When another tool changed a file
If another tool replaced or deleted a file the Manager installed, uninstalling or restoring asks "Game files changed by another tool". **Restore original** brings the game's file back, **Keep current file** leaves the other tool's file, and **Cancel** changes nothing.

# Reference

## Files and folders
> Where the Manager keeps its data, and what it puts in the game folder.

### The Manager's folder
- `Slotsloader Manager.exe` and `Slotsloader CLI.exe`, each with its `.exe.config` beside it.
- `7-Zip`: the 7-Zip copy used for RAR and 7Z archives.
- `Slotsloader`: the Slotsloader build the Manager installs, so installing it works offline.
- `CLI.md`: the command-line guide.
- `Data`: your settings and records (below). Keep it when you update.

You can move or rename the whole folder, `Data` included, while the Manager is closed. Never move `Data` on its own.

### The Data folder
Open it with **Settings → General → Open Data folder**.

- `settings.json` (game folder, 7-Zip, mod libraries), `view-settings.json` (window and filter choices) and `replace-settings.json` (replacement settings).
- `log.txt`: the Manager's log. Share it when you ask for help.
- `history.jsonl`: the timeline on the History page.
- `replace-state.json` and `replace-store`: your installed replacements and their stored copies. The store can be moved in **Settings → Game files & replacements**.
- `replace-backups` and `pak-backups`: the original game files and PAK tables that uninstall puts back.
- `downloads`, `leftover-paks`, `pak-repairs`, `portraits` and `updates`: downloads, removed leftover PAKs kept for Undo, repair records, fighter portraits and the previous version after an update.

### In the game folder
- `reframework\costume_mods\<Character>\<Outfit name>`: extra outfits. Disabled ones wait in `reframework\.slotsloader-installer-disabled`.
- `reframework\.slotsloader-installer-presets`: your presets.
- `reframework\data\SF6_Costumes_Data`: Slotsloader's data, including `outfits.json`, `variants.json` and `variant_choices.json`.
- `natives`: loose replaced files.
- `re_chunk_000.pak.patch_NNN.pak`: patch PAKs from PAK mods, Slotsloader's outfit PAK and other tools. SF6 reads them in number order and stops at the first missing number.
- `dinput8.dll` (REFramework) and `amd_ags_x64.dll` (Slotsloader). The files they replace are backed up and put back on uninstall.
- `SF6_CostumeLoader.log`: Slotsloader's log.

## Command line
> Check and manage mods without opening the window.

`Slotsloader CLI.exe` sits beside the Manager and uses the same `Data` folder, settings and safety checks. Run it from PowerShell in the Manager's folder, for example `& '.\Slotsloader CLI.exe' replace-status`. The full guide is `CLI.md`, beside the exe.

### Useful commands
- `list-options <mod>`: the mod's option IDs, defaults and file counts.
- `test-mod <mod>`: checks a mod's selection without installing anything. Without `--select` it uses the mod's default options; pick a bundle's options with `--select <ID>`.
- `replace-status`: game files, installed replacements, patch PAKs (leftovers included) and any problems. It changes nothing.
- `replace-plan <mod>`: the Replace original health check.
- `replace-install <mod>` and `replace-uninstall <name>`: install or uninstall a replacement.
- `replace-reapply`: the same as **Reapply** after an SF6 update.
- `replace-restore-all --yes`: the same as **Remove this Manager's mods**, but with no Undo.
- `replace-vanilla --yes`: the same as [[Restore and undo#Restore game to vanilla|Restore game to vanilla]].
- `pak-repair`: lists game entries left invalidated by another tool; add `--yes` to repair the proven ones.
- `variants`: lists the variants of archives you dropped into `costume_mods`; `--set` turns one on or off.
- `update-check` and `update-rollback`: check for updates, or go back to the version before the last update.

`--game <folder>` picks the game folder; without it the one in Settings is used. Exit code 0 means success, 1 refused or failed (the reason is printed), 2 invalid arguments.

## Getting help
> Where to ask, and what to send so someone can help quickly.

### Ask on Discord
**Settings → About → Discord** (under **Community & support**) opens the invite. The Discord icon at the right end of the status bar does the same. The **Source code** card leads to the GitHub page, where you can report issues.

### What to share
- What you did, what you expected, and the mod's download link.
- The Manager's log, `Data\log.txt` (**Settings → General → Open Data folder**).
- For extra outfits, Slotsloader's log: `SF6_CostumeLoader.log` in the game folder. Lines starting with `WARN` or `ERROR`, and the `check:` lines, show what it found and repaired.
- For a replacement, the Health report (click **Health**) or a screenshot of it.
- For a banner on the Installed page, click its text to see every detail. Ctrl+C copies them.
- The output of `Slotsloader CLI.exe replace-status`, which describes your game files without changing anything.

Before you post, remove anything personal from paths, such as your Windows user name.

### The Assistant tab
Turn it on with **Settings → General → Show Assistant tab** and save your API key in the same card. Then open **Assistant** (Ctrl+3). It answers questions about the Manager and Slotsloader from this documentation, reads your setup, and can search, inspect and plan installs. Every change it proposes waits in the **Plan** until you click **Apply selected**.
