# Check a mod without opening Slotsloader Manager

`Slotsloader CLI.exe` is the command-line companion of **Slotsloader Manager** (2.4.0; called SlotsLoader Manager until 2.5.8 and SF6 SlotsLoader Installer before 2.1.0). It is built from the same source and uses the same `Data` folder beside it.

Keep `Slotsloader CLI.exe.config` beside `Slotsloader CLI.exe`. In PowerShell:

```powershell
& '.\Slotsloader CLI.exe' list-options '<library>\My mod.zip'
& '.\Slotsloader CLI.exe' test-mod '<library>\My mod.zip' --json '.\check.json'
```

`list-options` prints component IDs, defaults and file counts. `test-mod` builds the GUI's composition, checks required choices and character identity, reads the selected payload, validates ZIP CRC/size limits, and records SHA256 hashes. It does not install anything. RAR/7Z use an installed 7-Zip, else the copy in the Manager's `7-Zip` folder; override with `--sevenzip 'C:\path\7z.exe'`.

For bundles, choose options explicitly with repeated `--select '<ID>'`, or save an ordered array of IDs and pass `--selection '.\selection.json'`. The empty string is a valid root ID. Later options override earlier ones; main files apply first. An installed `.slots-installer.json` is accepted if its source matches. Missing options and incomplete required groups fail; defaults do not guess a skin/outfit choice.

Use `--character 'A.K.I'` or `--character esf013` to resolve ambiguity; this does not retarget assets. Use `--json -` for machine-readable stdout. A report file must be new and outside the source/game. Exit codes: **0** passed, **1** validation failure, **2** invalid arguments, **130** Ctrl+C cancellation.

## Run the actual loader

```powershell
& '.\Slotsloader CLI.exe' test-loader '<library>\My mod.zip' `
  --selection '.\selection.json' `
  --loader 'C:\Tools\costume_loader.exe' `
  --game-root '<game folder>' `
  --sandbox 'C:\SF6-tests\new-check' `
  --json '.\native-check.json'
