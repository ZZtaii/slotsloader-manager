# Backend contracts (C# 5 / .NET Framework 4.8)

The 2.5.0 additions below were reviewed against the 2.5.0 source (2026-10-15). Reviewed against the **2.1.0 source, 2026-10-14** for the 2.1.0 additions below (app renamed SlotsLoader Manager, Slotsloader Manager since 2.5.9; namespace and formats unchanged); otherwise against the **2.0.2 source, 2026-10-14** (2.0.1 additions below; 2.0.2 changes only the Assistant model/effort catalog; 2.0.0 sections unchanged). Namespace: `SlotsInstaller`. Models use fields except computed properties noted below. This is an application contract, not a separately versioned SDK. See [architecture](../docs/ARCHITECTURE.md) for detailed rules and [development](../docs/DEVELOPMENT.md) for builds, checks and CLI examples.

## Replace original (2.0.0)

All types in this section are internal unless noted.

**PAKs: PakIndex.cs, Zstd.cs**
- `PakHash.PathHash(path) → ulong`: murmur3 hash, `upper << 32 | lower`. It is case- and separator-independent.
- `PakIndex.Open(path[, validateEntries])`:
  - reads the header and the entry table read-only (`FileShare.ReadWrite | Delete`);
  - refuses anything other than KPKA 4.x with `features == 0`;
  - validates every entry's bounds.
- `PakIndex` members: `Find(hash) → List<int>` (zeroed rows are skipped), `Contains`, `CountZeroed`, `Entry(i) → PakEntry`, `Read(entry)` (stored, deflate or zstd), `ReadByPath`, `TocSha256()` (header + table) and `LayoutSha256()` (hash fields ignored).
- `PakWriter.WriteHashes(pak, length, header, IList<PakHashWrite>)`:
  - rechecks the length and header;
  - requires each row to hold `Expected` (rows already at `Value` are skipped);
  - writes, calls `Flush(true)` and re-reads every row.
- `PakWriter.WriteFixture(...)` builds synthetic PAKs for tests.
- `Zstd.Decompress(byte[], expectedSize)`: an RFC 8878 decoder without dictionaries.

**Fixes: ModFixes.cs**
- `UpgradeModFile(ref byte[], GameReference) → List<FixResult>`: Slotsloader's `upgrade_mod_user`. `FixResult.Kind` is crc, layout, mip, badmip or stale.
- Individual fixes: `UserTypeCrcs`, `UpgradeUserCrcs`, `UpgradeCostumeMaterialLayout`, `TextureMipCheck`, `UpgradeTexSuffix` and `NormalizeNativePath`.
- `GameReference.Load(gameRoot, basePak, dlc)`:
  - reads the current type signatures from the vanilla `esf001_001_cmd_000.user.2`;
  - takes suffixes and the costume table from Slotsloader's `vanilla_costume_index.tsv` when installed, otherwise from `GameTables`.
- `GameReference.DescribeFolder(code, folder)` returns labels such as "Cammy Outfit 2 (esf009/002)".

**Engine: ReplaceEngine.cs, ReplaceModels.cs, ReplacePlanner.cs**
- `ReplaceEngine(gameRoot, dataDir, ReplaceSettings)`, or `ReplaceEngine.ForConfig(AppConfig)`. `FluffyProvider` supplies `FluffyInstalls`.
- `Install(ReplacePlan, name, test, replaceId) → ReplaceMod`. The plan's files must be staged inside the store.
- Changes: `SetEnabled(id, bool)`, `Move(id, delta)`, `Uninstall(id)`, `KeepTest(id)`, `Reapply()` (refixes stored files first), `RestoreAll()` and `SwitchMethod("loose"|"pak")`. Each returns a message.
- Every change holds the game mutex and refuses while SF6 or an external mod manager runs. The one exception is natives-only loose changes. Each change recovers first, journals and verifies, then commits `replace-state.json`.
- 1.0.9: after each commit, `ManagerInstalls.Write(gameRoot, state)` ([ManagerInstalls.cs](ManagerInstalls.cs)) writes `reframework\data\SF6_Costumes_Data\manager_installs.json` for other costume loaders. `ManagerInstalls.Sync(engine)` does the same at start-up and every refresh. Neither ever throws. The file is only written when that folder exists and something besides `generated` changed. `ManagerInstalls.Text(state, generatedUtc)` returns the text; the format is in docs/ARCHITECTURE.md.
- Recovery and status: `Recover() → string` (null if nothing was pending), `JournalPending`, `Status() → GameFilesStatus` (`NeedsReapply`, `PakChanged`, `BaseChanged`, `RowsNotZero`, `FilesMissing`, `FilesChanged`, `LooseLoaderOff`, `Summary`), `RestoreFromTocBackup(pak) → rows restored` (last resort).
- Other members: `LoadState()`, `CurrentMethod(state)`, `DesiredMethod()`, `StoreRoot(state)`, `EnsureTocBackup(pak)` and `ValidateDestination(path)` (static; protected paths).
- `ReplaceEngine.CrashAfterStep` is a test hook that throws `SimulatedCrashException`.
- Public models (JSON-serialized): `ReplaceSettings`, `ReplaceState`, `ReplaceMod`, `ModFile`, `DeployedFile`, `ZeroedRow`, `OriginalBackup`, `PakStamp`, `ReplaceJournal`, `JournalOp`, `JournalRow`, `TestSessionRecord`.
- `ReplacePlanner.Prepare(CompositionPlan replaceMode, sevenZip, engine, stagingFolder) → ReplacePlan`:
  - normalizes and renames paths, then copies and fixes the files;
  - analyses each file;
  - fills `Files` (`PreparedFile` with its fixes), `Affects`, `Items` (`HealthItem` level info/fix/warn/error), counts and `InvalidationPreview`;
  - sets `CanInstall` when there are no errors.
- `ReplacePlanner.Check(...)` prepares into a temporary folder and deletes it; `PrepareAndInstall(...)` stages in the store and installs; `Report(plan)` returns text.

**Composition and packages**
- `ModComponent.RootAssets` (public) holds game-root files in the external mod manager's layout; `HasFiles(includeRoot)` reports whether a component has files.
- `Composer.Plan(package, ids, replaceMode)`, `DefaultSelection(package, replaceMode)` and `UnresolvedChoiceGroups(package, ids, replaceMode)`. The existing overloads keep the extra-outfit behaviour.

