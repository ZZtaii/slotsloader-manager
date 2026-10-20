# Development, validation, and releases

Current source: **1.0.0, 2026-10-16**, the first public release (the same code as internal 2.5.9; the internal versions are in HISTORY.md). Earlier internal notes: **2.5.1, 2026-10-15** (wording: no other product named; see "2.5.1 checks"); 2.5.0 (see "2.5.0 checks": several slots per mod with suggested names, Reset options and Installed grouping; variant on/off for dropped mods; the loader pin is unchanged at 1.9.8); 2.4.3 (see "2.4.3 checks"); 2.4.2 (see "2.4.2 checks"); 2.4.1 (see "2.4.1 checks"); 2.4.0: update checks with in-app toasts and the Manager self-update (`UpdateCheck.cs`, `AppUpdater.cs`, `MainFormUpdates.cs`, shared HTTP in `SafeHttp.cs`), the Slotsloader costume pak clean-up (`LoaderCostumePak.cs`), no tooltips (`UiAudit.cs`) and the Assistant knowledge pack (see [2.4.0 checks](#240-checks)). 2.3.1: Install Slotsloader uses one pinned build from our GitHub release, found by SHA-256 in the libraries or Data\downloads, else downloaded and verified (`LoaderRelease.cs`, CLI `loader-fetch`; see [2.3.1 checks](#231-checks) and [Bumping the Slotsloader pin](#bumping-the-slotsloader-pin)). 2.3.0: the library Filters panel (one screen: fighter grid with portraits read from the game, Code tools / Misc / Installed / per-library tiles with Misc kind chips, no drop-downs; index v4), the joined INSTALL AS switch (forced Replace no longer sticks; Settings' Default install mode removed), Test in game removed (a leftover record is kept as a normal install at start-up; see [2.3.0 checks](#230-checks) and the architecture section). 2.2.0: external mod manager link and other tools' files. 2.1.0: the app is renamed **Slotsloader Manager** (GUI `Slotsloader Manager.exe`; CLI still `Slotsloader CLI.exe`; previously SF6 SlotsLoader Installer) and the app folder can be moved with its `Data` (`DataRelocation`, see [Moving the app folder](#moving-the-app-folder-210) below). Not deployed. 2.0.4 (deployed): tool keywords and character names also match by their start (three letters or more; `LibrarySearch.ToolKeyword`, `MainForm.SearchCharacterTerm`). 2.0.3 (deployed): tool-mod classification and search (`reframework`/`code`/`plugin`/`lua`/`dll`, `is:tool`, Filters Tool mod, row labels, library index v3, Assistant `kind: tool`), library right-click/footer uninstall of replacements and tool mods, and a themed message box that follows the Windows app theme (see the [architecture](ARCHITECTURE.md#tool-mods-library-uninstall-and-themed-dialogs-203) and the [UI reference](UI-REFERENCE.md)). 2.0.2: the Assistant offers only Claude Sonnet 5.5 (default) and Haiku 5.5 at Low/Medium/High effort (Opus, Fable, Extra high and Max removed; old saved choices load as Sonnet 5.5 / High), and the preview-image option and view_preview tool are removed (no images are sent). Everything else is 2.0.1, 2026-10-13: the health check (Replace mode) and the composition review (Extra outfit mode) run automatically in the background; conflict information (same outfit with or without shared files, other-area overrides, disabled mods, unknown loose files, the external manager's mod named) in the footer, inspector, report, Installed page and Assistant, with an inline recoverable Uninstall link and Undo; recoverable replacement uninstall; costume-aware library search (`cammy, c1`). See the [architecture](ARCHITECTURE.md#automatic-review-conflicts-and-costume-search-201) and the [UI reference](UI-REFERENCE.md). **Deployed 2026-10-14** to `<Manager folder>` (the 2.0.0 files were backed up first; `Data` unchanged). The source is also in the private repo `ZZtaii/slotsloader-manager` under `installer/`.

This is a personal Windows application, independent of Blender. Preserve both mod libraries, manual outfits, installed app `Data`, and the game loader. Development mutations belong in new workspace fixtures. **Replace-mode development never writes the real game**: every write test uses generated game folders with synthetic KPKA PAKs. The real game, libraries and external mod manager folders are read only by `--replace-real-check` and `replace-plan`/`replace-status`.

## Replace-mode checks (2.0.0)

- `--replace-check <fixture root>` runs the engine suite (29 checks since 2.1.0; the four relocation checks are described under [Moving the app folder](#moving-the-app-folder-210)) on generated game folders:
  - murmur3 vectors against the real index;
  - KPKA parsing and refusals (features != 0, truncated table, wrong expected bytes, size change);
  - the fixers;
  - the full PAK lifecycle with conflict/priority/disable/uninstall, verified byte for byte against the original game folder;
  - DLC rows and the patch-PAK block;
  - external mod manager ownership, including a missing `installed.ini`;
  - running SF6 or an external mod manager blocking changes, and the loose-method running exception;
  - journal recovery after a simulated crash at every step of an install and an uninstall;
  - game-update detection with Reapply, and rows restored by another tool;
  - tool mods with original backups, and protected paths;
  - loose ↔ PAK conversion;
  - REFramework config edits (single key, CRLF/LF, BOM) and log parsing;
  - the health check;
  - (2.3.0) a leftover Test-in-game record: kept as a normal replacement, logging keys restored, record deleted (`LeftoverTest`; Test in game itself is removed);
  - the crash-reporter rename;
  - TOC-backup fallback, external preset parse/map, and the CLI commands.
- `--replace-ui-check <fixture root>` (10 checks) runs a hidden native window: mode toggle, health check, footer/inspector fit at 1232×640 and 1110×610 in both modes, install and Installed rows (priority, read-only external manager row), the game-change banner with Reapply, Launch/running indicator, (2.3.0) no Test in game and the leftover record finished at start-up, the INSTALL AS switch (`ModeToggle`), Installed-page uninstall, the Settings tab and the Slotsloader notice. It saves renders into its fixture folder.
- `--replace-real-check <output>` (with `-RealArchives`) is **read-only**:
  - parses the real base PAK and decodes the reference colour file;
  - compares the fixers byte for byte with Slotsloader's C++ output on seven real outdated files;
  - runs a `replace-plan` dry run of `Cammy C2 Barefoot Colors.zip` against the real game and confirms the table hash and `natives` state are unchanged;
  - maps the six real external presets to the libraries;
  - reports tool and loader status.
- The `assistant` suite now has 30 checks; the new one covers `list_game_mods`, `install_replacement` and `change_game_mod` on a fixture game.

`test.ps1` runs `replace` and `replace-ui`; `-RealArchives` adds `replace-real` (`-Only replace,replace-ui,replace-real` for just these).

2.0.1 additions:
- `replace` (25 checks) adds conflicts (slot/stack/disabled/other-area/unknown loose/external manager text, update-in-place priority, no automatic removal), recoverable uninstall with byte-exact Undo (enabled state and priority), and the check cache (one extraction, identical results, invalidated by a changed archive).
- `replace-ui` (21 checks) adds the automatic check, debounce/supersede (`MainForm.ReviewTestDelay` keeps a run in flight), Install waiting for a fresh result, the report dialog (DWM dark-mode results, content sizing), the inline Uninstall link + Undo (no modal form, disabled while the mod manager runs), no removal by install, Installed-page conflict details and the Has conflicts filter, the external manager footer, the slot-mode automatic review with the duplicate note, and a cleanup that leaves the shared `Data` clean.
- `assistant` (31 checks) adds conflicts in `install_replacement`/`list_game_mods` results and titles, the prompt rule, and `search_mods` costume matching.
- `generated` adds the costume keys, cache v2 rescan-once and `cammy, c1` ranking checks (library regression).
- `replace-real` adds timing of the automatic check (Juri bundle and the largest RAR, cold and re-check) and the real-library costume search counts (read-only, `auto-check-timing.txt`, `costume-search.txt`).
- `--make-replace-fixture <new dir>` generates a synthetic game, fixture mods and a fixture mod manager copy for screenshots.

2.0.3 additions:
- `generated` (library regression) adds tool kinds from generated archives/folders (autorun Lua, plugin DLL in a wrapper folder, game-root DLL proxies, a nested modinfo layout, a costume mod with REFramework data that must not be a tool mod, a pure REFramework-files mod, `__MACOSX` junk), every tool keyword, `is:tool`/`type:tool`, combined terms, row labels, v3 cache reuse, a v3 record without tool kinds relisted once, and a v2 cache kept and relisted once.
- `layout` adds the tool terms, the Filters **Tool mod** item, row labels, the search tooltip and the 5-item library menu.
- `replace-ui` (22 checks) adds library uninstall of a replacement from the right-click menu and footer (enabled, recoverable, disabled with the reason while the mod manager runs, extra-outfit entries unchanged, footer fit with Uninstall shown).
- `ui-polish` adds the themed message box (both themes forced through `ThemedMessageBox.ForcedDark`, all six button sets, Enter/Esc/close box/mnemonics, icons, accessible names, Ctrl+C text without touching the clipboard, centring, sizing at 96/144/192/288 DPI without clipping, and an IL scan that no `MessageBox.Show` call remains).
- `assistant` (32 checks) adds `search_mods` `kind: tool`, tool words in `query` and `tool_kinds`/`tool_label`.
- `replace-real` adds the real-library tool-kind counts and the `reframework`/`code`/`plugin`/`lua`/`dll`/`is:tool` results (read-only, `tool-search.txt`).

The C++ cross-validation tool was built from the native loader source with MSVC into a scratch folder; it is not part of this project.

## Build

The x64 app targets .NET Framework 4.8 and C# 5. No NuGet, Node or Python dependency is needed. The Assistant calls the Anthropic Messages API over raw HTTPS (`System.Net.Http`) and stores its key with DPAPI (`System.Security`); `build.ps1` references both for both executables. The installed Windows framework compiler builds both GUI and console entry points:

```powershell
.\build.ps1 -OutputDirectory .\out\build
```

Run it from this folder; without `-OutputDirectory` it writes to `out\build`.

It writes `Slotsloader Manager.exe` (2.1.0–2.5.8: `SlotsLoader Manager.exe`; 2.0.4 and older: `SF6 SlotsLoader Installer.exe`) and `Slotsloader CLI.exe`; the CLI is compiled with `/define:CLI` only for its assembly title. Keep each executable's matching `.exe.config` beside it. The traditional `SF6-SlotsLoader-Installer.csproj` (file name kept) supports IDE navigation and MSBuild (`/p:ConsoleBuild=true` builds the CLI). MSBuild needs the net48 targeting pack for warning-free reference resolution; `build.ps1` remains the supported local build path. Do not rebuild a running executable.

`build.ps1` discovers `src/*.cs`. Core configuration, discovery and installation remain in `Core.cs`; package models/parsing, composition, archives and ZIP integrity are separate files. `MainForm.cs` owns screen behavior; `OptionTree.cs` paints complete buffered rows. `OperationContext.cs` carries cancellation/progress. See [architecture](ARCHITECTURE.md) and [API](../src/API.md).

## Repeatable validation

Use a **new** output directory. This runner builds both executables, writes isolated settings, creates fresh fixtures, and stops on failure:

```powershell
# From this folder; writes to a new out\tests-<time> unless -OutputDirectory is given:
.\test.ps1
# Also read real archives for the existing integration/UI suites:
.\test.ps1 -RealArchives
# Only some checks (real ones included), e.g. while iterating:
.\test.ps1 -Only filters,filter-real
# The visible real-app dialog check (opens windows on the screen):
.\test.ps1 -Dialogs
```

**Which runs (since 2.3.1).**
- `-Only <names>` (comma-separated or an array) runs just those checks, real ones and `dialog` included; an unknown name stops with the list of known names: `generated`, `outfit-sets`, `outfit-set-ui`, `layout`, `smoke`, `fields`, `ui-polish`, `assistant`, `assistant-ui`, `replace`, `replace-ui`, `filters`, `loader`, `variants`, `slots`, `real-backend`, `bundle-ui`, `bundle-layout`, `replace-real`, `filter-real`, `loader-real`, `bundle-polish`, `dialog`.
- The visible `dialog` check (`--dialog-check`) no longer runs by default or with `-RealArchives`; it runs with `-Dialogs` or when named in `-Only`.
- Policy: while iterating, run only the suites that cover the change through `-Only`; run the full `-RealArchives` once just before packaging a release (without `-Dialogs`); add `-Dialogs` only when `ThemedMessageBox` or the DPI/scaling code changes.
- Use an output folder outside `scratchpad`-like paths: the library-search regression in `generated` searches fixture paths for word starts such as `scr` and `plu`, so a parent folder named "scratchpad" or "plugins" makes it fail.

Generated checks cover required/implicit-root selections, independent main/overlay components, archive CRC/size bounds, library errors/cancellation, loader-result matching, recovery and CLI reports. Restricted tokens may not permit junction creation; that capability check is explicitly skipped. The retained October 13 permitted fixture run exercised junction rejection successfully.

`-RealArchives` adds the original 31 backend checks, six Juri selection checks and preview/layout checks. The existing real-input runner reads these archives without modifying them. Since 2.4.3 their paths, the game folder and the libraries come from `tests.local.json` beside test.ps1 (untracked; copy `tests.local.example.json`; `-LocalConfig <file>` picks another), passed to the checks in `SLOTSLOADER_TEST_CONFIG`; nothing personal is compiled in:

- `Archives.juri`: (Goth Juri C1) EA.zip
- `Archives.ingrid`: Ingrid C.C. Barefoot C1.zip
- `Archives.viperHair`: Viper - Hair - Manon C4.rar
- `Archives.numericOutfit`: 01outfit-1385-1-1-1703241589.zip

Individual flags remain `--self-test <workspace root>`, `--self-test-extra <fixture root>`, `--layout-check [Juri archive]`, `--field-check <new output directory>`, `--exercise-ui <Juri archive>`, and `--smoke-ui`. Reports are beside the executable and in its `Data`; field checks save their report and hidden renders to the requested directory. Headless modes bypass window preferences. The old real-input runner remains workspace-restricted; generated checks accept caller-provided fixture roots.

`--set-check <fixture root>` covers full preset and batch-removal lifecycles with generated managed/manual entries, opaque ZIP/RAR/7Z/PAK bytes, conflicts, cancellation and rollback. `--set-ui-check <new output root>` exercises native Ctrl/Shift selection, filter-limited Select all, action gating and the preset/confirmation dialogs with fixture data. Both are included in `test.ps1`. Native ListView test clicks must queue mouse down and up with PostMessage before pumping events: synchronous down alone can wait inside Windows' drag detection for a release that has not yet been queued.

DPI checks use actual device DPI. The October 13 desktop runs at 288 DPI (300%). Logical 1232x640 and 1110x610 clients become 3696x1920 and 3330x1830 physical clients; the larger logical 1460x920 case uses a canvas. Hover checks capture immediately after native enter/leave messages and check preview races, unchanged selections, keyboard and accessibility. A canvas pass does not substitute for native-window checks.

## Assistant checks

`--assistant-check <fixture root>` runs the engine suite (30 checks since 2.0.0) against a raw-socket loopback mock of `POST /v1/messages` with a fake key and generated fixtures. It covers:
- request shape for every model;
- strict schemas;
- the tool loop and byte-identical append-only replay;
- parallel results;
- no images: view_preview is not offered and a call to it is an unknown tool (2.0.2);
- invalid inputs;
- queue, approve, failure and auto-apply flows;
- refusal, max_tokens and pause_turn;
- retries (429 `retry-after`, 5xx/529 backoff, network errors) and 4xx errors;
- cancellation;
- the 40-call guard;
- model/effort switching;
- cost math;
- settings and DPAPI key storage;
- path scrubbing;
- the removed-set name regression;
- a scan of every written file for the key.

`--assistant-ui-check <fixture root>` drives the tab in a hidden native window with the scripted `AssistantDemo` conversation (13 checks). Both run in `test.ps1`. Neither needs the network or a real key.

`--assistant-live-check <fixture root> connect|full [model]` is **opt-in and never part of test.ps1**:
- It sends real requests with `ANTHROPIC_API_KEY` from the process environment.
- `full` runs one prompt against generated fixtures, applies the queued installs to the generated costume folder and sends a follow-up.
- Load the key into the child environment only. Do not print it, write it to a file or pass it as an argument. Remove it afterwards.
- Each run prints usage, the estimated cost and a key scan of its folder.

`--screenshot-available <png> "" assistant|assistant-demo|settings-key` renders the Assistant empty state, the scripted conversation (mock plus generated fixtures, nothing real written), and the Claude settings with a fake saved key.
## Headless mod/loader checks and screenshots

The [CLI guide](CLI.md) documents `list-options`, `test-mod`, `test-loader`, selections, JSON and exit codes. These bypass WinForms construction. GUI executables accept the same flags, but use `Slotsloader CLI.exe` for terminal output.

Native checks keep a new sandbox, copy selected assets/loader metadata, and read the installed base PAK through `--base-pak`. DLC PAKs are omitted and reported. This tests loader acceptance, not SF6 rendering. The loader is an explicit input, not bundled or replaced. The checked loader was a local build of the loader repository; the installed game may use a different build.

`--screenshot-available <new.png> [source] [installed|settings|merged|report|drop|min-<page>]` renders a hidden native window (since 1.5.0 via `PrintWindow` when it matches, so the dark title bar is captured; see `Data/screenshot-capture.txt`). `--screenshot` uses a larger canvas. These can read configured libraries and installed results; use dedicated development settings. `--open <source>` launches a visible app. During live review, use the computer-use skill and do not mutate real game outfits. Record hidden-render, canvas or live-capture provenance accurately.

## DPI, paths and responsiveness

- Ship the net48 target attribute, PerMonitorV2 config, DPI-aware layout and long-path manifest/config together. The old DPI-unaware/enhanced-GDI workaround is superseded by the requested native-text migration.
- Scale logical dimensions from a 96-DPI baseline. Fonts use points. Native edit/list fonts use the existing DPI-aware attachment. Never repeatedly scale already physical dimensions.
- Keep options in the managed buffered control. Native TreeView custom-draw suppression did not resolve the transient indicator erasure.
- Full-row pointer activation belongs to `OptionTree`, with disclosure arrows handled separately. Avoid a second toggle in the form's mouse handler. `LayoutChecks` covers padding, marker, text, trailing space, double-click and radio exclusivity.
- `PaddedTextField` centers an actual single-line Windows EDIT inside a padded frame; it retains native editing, selection and IME behavior. Field checks cover all five editable fields and dialog dimensions. Build modal controls from a suspended 96-DPI baseline before autoscaling.
- `assets/slotsloader.ico` is embedded as both the Windows executable icon and `SlotsInstaller.AppIcon`. `AppBranding` applies it to forms. The original PNG and image-generation prompt are retained in `assets`.
- Preview extraction and decoding run off the UI thread with bounded caches, decode pixel limits and generation checks.
- Long work supports cooperative cancellation/progress. The brief install commit disables cancellation for rollback consistency. Main-window closing behavior was explicitly excluded from this refresh.

## Deliver

Build/test in new output first. Package the two EXEs, configs and guides/reports; exclude Data, fixtures, mod assets and generated PAKs. Preserve historical releases.

For an installed update, back up and replace only selected EXE/config/docs in the installed app folder (`<Manager folder>`; named `SF6 SlotsLoader Installer` up to 2.0.4), following filesystem permissions. A 2.1.0 deployment replaces `SF6 SlotsLoader Installer.exe(.config)` with `Slotsloader Manager.exe(.config)`; remove the old GUI exe only after the new one starts. Preserve Data/unrelated files; never mirror the directory. Verify hashes afterward. Do not routinely change the installed loader or game files.

## 2.5.1 checks

- Suite `wording` (`--wording-check`): an IL scan of every string literal outside checks (allow-list of identifiers, persisted Data names, process/folder detection, old CLI/Assistant aliases), the CLI help and `manager-status` output, the Assistant's system prompt, knowledge pack, tool definitions and tool results, and the window's control texts on every page with an external-manager install present.
- Build step `tools\Check-Wording.ps1` scans README, docs and API.md (backticked identifiers excepted).

## 2.5.0 checks

- `--slots-check <fixture root>` (test.ps1 suite `slots`, 3 checks, hidden window, generated mod "Buff Leon as Luke C1" with Main files, Leon Hair and Red Jacket):
  - names: first install = the plain mod name, no hint; ticks unchanged after each install and the name then = the slot ("Updates this slot."); Leon Hair → "<mod> · Leon Hair" with "Adds a new slot. Keeps: <mod>."; a manual outfit with the next name → " 2"; two options joined with " + " and "+1 more"; the same ticks give the slot's name back; a typed name survives tick changes until cleared; Replace mode no hint; the tooltip text on the field and Install (read by reflection);
  - Reset options: hidden at defaults, shown when the ticks differ (both modes), no dialog, defaults back, name/hint follow; no tooltip outside `UiAudit.ToolTipAllowed` (window and IL);
  - grouping: "<mod> · 3 slots" row, the manual entry stays a row, expand, slot options, Select all visible / Clear, uninstall one slot + Undo, the group + Undo (label, three history events), two of three slot rows ("Uninstall 2 slots", only those removed, "Uninstalled 2 slots of <mod>", the last slot a plain row, Undo restores both), a single slot is a plain row. Renders `library-next-slot-<dpi>.png`, `installed-grouped-<dpi>.png`.
- `InstalledSetUiChecks` gives its two managed fixture outfits different sources (the same source would now be one group row).
- `--variants-check <fixture root>` (test.ps1 suite `variants`, 6 checks, generated game folder, no real game):
  - choices file: missing = all on; off stores only `false` under `version: 1`; other archives' entries kept (including a `true`); on again removes the entry; `Restore` round trip and `Restore(null)`; a differently cased key replaced by the loader's spelling; no `.tmp` left; an unlisted label refused;
  - malformed file read as all on and replaced by the next change; `version: 2` read as all on and never overwritten; non-bool values ignored; a missing or broken `variants.json` = `Present` false;
  - listing: archive and loose folder get variants; single variant, managed, unlisted entries don't; an older Slotsloader (`outfits.json` with two outfits, no `variants.json`) sets `VariantsUnsupported` only for that entry;
  - CLI `variants` in-process: list text, `--set` off/on (case- and `\`-insensitive source), SF6 running (fake probe) still writes and says so, exit codes 0/1/2, no `variants.json`;
  - Assistant `list_installed` rows and the too-old note; no variant tool;
  - hidden window (1232×640): panel, caption, rows/notes, height, no tooltip; click → file, status, Undo label; Undo; all-on removes the entry; SF6 running; Down + Space; accessibility; loose folder; hidden for single/managed; older Slotsloader line. Renders `installed-variants*-<dpi>.png`. Report `variants-check.txt`.
- Run while iterating: `test.ps1 -Only variants`.
- **Loose files never with the loader off** (`replace-ui` → AutomaticMethod): an explicit loose setting with the loader off installs with the PAK method (entries zeroed, setting kept, status ` · PAK method used: loose file loader off`); the health line is information.

## 2.4.3 checks

- **No personal paths** (build): `tools\Check-PublicPaths.ps1` runs in `build.ps1` after the knowledge pack and fails on the developer's own folders (a Windows user profile, the private workspace, the Steam library and mod manager folders, the installed Manager; the list is in the script, also matched doubled or forward-slashed) in src, docs, tests, assets, tools, README, test.ps1, build.ps1 and the knowledge pack. Its allow-list is empty.
- **Local test config:** real-archive checks read `tests.local.json` (`LocalTestConfig.cs`; keys in `tests.local.example.json`; untracked and in the publish exclusions). test.ps1 sets `SLOTSLOADER_TEST_CONFIG`, `SLOTSLOADER_WORKSPACE` (the old compiled-in workspace check) and `SLOTSLOADER_NO_DETECT=1`; `-RealArchives` without the file stops with what to do.
- **Detection** (`InstallDetection.cs`): `AppConfig.Load` without settings.json calls `AppConfig.Detected()`; checks replace `InstallDetection.SteamRoots` / `FluffyCandidates`.
- **Pin:** loader-v1.9.8 (see "Bumping the Slotsloader pin"); `loader-real` reports a library without the zip instead of failing.

## 2.4.2 checks

- **Old errors** (`ui-polish` → CheckOldErrors): the options tree paints with `package` null (was a NullReferenceException in DrawComponentNode, seen in ui-polish/bundle-polish logs since 2.2.0, caused by checks that null the package while the tree still showed one); `ConflictView.SetEntries` copies its input; a `Work` started without a synchronization context completes on the UI thread (`MainForm.OffUiThread`; the 2.3.0 "Collection was modified" trace came from a completion on a pool thread). The review worker uses the same guard.
- **Updates fixture** (`updates`): the catalogue's newer Manager is the running version + 1 (`UpdateChecks.Next`), so a version bump no longer breaks the suite.
- **External PAK-method uninstall and Repair** (`replace` → `FluffyPakChecks`, `replace-ui` → `FluffyPakUiChecks`):
  - Coverage: a 286 816-row sorted table with the real A.K.I / Chun-Li hashes (rows 11886, 140505, 244293); each proof source (TOC backup, archive list, installed.ini history, the mod manager's Log.txt, Slotsloader's vanilla index); the ambiguity and type-check refusals; owned rows; the invalidate and pakcopy uninstalls with Undo; both manager roots watched; the banner with Repair (no popup); `pak-repair` dry run and `--yes`.
  - Code: `PakRepair.cs` (proofs, known paths, `ArchiveList.bin` reader; per archive, u32 0, u32 count, then {u64 0, u64 offset, u64 hash}), `FluffyPakUninstall.cs`, `MainFormPakRepair.cs`.- **2.0.1 review follow-ups**, all six fixed:
  - `replace` adds 7 checks (`ReviewFollowupChecks.cs`): shared state reads (ReadWrite|Delete, a retry, 4 threads × 42 commits); copy not saved (a real file lock, retries, a stale record); pruning rules; pruning on disk; earlier store roots; the per-file check cache (folder source, a fake 7-Zip via `SourceReader.SevenZipRun`); Undo position.
  - `replace-ui` adds 4 (`ReplaceUiFollowupChecks.cs`): footer missing options in both modes, the Settings store note (`settings-store*-<dpi>.png`), the copy-not-saved status, and the window Undo below a newer install.
  - New Data records: `replace-trash-pending.json` (rebased like the journal) and `replace-state.json` `PreviousStoreRoots`.
  - Pruning (`ReplaceEngine.PruneRemoved`) runs from the window's `LoadGameRows(true)` under the game lock (30 days, 5 GB; never each mod's newest, a copy under 24 h, or one an Undo needs). `RestoreIndex` restores below newer installs.
## 2.4.1 checks

- **Space install** (`replace-ui` → SpaceInstall): sends WM_KEYDOWN through `ProcessCmdKey`, with lParam bit 30 marking an auto-repeat; `MainForm.FocusOverride` stands in for the focus hidden windows can't hold. It covers every excluded place (text fields, combos, buttons, toggles, Filters, toasts, other pages, busy, Install disabled), the tree's Enter toggle and a single install per press (`SpaceInstalls`).
- **Popups** (`ui-polish` → CheckPopupAllowList): `UiAudit.PopupCalls()` / `DialogCalls()` scan the IL of every method for `ThemedMessageBox.Show/Ask`, `MessageBox.Show` and `Form.ShowDialog`, and compare the result with `PolishChecks.PopupAllowList` / `WindowAllowList`. A new site needs an entry and a reason (permanent loss without Undo, or a decision only the user can make). The allow-lists are the inventory.
- **Undo** (`replace-ui` → UndoFlows): Disable, Move, the footer's one-click Uninstall (two records of one source), Restore original game files; Undo restores records, order and enabled state. SF6 running refuses inline. A new change ends the previous Undo. `MainFormUndo.cs` holds the pattern (`OfferUndo`; cleared when any Work starts, except the refresh that reports a result).
- **Method and switch** (`replace-ui` → AutomaticMethod, `replace` → LooseMethod, CliSameSource):
  - Automatic + loader off → PAK with entries zeroed; loader on → loose; an explicit loose setting is respected; banner Use PAK method; refused while SF6 runs.
  - A variant of the same source is one record at the same priority, and 2.4.0 duplicates collapse.
  - Slotsloader from another zip updates in place; uninstall still restores the game's amd_ags_x64.dll.
  - The switch lives in `ReplacePlanner.PrepareAndInstall` (app, CLI, Assistant). An in-place update gets a new record id at the same position.
- **Changed on disk** (`replace-ui` → SourceReload): edits a zip while it's open. Covers F5, window activation and Install-on-stale (a reload, not an install); option ids that changed map by name; the index re-reads the archive; Source changed + Reinstall; a deleted source.
  - `MainFormSourceWatch.cs`: `SourceStamp` is size + write time for archives, and file count + total size + newest write time for folders; `ChangedSources` compares the source's write time with the install time.
  - `SourceChangedException` is never shown: it triggers a reload.
- **Installed search** (`fields`): the shared × (`SearchClear.Attach`), Esc, and the library's matching (`MainFormInstalledSearch.cs`: rows matched as their library entry and as a synthetic entry built from name, character and file paths).
- **Presets, Show Assistant tab, sort, history, Delete**: see the suites `outfit-sets` / `outfit-set-ui`, `assistant-ui` (TabDefaults, HiddenTab), `filters` (CheckSorts …) and `filter-real` (SortReal), `history`, and the Delete checks.
## 2.4.0 checks

- `--updates-check <new dir>` (test.ps1 suite `updates`, 7 checks; a loopback GitHub-like server, no real network): release selection (highest tag per product, drafts skipped, pre-releases only with the setting, a release without a digest and without SHA256SUMS.txt refused and logged, the Manager's `*-portable.zip` verified through SHA256SUMS.txt); what is offered (newer only, never below the pin; the running Manager version); 404 and offline reasons and the 24 h throttle; a wrong published digest refused and deleted; the Manager self-update on a temporary copy of the app folder (stage with the FileVersion = tag rule, apply, `.old` clean-up and log at the next start, rollback, a failure after 3 copied files restoring every file, an unwritable folder refused, other Data untouched); the window (two stacked toasts above the status strip, keyboard/accessible names, no tooltips, What's new, SF6 running → "Close SF6 to update", the Slotsloader update in place with one confirmation and the genuine backup kept, ✕ back at the next check, Skip remembered, a silent logged 404, Settings → Updates "You're up to date." and the 404 reason; renders `toasts-stacked.png`, `settings-updates-up-to-date.png`); CLI `update-check`.
- `--replace-check` adds the Slotsloader costume pak (sole pak removed and the registry stamp cleared with slots kept; placeholder under another mod's pak; foreign, mod manager and stage paks byte-identical; restore-all; another mod's uninstall leaves it; orphan status, `replace-status` and `loader-pak-remove`) and the owned-outfit marker (below).
- `ui-polish` adds the app-wide no-tooltip audit (every method body scanned for ToolTip / ToolTipText; every page and Settings tab); `filters` and `layout` use the same audit. Menu checks use the menu's Opened event (a hidden test window is not the foreground app, so Windows may close a menu right after it opened).
- `assistant` adds the knowledge pack (sources, excluded text, deterministic rebuild, size), the cached prefix (system = instructions, then the pack with a 1 h breakpoint; tools + system byte-identical across requests and new conversations; tool order; usage counts logged) and `get_app_status`.
- `--loader-real-check` adds a read-only `replace-status` on the real game (patch paks listed, the loader's named, a leftover reported; the paks unchanged).
- Opt-in, never in test.ps1 (costs a few cents): `--assistant-live-check <dir> cache [model]` sends two tiny requests in two new conversations with the saved key (or ANTHROPIC_API_KEY) and requires cache reads on the second.

## Copy and spacing (2.4.0)

- **Copy standard**: the clearest information in the fewest words. Lead with what matters; no filler ("Please", "Note that", "This will"); errors give the cause and the next step; one term per concept: extra outfit, Replace original (an installed one is a replacement), tool mod, library, Slotsloader (never a bare "loader"), loose file loader, Manager (the app naming itself), external mod manager (never a product name), SF6. Shorter must not mean vaguer. CLI-only output and log lines keep their wording unless the string is shared with the UI.
- **Versions in the UI** go through `UpdateCheck.ShortVersion` ("1.9.8", not "1.9.8-custom"); logs and the CLI keep the full string.
- **Spacing**: `UiRhythm.cs` holds `Rhythm.Related` (6, a control and its own note), `Rhythm.AfterAction` (14, a button and its result) and `Rhythm.Group` (20, before the next caption), in 96-DPI logical pixels like every other construction-time size. Multi-line notes are `NoteLabel`s: word wrap, line height 1.35 × the measured text height, a path line kept whole with a middle ellipsis, and (docked to the top) a height that fits the text. They measure with the control's own device context once the handle exists, because `Font.Height` reports the system DPI, not the window's.
- **Tests**: `updates` checks the Updates tab (no heading, a normal **Check now**, at least 12 px before the one-line result, the skipped-release line muted, the full reason in the log). Settings captions are asserted by `assistant-ui` and `replace-ui`; the General tab's TableLayoutPanel rows (caption, field, note, gap) are indexed by `fields`.

## Publishing a release so the update check finds it

- Tags on ZZtaii/slotsloader-manager: `loader-v<version>` for Slotsloader (e.g. `loader-v1.9.9`), `manager-v<version>` for the app (e.g. `manager-v2.4.1`). The version is read from the tag; anything after the numbers (`-custom`) is ignored.
- Assets: Slotsloader: exactly one `.zip` (the tool-mod package, with `modinfo.ini` `version=` set; the app reads the installed version from it). The app: the portable zip named `*-portable.zip` holding exactly the 7 release files (`Slotsloader Manager.exe`, `.exe.config`, `Slotsloader CLI.exe`, `.exe.config`, `README.md`, `CLI.md`, `VALIDATION.txt`) at its root; its exe's FileVersion must equal the tag's version. Other zips in the same release are ignored for the app.
- Checksums: GitHub's asset digest (`sha256:` in the API) is used; when it is missing, add `SHA256SUMS.txt` (`<sha256>  <asset name>` per line). A release with neither is not offered.
- Pre-release flag: a test build published as a GitHub pre-release reaches only users with "Include test builds (pre-releases)" on. Drafts are never offered.
- The repository must be public for the anonymous API; while it is private the check gets 404 and stays silent.

## Owned-outfit marker (technical, not in user docs or the Assistant)

`Data\show_unowned_slots.txt` is a per-user marker that Data never ships. Its first line names one plain file (no separators, no `..`, no reserved names, at most 64 characters) that the app re-creates, empty, in `reframework\data\SF6_CostumeSlots_data` of the game after a Slotsloader install or update (window, `replace-install`, `update-install loader`) and after Reapply (window, `replace-reapply`); never at start-up, so the file can be deleted to test mods. The file is never deleted and never recorded as deployed; the log says only "Owned-outfit opt-out marker restored." The name exists only in the marker; source, tests and docs contain none. Check: `replace` suite "owned-outfit marker" (a made-up name).

## Assistant knowledge pack

`tools\Build-AssistantKnowledge.ps1` (run by build.ps1) writes `obj\assistant-knowledge.md`, embedded as `SlotsInstaller.AssistantKnowledge.md`: README (What changed limited to the three newest versions), the "Views" section and the three newest version sections of UI-REFERENCE, CLI.md, and the Slotsloader repository's README.md and COMPATIBILITY.md; never DEVELOPMENT, API, ARCHITECTURE or TESTING. Hashes and absolute local paths are replaced, CR and repeated blank lines removed, no dates added: the same docs give the same bytes. The build fails when the pack would contain owned-outfit opt-out text (generic patterns) or lacks its main sections. Size today: about 18k tokens (characters / 4; no real key was used for count_tokens).
## 2.3.1 checks

- `--loader-check <new dir>` (test.ps1 suite `loader`, 10 checks, no real network: a `TcpListener` HTTP server on 127.0.0.1 and fixture pins): the pin values; a library hit by SHA-256 with no request, hashing only files of the pinned size, a renamed non-pinned `SF6_SlotsLoader_v9.9.zip` of the same size not chosen; the cached copy (no request) and a corrupt cached copy deleted; a download through a redirect (`.part`, progress, verified, renamed, then cached); wrong SHA-256 rejected and deleted; oversize (Content-Length above the pin, an unsized body that grows past it, a short body); 404, connection refused and a 1 s time-out with the one-message error and the release page; cancel after the first 16 KB (no `.part` left); plain http and redirects to http/ftp refused before connecting, a redirect loop; `loader-fetch` in-process (library hit, `--no-download` exit 1, a failed and a good download). Report `loader-check\loader-check.txt`.
- `--replace-ui-check` adds the offer in the window: a library copy under another name found by hash beside a renamed `SF6_SlotsLoader_v9.9.zip`, opened in Replace original; not in a library: the download question (No downloads nothing), Yes downloads, verifies into `Data\downloads`, opens it and the health check is installable; again: the cached copy; a 404: the status line has the reason and the release page.
- `--loader-real-check <output>` (suite `loader-real`, `-RealArchives`, read-only): finds the pinned zip in the user's libraries by SHA-256 (no network; the library folders are compared before/after), runs the built `Slotsloader CLI.exe loader-fetch --no-download` on them, and makes one anonymous GET of the pinned URL and records the result (404 while the repository is private; a successful download would be verified and deleted). Reports `loader-real\loader-real-check.txt`, `loader-fetch-real.txt`.

- `--filter-check` (2.3.1 additions): the name-band gradient has no seam row; `NameAsBundle` names the package; W/A/S/D on the grid, tiles and chips (and not in the search box); no tooltip on any control of the panel; a mod picked in the list (posted click, Down, W/S, Enter, top search match) closes the panel while filter/search/programmatic selections keep it open, A/D change nothing, other letters still type-ahead.
- `--filter-real-check` adds `filters-portrait-band-2x.png` (the band top row is not darker than its neighbours at 288 DPI) and `filters-mod-picked.png` (a posted click on a real mod closes the panel and shows its options).
- `--replace-ui-check`: no "· remembered" hint; "· locked for this mod" while forced.

## Bumping the REFramework pin (1.0.8)

The Extra outfit notice and the REFramework offer use `ReFrameworkRelease.Supported` (`src/ReFramework.cs`): a praydog/REFramework-nightly release (`tag`, nightly number, `REFramework.zip` size and SHA-256 = the release asset digest, commit, build date). External mod managers download that repository's latest `REFramework.zip` (one `dinput8.dll` for every game plus `reframework_revision.txt`). To move to a newer nightly: test it in game with Slotsloader, then take its tag and asset digest from `gh api repos/praydog/REFramework-nightly/releases/tags/<tag>` and the build date its `dinput8.dll` prints (ASCII `dd.MM.yyyy`, read by `ReFrameworkBuild.Parse`). Installed builds older than that date are flagged (never upscaler builds, recognised by the word "Upscaler"; an unreadable date is not flagged). Run `test.ps1 -Only replace-ui` (check "REFramework (1.0.8)").

## Bumping the Slotsloader pin

The "install Slotsloader" offer and `loader-fetch` only ever use `LoaderPin.Release` (`src/LoaderRelease.cs`), read from `assets\loader-pin.json` (`tag`, `version`, `asset`, `size`, `sha256`), which `build.ps1` embeds as `SlotsInstaller.LoaderPin.json`; the URL and release page follow the tag. Since 2.5.9 the pin is set by packaging, not by hand:
1. Publish the loader zip as the single `.zip` asset of a new tag on ZZtaii/slotsloader-manager (e.g. `loader-v1.10.0`), not a draft or pre-release; never replace the asset of an existing tag. GitHub's `sha256` digest must be shown for it.
2. Run `tools\Package-Release.ps1 [-TestReport <test summary>]` (needs `gh` signed in). It takes the newest published `loader-v*` release, downloads its zip, checks size and SHA-256 against the digest, rewrites `assets\loader-pin.json`, builds, and writes `Slotsloader-Manager-<version>-portable.zip` (the 7 release files plus `Slotsloader/<loader zip>`) and `SHA256SUMS.txt` into a new `out\release-<time>` folder. It refuses a newest release older than the current pin (`-AllowOlderLoader` overrides) and publishes nothing; it prints the `gh release create` line to run after approval. Offline: `-NoLoaderFetch -LoaderZip <the pinned zip>`.
3. Commit the rewritten `assets\loader-pin.json`; update README/CLI.md/UI-REFERENCE where they name a version, and run `test.ps1 -Only loader,replace-ui,updates,loader-real` while iterating, then the full `-RealArchives` before publishing.
- Install order at runtime (`LoaderRelease.FindLocal`): the bundled `Slotsloader\<asset>` beside the exe (left alone when it does not verify), a library file by SHA-256, `Data\downloads`, then the download. The self-updater (`AppUpdater`) accepts one `Slotsloader/<name>.zip` in a release zip, replaces the app folder's bundled zips with it, keeps the previous ones for rollback and restores them on failure; a release without one leaves them as they are.
## 2.3.0 checks

- `--filter-check <fixture root>` (test.ps1 suite `filters`, 15 checks): BC1/BC3/BC7 against 57 blocks decoded by bcdec.h v0.985 itself (an MSVC build of the upstream header on 2026-10-14; random blocks of every BC7 mode, the reserved mode, BC1 punch-through, both BC3 alpha modes); surfaces (edge clipping, pitch, out-of-file data); `.tex` parsing and rejections; candidate order (030+ spelling); generated KPKA PAKs (base/DLC/patch override, zeroed rows, deflate entries, a broken texture falling back to key art, a missing fighter, a new fighter 035, the stage-name GMSG); the cache (hit without a table scan, stale after a PAK mtime change, lost PNG, foreign manifest, a read overlapping a change not cached, a lock deferring the read, no game folder); the 5:7 alpha-centroid crop; GMSG parsing; categories of 21 generated sources (costume, fighter VFX, colour data, fighter art, costume + UI/sound, fighter on a stage, stage packs as Misc Stage, tool + stage, UI, sound, avatar, shared files, PAK-only, readme-only, viewer scenes = Other) with "never Misc with fighter files or code" and index v4 reuse/relisting; friendly labels, colour/art rules; search terms and row labels; then hidden windows: layout at 1232×640 / 1460×920 (canvas) / 1110×610 (one screen, parts in order, tiles the size of the cards, library gap, Clear in the FIGHTERS line, no combos or checkboxes, chips, no scrolling at 1460×920), filter combinations (fighters, tiles narrowing fighters, chips, the Installed tile, the removed drop-downs' search tokens, Clear), library tiles (hidden with one library, OR among them and AND with the rest, counts, tooltips, saved, 2.2.0 `Source` migrated only while configured, dropped on removal, duplicate folder names incl. the user's two paths), grid and tile keyboard/mouse/accessibility/dimming/placeholder states, asynchronous portraits (synthetic cached PNGs, then extraction from the generated PAKs), view settings (removed drop-down values neither written nor applied) and the 2.2.0 `Character` / tab-build migrations. Report `filter-check\filter-check.txt` and renders.
- `--filter-real-check <output> <game root>` (suite `filter-real`, `-RealArchives`, read-only for the game and both libraries): extracts every fighter's portrait from the real PAKs into the run's `Data\portraits\v1` (cold, then cached), requires all of `Characters.ByCode` and the stage names, notes PAK identity changes made by other tools during the run, lists both real libraries into the run's Data and reports categories, Misc kinds, the label map and the fighter codes from art/colour data (`categories-real.txt`; no Misc mod may have fighter files or code, no raw folder label), checks the category search terms, loads both libraries through the window's own `RefreshLibrary` with an empty index and then cached and requires the status line "N mods found in 2 libraries · …" each time (`screenshots-real.txt`), and renders the whole panel at 1460×920 (`filters-fighters.png` Cammy + Juri, `filters-misc-ui.png` Misc → UI with no scrolling, `filters-code-tools.png`, `filters-installed.png`, `filters-library.png` the second library's tile; half size) in a hidden window without opening any mod.
- `layout` checks the Filters panel beside the list instead of the popup; `ui-polish` adds Ctrl+Shift+F; `generated` (library regression) uses the v4 index and treats REFramework colour data for a fighter as the fighter's (no tool kind).
## 2.2.0 checks

- `--replace-check` adds (`FluffyLinkChecks.cs`): installed.ini parsing + watcher (install/removal seen within 2 s, `NOTE watcher` gives the times), external manager uninstall in both orders with Undo, refusals (mod manager/SF6 running, invalidate, BOM+LF byte-exactness), the user's drift case (Keep current / Restore original, Recovery entries), missing files, the priority stack in three orders, and the CLI (`manager-*`, drift flags) in-process.
- `--replace-ui-check` adds the external mod manager link in the window (watcher refresh ≤ 2 s, Uninstall enabled/disabled, library item) and the drift dialog (Cancel, Keep current file). UI checks turn `MainForm.FluffyAutoRefresh` off except in that check because other checks edit `installed.ini` themselves.
- `--dialog-check <dir>` (test.ps1 suite `dialog`, started with a normal window): the full app as a user launch, five themed dialogs owned by the main window (medium text, the loose-loader question, a short message, forced light and dark); each must draw its first line and its Yes button within 10/15 % of a main-window `Palette.Normal` label (ink height on screen; windows are made topmost for the copy), not scroll, keep its buttons visible and stay ≤ 760 logical px wide. Reports `dialog-check\dialog-check.txt` + PNGs; `Data\log.txt` gets one `Dialog '…'` line per show. With the 2.1.0 dialog it fails (ink ×3.03, scroll bar).
## Moving the app folder (2.1.0)

Every executable resolves `Data` beside itself (`AppConfig.DataDir`). These Data records store absolute paths into their own Data folder:

| Record | Fields |
|---|---|
| `replace-state.json` | `StoreRoot` (default `Data\replace-store`), `Originals[].Backup` (`Data\replace-backups\<id>.bin`, e.g. the genuine `amd_ags_x64.dll`), `Paks[].Backup` and `BaseAtInstall.Backup` (`Data\pak-backups\<stem>.toc`) |
| `replace-journal.json` | `Ops[].BeforeSource/AfterSource` (stored copies, original backups), `CreatedPaths`, `CleanupAfterCommit`, `TrashAfterCommit[].From/To` |
| `pak-backups\*.json` | `Backup` (informational; `RestoreFromTocBackup` derives the `.toc` beside the `.json`) |
| `replace-settings.json` | `StoreLocation` only when the user chose a store inside `Data` |

Not path-bearing for Data: `settings.json` (game, libraries, 7-Zip), `view-settings.json`, `test-session.json` (game-relative files), `<store>\.removed\*\record.json` (game root + mod), the library index caches (library paths), the Assistant files (`claude-key.dat` is DPAPI CurrentUser with a fixed entropy string, not path-bound), extra-outfit manifests and the Recovery/preset areas beside `costume_mods`.

`DataRelocation.Rebase` reads a recorded path as `<current Data>\<relative>` only when: it is rooted and not inside the current Data folder; it does not exist; one of its folders is named like the current Data folder (case-insensitive; trailing separators and `/` tolerated; outermost match first); and `<current Data>\<relative>` exists. Game-folder paths are excluded. Otherwise it is left as recorded: an existing path in another app folder's Data (a copied, not moved, folder) is reported as foreign; a path missing in both places is reported as missing and the status shows it (`GameFilesStatus.BackupsMissing` / `StoredMissing`, the Installed banner, `replace-status`). `RebaseTarget` handles journal targets that do not exist yet (a `.removed` folder) through their nearest rebased ancestor, so recovery never recreates the old folder.

`ReplaceEngine.LoadState` rebases in memory and logs once per process per record file (`Data\log.txt`, `Relocation: ...`); `Recover` rebases the journal before rolling back or finishing; `ReplaceSettings.Load` rebases `StoreLocation`. `PersistRelocation()` saves the corrected state, pak-backups metadata and settings under the game lock (skipped while a journal is pending). The window calls it from `LoadGameRows(true)` (start-up and refresh) and the CLI before each changing command; `replace-status` changes no record. Any normal state write also saves the corrected paths.

Checks (`--replace-check`): `relocation rules` (unit cases incl. a nested `data` folder inside the store, foreign copies, custom store outside Data, PersistRelocation idempotence); `relocation` (tool mod backing up the genuine dll + PAK-method replacement + a recoverable uninstall, then the app folder with a copied `Slotsloader CLI.exe` and Data is moved and the old one deleted; `replace-status` from the new folder: OK, 0 missing, rebase logged once, state file unchanged; uninstalls via the copied CLI (in-process when SF6 or an external mod manager is running on the test machine), genuine bytes and PAK index byte-exact, Recovery lists all three copies, state and pak-backups name only the new folder); `relocation missing backup`; `relocation journal` (uninstall interrupted at every step and install interrupted at the first steps before the move, recovered from the new folder).

## Other 2.1.0 changes

- **Themed message box at high DPI.** The user's 288-DPI monitor showed the box about 3x too large (text cap height ~90 px, scroll bar, buttons cut off). The layout used a pixel font computed for the owner's DPI and laid out again in `OnLoad` only when the DPI differed, so a rescale applied by Windows Forms while creating the handle (seen when the process's system DPI differs from the monitor's) stayed. Now `OnLoad` always lays out again (fonts included) for `DeviceDpi`, and the text uses `Palette.Normal`'s size like the main window. On this machine (system and monitor DPI both 288) the old code did not reproduce in a real modal show; the `--polish-check` real-window check therefore also runs a simulated x3 creation-time rescale, which fails with the old load rule (159 px text vs 54 px) and passes now.
- **Loose file loader optional** (see the README): `EnsureLooseLoader(engine, installsNatives)`; Settings checkbox `settingsAskLoader`; CLI `--no-loader`. Checks: `replace-ui` "loose file loader is optional" and `replace` "CLI loose loader".
- **An open external mod manager** blocks only PAK row changes (`ReplaceEngine.Change` inspects the planned journal). Checks: `replace` (the open-manager case, 2.1.0), `LooseWhileRunning` updated, `replace-ui` loose form with a fake manager process.
- **Settings save bug (found while testing):** `SaveReplacePreferences` called `Engine()` (which reloads `replaceSettings`) after applying the edits, so the reloaded copy was saved and method/default mode/crash-reporter changes were lost. `Engine()` now runs first.

## Known issues in 2.0.1

All six were fixed in 2.4.2 (see "2.4.2 checks").