```

The sandbox must be **new**, separate from the mod/game, and free of links/junctions. The tool copies the composition and installed loader metadata there, then runs the specified native executable with the original base PAK as read-only input. It retains generated PAKs, `outfits.json`, logs and a native report. The game is not launched; its loader is not replaced.

The report names the actual loader build/hash and omitted DLC PAKs. Input checks compare original metadata hashes, sandbox asset hashes, and the base PAK's header/index hash, length and timestamp. The base comparison is not a full-content hash. A pass means that loader accepted the isolated composition; it does not prove visual correctness, every dependency, or compatibility with all other active mods.

The current native executable accepts paths through the Windows ANSI code page into 260-character buffers. The CLI rejects unsupported sandbox/base-PAK paths up front. Ordinary `test-mod` and install paths retain Unicode/long-path support.

## Replace original from the command line (2.0.0)

These commands use the same engine, `Data` folder (beside the executable) and safety checks as the app window:
- Every change refuses while SF6 runs, except loose-method changes to `natives` files only. Since 2.1.0 an open external mod manager only blocks PAK method changes (PAK entries or a conversion to the PAK method: both managers edit the same PAK); loose and tool-mod changes and REFramework config edits go ahead, with its `installed.ini` read again first.
- An interrupted change is completed or rolled back before the next one.
- `--game` defaults to the game folder in Settings.

```powershell
$game = '<game folder>'
& '.\Slotsloader CLI.exe' replace-status --game $game
& '.\Slotsloader CLI.exe' replace-plan '<library>\Cammy C2 Barefoot Colors.zip' --game $game --method pak
& '.\Slotsloader CLI.exe' replace-install '<library>\Cammy C2 Barefoot Colors.zip' --game $game --method pak --name 'Cammy colours'
& '.\Slotsloader CLI.exe' replace-uninstall 'Cammy colours' --game $game
& '.\Slotsloader CLI.exe' replace-restore-all --game $game --yes
& '.\Slotsloader CLI.exe' replace-method --game $game                     # show the method and loader state
& '.\Slotsloader CLI.exe' replace-method --game $game --set loose --yes   # converts installed replacements
& '.\Slotsloader CLI.exe' replace-method --game $game --set auto          # back to the automatic default
& '.\Slotsloader CLI.exe' preset-map '<manager folder>\Games\SF6\Presets\zz.prt'
```

| Command | What it does |
|---|---|
| `replace-status` | Prints the base PAK (entry count, fingerprint, zeroed entries, and the entry-table SHA-256 with its 16-character prefix), each DLC PAK's zeroed entries, the `natives` folder file count, the method and REFramework loader state, tool status, external mod manager detection, installed replacements and game-file status. Since 2.1.0 also `App data:` (the Data folder in use), `Backups: N recorded, M missing on disk (first: <path>)` with the stored copies that are missing, and `Relocation:` (recorded paths that point to a previous app folder and resolve in this Data folder, paths missing in both places, or a copied folder that is still in use). Read-only: it changes no record (it may add one line to `Data\log.txt`). |
| `replace-plan <source> [--select <id> ...]` | The health check. Lists what the mod replaces, the fixes, warnings (since 2.0.1 including other installed mods for the same outfit or files, with counts) and every PAK row the PAK method would invalidate. It only writes a temporary folder; `--json <new.json\|->` saves the report. |
| `replace-install <source> [--select <id> ...] [--name <name>] [--yes \| --no-loader]` | Installs a replacement or tool mod. `--method loose\|pak` applies when nothing is installed yet. With the loose method, REFramework's loader off and `natives` files in the selection, it fails unless `--yes` turns on `LooseFileLoader_Enabled` (recommended; the config is backed up first) or `--no-loader` installs with the loader off (the game ignores those files until it is turned on). Tool-only mods need neither (2.1.0). |
| `replace-uninstall <id prefix\|name>` | Uninstalls one replacement and restores what it replaced. Since 2.0.1 a copy is kept in `<store>\.removed` (restore it in the app: gear → Recover removed outfits). |
| `replace-reapply` | Re-copies files and re-invalidates entries after a game update or a Steam file check. |
| `replace-restore-all --yes` | Removes every replacement and tool mod this app installed and restores the original PAK entries and files. |
| `replace-method [--set loose\|pak\|auto] [--yes]` | Shows or changes the method; `auto` returns to the default (2.4.1: loose while REFramework's loose file loader is on, else the PAK method). With replacements installed, changing it converts them: PAK → loose restores the entries the app zeroed, and loose → PAK zeroes them. |
| `preset-map <file.prt>` | Shows how an external preset (`.prt`) maps to the configured libraries. Read-only. |
| `variants [--set <source> <label> on/off]` | (2.5.0) Variants of archives and folders dropped into costume_mods: list, or turn one on or off (see below). |
| `pak-repair [--yes]` | (2.4.2) Game entries another tool left invalidated (zeroed PAK entries no installed mod owns). A dry run lists each with the hash it would restore and its proof (TOC backup, the mod manager's archive list, or hash(path) between the neighbouring entries); `--yes` restores the proven ones. Refused while SF6 or the mod manager runs. |

Exit codes: **0** success, **1** refused or failed (the reason is printed), **2** invalid arguments, **130** cancelled.

## External mod manager mods and files other tools changed (2.2.0)

```powershell
& '.\Slotsloader CLI.exe' manager-status                      # read-only: manager copies, installed mods (+ shared files), mods uninstalled here
& '.\Slotsloader CLI.exe' manager-uninstall "SF6 Mod Suite"   # 1.0.1: refused: "Uninstall it in the external mod manager." (exit 1)
& '.\Slotsloader CLI.exe' manager-restore "SF6 Mod Suite"     # undo of an uninstall an earlier version made here: files, backups and the installed.ini entry come back
& '.\Slotsloader CLI.exe' replace-uninstall "SF6 Slots Loader" --keep-current        # or --restore-originals
& '.\Slotsloader CLI.exe' replace-restore-original amd_ags_x64.dll                  # the genuine file behind a kept one
```

- 1.0.1: `manager-uninstall` refuses every mod (the external mod manager uninstalls its own), and while that manager has any mod installed `replace-install` refuses everything except Slotsloader (exit 1, "only Extra outfit, Slotsloader and REFramework install here"; 1.0.10: plain REFramework installs too unless that manager installed `dinput8.dll`). `manager-restore` needs the external mod manager and SF6 closed.

```powershell
& '.\Slotsloader CLI.exe' pak-repair          # dry run: game entries another tool left invalidated, each with its proof
& '.\Slotsloader CLI.exe' pak-repair --yes    # restore the proven ones (SF6 and the mod manager closed)
```

- `pak-repair` (2.4.2) lists every zeroed base or DLC PAK entry that no installed mod owns (not the Manager's, not a path in any external mod manager's `installed.ini`) with the hash it restores and how it was proved: `the TOC backup` (Data\pak-backups, same PAK identity), `the mod manager's archive list` (only when it agrees with every other entry) or `hash(path)` (the only known path whose hash lies between the neighbouring entries of the hash-sorted table; paths from the mod manager's installed.ini, its history and its Log.txt, the Manager's records and Slotsloader's vanilla index). Entries it can't prove are listed with the reason and left as they are; owned entries are listed as left alone. `Evidence:` says per PAK which sources match. Without `--yes` nothing changes (exit 0); `--yes` restores the proven entries (`Repaired 2 game entries.`), records them in `Data\pak-repairs\` and exits 1 when nothing can be proven (`Verify SF6's files in Steam`). `replace-status` reports such entries (`2 game entries left invalidated by another tool: re_chunk_000.pak #11886, …`) instead of `Status: OK`.
- When another tool replaced or deleted files this app installed, `replace-uninstall` and `replace-restore-all` without a flag list them with who changed them and exit 1 without changing anything. `--restore-originals` puts the genuine files (or the next replacement's copy) back and keeps the other files in `Data`; `--keep-current` leaves them and keeps the originals restorable. `replace-status` names the changer and lists kept files.
## Variants of dropped mods (2.5.0)

An archive or folder you dropped into `costume_mods` can hold several variants. Slotsloader lists them in `reframework\data\SF6_Costumes_Data\variants.json`; the Manager writes your on/off choices to `variant_choices.json` beside it, and Slotsloader applies them at the next SF6 launch.

```powershell
& '.\Slotsloader CLI.exe' variants --game $game                                       # every archive with variants: on/off and the slot each got
& '.\Slotsloader CLI.exe' variants --game $game --set 'Luke/X.zip' 'Blue' off         # turn one off (on turns it back on)
```

- `<source>` is the path below `costume_mods` as `variants` prints it (`Luke/X.zip`); `\` and any case are accepted. The label must be one Slotsloader lists.
- A variant is on unless the file says `false`. An archive's entry is removed when all its variants are on again; other archives' entries are kept. The file is written through a temporary file and a rename.
- Rows show `On at next launch` / `Off at next launch` until Slotsloader has applied a change, otherwise the slot (`Outfit V`).
- SF6 running: the file is still written and the line says `applies after SF6 restarts`.
- No `variants.json` (an older Slotsloader, or SF6 not started since): the list says so and exits 0; `--set` exits 1. An unknown source or label exits 1, bad arguments 2.

## The pinned Slotsloader build (2.3.1)

```powershell
& '.\Slotsloader CLI.exe' loader-fetch                                    # the libraries from Settings
& '.\Slotsloader CLI.exe' loader-fetch --library '<manager folder>\Games\SF6\Mods' --no-download
```

`loader-fetch` finds the one Slotsloader build the app installs (since 2.5.9 the newest published `loader-v*` release of ZZtaii/slotsloader-manager when the Manager was packaged; 2.4.3–2.5.8 `loader-v1.9.8`, 1.9.7 before):
0. the copy bundled with the Manager (`Slotsloader\<asset>` beside the exe, 2.5.9) if it verifies;
1. a file in a library (top level, any name) with that SHA-256 — only files of exactly that size are hashed;
2. else `Data\downloads\<asset>` if it still verifies (a copy that doesn't is deleted);
3. else it downloads it over HTTPS (system proxy, redirects only to HTTPS, 30 s time-out, Ctrl+C cancels) into `Data\downloads\<asset>.part`, stops if it grows past the pinned size, checks size and SHA-256 and only then renames it. `--no-download` skips this step.

It prints the pin, `Hashed N library file(s) of the pinned size.`, `Source: copy bundled with the Manager|library|cached download|download`, `Path:` and `SHA-256:`. It installs nothing and never writes into a library. `--library <dir>` (repeatable) replaces the libraries from Settings. Exit codes: **0** found or downloaded and verified, **1** not found with `--no-download` or the download failed (`ERROR: Couldn't download Slotsloader 1.9.7: <reason>. …`), **2** invalid arguments, **130** cancelled.
## Updates (2.4.0)

```powershell
& '.\Slotsloader CLI.exe' update-check                          # installed vs latest for Slotsloader and Slotsloader Manager
& '.\Slotsloader CLI.exe' update-check --include-prereleases    # test builds too
& '.\Slotsloader CLI.exe' update-install loader --yes           # download, verify, install Slotsloader in place (Replace mode)
& '.\Slotsloader CLI.exe' update-install manager --yes          # download, verify, replace the 7 app files (Data untouched)
& '.\Slotsloader CLI.exe' update-rollback                       # put back the app files kept before the last update
& '.\Slotsloader CLI.exe' loader-pak-remove                     # Slotsloader not installed: remove its leftover costume pak
```

- The source is the GitHub Releases API of ZZtaii/slotsloader-manager (one unauthenticated request). Slotsloader releases are tagged `loader-v*`, app releases `manager-v*` (since 1.0.0 the private builds' `installer-v*` tags are ignored). Drafts are ignored; pre-releases only with `--include-prereleases` (or the Settings option). A release is offered only when its zip (the app: the `*-portable.zip`) has a SHA-256: GitHub's asset digest, else a `SHA256SUMS.txt` in the release; otherwise it is listed as refused.
- `update-check` prints the installed version (Slotsloader: from the zip this app installed it from), the latest release with its tag, asset, size, SHA-256 and where the checksum came from, and refused releases.
- `update-install` without `--yes` only says what it would install and exits 1. Downloads go through HTTPS only, are checked (size and SHA-256) before use and kept in `Data\downloads`. Slotsloader is installed over the recorded one at the same priority (the genuine amd_ags_x64.dll backup stays); a Slotsloader that was never installed by the app gets the newest known release (never older than 1.9.7). The app update renames the running executables to `*.old` (removed at the next start), keeps the previous 7 files in `Data\updates\previous` and puts everything back if any copy fails.
- `loader-pak-remove` refuses while SF6 runs or while a Slotsloader is installed. `replace-status` lists every patch pak (`Patch PAKs: re_chunk_000.pak.patch_001.pak (generated by Slotsloader, 8724 entries, 1.7 GB)`, another mod's, the stage pak) and says when Slotsloader's pak is left over.
## After moving the app folder (2.1.0)

Move the whole folder (executables + `Data`) with the app closed, then run `& '.\Slotsloader CLI.exe' replace-status` from the new folder. Expect `Backups: N recorded, 0 missing on disk`, `Relocation:` naming the previous folder, and `Status: OK`. The corrected paths are saved by the first change (`replace-install`, `replace-uninstall`, `replace-reapply`, `replace-restore-all`, `replace-method --set`) or when the app window starts. A backup missing in both places is reported and kept as recorded; an uninstall that needs it refuses without changing anything.