**Environment: GameEnvironment.cs, GameSession.cs**
- `GameProcesses.Probe` and `Launcher` are replaceable in tests (`FakeProcessProbe`). `RequireIdle(action)` throws while SF6 or an external mod manager runs.
- `FluffyInstalls.DetectRoots(libraries,extra)` and `Load(roots)` read `installed.ini`; `Owner(path)` and `Parse(text, root)` resolve ownership.
- `ToolStatus.Detect(gameRoot)` reports REFramework, Slotsloader and the crash reporter; (`SlotsloaderPackages` was removed in 2.3.1; see `LoaderRelease`).
- `LooseLoader`: `State(gameRoot, out detail)`, `Read(key)`, `Write(gameRoot, key, value, backupDir) → previous`, `LoggedPaths(gameRoot, sinceUtc)`, `ParseLogLine`, `Matched`.
- `DurableFile.Write` / `WriteJson` write a temporary file, call `Flush(true)` and replace the target.
- `TestSession` (2.3.0: only for a record left by an older version): `FilePath`, `Load`, `Save`, `Clear`, `FinishLeftover(engine, record) → message` (keeps a Replace test via `ReplaceEngine.KeepTest`, restores `RestoreConfig` keys, deletes the record). `Tick`, `EnableLoadLogging`, `RestoreLogging`, `Finish`, `LoadedFilesReport` and `LoaderLog` were removed with Test in game.

**External presets: `FluffyPresets.cs`**
- `FluffyPresets.Parse(text)` → `List<FluffyPresetEntry>`.
- `Map(entries, library, sevenZip) → List<PresetMapping>`: one mapping per section, matched by folder/archive and modinfo names and verified against the package.
- `Group(mappings)` merges sections that map to the same library entry; `PresetFiles(roots)` lists preset files.
- `PresetInstaller.Install(config, engine, mappings) → summary` installs each mapping in its `Mode`; tool mods always install as Replace.

**Assistant additions**
- Four tools, for 16 in total: `list_game_mods`, `install_replacement`, `change_game_mod`, `import_external_preset` (2.5.1; the old name is still accepted for older transcripts).
- New action kinds: `ReplaceInstall`, `GameModChange`, `PresetImport`. `AssistantAction.ModeLabel` puts the mode in every title.
- New IDs: `rep-` (`AssistantWorkspace.ReplacementId`) and `prt-` (`FluffyPresetId`).

**CLI**
- `ReplaceCommandLine.Run(args)` handles `replace-plan`, `replace-install`, `replace-status`, `replace-uninstall`, `replace-reapply`, `replace-restore-all`, `replace-method` and `preset-map`.

## 2.0.1 additions

- `ReplacePlanner.Check(composition, sevenZip, engine, PrepareCache cache, string updateId)` and `Prepare(…, cache, updateId)`: the automatic check reuses extracted/fixed files (`PrepareCache`; archives, and since 2.4.2 folders keyed by size + write time, filled per file); `updateId` keeps that replacement's priority when comparing.
- `ReplacePlan.ConflictList` (`ReplaceConflict`: `Kind` slot/files/stack, `ModId`, `ModName`, `InstalledUtc`, `Enabled`, `Priority`, `TheirFiles`, `Shared`, `SharedPaths`, `Areas`, `NewWins`, `Text`, `CanUninstall`), `ReplacePlan.UnknownLoose`, `ReplacePlan.Headline`, `ReplacePlan.Milliseconds`.
- `ReplacePlanner.AnalyzeConflicts(paths, state, updateId, reference[, sourcePath, selectedIds])`, `AreaKey`, `SlotAreas`, `AreaLabel`, `ConflictText`, `ConflictBrief`, `ReferenceKey`.
- `ReplaceEngine.Uninstall(id)` keeps a recoverable copy; `Uninstall(id, recoverable, out folder)`, `ListRemoved()`, `ReadRemoved(folder)`, `RestoreRemoved(folder, out mod)` (files, enabled state; 2.4.2: position from `RestoreIndex`, below replacements installed after the removal), `DeleteRemoved(folder)`. `ReplaceJournal.TrashAfterCommit`.
- `GameReference.Tables(gameRoot)`: outfit labels without reading a PAK.
- `LibraryEntry.CostumeKeys`, identity cache `sf6-native-library-index-v2`; `LibrarySearch.Parse/CostumeRank/MatchesText/CostumeLabel/CostumesIn`.
- Assistant: `search_mods` takes `costume` and returns `costumes`, `costume_label`, `costume_match`; `install_replacement` results carry `conflicts` and `conflict_note`; `list_game_mods` rows carry `shares_files_with` and `same_outfit_no_shared_files`; `check_outfit` returns `already_installed_as`.

## 2.5.1 additions

- CLI: `manager-status`, `manager-uninstall`, `manager-restore`; `ReplaceCommandLine.Aliases` maps the old names (`fluffy-status`, `fluffy-uninstall`, `fluffy-restore`) to them; the old names are not in the help.
- Assistant: `import_external_preset` (the old `import_fluffy_preset` still runs); result keys `external_manager_detected`, `external_manager_mods`, `external_presets`; `get_history` kind `external`.
- `UiAudit.WordAllowed`, `UiAudit.LiteralFindings(word,out scanned)`, `UiAudit.TextFindings(control,word)`; `WordingChecks.Run` (`--wording-check`); build step `tools\Check-Wording.ps1`.

## 2.5.0 additions

