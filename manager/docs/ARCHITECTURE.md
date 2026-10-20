# Slotsloader Manager architecture (previously SlotsLoader Manager, and SF6 SlotsLoader Installer before 2.1.0)

Reviewed against the **2.0.4 source on 2026-10-14** (2.0.4 only adds prefix matching of search keywords and character names) (2.0.3 adds tool-mod classification and search, library uninstall of replacements and themed dialogs, see [2.0.3](#tool-mods-library-uninstall-and-themed-dialogs-203); 2.0.2 narrowed the Assistant to Sonnet 5.5 / Haiku 5.5 and Low–High effort) (Replace original from 2.0.0, see [Replace original (2.0.0)](#replace-original-200); automatic review, conflicts and costume search, see [2.0.1](#automatic-review-conflicts-and-costume-search-201)). This is a personal standalone Windows application with no Blender or RE Mesh Editor runtime dependency. Source behavior and current test reports take precedence over historical release notes.

**Source code is the authority for application behavior.** [API.md](../src/API.md) indexes the backend contracts; [DEVELOPMENT.md](DEVELOPMENT.md) covers builds, validation and command-line use. The historical [Slotsloader findings](../tests/slotsloader-findings.md) describe the separate native loader, whose capabilities do not automatically become installer features.

## Runtime and source map

The app targets Windows x64, C# 5 and .NET Framework 4.8. It uses WinForms, System.IO.Compression and System.Web.Extensions. [build.ps1](../build.ps1) compiles all source files into the GUI and `Slotsloader CLI.exe`, with separate entry points. Keep each executable's `.exe.config` beside it; the manifest/config and target-framework attribute work together for long paths and the current PerMonitorV2 UI. RAR and 7Z operations call the configured 7-Zip executable. ZIP support has no external-tool requirement.

The primary entry points and current source locations are:

| Source | Symbols / purpose |
|---|---|
| `Core.cs` | Settings/JSON, roster, library index, installer ownership/lifecycle and path safety |
| `ArchiveSource.cs`, `ZipIntegrity.cs` | Source listings, bounded reads/copies, ZIP CRC checks and external processes |
| `PackageModels.cs`, `PackageReader.cs`, `Composition.cs` | Indexed components/previews, metadata parsing and selection rules |
| `OperationContext.cs` | Thread-local cancellation and progress; non-cancellable commit scopes |
| `LoaderResults.cs` | Read-only mapping of installed sources to the last native-loader result |
| `RecoveryStore.cs`, `RecoveryForm.cs` | Inspect, restore or explicitly delete retained managed copies |
| `OutfitSetStore.cs`, `OutfitSetPointers.cs`, `OutfitSetsForm.cs` | Named presets (1.0.8: lists of library sources, v3; older full-copy v1/v2 still restore), explicitly selected batch removal and reusable set restoration |
| `SourceRelink.cs` | 1.0.10: at preset save, points a replacement or managed extra outfit whose source is gone at the one library mod holding its files |
| `LibraryFilters.cs`, `MainFormCustomFilters.cs`, `CustomFilterEditor.cs` | 1.0.8 MY FILTERS: keyword filters (default NSFW) in the Filters panel, `Data\library-filters.json` |
| `MainFormPerformance.cs`, `PreviewImages.cs` | Deferred virtual review rows; background keyword matching on isolated name snapshots for libraries over 128 entries; decoded preview LRU cache (64 MiB, 128 entries), with independent displayed images and full-resolution enlargement |
| `OverlayScroll.cs`, `MainFormInspectorScroll.cs` | 1.0.8 scrolling inspector with a thin overlay thumb |
| `CommandLine.cs`, `NativeModCheck.cs` | Headless composition/integrity checks and isolated native-loader runs |
| `MainForm.cs`, `OptionTree.cs`, `UiControls.cs`, `UiPolish.cs` | Screens, view preferences, option interaction, drawing and DPI behavior; vector glyphs, chips, drop target, dark title bar and shortcut table |
| `Program.cs`, `AssemblyInfo.cs`, `app.manifest`, `app.config` | Entry point, version and runtime configuration |
| `IntegrationTests.cs`, `*RegressionChecks.cs`, `LoaderResultChecks.cs`, `LayoutChecks.cs`, `PolishChecks.cs` | Backend fixtures, focused regressions and UI checks |
| `ModCategories.cs`, `FighterPortraits.cs`, `TexDecode.cs`, `FilterPanel.cs` | 2.3.0 library categories (Characters, Code tools, Misc with kinds such as Stage), game-PAK portraits and stage names with their cache, BC/tex decoding (bcdec port, MIT), the one-screen Filters panel (fighter grid, kind tiles with placeholder art) |
| `PakIndex.cs`, `Zstd.cs` | KPKA v4 table reading, murmur3 path hash, verified hash-field writes, managed zstd/deflate entry reads (2.0.0) |
| `ModFixes.cs`, `GameTables.cs` | Ports of Slotsloader's fixers (type-signature CRC, 2023 colour layout, texture mip table), texture suffix upgrade; built-in costume/suffix tables (2.0.0) |
| `ReplaceModels.cs`, `ReplaceEngine.cs`, `ReplacePlanner.cs` | Replace-original state, store, journal, reconcile/execute/recover, health check (2.0.0) |
| `SafeHttp.cs`, `UpdateCheck.cs`, `AppUpdater.cs`, `MainFormUpdates.cs` | 2.4.0 shared HTTPS path, update check (GitHub releases), update settings and toasts, Settings → Updates, Slotsloader update in place, Manager self-update and rollback |
| `MainFormUndo.cs` | 2.4.1 Undo in the status strip (`OfferUndo`, until the next change); one-click uninstall of every record of a source; recoverable Restore original game files |
| `MainFormSourceWatch.cs`, `MainFormInstalledSearch.cs` | 2.4.1 the open mod reloads when its source changes (stamps, selection mapping by id then name, `SourceChangedException`); Source changed / Reinstall on installed copies; the shared search × (`SearchClear`) and the Installed page's LibrarySearch matching |
| `LibrarySort.cs`, `ReplacePresets.cs`, `SegmentedChoice.cs` | 2.4.1 sort chips (Added · Last used · Installed) and Refresh; presets with replacements (store snapshots, v2 format); the N-way segmented control |
| `UiRhythm.cs` | 2.4.0 one vertical rhythm (`Rhythm.Related` / `AfterAction` / `Group`) and `NoteLabel` (wrapped notes with even line spacing that size to their text); 2.4.1 `NoticeBanner`, the one compact banner |
| `LoaderCostumePak.cs`, `UiAudit.cs` | 2.4.0 Slotsloader's generated costume pak (recognition, removal with placeholder, orphan), the owned-outfit marker; the no-tooltip audit |
| `MainFormSlots.cs` | 2.5.0 several slots per mod: suggested outfit name, the slot hint tooltip, Reset options, the Installed page's group rows |
| `VariantChoices.cs`, `MainFormVariants.cs` | 2.5.0 variant on/off for dropped archives/folders: Slotsloader's `variants.json` (read), `variant_choices.json` (written), the CLI `variants` command; the Installed inspector's VARIANTS list |
| `LoaderRelease.cs` | 2.3.1 pinned Slotsloader build: `LoaderPin.Release` (tag, asset, size, SHA-256), library/`Data\downloads` lookup by hash, verified HTTPS download (`.part`, size cap, redirects only to HTTPS, system proxy, cancellable) |
| `GameEnvironment.cs`, `GameSession.cs` | Running-process probe, launcher, external mod manager `installed.ini`, REFramework/Slotsloader detection, loose-file-loader config; since 2.3.0 `GameSession.cs` only finishes a leftover Test-in-game record (`TestSession.FinishLeftover`) |
| `FluffyPresets.cs`, `FluffyPresetForm.cs` | External `.prt` preset parsing, library mapping and install (2.0.0) |
| `MainFormReplace.cs`, `ReplaceCommandLine.cs` | Replace-mode UI (partial `MainForm`) and CLI commands (2.0.0) |
| `ReplaceChecks.cs`, `ReplaceUiChecks.cs`, `ReplaceRealChecks.cs` | Fixture engine/UI checks and read-only real-game/library checks (2.0.0) |

Source filenames above are relative to `src`; use symbols rather than historical line numbers when navigating.

The processing flow is:

```text
library paths -> Library.Scan -> LibraryEntry rows and detected native codes
selected source -> SourceReader -> PackageReader.Load -> ModPackage/components
selected component IDs -> Composer.Plan -> winning files, overrides, errors/warnings
reviewed plan -> Installer.Install -> staged merged folder -> managed outfit
next SF6 launch -> existing Slotsloader -> generated pak and in-game outfit slots
```

## Settings and working paths

`AppConfig` defaults to the user's existing paths:

| Setting | Default |
|---|---|
| Game root | `<game folder>`: on a first start (no settings.json) detected from Steam (registry, libraryfolders.vdf, appmanifest_1364780.acf), else empty and Settings asks (2.4.3) |
| Libraries | `<manager folder>\Games\SF6\Mods` for each external mod manager install found on a first start (a running one, the configured manager folders, folders named after it on fixed drives), else none (2.4.3) |
| 7-Zip | `C:\Program Files\7-Zip\7z.exe` |
| Destination | game root + `reframework\costume_mods` |

Application data lives beside the executable in `Data` (`AppConfig.DataDir`). Since 2.1.0 the whole app folder may be moved: records that store absolute paths into their own `Data` (replace state, journal, pak-backups metadata, a store location inside `Data`) are rebased by `DataRelocation` when the recorded location is gone and the same relative path exists in the current `Data`; see [DEVELOPMENT.md](DEVELOPMENT.md#moving-the-app-folder-210) for the rules, every persisted path and the checks. `settings.json` stores the configuration. `library-native-index-v3.json` is an optional derived identity cache (older `-v1`/`-v2` files may remain beside it). These files are separate from source mod archives and the loader's game data. Tests redirect the identity cache to their generated workspace run directory and restore the prior setting afterward.

Independent `ViewPreferences` in `Data/view-settings.json` retain window geometry, filters and divider widths. A 600 ms UI timer and FormClosing save them; display/splitter bounds constrain restoration. Invalid view JSON falls back to defaults with a log entry. View autosave does not rewrite game/library settings, and headless commands do not create a form or restore its preferences.

`Json.Write` publishes UTF-8 settings through a temporary file and atomic replacement. `Json.WriteNew` publishes reports without replacing a file created by another process. The GUI needs a writable Data directory. An identity-cache write failure does not prevent scanning; incomplete settings produce a specific reset instruction.

## Library discovery and authoritative native identity

`Library.Scan(libraries, sevenZipPath)` enumerates **direct children** of each library. It accepts extracted directories and `.zip`, `.rar`, `.7z` files, skips hidden/reparse entries, deduplicates paths case-insensitively, and sorts rows by name. A missing/unreadable library or disappearing source produces an `Error` row with `DetectionError`; other libraries and already discovered entries remain available. Whole-library diagnostics have `Kind == "Library"`. Error rows have no character guess and are not cached.

Four parallel workers inspect paths without extracting payloads or parsing every INI. Workers explicitly inherit the operation token; cancellation is propagated as cancellation, not recorded as an unreadable mod. Progress counts are ordered and throttled.

Character detection considers entry paths only after a case-insensitive `natives` path segment. It uses this case-insensitive regex on that remaining path:

```regex
(?:/esf/esf|/esf)(\d{3})(?:/|_)
```

This is a path identity check, not a binary resource validator. Incidental ESF text in documentation outside natives is ignored. Code matches below natives are deduplicated and sorted. The same identity rule is applied when a full `ModPackage` is opened, so a source filename cannot override known native payload identity.

| `LibraryEntry.Detection` | `Character` | Meaning |
|---|---|---|
| `Native` | mapped canonical name | Exactly one known native code; overrides filename |
| `Ambiguous` | empty | More than one native code; retain all `CharacterCodes` |
| `UnknownNative` | empty | Exactly one unmapped code; no filename fallback |
| `Filename` | guessed name | No native codes; labelled filename fallback |
| `Unknown` | empty | Neither native code nor usable filename guess |
| `Error` | empty | Source could not be safely/readably listed; `DetectionError` records why |

For readable sources, `FilenameCharacter` retains the original guess independently. For example, `Viper - Hair - Manon C4.rar` guesses Manon but its `esf030` payload is C. Viper; the numeric `01outfit-1385-1-1-1703241589.zip` is classified as Chun-Li from `esf004`.

`Characters.ByCode` contains the exact supported roster below. **023 and 024 are deliberately absent.** Do not replace this with a sequential list whose indexes shift later fighters.

| ESF | Canonical folder | ESF | Canonical folder |
|---|---|---|---|
| 001 | Ryu | 002 | Luke |
| 003 | Kimberly | 004 | Chun-Li |
| 005 | Manon | 006 | Zangief |
| 007 | JP | 008 | Dhalsim |
| 009 | Cammy | 010 | Ken |
| 011 | Dee Jay | 012 | Lily |
| 013 | A.K.I | 014 | Rashid |
| 015 | Blanka | 016 | Juri |
| 017 | Marisa | 018 | Guile |
| 019 | Ed | 020 | E. Honda |
| 021 | Jamie | 022 | Akuma |
| 025 | Sagat | 026 | M. Bison |
| 027 | Terry | 028 | Mai |
| 029 | Elena | 030 | C. Viper |
| 031 | Alex | 032 | Ingrid |
| 033 | Yasmine | 034 | Arjun |

The folder spelling is `A.K.I`, without a trailing dot, because Windows strips trailing dots. The display spelling elsewhere can be A.K.I.

### Identity cache

Archives are cached by full source path plus a stamp containing archive byte length, last-write UTC ticks, configured 7-Zip full path, and the tool's length/last-write time. This is a metadata stamp, **not a content hash**. A source replaced with the same size and preserved timestamp can retain its previous cached identity or option count until explicitly invalidated.

The cache format is `sf6-native-library-index-v3` in `library-native-index-v3.json` (2.0.3; v2 in `library-native-index-v2.json` came with 2.0.1. Older files are left for older builds, and each archive is listed once more); records carry `SourcePath`, `Stamp`, `CharacterCodes`, `CostumeKeys`, `ToolKinds`, `OptionCount`, and `OptionIds`. `ToolKinds` (2.0.3) classify the files outside `natives` in the same listing pass, see [Tool mods (2.0.3)](#tool-mods-library-uninstall-and-themed-dialogs-203); a record without them, or with an unknown kind, is relisted. `CostumeKeys` are the costume folders the native files touch ("009/001" for `natives/stm/(streaming/)product/model/esf/esf009/001/...`; "009/000" is the character's shared parts), collected in the same listing pass as the character codes; a record without them is relisted. Each ID is a distinct component root ending immediately before a `natives` directory with files, matching the installer’s selectable native-file components. A count above four marks a Bundle; zero through four is Normal. Older records without the IDs are relisted once and saved with them. A lock protects memory loading, access, and saving. Changing `Library.IdentityCacheDirectory`, even to the same directory, resets loaded memory so the next scan loads that directory's disk index. `Library.ClearIdentityCache()` clears memory and deletes only the generated index file at the configured location. Malformed cache data is discarded. Failed source reads are not cached and are retried on refresh.

`Library.InstallState` matches only app-managed outfits whose manifest `SourcePath` equals the library entry path. It unions `SelectedIds` from enabled outfits and compares them with indexed `OptionIds` for Full or Partial coverage. Disabled-only sources are labelled Disabled; manual outfits have no source provenance. This is a saved-selection marker, not proof that installed bytes still match the archive or that SF6 loads the outfit.

Folders are always rescanned: changing a nested native file need not change the top directory's timestamp. UI library refresh calls `Library.Scan` and benefits from automatic archive-stamp invalidation; it does not currently call `ClearIdentityCache` to force every unchanged archive to be re-read.

## SourceReader and package parsing

`SourceReader` presents a case-insensitive dictionary of normalized entry paths, original source entry names, and sizes.

- **Extracted folders:** recursively list files; reject junctions/symlinks/reparse entries. Original files remain in place.
- **ZIP:** .NET `ZipFile.OpenRead` handles names, decompression and data descriptors; `ZipIntegrity` reads expected CRC-32 values from the central directory, including ZIP64. Reading metadata/previews, verifying files and copying native files all validate CRC and exact length. Stored/deflated payloads, UTF-8 names, and signed/unsigned data descriptors have fixture coverage. Listings alone do not verify payload CRC. ZIP routing is by extension, not archive sniffing.
- **RAR / 7Z:** `7z l -slt -ba -sccUTF-8` lists entries. Package loading batches metadata and a bounded subset of previews into generated temporary storage, even when the source has a larger image set. At most 16 images totaling 16 MB are preloaded; uncached images remain available individually. Install extracts an exact UTF-8 list of **winning selected entries** with wildcard matching disabled, then moves them within staging and hashes them. Sizes and reparse chains are checked; 7-Zip supplies archive-integrity errors.

Path validation rejects absolute/drive-qualified paths, parent traversal, empty segments, invalid/reserved Windows names, links and case-insensitive duplicates. ZIP/folder transfer limits are enforced during streaming, before excess bytes reach memory or the destination; changed folder-file lengths are rejected too. `VerifyFiles` uses the same integrity/hash path without publishing an outfit: ZIP/folder streams go to `Stream.Null`; RAR/7Z uses selected temporary extraction and cleanup.

External processes run hidden without a shell, with quoted arguments and closed stdin. Stdout has a caller limit (64 MB for text), stderr is capped at 1 MB, and the default deadline is ten minutes. Cancellation or failure terminates and waits for the child. The overload accepts an explicit token/deadline; errors identify the executable. There is no password-entry or nested-archive composition workflow.

`PackageReader.Load` reads every `modinfo.ini` and creates components keyed by its containing directory. The empty string is a valid root component ID. **`ParentId == null` means no parent; `ParentId == ""` means the root component.** Keep that distinction in future changes.

`ParseIni` recognizes UTF-16 LE with BOM, otherwise strict UTF-8 with Windows-1252 fallback. Keys are case-insensitive. It accepts `key=value` and `key:value`, trims whitespace, skips comment/section lines, and expands literal `\n` in values. All parsed metadata is retained. Behavior uses `name`, `description`, `author`, `version`, `addonfor`, `dummymod`, and `screenshot`; raw `nameasbundle` is retained but is not used by the current app's group resolver.

Each native tree belongs to its **exact containing directory**, never to the nearest ancestor INI. A root with no INI gets a synthetic component. Thus `backup_colors/snapshots/original/natives` cannot silently overwrite the parent's current palettes just because the parent component was checked. Metadata links resolve `Addonfor` by a unique trimmed case-insensitive component name. Missing/ambiguous links warn. Without an explicit link, the nearest physical component ancestor becomes the parent. Dependency cycles are broken and reported. Components are naturally sorted for presentation.

Parsing indexes component IDs/names and images by directory instead of repeatedly scanning the whole package. `ModPackage` indexes IDs, children and preview fallback. Choice/index snapshots are refreshed at composition boundaries; callers modifying public component fields should rebuild through `PrepareChoices`/`Composer.Plan` before using the changed hierarchy.

Asset destination paths strip the source wrapper/option directory and retain one `natives/` prefix. Every selected file below that prefix is preserved; files elsewhere are not installed. External `.lua`, `.dll`, `.pak`, and `.exe` files outside natives produce a dependency warning. PAK-only packages have no usable loose native payload. The parser does not unpack PAK content.

Preview selection uses the `screenshot` entry relative to its component, otherwise a same-directory JPG/JPEG/PNG/BMP/GIF with `screenshot.*` preferred. `ModPackage.PreviewBytes` checks the component, its ancestors, then another available component preview, with a 25 MB read limit. Metadata reads have a 1 MB limit. Source preview images are used by the UI and are **not copied into the installed outfit**.

Preview bytes share a package cache by resolved entry, capped at 16 MB; the UI has its separate 64 MB cache. An unchanged RAR/7Z listing can be reused by size/mtime for preview reads. Installation always reinspects the source. Hover is asynchronous and must not alter checks or merge order; see [UI-REFERENCE.md](UI-REFERENCE.md).

## Composition and UI selection

`Composer.Plan(package, selectedIds)` is deterministic for a given source listing and selected order. It removes duplicate IDs, rejects missing IDs, and keeps only real components with assets. Dummy/category components do not contribute files. Components whose names match `main`, `main file(s)`, `base`, `mandatory`, or `required` are recognized as base layers and applied first; the remaining layers keep their selection order. The last layer writing a case-insensitive destination path wins. Each collision is reported with previous/winning component names.

Selecting a child whose direct real parent contributes assets requires that parent in the plan. A nonempty selection activates the package's implicit root, even if every selected component is nested: root main files and required groups still apply. Explicit independent bundle branches keep their own scope, so unused branches do not impose their requirements.

Both backend and UI use the same choice classification. Explicit groups are dummy components whose descriptions say “select/choose/pick one”; zero choices is an error unless optional. Inferred groups contain multiple real immediate **non-base** children that all replace a shared destination. Main files are excluded from inferred exclusivity, allowing main-plus-overlay combinations. Selected parent/child components count as one immediate choice branch, not two choices. Inferred groups prohibit conflicting alternatives but do not automatically require a selection.

`DefaultSelection` selects base components sharing the first base's parent, or the only real component, otherwise real root components. It does not invent a complete combination for every modular bundle.

`MainForm.ComponentChecked` refuses category checkboxes, unchecks sibling alternatives in a choice group, automatically selects contributing real ancestors, and records checked components in order. Uncheck/recheck moves an option later. Unchecking a real parent unchecks its descendants. Hover preview does not modify that order or selection. Do not turn every dummy category into a radio group: independent optional extras must coexist.

Missing/no-longer-selectable saved option IDs block installation until the user agrees to review the remaining selection through **Review files**. Checkbox edits do not dismiss this requirement; agreeing does not itself replace the installed outfit.

Composition validation checks path presence only:

```regex
^natives/stm/product/model/esf/esf(\d{3})/(\d{3})/01/[^/]+\.mesh\.230110883$
```

A body in shared folder `000` is excluded from the complete body set. If no complete body exists but another current-version part mesh exists, the app warns about a partial costume; if no qualifying mesh exists at all it errors. It warns when a body has no local material in its part directory, when several bodies/source costume folders exist, and when native paths fall outside `KnownLoaderRoute`. Materials are accepted for this presence check as `*.mdf2.<digits>`; this is not proof of binary or scene compatibility. Missing MDF is a warning because Slotsloader can sometimes use vanilla materials.

`KnownLoaderRoute` is an advisory route heuristic for model/esf assets, costume scenes, root-level model resources, or filenames containing `esfNNN_FFF_`. It preserves unknown native files and warns; it does not discard them or prove upstream recognizes them. `CanInstall` means the plan has files and no composition errors. It does not mean the outfit has been game-tested.

## Installation, ownership, and recovery

The install API receives `costumeRoot`, a canonical character, a user outfit name, and an explicit `replaceManaged` flag. It validates Windows path/name segments, rejects dot-prefixed outfit names and the reserved name `natives`, enforces a known character, checks selected `/esf/esfNNN/` routes against that target, and enforces path containment. `ValidateRoot` requires the destination leaf to be named `costume_mods` and rejects reparse chains; it does not validate the entire SF6 installation. The UI derives that root from Settings.

The installed tree is:

```text
Street Fighter 6/reframework/
  costume_mods/<Character>/<Outfit name>/
    modinfo.ini
    .slots-installer.json
    natives/stm/...
  .slotsloader-installer-staging/<transaction GUID>/
  .slotsloader-installer-backups/<backup GUID>/
  .slotsloader-installer-disabled/<managed outfit ID>/
  .slotsloader-installer-trash/<ID>-<UTC timestamp>-<suffix>/
  .slotsloader-installer-presets/<set GUID>/set.json
  .slotsloader-installer-presets/<set GUID>/payload/<entry number>
  .slotsloader-installer-removed-sets/<set GUID>/set.json
  .slotsloader-installer-removed-sets/<set GUID>/payload/<entry number>
  .slotsloader-installer-set-staging/<transaction GUID>/
```

All temporary, disabled, previous, and removed versions are **siblings outside `costume_mods`**. A backup natives tree inside an enabled outfit would be another loader source and must not be used for recovery storage.

`Installer.Install` performs these steps:

1. Refuse an existing destination unless replacement was requested and a valid managed manifest exists. Manual destinations are never replaced.
2. Create a GUID transaction in the sibling staging root.
3. Re-read the source and re-compose the selected IDs. Require the current plan's winning paths, entry paths, sizes, and count to match the reviewed plan.
4. Extract/copy the winning payload to staging and record SHA-256 hashes of the staged native files.
5. Write fresh flat `modinfo.ini` containing name, authors, description, and version. Omit source `Addonfor`/`NameAsBundle` links so the loader does not re-compose an already completed variant.
6. Write `.slots-installer.json`. At commit, recheck replacement permission and exact prior manifest contents/identity; refuse unexpectedly new, changed or disappeared destinations. Move an approved previous destination to a recoverable backup.
7. Move the prepared directory to its final destination. If commit fails and the destination is absent, restore the backup. Cleanup is confined to the generated transaction root.

Reinspection compares paths and lengths, not an earlier content hash. Same-size changed source bytes may be copied; ZIP CRC checks establish archive consistency and manifest SHA-256 values describe the bytes actually staged. Directory moves retry brief sharing/lock failures. Backup/trash retention is intentional; there is no automatic purge or startup crash-recovery journal. A killed process can leave staging/backup folders for inspection.

Install, enable, disable, uninstall, recovery and outfit-set mutations share a named mutex for the normalized costume root in the current Windows session. Other programs do not participate. `OperationContext` provides cooperative progress/cancellation during preparation; final publication/moves and rollback run in non-cancellable scopes. This is not a shutdown/crash-safety guarantee.

`InstallManifest.Format` is `sf6-slotsloader-installer-v1`. Its fields are `Id` (32 lowercase hex characters), `Name`, `Character`, `SourcePath`, `CreatedUtc`, `SelectedIds`, and native `Sha256` values. Replacement keeps the managed ID. `ManagedManifest` recognizes ownership by format, required fields, valid segments, and matching character. The saved hashes are provenance: existing file contents are not automatically rehashed by these `Installer` lifecycle APIs. Outfit-set verification has a separate full inventory, described below.

`ListInstalled` reads visible, nonempty **direct character/outfit folders**, plus direct ZIP/RAR/7Z/PAK files under each character and valid managed copies in the disabled sibling. Dot/hidden/reparse entries are excluded where applicable. A manual archive's `InstalledOutfit.Folder` is its file path. The internal `SnapshotStamp` records entry type, creation/modification times and file length where applicable. It detects ordinary stale list rows without hashing every installed tree while browsing; it is not a recursive content fingerprint.

`ValidateOutfit` rechecks manifest ID and exact expected managed location before mutation. `SetEnabled(false)` moves to the disabled sibling by ID; enable restores the original character/name and refuses collisions. `Installer.Uninstall` moves the whole managed folder to timestamped recoverable trash. These APIs retain their manual-outfit guard. Library uninstall also remains managed-only, matches the exact saved source path including disabled copies, and preserves the source mod. The Installed page has a separate explicit-selection batch action through `OutfitSetStore`, described below; it can remove selected manual entries reversibly. Manual entries cannot be enabled/disabled or replaced through the managed lifecycle APIs.

**Recover removed outfits** uses `RecoveryStore` to inspect retained backup/trash directories, restore a selected managed copy without replacing any existing destination, or explicitly delete that retained copy. Ownership, generated folder naming, containment, links and the reviewed manifest/size/count snapshot are rechecked. Unknown/manual folders remain protected; source-inside-copy records cannot be deleted. Recovery does not rehash every payload against its old manifest, automatically purge old copies, or recover arbitrary interrupted staging trees.

## Named presets and selected outfit sets

1.0.8: a preset is a list (format v3, `OutfitSetPointers.cs`): each enabled outfit and replacement by library source, options, place and order; restore reinstalls item by item and reports what it couldn't (`OutfitSetRestoreResult.Notes`). Only entries without a library source are copied, in the v1/v2 layout described below, which older presets keep using. Details: `src/API.md`.

1.0.10: a manual drop can be listed too. When an archive in costume_mods has no library source, Save looks for an identical archive in the Mod library's current list (no new scan). Only same-size archives are compared, by SHA-256, and the drop is hashed only when one exists. If one matches, the drop is saved as an `all_variants` pointer to it and restored from it under its own file name. Each copy keeps its reason in `Pointers.CopiedOutfitReasons` / `CopiedReplacementReasons`. The Presets window lists the copies with their size and reason in the details, and after Save the status line says how many were copied.

1.0.10, renamed sources (`SourceRelink.cs`): before the list is made, Save also looks for the library mod behind a replacement, tool mod or managed extra outfit whose recorded source is gone (renamed or moved outside the Manager).
- Candidates come from the same list, by name only: the record's name, its old file name and the modinfo name are compared as letters and digits, and a version after the name counts as the same name ("X - v1.0" and "X").
- Each candidate is read like an install: its listing, then the record's options composed. Paths are named as `ReplacePlanner.Prepare` names them; a texture or mesh it renamed from an older version suffix is found under its old name.
- Every file of the record must be there. An unchanged file must have the same size, then the same SHA-256. A file the Manager fixed (`ModFile.Fixes`) only has to be present, and at least one unchanged file must be compared. A patch PAK must equal one of the mod's PAKs.
- An extra outfit has no fix marks, so all its files must match. One whose swatches were rebuilt never relinks, and one with parts from other mods is skipped.
- Exactly one match: `SourcePath` is rewritten in `replace-state.json` (under the game lock, not while a journal is pending) or in the outfit's `.slots-installer.json`, as a library rename writes them. No game file changes, and a Rename history event ("from X, found by its files") is recorded.
- None or several: nothing changes, and the item is copied with "not found at <path>".
- It does not run at start-up or refresh: a candidate that keeps not matching would be read again every time.

`OutfitSetStore.CreatePreset` enumerates the current installed entries afresh and copies **all enabled entries**, regardless of Installed-page search, filters or selection. Empty presets and duplicate preset names (case-insensitive) are refused. Managed folders, manual folders and direct manual ZIP/RAR/7Z/PAK files are included. Archives and PAKs are opaque files: saving or restoring them needs neither archive extraction nor the original library source. Existing component composition behavior is unchanged.

Each saved set has a generated GUID directory outside the loader scan tree. `set.json` uses format `sf6-slotsloader-outfit-set-v1` and records the set ID, display name, `Preset` or `Removed` kind, UTC creation time, and entries. Each entry records its character/name destination, enabled state, file-versus-directory type, managed identity, numbered payload location, complete directory inventory (including empty directories), and every file's relative path, length and SHA-256. This preserves `.slots-installer.json`, `modinfo.ini`, supplementary files and native bytes together. The set's inventory is separate from the older managed manifest's native-file hashes.

`RemoveOutfits` accepts only the explicitly selected `InstalledOutfit` rows. It re-enumerates the current root, rejects duplicate, missing, changed or out-of-scope rows, inventories the complete selected entries, and rechecks those inventories before committing. It moves the selected entries into one generated removed set; unselected entries stay in place. This is the only new removal scope that accepts manual outfits. Disabled managed entries may be selected too; their enabled state and original disabled ID location are retained. Presets contain enabled entries only. Successful removed sets appear alongside presets through `OutfitSetStore.List`; older backup/trash records continue to use `RecoveryStore`.

Restore is **additive**. It inspects the complete saved set and every intended destination before publishing anything. A missing destination is staged for restoration. An existing destination is skipped only if its file/directory type, complete relative file and directory structure, lengths and file hashes match; timestamps are not part of byte identity. Same-name changed contents are a conflict, including an extra or missing file or directory. Any conflict stops the entire operation without overwriting or removing installed outfits. Unrelated installed entries are kept. The destination checks run again after preparation. Missing entries are copied from retained payloads, so both presets and removed sets remain reusable after restore. Removed disabled entries return to the disabled sibling. Restored file/directory modification times are marked current so older loader results are not presented as current.

Listing validates generated locations, record schema, paths, managed ownership agreement, and payload shape/length; it does not hash every payload just to display the manager. Restore and explicit saved-set deletion perform full SHA-256 verification and compare the exact previously listed record text. Managed payload metadata must agree with the saved ID, character and outfit name, including disabled destinations. Unexpected payload entries, unsafe paths, reparse chains, changed records and damaged payloads are refused. Deleting a selected saved set acts only on that revalidated generated copy and does not delete active outfits or source mods; the UI requires explicit confirmation. There is no automatic retention deletion.

Preparation reports byte progress and accepts cancellation while hashing/copying. Copying verifies its streamed hashes and staged file structure/lengths; saving also rechecks original source inventories. Commit and rollback are non-cancellable. Ordinary move/publication failures roll completed moves back in reverse order; brief scanner/share locks receive the installer's bounded retry policy. If rollback itself is blocked, the error identifies a retained staging directory and the original payload there is not cleaned up. These are ordinary-error guarantees, not a durable crash journal or automatic recovery of interrupted processes.

## Separate SF6 Slotsloader contract

The app produces a selected loose-file composition; the installed loader allocates extra slots and rewrites resource references at the **next SF6 process launch**. Extra-outfit operations do not modify mod libraries, ordinary game `natives`, native binary assets, the installed loader, saves or loader-generated game data. Replace original (2.0.0) is the separate, explicit path that changes game files; see [Replace original (2.0.0)](#replace-original-200). The optional native CLI check runs the chosen executable in a separate sandbox, where that loader creates its own output.

Historical research inspected [SF6-SlotsLoader v1.9.2 / commit 630da6e](https://github.com/Wael3rd/SF6-SlotsLoader/tree/630da6e3e58f15231d0d6a492528f343664372bc). Older recorded build labels are historical, not a statement of today's installed loader. Check the actual executable/result metadata when diagnosing behavior; the native CLI report records the chosen loader path, SHA-256 and emitted build label. Do not silently upgrade the game loader.

The researched loader scans `costume_mods/<Character>/<Variant>`, can find additional nested native roots, and accepts archives/PAKs itself. Its loose-folder routes include model/esf, streaming model/esf, individual costume scenes, costume-prefixed auxiliary filenames such as weather/wind chains, and files directly in product/model. Global UI/sound/script/plugin resources may be ignored. Selected custom textures, shared `000` resources, palettes, and physics files must remain together with their original suffixes and bytes.

The loader supports 100 extra costume IDs per character, internally 5 through 104. It determines source costume ownership from its vanilla index; do not assume folder minus one always equals source costume. Several source costume bodies can create several slots. Copying assets under another character's directory does not retarget them. The app can compose a single-character branch of a multi-character package, but the target guard refuses selected native routes belonging to another fighter.

Roman names are allocated by the loader from active internal slot order. Existing slot identity/registry assignments take precedence over source alphabetic order. A manager folder called Outfit I cannot force that in-game number. Loader folder identity uses engine path hashes and file lengths, with relative-path-derived collision IDs, so keeping variant folder names stable is useful.

The loader also repairs and validates scene resources, mesh/MDF material names, texture references/mip layouts, and other known compatibility problems. A successful app merge establishes that the requested files were copied, not that those loader checks or in-game rendering succeeded. Restart SF6 after changes; this app has no runtime loader refresh command.

`LoaderResults.Apply` reads `reframework/data/SF6_Costumes_Data/outfits.json` and joins its rows to each installed source's exact normalized path relative to `costume_mods` (including the character directory and archive extension). One source may have several reported outfit names. Matching by display label, archive stem or filename substring is deliberately avoided.

Statuses distinguish **Loaded**, **Pending restart**, **Not in last loader result**, **Disabled**, missing results and unreadable results. Managed install time and current source file/directory modification times detect obvious stale reports; old names may remain visible as historical details. This is a last-loader-result view, not proof of current in-game rendering. `left_out` rows without an exact source are not guessed onto unrelated installed outfits. No result/registry/PAK is rewritten by this view.

## Several slots per mod (2.5.0)

Extra outfit mode installs one composed outfit per Install, as before; several variants of one mod are repeated installs under different names (`MainFormSlots.cs`). Nothing in `Composer` or `Installer` changed.

- **Name** (`SuggestedOutfitName`): with no Manager outfit of the open source for the target character, the plain mod name (`CleanName(package.Name)`, unchanged). Otherwise an enabled slot whose `SelectedIds` equal the ticks gives its name, so `InstallSlot` takes the existing same-name switch (update in place). Else `<mod> · <options>` (`VariantOptionNames`: ticked real components in package order, minus base/main files, the implicit root and parents of another tick, joined with ` + `; capped at 120 characters), then ` 2`, ` 3`… while any outfit of the character has the name. `ApplySuggestedName` runs from `ResetPlan` (ticks, mode), the character combo and `RefreshInstalledDone`; it is skipped in Replace original, after the user typed a name (`outfitNameTyped`, cleared when the mod opens or the field is emptied) and for Edit options (`outfitNameLocked`). `SetOutfitName` changes the field without counting as typing; a source reload carries both flags (`PackageReload.NameTyped/NameLocked`).
- **Ticks**: installs and refreshes never call `FillComponents`; only opening a mod, a source reload (selection mapped by id/name) and Reset options do.
- **Hint** (`SlotHint`, `UpdateSlotHint`): "Updates this slot." / "Adds a new slot. Keeps: A, B +N more." as a `ToolTip` on the outfit name (and its edit) and Install. It is the app's only tooltip, at the user's request; `UiAudit.ToolTipAllowed` lists the field and the two methods so both no-tooltip audits still cover everything else.
- **Reset options** (`ResetOptions`): `FillComponents(Composer.DefaultSelection(package, IsReplaceMode || IsToolPackage(package)))` + `ResetPlan`; the button shows while `OptionsDifferFromDefaults`.
- **Installed grouping** (`FillInstalled`, `BuildGroups`): after the page's filters, Manager outfits with the same source and character, two or more, become one `OutfitGroup` row (`Tag`), followed by its slots when expanded (`expandedGroups`; chevron click, double-click, Right/Left). `GetInstalledSelections` expands a group row into its slots (distinct), so Uninstall, Select all visible and presets work unchanged: `RemoveOutfitsWithUndo` puts them in one removed set (history: one Uninstall per slot; Undo restores the set). `ShowGroupDetails` is the group inspector.
## Variant on/off for dropped mods (2.5.0)

A whole mod archive or loose multi-option folder dropped into `costume_mods/<Character>/` is composed by Slotsloader itself, which may build several variants (outfit slots) from it. The contract with Slotsloader (fixed; the loader implements it separately) is two files in `reframework/data/SF6_Costumes_Data`:

- `variants.json` (Slotsloader writes, the Manager reads): `{ generated, loader, archives: [ { source, fighter_dir, variants: [ { label, enabled, slot } ] } ] }`. `source` is relative to `costume_mods` with `/`; every variant is listed, on and off; `slot` is `"Outfit V"` for a built variant, else null. Missing with an older Slotsloader.
- `variant_choices.json` (the Manager writes, Slotsloader reads at launch): `{ version: 1, archives: { <source>: { <label>: false } } }`. Default on: only `false` turns a variant off.

**Reading** (`VariantChoices.ReadReport` / `ReadChoices`): shared reads (`ReplaceEngine.ReadShared`), no reparse chain, 16 MB cap. A missing or unreadable `variants.json` gives `Present = false` (logged). A missing choices file is all on; malformed JSON, a missing or unknown `version` or non-bool values are tolerated (`Malformed`, read as all on, logged). Sources compare case-insensitively with `\`/`/` normalised; labels are exact.

**Writing** (`Set`, `Restore`): under a process lock the file is read again, the archive's entry is replaced (a differently spelt key is replaced by the loader's spelling), other archives' entries are kept, and the whole file is written by `DurableFile.WriteJson` (temporary file flushed, then `File.Replace`). Only `false` is stored; `Set(on)` removes the label and the entry goes when every variant `variants.json` lists is on. A file with another `version` is never overwritten (an `IOException` says so); a malformed version-1 file is replaced (logged). The game may be running: Slotsloader reads the file at the next launch.

**Listing**: `LoaderResults.Apply` (every `Installer.ListInstalled`) sets `InstalledOutfit.Variants` for enabled manual entries whose source has two or more variants, and `VariantsUnsupported` when there is no `variants.json` but the last `outfits.json` maps two or more outfits to the entry. Managed outfits are one composed variant each and never get toggles.

**UI** (`MainFormVariants.cs`): `ShowVariants` (from `ShowInstalledSelection`) fills `VariantList` with `VariantChoices.States` (`On` = the choice, `Built` = what Slotsloader applied, `Pending` when they differ). `ToggleVariant` writes at once (no `Work`: a small file), reports "<label> off · applies at next launch" (or "applies after SF6 restarts" when `GameProcesses.Probe` sees SF6) and `OfferUndo` with the archive's previous entry (`VariantChoices.Entry`) for `Restore`.

**CLI / Assistant**: `variants [--game] [--set <source> <label> on|off]` (`VariantsCommandLine`, routed from `ReplaceCommandLine.Run`). The Assistant's `list_installed` adds `variants` (label, on, slot, applies_next_launch) or `variants_note`; there is no write tool.

## Assistant (1.6.0)

The Assistant tab lets Claude browse the user's library and installed outfits through 12 app tools. It then performs app actions only through the same backend calls the GUI uses.

**Transport and history.**
- ClaudeClient posts to https://api.anthropic.com/v1/messages over raw HTTPS (HttpClient, TLS 1.2/1.3, 10-minute timeout). The official SDK requires a newer .NET than this C# 5 / .NET Framework build.
- Each request carries a constant system prompt, the 15 strict tools in a fixed order (2.0.2 removed view_preview), tool_choice: auto, adaptive summarized thinking, an explicit output_config.effort, top-level automatic cache_control, and fallbacks: "default" with the server-side-fallback-2026-07-01 beta header. Haiku 5.5 has no server fallback, so neither is sent for it.
- AssistantConversation keeps the history as serialized message strings and only ever appends to it. Assistant turns are replayed byte-for-byte as received, including thinking signatures, so model or effort switches between turns never edit history.
- All tool_result blocks for one assistant turn go back in one user message. Unanswered tool calls (after a refusal, max_tokens, pause or Stop) are closed with error results at the start of the next user message.
- One prompt can make at most 40 model calls.
- Retries: 429 honours 
etry-after; 5xx, 529 and network errors back off with jitter; at most 3 attempts. Other 4xx errors are reported with their request-id.

**Tools and IDs (AssistantTools.cs, AssistantWorkspace.cs).**
- Read-only tools run at once: overview, search, options, check, list installed, list presets (2.0.2 removed the preview-image tool).
- Mutating tools are queued as numbered AssistantActions unless the session's auto-apply is on: install or replace, enable/disable, uninstall, save preset, restore preset.
- Inputs are checked against each tool's schema before use. Installs are validated with Composer.Plan, the files' ESF character, folder-name rules, collisions with installed and queued names, and the managed-only replace rules.
- AssistantActionRunner.RunBatch re-validates each approved action just before running it, runs actions in order under OperationContext, and stops at the first failure.
- Uninstall uses OutfitSetStore.RemoveOutfits, so it is recoverable. Claude has no tool for permanent deletion, settings or Explorer.
- IDs are short SHA-256 hashes: mod- of the source path, out- of the managed manifest ID (or the path for manual entries), and set- of the set GUID.

**Data sent to Anthropic.**
- Sent: the user's messages; mod, option and outfit names; descriptions (shortened to 300 characters); warnings; counts and sizes; loader labels. No images (2.0.2 removed the preview-image tool and its Settings option).
- Not sent: local paths. Tool output uses IDs, and error text passes through AssistantWorkspace.Scrub, which replaces known roots and any drive path with placeholders.

**Key storage.**
- Data\claude-key.dat holds the key encrypted with ProtectedData (DPAPI, CurrentUser scope, app-specific entropy). If no key is saved, the ANTHROPIC_API_KEY environment variable is used.
- The UI shows only the last four characters.
- The key is removed from every error message. The diagnostics file Data\assistant-log.txt records only time, status, attempt, timing and request-id.
- Preferences (model, effort, auto_apply default; an allow_images value from 2.0.1 or earlier is ignored and dropped on the next save) are in Data\assistant-settings.json; settings.json is unchanged.

**Prompt injection.**
- The system prompt states that mod names, descriptions, file names, image contents and metadata are untrusted data, never instructions. Only the user's own messages express intent.
- Even when such text steers Claude, it can only queue the same validated actions the user must approve, unless the user turned on per-session auto-apply. That needs confirmation and shows a red warning.
- Claude is told that a queued action has not run. The [App] note reports what actually happened.
- 2.2.0: a **Content** section follows **Security**: adult-themed mods are managed like any other mod, without moralising, warnings or euphemisms, and referred to by name; all SF6 fighters are adults; descriptions stay short and factual. Metadata is still data, never instructions.
## Replace original (2.0.0)

Extra outfits (Slotsloader) are unchanged. Replace original installs a composed selection over the game's own files, as external mod managers do, and installs tool mods (files outside `natives`) into the game folder. It is one engine with two **methods**.

**Methods.**
- **Loose** (REFramework's loose file loader): loose files only; no PAK is touched. It is the automatic default when REFramework is installed (`dinput8.dll` plus the `reframework` folder). It needs `LooseFileLoader_Enabled=true` in `re2_fw_config.txt`. The app asks once, backs the config up to `Data\refw-config-backups`, and changes only that key, preserving every other line, the order, the BOM and the line endings. The game must be closed for this, because REFramework rewrites the config on exit.
- **PAK**: for each `natives/...` file, every base and DLC PAK row whose path hash matches gets its two hash fields zeroed (8 bytes at `16 + index * 48`). The original values are recorded per row. This is exactly what Slotsloader 1.9.x recognizes as mod manager invalidation: its fingerprint ignores the hash fields, and its vanilla inventory still protects those keys. It is the default without REFramework.
- 2.5.0: **never loose files with the loader off.** `MainForm.EnsureLooseLoader` returns `usePak` whenever natives files would go loose while `LooseFileLoader_Enabled` is off (REFramework missing or its config unreadable included), also with an explicit loose `ReplaceSettings.Method`: `InstallReplacement` then installs through a per-install engine with `Method = pak` (the saved setting is unchanged; older loose installs are converted with `SwitchMethod` as before) and the status line adds ` · PAK method used: loose file loader off`. The health check's loader line is information: "Loose file loader off: the app installs these with the PAK method instead." (`ReplacePlanner`). Checked by `replace-ui` → AutomaticMethod.
- `ReplaceSettings.Method` empty means automatic. Once replacements exist, `ReplaceState.Method` is authoritative. `SwitchMethod` converts by reconciling with the other method: PAK → loose restores the rows the app zeroed and keeps the loose files; loose → PAK zeroes the rows.

**PAK rules.**
- `PakIndex.Open` refuses a PAK that is not KPKA 4.x with `features == 0`, or whose table doesn't parse exactly (count and every entry inside the file).
- Patch PAKs (`re_chunk_000.pak.patch_*.pak`, for example Slotsloader's) are never modified. A path they contain is an error with the PAK method and a warning with the loose method.
- Before the first change to each PAK identity (length, fingerprint and layout hash ignoring the hash fields), its full header and entry table are saved to `Data\pak-backups\<pak>-<length>-<fingerprint>-<layout>.toc` with a `.json` stamp, and kept.
- `PakWriter.WriteHashes` opens with `FileShare.Read`, rechecks the length and header, requires every row to hold the expected bytes, writes, calls `Flush(true)` and re-reads each row.
- Only rows the app zeroed are ever restored. `RestoreFromTocBackup` is a last resort for a lost record: it restores rows that are zero now and non-zero in the backup, but only when the layout still matches and the path is not owned by an external mod manager.

**State and files (all in `Data`; existing formats unchanged).**
- `replace-settings.json`: default and last install mode, store location, method, loader consent, crash-reporter rename, extra external manager roots (`FluffyRoots`).
- `replace-state.json` (`sf6-replace-state-v1`):
  - `Mods` in priority order (index 0 wins shared files): id, name, source, selected IDs, kind (Replacement/Tool/Mixed), enabled, test flag, files (path, size, SHA-256, fixes) and notes;
  - `Deployed`: each game file the app currently provides, with its owner, hash and the zeroed rows (PAK, index, original lower/upper);
  - `Originals`: pre-existing files the app overwrote, backed up in `replace-backups`;
  - `Paks` stamps, `BaseAtInstall`, `CreatedDirs`, `CrashReporterRenamed` and a `Generation` counter.
- Replacement store: `Data\replace-store\<mod id>\...` holds each installed mod's prepared (fixed) files, so enable, disable, priority changes and Reapply re-materialize without re-reading archives. Uninstall deletes that copy after commit.
- `replace-journal.json`: the intent of one change (see below).
- `test-session.json`: a Test-in-game session left by 2.2.0 or older (2.3.0 only reads it once at start-up, keeps the install, restores the logging keys and deletes it).
- 1.0.9, in the game folder: `reframework\data\SF6_Costumes_Data\manager_installs.json` (`sf6-manager-installs-1`, the format the upstream loader's author proposed). It lets other costume loaders leave the Manager's replacements alone without searching for `Data\replace-state.json`. It is written by `ManagerInstalls` after every committed change and at start-up and every refresh, only when that folder exists (a costume loader keeps its data there).
  - Layout, keys in this order, 2-space indent, LF, UTF-8 without BOM: `format`, `generated` (UTC), `manager` ("Slotsloader Manager x.y.z"), `mods`.
  - `mods` is every record of `replace-state.json` in priority order (first = the one whose file is in the game). Each has `id`, `name`, `installed_utc`, `enabled`, `files` and `paks`. Each file entry is `{ "path", "sha256", "size" }`, where `path` is relative to the game folder with `/` and `sha256` is lower case.
  - `paks` are the `re_chunk_000.pak.patch_NNN.pak` files; `files` is everything else.
  - Files kept with "Keep current file" (`Released`) are not listed. `mods` is `[]` when nothing is installed.
  - Rewritten whole (temporary file, then replaced) only when something besides `generated` changed. A failure is logged and never fails the change.
  - Readers match files by size and treat `enabled: false` as claiming nothing. `generated`, `manager`, `id` and `installed_utc` are for logs only. Adding fields keeps the format string; an incompatible layout would get a new one.

**Transactions.**
- `ReplaceEngine.Change` takes a named mutex for the game root and refuses while SF6 or an external mod manager runs (`GameProcesses.Probe`). The one exception: with the loose method, a change made only of `natives` file operations may run while SF6 runs. It then runs `Recover()` and builds the next state.
- `Reconcile` turns the current state into the next one. It computes the desired owner of every path (the highest-priority enabled mod) and plans these operations:
  - file operations (`path: before SHA/source → after SHA/source`), whose sources are store files, original backups or kept drift copies;
  - PAK row operations (`before → after` bytes);
  - the crash-reporter rename (`CrashReport.exe` ↔ `CrashReport_FMMBACKUP.exe`, only when the app made it).
- Ordering: entry restores first, then files, then renames, then entry zeroing. The game therefore never sees a zeroed entry without its loose file.
- Pre-existing files are backed up before the journal is written.
- Execution: the journal is written with `Flush(true)`. Each operation runs and is verified (file copies are hashed before they replace the target; PAK rows are re-read). The new state is then written durably with `Generation + 1`, and the journal is deleted.
- An ordinary failure rolls back in-process. Every operation is idempotent in both directions: a target already in the wanted state is skipped, and an unexpected state is reported, never overwritten.
- `Recover()` at startup, and before every change, finishes a committed journal (state generation ≥ journal) or rolls an uncommitted one back.
- A file changed outside the app is refused on disable or uninstall. Reapply keeps a copy of it, then puts the installed version back.

**Game updates.** `Status()` compares each held PAK's identity and checks that every zeroed row is still zero. It also checks deployed files' presence and size, and the base PAK identity since the last apply. The UI banner and `replace-status` report "Game files changed — reapply". `Reapply` does the following:
- re-runs the fixers on stored files with the current game's type signatures;
- drops rows recorded against an updated PAK layout, since the update already restored them;
- re-zeroes rows and re-copies files.

**External mod manager coexistence.**
- `FluffyInstalls` parses `installed.ini` in every external mod manager copy found next to a configured `Mods` library (`<manager folder>\Games\SF6`). A missing file means no such mods.
- Any listed path is owned by that manager. Planning and reconciling refuse to overwrite it and name the mod to disable first.
- Its rows appear read-only on Installed outfits.
- The app writes nothing into the manager's folders. It reads `.prt` presets for import.

**Tool and root files.**
- `PackageReader` adds `RootAssets` with the external mod manager layout:
  - files beside a `modinfo.ini` go to the game root, relative to that folder;
  - without any `modinfo.ini`, single wrapper folders are stripped (only for packages that have no native files);
  - top-level previews, `.txt`, readme and licence files, `modinfo.ini`, `desktop.ini` and `__MACOSX` are skipped.
- `RootAssets` never adds tree nodes to native-file packages, so extra-outfit behaviour is unchanged. `Composer.Plan(package, ids, replaceMode: true)` includes them and skips the mesh/route rules.
- `ReplaceEngine.ValidateDestination` refuses: root executables the game folder already has (one it doesn't have, such as Slotsloader's `SF6_SlotsClean.exe`, is skipped by `ReplacePlanner` with a note; `ReplaceEngine.SkippedProgram`) and `steam_api*`, any `.pak` outside natives, `dlc/`, `re2_fw_config.txt`, extra-outfit folders below `costume_mods/<Character>/`, and the app's own `.slotsloader-installer-*` storage folders.
- An overwritten original (for example the genuine `amd_ags_x64.dll` replaced by the Slotsloader proxy) is backed up and restored exactly. Empty folders the app created are removed on uninstall.

**Fixes (Replace mode only; Slotsloader applies its own at game start).** `ModFixes` ports Slotsloader's `upgrade_mod_user` (from `patch.cpp` and `loader_core.cpp`):
- textures get the mip-table check and repair;
- user files get the 2023 colour-layout conversion, otherwise the allow-listed CRC upgrade (`0x9E32F1F4 app.CostumeMaterialData.MaterialData`); other stale types are reported;
- the current signatures come from the vanilla `esf001_001_cmd_000.user.2` decoded from the base PAK (zstd).

`ReplacePlanner` also does the following:
- normalizes paths;
- renames `.tex.143230113` to the current texture version (from the Slotsloader index if installed, otherwise the built-in October 2026 table);
- renames older `.mesh` versions when the current-version path is vanilla.

All 2,099 outdated files found in the user's ZIP libraries produce byte-identical output to the C++ build. The managed zstd decoder matches the reference `zstddeclib` on 2,051 sampled real entries (932 MB).

**Health check (`ReplacePlanner.Check`).** It prepares into a temporary folder and reports:
- the vanilla outfits affected, using the costume table (`Cammy Outfit 2 (esf009/002)`);
- vanilla and new file counts, and the rows to invalidate with their PAK and index;
- fixes, stale types and unrepairable textures;
- non-vanilla costume folders (never loaded by a replacement), outdated version suffixes, and streaming twins the mod doesn't ship;
- External mod manager ownership, conflicts with installed replacements, patch-PAK paths, root files and backups, and loader state.

`CanInstall` requires no errors.

**Test in game** (2.0.0–2.2.0) is removed in 2.3.0. A leftover `Data\test-session.json` is finished once after the window is shown (`MainForm.FinishLeftoverTest`, retried by the game poll while SF6 runs or a task is busy): `TestSession.FinishLeftover` keeps a Replace test as a normal replacement (`ReplaceEngine.KeepTest`), restores the REFramework keys saved in the record (`RestoreConfig`, e.g. `LooseFileLoader_LogAccessedFiles`), clears an extra-outfit or already-removed record, deletes the file and logs "Test in game (removed in 2.3.0): …" to `Data\log.txt`. No prompt.

**Not supported.**
- PAK-distributed mods in Replace mode. Adding a patch PAK above Slotsloader's would break its contiguous numbering, because Slotsloader deletes and renumbers its own pak. Extra outfit mode still accepts PAKs in `costume_mods`.
- The external mod manager's `pak_mods` folder.
- Replacing executables.

## Automatic review, conflicts and costume search (2.0.1)

**Automatic review** (`MainFormReview.cs`). `RequestReview(delay)` bumps `reviewGeneration`, cancels a running review's token and restarts a 300 ms `Timer`. The tick starts at most one `BackgroundWorker` (never while `busy`; it polls every 150 ms until the app-wide work ends). The worker composes (`Composer.Plan`) and, in Replace mode with an installable composition and no open required choice, runs `ReplacePlanner.Check(composition, sevenZip, engine, PrepareCache, updateId)`. Completion applies only when generation, package object and mode are unchanged; otherwise the result is dropped (`ReviewDiscarded`) and the newest request starts. `ReviewPending` (applied generation ≠ requested generation) gates Install, so an install always uses the result of the current selection, and the engine re-validates at install time as before. Callers: `ResetPlan`, `FillComponents`, `SetInstallMode` (via `ResetPlan`), Settings save, and `ApplyGameRows` (every refresh/install/uninstall). A static `ReviewTestDelay` keeps a run in flight for the supersede check.

**Check cache.** `PrepareCache` (one per loaded package, in `%TEMP%\sf6-check-cache-*`, deleted when the package changes or the window closes) stores each extracted and fixed file under a synthetic name, keyed by `ReferenceKey(game facts) | entry | size | destination`. Archives only (folder sources are read directly); a changed archive size/date clears it. `Stage` extracts only the missing entries in one `CopyFiles` call. The listing reuses `ModPackage.PreviewEntries` when its stamp matches. Install (`PrepareAndInstall`) does not use the cache.

**Conflicts.** `ReplacePlanner.AnalyzeConflicts(paths, state, updateId, reference, sourcePath, selectedIds)` compares the plan's destination paths with every installed replacement (enabled and disabled; the replacement being updated and an identical same-source selection are skipped). Areas come from `AreaKey`: `esfNNN/CCC` for model files (CCC = 000 is shared parts, not a slot), the first folders for other natives files and game-root files. Kinds: `slot` (a common slot area and shared files: warning with an Uninstall link), `files` (shared files without a common slot: warning, no link), `stack` (common slot, no shared files: information). `NewWins` is true for a new install (it goes to index 0); an update keeps its index. Texts name the mod, state, install date and file counts. External mod manager ownership is one error per such mod; natives files already in the game folder that neither this app nor an external mod manager installed are listed in `UnknownLoose` (the engine backs them up as before). Nothing is removed automatically: installs only add on top.

**Recoverable uninstall.** `ReplaceEngine.Uninstall(id)` (Installed page, inspector link, Assistant, CLI) passes the mod to `Change` as a trash target. The journal's `TrashAfterCommit` moves `<store>\<id>` to `<store>\.removed\<id>-<time>-<rand>\files` with `record.json` (`ReplaceRemovedRecord`: the mod, its list index, game root) after the commit, and again during recovery of a completed journal. `RestoreRemoved(folder)` verifies every stored hash, copies the files back into the store inside the `Change` mutation, inserts the mod at its recorded index with its enabled state and deletes the removed copy after the commit (a failed change leaves it intact). `ListRemoved`/`DeleteRemoved` back gear → Recover removed outfits. Update in place keeps nothing, as before; Restore original game files is unchanged.

**Costume search** (`LibrarySearch.cs`, shared by the library and the Assistant's `search_mods`). Terms `c1`, `c01`, `o1`, `costume 1`, `outfit 1` and the token `costume:1` mean folder 001. Ranks: 0 = a native key for the costume (and character, when one is named), 1 = only the named character's shared parts (000), 2 = the title says the costume (unreadable sources fall back to this). Name words and `is:`/`format:` filters combine as before; ranks order the list.

## Tool mods, library uninstall and themed dialogs (2.0.3)

**Tool kinds** (`Library.ToolKind`, `Core.cs`). While `DetectIdentity` lists an archive or folder once, every file outside `natives` is classified; wrapper folders before `reframework/` are allowed (a modinfo component, a single wrapper folder), `__MACOSX` and `desktop.ini`/`Thumbs.db`/`.DS_Store` never count:
- `script`: `…/reframework/autorun/**.lua`;
- `plugin`: `…/reframework/plugins/**.dll`;
- `dll`: any other `.dll` (game-root `dinput8.dll`, `amd_ags_x64.dll` proxies, a DLL elsewhere under `reframework/`);
- `reframework`: any other file under `reframework/` (data, fonts, colour JSON).

`LibraryEntry.ToolKinds` keeps them in that order and the v3 cache stores them. This follows `PackageReader.CollectRootFiles`, which installs such files to the game root (and, beside natives files, keeps only `reframework/` ones), without its component bookkeeping: the index only needs the kinds.

**Tool mod** (`LibrarySearch.IsToolMod`): a `script`, `plugin` or `dll` kind, or `reframework` files when the mod has no natives options (`OptionCount == 0`). Costume mods that carry REFramework data or colour JSON beside natives files are therefore not tool mods (their kinds are still recorded and returned to the Assistant).

**Search** (`LibrarySearch.ToolKeyword`, whole term, case-insensitive, spaces collapsed): `reframework`, `ref`, `code`, `tool`, `tools`, `tool mod(s)`, `code mod(s)` → any tool mod; `plugin(s)` → plugin; `script(s)`, `lua` → script; `dll(s)` → dll or plugin. A plain query or a comma term that is a keyword matches by kind **or** by the usual name/path substring. `is:tool`/`type:tool` and the Filters type **Tool mod** keep only tool mods (`MainForm.MatchesModType`). Rows without a costume label show `ToolLabel` ("REFramework plugin", "REFramework script", "DLL", "Script + plugin", "REFramework files"). The Assistant's `search_mods` has `kind: "tool"`, treats keyword words (and "tool mods"/"code mods" pairs) the same way and returns `tool_kinds` (+ `tool_label`).

**Library uninstall of replacements** (`MainForm.cs`). The library's right-click menu and footer button look up the clicked/loaded source in the Installed page's `gameRows` (`LinkedReplacements`, exact source path, case-insensitive, the same match as the footer's "Already installed as"). One replacement → **Uninstall replacement** (**Uninstall tool mod** when every row is a tool mod) runs `UninstallGameRow` (confirmation, recoverable `ReplaceEngine.Uninstall`); several → a submenu "Replacement #1 · name". "Uninstall outfit" stays for extra outfits and is hidden only when the source has replacements and no extra outfit. While an external mod manager (or SF6 with the PAK method) runs, the item is disabled with "(close the external mod manager first)" and the inspector's reason as its tooltip (`ReplacementUninstallBlocker`). The footer button (Extra outfit mode: "Uninstall outfit"; Replace mode: "Uninstall", shown only for such a source) offers outfits and replacements in one chooser.

**Themed dialogs** (`ThemedMessageBox.cs`). Every former `MessageBox.Show` calls `ThemedMessageBox.Show` with the same arguments. Each show reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` (0 = dark; missing/unreadable = light; `ForcedDark` overrides it for tests). Dark uses `Palette` and `WindowChrome`'s dark caption; light uses system colours and native buttons. `ThemedMessageForm` lays itself out in physical pixels for one DPI (the owner's, re-laid out on `DpiChanged` or when created on another monitor): 9 pt Segoe UI, text wrapped at 520 logical px (less on small screens), long text scrolls inside the working area, buttons at least 88×26 logical px, centred on the owner and clamped to its screen. Windows rules: button sets/order, default button, Enter, Escape and the close box (Cancel if present, OK for an OK box, otherwise none: Yes/No and Abort/Retry/Ignore have no close box), Alt mnemonics (&Yes, &No, &Abort, &Retry, &Ignore), the icon kind and its system sound, and Ctrl+C copies the standard "---" text. `Program.Headless` guards stay at the call sites as before.

## External mod manager link, other tools' files and dialog scaling (2.2.0)

**One-way external mod manager link** (`FluffyLink.cs`, `GameEnvironment.cs`). The Manager reads and manages the external mod manager's installs; it never writes its own installs into that manager's records.
- `FluffyInstalls` parses every detected `<manager folder>\Games\SF6\installed.ini` (sections `[name]` with `ModID_v1`, `ModName`, `ShortModID_v1`, `installtype`, repeated `file=` lines, `/` or `\`, any case). It is read shared with retries (the manager writes `installed.ini.new` and replaces the file); absent = no mods. `Owner(path)` is the last section listing a path (its copy is in place: the manager appends each install), `OwnersOf(path)` all of them, `Stamp` the file's length + write time.
- `FluffyWatcher` (FileSystemWatcher on `installed.ini*`, 250 ms debounce) plus a stamp check on window activation and every 1.5 s poll refresh the Installed page (`MainForm.FluffyChanged`; deferred while busy).
- `ReplaceEngine.UninstallFluffy`(root, section, out folder) follows the external mod manager's own Log.txt semantics: a listed file another remaining section lists is kept (when the removed mod was installed later, the other mod's copy is put back from `Mods\<name>` or `Mods\<name>.zip`: "Restored file from other mod"); otherwise `Games\SF6\Backup\<path>` is copied back and deleted ("Restored file by copying …Backup…"); a file this app manages is left to it; otherwise the file is deleted and a `<stem>_FMMBACKUP<ext>` beside it is renamed back. Emptied folders go, except `natives`, `natives/stm`, `reframework` and its `autorun`, `plugins`, `data`, `fonts`, `costume_mods`, `images`, and `dlc`. With no external manager mod left, `CrashReport_FMMBACKUP.exe` goes back to `CrashReport.exe` unless this app's own enabled replacements want it renamed (then this app takes the rename over). The section's byte range is cut out of the exact bytes (BOM, line endings, comments and every other byte unchanged), after a copy to `Data\fluffy-ini-backups` and a re-read that proves the manager did not write meanwhile; an `installed.ini` left without sections is deleted (the manager keeps none while nothing is installed). Everything removed is copied first to `Data\fluffy-removed\<name>-<time>\` (`record.json` `sf6-fluffy-removed-v1`, `files\`, `fluffy-backup\`, `installed.ini.before`); a failure puts back what changed. `RestoreFluffyRemoved` (Recovery / `manager-restore`) refuses over files changed since, and puts the section back at its offset (or at the end when `installed.ini` changed). The manager open, or SF6 running, refuses both.
- 2.4.2: `invalidate` and `pakcopy_reengine_invalidate` installs are uninstalled here too (`FluffyPakUninstall.cs`): each zeroed row is restored only when proven, by the Manager's TOC backup (same PAK), the external mod manager's `ArchiveList.bin` (which stores every row's original hash; used only when it agrees with every live row), or hash(path) when it is the only known path hash between the row's live or proven neighbours (`PakRepair.Prove`). Any unproven row a path of the mod could fill refuses the whole uninstall. Rows the Manager or any current installed.ini owns are never written. The mod's patch PAK is deleted, or replaced by the manager's 16-byte dummy when a real PAK sits above it; dummies above the last real PAK go. The removed record keeps the restored rows and PAK copies for an exact Undo. Other installtypes (`pak_mods`, …) are still refused. Orphaned zeroed rows (owned by nobody) are reported by `ReplaceEngine.AnalyzeZeroedRows` and repaired with the same proofs (`RepairOrphanedRows`, CLI `pak-repair`).

**Files other tools change** (`ReplaceEngine.Reconcile`, `ReplaceModels.cs`). A deployed file whose hash is neither this app's copy nor the recorded original was changed by another tool; `KnownBuilds.Changer` names it: the external mod manager section listing it, else a SHA-256 table (Slotsloader 1.9.2–1.9.7 proxies/plugins, the Mod Suite 1.11.2 build, the genuine AMD AGS library), else "another tool". `DriftChoice`: `Refuse` (enable/disable/priority/install: refused, changer named), `Ask` (uninstall, restore-all: `DriftException` lists the files, missing ones with a backed-up original included; nothing changes; the window asks, the CLI wants a flag), `RestoreOriginal` (the genuine file or the next replacement's copy is written; the other file goes to `ReplaceState.ForeignCopies`, `Data\replace-backups\changed-*.bin`), `KeepCurrent` (the file stays; `ReplaceState.Released` keeps the genuine backup restorable; this app's PAK rows for it are restored unless an external mod manager lists the file), `Repair` (Reapply; it also takes released files back). An uninstall only decides about files whose owner changes; a Manager file an external manager mod also lists (identical copy, e.g. `amd_ags_x64_real.dll`) is left to that manager. Restore-all includes released files in its question. Recovery lists `Released` originals (`RestoreReleasedOriginal`, `DropReleased`) and `ForeignCopies` (`RestoreForeignCopy`, `DeleteForeignCopy`). The priority stack is the replacement list: one `DeployedFile` and one `OriginalBackup` per path, the highest enabled owner deployed, the next owner or the original restored on uninstall in any order.

**Dialog scaling** (`ThemedMessageBox.cs`, `DialogCheck.cs`). The 2.0.3–2.1.0 dialog used a pixel font; on the user's machine GDI+ reports 96 DPI for screen DCs while GDI and the window DCs are at 288, so TextRenderer converted 40 px to 30 pt (at 96) and drew 30 pt at 288 DPI: text 3× too large in a box measured for the normal size. 2.2.0 builds the dialog like the other dialogs (96-DPI logical units, `AutoScaleMode.Dpi`, `Palette.Normal` in points inherited by every control, `PerformAutoScale`), then lays it out in device pixels on its monitor (`OnLoad`, again after `DpiChanged`) measuring with the inherited point font at GDI's DPI (`GdiDpi`, `GetDeviceCaps(LOGPIXELSY)`); fonts replaced while the handle was created are reset to the inherited one; light-theme native buttons get a DPI HFONT (`NativeDpiFont`). Each shown dialog logs one `Dialog '…'` line (DeviceDpi, LayoutDpi, font, line height, client size, scrolling) to `Data\log.txt`. `--dialog-check` starts the full app like a user launch and compares the ink height of the dialogs' text and buttons on screen with a main-window label in `Palette.Normal`.
## Updates, costume pak, tooltips, Assistant knowledge (2.4.0)

**HTTP** (`SafeHttp.cs`): the one HTTP path (the pinned loader download, the update check, update downloads). `GetText` (small answers, size-capped) and `Download` (to `<name>.part`, size cap, incremental SHA-256, rename only after size and hash match, `.part` deleted on any failure). Manual redirects, each target HTTPS (plain http only to loopback when a test sets `AllowLoopbackHttp`), system proxy with default credentials, time-outs, `OperationContext` cancellation → `Abort`. Failures are `DownloadFailure` (reason, HTTP status). `LoaderRelease.Download` wraps it (`LoaderFetchException`).

**Update check** (`UpdateCheck.cs`): `GET https://api.github.com/repos/ZZtaii/slotsloader-manager/releases?per_page=100` (User-Agent `Slotsloader-Manager/<ver>`, 10 s). Per product (`loader-v*`; `manager-v*`, the private builds' `installer-v*` ignored since 1.0.0): skip drafts and, unless enabled, pre-releases; the highest version whose asset verifies wins (single `.zip`, the app prefers `*-portable.zip`; SHA-256 from the asset `digest`, else `SHA256SUMS.txt`); others are `Refused` and logged. `UpdateSettings` (`Data\update-settings.json`): `AutoCheck`, `IncludePrereleases`, `LastCheckUtc`, `Last` (the last successful result), `Skip*`, `Dismissed*`. Offers: `LoaderUpdate(result, installed)` (newer than the record's version, not below `LoaderPin.Release`), `ManagerUpdate(result, running)`, `InstallPin(last)` for the install link. `InstalledLoader.Version` reads the recorded source's `modinfo.ini` `version`, else the version in its file name.

**Window** (`MainFormUpdates.cs`): `StartUpdates` (Shown, not headless): `AppUpdater.CleanupAfterUpdate`, then a 4 s timer → `RunUpdateCheck(false)` when due (24 h), else the cached toasts. The check runs on a background thread and comes back with `BeginInvoke`; a failure is one log line. Toasts live in a `FlowLayoutPanel` (bottom-up) placed above the status strip and brought to front. Slotsloader update: verified download → `OpenLoader` → `LoadPackage` (forced Replace); `ApplyReview` calls `ContinueLoaderUpdate`: no warnings (the review's `updateId` is the recorded Slotsloader, so it is not its own conflict) → one confirmation → `InstallReplacement` with `PendingLoaderReplaceId()` (in place, same priority). Manager update: verified download → `AppUpdater.Stage` → confirmation → `AppUpdater.Apply` in `Work` → relaunch and close.

**Self-update** (`AppUpdater.cs`): `Stage` extracts to `Data\updates\<version>`, requires exactly the 7 release files and `FileVersion` = tag. `Apply` probes the app folder for write access, copies the current 7 files to `Data\updates\previous`, renames the two exes to `*.old`, copies the new 7; on any failure the copied exes are deleted, the `.old` renamed back and the other files restored from an undo copy. `CleanupAfterUpdate` deletes `*.old` and logs the recorded `applied.txt` once. `Rollback` applies `previous` the same way.

**Slotsloader costume pak** (`LoaderCostumePak.cs`): the loader's own recognition (loader_core.cpp `classify_patch_paks`, common/pak.cpp `classify_patch_pak`): a `re_chunk_000.pak.patch_NNN.pak` whose entry table holds the hash of `natives/stm/sf6_costume_slots.marker` is the loader's (one entry = its marker-only placeholder); `natives/stm/sf6_stage_slots.marker` = the stage pak; anything else = another mod's (never touched). `Remove`: the loader's paks above every foreign pak are deleted, the ones below are replaced by the loader's marker-only placeholder (`write_marker_pak` bytes), `registry.json`'s `fingerprint` is cleared (slots kept) so a reinstalled loader rebuilds. `ReplaceEngine.Uninstall`/`RestoreAll` call `AfterLoaderRemoved` when a Slotsloader record (its plugin path) was removed and no Slotsloader is left in the game folder. `GameFilesStatus.OrphanCostumePaks` (no Slotsloader, a real loader pak) drives the banner's Remove it and `replace-status`. The in-place update never removes the pak (the new loader rebuilds on its build id).

**No tooltips** (`UiAudit.cs`): no ToolTip component or tooltip text exists; `UiAudit.ToolTipCode` scans every method body (newobj/call to ToolTip, set_ToolTipText, set_AutoToolTip), `ToolTipFindings` walks a window. Disabled reasons are `AccessibleDescription`s; visible homes are listed in UI-REFERENCE 2.4.0.

**Assistant** (`AssistantAgent.cs`): `AssistantPrompt.SystemJson()` = [instructions, knowledge pack with `cache_control {ephemeral, 1h}`]; the request keeps the top-level automatic `cache_control` for the messages (5 min, after the 1 h entry). Nothing volatile in tools or system; live state comes from tools (`get_app_status`). Each response logs input / cache read / cache write / output counts. Default model Claude Haiku 5.5 (2.4.0; `AssistantModels.DefaultModel`, first in `All`): the same body as Sonnet minus `fallbacks` and the fallback beta header (Haiku has no server-side fallback); adaptive summarized thinking, explicit effort, no sampling or `between_tools`; the 1 h breakpoint on the knowledge pack (about 20k tokens, above the 512-token minimum). A refusal ends the turn with one line and no retry.
## Pinned Slotsloader build (2.3.1)

`OfferSlotsloaderInstall` runs `LoaderRelease.FindLocal` in `Work` (top-level files of each configured library whose length equals `Size` are hashed with SHA-256; then `Data\downloads\<asset>`, deleted if it no longer verifies). Found: the usual Yes/No question, then `LoadPackage` in Replace original. Not found: a Yes/No question naming the asset, size and origin, then `LoaderRelease.Download` in `Work`: `HttpWebRequest` with `AllowAutoRedirect = false` (each `Location` must be HTTPS; up to 5), the system proxy with default credentials, 30 s `Timeout`/`ReadWriteTimeout`, `CancellationToken` → `Abort`; the body streams into `<asset>.part` with an incremental SHA-256 and `OperationContext.Report` progress, a `Content-Length` above `Size` or a body longer than `Size` stops it; then length and hash must equal the pin before `File.Move` to `<asset>`. Every failure deletes the `.part` file and throws `LoaderFetchException` ("Couldn't download Slotsloader 1.9.7: <reason>. Download it from <release page> and drop the zip on the window."), shown by `Error`. No token or `gh` authentication: the private repository answers 404 until it is public. Nothing is written into a library.

## Filters panel, portraits and categories (2.3.0)

- **Categories** (`ModCategories.cs`) are decided in the library listing pass (`Library.DetectIdentity`, index **v4** `library-native-index-v4.json`; v3 is left for older builds, every archive is listed once; a v4 record without `StageIds`/`MiscAreas` or with an invalid key is listed again). A mod is **Characters** when it has fighter files: a native path with `/esf/esfNNN/` or `/esfNNN_` (model incl. `_cmd_` colour files, charparam, `vfx/character/…/esfNNN`, weather/wind, animation), the fighter's own art `product/gui/data/character_image/NNN/`, or REFramework colour data keyed to a fighter (`ColourDataFighter`: a `colo(u)r` word inside `reframework/…` plus an `esfNNN` or whole fighter-name segment, e.g. `reframework/data/SF6ColorSync/Juri/…`; such files are not tool files). The codes go to `CharacterCodes`, so the fighter's card counts them. **Code tools** = `LibrarySearch.IsToolMod` (2.0.3). **Misc** only for a readable mod that is neither: its kinds are friendly labels (`MiscLabel`, `LabelMap`): Stage (a native path with a stage code `essNNNN`, the `_NN` after it ignored, or a stage folder without a code: `product/environment/`, `esmcontents/stage/`, `level/scene/ess`, `camera/scene/ess/`, `weather/wind/ess/`, `light/*/ess/`, `vfx/environment/`, `sound/sound_scene/ess/`, a `message/…/stagename…` file; NPC models of a stage pack are part of the stage), UI (gui, ui), Text (message), Sound, VFX, Lighting, Camera, Animation, World Tour avatar (model/wcs, wpl, wcf, wcm), NPCs (model/mob, npc), Shared character files (model/cmn, loose model/*.tex), Materials (mastermaterial, systems/rendering), Weather, PAK only (no natives, a .pak), Other (anything else: character/escape/charparam without a fighter code, scenes, no natives). `StageIds` (`"0800"`, `"other"`) are kept for every mod; the stage-name map gives the row labels. Raw folder names never reach the UI. The upstream `stage_paths.txt` (2947 paths, every one holding a stage code) was used to derive and check the stage patterns; it is not shipped.
- **Filtering** (`MainForm.ApplyFilters`): search and the shared filters (library, type, format, installed, costume) make the base set. Fighters (`FighterCodes`: native codes plus the detected/filename character) are OR-ed; the kind tiles (Code tools, Misc narrowed by its chips) are OR-ed and AND-ed with the fighters. Fighter counts are taken over the base ∧ tiles (using each fighter's costume folder), tile/chip counts over the base ∧ fighters. `RefreshFacetRows` rebuilds the fighter cards and the Misc chips when the library changes; chips restored before the scan apply when they appear.
- **Portraits** (`FighterPortraits.cs`, `TexDecode.cs`): `GamePakReader` opens base, `dlc\*.pak`, then `re_chunk_000.pak.patch_*.pak` (later wins; unsupported headers skipped) read-only (`FileShare.ReadWrite|Delete`) and resolves a hash set of candidate paths in one streaming pass over the entry tables (`PakHash`, zeroed rows never match), then reads entries with `PakIndex.Decompress` (zstd/deflate). Candidates per fighter: face `{id}/caracterselect/tex_esf{id}_face_l_l_iam` (030+: `characterselect` first), `pub/tex_{id}_cfn_iam`, `_1k_iam`, `_card_iam`, `new_challenger/tex_nextchallenger_esf{id}_00_00_iam`, `battlehud/tex_battlehud_{id}_1p_iam`, each with the current tex suffix (Slotsloader TSV or `GameTables`) then `143230113`; a texture that fails to parse/decode falls back to the next. Codes 001–060 not in `Characters.ByCode` are probed and become cards when found. `ReTex` parses the header/mip table (rejecting data outside the file); `BcDecoder` is the bcdec port (BC1–BC5, BC7). The tile is 300×420 (5:7) around the alpha-weighted centroid (faces full height, key art 10–90 %). Cache `Data\portraits\v1\{id}.png` + `portraits.tsv` (`#format`, `#identity` = per PAK `name|length|mtime ticks|header fingerprint`, a row per fighter incl. missing ones, `#stage` rows); PNGs and manifest via temp file + rename. hit = same identity and every requested fighter present; stale/miss re-extract. `MainForm.EnsurePortraits` runs it on a pool thread (cancellation token; each tile `BeginInvoke`d; disposed when the form is gone), waits while `ReplaceEngine.ActiveLocks > 0` (a change of this app holds the game lock), and does not cache a read during which a lock was taken or any PAK identity changed (retried after the operation / on the next open). Started on the first panel open, re-checked after every library refresh. No game folder: name tiles.
- **Stage names** (`GameMessages`, used for the Misc row labels): `natives/stm/product/message/cmn/001/wordlist/stagenamemessage.msg.<msg suffix>` from the same PAK pass; GMSG data decrypted with the RE Engine message key (XOR with the previous cipher byte and a 16-byte key), entries `StageNameMessageN` (N = NNNN×100 + MM) → English text (language 1). Cached as `#stage` rows.
- **Panel UI** (`FilterPanel.cs`): `FilterPanel` (AutoScroll; a body panel holding the header with **Clear**, fighter grid, kinds header, tile grid and Misc chips; no combos since 2.3.0; `Arrange` sizes the grids to their cards). Tiles: `tools`, `misc`, `installed` and `lib:<full path>` per configured library (`SetLibraries`: shown only with two or more, `GroupBreak = 3` puts a `GroupGap` before them, labels disambiguated by the nearest distinguishing parent folder, a lost selection raises `SelectionChanged`). `MainForm.ApplyFilters`: fighters OR; tools/misc OR, AND with fighters; installed AND; libraries OR, AND with the rest; search AND; each count is the result with that item added. `ActiveFilterCount` = fighters + kinds + installed + libraries. `ViewPreferences`: `InstalledOnly`, `SelectedLibraries` written; `Costume`, `ModType`, `ArchiveFormat` no longer exist (ignored on read); `Source` is read once to migrate (`[ScriptIgnore]`), `FacetSurface` base (owner-drawn items, focus item, keyboard, accessible children; a non-scrolling surface scrolls its scrolling parent to the focused item), `FighterGrid` (fighters and kind tiles; `KeepOrder` for the tiles), `KindArt.Placeholder` (the only place drawing tile art, to be replaced by real images later).
## Headless checks

`Slotsloader CLI.exe` shares the backend without creating a form. See [DEVELOPMENT.md](DEVELOPMENT.md) and `--help` for examples and flags.

- `list-options` reports component IDs, names, parents, native-file counts and normal defaults.
- `test-mod` composes selected IDs and verifies the winning bytes, ZIP CRC and SHA-256 without installing. Selection can be repeated `--select` values, an ordered JSON array, or a saved installer manifest for the same source. Omitting selection uses ordinary defaults; incomplete bundles fail rather than choosing arbitrary variants.
- `test-loader` additionally requires an explicit native executable, source game root and **new** sandbox directory separate from the source/game. It copies loader metadata and the composed outfit into that sandbox, runs the executable with a read-only base-PAK override, and retains the native reports/logs. DLC PAKs are omitted and listed. Unsupported native-wrapper path lengths/code-page characters are rejected.

JSON reports use a new destination or stdout (`--json -`). Exit codes are 0 for success, 1 for validation failure, 2 for argument errors and 130 for cancellation. `ModCheckReport.CanInstall` describes composition eligibility; integrity and native acceptance are separate fields.

Native acceptance requires at least one retained outfit, no rejected outfits and a generated PAK. Preservation evidence checks SHA-256 of original loader metadata and sandbox input assets, plus the base PAK header/index hash, length and modification time. It does **not** hash the complete base PAK. Native validation is stronger than a byte-copy check but remains separate from an actual SF6 rendering test.

## Evidence and limits of validation

Current counts and pass/skip results belong in the dated run reports, not this source map. `IntegrationTests` covers composition/install lifecycle and read-only real archives. `ExtraRegressionChecks` adds focused composition, archive, library, loader-result and recovery fixtures; `OutfitSetChecks` covers opaque mixed-entry roundtrips, additive conflicts/duplicates, disabled entries, record tampering, cancellation, mutexes and ordinary/blocked rollback. `LayoutChecks`, `InstalledSetUiChecks` and the UI exercise cover interaction/layout. Tests write new workspace fixtures and redirect the optional cache, preserving source mods and installed app Data.

Important fixture examples are Goth Juri's selected 49-file combination and Ingrid C.C. Barefoot's single 29-file composition. The native identity regression also uses the numeric Chun-Li ZIP and donor-named Viper RAR. Synthetic meshes/MDFs used for core filesystem tests are marker bytes; those checks intentionally do not establish binary game compatibility.

Archive fixtures include same-length CRC corruption, understated expanded sizes, ZIP64/data descriptors, changed folder files and cancellation. Library fixtures cover unavailable sources and recovery on refresh. Recovery junction checks can be skipped when the execution token cannot create junctions; a skip is not a passing safety test. Historical release snapshots and loader observations remain useful references, but do not establish validation or deployment of today's source.

For future work, review current source and the relevant test report before extending scope. Preserve source archives, settings, retained recovery folders and manual outfits outside an explicitly selected user action. Development tests still use workspace fixtures only. Update parser/composition tests when changing grouping or path semantics, bump the cache format/filename when changing identity rules, and retain the distinction between plan validation, byte-copy verification, loader validation, and an actual game test.