- `MainFormSlots.cs`: `OutfitGroup` (`Key`, `Source`, `Character`, `Name`, `Slots`, `Label`); `MainForm.ModSlots(character)`, `VariantOptionNames()`, `SuggestedOutfitName()`, `ApplySuggestedName()`, `SetOutfitName`, `SlotHint()`, `SlotHintText` (tests), `UpdateSlotHint()`, `DefaultOptionIds()`, `OptionsDifferFromDefaults()`, `ResetOptions()`, `ToggleGroup(group[, expand])`, `SlotOptions(outfit)`, `GetGroupSelection()`. `UiAudit.ToolTipAllowed`. Checks: `SlotChecks.Run` (`--slots-check`).
- `VariantChoices` (`VariantChoices.cs`): `ReportName` / `ChoicesName`, `DataFolder(costumeRoot)`, `ReportFile`, `ChoicesFile`, `Normalize(source)`, `SourceOf(outfit, costumeRoot)`, `ReadReport(costumeRoot) → VariantReport` (`Present`, `Generated`, `Loader`, `Error`, `Archives`, `Find(source)`), `ReadChoices(costumeRoot) → VariantChoiceFile` (`Malformed`, `Version`, `Archives`, `Find(source)`, `IsOn(source, label)`), `States(archive, choices) → List<VariantState>` (`Label`, `Slot`, `On`, `Built`, `Pending`), `Entry(costumeRoot, source)` (a copy for Undo, null when none), `Set(costumeRoot, archive, label, on)` (throws for a label the report doesn't list or a file of another version), `Restore(costumeRoot, source, entry)`, `Note(state)`. Models `ArchiveVariants` (`Source`, `FighterDir`, `Variants`) and `ArchiveVariant` (`Label`, `Enabled`, `Slot`).
- `InstalledOutfit.Variants` / `VariantsUnsupported` (internal, filled by `LoaderResults.Apply`).
- `VariantsCommandLine.Run(args)` (CLI `variants`), `AppliesText(gameRunning)`.
- UI: `VariantList` (`SetRows`, `Rows`, `Toggle(index)`, `Toggled`, `RowHeight`), `MainForm.BuildVariantsPanel`, `ShowVariants`, `ToggleVariant`, `VariantsTooOld`, `VariantListForTest`.
- Assistant `list_installed`: `variants` (`label`, `on`, `slot`, `applies_next_launch`) or `variants_note` for manual entries.
- Checks: `VariantChecks.Run` (`--variants-check`).

## 2.4.0 additions

- `SafeHttp`: `GetText(url, maxBytes, accept, timeoutMs)`, `Download(url, dir, fileName, size, sha256, stage, word) → path`, `HashFile`, `AppVersion`, `AllowLoopbackHttp` (tests), `TimeoutMs`; `DownloadFailure` (`Reason`, `Status`). `LoaderRelease.AllowLoopbackHttp`/`TimeoutMs` forward to it.
- `UpdateCheck`: `ApiUrl` (tests), `TimeoutMs`, `Interval` (24 h), `Check(includePrereleases) → UpdateCheckResult` (`Ok`, `Reason`, `Status`, `Loader`, `Manager`, `Refused`, `CheckedUtc`), `ParseVersion`, `ProductOf(tag)`, `LoaderUpdate`, `ManagerUpdate`, `InstallPin`, `Running`. `ReleaseOffer` (`Product`, `Tag`, `Version`, `Asset`, `Url`, `Size`, `Sha256`, `DigestSource`, `HtmlUrl`, `Prerelease`, `AsPin()`). `UpdateSettings` (`Data\update-settings.json`: `AutoCheck`, `IncludePrereleases`, `LastCheckUtc`, `Last`, `SkipLoader`, `SkipManager`, `DismissedLoader`, `DismissedManager`; `Load`, `Save`, `Due`). `InstalledLoader.Record(state)`, `InstalledLoader.Version(mod, out source)`.
- `AppUpdater`: `ReleaseFiles`, `Stage(zip, version, dataDir)`, `Apply(appDir, source, previous, from, to, dataDir)`, `CleanupAfterUpdate(appDir, dataDir)`, `Rollback(appDir, dataDir)`, `UpdatesDir`, `PreviousDir`, `FailAfterCopies` (tests).
- `MainForm`: `RunUpdateCheck(manual)`, `ShowUpdateToasts()`, `UpdateToast(product)`, `UpdateSummary(result)`, `AppDir`/`AppDirOverride`, `NoRelaunch`, `OpenUrlHook`, `LastUpdateMessage`, `LastUpdateResult`, `UpdateChecksRun` (tests); `UninstallQuestion(mod, name)`.
- `LoaderCostumePak`: `Scan`, `Classify`, `Generated`, `Orphaned`, `LoaderPresent`, `Remove`, `WritePlaceholder`, `ClearFingerprint`, `IsLoaderMod`, `OrphanText`; `PatchPak`, `PatchPakKind`. `ReplaceEngine.AfterLoaderRemoved()`, `RemoveOrphanedCostumePak()`; `GameFilesStatus.OrphanCostumePaks`. `UnownedSlotsFlag.Ensure(gameRoot)` (technical; see DEVELOPMENT.md).
- `UiAudit.ToolTipFindings(control)`, `UiAudit.ToolTipCode(out scanned)`.
- `Rhythm.Related` / `Rhythm.AfterAction` / `Rhythm.Group` / `Rhythm.Gap(height)`; `NoteLabel` (`MinLines`, `LineCount`). `UpdateCheck.ShortVersion(text)`. `MainForm.UpdateSummary(result)` (one line) and `MainForm.SkippedSummary(result)`.
- Assistant: `AssistantPrompt.Knowledge`, `AssistantPrompt.SystemJson()`; tool `get_app_status`.
- CLI: `update-check [--include-prereleases]`, `update-install loader|manager [--yes] [--include-prereleases]`, `update-rollback`, `loader-pak-remove [--game]`.
- Checks: `UpdateChecks.Run` (`--updates-check`), `LoaderPakChecks`, the `cache` step of `--assistant-live-check`.
## 2.3.1 additions

- `LoaderPin` (`src/LoaderRelease.cs`): `Release` (the only production pin: `Version`, `Asset`, `Size`, `Sha256`, `Url`, `ReleasePage`, `Origin`; `Repo`, `Tag` constants). `LoaderRelease`: `Pin` (tests swap it), `AllowLoopbackHttp` (tests), `TimeoutMs` (30000), `MaxRedirects` (5), `DownloadDir` (`Data\downloads`), `FindLocal(pin, libraries, downloadDir) → LoaderFile|null`, `Download(pin, downloadDir) → LoaderFile` (throws `LoaderFetchException` with `Reason`; `OperationCanceledException` when cancelled), `Fetch(pin, libraries, downloadDir, download)`, `HashFile`, `Description(pin)`, `HashedFiles`. `LoaderFile`: `Path`, `Sha256`, `Size`, `Source` (`LoaderSource.Library|Cache|Download`), `SourceLabel`.
- `MainForm.OfferSlotsloaderInstall` (notice click and the Settings button) uses them; `LastLoaderFile` (tests). Removed: `ToolStatus.SlotsloaderPackages` and `ToolStatus.VersionKey` (name-based picking; nothing else used them). `IsSlotsloaderPackage` and the "already installed" match in `InstallReplacement` are unchanged.
- CLI: `loader-fetch [--library <dir> ...] [--no-download]`.
- Checks: `LoaderChecks.Run` (`--loader-check`), `LoaderChecks.RunReal` (`--loader-real-check`), `LoaderChecks.LoopbackServer`, `ReplaceUiChecks.LoaderOffer`.

## 2.3.0 additions

- `LibraryEntry.StageIds` (`"0800"`, `"other"`) and `MiscAreas` (Misc kinds: `"Stage"`, `"UI"`, `"Sound"` …; empty for Characters and Code tools mods), filled by `Library.Scan` (index v4, `Library.CacheFormat` `sf6-native-library-index-v4`); fighter art and REFramework colour data add `CharacterCodes`. `ModCategories`: `IsCharacter/IsTool/IsMisc/IsStage(e)` (`IsStage` = Misc kind Stage), `HasMiscKind`, `InCategory(e, characters|tools|misc|stages)`, `StageOf`, `MiscLabel`, `LabelMap`, `LabelOrder`, `FighterArtCode`, `ColourDataFighter`, `FighterCodeOfName`, `Collector`, `StageName`, `RowLabel`, `Keyword(term)` (`stage(s)`, `misc`).
- `LibrarySearch`: `is:stage|misc|character` tokens; `Query.PlainCategory`/`NameCategories` (OR-ed with the name match). `MainForm.MatchesModType` accepts `stage`, `misc`, `character`; `MainForm.LibraryRowLabel` adds Misc labels; `MainForm.StageNames` (static, from the game).
- `FighterPortraits.Load(gameRoot, ids, cacheDir, token, tile(info, bitmap|null), changeInProgress) → PortraitReport` (`Cache` hit/miss/stale/none/deferred, `Fighters` with `Status`/`Candidate`/`Path`/`Pak`/`Size`/`Format`/`Milliseconds`, `StageNames`, `Discarded`, timings). The callback owns the bitmap. `Candidates(id, versions)`, `TexVersions(gameRoot)`, `CacheState`, `ReadManifest`/`WriteManifest`, `CropTile`, `AlphaCentroidX`, `WriteAtomic`, `DefaultCacheDirectory` (`Data\portraits\v1`).
- `GamePakReader.Open(gameRoot)` (`Paks`, `Identity`, `Resolve(hashes, token)`, `Read(hit)`, `StillCurrent()`), read-only. `GameMessages.Entries(file, language)`, `StageNames(file)`.
- `ReTex.Parse(bytes)` (throws `InvalidDataException` for a bad header or data outside the file), `DecodeLevel(level)` (`NotSupportedException` for BC6H and other formats), `PickLevel`, `ToBitmap`. `BcDecoder.DecodeBlock(fmt, src, at, bgra64)`, `DecodeSurface(fmt, src, offset, w, h, pitch)` (BGRA output).
- `FilterPanel`: `Grid`, `Kinds` (tiles `"tools"`, `"misc"`, `"installed"`, `"lib:<path>"`), `MiscChips`, `ClearButton`, `ToolsOn`, `MiscOn`, `InstalledOn`, `SetInstalled`, `SetLibraries(paths)`, `LibrariesSelected`, `LibraryKey(path)`, `MiscSelected`, `SetKinds`, `ClearSelections`, `Arrange`; `FighterGrid` (`SetFighters`, `SetCounts`, `SetSelected`, `SetPortrait`, `Activate`, `HeightFor`, `KeepOrder`); `KindArt.Placeholder(kind)` (`tools`, `misc`, `installed`, `library`). `SegmentedToggle` (`UiControls.cs`): `SelectedIndex`, `Locked`, `SelectionChanged`, `Descriptions`, `SegmentBounds(i)`.
- `ReplaceEngine.ActiveLocks`: game locks held in this process.
- `MainForm.ShowFilters(open, focusPanel)`, `FiltersOpen`, `ActiveFilterCount()`, `EnsurePortraits(recheck)`, `PortraitCacheDirectory`, `PortraitsLoading`, `LastPortraitReport`; `ShortcutAction.ToggleFilters` (Ctrl+Shift+F). `ViewPreferences.FiltersOpen/Fighters/Kinds/MiscAreas/InstalledOnly/SelectedLibraries` (`FilterTab`/`Stages`/`Source` read for migration only; `Costume`/`ModType`/`ArchiveFormat` removed). `ReplaceSettings.DefaultMode` is kept for file compatibility but no longer shown or written by the UI; `StartMode()` returns `LastMode`, or a saved `DefaultMode` while no choice was made yet.
- Checks: `--filter-check <fixture root>`, `--filter-real-check <output> <game root>` (read-only).
## 2.2.0 additions

- `FluffyMod`: `Index` (installed.ini order), `ModId`, `IniPath`, `CopiedFiles` (installtype normal/empty), `UninstallBlocker` (reason for PAK installs, else null). `FluffyInstalls`: `OwnersOf(path)`, `Find(root, section)`, `Stamp`, `StampOf(roots)`, `ReadShared(path)` (shared read, retries, null when absent), `DecodeIni(bytes)`.
- `FluffyWatcher(roots,debounceMs)`: `Changed` (thread-pool), `RootsKey`, `Count`, `Dispose()`. `MainForm.FluffyAutoRefresh` (checks), `FluffyRefreshCount`.
- `ReplaceEngine.UninstallFluffy`(root, section, out folder) → string, `RestoreFluffyRemoved(folder)` → string, `ListFluffyRemoved()` → `List<FluffyRemoved>`, `DeleteFluffyRemoved(folder)`, `SectionRange(bytes, section, out start, out end)`, `FluffyModFile(mod,relative)`, `FluffyRemovedDir`, `FluffyIniBackupDir`, `FluffyRunningReason`. Records: `FluffyRemovedRecord` (`sf6-fluffy-removed-v1`), `FluffyRemovedFile` (`Action`: deleted, restored-backup, restored-other, kept-shared, kept-manager, missing; `FmmRenamed`).
- `DriftChoice` (`Refuse`, `Ask`, `RestoreOriginal`, `KeepCurrent`, `Repair`), `DriftItem`, `DriftException : IOException` (`Items`). `ReplaceEngine.Uninstall(id[, DriftChoice])` (default `Ask`), `Uninstall(id, recoverable, out folder[, choice])` (a non-recoverable test revert uses `Refuse`), `RestoreAll([choice])`, `RestoreReleasedOriginal(path)`, `DropReleased(path)`, `RestoreForeignCopy(copy)`, `DeleteForeignCopy(copy)`. `ReplaceState.Released` (`ReleasedFile`: `Path`, `ForeignSha`, `ForeignSize`, `Changer`, `ReleasedUtc`, `Original`), `ReplaceState.ForeignCopies` (`ForeignCopy`); both optional in the JSON and relocated with Data. `GameFilesStatus.ChangedBy`, `Released`, `ForeignCopies`; game-folder (non-natives) files are compared by SHA-256.
- `KnownBuilds.Table`, `Extra` (checks), `Name(sha)`, `Changer(path, sha, installs)` (`installs`: `FluffyInstalls`).
- UI: `MainForm.ChangeWithDriftChoice`, `DriftQuestion`, `DriftCaption`, `DriftButtons`, `LastDriftChoice`; `FluffyUninstallBlocker(mod)`, `UninstallFluffyRow(row)`, `FluffySourceMatches(mod,source)`, `LinkedFluffy(source)`, `PopulateFluffyUninstall(item,source)`; the Installed type filter item is "External mod manager" (2.5.1). `RecoveryEntry.Fluffy`, `Released`, `Foreign`; `RecoveryForm.AddGameFileEntries(engine, listing)`.
- CLI: `manager-status`, `manager-uninstall <name|section> [--root]`, `manager-restore <name|folder>`, `replace-restore-original <path>`; `replace-uninstall` / `replace-restore-all` take `--restore-originals` or `--keep-current` (neither: exit 1 listing the files).
- `ThemedMessageForm` (supersedes the 2.1.0 note below): `AutoScaleMode.Dpi`, inherited `Palette.Normal` points, `ApplyLayout(dpi)` in device pixels, `Measure(text, width, flags)` (point font at `GdiDpi()`, scaled to `LayoutDpi`), `PaintMetrics` (`DialogPaintMetrics`, first message paint, one `Program.LogLine` per show), `ThemedMessageBox.ShownHook` (checks). `--dialog-check <dir>` → `DialogCheck` (full app, exit 0/1, 3 on timeout).
- `AssistantPrompt.System` has a **Content** section after **Security** (adult-themed mods handled matter-of-factly; all SF6 fighters are adults; short factual descriptions).
## 2.1.0 additions

- `DataRelocation.Rebase(path, dataDir, RelocationReport, params excludedRoots) → string` returns the current-Data equivalent of a recorded path that does not exist (a folder named like the current Data folder splits it; outermost first; the equivalent must exist), else the path unchanged. `RebaseTarget(...)` does the same for a path that may not exist yet through its nearest ancestor. `DataRoots`, `Candidate`, `IsUnderOrSame`, `Log(report, file)` (once per process per message, `Data\log.txt`).
- `RelocationReport`: `Rebased`, `Missing` (+ `FirstMissing`), `Foreign` (existing paths in another app folder's Data), `OldRoots`, `ForeignRoots`, `Summary(file)`.
- `ReplaceEngine.LoadState(out RelocationReport)`; `LoadState()` rebases too. `Recover()` rebases the journal first. `PersistRelocation() → string` (null when nothing moved) saves the corrected state, `pak-backups\*.json` and a relocated `ReplaceSettings.StoreLocation` under the game lock. `ReplaceSettings.Relocated` (not serialized).
- `GameFilesStatus`: `BackupsRecorded`, `BackupsMissing`, `StoredMissing`, `FirstMissingBackup`, `FirstMissingStored`, `Relocation`; `Summary` reports missing backups/stored copies after the reapply message. `ReplaceEngine.CountBackups(state, status, storeFile)`.
- `replace-status` adds `App data:`, `Backups:` and `Relocation:` lines; `replace-install` takes `--no-loader`, and tool-only installs never need `--yes`.
- `ThemedMessageBox.Ask(owner, text, caption, icon, buttons[], defaultIndex, checkBox, out isChecked) → int` (button index, -1 for Escape/close); `ThemedMessageBox.TestResponder` answers dialogs in tests without showing them. `ThemedMessageForm` uses a pixel font of `Palette.Normal`'s size for its DPI and lays out again in `OnLoad` and `OnDpiChanged` (`LayoutCount`), so creation-time rescaling cannot enlarge it; `OptionBox` is the optional check box.
- `ReplaceSettings.SkipLooseLoaderPrompt` (optional in the JSON, default false). `MainForm.LooseLoaderPromptText/LooseLoaderButtons/LooseLoaderDontAsk/LooseLoaderCaption`, `EnsureLooseLoader(engine, installsNatives)`, `LastLooseLoaderChoice` (tests).
- `GameProcesses.RequireGameClosed(action)` (REFramework config edits) and `FluffyPakMessage(action)`. `ReplaceEngine.Change` refuses with that message only when an external mod manager runs and the planned journal (or a pending one) has PAK row operations; crash-reporter renames are deferred while it runs. UI: `FluffyBlocksPak`, `FluffyBlocks(mods)`, `FluffyBlocksPlan`, `FluffyPakReason`.

## 2.0.4 additions

- `LibrarySearch.ToolKeyword(term)` also returns a kind for a start of three letters or more of reframework/tools/code (any tool), plugins (plugin) and scripts (script); `refw` is a whole-term alias.
- `MainForm.SearchCharacterTerm(term)` also returns the character whose compact name (or the viper/honda/bison alias) uniquely starts with a term of three letters or more; ambiguous starts return null.

## 2.0.3 additions

- `LibraryEntry.ToolKinds` (`script`, `plugin`, `dll`, `reframework`, in that order) from the same listing pass; `Library.ToolKind(string[] pathParts)` classifies one entry outside `natives`; identity cache `sf6-native-library-index-v3` (`Library.CacheFormat`, `Library.CacheFilename` = `library-native-index-v3.json`), `LibraryIdentityRecord.ToolKinds` (a record without it is relisted).
- `LibrarySearch.ToolKeyword(term)` (`tool`/`plugin`/`script`/`dll` or null), `IsToolMod(e)`, `MatchesTool(e, keyword)`, `ToolLabel(e)`; `Query.PlainTool`, `Query.NameTools`; `is:tool`/`type:tool` set `Query.Type = "tool"`. `MainForm.MatchesModType(e, type)` accepts `tool`/`Tool mod`; `MainForm.LibraryRowLabel(e, rank)` is the row's costume or tool label.
- Assistant `search_mods`: `kind` enum `any|bundle|normal|tool`; query words that are tool keywords (`reframework`, `code`, `tool mods`, `plugin`, `lua`, `dll`, …) also match by kind; each result has `tool_kinds` and, for tool mods, `tool_label`.
- Library uninstall: `MainForm.LinkedReplacements(source)`, `PopulateReplacementUninstall(item, source)`, `UninstallLibraryReplacement(row)` (the Installed page's `UninstallGameRow` flow), `ReplacementUninstallBlocker()`.
- `ThemedMessageBox.Show([owner,] text, caption, buttons, icon[, defaultButton]) → DialogResult` replaces every `MessageBox.Show`; `ThemedMessageBox.Dark` (registry `AppsUseLightTheme`, `ForcedDark` for tests), `ThemedMessageForm` (layout for a given DPI, `CopyText`).

## Settings and library — Core.cs

- `AppConfig`: `GameRoot`, `Libraries`, `SevenZipPath`; `CostumeRoot` derives `GameRoot/reframework/costume_mods`. `Load()` / `Save()` use `Data/settings.json`; `DataDir` is beside the executable. GUI view preferences are separate.
- `Characters`: `ByCode` maps explicit ESF codes to canonical folder names; `Names` returns the roster; `Guess(string)` returns a filename fallback. Native identity takes precedence; preserve intentional code gaps and `A.K.I` spelling.
- `LibraryEntry`: `SourcePath`, `Name`, `Character`, `Kind`, `Size`, `OptionCount`, `OptionIds`, `CharacterCodes`, `Detection`, `DetectionError`, `FilenameCharacter`. Detection values: `Native`, `Ambiguous`, `UnknownNative`, `Filename`, `Unknown`, `Error`. Multiple/unmapped codes leave character empty; errors also clear filename guesses and keep option count at -1.
- `Library.Scan(IEnumerable<string>[, string sevenZipPath]) → List<LibraryEntry>` scans direct ZIP/RAR/7Z/folder children, deduplicates paths and detects native IDs. Per-source/per-library failures produce diagnostic rows while retaining healthy results; whole-library rows have `Kind == "Library"`. Four workers inherit cancellation and report ordered progress. Failures are uncached. `IdentityCacheDirectory` redirects the optional cache; `ClearIdentityCache()` invalidates it. Archive stamps use path/size/mtime/tool metadata; folders rescan.
- `Library.InstallState(entry, outfits) → LibraryInstallState` returns `None`, `Partial`, `Full` or `Disabled` from exact saved source paths and option-ID coverage. This is saved selection state, not a loader/game compatibility result.

## Archive I/O — ArchiveSource.cs / ZipIntegrity.cs

Internal APIs:

- `SourceReader(source, sevenZip)` is disposable; `Entries` maps normalized paths to `SourceEntry` (`Path`, `Original`, `Size`, optional `ZipChecksum`). `UsesSevenZip` and `ListingStamp` support preview listing reuse.
- `ReadBytes(entry, limit) → byte[]` / `ReadManyBytes(entries, limit)` enforce expected sizes and caller limits. Metadata is capped at 1 MB and previews at 25 MB by callers. ZIP/folder transfers stop excess bytes during streaming.
- `CopyFiles(IEnumerable<PlannedFile>, target, scratch) → Dictionary<string,string>` stages selected winners and returns destination-path SHA-256 values. ZIP/folder bytes hash while copying; selected RAR/7Z files move within staging before hashing.
- `VerifyFiles(IEnumerable<PlannedFile>) → Dictionary<string,string>` uses the same integrity/hash path without publishing an outfit. ZIP/folder streams go to `Stream.Null`; RAR/7Z uses generated selected extraction and cleanup.
- `ZipIntegrity` supplies central-directory CRC values, including ZIP64. .NET handles names, decompression and data descriptors. Reads, copies and verification check CRC and exact length; listing alone does not verify payloads. ZIP requires no external tool.
- `ProcessUtil.RunText(exe,args)` / `RunBytes(exe,args,limit[, token, timeout])`: hidden execution without a shell, closed stdin, bounded streams, process-specific errors and child cleanup on failure/cancel. Defaults: ten-minute deadline, 64 MB text stdout, 1 MB stderr. The overload accepts another deadline.

Readers reject unsafe paths, case-insensitive duplicates and links. Password entry, recursive inner archives and PAK composition are unsupported.

## Packages and composition — PackageModels.cs / PackageReader.cs / Composition.cs

- `NativeAsset`: `EntryPath`, `DestinationPath` (one `natives/` prefix), `Size`.
- `ModComponent`: `Id`, `Name`, `Description`, `Author`, `Version`, `AddonFor`, `ParentId`, `PreviewEntry`, `IsDummy`, `Metadata`, `Assets`; `IsChoiceGroup` recognizes an explicit choose-one description. Empty string is a valid root ID. `ParentId == null` means no component parent; `ParentId == ""` points to the root component.
- `ModPackage`: `SourcePath`, `Name`, `Character`, `CharacterCodes`, `Components`, `Warnings`. `Find(id)` is indexed and case-insensitive. `PreviewBytes(componentId, sevenZipPath)` reads a component/ancestor/fallback image; its resolved-entry cache is capped at 16 MB. Internal `RebuildIndexes` / `PrepareChoices` refresh the component/children/choice snapshot.
- `PackageReader.Load(sourcePath, sevenZipPath[, bool preloadPreviews]) → ModPackage`; an internal overload shares an existing reader. Native trees belong to their exact containing component. INI parsing accepts equals/colon syntax, UTF-16LE BOM, UTF-8 and Windows-1252 fallback. `Addonfor` resolves a unique trimmed display name; unresolved links/cycles warn. Parsing uses component/name and preview-directory indexes.
- `CompositionPlan`: `Package`, ordered `Layers`, case-insensitive winning `Files`, `Overrides`, `Errors`, `Warnings`; computed `TotalBytes` and `CanInstall` (files present and no errors).
- `PlannedFile`: `Asset`, `Component`. `FileOverride`: destination `Path`, and component names `Previous` / `Winner`.
- `Composer.Plan(package, selectedIds) → CompositionPlan`: removes duplicate IDs, rejects missing IDs, puts main/base layers first, and preserves other selection order; later layers win collisions. `DefaultSelection(package) → List<string>` selects related bases, a sole real component or real roots. It does not complete arbitrary bundles.
- `Composer.UnresolvedChoiceGroups(package, selectedIds) → List<ModComponent>` (internal, 1.5): the choose-one groups for which `Plan` would report "Choose an option in …", evaluated without building a file plan; the caller must have run `PrepareChoices`. The UI uses it to mark required groups live. `Plan` itself is unchanged.

Any nonempty selection activates implicit-root main/required groups, including nested-only selections. Explicit independent bundle branches remain scoped. Backend and UI share choice rules: parent/child selections count as one immediate branch, and inferred exclusivity considers overlapping **non-base** peers, so main-plus-overlay combinations remain valid. Explicit required groups need a choice unless optional; inferred groups only prohibit conflicts. IDs/parents compare case-insensitively. `Composer.Plan` refreshes indexes after model edits.

`CanInstall` validates composition/routes and mesh presence, not binary game compatibility. Missing local MDF may be a warning because the loader can supply vanilla materials.

## Installation and results — Core.cs / LoaderResults.cs

- `InstallManifest`: format `sf6-slotsloader-installer-v1`, `Id`, `Name`, `Character`, `SourcePath`, `CreatedUtc`, `SelectedIds`, destination-path `Sha256`. Replacement retains the managed ID.
- `Installer.Install(plan, costumeRoot, character, outfitName, sevenZipPath, replaceManaged) → InstallResult`: reinspects/recomposes, validates target identity and reviewed winning paths/sizes, stages verified bytes, then publishes. Existing destinations require explicit replacement and unchanged managed ownership. `InstallResult` contains `Folder`, `Message`, `FileCount`, `Bytes`.
- `InstalledOutfit`: `Name`, `Character`, `Folder`, `Managed`, `Enabled`, `Id`, `SourcePath`, `SelectedIds`, `InstalledUtc`; loader fields `LoaderOutfitName`, `LoaderStatus`, `LoaderGenerated`, `LoaderBuild`, `LoaderOutfitDetails`. Internal `SnapshotStamp` captures filesystem type, creation/modification times and file length where applicable; it is a cheap stale-row check, not a recursive content hash.
- `Installer.ListInstalled(costumeRoot)` includes direct character/outfit folders, direct manual ZIP/RAR/7Z/PAK files and managed disabled copies. For a manual archive, `Folder` is its **file path**. Pass the returned rows unchanged to selected set operations so their snapshot checks remain meaningful.
- `Installer.SetEnabled(outfit, costumeRoot, enabled) → string` and `Uninstall(outfit, costumeRoot) → string` perform managed-only moves to/from the disabled sibling or recoverable trash. IDs, locations and collisions are rechecked. `OpenFolder(path)` invokes Explorer for explicit actions; `Utilities.FriendlyBytes(long)` formats sizes.
- `LoaderResults.Apply(outfits, costumeRoot)` reads last-run `outfits.json`. Exact normalized relative `source` paths map folders/archives, including several reported names from one source. Status distinguishes loaded, pending restart, disabled, absent and unreadable results. Source-less rejections are not guessed from display labels. The view never changes loader output.

Source reinspection compares paths/sizes rather than a review-time hash; ZIP CRC establishes archive consistency and manifest SHA-256 describes staged bytes. Mutations share a named costume-root mutex across app instances in the current Windows session. `Installer.Uninstall`, `SetEnabled`, replacement and library uninstall retain their managed-only guards. Explicit Installed-page batch removal uses the separate `OutfitSetStore` scope below and may include manual entries. Original source mods, unselected entries and installed app Data remain protected. `Safety` enforces path/link rules. `Json.Write` atomically replaces application state; `Json.WriteNew` publishes reports without replacing existing files.

## Named presets and selected removal — OutfitSetStore.cs

All types and entry points in this section are internal to the app:

- `OutfitSetKind`: `Preset` or `Removed`.
- `OutfitSetInfo`: `Id`, `Name`, `Folder`, `Kind`, `CreatedUtc`, `Bytes`, `OutfitCount`, display-path list `Outfits`, and exact `ManifestText` snapshot. Retain the returned object unchanged for restore/delete; record changes require a fresh list.
- `OutfitSetListing`: `Entries` and `Warnings`. Unknown, incomplete or unsafe generated sets are excluded with warnings.
- `OutfitSetRestoreResult`: integer `Restored` and `Skipped` counts.
- `OutfitSetStore.List(string costumeRoot) → OutfitSetListing` lists recognized preset and removed-set GUID directories. It validates records, paths, ownership agreement and complete payload shape/length without hashing all bytes for the display.
- 1.0.8: `CreatePreset(string costumeRoot, string name, PresetScope scope, ReplaceEngine engine) → OutfitSetInfo` saves a **list preset** (format `sf6-slotsloader-outfit-set-v3`, [OutfitSetPointers.cs](OutfitSetPointers.cs)): `Pointers.Outfits` (Character, Folder, Name, Mode `composed`/`all_variants`, SourcePath, SourceStamp, SelectedIds, Parts) for enabled outfits whose library source exists (`HasSource`; Add to outfit parts need theirs too), `Pointers.Replacements` (Name, SourcePath, SourceStamp, Kind, SelectedIds, Order = place among all saved replacements, copied ones included) for enabled replacements and tool mods whose source exists. Only the rest is copied, as `Entries` / `Replacements` exactly like v1/v2. `Restore(costumeRoot, set, scope, engine[, sevenZip])` on v3: copied replacements, listed replacements (each through `ReplacePlanner.PrepareAndInstall`, lowest priority first; same source and options = skipped, same source with other options or another mod under the saved name = `ReplacementConflicts`; SF6 closed, external-manager lockout and its PAK rule checked once), then `ReplaceEngine.ArrangeRestored` moves the restored ones next to their saved neighbours in that one order (as `RestorePresetReplacements` places copies), copied outfits, listed outfits (an installed outfit whose added parts differ is noted, not skipped; `Installer.Install` + `AddToOutfit` per part, or `ArchiveInstalls.Install`). Each listed item fails alone into `OutfitSetRestoreResult.Notes` (source gone, place taken, install error; a changed source installs its current version and is noted); options are mapped by id (`MainForm.MapSelection`). `RenameSource` (called by `SourceRename.Run`) moves pointers with a renamed library mod. `CreateCopyPreset` (and the two-argument `CreatePreset`, tests) still makes the 2.4.1 formats, which list, restore and delete as before.
- `CreatePreset(string costumeRoot, string name) → OutfitSetInfo` (tests; v1 copy) freshly enumerates and copies **all enabled installed entries**, independently of UI filters or selection. It refuses an empty set or an existing case-insensitive preset name. Managed/manual directories and direct ZIP/RAR/7Z/PAK files are self-contained payloads; originals remain unchanged and original mod archives are not needed later.
- `RemoveOutfits(string costumeRoot, IEnumerable<InstalledOutfit> selected, string name) → OutfitSetInfo` preflights the explicit selected rows and full content inventories, then reversibly relocates them into one generated removed set. It accepts manual entries, managed enabled entries and managed disabled entries; rejects null/empty selections, duplicate rows, stale identities/stamps, unsafe locations and reparse entries. Unselected entries remain installed. Disabled entries retain their prior state/location.
- `Restore(string costumeRoot, OutfitSetInfo selected) → OutfitSetRestoreResult` fully verifies retained payloads and preflights every destination. It adds missing entries, skips exact same-destination byte/structure matches, and refuses the whole operation on a changed same-name conflict. It does not overwrite or remove other installed entries. Checks are repeated before commit. Saved sets are retained for repeated restore; removed disabled entries return disabled. Restored destination timestamps invalidate obviously older loader results.
- `Delete(string costumeRoot, OutfitSetInfo selected)` fully verifies the selected generated copy and exact listed record before permanent deletion. The UI must explicitly confirm this action. Active outfits, original source mods and other sets are unaffected; no automatic retention deletion is performed.

Presets are stored beside `costume_mods` in `.slotsloader-installer-presets/<GUID>`; removed sets use `.slotsloader-installer-removed-sets/<GUID>`. Both contain `set.json` plus `payload/<six-digit entry number>`. Generated transactions use the separate sibling `.slotsloader-installer-set-staging/<GUID>`. Older managed backup/trash records continue through `RecoveryStore` and are not converted automatically.

`OutfitSetRecord` has format `sf6-slotsloader-outfit-set-v1`, `Id`, `Name`, string `Kind`, `CreatedUtc` and `Entries`. Each `OutfitSetItem` records `RelativePath` (`Character/name`), `ManagedId`, `Storage`, `Directory`, `Enabled`, `Managed`, complete relative `Directories`, and `Files` (`Path`, `Length`, `Sha256`). For an opaque file entry, its sole file path is the empty string. All bytes are inventoried, including managed manifests, metadata and non-native files; empty directories are preserved. Managed manifest ID, name and character must agree with the record. Archives/PAKs are not parsed or recomposed.

Duplicate comparison is case-insensitive for relative paths and requires matching entry type, every file's length/SHA-256 and the entire directory/file structure. Modification timestamps are excluded. A same-content entry under a different destination is not a duplicate. Changed records, damaged payloads, path escapes and links are refused. Listing verifies shape/length; restore/delete verify SHA-256. Saving hashes/copies and rechecks source inventories; `SnapshotStamp` alone does not claim content integrity.

All writes use the shared root mutex. Preparation is cancellable and reports file-byte progress through `OperationContext`; final publication/moves and reverse-order rollback are non-cancellable. Brief scanner/share locks receive bounded retries. Ordinary commit failures roll earlier moves back; if rollback is blocked, an error identifies retained staging payloads and they are not deleted by cleanup. There is no durable crash journal or automatic recovery guarantee for a killed process.

## Assistant — Assistant*.cs / ClaudeClient.cs (internal, 1.6)

- ClaudeClient(apiKey[, baseUrl]): Send(body, serverFallback, token) → ClaudeResponse (Status, RequestId, RawBody, exact RawContent, parsed Data).
  - MaxAttempts 3. A 429 waits for etry-after; 5xx/529 and network errors use jittered exponential backoff (BackoffBaseSeconds).
  - Failures throw ClaudeApiException (Status, ErrorType, ApiMessage, RequestId, Attempts) with the key redacted.
  - Diagnostics receives one redacted line per attempt. DescribeHeaders masks x-api-key.
- AssistantJson: JsonObj (ordered), JsonRaw (verbatim), Write, ParseObject, and RawMember(json, name) (exact source text of a top-level member).
- AssistantModels.All covers Haiku 5.5 (the default since 2.4.0, listed first) and Sonnet 5.5: ID, display name, ServerFallback and prices. Efforts is low, medium, high (2.0.2: Opus 5.5, Fable 5.1, xhigh and max were removed; saved settings naming them load as the defaults, Sonnet 5.5 / high).
  - AssistantUsageMeter.Estimate(model, in, out, cacheRead, cacheWrite) applies Haiku's tier above 100K total input. Add and Snapshot work per model.
- AssistantSettings.Load(dataDir) / Save(dataDir) read and write assistant-settings.json.
- AssistantKeyStore(dataDir[, envReader]): Save, Load(out source) (saved, environment, unreadable or 
one), Remove, HasSavedKey, and Status() (masked).
- AssistantWorkspace(config, settings):
  - Library() (the GUI's LibraryProvider or Library.Scan), Installed(), and Package(entry), which is cached by size and mtime.
  - ID helpers ModId, InstalledId, PresetId; Scrub(text).
- AssistantToolbox:
  - Specs are the 12 ToolSpecs. Definition() returns the strict JSON schema and Validate(input) checks an input against it.
  - Execute(name, input) → ToolOutcome (Content, IsError, Activity, Action). Actions and PendingActions() give the queue; ActionChanged reports changes.
- AssistantActionRunner: ValidateInstall, ValidateOther, Execute(action), and RunBatch(actions, token, progress, changed) → AssistantBatchResult, which stops at the first failure.
- AssistantConversation(workspace, clientFactory, model, effort):
  - Send(text, token) → AssistantTurnResult. Status is Completed, Refused, MaxTokens, Cancelled, Failed or IterationLimit; the result also has RefusalCategory, ModelCalls and Cost.
  - SetSelection(model, effort) returns a transcript hint and throws while a run is in progress.
  - Apply(numbers, token, progress) and Discard(numbers) feed the next [App] note.
  - BuildRequestBody(); Usage; Events (AssistantEvent); MaxModelCalls (40).
- OutfitSetStore.RemovedSetName(DateTime) (internal) gives the folder-safe default name for selected removals.
## Progress, recovery and CLI

- `OperationContext.Begin(CancellationToken, Action<OperationProgress>) → IDisposable` establishes thread-local `Token`/`Progress`. `ThrowIfCancellationRequested()` / `Report(stage, completed, total)` are cooperative. `NonCancellable()` protects final mutation/rollback. Parallel workers must explicitly inherit context. Progress contains `Stage`, `Completed`, `Total`, `CanCancel`.
- `RecoveryStore.List(costumeRoot) → RecoveryListing` returns recognized backup/trash entries plus warnings. `RecoveryEntry` retains location/ownership, manifest text, date, byte/file counts and `RecoveryKind`. `Restore(costumeRoot, entry)` refuses existing destinations; `Delete(costumeRoot, entry)` permanently deletes only a revalidated retained copy, with source-inside-copy protection. Both share the installer mutex, reject links/changed snapshots and use non-cancellable mutation scopes. Manual outfits and arbitrary staging remnants are excluded; payload hashes are not automatically rechecked. `RecoveryForm` supplies explicit user actions.
- `CommandLine.Run(string[] args) → int` backs `Slotsloader CLI.exe`: `list-options`, `test-mod`, `test-loader` (also accepted with a `--` prefix). Selection uses repeated IDs, an ordered JSON array or same-source installer manifest; empty-string root IDs remain valid. Reports use stdout or a new JSON path. Exit codes: 0 success, 1 validation failure, 2 argument errors, 130 cancelled. No form or normal GUI settings load is needed.
- `ModCheckReport` separates composition `CanInstall`, `IntegrityVerified`, errors/warnings, IDs/options, winning files/hashes, overrides and optional `NativeLoader` results.
- `NativeModCheck.Run(plan, character, sevenZip, loader, gameRoot, sandbox) → NativeCheckResult` prepares a new isolated sandbox, copies selected content/metadata, and runs the explicit native loader with the original base PAK read-only. It records loader identity, reports/logs, kept/rejected outfits and omitted DLC PAKs. Native-wrapper path length/code-page limitations are checked first. Original metadata receives full SHA-256 comparison; base PAK preservation checks header/index, length and mtime, not every content byte. Sandbox artifacts are retained. Native acceptance remains separate from a game rendering test.

Use `Slotsloader CLI.exe --help` and [DEVELOPMENT.md](../docs/DEVELOPMENT.md) for command flags and examples. Source responsibilities and retained limitations are described in [ARCHITECTURE.md](../docs/ARCHITECTURE.md).
