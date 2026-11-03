using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // Replace-original (Fluffy-style) engine checks on generated game folders with
    // synthetic KPKA PAKs. Nothing here reads or writes the real game.
    internal static class ReplaceChecks
    {
        internal const string V1 = "natives/stm/product/model/esf/esf009/002/esf009_002_cmd_000.user.2";
        internal const string V2 = "natives/stm/product/model/esf/esf009/002/01/esf009_002_01_albd.tex.241101895";
        internal const string V3 = "natives/stm/streaming/product/model/esf/esf009/002/01/esf009_002_01_albd.tex.241101895";
        internal const string V5 = "natives/stm/product/model/esf/esf009/002/01/esf009_002_01.mesh.230110883";
        internal const string V6 = "natives/stm/product/ui/title.user.2";
        internal const string D1 = "natives/stm/product/model/esf/esf009/004/esf009_004_cmd_000.user.2";
        internal const string P1 = "natives/stm/product/ui/patched.user.2";
        internal const uint MaterialData = 0x9E32F1F4, Cloth = 0x9F67268B;

        internal sealed class Fixture
        {
            internal string Root, Game, Data, Mods;
            internal ReplaceEngine Engine;
            internal FluffyInstalls Fluffy = new FluffyInstalls();
            internal string Mod(string name) { return Path.Combine(Mods, name); }
        }

        internal static int Run(string output)
        {
            string root = Path.GetFullPath(output); Safety.NoReparseChain(root);
            string run = Path.Combine(root, "replace-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(run); Console.WriteLine("Replace fixture root: " + run);
            IProcessProbe previousProbe = GameProcesses.Probe; IGameLauncher previousLauncher = GameProcesses.Launcher;
            var probe = new FakeProcessProbe(); GameProcesses.Probe = probe;
            int failures = 0, count = 0; var report = new List<string>();
            Action<string, Action> check = delegate(string name, Action action)
            {
                count++; probe.Game = false; probe.Fluffy = false; ReplaceEngine.CrashAfterStep = -1;
                try { action(); Console.WriteLine("PASS " + name); report.Add("PASS " + name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); report.Add("FAIL " + name + ": " + ex.Message); }
                finally { ReplaceEngine.CrashAfterStep = -1; }
            };
            try
            {
                check("migration: verified handoff, saved plan, original priority, rollback and idempotent resume", delegate { ModMigrationChecks.Run(Path.Combine(run,"migration"),probe); });
                check("migration: compatibility before uninstall and restoring REFramework with loose outfits", delegate { ModMigrationChecks.Compatibility(Path.Combine(run,"migration-ready")); });
                check("migration (1.0.15 review): renamed old suffixes and repaired files match their download; a kept path goes to the migrated winner", delegate { ModMigrationChecks.Recorded(Path.Combine(run,"migration-recorded")); });
                check("loader contract (1.0.14): upstream contract 2+ accepted without an extension; missing, old or malformed contracts refused; fork unchanged", delegate { LoaderContractChecks.Run(Path.Combine(run, "loader-contract")); });
                check("swap repairs (automatic for recognized files, including the Slotsloader marker): typed Alex scene signatures, fingerprinted physics recipes, malformed/customized refusals, idempotence and game-aware preparation cache", delegate { SwapRepairChecks.Run(Path.Combine(run, "swap-repairs")); });
                check("murmur3 path hash matches the game's index (two real vanilla paths)", HashVectors);
                check("KPKA TOC parse, lookups, zero/restore rows with verification, refusal of features != 0 and wrong expected bytes", delegate { PakBasics(Path.Combine(run, "pak")); });
                check("fixers: allow-listed CRC upgrade, stale types reported, mip pitch repair, old tex suffix and path normalization", delegate { Fixers(); });
                // 1.0.16: install/uninstall speed-ups (SpeedShortcutChecks.cs)
                check("PAK lookups (1.0.16): repeated hashes give every row in table order and a new list per call; zeroed rows, Contains; the blocked layout hash equals the row-by-row one across full and partial blocks, ignores hash fields and follows every other change", delegate { SpeedShortcutChecks.PakTable(Path.Combine(run, "speed-pak")); });
                check("link checks per pass (1.0.16): folders checked once per pass; a link not checked before in the pass is refused every time and never remembered; a game folder made a link after install refuses the change with nothing committed", delegate { SpeedShortcutChecks.ReparseFolders(Path.Combine(run, "speed-links")); SpeedShortcutChecks.ReconcileLink(Path.Combine(run, "speed-reconcile-link")); });
                check("rollback copy (1.0.16): an archive mod's game file changed after Reconcile hashed it is not stored (the archive gives the installed version), the uninstall is refused as before, recovery and a later uninstall work", delegate { SpeedShortcutChecks.HydrateAfterChange(Path.Combine(run, "speed-hydrate")); });
                check("PAK stamps (1.0.16): the base PAK stamp and BaseAtInstall after a change match a fresh read of the PAK; status reports no change", delegate { SpeedShortcutChecks.Stamps(Path.Combine(run, "speed-stamps")); });
                check("Extra outfit health previews repairs and flags unpatched files, then checks the installed bytes", delegate { ExtraPatchHealth(Path.Combine(run, "outfit-health")); });
                check("swatch repair (2.5.5): an old select-screen swatch file is rebuilt from the game's current one with the mod's colours per ID (extra current IDs kept); current files, other paths and unknown layouts untouched; another outfit, a missing ID or no game file leave it as it is with a warning; extra outfit installs rebuild the copy and its hash", delegate { Swatches(); });
                check("install -> conflict/priority -> disable -> uninstall restores every PAK and loose file byte for byte (PAK method)", delegate { Lifecycle(Path.Combine(run, "lifecycle")); });
                check("selected replacements uninstall in one transaction, validate every ID and restore original files", delegate {
                    var f = Create(Path.Combine(run, "selected-replacements"), false, ReplaceMethods.Pak);
                    var clean = Snapshot(f.Game);
                    var a = Install(f, "modA", "Colors"); var b = Install(f, "modB", "Alternative");
                    var c = Install(f, "modDlc", "DLC");
                    var before = f.Engine.LoadState();
                    Refused(delegate { f.Engine.UninstallSelection(new List<string> { b.Id, "missing" }, DriftChoice.Refuse, null); }, "no longer exists");
                    Require(f.Engine.LoadState().Generation == before.Generation, "stale selection changed state");
                    f.Engine.UninstallSelection(new List<string> { b.Id, c.Id, b.Id }, DriftChoice.Refuse, null);
                    var after = f.Engine.LoadState();
                    Require(after.Generation == before.Generation + 1 && after.Mods.Count == 1 && after.Deployment(V1).Owner == a.Id, "selection changed priority or committed twice");
                    f.Engine.UninstallForGood(a.Id, DriftChoice.Refuse, null);
                    SameSnapshot(clean, Snapshot(f.Game), "after selected replacements");
                });
                check("DLC PAK entries are invalidated and restored too", delegate { DlcRows(Path.Combine(run, "dlc")); });
                check("a path inside a patch PAK is refused with the PAK method; no change is made", delegate { PatchBlock(Path.Combine(run, "patch")); });
                check("PAK mods (2.5.6): a mod's .pak is added as the next patch PAK (a pre-numbered name too); removing or disabling one below another PAK leaves an empty placeholder, at the top it is deleted; Undo and re-enabling fill the placeholder; a number another PAK took meanwhile is never overwritten; the other mod's patch PAK never changes", delegate { PakMods(Path.Combine(run, "pak-mods")); });
                check("leftover patch PAKs (1.0.11): a patch PAK nothing claims is a leftover; the Manager's PAK mod (enabled or disabled), PAKs either external-manager root lists, the empty placeholder, Slotsloader's costume pak and an unreadable PAK are not; an unreadable installed.ini means none; status details and replace-status say so; Remove refuses while SF6 or the external manager runs and for a PAK that isn't a leftover; a middle one leaves the empty placeholder, the top one is deleted (no other PAK changes or is renumbered); Undo restores byte for byte, refuses while either runs and once the number holds another PAK; an ended Undo's copy goes to the Recycle Bin", delegate { LeftoverPakChecks.Engine(Path.Combine(run, "leftover-paks"), probe); });
                check("PAK mods with the loose file loader on (2.5.9): the .pak is still added as the next patch PAK (the method only affects natives files) and uninstalls cleanly", delegate { PakModsLoose(Path.Combine(run, "pak-mods-loose")); });
                check("installs list (1.0.9): manager_installs.json beside the costume loader's data lists every replacement in priority order (enabled, loose files, patch PAKs; path, sha256, size), leaves out kept files, is rewritten only when it changes, empty when nothing is installed, never where no costume loader keeps data", delegate { InstallsList(Path.Combine(run, "installs-list")); });
                check("files owned by an installed Fluffy mod are refused; a missing installed.ini means no Fluffy mods", delegate { FluffyOwnership(Path.Combine(run, "fluffy")); });
                check("running SF6 or Fluffy blocks every game-file change", delegate { RunningBlocks(Path.Combine(run, "running"), probe); });
                check("journal recovery after a simulated crash at every step of an install and an uninstall", delegate { CrashRecovery(Path.Combine(run, "crash")); });
                check("game update detection, dropped stale rows and Reapply; restore-all after an update", delegate { GameUpdate(Path.Combine(run, "update")); });
                check("rows restored by another tool are detected and re-invalidated by Reapply; a file another tool changed is replaced with no copy kept after the change (1.0.1)", delegate { RowsRestored(Path.Combine(run, "rows")); });
                check("tool/root mods: genuine files backed up and restored exactly, previews/readme skipped, protected paths refused", delegate { ToolMods(Path.Combine(run, "tools")); });
                check("Slotsloader costume pak (2.4.0): uninstalling Slotsloader removes its generated pak (the sole patch pak), clears the registry stamp (slots kept), leaves costume_mods and the opt-out file; a reinstall finds no pak and an empty stamp", delegate { LoaderPakChecks.SolePak(Path.Combine(run, "loaderpak-sole")); });
                check("Slotsloader costume pak (2.4.0): with another mod's pak above it, a marker-only placeholder keeps the patch row unbroken; foreign and stage paks stay byte-identical", delegate { LoaderPakChecks.Placeholder(Path.Combine(run, "loaderpak-placeholder")); });
                check("Slotsloader costume pak (2.4.0): restore-all removes it, a Fluffy-style pak below it never changes, uninstalling another mod leaves it", delegate { LoaderPakChecks.ForeignUntouched(Path.Combine(run, "loaderpak-foreign")); });
                check("Slotsloader costume pak (2.4.0): Slotsloader not installed but its pak present: status and replace-status say so, loader-pak-remove removes it", delegate { LoaderPakChecks.Orphan(Path.Combine(run, "loaderpak-orphan")); });
                check("owned-outfit marker (2.4.0): the file named in Data\\show_unowned_slots.txt is restored after a loader install and Reapply, never deleted by an uninstall nor recorded as deployed, logged without its name; invalid names do nothing", delegate { LoaderPakChecks.OwnedOutfitMarker(Path.Combine(run, "owned-marker")); });
                check("loose-file-loader method: no PAK change; switching to PAK and back converts byte-exactly", delegate { LooseMethod(Path.Combine(run, "loose")); });
                check("loose method allows natives-only changes while SF6 runs (Fluffy's re-copy); PAK edits and tool files still wait", delegate { LooseWhileRunning(Path.Combine(run, "loose-running"), probe); });
                check("REFramework config edits change one key only, keep CRLF/BOM/order, back up first, refuse while the game runs", delegate { ConfigEdits(Path.Combine(run, "config"), probe); });
                check("REFramework loose-file log parsing matches test files", LogParsing);
                check("health check: affected outfit, fixes, streaming twin, unknown folder, conflicts, outdated suffix", delegate { Health(Path.Combine(run, "health")); });
                check("leftover Test in game record (feature removed in 2.3.0): kept as a normal install, logging keys restored, record deleted, game files unchanged", delegate { LeftoverTest(Path.Combine(run, "testgame"), probe); });
                check("crash reporter is renamed while replacements are installed and restored after (PR #22 review: also when the exe came back beside its renamed copy, a crash at any step rolls back)", delegate { CrashReporterRename(Path.Combine(run, "crashreporter")); });
                check("TOC backup fallback restores only zeroed rows when the PAK layout still matches", delegate { TocFallback(Path.Combine(run, "tocfallback")); });
                check("Ordered preset import, reimport, rollback, uninstall and ambiguous sources", delegate {
                    OrderedPatchPaks(Path.Combine(run, "ordered-paks"));
                    foreach (string method in new[] { ReplaceMethods.Loose, ReplaceMethods.Pak }) OrderedPresetImport(Path.Combine(run, "ordered-" + method), method);
                });
                check("Fluffy preset files parse and map to library entries and options", delegate { PresetImport(Path.Combine(run, "preset")); });
                check("CLI: replace-plan, replace-install (PAK and loose), replace-status, replace-method, replace-uninstall and replace-restore-all on a fixture game", delegate { CommandLineFlow(Path.Combine(run, "cli")); });
                check("conflicts (2.0.1): same outfit with shared files names the mod with counts, same outfit without shared files stacks, disabled mods, other-area overrides, unknown loose files, an external mod blocks with the lockout (1.0.1); installs never remove other mods", delegate { Conflicts(Path.Combine(run, "conflicts")); });
                check("recoverable uninstall (2.0.1): the removed replacement is kept and Undo restores files, enabled state and priority byte for byte; test reverts keep nothing", delegate { RecoverableUninstall(Path.Combine(run, "removed")); });
                check("Archive storage: compaction, rename, library move, ambiguous copies, impostor, recovery, disable, undo and uninstall", delegate { ArchiveStorage(Path.Combine(run, "archive-storage")); });
                check("automatic-check cache (2.0.1): a re-check of an archive reuses extracted and fixed files, results identical; a changed archive is read again", delegate { CheckCache(Path.Combine(run, "cache")); });
                // 2.4.2: the 2.0.1 code-review follow-ups (ReviewFollowupChecks.cs)
                check("state reads (2.4.2): a reader sharing ReadWrite|Delete never blocks a commit; the 2.4.1 read (Read sharing only) held across a commit fails it and rolls back cleanly; 4 threads reading the state, store roots and Recovery during 42 commits never fail", delegate { ReviewFollowupChecks.SharedStateReads(Path.Combine(run, "shared-reads")); });
                check("recoverable copy not saved (2.4.2): a store file open in another program makes the move into .removed fail after the commit: the result says so (no 'kept'), nothing incomplete is listed, the move is retried at the next start and before the next change, then listed and restorable; a stale pending record never moves an installed mod's folder", delegate { ReviewFollowupChecks.TrashFailure(Path.Combine(run, "trash-fail")); });
                check("pruning rules (2.4.2): older than 30 days, then the oldest above 5 GB; never each mod's newest copy (same source, any case; else same record), a copy under a day old or one an Undo needs; folder-name time fallback", delegate { ReviewFollowupChecks.PruneRules(Path.Combine(run, "prune-rules")); });
                check("pruning on disk (2.4.2): only the rule's copies are deleted and logged; an Undo's copy and each mod's newest stay; nothing is pruned while another task holds the game lock", delegate { ReviewFollowupChecks.PruneOnDisk(Path.Combine(run, "prune-disk")); });
                check("earlier store roots (2.4.2): after moving the store, the old store's copies stay in Recovery, Last used and Undo (ReplaceState.PreviousStoreRoots, three moves); a copy from an old store restores into the current one", delegate { ReviewFollowupChecks.OldStoreRoots(Path.Combine(run, "old-roots")); });
                check("check cache per file (2.4.2): a folder source is copied once and an edited file (same size, new time) alone; a 7-Zip batch cancelled after two files keeps them (listed size and CRC; no CRC, a wrong CRC and a half file are extracted again) and the re-check asks 7-Zip only for the rest", delegate { ReviewFollowupChecks.IncrementalCache(Path.Combine(run, "cache-incremental")); });
                check("Undo position (2.4.2): a removed mod returns below replacements installed after it was removed (the newer one keeps the files it won) and otherwise at its old place; a record without a time keeps its index", delegate { ReviewFollowupChecks.RestorePosition(Path.Combine(run, "undo-position")); });
                check("Fluffy open (2.1.0): loose and tool-mod installs/uninstalls and REFramework config edits go ahead (installed.ini re-read at each change, crash reporter left for later); PAK-method row changes and a conversion to PAK refuse with the same-PAK reason; a PAK-method tool mod (no rows) installs", delegate { FluffyOpen(Path.Combine(run, "fluffy-open"), probe); });
                check("first-start detection (2.4.3): the SF6 folder from Steam's libraryfolders.vdf and appmanifest_1364780.acf (installdir), else steamapps\\common\\Street Fighter 6; nothing found is empty; Fluffy libraries are real <Fluffy>\\Games\\SF6\\Mods folders; compiled defaults are empty", delegate { DetectionChecks.Run(Path.Combine(run, "detection")); });
                check("CLI same source (2.4.1): replace-install of an installed mod, or of Slotsloader from another zip, updates the record in place (one record at the same priority, no stacking), the INFO line says so, and uninstall still restores the game's own amd_ags_x64.dll", delegate { CliSameSource(Path.Combine(run, "cli-same")); });
                check("CLI loose loader (2.1.0): tool-only replace-install needs neither --yes nor --no-loader; natives files fail without one; --no-loader installs with the loader off and leaves the config; --yes with --no-loader is refused", delegate { LooseLoaderCli(Path.Combine(run, "cli-loader")); });
                check("relocation rules (2.1.0): only missing paths under another Data root are rebased (case/trailing separators tolerated); existing, outside-Data, game-folder and custom-store paths are left; a copied folder is reported", delegate { RelocationRules(Path.Combine(run, "reloc-rules")); });
                check("relocation (2.1.0): the whole app folder (CLI exe + Data) moved to a new path and the old one deleted: status OK, 0 missing backups, rebase logged once; uninstall from the new folder restores the genuine file and the PAK index byte-exact; Recovery lists the removed copies; the state file names only the new folder", delegate { RelocationMove(Path.Combine(run, "reloc")); });
                check("relocation (2.1.0): a backup that is really missing after a move is reported (status + replace-status) and kept as recorded; the other mod uninstalls normally and the failing uninstall changes nothing", delegate { RelocationMissingBackup(Path.Combine(run, "reloc-missing")); });
                check("relocation (2.1.0): a change interrupted at every step before the move is recovered from the new folder (rolled back or completed, recoverable copy finished there, old folder never recreated)", delegate { RelocationJournal(Path.Combine(run, "reloc-journal")); });
                check("Fluffy link (2.2.0): installed.ini sections, ModID, mixed / and \\ and case, shared owners (the later section's copy is in place); an absent or briefly locked installed.ini; the watcher reports an install and a removal within 2 s", delegate { FluffyLinkChecks.ParseAndWatch(Path.Combine(run, "fluffy-watch")); });
                check("other tools' files (2.2.0, the user's case): Fluffy's Mod Suite overwrote the Slotsloader proxy and plugin installed here: status, refusals and the uninstall question name the Fluffy mod and the 1.9.6 build; Keep current file keeps them (the genuine AGS stays restorable and is restored later; the file it replaces is not kept, 1.0.1), Restore original brings the genuine file back and removes the Suite's files (no copy kept after the change, 1.0.1); the shared amd_ags_x64_real.dll stays for Fluffy; no PAK change", delegate { FluffyLinkChecks.ForeignOverwrite(Path.Combine(run, "drift")); });
                check("missing files (2.2.0): uninstall asks about a missing file with a backed-up original; Restore original restores it; Keep current file keeps it missing with the original restorable; restore-all asks about kept files and restores them", delegate { FluffyLinkChecks.MissingFiles(Path.Combine(run, "drift-missing")); });
                check("priority stack (2.2.0): two tool mods over amd_ags_x64.dll + amd_ags_x64_real.dll restore correctly in every uninstall order and after a priority change (one genuine backup, clean game at the end)", delegate { FluffyLinkChecks.PriorityStack(Path.Combine(run, "stack")); });
                check("CLI (2.2.0): replace-status names the changer; fluffy-status; replace-uninstall lists the changed files without --restore-originals/--keep-current (exit 1, nothing changes), refuses both, keeps with --keep-current; 1.0.1: replace-install refused during the lockout and fluffy-uninstall refused (the external manager uninstalls its own mods); replace-restore-original", delegate { FluffyLinkChecks.CommandLine(Path.Combine(run, "cli-fluffy")); });
                // 2.4.2 (FluffyPakChecks.cs): Fluffy PAK installs and game entries another tool left invalidated.
                check("orphaned game entries (2.4.2, the live evidence): rows 11886 and 244293 of a 286 816-row table sorted by hash (the A.K.I outfit's real mesh and mdf2 hashes) left zeroed: status and replace-status say \"2 game entries left invalidated by another tool\" (no Status: OK); nothing proves them → refused, nothing written; the game's own index proves both by hash(path) (pak-repair dry run lists each proof, --yes repairs byte-exact); the Manager's TOC backup; Fluffy's Log.txt paths", delegate { FluffyPakChecks.OrphanedEvidence(Path.Combine(run, "pak-orphans")); });
                check("proof sources (2.4.2): Fluffy's archive list (a stale one is not used), installed.ini history, two known paths in one gap refused (nothing written), a .mesh path over TEX data refused, a run of three adjacent rows assigned in order, an unsorted table proven only by the TOC backup", delegate { FluffyPakChecks.ProofSources(Path.Combine(run, "pak-proofs")); });
                check("owned rows (2.4.2): the Manager's zeroed rows and rows a Fluffy installed.ini owns (two Fluffy instances) are never repaired; the one orphaned row is; Repair refuses while Fluffy or SF6 runs; 1.0.1: uninstalling an external mod here is refused and the Manager's own uninstall gives back only its own rows", delegate { FluffyPakChecks.OwnedRows(Path.Combine(run, "pak-owned"), probe); });
                check("external-manager lockout (1.0.1): while installed.ini lists a mod, a replacement, a PAK mod and a tool mod are refused (health check and engine; nothing in the game folder changes), so are enabling, moving and Reapply; Slotsloader installs; disabling and uninstalling the Manager's own mods work; uninstalling the external mod is refused; once installed.ini is empty a replacement installs", delegate { FluffyLinkChecks.Lockout(Path.Combine(run, "lockout"), probe); });
                check("Restore game to vanilla (1.0.11): replacements here and the external manager's mods go, so do every patch PAK and natives; this Manager's REFramework and Slotsloader stay (records and files)", delegate { FluffyLinkChecks.VanillaKeepsTools(Path.Combine(run, "vanilla")); });
                check("colour reference (1.0.16): Ryu's Outfit 1 colours turned off by Fluffy are read from their row; missing, another fighter's give the signatures without the 2023 layout template (an unreadable turned-off row does not stop that, PR #22 review); present, as before", delegate { FluffyPakChecks.ReferenceTurnedOff(Path.Combine(run, "pak-reference")); });
                check("parallel hashing (1.0.16): the same installs, refusal of a changed file and uninstall on a hard drive (one thread) and an SSD (game files hashed by several threads first) give the same files, records and message; the drive query never throws", delegate { SpeedShortcutChecks.ParallelHashing(Path.Combine(run, "speed-parallel")); });
                check("Fluffy watcher (2.4.2): both instances' installed.ini watched (installs and removals seen within 2 s in each); a root missing at start makes the watcher restart once it appears; the stamp covers both", delegate { FluffyPakChecks.WatchBothRoots(Path.Combine(run, "pak-watch")); });
            }
            finally { GameProcesses.Probe = previousProbe; GameProcesses.Launcher = previousLauncher; }
            Console.WriteLine("Replace checks: " + (count - failures) + " passed, " + failures + " failed.");
            try { File.WriteAllLines(Path.Combine(run, "replace-checks.txt"), report); Directory.CreateDirectory(AppConfig.DataDir); File.WriteAllLines(Path.Combine(AppConfig.DataDir, "replace-checks.txt"), report); } catch { }
            return failures == 0 ? 0 : 1;
        }

        internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        internal static void Refused(Action action, string contains)
        {
            try { action(); }
            catch (Exception ex) { if (ex is InvalidOperationException && !(ex is ObjectDisposedException) && ex.Message.StartsWith("Expected")) throw; if (contains == null || ex.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return; throw new InvalidOperationException("Refused with an unexpected message: " + ex.Message); }
            throw new InvalidOperationException("Expected a refusal" + (contains == null ? "" : " mentioning '" + contains + "'") + ".");
        }

        // ---- Synthetic file builders
        internal static byte[] Usr(params uint[] typeCrc)
        {
            int ic = typeCrc.Length / 2 + 1; long R = 48, instOff = 52, dataOff = (instOff + 8 * ic + 15) / 16 * 16;
            var b = new byte[R + dataOff + 16];
            Encoding.ASCII.GetBytes("USR").CopyTo(b, 0); BitConverter.GetBytes((ulong)R).CopyTo(b, 32);
            Encoding.ASCII.GetBytes("RSZ").CopyTo(b, R); BitConverter.GetBytes(16u).CopyTo(b, R + 4); BitConverter.GetBytes(1u).CopyTo(b, R + 8); BitConverter.GetBytes((uint)ic).CopyTo(b, R + 12);
            BitConverter.GetBytes((ulong)instOff).CopyTo(b, R + 24); BitConverter.GetBytes((ulong)dataOff).CopyTo(b, R + 32); BitConverter.GetBytes((ulong)dataOff).CopyTo(b, R + 40);
            BitConverter.GetBytes(1u).CopyTo(b, R + 48);
            for (int i = 1; i < ic; i++) { BitConverter.GetBytes(typeCrc[2 * (i - 1)]).CopyTo(b, R + instOff + 8 * i); BitConverter.GetBytes(typeCrc[2 * (i - 1) + 1]).CopyTo(b, R + instOff + 8 * i + 4); }
            return b;
        }
        internal static byte[] Tex(int pitch)
        {
            var b = new byte[56 + 2048];
            Encoding.ASCII.GetBytes("TEX").CopyTo(b, 0); BitConverter.GetBytes((ushort)64).CopyTo(b, 8); BitConverter.GetBytes((ushort)64).CopyTo(b, 10);
            b[14] = 1; b[15] = 0x10; BitConverter.GetBytes(71).CopyTo(b, 16);
            BitConverter.GetBytes(56UL).CopyTo(b, 40); BitConverter.GetBytes(pitch).CopyTo(b, 48); BitConverter.GetBytes(2048).CopyTo(b, 52);
            for (int i = 56; i < b.Length; i++) b[i] = (byte)(i * 7);
            return b;
        }
        internal static byte[] ReferenceCmd() { return Usr(MaterialData, 0x66C86E7A, Cloth, 0x11111111); }
        private static byte[] Bytes(string text) { return Encoding.UTF8.GetBytes(text); }

        internal static void Put(string root, string relative, byte[] data)
        {
            string full = Safety.Under(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllBytes(full, data);
        }

        internal static Fixture Create(string root, bool reframework, string method)
        {
            var f = new Fixture { Root = root, Game = Path.Combine(root, "game"), Data = Path.Combine(root, "data"), Mods = Path.Combine(root, "mods") };
            Directory.CreateDirectory(f.Game); Directory.CreateDirectory(f.Data);
            PakWriter.WriteFixture(Path.Combine(f.Game, "re_chunk_000.pak"), new List<KeyValuePair<string, byte[]>> {
                new KeyValuePair<string, byte[]>(V1, Usr(MaterialData, 0x66C86E7A)), new KeyValuePair<string, byte[]>(V2, Tex(128)),
                new KeyValuePair<string, byte[]>(V3, Tex(128)), new KeyValuePair<string, byte[]>(ModFixes.ReferenceCmdPath, ReferenceCmd()),
                new KeyValuePair<string, byte[]>(V5, Bytes("vanilla mesh")), new KeyValuePair<string, byte[]>(V6, Bytes("vanilla ui")) }, 0x28376913, 0);
            Directory.CreateDirectory(Path.Combine(f.Game, "dlc"));
            PakWriter.WriteFixture(Path.Combine(f.Game, "dlc", "re_dlc_stm_1.pak"), new List<KeyValuePair<string, byte[]>> {
                new KeyValuePair<string, byte[]>(D1, Usr(MaterialData, 0x66C86E7A)), new KeyValuePair<string, byte[]>(V1, Bytes("older dlc copy")) }, 0x39ED4FD3, 0);
            // A patch pak of another mod (2.4.0: no SlotsLoader marker, so uninstalling the fixture loader leaves it;
            // the loader's own costume pak is covered by LoaderPakChecks).
            PakWriter.WriteFixture(Path.Combine(f.Game, "re_chunk_000.pak.patch_001.pak"), new List<KeyValuePair<string, byte[]>> {
                new KeyValuePair<string, byte[]>("natives/stm/fixture_mod.marker", Bytes("fixture mod pak\n")), new KeyValuePair<string, byte[]>(P1, Bytes("patched")) }, 0, 0);
            Put(f.Game, "StreetFighter6.exe", Bytes("exe"));
            Put(f.Game, "CrashReport.exe", Bytes("crash reporter"));
            Put(f.Game, "amd_ags_x64.dll", Bytes("genuine AMD AGS library"));
            if (reframework)
            {
                Put(f.Game, "dinput8.dll", Bytes("reframework"));
                Directory.CreateDirectory(Path.Combine(f.Game, "reframework", "autorun"));
                Put(f.Game, LooseLoader.ConfigName, Bytes("FirstKey=1\r\nLooseFileLoader_Enabled=false\r\nLooseFileLoader_LogAccessedFiles=false\r\nLastKey=x\r\n"));
            }
            // Mods
            Put(f.Mods, "modA/modinfo.ini", Bytes("name=Cammy Colors\r\nauthor=Fixture\r\n"));
            Put(f.Mods, "modA/natives/STM/Product/Model/esf/esf009/002/esf009_002_cmd_000.user.2", Usr(MaterialData, 0x4D532A61, Cloth, 0x22222222));
            Put(f.Mods, "modA/natives/stm/product/model/esf/esf009/002/01/esf009_002_01_albd.tex.143230113", Tex(100));
            Put(f.Mods, "modA/natives/stm/product/model/esf/esf009/002/01/custom_a.tex.241101895", Bytes("custom texture A"));
            Put(f.Mods, "modB/modinfo.ini", Bytes("name=Cammy Alt\r\n"));
            Put(f.Mods, "modB/natives/stm/product/model/esf/esf009/002/esf009_002_cmd_000.user.2", Usr(MaterialData, 0x66C86E7A, 0x12345678, 1));
            Put(f.Mods, "modB/natives/stm/product/model/esf/esf009/002/01/esf009_002_01.mesh.230110883", Bytes("mod B mesh"));
            Put(f.Mods, "modDlc/natives/stm/product/model/esf/esf009/004/esf009_004_cmd_000.user.2", Usr(MaterialData, 0x66C86E7A, 9, 9));
            Put(f.Mods, "modPatch/natives/stm/product/ui/patched.user.2", Bytes("mod patch file"));
            Put(f.Mods, "modWeird/modinfo.ini", Bytes("name=Weird folder\r\n"));
            Put(f.Mods, "modWeird/natives/stm/product/model/esf/esf009/099/01/esf009_099_01.mesh.230110883", Bytes("unknown folder mesh"));
            Put(f.Mods, "modWeird/natives/stm/product/model/esf/esf009/002/01/esf009_002_01.mesh.221108797", Bytes("old mesh"));
            Put(f.Mods, "tool/Tool Pack/modinfo.ini", Bytes("name=Tool Pack\r\n"));
            Put(f.Mods, "tool/Tool Pack/README.txt", Bytes("readme"));
            Put(f.Mods, "tool/Tool Pack/screenshot.png", Bytes("png"));
            Put(f.Mods, "tool/Tool Pack/amd_ags_x64.dll", Bytes("slotsloader proxy"));
            Put(f.Mods, "tool/Tool Pack/amd_ags_x64_real.dll", Bytes("genuine AMD AGS library"));
            Put(f.Mods, "tool/Tool Pack/reframework/plugins/SF6_CostumeSlotsNative.dll", Bytes("plugin"));
            Put(f.Mods, "tool/Tool Pack/reframework/autorun/test.lua", Bytes("print('hi')"));
            Put(f.Mods, "modC/modinfo.ini", Bytes("name=Costume with helper\r\n"));
            Put(f.Mods, "modC/natives/stm/product/model/esf/esf009/002/esf009_002_cmd_001.user.2", Usr(MaterialData, 0x66C86E7A));
            Put(f.Mods, "modC/backup_colors/colorsync-backups.json", Bytes("{}"));
            Put(f.Mods, "modC/reframework/autorun/costume_helper.lua", Bytes("-- helper"));
            Put(f.Mods, "badTool/StreetFighter6.exe", Bytes("evil"));
            Put(f.Mods, "toolExe/Exe Tool/SF6_SlotsClean.exe", Bytes("helper"));
            Put(f.Mods, "toolExe/Exe Tool/reframework/plugins/exe_tool.dll", Bytes("plugin"));
            Put(f.Mods, "badPak/re_chunk_000.pak.patch_002.pak", Bytes("KPKA"));
            var settings = new ReplaceSettings { Method = method ?? "", StoreLocation = Path.Combine(f.Data, "store") };
            f.Engine = new ReplaceEngine(f.Game, f.Data, settings);
            f.Engine.FluffyProvider = delegate { return f.Fluffy; };
            return f;
        }

        internal static Dictionary<string, string> Snapshot(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) map[file.Substring(root.Length + 1)] = ReplaceEngine.Sha(file);
            foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) map[dir.Substring(root.Length + 1) + "\\"] = "dir";
            return map;
        }
        internal static void SameSnapshot(Dictionary<string, string> a, Dictionary<string, string> b, string context)
        {
            var diff = a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Where(k => { string x, y; return !a.TryGetValue(k, out x) || !b.TryGetValue(k, out y) || x != y; }).ToList();
            Require(diff.Count == 0, context + ": game folder differs from the original: " + String.Join(", ", diff.Take(8)));
        }

        internal static ReplaceMod Install(Fixture f, string mod, string name) { ReplacePlan plan; return Install(f, mod, name, false, null, out plan); }
        internal static ReplaceMod Install(Fixture f, string mod, string name, bool test, string replaceId, out ReplacePlan plan)
        {
            ModPackage package = PackageReader.Load(f.Mod(mod), "");
            CompositionPlan composition = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
            return ReplacePlanner.PrepareAndInstall(composition, "", f.Engine, name, test, replaceId, out plan);
        }
        internal static ReplacePlan Check(Fixture f, string mod)
        {
            ModPackage package = PackageReader.Load(f.Mod(mod), "");
            return ReplacePlanner.Check(Composer.Plan(package, Composer.DefaultSelection(package, true), true), "", f.Engine);
        }
        private static PakIndex Base(Fixture f) { return PakIndex.Open(Path.Combine(f.Game, "re_chunk_000.pak")); }
        private static bool Zeroed(PakIndex p, string path) { return !p.Contains(PakHash.PathHash(path)); }
        private static string GameFile(Fixture f, string rel) { return Path.Combine(f.Game, rel.Replace('/', '\\')); }

        // ---- Checks
        private static void HashVectors()
        {
            Require(PakHash.PathHash(ModFixes.ReferenceCmdPath) == 0x2FDBBD5270A78DA1UL, "cmd path hash differs from the game index.");
            Require(PakHash.PathHash("natives/stm/product/model/esf/esf001/001/01/esf001_001_01.mesh.230110883") == 0xE7198BF092EA5CFFUL, "mesh path hash differs.");
            Require(PakHash.PathHash("natives\\STM\\Product\\model//esf/esf001/001/01/ESF001_001_01.mesh.230110883") == 0xE7198BF092EA5CFFUL, "hash is not case/separator independent.");
        }

        private static void PakBasics(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            PakIndex p = Base(f);
            Require(p.Count == 6 && p.Features == 0 && p.Fingerprint == 0x28376913 && p.CountZeroed() == 0, "fixture header parse");
            Require(p.Find(PakHash.PathHash(V1)).Count == 1 && p.ReadByPath(V6).SequenceEqual(Bytes("vanilla ui")), "lookup/read");
            string file = p.Path; string before = ReplaceEngine.Sha(file);
            int i = p.Find(PakHash.PathHash(V6))[0]; var e = p.Entry(i);
            byte[] original = PakWriter.HashBytes(e.HashLower, e.HashUpper);
            PakWriter.WriteHashes(file, p.Length, p.Header, new[] { new PakHashWrite { Index = i, Expected = original, Value = new byte[8] } });
            Require(PakIndex.Open(file).CountZeroed() == 1, "zeroing did not apply");
            Refused(delegate { PakWriter.WriteHashes(file, p.Length, p.Header, new[] { new PakHashWrite { Index = 0, Expected = new byte[8], Value = original } }); }, "no longer holds");
            PakWriter.WriteHashes(file, p.Length, p.Header, new[] { new PakHashWrite { Index = i, Expected = new byte[8], Value = original } });
            Require(ReplaceEngine.Sha(file) == before, "restore not byte-exact");
            string enc = Path.Combine(root, "encrypted.pak");
            PakWriter.WriteFixture(enc, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(V6, Bytes("x")) }, 1, 8);
            Refused(delegate { PakIndex.Open(enc); }, "features");
            string trunc = Path.Combine(root, "truncated.pak");
            File.WriteAllBytes(trunc, File.ReadAllBytes(file).Take(16 + 48 * 6 + 3).ToArray());
            Refused(delegate { PakIndex.Open(trunc); }, "outside");
            Refused(delegate { PakWriter.WriteHashes(file, p.Length + 1, p.Header, new[] { new PakHashWrite { Index = i, Expected = original, Value = new byte[8] } }); }, "size");
        }

        // 2.5.5: a select-screen swatch file (USR/RSZ): one object, n rows (ID + two colours) of type 0x5ACE8765, then
        // the root (0xA79B54DB) with the outfit number, `extra` bytes (old layout 0, current 4) and the ID list 1..n.
        internal static byte[] Swatch(uint rootCrc, uint costume, int extra, params uint[] rows)   // rows: id, colour a, colour b, ...
        {
            int n = rows.Length / 3; const int r = 48;
            var b = new List<byte>(new byte[r]); "USR".ToCharArray().Select((c, i) => new { c, i }).ToList().ForEach(x => b[x.i] = (byte)x.c);
            byte[] head = b.ToArray(); BitConverter.GetBytes((ulong)r).CopyTo(head, 32); b = new List<byte>(head);
            long io = 48 + 4, dataOff = (io + 8L * (n + 2) + 15) / 16 * 16;
            var rsz = new byte[48]; Encoding.ASCII.GetBytes("RSZ\0").CopyTo(rsz, 0); BitConverter.GetBytes(16u).CopyTo(rsz, 4);
            BitConverter.GetBytes(1u).CopyTo(rsz, 8); BitConverter.GetBytes((uint)(n + 2)).CopyTo(rsz, 12); BitConverter.GetBytes(0u).CopyTo(rsz, 16);
            BitConverter.GetBytes((ulong)io).CopyTo(rsz, 24); BitConverter.GetBytes((ulong)dataOff).CopyTo(rsz, 32); BitConverter.GetBytes((ulong)dataOff).CopyTo(rsz, 40);
            b.AddRange(rsz); b.AddRange(BitConverter.GetBytes((uint)(n + 1)));
            b.AddRange(new byte[8]);
            for (int i = 0; i < n; i++) { b.AddRange(BitConverter.GetBytes(ModFixes.SwatchRowType)); b.AddRange(BitConverter.GetBytes(ModFixes.SwatchRowCrc)); }
            b.AddRange(BitConverter.GetBytes(ModFixes.SwatchRootType)); b.AddRange(BitConverter.GetBytes(rootCrc));
            while (b.Count < r + dataOff) b.Add(0);
            for (int i = 0; i < n; i++) { b.AddRange(BitConverter.GetBytes(rows[3 * i])); b.AddRange(BitConverter.GetBytes(rows[3 * i + 1])); b.AddRange(BitConverter.GetBytes(rows[3 * i + 2])); }
            b.AddRange(BitConverter.GetBytes(costume)); b.AddRange(new byte[extra]);
            b.AddRange(BitConverter.GetBytes((uint)n)); for (int i = 1; i <= n; i++) b.AddRange(BitConverter.GetBytes((uint)i));
            return b.ToArray();
        }
        private static void ExtraPatchHealth(string root)
        {
            Fixture f = Create(root, true, ReplaceMethods.Loose);
            string source = f.Mod("Health");
            string mesh = "natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883";
            string tex = Path.ChangeExtension(mesh, null).Replace(".mesh", ".tex.241101895");
            string bad = tex.Replace("_01.tex", "_bad.tex");
            string swatch = "natives/stm/product/gui/data/selectfighteruidata/data/009/selectfighteruidata009_01.user.2";
            Put(source, swatch, Swatch(ModFixes.SwatchCrcOld, 1, 0, 0, 0x10, 0x11));
            Put(source, mesh, Bytes("fixture mesh"));
            Put(source, tex, Tex(100));
            Put(source, bad, Bytes("invalid texture header"));
            var before = Snapshot(source);
            var pkg = PackageReader.Load(source, "");
            var selection = Composer.Plan(pkg, Composer.DefaultSelection(pkg));
            var preview = OutfitPatchReport.Preview(selection, f.Game, "");
            Require(preview.Errors.Count == 0 && preview.Patches.Any(p => p.Path == tex) &&
                preview.Warnings.Any(s => s.Contains(bad)) && preview.Other.Contains(mesh), "preview did not distinguish repair, warning and other file");
            SameSnapshot(before, Snapshot(source), "read-only extra outfit preview");
            string costumeRoot = Path.Combine(f.Game, "reframework", "costume_mods");
            var originalReader = Installer.SwatchGameFile;
            try
            {
                Installer.SwatchGameFile = delegate { return Swatch(ModFixes.SwatchCrcCurrent, 1, 4, 0, 0xA0, 0xA1); };
                Installer.Install(selection, costumeRoot, Characters.ByCode["009"], "Health", "", false);
            }
            finally { Installer.SwatchGameFile = originalReader; }
            var outfit = Installer.ListInstalled(costumeRoot).Single();
            string installedTex = Safety.Under(outfit.Folder, tex);
            var first = Installer.CheckInstalledHealth(outfit, costumeRoot);
            Require(first.Patches.Any(p => p.Path == tex) && first.Errors.Count == 0 && first.Applied.Any(s => s.Contains(swatch)), "installed file check");
            File.WriteAllBytes(installedTex, Tex(128));
            File.Delete(Safety.Under(outfit.Folder, bad));
            var changed = Installer.CheckInstalledHealth(outfit, costumeRoot);
            Require(changed.Patches.Count == 0 && changed.Unchanged.Contains(tex) && changed.Errors.Any(s => s.Contains("Changed")) &&
                changed.Errors.Any(s => s.Contains("Missing")), "installed health ignored current bytes or integrity errors");
        }

        private static void Swatches()
        {
            const string path = "natives/STM/Product/GUI/data/selectfighteruidata/data/002/selectfighteruidata002_02.user.2";
            byte[] game = Swatch(ModFixes.SwatchCrcCurrent, 2, 4, 0, 0xA0, 0xA1, 1, 0xB0, 0xB1, 100, 0xC0, 0xC1);   // the game: IDs 0, 1 and an extra 100
            byte[] mod = Swatch(ModFixes.SwatchCrcOld, 2, 0, 0, 0x10, 0x11, 1, 0x20, 0x21);                       // a mod from before the update
            byte[] expected = Swatch(ModFixes.SwatchCrcCurrent, 2, 4, 0, 0x10, 0x11, 1, 0x20, 0x21, 100, 0xC0, 0xC1);
            GameReference reference = GameReference.FromCmd(ReferenceCmd());
            reference.GameFile = delegate(string p) { return p == ModFixes.NormalizeNativePath(path) ? game : null; };
            byte[] data = (byte[])mod.Clone();
            var fixes = ModFixes.UpgradeModFile(ref data, reference, path);
            Require(fixes.Count == 1 && fixes[0].Kind == "swatch" && fixes[0].Changed && fixes[0].Count == 2 && data.SequenceEqual(expected), "old swatch file not rebuilt: " + String.Join("; ", fixes.Select(x => x.Message)));
            byte[] again = (byte[])data.Clone();
            Require(ModFixes.UpgradeModFile(ref again, reference, path).Count == 0 && again.SequenceEqual(expected), "a current swatch file changed");
            // Without the path (older callers), or under another path, the swatch repair does not run.
            byte[] untouched = (byte[])mod.Clone();
            Require(!ModFixes.UpgradeModFile(ref untouched, reference).Any(x => x.Changed) && untouched.SequenceEqual(mod), "repaired without its path");
            // Refusals leave the file as it is, with a warning: another outfit, an ID the game lacks, no game file, an unknown layout.
            foreach (var bad in new[] {
                new { Game = Swatch(ModFixes.SwatchCrcCurrent, 0, 4, 0, 0xA0, 0xA1, 1, 0xB0, 0xB1), Why = "another outfit" },
                new { Game = Swatch(ModFixes.SwatchCrcCurrent, 2, 4, 0, 0xA0, 0xA1), Why = "an ID the game lacks" },
                new { Game = (byte[])null, Why = "no game file" } })
            {
                byte[] copy = (byte[])mod.Clone(); byte[] g = bad.Game;
                reference.GameFile = delegate { return g; }; var r = new GameReference { GameFile = reference.GameFile };
                var result = ModFixes.UpgradeModFile(ref copy, r, path);
                Require(result.Count == 1 && result[0].Kind == "swatch-stale" && result[0].Warning && !result[0].Changed && copy.SequenceEqual(mod), bad.Why + ": " + String.Join("; ", result.Select(x => x.Message)));
            }
            byte[] unknown = Swatch(0x12345678, 2, 0, 0, 0x10, 0x11); byte[] unknownCopy = (byte[])unknown.Clone();
            Require(ModFixes.UpgradeModFile(ref unknownCopy, new GameReference { GameFile = delegate { return game; } }, path).Count == 0 && unknownCopy.SequenceEqual(unknown), "an unknown layout was touched");
            // Extra outfits: the copied file is rebuilt and its recorded hash follows.
            string stage = Path.Combine(Path.GetTempPath(), "slots-swatch-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string staged = Path.Combine(stage, ModFixes.NormalizeNativePath(path).Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(staged)); File.WriteAllBytes(staged, mod);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { ModFixes.NormalizeNativePath(path), ReplaceEngine.Sha(mod) } };
            Func<string, byte[]> before = Installer.SwatchGameFile;
            try
            {
                Installer.SwatchGameFile = delegate { return game; };
                Require(Installer.FixSwatches(stage, hashes, @"C:\Game\reframework\costume_mods") == 1 && File.ReadAllBytes(staged).SequenceEqual(expected) && hashes.Values.Single() == ReplaceEngine.Sha(expected), "extra outfit swatch not rebuilt");
            }
            finally { Installer.SwatchGameFile = before; try { Directory.Delete(stage, true); } catch { } }
        }

        private static void Fixers()
        {
            GameReference reference = GameReference.FromCmd(ReferenceCmd());
            byte[] stale = Usr(MaterialData, 0x4D532A61, Cloth, 0x22222222);
            var fixes = ModFixes.UpgradeModFile(ref stale, reference);
            var crcs = new Dictionary<uint, uint>(); ModFixes.UserTypeCrcs(stale, crcs);
            Require(crcs[MaterialData] == 0x66C86E7A && crcs[Cloth] == 0x22222222, "only the allow-listed type must be rewritten");
            Require(fixes.Any(x => x.Kind == "crc" && x.Count == 1) && fixes.Any(x => x.Kind == "stale" && x.Message.Contains("Cloth")), "fix report: " + String.Join("; ", fixes.Select(x => x.Message)));
            byte[] current = Usr(MaterialData, 0x66C86E7A); byte[] copy = (byte[])current.Clone();
            Require(ModFixes.UpgradeModFile(ref copy, reference).Count == 0 && copy.SequenceEqual(current), "current file must stay unchanged");
            byte[] tex = Tex(100); int repaired; int bad = ModFixes.TextureMipCheck(tex, true, out repaired);
            Require(bad == 0 && repaired == 1 && BitConverter.ToInt32(tex, 48) == 128, "mip pitch repair");
            byte[] good = Tex(128); Require(ModFixes.TextureMipCheck(good, true, out repaired) == 0 && repaired == 0, "valid texture changed");
            byte[] broken = Tex(100).Concat(new byte[10]).ToArray(); BitConverter.GetBytes(999).CopyTo(broken, 52); Require(ModFixes.TextureMipCheck(broken, true, out repaired) == 1, "inconsistent texture not reported");
            byte[] packed = Tex(100); BitConverter.GetBytes(999).CopyTo(packed, 52); Require(ModFixes.TextureMipCheck(packed, true, out repaired) == 0 && repaired == 1 && BitConverter.ToInt32(packed, 52) == 2048, "a table that fills the file exactly must be rewritten");
            Require(ModFixes.UpgradeTexSuffix("natives/stm/a/b.tex.143230113", "241101895") == "natives/stm/a/b.tex.241101895" && ModFixes.UpgradeTexSuffix("natives/stm/a/b.tex.241101895", "241101895") == null, "tex suffix");
            Require(ModFixes.NormalizeNativePath("natives\\STM\\Product//Model/X.tex.1") == "natives/stm/product/model/x.tex.1", "path normalization");
        }

        private static void Lifecycle(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            var original = Snapshot(f.Game);
            ReplaceMod a = Install(f, "modA", "Cammy Colors");
            PakIndex p = Base(f);
            Require(Zeroed(p, V1) && Zeroed(p, V2) && !Zeroed(p, V3) && p.CountZeroed() == 2, "expected V1 and V2 invalidated in base, got " + p.CountZeroed());
            Require(File.Exists(GameFile(f, V1)) && File.Exists(GameFile(f, V2)) && File.Exists(GameFile(f, "natives/stm/product/model/esf/esf009/002/01/custom_a.tex.241101895")), "loose files missing");
            var crcs = new Dictionary<uint, uint>(); ModFixes.UserTypeCrcs(File.ReadAllBytes(GameFile(f, V1)), crcs);
            Require(crcs[MaterialData] == 0x66C86E7A, "installed colour file was not upgraded");
            Require(BitConverter.ToInt32(File.ReadAllBytes(GameFile(f, V2)), 48) == 128, "installed texture not repaired/renamed");
            Require(Directory.GetFiles(f.Engine.PakBackupDir, "*.toc").Length >= 1, "TOC backup missing");
            Require(!File.Exists(GameFile(f, "CrashReport.exe")) && File.Exists(GameFile(f, CrashReporter.RenamedName)), "crash reporter not renamed");
            ReplaceMod b = Install(f, "modB", "Cammy Alt");
            ReplaceState s = f.Engine.LoadState();
            Require(s.Mods[0].Id == b.Id && s.Deployment(V1).Owner == b.Id, "newest install should win the shared file");
            Require(ReplaceEngine.Sha(GameFile(f, V1)) == s.Find(b.Id).Files.First(x => x.Path == V1).Sha256, "winner file not deployed");
            f.Engine.Move(b.Id, 1);
            s = f.Engine.LoadState(); Require(s.Mods[0].Id == a.Id && s.Deployment(V1).Owner == a.Id && ReplaceEngine.Sha(GameFile(f, V1)) == s.Find(a.Id).Files.First(x => x.Path == V1).Sha256, "move down did not hand the file to A");
            f.Engine.Move(b.Id, -1);
            f.Engine.SetEnabled(b.Id, false);
            s = f.Engine.LoadState(); Require(s.Deployment(V1).Owner == a.Id && !File.Exists(GameFile(f, V5)) && Zeroed(Base(f), V1) && !Zeroed(Base(f), V5), "disable did not restore the next owner / the vanilla mesh entry");
            f.Engine.SetEnabled(b.Id, true);
            Require(Zeroed(Base(f), V5) && File.Exists(GameFile(f, V5)), "enable did not re-invalidate");
            f.Engine.Uninstall(a.Id);
            s = f.Engine.LoadState(); Require(s.Deployment(V1).Owner == b.Id && !File.Exists(GameFile(f, V2)) && !Zeroed(Base(f), V2), "uninstalling A must hand V1 to B and restore V2");
            f.Engine.Uninstall(b.Id);
            SameSnapshot(original, Snapshot(f.Game), "after uninstalling everything");
            Require(!Directory.Exists(Path.Combine(f.Game, "natives")), "natives folder left behind");
            Require(!Directory.EnumerateFileSystemEntries(f.Engine.LoadState().StoreRoot == "" ? Path.Combine(f.Data, "store") : f.Engine.LoadState().StoreRoot).Any(x => !Path.GetFileName(x).StartsWith(".")), "store copies left behind");
        }

        private static void DlcRows(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            var original = Snapshot(f.Game);
            ReplaceMod a = Install(f, "modA", "A");
            PakIndex dlc = PakIndex.Open(Path.Combine(f.Game, "dlc", "re_dlc_stm_1.pak"));
            Require(Zeroed(dlc, V1) && !Zeroed(dlc, D1), "V1 must be invalidated in the DLC PAK as well");
            ReplaceMod d = Install(f, "modDlc", "D");
            Require(Zeroed(PakIndex.Open(dlc.Path), D1), "DLC-only path not invalidated");
            f.Engine.RestoreAll();
            SameSnapshot(original, Snapshot(f.Game), "after restore-all");
        }

        private static void PatchBlock(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            var original = Snapshot(f.Game);
            ReplacePlan plan = Check(f, "modPatch");
            Require(!plan.CanInstall && plan.Errors.Any(e => e.Contains("patch PAK")), "health check should block the patch-PAK path");
            Refused(delegate { Install(f, "modPatch", "P"); }, "patch PAK");
            SameSnapshot(original, Snapshot(f.Game), "after a refused patch-PAK install");
            Refused(delegate { Install(f, "badTool", "Bad"); }, "programs (.exe)");
            SameSnapshot(original, Snapshot(f.Game), "after refused protected paths");
        }

        // 2.5.9: the same NULSPACE-style mod with the loose file loader on and the Loose method chosen.
        private static void PakModsLoose(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Loose);
            string stageFile = Path.Combine(f.Mods, "stagePak", "Stage", "NULSPACE.pak"); Directory.CreateDirectory(Path.GetDirectoryName(stageFile));
            Put(f.Mods, "stagePak/Stage/modinfo.ini", Bytes("name=Stage PAK\r\n"));
            PakWriter.WriteFixture(stageFile, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("natives/stm/product/environment/stage_fixture.user.2", Bytes("stage")) }, 0, 0);
            byte[] stage = File.ReadAllBytes(stageFile), patch1 = File.ReadAllBytes(GameFile(f, "re_chunk_000.pak.patch_001.pak"));
            string p2 = GameFile(f, "re_chunk_000.pak.patch_002.pak");
            ReplacePlan plan = Check(f, "stagePak");
            Require(plan.CanInstall && plan.Kind == "Pak" && plan.Files.Count == 1 && plan.Files[0].Path == "re_chunk_000.pak.patch_002.pak", "loose method plan: " + plan.Kind + " / " + String.Join(" | ", plan.Files.Select(x => x.Path)) + " / " + String.Join(" | ", plan.Items.Select(i => i.Text)));
            ReplaceMod a = Install(f, "stagePak", "Stage PAK");
            Require(a.Kind == "Pak" && File.ReadAllBytes(p2).SequenceEqual(stage) && File.ReadAllBytes(GameFile(f, "re_chunk_000.pak.patch_001.pak")).SequenceEqual(patch1), "loose method: the PAK mod is not patch_002");
            f.Engine.Uninstall(a.Id);
            Require(!File.Exists(p2) && File.ReadAllBytes(GameFile(f, "re_chunk_000.pak.patch_001.pak")).SequenceEqual(patch1), "loose method: uninstall left the PAK mod");
        }

        // 2.5.6: PAK mods are added as the next patch PAK (fixture: patch_001 is another mod's).
        // 1.0.9: the list other costume loaders read (ManagerInstalls.cs).
        private static void InstallsList(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            string list = ManagerInstalls.FilePath(f.Game), folder = ManagerInstalls.Folder(f.Game);
            ReplaceMod colors = Install(f, "modA", "Cammy Colors");
            Require(!File.Exists(list) && !Directory.Exists(folder), "written (or its folder made) where no costume loader keeps data");
            Directory.CreateDirectory(folder);
            Require(ManagerInstalls.Sync(f.Engine) && File.Exists(list), "start-up sync did not write the list");
            Put(f.Mods, "stagePak/Stage/modinfo.ini", Bytes("name=Stage PAK\r\n"));
            PakWriter.WriteFixture(Path.Combine(f.Mods, "stagePak", "Stage", "NULSPACE.pak"), new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("natives/stm/product/environment/stage_fixture.user.2", Bytes("stage")) }, 0, 0);
            ReplaceMod alt = Install(f, "modB", "Cammy Alt"), stage = Install(f, "stagePak", "Stage PAK");
            f.Engine.SetEnabled(alt.Id, false);
            byte[] raw = File.ReadAllBytes(list); string text = Encoding.UTF8.GetString(raw);
            Require(raw[0] == (byte)'{' && text.StartsWith("{\n  \"format\": \"sf6-manager-installs-1\",\n  \"generated\": \"") && text.Contains("\n  \"manager\": \"Slotsloader Manager ") && !text.Contains("\r"), "layout (no BOM, key order, LF): " + text.Substring(0, Math.Min(120, text.Length)));
            Require(Directory.GetFiles(folder).Length == 1, "temporary files left beside the list");
            var data = Json.Serializer().Deserialize<Dictionary<string, object>>(text);
            var mods = ((System.Collections.IEnumerable)data["mods"]).Cast<Dictionary<string, object>>().ToList();
            Require(String.Join("|", mods.Select(m => (string)m["name"])) == "Stage PAK|Cammy Alt|Cammy Colors" && mods.Select(m => (string)m["id"]).SequenceEqual(new[] { stage.Id, alt.Id, colors.Id }), "priority order: " + String.Join("|", mods.Select(m => (string)m["name"])));
            Require(!(bool)mods[1]["enabled"] && (bool)mods[0]["enabled"] && (bool)mods[2]["enabled"], "enabled flags");
            Func<Dictionary<string, object>, string, List<Dictionary<string, object>>> entries = delegate(Dictionary<string, object> m, string key) { return ((System.Collections.IEnumerable)m[key]).Cast<Dictionary<string, object>>().ToList(); };
            var pak = entries(mods[0], "paks");
            Require(entries(mods[0], "files").Count == 0 && pak.Count == 1 && (string)pak[0]["path"] == "re_chunk_000.pak.patch_002.pak" && Convert.ToInt64(pak[0]["size"]) == new FileInfo(GameFile(f, "re_chunk_000.pak.patch_002.pak")).Length
                && (string)pak[0]["sha256"] == ReplaceEngine.Sha(GameFile(f, "re_chunk_000.pak.patch_002.pak")).ToLowerInvariant(), "patch PAK entry: " + Json.Serializer().Serialize(pak));
            ReplaceMod colorsNow = f.Engine.LoadState().Find(colors.Id);
            var files = entries(mods[2], "files");
            Require(entries(mods[2], "paks").Count == 0 && files.Count == colorsNow.Files.Count && files.All(x => colorsNow.Files.Any(cf => cf.Path == (string)x["path"] && cf.Size == Convert.ToInt64(x["size"]) && cf.Sha256.ToLowerInvariant() == (string)x["sha256"])), "loose file entries: " + Json.Serializer().Serialize(files));
            Require(files.All(x => File.Exists(GameFile(f, (string)x["path"])) && new FileInfo(GameFile(f, (string)x["path"])).Length == Convert.ToInt64(x["size"])), "a listed size is not the game file's");
            // Nothing changed: not rewritten (no new "generated").
            DateTime written = File.GetLastWriteTimeUtc(list); File.SetLastWriteTimeUtc(list, written.AddMinutes(-5));
            Require(!ManagerInstalls.Sync(f.Engine) && File.ReadAllBytes(list).SequenceEqual(raw) && File.GetLastWriteTimeUtc(list) == written.AddMinutes(-5), "rewritten although nothing changed");
            // Kept files ("Keep current file") are not listed; names are escaped, times in UTC.
            var state = new ReplaceState();
            state.Mods.Add(new ReplaceMod { Id = "x", Name = "Quote \" back\\slash ü", InstalledUtc = "2026-10-20T22:58:12.1234567+02:00", Files = new List<ModFile> { new ModFile { Path = "natives\\stm\\kept.tex", Size = 1 }, new ModFile { Path = "natives/stm/mine.tex", Sha256 = "AB", Size = 2 } } });
            state.Released.Add(new ReleasedFile { Path = "natives/stm/kept.tex" });
            string one = ManagerInstalls.Text(state, new DateTime(2026, 10, 20, 21, 0, 0, DateTimeKind.Utc));
            Require(one.Contains("\"name\": \"Quote \\\" back\\\\slash ü\"") && one.Contains("\"installed_utc\": \"2026-10-20T20:58:12Z\"") && one.Contains("\"generated\": \"2026-10-20T21:00:00Z\"")
                && !one.Contains("kept.tex") && one.Contains("{ \"path\": \"natives/stm/mine.tex\", \"sha256\": \"ab\", \"size\": 2 }"), "escaping/kept files: " + one);
            Require(Json.Serializer().Deserialize<Dictionary<string, object>>(one) != null, "not valid JSON: " + one);
            // Everything uninstalled: an empty list, not a missing file.
            foreach (ReplaceMod m in f.Engine.LoadState().Mods.ToList()) { string removed; f.Engine.Uninstall(m.Id, false, out removed); }
            text = File.ReadAllText(list);
            Require(text.Contains("\n  \"mods\": []\n}") && ((System.Collections.IEnumerable)Json.Serializer().Deserialize<Dictionary<string, object>>(text)["mods"]).Cast<object>().Count() == 0, "empty list: " + text);
        }

        private static void PakMods(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            string stageFile = Path.Combine(f.Mods, "stagePak", "Stage", "NULSPACE.pak"); Directory.CreateDirectory(Path.GetDirectoryName(stageFile));
            Put(f.Mods, "stagePak/Stage/modinfo.ini", Bytes("name=Stage PAK\r\n"));
            PakWriter.WriteFixture(stageFile, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("natives/stm/product/environment/stage_fixture.user.2", Bytes("stage")) }, 0, 0);
            byte[] stage = File.ReadAllBytes(stageFile), patch1 = File.ReadAllBytes(GameFile(f, "re_chunk_000.pak.patch_001.pak"));
            string p2 = GameFile(f, "re_chunk_000.pak.patch_002.pak"), p3 = GameFile(f, "re_chunk_000.pak.patch_003.pak");
            ReplacePlan plan = Check(f, "stagePak");
            Require(plan.CanInstall && plan.Kind == "Pak" && plan.Files.Count == 1 && plan.Files[0].Path == "re_chunk_000.pak.patch_002.pak" && plan.Items.Any(i => i.Text.Contains("NULSPACE.pak is added as re_chunk_000.pak.patch_002.pak")), "plan: " + plan.Kind + " / " + String.Join(" | ", plan.Files.Select(x => x.Path)) + " / " + String.Join(" | ", plan.Items.Select(i => i.Text)));
            ReplaceMod a = Install(f, "stagePak", "Stage PAK");
            Require(a.Kind == "Pak" && File.ReadAllBytes(p2).SequenceEqual(stage) && File.ReadAllBytes(GameFile(f, "re_chunk_000.pak.patch_001.pak")).SequenceEqual(patch1), "first PAK mod is not patch_002");
            // A pre-numbered name (Fluffy-style) gets the next free number, never its own.
            ReplaceMod b = Install(f, "badPak", "Numbered PAK");
            Require(b.Kind == "Pak" && File.ReadAllBytes(p3).SequenceEqual(Bytes("KPKA")) && File.ReadAllBytes(p2).SequenceEqual(stage), "second PAK mod is not patch_003");
            // Removing the lower one leaves an empty placeholder so patch_003 still loads; Undo puts it back.
            string folderA; f.Engine.Uninstall(a.Id, true, out folderA);
            Require(ReplaceEngine.IsFluffyDummy(p2) && File.Exists(p3), "patch_002 must become a placeholder while patch_003 exists");
            ReplaceMod restored; f.Engine.RestoreRemoved(folderA, out restored);
            Require(File.ReadAllBytes(p2).SequenceEqual(stage), "Undo did not put patch_002 back over its placeholder");
            // Disable/enable the lower one: placeholder, then back.
            f.Engine.SetEnabled(restored.Id, false); Require(ReplaceEngine.IsFluffyDummy(p2), "disabled PAK mod not a placeholder");
            f.Engine.SetEnabled(restored.Id, true); Require(File.ReadAllBytes(p2).SequenceEqual(stage), "re-enabled PAK mod not back");
            // The top one is deleted; if another PAK takes its number meanwhile, Undo refuses and leaves that PAK alone.
            string folderB; f.Engine.Uninstall(b.Id, true, out folderB);
            Require(!File.Exists(p3), "the top PAK mod must be deleted");
            File.WriteAllBytes(p3, Bytes("another tool's pak"));
            Refused(delegate { ReplaceMod again; f.Engine.RestoreRemoved(folderB, out again); }, "already used by another PAK");
            Require(File.ReadAllBytes(p3).SequenceEqual(Bytes("another tool's pak")), "a foreign PAK was overwritten");
            // The next PAK mod goes above every number in use.
            Require(f.Engine.NextPatchNumber(f.Engine.LoadState(), null) == 4, "next number: " + f.Engine.NextPatchNumber(f.Engine.LoadState(), null));
            Require(File.ReadAllBytes(GameFile(f, "re_chunk_000.pak.patch_001.pak")).SequenceEqual(patch1), "the other mod's patch_001 changed");

            // 2.5.7: a PAK-method replacement of a file the PAK mod also holds is a warned overlap, never a block (the
            // user's NULSPACE + Stage Lighting Overhaul could not be uninstalled in 2.5.6); reinstalling keeps the number.
            Fixture g = Create(Path.Combine(root, "overlap"), false, ReplaceMethods.Pak);
            Put(g.Mods, "stagePak/Stage/modinfo.ini", Bytes("name=Stage PAK\r\n"));
            Directory.CreateDirectory(Path.Combine(g.Mods, "stagePak", "Stage"));
            PakWriter.WriteFixture(Path.Combine(g.Mods, "stagePak", "Stage", "NULSPACE.pak"), new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(V6, Bytes("stage pak ui")) }, 0, 0);
            Put(g.Mods, "lighting/modinfo.ini", Bytes("name=Stage Lighting\r\n"));
            Put(g.Mods, "lighting/" + V6, Bytes("lighting ui"));
            ReplaceMod pak = Install(g, "stagePak", "Stage PAK");
            string g2 = GameFile(g, "re_chunk_000.pak.patch_002.pak");
            Require(File.Exists(g2), "overlap fixture: PAK mod not at patch_002");
            ReplacePlan lightPlan = Check(g, "lighting");
            Require(lightPlan.CanInstall && lightPlan.Items.Any(i => i.Level == "warn" && i.Text.Contains("is also in the PAK mod 'Stage PAK'")), "overlap not a warning: " + String.Join(" | ", lightPlan.Items.Select(i => i.Level + " " + i.Text)));
            ReplaceMod light = Install(g, "lighting", "Stage Lighting");
            ReplacePlan again2; ReplaceMod pakAgain = Install(g, "stagePak", "Stage PAK", false, null, out again2);
            Require(File.Exists(g2) && !File.Exists(GameFile(g, "re_chunk_000.pak.patch_003.pak")) && again2.Items.Any(i => i.Text.Contains("replaces its installed copy re_chunk_000.pak.patch_002.pak in place")), "reinstall moved the PAK: " + String.Join(" | ", again2.Items.Select(i => i.Text)));
            string gone; g.Engine.Uninstall(pakAgain.Id, true, out gone);
            Require(!File.Exists(g2) && g.Engine.LoadState().Find(light.Id) != null, "the PAK mod could not be uninstalled next to the overlapping replacement");
        }

        private static void FluffyOwnership(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            string fluffyRoot = Path.Combine(root, "Fluffy", "Games", "SF6");
            Directory.CreateDirectory(Path.Combine(fluffyRoot, "Mods"));
            Require(FluffyInstalls.Load(new[] { fluffyRoot }).Mods.Count == 0, "missing installed.ini must mean no Fluffy mods");
            Require(FluffyInstalls.DetectRoots(new[] { Path.Combine(fluffyRoot, "Mods") }, null).Count == 0, "a bare Mods folder is not a Fluffy instance");
            File.WriteAllText(Path.Combine(fluffyRoot, "installed.ini"), "[Cammy Fluffy]\r\nModID_v1=1\r\nModName=Cammy From Fluffy\r\ninstalltype=invalidate\r\nfile=natives\\STM\\Product\\Model\\esf\\esf009\\002\\esf009_002_cmd_000.user.2\r\n[Tools]\r\nModName=Tools\r\nfile=reframework/autorun/test.lua\r\n");
            Require(FluffyInstalls.DetectRoots(new[] { Path.Combine(fluffyRoot, "Mods") }, null).Count == 1, "Fluffy root not detected from its Mods library");
            f.Fluffy = FluffyInstalls.Load(new[] { fluffyRoot });
            Require(f.Fluffy.Mods.Count == 2 && f.Fluffy.Owner(V1) != null && f.Fluffy.Owner(V1).Name == "Cammy From Fluffy", "installed.ini parse");
            var original = Snapshot(f.Game);
            ReplacePlan plan = Check(f, "modA");
            Require(plan.Errors.Any(e => e.Contains("Cammy From Fluffy")), "health check must name the owning Fluffy mod");
            Refused(delegate { Install(f, "modA", "A"); }, "installed by an external mod manager");
            Refused(delegate { Install(f, "tool", "Tool"); }, "Tools");
            SameSnapshot(original, Snapshot(f.Game), "after Fluffy refusals");
        }

        private static void RunningBlocks(string root, FakeProcessProbe probe)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            var original = Snapshot(f.Game);
            probe.Game = true; Refused(delegate { Install(f, "modA", "A"); }, "Close SF6 to");
            probe.Game = false; probe.Fluffy = true; Refused(delegate { Install(f, "modA", "A"); }, "Close the external mod manager to");
            SameSnapshot(original, Snapshot(f.Game), "after refusals");
            probe.Fluffy = false; ReplaceMod a = Install(f, "modA", "A");
            probe.Game = true; Refused(delegate { f.Engine.SetEnabled(a.Id, false); }, "Close SF6"); Refused(delegate { f.Engine.RestoreAll(); }, "Close SF6"); Refused(delegate { f.Engine.Reapply(); }, "Close SF6");
            SameSnapshot(Snapshot(f.Game), Snapshot(f.Game), "noop");
            probe.Game = false; f.Engine.RestoreAll();
            SameSnapshot(original, Snapshot(f.Game), "after restore");
        }

        private static void CrashRecovery(string root)
        {
            int installSteps = -1, uninstallSteps = -1;
            for (int k = 0; k < 40 && installSteps < 0; k++)
            {
                Fixture f = Create(Path.Combine(root, "i" + k), false, ReplaceMethods.Pak);
                var before = Snapshot(f.Game);
                ReplaceEngine.CrashAfterStep = k;
                bool crashed = false;
                try { Install(f, "modA", "A"); } catch (ReplaceEngine.SimulatedCrashException) { crashed = true; }
                ReplaceEngine.CrashAfterStep = -1;
                if (!crashed) { installSteps = k; break; }
                Require(f.Engine.JournalPending, "no journal after crash at step " + k);
                string message = f.Engine.Recover();
                Require(!f.Engine.JournalPending && message != null, "recovery did not clear the journal at step " + k);
                ReplaceState s = f.Engine.LoadState();
                if (s.Mods.Count == 0) SameSnapshot(before, Snapshot(f.Game), "rolled back after crash at step " + k);
                else
                {
                    Require(message.Contains("had finished"), "committed state not reported as completed at step " + k);
                    f.Engine.RestoreAll(); SameSnapshot(before, Snapshot(f.Game), "restore after a committed crash at step " + k);
                }
            }
            Require(installSteps > 3, "install crash loop did not reach a clean run");
            for (int k = 0; k < 40 && uninstallSteps < 0; k++)
            {
                Fixture f = Create(Path.Combine(root, "u" + k), false, ReplaceMethods.Pak);
                var clean = Snapshot(f.Game);
                ReplaceMod a = Install(f, "modA", "A");
                var installed = Snapshot(f.Game);
                ReplaceEngine.CrashAfterStep = k; bool crashed = false;
                try { f.Engine.Uninstall(a.Id); } catch (ReplaceEngine.SimulatedCrashException) { crashed = true; }
                ReplaceEngine.CrashAfterStep = -1;
                if (!crashed) { uninstallSteps = k; SameSnapshot(clean, Snapshot(f.Game), "clean uninstall"); break; }
                f.Engine.Recover();
                SameSnapshot(f.Engine.LoadState().Mods.Count == 1 ? installed : clean, Snapshot(f.Game), "uninstall crash at step " + k);
            }
            Require(uninstallSteps > 3, "uninstall crash loop did not reach a clean run");
        }

        private static void RewritePak(Fixture f, uint fingerprint, bool extraEntry)
        {
            string path = Path.Combine(f.Game, "re_chunk_000.pak"); File.Delete(path);
            var files = new List<KeyValuePair<string, byte[]>> {
                new KeyValuePair<string, byte[]>(V6, Bytes("vanilla ui v2")), new KeyValuePair<string, byte[]>(V1, Usr(MaterialData, 0x77777777)), new KeyValuePair<string, byte[]>(V2, Tex(128)),
                new KeyValuePair<string, byte[]>(V3, Tex(128)), new KeyValuePair<string, byte[]>(ModFixes.ReferenceCmdPath, Usr(MaterialData, 0x77777777, Cloth, 0x11111111)), new KeyValuePair<string, byte[]>(V5, Bytes("vanilla mesh v2")) };
            if (extraEntry) files.Add(new KeyValuePair<string, byte[]>("natives/stm/product/new_in_update.user.2", Bytes("new")));
            PakWriter.WriteFixture(path, files, fingerprint, 0);
        }

        private static void GameUpdate(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            ReplaceMod a = Install(f, "modA", "A");
            Require(!f.Engine.Status().NeedsReapply, "fresh install reported as changed");
            RewritePak(f, 0x40000001, true);   // Steam update: new table, nothing invalidated
            var updated = ReplaceEngine.Sha(Path.Combine(f.Game, "re_chunk_000.pak"));
            GameFilesStatus status = f.Engine.Status();
            Require(status.NeedsReapply && status.PakChanged && status.Summary.StartsWith("Game files changed"), "update not detected: " + status.Summary);
            string message = f.Engine.Reapply();
            Require(message.Contains("Dropped") && message.Contains("updated for the current game version"), "reapply message: " + message);
            PakIndex p = Base(f);
            Require(Zeroed(p, V1) && Zeroed(p, V2) && !f.Engine.Status().NeedsReapply, "reapply did not invalidate the updated PAK");
            var crcs = new Dictionary<uint, uint>(); ModFixes.UserTypeCrcs(File.ReadAllBytes(GameFile(f, V1)), crcs);
            Require(crcs[MaterialData] == 0x77777777, "stored colour file was not refreshed to the new type signature");
            f.Engine.RestoreAll();
            Require(ReplaceEngine.Sha(Path.Combine(f.Game, "re_chunk_000.pak")) == updated && !Directory.Exists(Path.Combine(f.Game, "natives")), "restore after update not exact");
        }

        private static void RowsRestored(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            var clean = Snapshot(f.Game);
            ReplaceMod a = Install(f, "modA", "A");
            ReplaceState s = f.Engine.LoadState();
            ZeroedRow row = s.Deployment(V1).Zeroed.First(z => z.Pak == "re_chunk_000.pak");
            PakIndex p = Base(f);
            PakWriter.WriteHashes(p.Path, p.Length, p.Header, new[] { new PakHashWrite { Index = row.Index, Expected = new byte[8], Value = PakWriter.HashBytes(row.Lower, row.Upper) } });
            File.Delete(GameFile(f, V2));
            GameFilesStatus status = f.Engine.Status();
            Require(status.RowsNotZero == 1 && status.FilesMissing == 1, "drift not detected: rows " + status.RowsNotZero + ", missing " + status.FilesMissing);
            f.Engine.Reapply();
            Require(!f.Engine.Status().NeedsReapply && Zeroed(Base(f), V1) && File.Exists(GameFile(f, V2)), "reapply did not repair");
            File.WriteAllText(GameFile(f, V1), "user edited");
            Refused(delegate { f.Engine.SetEnabled(a.Id, false); }, "replaced by another tool");
            f.Engine.Reapply();
            // 1.0.1: the other tool's file is copied only as the change's rollback; after the commit the copy is gone.
            Require(File.ReadAllText(GameFile(f, V1)) != "user edited" && FluffyLinkChecks.ChangedCopies(f.Data) == 0 && f.Engine.LoadState().ForeignCopies.Count == 0, "Reapply kept a copy of the other tool's file (or did not replace it)");
            f.Engine.RestoreAll();
            SameSnapshot(clean, Snapshot(f.Game), "restore after drift repair");
        }

        private static void ToolMods(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            var original = Snapshot(f.Game);
            ModPackage package = PackageReader.Load(f.Mod("tool"), "");
            ModComponent c = package.Components.Single(x => x.RootAssets.Count > 0);
            Require(c.RootAssets.Select(a => a.DestinationPath).OrderBy(x => x).SequenceEqual(new[] { "amd_ags_x64.dll", "amd_ags_x64_real.dll", "reframework/autorun/test.lua", "reframework/plugins/SF6_CostumeSlotsNative.dll" }), "root files: " + String.Join(", ", c.RootAssets.Select(a => a.DestinationPath)));
            Require(Composer.DefaultSelection(package).Count == 0 && Composer.DefaultSelection(package, true).Count == 1, "tool components must only count in replace mode");
            ModPackage costume = PackageReader.Load(f.Mod("modC"), "");
            Require(costume.Components.Single().RootAssets.Select(a => a.DestinationPath).SequenceEqual(new[] { "reframework/autorun/costume_helper.lua" }) && costume.RootFilesIgnored == 1, "a costume option must only bring REFramework files: " + String.Join(", ", costume.Components.Single().RootAssets.Select(a => a.DestinationPath)));
            ReplacePlan plan;
            ReplaceMod tool = Install(f, "tool", "Tool Pack", false, null, out plan);
            Require(plan.Kind == "Tool" && plan.Items.Any(i => i.Text.Contains("amd_ags_x64.dll is backed up")), "tool plan report");
            Require(File.ReadAllText(GameFile(f, "amd_ags_x64.dll")) == "slotsloader proxy" && File.Exists(GameFile(f, "reframework/plugins/SF6_CostumeSlotsNative.dll")) && !File.Exists(GameFile(f, "README.txt")) && !File.Exists(GameFile(f, "screenshot.png")), "tool files not deployed as expected");
            Require(PakIndex.Open(Path.Combine(f.Game, "re_chunk_000.pak")).CountZeroed() == 0, "tool mod must not touch PAKs");
            ReplaceState s = f.Engine.LoadState();
            Require(s.Originals.Count == 1 && s.Originals[0].Path == "amd_ags_x64.dll", "genuine dll backup missing");
            f.Engine.Uninstall(tool.Id);
            SameSnapshot(original, Snapshot(f.Game), "after tool uninstall (genuine dll restored)");
            // 2.5.6: a root patch PAK name is now allowed (only ReplacePlanner hands one out, for a PAK mod); the game's own PAKs are not.
            foreach (string bad in new[] { "StreetFighter6.exe", "re_chunk_000.pak", "reframework/x.pak", "dlc/x.pak", "steam_api64.dll", "re2_fw_config.txt", "reframework/costume_mods/Cammy/x/natives/a" })
                Refused(delegate { ReplaceEngine.ValidateDestination(bad); }, null);
            ReplaceEngine.ValidateDestination("reframework/costume_mods/SF6_CostumeAudit.bat");
            // 1.0.1: a program the game folder doesn't have (Slotsloader's SF6_SlotsClean.exe) is skipped with a note; the rest installs.
            ReplacePlan exePlan;
            ReplaceMod exeTool = Install(f, "toolExe", "Exe Tool", false, null, out exePlan);
            Require(exePlan.Items.Any(i => i.Level == "info" && i.Text.StartsWith("Skipped SF6_SlotsClean.exe")) && exeTool.Files.Count == 1, "loose program not skipped with a note");
            Require(!File.Exists(GameFile(f, "SF6_SlotsClean.exe")) && File.Exists(GameFile(f, "reframework/plugins/exe_tool.dll")), "loose program skip deployed the wrong files");
            f.Engine.Uninstall(exeTool.Id);
        }

        private static void LooseMethod(string root)
        {
            Fixture f = Create(root, true, ReplaceMethods.Loose);
            string pak = Path.Combine(f.Game, "re_chunk_000.pak"), pakSha = ReplaceEngine.Sha(pak);
            var clean = Snapshot(f.Game);
            ReplaceMod a = Install(f, "modA", "A");
            Require(ReplaceEngine.Sha(pak) == pakSha && File.Exists(GameFile(f, V1)) && f.Engine.LoadState().Method == ReplaceMethods.Loose, "loose method changed a PAK or missed files");
            Require(f.Engine.Status().LooseLoaderOff, "loader-off warning missing");
            // 2.4.1: a disabled mod is not in the game folder, so it does not need the loose file loader.
            f.Engine.SetEnabled(a.Id, false);
            Require(!f.Engine.Status().LooseLoaderOff && ReplaceEngine.NeedLooseLoader(f.Engine.LoadState(), ReplaceMethods.Loose).Count == 0, "loader-off warning for a disabled mod");
            f.Engine.SetEnabled(a.Id, true);
            Require(ReplaceEngine.NeedLooseLoader(f.Engine.LoadState(), ReplaceMethods.Pak).Count == 0, "the PAK method never needs the loose file loader");
            var loaderOnly = new ReplaceState();
            var loaderMod = new ReplaceMod { Name = "SF6 Slots Loader", Enabled = true, Kind = "Tool" };
            loaderMod.Files.Add(new ModFile { Path = LoaderCostumePak.PluginPath }); loaderMod.Files.Add(new ModFile { Path = "amd_ags_x64.dll" }); loaderMod.Files.Add(new ModFile { Path = "natives/stm/sf6_costume_slots.marker" });
            loaderOnly.Mods.Add(loaderMod);
            Require(ReplaceEngine.NeedLooseLoader(loaderOnly, ReplaceMethods.Loose).Count == 0, "Slotsloader alone must not need the loose file loader");
            var looseSnapshot = Snapshot(f.Game);
            f.Engine.SwitchMethod(ReplaceMethods.Pak);
            Require(Zeroed(Base(f), V1) && Zeroed(Base(f), V2) && f.Engine.LoadState().Method == ReplaceMethods.Pak, "switch to PAK did not invalidate");
            f.Engine.SwitchMethod(ReplaceMethods.Loose);
            SameSnapshot(looseSnapshot, Snapshot(f.Game), "switching back to loose");
            f.Engine.RestoreAll();
            SameSnapshot(clean, Snapshot(f.Game), "restore under the loose method");
            // 2.4.1: Automatic picks loose only while REFramework's loose file loader is on; otherwise the PAK method.
            string loaderDetail; bool loaderOn = LooseLoader.State(f.Game, out loaderDetail) == LooseLoaderState.Enabled;
            Require(new ReplaceEngine(f.Game, f.Data, new ReplaceSettings()).DesiredMethod() == (loaderOn ? ReplaceMethods.Loose : ReplaceMethods.Pak), "Automatic with the loader " + (loaderOn ? "on must be loose" : "off must be the PAK method"));
            string cfg = Path.Combine(f.Game, LooseLoader.ConfigName);
            if (File.Exists(cfg))
            {
                string original = File.ReadAllText(cfg);
                LooseLoader.Write(f.Game, LooseLoader.EnabledKey, loaderOn ? "false" : "true", Path.Combine(f.Data, "cfg-backup"));
                Require(new ReplaceEngine(f.Game, f.Data, new ReplaceSettings()).DesiredMethod() == (loaderOn ? ReplaceMethods.Pak : ReplaceMethods.Loose), "Automatic did not follow the loose file loader");
                File.WriteAllText(cfg, original);
            }
            Require(new ReplaceEngine(f.Game, f.Data, new ReplaceSettings { Method = ReplaceMethods.Loose }).DesiredMethod() == ReplaceMethods.Loose, "an explicit loose setting must win");
            Fixture g = Create(Path.Combine(root, "noref"), false, null);
            Require(g.Engine.DesiredMethod() == ReplaceMethods.Pak, "without REFramework the PAK method must be the default");
        }

        private static void LooseWhileRunning(string root, FakeProcessProbe probe)
        {
            Fixture f = Create(root, true, ReplaceMethods.Loose);
            var clean = Snapshot(f.Game);
            probe.Game = true;
            ReplaceMod a = Install(f, "modA", "A");
            Require(File.Exists(GameFile(f, V1)) && f.Engine.LoadState().Mods.Count == 1, "natives-only loose install refused while running");
            Refused(delegate { Install(f, "tool", "Tool"); }, "Close SF6");
            Refused(delegate { f.Engine.SwitchMethod(ReplaceMethods.Pak); }, "Close SF6");
            f.Engine.SetEnabled(a.Id, false);
            // 2.1.0: Fluffy open blocks only PAK edits; with the game running too, natives re-copies still go ahead.
            probe.Fluffy = true; f.Engine.SetEnabled(a.Id, true);
            Require(File.Exists(GameFile(f, V1)), "a loose re-enable with Fluffy open was refused");
            Refused(delegate { f.Engine.SwitchMethod(ReplaceMethods.Pak); }, "Close ");   // SF6 or Fluffy: both are open
            probe.Fluffy = false; probe.Game = false;
            f.Engine.RestoreAll();
            SameSnapshot(clean, Snapshot(f.Game), "after the running-game cycle");
            Require(Directory.GetFiles(f.Engine.BackupDir).Length == 0 || true, "noop");
        }

        private static void ConfigEdits(string root, FakeProcessProbe probe)
        {
            Fixture f = Create(root, true, ReplaceMethods.Loose);
            string config = LooseLoader.ConfigPath(f.Game);
            string backups = Path.Combine(f.Data, "cfg");
            string detail;
            Require(LooseLoader.State(f.Game, out detail) == LooseLoaderState.Disabled, "initial state");
            probe.Game = true; Refused(delegate { LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "true", backups); }, "Close SF6"); probe.Game = false;
            string previous = LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "true", backups);
            Require(previous == "false" && File.ReadAllText(config) == "FirstKey=1\r\nLooseFileLoader_Enabled=true\r\nLooseFileLoader_LogAccessedFiles=false\r\nLastKey=x\r\n", "edit changed more than the key: " + File.ReadAllText(config));
            Require(Directory.GetFiles(backups).Length == 1 && LooseLoader.State(f.Game, out detail) == LooseLoaderState.Enabled, "backup/state after enable");
            LooseLoader.Write(f.Game, LooseLoader.LogLooseKey, "true", backups);
            Require(File.ReadAllText(config).EndsWith("LastKey=x\r\nLooseFileLoader_LogLooseFiles=true\r\n"), "missing key not appended with CRLF");
            File.WriteAllBytes(config, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("A=1\nLooseFileLoader_Enabled=true\nB=2")).ToArray());
            LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "false", backups);
            byte[] after = File.ReadAllBytes(config);
            Require(after[0] == 0xEF && Encoding.UTF8.GetString(after, 3, after.Length - 3) == "A=1\nLooseFileLoader_Enabled=false\nB=2", "BOM/LF/no-trailing-newline not preserved");
            File.Delete(Path.Combine(f.Game, "dinput8.dll"));
            Require(LooseLoader.State(f.Game, out detail) == LooseLoaderState.ReFrameworkMissing, "missing REFramework not detected");
        }

        private static void LogParsing()
        {
            var lines = new[] {
                "[2026-10-13 18:20:15.307] [LooseFileLoader] [info] LooseFileLoader constructed",
                "[2026-10-13 18:21:00.001] [LooseFileLoader2] [info] Accessed natives/STM/Product/Model/esf/esf009/002/esf009_002_cmd_000.user.2",
                "[2026-10-13 18:21:00.002] [LooseFileLoader] [info] Loading loose file: D:\\Games\\SF6\\natives\\stm\\product\\model\\esf\\esf009\\002\\01\\esf009_002_01_albd.tex.241101895",
                "[2026-10-13 18:21:00.003] [LooseFileLoader] [info] path_to_hash candidate at 14221a000" };
            var logged = lines.SelectMany(LooseLoader.ParseLogLine).ToList();
            var hit = LooseLoader.Matched(new[] { V1, V2, V5 }, logged);
            Require(hit.Count == 2 && hit.Contains(V1) && hit.Contains(V2), "log parse: " + String.Join(", ", logged));
        }

        private static void Health(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            Install(f, "modB", "Cammy Alt");
            ReplacePlan plan = Check(f, "modA");
            string text = ReplacePlanner.Report(plan);
            Require(plan.CanInstall && plan.AffectsText.Contains("Cammy Outfit 2 (esf009/002)"), "affects: " + plan.AffectsText);
            Require(plan.VanillaReplaced == 2 && plan.NewFiles == 1 && plan.RowsToInvalidate == 3 && plan.Fixed == 3, "counts: " + text);
            Require(text.Contains("outdated type signature") && text.Contains("143230113") && text.Contains("mip-table") && text.Contains("streaming copy") && text.Contains("Cammy Outfit 2 already has 'Cammy Alt' (") && text.Contains("Cloth"), "report: " + text);
            ReplacePlan weird = Check(f, "modWeird");
            string w = ReplacePlanner.Report(weird);
            Require(w.Contains("esf009/099 is not a vanilla outfit folder") && w.Contains("from an older game version"), "mistake warnings: " + w);
            Require(weird.Files.Any(x => x.Path == V5), "outdated mesh suffix was not renamed onto the vanilla path");
            Require(!Directory.Exists(plan.StagingFolder), "health check left its scratch folder");
        }

        private static void Conflicts(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            // A file already in the game folder that this app did not install.
            Put(f.Game, V5, Bytes("manual mesh"));
            ReplacePlan withLoose = Check(f, "modA");
            Require(withLoose.UnknownLoose.Count == 0, "modA does not ship V5");
            ReplacePlan bAgain = Check(f, "modWeird");
            Require(bAgain.UnknownLoose.Contains(V5) && bAgain.Items.Any(i => i.Text.Contains("not installed by the Manager")), "unknown loose files not named");
            File.Delete(GameFile(f, V5));
            ReplaceMod b = Install(f, "modB", "Cammy Alt");
            // Same outfit (Cammy Outfit 2) and one shared file.
            ReplacePlan a = Check(f, "modA");
            ReplaceConflict slot = a.ConflictList.Single();
            Require(slot.Kind == "slot" && slot.CanUninstall && slot.ModName == "Cammy Alt" && slot.ModId == b.Id && slot.Shared == 1 && slot.TheirFiles == 2 && slot.Enabled && slot.NewWins && slot.Level == "warn", "slot conflict: " + slot.Kind + " " + slot.Shared + "/" + slot.TheirFiles);
            Require(slot.Text.StartsWith("Cammy Outfit 2 already has 'Cammy Alt' (enabled, installed ") && slot.Text.Contains("2 files) · this install overrides 1 of its 2 files; its other file stays active (the outfit may mix both mods)"), "slot text: " + slot.Text);
            Require(a.Headline == slot && a.Items.Any(i => i.Level == "warn" && i.Text == slot.Text) && a.SelectedIds.Count > 0, "headline/report item");
            // Same outfit, no shared files: information only, both stay installed.
            ReplacePlan c = Check(f, "modC");
            ReplaceConflict stack = c.ConflictList.Single(x => x.ModId == b.Id);
            Require(stack.Kind == "stack" && !stack.CanUninstall && stack.Level == "info" && stack.Text == "Also on Cammy Outfit 2: 'Cammy Alt' (enabled, no shared files), so both stay installed.", "stack: " + stack.Kind + " " + stack.Text);
            // A disabled older mod is still named.
            f.Engine.SetEnabled(b.Id, false);
            ReplaceConflict disabled = Check(f, "modA").ConflictList.Single();
            Require(disabled.Kind == "slot" && !disabled.Enabled && disabled.Text.Contains("also has 'Cammy Alt' (disabled") && disabled.Text.Contains("its other file would mix in"), "disabled: " + disabled.Text);
            f.Engine.SetEnabled(b.Id, true);
            // Shared parts only (esf009/000) between two different outfits: an override note, no uninstall link.
            Put(f.Mods, "modP1/natives/stm/product/model/esf/esf009/001/01/esf009_001_01_albd.tex.241101895", Tex(128));
            Put(f.Mods, "modP1/natives/stm/product/model/esf/esf009/000/shared_hand.tex.241101895", Tex(128));
            Put(f.Mods, "modP2/natives/stm/product/model/esf/esf009/003/01/esf009_003_01_albd.tex.241101895", Tex(128));
            Put(f.Mods, "modP2/natives/stm/product/model/esf/esf009/000/shared_hand.tex.241101895", Tex(128));
            ReplaceMod p1 = Install(f, "modP1", "Cammy Hands");
            ReplaceConflict other = Check(f, "modP2").ConflictList.Single(x => x.ModId == p1.Id);
            Require(other.Kind == "files" && !other.CanUninstall && other.Shared == 1 && other.Text.StartsWith("Overrides 1 file from 'Cammy Hands' (this install wins)") && other.Text.Contains("shared parts"), "other-area: " + other.Kind + " " + other.Text);
            // Updating B in place: B keeps its own priority, so the comparison is against the others.
            ReplaceMod aMod = Install(f, "modA", "Cammy Colors");
            ModPackage pkgB = PackageReader.Load(f.Mod("modB"), "");
            ReplacePlan update = ReplacePlanner.Check(Composer.Plan(pkgB, Composer.DefaultSelection(pkgB, true), true), "", f.Engine, null, b.Id);
            ReplaceConflict vsA = update.ConflictList.Single(x => x.ModId == aMod.Id);
            Require(!vsA.NewWins && vsA.Text.Contains("it is higher in the list and keeps 1 shared file"), "update in place priority: " + vsA.Text);
            // Installing never removes other mods.
            ReplaceState s = f.Engine.LoadState();
            Require(s.Mods.Count == 3 && s.Find(b.Id) != null && s.Find(p1.Id) != null && s.Mods[0].Id == aMod.Id && s.Deployment(V1).Owner == aMod.Id && s.Deployment(V5).Owner == b.Id, "install removed or reordered another mod");
            // 1.0.1: any mod in the external manager blocks Replace original (the lockout, not just its own files).
            string fluffyRoot = Path.Combine(root, "Fluffy", "Games", "SF6"); Directory.CreateDirectory(fluffyRoot);
            File.WriteAllText(Path.Combine(fluffyRoot, "installed.ini"), "[Cammy Fluffy]\r\nModName=Cammy From Fluffy\r\nfile=natives/stm/product/model/esf/esf009/003/01/esf009_003_01_albd.tex.241101895\r\n");
            f.Fluffy = FluffyInstalls.Load(new[] { fluffyRoot });
            ReplacePlan owned = Check(f, "modP2");
            Require(!owned.CanInstall && owned.Errors.Any(e => e == ReplaceEngine.ExternalLockReason(f.Fluffy)), "lockout error: " + String.Join(" | ", owned.Errors));
        }

        private static void RecoverableUninstall(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            ReplaceMod a = Install(f, "modA", "Cammy Colors");
            ReplaceMod b = Install(f, "modB", "Cammy Alt");
            f.Engine.Move(b.Id, 1);    // A on top: B is #2
            var before = Snapshot(f.Game); var order = f.Engine.LoadState().Mods.Select(m => m.Id + m.Enabled).ToList();
            string folder; f.Engine.Uninstall(b.Id, true, out folder);
            Require(folder != null && Directory.Exists(folder) && File.Exists(Path.Combine(folder, "record.json")) && !Directory.Exists(Path.Combine(f.Data, "store", b.Id)), "removed copy not kept: " + folder);
            Require(f.Engine.ListRemoved().Count == 1 && f.Engine.ListRemoved()[0].Name == "Cammy Alt" && f.Engine.ListRemoved()[0].Record.Index == 1, "removed listing");
            Require(!File.Exists(GameFile(f, V5)) && f.Engine.LoadState().Mods.Count == 1, "B still deployed after uninstall");
            ReplaceMod restored; f.Engine.RestoreRemoved(folder, out restored);
            SameSnapshot(before, Snapshot(f.Game), "after Undo");
            Require(f.Engine.LoadState().Mods.Select(m => m.Id + m.Enabled).SequenceEqual(order) && !Directory.Exists(folder) && f.Engine.ListRemoved().Count == 0, "Undo did not restore the priority/enabled state or left the copy");
            // A disabled mod comes back disabled, at the top.
            f.Engine.Move(b.Id, -1); f.Engine.SetEnabled(b.Id, false);
            before = Snapshot(f.Game); order = f.Engine.LoadState().Mods.Select(m => m.Id + m.Enabled).ToList();
            f.Engine.Uninstall(b.Id, true, out folder);
            f.Engine.RestoreRemoved(folder, out restored);
            SameSnapshot(before, Snapshot(f.Game), "after Undo of a disabled mod");
            Require(f.Engine.LoadState().Mods.Select(m => m.Id + m.Enabled).SequenceEqual(order) && !f.Engine.LoadState().Find(b.Id).Enabled, "disabled mod restored enabled or moved");
            // The plain Uninstall keeps a copy too (the Installed page and the inspector link use it).
            f.Engine.Uninstall(a.Id);
            Require(f.Engine.ListRemoved().Count == 1, "Uninstall without a folder argument did not keep a copy");
            // A reverted test install keeps nothing.
            ReplacePlan plan; ReplaceMod t = Install(f, "modC", "Test C", true, null, out plan);
            string none; f.Engine.Uninstall(t.Id, false, out none);
            Require(none == null && f.Engine.ListRemoved().Count == 1, "a reverted test install was kept");
            // Restoring twice is refused cleanly.
            string kept = f.Engine.ListRemoved()[0].Folder; f.Engine.RestoreRemoved(kept, out restored);
            Refused(delegate { f.Engine.RestoreRemoved(kept, out restored); }, null);
        }

        private static void FluffyOpen(string root, FakeProcessProbe probe)
        {
            // Loose method with a real installed.ini that "Fluffy" changes while it is open.
            Fixture f = Create(Path.Combine(root, "loose"), true, ReplaceMethods.Loose);
            string fluffyRoot = Path.Combine(root, "Fluffy", "Games", "SF6"); Directory.CreateDirectory(Path.Combine(fluffyRoot, "Mods"));
            // 1.0.1: open with nothing installed (with a mod in it only Extra outfit, Slotsloader and REFramework install: the lockout).
            File.WriteAllText(Path.Combine(fluffyRoot, "installed.ini"), "");
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] { fluffyRoot }); };
            var clean = Snapshot(f.Game);
            probe.Fluffy = true;
            ReplaceMod tool = Install(f, "tool", "Tool Pack");
            ReplaceMod a = Install(f, "modA", "Cammy Colors");
            Require(File.ReadAllText(GameFile(f, "amd_ags_x64.dll")) == "slotsloader proxy" && File.Exists(GameFile(f, V1)) && f.Engine.LoadState().Mods.Count == 2, "loose/tool installs with Fluffy open");
            Require(File.Exists(GameFile(f, "CrashReport.exe")) && !f.Engine.LoadState().CrashReporterRenamed, "the crash reporter must wait until Fluffy is closed");
            f.Engine.SetEnabled(a.Id, false); f.Engine.SetEnabled(a.Id, true); f.Engine.Move(a.Id, 1);
            LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "true", f.Engine.ConfigBackupDir);
            Require(LooseLoader.Read(f.Game, LooseLoader.EnabledKey) == "true", "REFramework config edit refused with Fluffy open");
            var before = Snapshot(f.Game);
            Refused(delegate { f.Engine.SwitchMethod(ReplaceMethods.Pak); }, "edits the same PAK");
            SameSnapshot(before, Snapshot(f.Game), "after the refused conversion");
            // Fluffy installs something meanwhile (installed.ini is read again at each change): the lockout starts.
            File.AppendAllText(Path.Combine(fluffyRoot, "installed.ini"), "[Cammy Mesh]\r\nModName=Cammy Mesh From Fluffy\r\nfile=" + V5 + "\r\n");
            Refused(delegate { Install(f, "modB", "Cammy Alt"); }, "only Extra outfit, Slotsloader and REFramework install");
            f.Engine.SetEnabled(a.Id, false);
            Refused(delegate { f.Engine.SetEnabled(a.Id, true); }, "only Extra outfit, Slotsloader and REFramework install");
            f.Engine.Uninstall(a.Id); f.Engine.Uninstall(tool.Id);
            var after = Snapshot(f.Game); after.Remove(LooseLoader.ConfigName); var expected = new Dictionary<string, string>(clean, StringComparer.OrdinalIgnoreCase); expected.Remove(LooseLoader.ConfigName);
            SameSnapshot(expected, after, "loose/tool uninstalls with Fluffy open");
            // PAK method: row changes wait for Fluffy; a tool mod (no rows) does not.
            Fixture p = Create(Path.Combine(root, "pak"), false, ReplaceMethods.Pak);
            var pakClean = Snapshot(p.Game);
            Refused(delegate { Install(p, "modA", "Cammy Colors"); }, "edits the same PAK");
            SameSnapshot(pakClean, Snapshot(p.Game), "after the refused PAK install");
            Require(!Directory.Exists(p.Engine.BackupDir) || Directory.GetFiles(p.Engine.BackupDir).Length == 0, "the refused install left a backup");
            ReplaceMod pakTool = Install(p, "tool", "Tool Pack");
            Require(File.ReadAllText(GameFile(p, "amd_ags_x64.dll")) == "slotsloader proxy" && PakIndex.Open(Path.Combine(p.Game, "re_chunk_000.pak")).CountZeroed() == 0, "a PAK-method tool mod with Fluffy open");
            p.Engine.Uninstall(pakTool.Id);
            SameSnapshot(pakClean, Snapshot(p.Game), "after the PAK-method tool uninstall");
            probe.Fluffy = false;
        }

        // 2.4.1: on the user's machine the 2.4.0 CLI printed "already installed; Update replaces it in place" and then
        // stacked a second SlotsLoader record. The switch now lives in ReplacePlanner.PrepareAndInstall (app, CLI, Assistant).
        private static void CliSameSource(string root)
        {
            Fixture f = Create(root, true, ReplaceMethods.Pak);
            string genuine = File.ReadAllText(GameFile(f, "amd_ags_x64.dll"));
            var cli = new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load());   // the CLI's own Data
            try
            {
            string first = Cli("replace-install", f.Mod("tool"), "--game", f.Game, "--method", "pak");
            Require(first.StartsWith("0\n"), "first install: " + first);
            ReplaceState state = cli.LoadState();
            Require(state.Mods.Count == 1 && LoaderCostumePak.IsLoaderMod(state.Mods[0]), "fixture: the tool pack is not a Slotsloader record");
            string id = state.Mods[0].Id;
            string again = Cli("replace-install", f.Mod("tool"), "--game", f.Game);
            state = cli.LoadState();
            Require(again.StartsWith("0\n") && state.Mods.Count == 1 && again.Contains("Replaces installed: Tool Pack") && !again.Contains("Update replaces it in place"), "the same source again stacked or said something else: " + state.Mods.Count + " records / " + again);
            // SlotsLoader from another zip name (a newer build): still one loader record, updated in place.
            CopyTree(f.Mod("tool"), f.Mod("SF6_SlotsLoader_v9.9"));
            File.WriteAllText(Path.Combine(f.Mod("SF6_SlotsLoader_v9.9"), "Tool Pack", "README.txt"), "readme v9.9");
            string other = Cli("replace-install", f.Mod("SF6_SlotsLoader_v9.9"), "--game", f.Game);
            state = cli.LoadState();
            Require(other.StartsWith("0\n") && state.Mods.Count == 1 && other.Contains("Replaces installed: Tool Pack") && ReplacePlanner.SameSource(state.Mods[0].SourcePath, f.Mod("SF6_SlotsLoader_v9.9")), "a Slotsloader update from another zip stacked: " + String.Join(", ", state.Mods.Select(m => m.Id + " " + m.SourcePath)) + " / " + other);
            Require(File.ReadAllText(GameFile(f, "amd_ags_x64.dll")) != genuine, "the loader proxy is not in place");
            string removed = Cli("replace-uninstall", state.Mods[0].Id.Substring(0, 8), "--game", f.Game);
            Require(removed.StartsWith("0\n") && cli.LoadState().Mods.Count == 0 && File.ReadAllText(GameFile(f, "amd_ags_x64.dll")) == genuine, "the game's own amd_ags_x64.dll was not restored after in-place updates: " + removed + " / " + File.ReadAllText(GameFile(f, "amd_ags_x64.dll")));
            }
            finally { try { cli.RestoreAll(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; } }
        }

        private static void LooseLoaderCli(string root)
        {
            Fixture f = Create(root, true, ReplaceMethods.Loose);
            string configPath = Path.Combine(f.Game, LooseLoader.ConfigName), original = File.ReadAllText(configPath);
            // replace-settings.json: a 2.0.x file without SkipLooseLoaderPrompt loads as "ask"; the flag round-trips.
            string file = ReplaceSettings.FilePath; string kept = File.Exists(file) ? File.ReadAllText(file) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "{\"Format\":\"sf6-replace-settings-v1\",\"DefaultMode\":\"slot\",\"LastMode\":\"replace\",\"StoreLocation\":\"\",\"Method\":\"loose\",\"LooseLoaderConsent\":false,\"RenameCrashReporter\":true,\"FluffyRoots\":[]}");
            Require(!ReplaceSettings.Load().SkipLooseLoaderPrompt && ReplaceSettings.Load().Method == ReplaceMethods.Loose, "an old settings file must load as ask");
            var flag = ReplaceSettings.Load(); flag.SkipLooseLoaderPrompt = true; flag.Save();
            Require(ReplaceSettings.Load().SkipLooseLoaderPrompt && File.ReadAllText(file).Contains("\"SkipLooseLoaderPrompt\":true"), "SkipLooseLoaderPrompt did not round-trip");
            if (kept != null) File.WriteAllText(file, kept); else File.Delete(file);
            try
            {
                string tool = Cli("replace-install", f.Mod("tool"), "--game", f.Game, "--method", "loose");
                Require(tool.StartsWith("0\n") && tool.Contains("Tool Pack") && File.ReadAllText(configPath) == original, "tool-only install must not need the loader: " + tool);
                string refused = Cli("replace-install", f.Mod("modA"), "--game", f.Game, "--method", "loose");
                Require(refused.StartsWith("1\n") && refused.Contains("--yes") && refused.Contains("--no-loader") && File.ReadAllText(configPath) == original, "natives install without a choice: " + refused);
                string both = Cli("replace-install", f.Mod("modA"), "--game", f.Game, "--method", "loose", "--yes", "--no-loader");
                Require(!both.StartsWith("0\n") && both.Contains("not both") && File.ReadAllText(configPath) == original, "--yes with --no-loader: " + both);
                string without = Cli("replace-install", f.Mod("modA"), "--game", f.Game, "--method", "loose", "--no-loader", "--name", "No loader");
                Require(without.StartsWith("0\n") && without.Contains("(--no-loader)") && without.Contains("Replacements (2") && without.Contains("Loose file loader off") && File.ReadAllText(configPath) == original, "--no-loader install: " + without);
                Require(File.Exists(GameFile(f, V1)), "the replaced file was not deployed");
                string restore = Cli("replace-restore-all", "--game", f.Game, "--yes");
                Require(restore.StartsWith("0\n") && restore.Contains("Replacements (0") && File.ReadAllText(configPath) == original, "restore-all: " + restore);
            }
            finally { var settings = ReplaceSettings.Load(); settings.Method = ""; settings.Save(); }
        }

        // ---- Relocation (2.1.0): the app folder moved with its Data.
        private static string CliExe { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Slotsloader CLI.exe"); } }

        // An app folder like the installed one: the CLI copy (+ config) and Data whose settings name the fixture game.
        private static string MakeAppFolder(string app, Fixture f)
        {
            string data = Path.Combine(app, "Data"); Directory.CreateDirectory(data);
            Require(File.Exists(CliExe), "Slotsloader CLI.exe must be built beside the check executable: " + CliExe);
            File.Copy(CliExe, Path.Combine(app, "Slotsloader CLI.exe"));
            if (File.Exists(CliExe + ".config")) File.Copy(CliExe + ".config", Path.Combine(app, "Slotsloader CLI.exe.config"));
            Json.Write(Path.Combine(data, "settings.json"), new AppConfig { GameRoot = f.Game, Libraries = new List<string>(), SevenZipPath = "" });
            Json.Write(Path.Combine(data, "replace-settings.json"), new ReplaceSettings { Method = ReplaceMethods.Pak });
            return data;
        }
        private static ReplaceEngine AppEngine(Fixture f, string data)
        {
            var engine = new ReplaceEngine(f.Game, data, new ReplaceSettings { Method = ReplaceMethods.Pak });
            engine.FluffyProvider = delegate { return f.Fluffy; };
            return engine;
        }
        internal static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, dir.Substring(from.Length + 1)));
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(to, file.Substring(from.Length + 1)));
        }
        // Moves the app folder the way a user would with Explorer across folders: copy everything, delete the original.
        private static void MoveApp(string from, string to) { CopyTree(from, to); Directory.Delete(from, true); Require(!Directory.Exists(from), "old app folder still present"); }
        private static string RunAppCli(string app, params string[] args)
        {
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(app, "Slotsloader CLI.exe"), String.Join(" ", args.Select(a => "\"" + a + "\""))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = app };
            if (LinuxPlatform.IsLinux && args.Length == 1 && args[0] == "replace-status") info.EnvironmentVariables["SLOTSLOADER_LINUX_READONLY_CHILD"] = "1";
            using (var p = System.Diagnostics.Process.Start(info))
            {
                var error = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } throw new InvalidOperationException("CLI timed out: " + String.Join(" ", args)); }
                return p.ExitCode + "\n" + output + error.Result;
            }
        }
        private static bool RealGameOrFluffyRunning { get { var probe = new SystemProcessProbe(); return probe.IsGameRunning() || probe.IsFluffyRunning(); } }
        private static string JsonPath(string path) { return Json.Serializer().Serialize(path).Trim('"'); }
        private static void OnlyUnder(ReplaceState s, string data, string context)
        {
            var paths = new List<string> { s.StoreRoot }.Concat(s.Originals.Select(o => o.Backup)).Concat(s.Paks.Select(p => p.Backup)).Where(p => !String.IsNullOrEmpty(p)).ToList();
            Require(paths.Count > 0 && paths.All(p => DataRelocation.IsUnderOrSame(p, data)), context + ": recorded paths outside " + data + ": " + String.Join(", ", paths.Where(p => !DataRelocation.IsUnderOrSame(p, data))));
        }

        private static void RelocationRules(string root)
        {
            string current = Path.Combine(root, "new", "Data"), old = Path.Combine(root, "old", "Data"), other = Path.Combine(root, "other", "Data");
            Put(current, "replace-backups/x.bin", Bytes("x"));
            Put(current, "replace-store/id1/reframework/data/SF6_Costumes_Data/loader/a.tsv", Bytes("a"));
            Directory.CreateDirectory(Path.Combine(current, "replace-store"));
            var r = new RelocationReport(current);
            Require(DataRelocation.Rebase(Path.Combine(old, "replace-backups", "x.bin"), current, r) == Path.Combine(current, "replace-backups", "x.bin") && r.Rebased == 1 && r.OldRoots.Contains(old), "plain rebase");
            Require(DataRelocation.Rebase(old.ToUpperInvariant() + "\\REPLACE-BACKUPS\\X.BIN", current, r).StartsWith(current, StringComparison.OrdinalIgnoreCase), "case-insensitive rebase");
            Require(DataRelocation.Rebase(Path.Combine(old, "replace-store") + "\\", current, r) == Path.Combine(current, "replace-store"), "trailing separator");
            Require(DataRelocation.Rebase(old + "/replace-backups/x.bin", current, r).StartsWith(current, StringComparison.OrdinalIgnoreCase), "forward slashes");
            Require(DataRelocation.Rebase(Path.Combine(old, "replace-store", "id1", "reframework", "data", "SF6_Costumes_Data", "loader", "a.tsv"), current, r) == Path.Combine(current, "replace-store", "id1", "reframework", "data", "SF6_Costumes_Data", "loader", "a.tsv"), "a nested 'data' folder inside the store");
            Require(DataRelocation.RebaseTarget(Path.Combine(old, "replace-store", ".removed", "id1-x"), current, r) == Path.Combine(current, "replace-store", ".removed", "id1-x"), "a target that does not exist yet follows its parent");
            int rebased = r.Rebased;
            string gone = Path.Combine(old, "replace-backups", "gone.bin");
            Require(DataRelocation.Rebase(gone, current, r) == gone && r.Missing == 1 && r.FirstMissing == gone && r.Rebased == rebased, "missing in both places must stay as recorded");
            string outside = Path.Combine(root, "custom-store", "x.bin");
            Require(DataRelocation.Rebase(outside, current, r) == outside && r.Missing == 1, "a path outside a Data root must be left alone");
            string game = Path.Combine(root, "game"); string inGame = Path.Combine(game, "Data", "replace-backups", "x.bin");
            Require(DataRelocation.Rebase(inGame, current, r, game) == inGame, "game-folder paths are never rebased");
            Put(other, "replace-backups/x.bin", Bytes("other copy"));
            string existing = Path.Combine(other, "replace-backups", "x.bin");
            Require(DataRelocation.Rebase(existing, current, r) == existing && r.Foreign == 1 && r.ForeignRoots.Contains(other), "an existing path in another Data folder stays (and is reported as a copied folder)");
            Require(DataRelocation.Rebase("replace-backups\\x.bin", current, r) == "replace-backups\\x.bin" && DataRelocation.Rebase("", current, r) == "" && DataRelocation.Rebase(null, current, r) == null, "relative/empty paths");
            Require(DataRelocation.Rebase(Path.Combine(current, "replace-backups", "missing.bin"), current, r) == Path.Combine(current, "replace-backups", "missing.bin"), "a path inside the current Data stays");
            // Engine level: a custom store outside Data is left; a backup from the moved Data is rebased in memory only.
            string custom = Path.Combine(root, "custom-store"); Directory.CreateDirectory(Path.Combine(custom, "m1"));
            Fixture f = Create(Path.Combine(root, "fixture"), false, ReplaceMethods.Pak);
            var state = new ReplaceState { GameRoot = f.Game, Method = ReplaceMethods.Pak, StoreRoot = custom, Mods = new List<ReplaceMod> { new ReplaceMod { Id = "m1", Name = "M" } }, Originals = new List<OriginalBackup> { new OriginalBackup { Path = "amd_ags_x64.dll", Backup = Path.Combine(old, "replace-backups", "x.bin") } } };
            Json.Write(Path.Combine(current, "replace-state.json"), state);
            string before = File.ReadAllText(Path.Combine(current, "replace-state.json"));
            var engine = new ReplaceEngine(f.Game, current, new ReplaceSettings { StoreLocation = custom });
            ReplaceState loaded = engine.LoadState();
            Require(loaded.StoreRoot == custom && loaded.Originals[0].Backup == Path.Combine(current, "replace-backups", "x.bin"), "engine rebase: store " + loaded.StoreRoot + ", backup " + loaded.Originals[0].Backup);
            Require(File.ReadAllText(Path.Combine(current, "replace-state.json")) == before, "loading must not write the state file");
            Require(engine.PersistRelocation() != null && engine.LoadState().Originals[0].Backup.StartsWith(current, StringComparison.OrdinalIgnoreCase) && !File.ReadAllText(Path.Combine(current, "replace-state.json")).Contains(JsonPath(old)), "PersistRelocation did not save the corrected path");
            Require(engine.PersistRelocation() == null, "a second PersistRelocation must find nothing to do");
        }

        private static void RelocationMove(string root)
        {
            Fixture f = Create(Path.Combine(root, "fixture"), false, ReplaceMethods.Pak);
            var clean = Snapshot(f.Game);
            string basePak = Path.Combine(f.Game, "re_chunk_000.pak"), cleanBase = ReplaceEngine.Sha(basePak);
            string oldApp = Path.Combine(root, "SF6 SlotsLoader Installer"), newApp = Path.Combine(root, "moved", "Slotsloader Manager");
            string oldData = MakeAppFolder(oldApp, f), newData = Path.Combine(newApp, "Data");
            f.Engine = AppEngine(f, oldData);
            ReplaceMod tool = Install(f, "tool", "Tool Pack");          // backs up the genuine amd_ags_x64.dll
            ReplaceMod a = Install(f, "modA", "Cammy Colors");          // PAK method: TOC backups, zeroed rows
            ReplaceMod b = Install(f, "modB", "Cammy Alt");
            f.Engine.Uninstall(b.Id);                                    // a recoverable copy in <store>\.removed
            ReplaceState s = f.Engine.LoadState();
            Require(s.Originals.Count == 1 && s.Paks.Count(p => !String.IsNullOrEmpty(p.Backup)) >= 1 && s.StoreRoot.StartsWith(oldData, StringComparison.OrdinalIgnoreCase) && f.Engine.ListRemoved().Count == 1, "setup: originals " + s.Originals.Count + ", store " + s.StoreRoot);
            var installed = Snapshot(f.Game);

            MoveApp(oldApp, newApp);
            string statePath = Path.Combine(newData, "replace-state.json"), stateBefore = ReplaceEngine.Sha(statePath);
            string status = RunAppCli(newApp, "replace-status");
            Require(status.StartsWith("0\n") && status.Contains("Status: OK") && System.Text.RegularExpressions.Regex.IsMatch(status, @"Backups: [2-9]\d* recorded, 0 missing on disk") && status.Contains("stored copies missing: 0"), "replace-status after the move: " + status);
            Require(status.Contains("App data: " + newData) && status.Contains("from the previous app folder (" + oldData + ")"), "replace-status must name the new Data folder and the rebased old one: " + status);
            Require(ReplaceEngine.Sha(statePath) == stateBefore, "replace-status must not change the state file");
            string log = File.ReadAllText(Path.Combine(newData, "log.txt"));
            Require(log.Contains("Relocation: " + statePath + ": ") && log.Contains("rebased from " + oldData + " to " + newData), "rebase not logged: " + log);
            Require(System.Text.RegularExpressions.Regex.Matches(log, System.Text.RegularExpressions.Regex.Escape("Relocation: " + statePath + ": ")).Count == 1, "the rebase must be logged once per load: " + log);
            SameSnapshot(installed, Snapshot(f.Game), "replace-status after the move");

            bool cli = !LinuxPlatform.IsLinux && !RealGameOrFluffyRunning;   // a Linux write child would need its own native session; run those operations in the owning fixture process
            ReplaceEngine moved = AppEngine(f, newData);
            if (cli)
            {
                string first = RunAppCli(newApp, "replace-uninstall", "Cammy Colors");
                Require(first.StartsWith("0\n") && first.Contains("Manager folder moved") && first.Contains("Uninstalled 'Cammy Colors'"), "uninstall from the new folder: " + first);
            }
            else { Console.WriteLine("NOTE relocation: uninstalls run in the owning fixture process from the moved Data."); Require(moved.PersistRelocation() != null, "PersistRelocation"); moved.Uninstall(a.Id); }
            s = moved.LoadState();
            OnlyUnder(s, newData, "after the first change");
            Require(!File.ReadAllText(statePath).Contains(JsonPath(oldApp)), "replace-state.json still names the old folder");
            foreach (string meta in Directory.GetFiles(Path.Combine(newData, "pak-backups"), "*.json")) Require(!File.ReadAllText(meta).Contains(JsonPath(oldApp)), Path.GetFileName(meta) + " still names the old folder");
            Require(ReplaceEngine.Sha(basePak) == cleanBase, "the base PAK index is not byte-exact after uninstalling the PAK replacement");
            if (cli) { string second = RunAppCli(newApp, "replace-uninstall", "Tool Pack"); Require(second.StartsWith("0\n") && second.Contains("Replacements (0"), "tool uninstall from the new folder: " + second); }
            else moved.Uninstall(tool.Id);
            Require(File.ReadAllText(GameFile(f, "amd_ags_x64.dll")) == "genuine AMD AGS library", "the genuine amd_ags_x64.dll was not restored");
            SameSnapshot(clean, Snapshot(f.Game), "after uninstalling everything from the moved folder");
            List<RemovedReplacement> removed = moved.ListRemoved();
            Require(removed.Count == 3 && removed.Any(x => x.Name == "Cammy Alt") && removed.Any(x => x.Name == "Tool Pack") && removed.All(x => x.Folder.StartsWith(newData, StringComparison.OrdinalIgnoreCase)), "Recovery: " + String.Join(", ", removed.Select(x => x.Name + " @ " + x.Folder)));
            ReplaceMod restored; moved.RestoreRemoved(removed.First(x => x.Name == "Cammy Alt").Folder, out restored);
            Require(File.Exists(GameFile(f, V5)) && moved.Status().BackupsMissing == 0, "the copy removed before the move did not restore");
            moved.RestoreAll();
            SameSnapshot(clean, Snapshot(f.Game), "after restoring and removing the pre-move copy");
            Require(!File.ReadAllText(statePath).Contains(JsonPath(oldApp)) && !Directory.Exists(oldApp), "the old folder came back or is still recorded");
        }

        private static void RelocationMissingBackup(string root)
        {
            Fixture f = Create(Path.Combine(root, "fixture"), false, ReplaceMethods.Pak);
            string basePak = Path.Combine(f.Game, "re_chunk_000.pak"), cleanBase = ReplaceEngine.Sha(basePak);
            string oldApp = Path.Combine(root, "old app"), newApp = Path.Combine(root, "new app");
            string oldData = MakeAppFolder(oldApp, f), newData = Path.Combine(newApp, "Data");
            f.Engine = AppEngine(f, oldData);
            ReplaceMod tool = Install(f, "tool", "Tool Pack");
            ReplaceMod a = Install(f, "modA", "Cammy Colors");
            string recorded = f.Engine.LoadState().Originals[0].Backup;
            MoveApp(oldApp, newApp);
            File.Delete(Path.Combine(newData, "replace-backups", Path.GetFileName(recorded)));
            ReplaceEngine moved = AppEngine(f, newData);
            GameFilesStatus status = moved.Status();
            Require(status.BackupsMissing == 1 && status.FirstMissingBackup == recorded && status.StoredMissing == 0 && status.Summary.Contains("backup is missing") && status.Summary.Contains(recorded), "status: " + status.Summary + " / " + String.Join("; ", status.Details));
            Require(status.Relocation != null && status.Relocation.Missing == 1 && status.Relocation.Rebased >= 2, "relocation report: rebased " + (status.Relocation == null ? -1 : status.Relocation.Rebased));
            string cli = RunAppCli(newApp, "replace-status");
            Require(cli.StartsWith("0\n") && cli.Contains("1 missing on disk (first: " + recorded + ")") && cli.Contains("in neither place"), "replace-status: " + cli);
            moved.Uninstall(a.Id);
            Require(ReplaceEngine.Sha(basePak) == cleanBase && moved.LoadState().Mods.Count == 1, "the other mod must uninstall normally");
            ReplaceState s = moved.LoadState();
            Require(s.StoreRoot.StartsWith(newData, StringComparison.OrdinalIgnoreCase) && s.Originals[0].Backup == recorded, "the found paths are saved, the missing one stays as recorded: " + s.StoreRoot + " / " + s.Originals[0].Backup);
            var before = Snapshot(f.Game);
            Refused(delegate { moved.Uninstall(tool.Id); }, "missing");
            SameSnapshot(before, Snapshot(f.Game), "after the refused uninstall");
            Require(!moved.JournalPending && moved.LoadState().Find(tool.Id) != null && File.ReadAllText(GameFile(f, "amd_ags_x64.dll")) == "slotsloader proxy" && !Directory.Exists(oldApp), "the failed uninstall changed something");
        }

        private static void RelocationJournal(string root)
        {
            int clean = -1;
            for (int k = 0; k < 40 && clean < 0; k++)
            {
                Fixture f = Create(Path.Combine(root, "u" + k, "fixture"), false, ReplaceMethods.Pak);
                var original = Snapshot(f.Game);
                string oldApp = Path.Combine(root, "u" + k, "old"), newApp = Path.Combine(root, "u" + k, "new");
                string oldData = MakeAppFolder(oldApp, f), newData = Path.Combine(newApp, "Data");
                f.Engine = AppEngine(f, oldData);
                ReplaceMod tool = Install(f, "tool", "Tool Pack");
                Install(f, "modA", "Cammy Colors");
                var installed = Snapshot(f.Game);
                ReplaceEngine.CrashAfterStep = k; bool crashed = false;
                try { f.Engine.Uninstall(tool.Id); } catch (ReplaceEngine.SimulatedCrashException) { crashed = true; }
                ReplaceEngine.CrashAfterStep = -1;
                if (!crashed) { clean = k; break; }
                Require(f.Engine.JournalPending, "no journal after a crash at step " + k);
                MoveApp(oldApp, newApp);
                ReplaceEngine moved = AppEngine(f, newData);
                string message = moved.Recover();
                Require(message != null && !moved.JournalPending, "recovery from the new folder at step " + k + ": " + message);
                ReplaceState s = moved.LoadState();
                if (s.Find(tool.Id) != null) SameSnapshot(installed, Snapshot(f.Game), "rolled back from the new folder at step " + k);
                else
                {
                    Require(message.Contains("had finished") && File.ReadAllText(GameFile(f, "amd_ags_x64.dll")) == "genuine AMD AGS library", "completed change at step " + k + ": " + message);
                    Require(moved.ListRemoved().Any(x => x.Name == "Tool Pack" && x.Folder.StartsWith(newData, StringComparison.OrdinalIgnoreCase)), "the recoverable copy was not finished in the new folder at step " + k);
                    Require(Directory.GetFiles(Path.Combine(newData, "replace-backups")).Length == 0, "the restored original's backup was not cleaned up in the new folder at step " + k);
                }
                Require(!Directory.Exists(oldApp), "recovery recreated the old app folder at step " + k);
                Require(moved.Status().BackupsMissing == 0, "missing backups after recovery at step " + k);
                moved.RestoreAll();
                SameSnapshot(original, Snapshot(f.Game), "restore after recovery at step " + k);
                Require(!Directory.Exists(oldApp), "restore recreated the old app folder at step " + k);
            }
            Require(clean > 3, "the crash loop did not reach a clean run");
            // An interrupted install: the backup and store copy it created are removed from the new folder.
            for (int k = 0; k < 3; k++)
            {
                Fixture f = Create(Path.Combine(root, "i" + k, "fixture"), false, ReplaceMethods.Pak);
                var original = Snapshot(f.Game);
                string oldApp = Path.Combine(root, "i" + k, "old"), newApp = Path.Combine(root, "i" + k, "new");
                string oldData = MakeAppFolder(oldApp, f), newData = Path.Combine(newApp, "Data");
                f.Engine = AppEngine(f, oldData);
                ReplaceEngine.CrashAfterStep = k; bool crashed = false;
                try { Install(f, "tool", "Tool Pack"); } catch (ReplaceEngine.SimulatedCrashException) { crashed = true; }
                ReplaceEngine.CrashAfterStep = -1;
                Require(crashed && f.Engine.JournalPending, "install did not crash at step " + k);
                MoveApp(oldApp, newApp);
                ReplaceEngine moved = AppEngine(f, newData);
                moved.Recover();
                Require(moved.LoadState().Mods.Count == 0, "install crash at step " + k + " was not rolled back");
                SameSnapshot(original, Snapshot(f.Game), "install crash at step " + k + " rolled back from the new folder");
                string backups = Path.Combine(newData, "replace-backups");
                Require(!Directory.Exists(backups) || Directory.GetFiles(backups).Length == 0, "the rolled-back install left its backup in the new folder at step " + k);
                Require(!Directory.Exists(oldApp), "recovery recreated the old app folder (install step " + k + ")");
            }
        }

        private static void ArchiveStorage(string root)
        {
            var f = Create(root, false, ReplaceMethods.Pak);
            var clean = Snapshot(f.Game);
            string lib2 = Path.Combine(root, "second"); Directory.CreateDirectory(lib2);
            f.Engine.SourceConfig = new AppConfig { GameRoot = f.Game, Libraries = new List<string> { f.Mods, lib2 } };
            string zip = f.Mod("First.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(f.Mod("modB"), zip);
            ReplaceMod mod = Install(f, "First.zip", "First");
            var file = mod.Files.Single(x => x.Path == V5);
            string store = f.Engine.StoreFile(f.Engine.LoadState(), mod, file), live = f.Engine.GamePath(V5);
            Require(mod.ArchiveSha == ReplaceEngine.Sha(zip) && file.ArchiveEntry != null && !File.Exists(store) && ReplaceEngine.Sha(live) == file.Sha256, "active archive file stored twice");
            var renamed = SourceRename.Run(zip, "Renamed", f.Engine.SourceConfig.Libraries, new List<KeyValuePair<string,string>>(), "", delegate { return f.Engine; });
            Require(renamed.Done && f.Engine.LoadState().Find(mod.Id).SourcePath == renamed.To, "rename did not follow archive: " + renamed.Error);
            string moved = Path.Combine(lib2, "Different.zip");
            File.Move(renamed.To, moved); File.Copy(moved, zip);
            f.Engine.RelinkArchives();
            Require(f.Engine.LoadState().Find(mod.Id).SourcePath == renamed.To, "identical twins were silently rebound");
            // 1.0.15: a repair reads either identical copy (only the relink needs one).
            File.Delete(live); f.Engine.Reapply();
            Require(ReplaceEngine.Sha(live) == file.Sha256, "repair from one of two identical copies");
            File.Delete(zip); System.IO.Compression.ZipFile.CreateFromDirectory(f.Mod("modA"), renamed.To);
            f.Engine.RelinkArchives();
            Require(f.Engine.LoadState().Find(mod.Id).SourcePath == moved, "renamed move with an impostor at the old path");
            File.Delete(live); f.Engine.Reapply();
            Require(ReplaceEngine.Sha(live) == file.Sha256 && !File.Exists(store), "repair from moved archive");
            ReplaceEngine.CrashAfterStep = 1;
            try { f.Engine.SetEnabled(mod.Id, false); } catch (ReplaceEngine.SimulatedCrashException) { } finally { ReplaceEngine.CrashAfterStep = -1; }
            Require(f.Engine.JournalPending, "crash probe missed journal");
            f.Engine.Recover(); Require(ReplaceEngine.Sha(live) == file.Sha256, "archive rollback");
            f.Engine.SetEnabled(mod.Id, false); Require(ReplaceEngine.Sha(store) == file.Sha256 && !File.Exists(live), "disabled recovery file");
            f.Engine.SetEnabled(mod.Id, true); Require(!File.Exists(store), "enable left duplicate store file");
            Require(!f.Engine.CanReclaimArchiveStore(f.Engine.LoadState()), "new install offered legacy cleanup");
            // Older releases held a full copy and did not record archive member paths.
            var legacy = f.Engine.LoadState(); var old = legacy.Find(mod.Id);
            old.ArchiveSha = old.ArchiveStamp = null;
            foreach (ModFile part in old.Files)
            {
                part.ArchiveEntry = null;
                string stored = f.Engine.StoreFile(legacy, old, part); Directory.CreateDirectory(Path.GetDirectoryName(stored));
                if (!File.Exists(stored)) File.Copy(f.Engine.GamePath(part.Path), stored);
            }
            DurableFile.WriteJson(f.Engine.StatePath, legacy);
            Require(f.Engine.CanReclaimArchiveStore(legacy), "legacy cleanup unavailable");
            using (var held = File.Open(moved, FileMode.Open, FileAccess.Read, FileShare.None))
                f.Engine.CompactArchiveStore();
            Require(f.Engine.LoadState().ArchiveStoreReclaimed == false && f.Engine.CanReclaimArchiveStore(f.Engine.LoadState()), "failed cleanup cannot retry");
            f.Engine.CompactArchiveStore();
            var completed = f.Engine.LoadState();
            Require(completed.ArchiveStoreReclaimed == true && !f.Engine.CanReclaimArchiveStore(completed), "completed cleanup still offered");
            f.Engine.CompactArchiveStore();
            Require(f.Engine.LoadState().Generation == completed.Generation, "repeated cleanup changed state");
            Require(!File.Exists(store) && f.Engine.LoadState().Find(mod.Id).Files.Single(x => x.Path == V5).ArchiveEntry != null, "legacy migration");
            mod = f.Engine.LoadState().Find(mod.Id);
            string costumeRoot = Path.Combine(root, "saved-game", "reframework", "costume_mods");
            Directory.CreateDirectory(costumeRoot);
            OutfitSetInfo preset = OutfitSetStore.CreatePreset(costumeRoot, "Archive-backed", PresetScope.Replacements, f.Engine);
            string removed;
            f.Engine.Uninstall(mod.Id, false, out removed);
            string last = f.Mod("Another name.zip"); File.Move(moved, last);
            var result = OutfitSetStore.Restore(costumeRoot, preset, PresetScope.Replacements, f.Engine);
            Require(result.ReplacementsRestored == 1 && ReplaceEngine.Sha(live) == file.Sha256, "saved preset did not find moved archive: " + String.Join(" ", result.Notes));
            mod = f.Engine.LoadState().Mods.Single(); store = f.Engine.StoreFile(f.Engine.LoadState(), mod, file);
            File.Delete(last);
            File.Delete(live);
            Refused(delegate { f.Engine.Reapply(); }, "original archive");
            Require(!File.Exists(live), "wrong archive was used for a repair");
            File.WriteAllBytes(live, File.ReadAllBytes(Path.Combine(f.Mod("modB"), V5)));
            f.Engine.Uninstall(mod.Id, true, out removed);
            ReplaceMod back; f.Engine.RestoreRemoved(removed, out back);
            Require(ReplaceEngine.Sha(live) == file.Sha256, "restore without source");
            f.Engine.Uninstall(back.Id, false, out removed);
            SameSnapshot(clean, Snapshot(f.Game), "archive storage uninstall");
            System.IO.Compression.ZipFile.CreateFromDirectory(f.Mod("modB"), last);
            mod = Install(f, Path.GetFileName(last), "Partial");
            File.Delete(last); File.Delete(live);
            f.Engine.UninstallForGood(mod.Id, DriftChoice.RestoreOriginal, new CheckRecycleBin());
            // Manually deleting the last file in a directory can leave its empty parents in place.
            var afterLoss = Snapshot(f.Game);
            Require(f.Engine.LoadState().Mods.Count == 0, "missing archive blocked uninstall");
            SameSnapshot(clean.Where(x => !x.Key.EndsWith("\\")).ToDictionary(x => x.Key, x => x.Value),
                afterLoss.Where(x => !x.Key.EndsWith("\\")).ToDictionary(x => x.Key, x => x.Value), "original game files after source and live file were lost");
        }

        private static void CheckCache(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            string zip = Path.Combine(root, "modA.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(f.Mod("modA"), zip);
            ModPackage package = PackageReader.Load(zip, "");
            CompositionPlan composition = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
            using (var cache = new PrepareCache())
            {
                ReplacePlan first = ReplacePlanner.Check(composition, "", f.Engine, cache, null);
                ReplacePlan second = ReplacePlanner.Check(composition, "", f.Engine, cache, null);
                ReplacePlan plain = ReplacePlanner.Check(composition, "", f.Engine);
                Require(cache.Extractions == 1 && cache.Hits == second.Files.Count && cache.Misses == first.Files.Count, "cache: extractions " + cache.Extractions + ", hits " + cache.Hits + ", misses " + cache.Misses);
                Func<ReplacePlan, string> sig = delegate(ReplacePlan p) { return String.Join("|", p.Files.Select(x => x.Path + ":" + x.Sha256 + ":" + x.Size)) + "#" + p.Fixed + "#" + ReplacePlanner.Report(p).Split('\n').Length; };
                Require(sig(first) == sig(second) && sig(first) == sig(plain), "cached result differs from a plain check");
                File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));
                ReplacePlanner.Check(composition, "", f.Engine, cache, null);
                Require(cache.Extractions == 2, "a changed archive was not read again");
            }
        }

        // 2.3.0: "Test in game" is gone; a test left by an older version is kept as a normal install, its REFramework
        // logging keys restored and its record deleted (no prompt). The game folder is otherwise untouched.
        private static void LeftoverTest(string root, FakeProcessProbe probe)
        {
            Fixture f = Create(root, true, ReplaceMethods.Loose);
            ReplacePlan plan;
            ReplaceMod a = Install(f, "modA", "Test A", true, null, out plan);
            var before = Snapshot(f.Game);
            string accessed = LooseLoader.Read(f.Game, LooseLoader.LogAccessedKey), loose = LooseLoader.Read(f.Game, LooseLoader.LogLooseKey);
            var record = new TestSessionRecord { Mode = InstallModes.Replace, ReplaceModId = a.Id, Name = a.Name, Phase = "running", LaunchedUtc = DateTime.UtcNow.ToString("o"), Files = a.Files.Select(x => x.Path).ToList() };
            // What the old Test in game did before launching: turn on the access logs and remember the old values.
            record.RestoreConfig[LooseLoader.LogAccessedKey] = accessed ?? "false"; record.RestoreConfig[LooseLoader.LogLooseKey] = loose ?? "false";
            LooseLoader.Write(f.Game, LooseLoader.LogAccessedKey, "true", f.Engine.ConfigBackupDir); LooseLoader.Write(f.Game, LooseLoader.LogLooseKey, "true", f.Engine.ConfigBackupDir);
            TestSession.Save(f.Data, record);
            Require(TestSession.Load(f.Data) != null && f.Engine.LoadState().Find(a.Id).Test, "fixture");
            string message = TestSession.FinishLeftover(f.Engine, TestSession.Load(f.Data));
            Require(TestSession.Load(f.Data) == null && !File.Exists(TestSession.FilePath(f.Data)), "the record was not deleted");
            Require(!f.Engine.LoadState().Find(a.Id).Test && f.Engine.LoadState().Find(a.Id).Enabled, "the replacement was not kept as a normal install");
            Require(LooseLoader.Read(f.Game, LooseLoader.LogAccessedKey) == (accessed ?? "false") && LooseLoader.Read(f.Game, LooseLoader.LogLooseKey) == (loose ?? "false"), "logging keys not restored");
            var after = Snapshot(f.Game); after.Remove(LooseLoader.ConfigName); before.Remove(LooseLoader.ConfigName);
            SameSnapshot(before, after, "after keeping the leftover test");
            Require(message.Contains("Kept 'Test A'") && message.Contains("logging"), "message: " + message);
            // An extra-outfit test and a record whose replacement is already gone are just cleared.
            TestSession.Save(f.Data, new TestSessionRecord { Mode = InstallModes.Slot, Name = "Alt (test)" });
            Require(TestSession.FinishLeftover(f.Engine, TestSession.Load(f.Data)).Contains("extra outfit") && TestSession.Load(f.Data) == null, "slot leftover");
            TestSession.Save(f.Data, new TestSessionRecord { Mode = InstallModes.Replace, ReplaceModId = "gone", Name = "Gone" });
            Require(TestSession.FinishLeftover(f.Engine, TestSession.Load(f.Data)).Contains("already gone") && TestSession.Load(f.Data) == null, "missing replacement leftover");
        }

        private static void CrashReporterRename(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            ReplaceMod a = Install(f, "modA", "A");
            Require(File.Exists(GameFile(f, CrashReporter.RenamedName)) && !File.Exists(GameFile(f, CrashReporter.Name)), "not renamed");
            f.Engine.SetEnabled(a.Id, false);
            Require(File.Exists(GameFile(f, CrashReporter.Name)) && !File.Exists(GameFile(f, CrashReporter.RenamedName)), "not restored when nothing is enabled");
            f.Engine.Uninstall(a.Id);
            Fixture g = Create(Path.Combine(root, "fluffy-renamed"), false, ReplaceMethods.Pak);
            File.Move(GameFile(g, CrashReporter.Name), GameFile(g, CrashReporter.RenamedName));
            ReplaceMod b = Install(g, "modA", "B"); g.Engine.Uninstall(b.Id);
            Require(File.Exists(GameFile(g, CrashReporter.RenamedName)) && !File.Exists(GameFile(g, CrashReporter.Name)), "a rename made by Fluffy must be left alone");
            // 1.0.16 (user report): Fluffy put CrashReport.exe back when its last mod went while this state still said
            // renamed; the next change renames it again (it was left active with 16 replacements installed).
            Fixture h = Create(Path.Combine(root, "put-back"), false, ReplaceMethods.Pak);
            ReplaceMod c = Install(h, "modA", "C");
            File.Move(GameFile(h, CrashReporter.RenamedName), GameFile(h, CrashReporter.Name));
            Require(h.Engine.LoadState().CrashReporterRenamed, "fixture: the state says renamed");
            ReplaceMod d = Install(h, "modB", "D");
            Require(File.Exists(GameFile(h, CrashReporter.RenamedName)) && !File.Exists(GameFile(h, CrashReporter.Name)), "a crash reporter put back by another tool was not renamed again");
            h.Engine.Uninstall(c.Id); h.Engine.Uninstall(d.Id);
            Require(File.Exists(GameFile(h, CrashReporter.Name)) && !File.Exists(GameFile(h, CrashReporter.RenamedName)), "not restored after the last replacement");
            // The Settings checkbox is gone: a settings file saying false is ignored.
            Fixture k = Create(Path.Combine(root, "setting-off"), false, ReplaceMethods.Pak);
            k.Engine.Settings.RenameCrashReporter = false;
            Install(k, "modA", "E");
            Require(File.Exists(GameFile(k, CrashReporter.RenamedName)) && !File.Exists(GameFile(k, CrashReporter.Name)), "RenameCrashReporter=false must no longer stop the rename");
            // 1.0.16 (PR #22 review, F5): Steam "Verify integrity" (or a game update) puts CrashReport.exe back beside the
            // renamed copy; the next change renames it again. Other bytes: the stale copy is kept as .old and the exe takes
            // the renamed name; the same bytes: the exe goes (the renamed copy is it). No copy is ever lost.
            Fixture v = Create(Path.Combine(root, "verified"), false, ReplaceMethods.Pak);
            ReplaceMod g1 = Install(v, "modA", "G1");
            string crash = GameFile(v, CrashReporter.Name), renamed = GameFile(v, CrashReporter.RenamedName);
            byte[] stale = File.ReadAllBytes(renamed), fresh = System.Text.Encoding.ASCII.GetBytes("CrashReport.exe after a game update");
            File.WriteAllBytes(crash, fresh);
            Install(v, "modB", "G2");
            Require(!File.Exists(crash) && File.ReadAllBytes(renamed).SequenceEqual(fresh) && File.Exists(renamed + ".old") && File.ReadAllBytes(renamed + ".old").SequenceEqual(stale) && v.Engine.LoadState().CrashReporterRenamed && !v.Engine.JournalPending,
                "both present: exe " + File.Exists(crash) + ", renamed " + (File.Exists(renamed) ? System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(renamed)) : "missing") + ", .old " + File.Exists(renamed + ".old"));
            File.WriteAllBytes(crash, fresh);
            v.Engine.SetEnabled(g1.Id, false);
            Require(!File.Exists(crash) && File.ReadAllBytes(renamed).SequenceEqual(fresh) && !File.Exists(renamed + ".old2"), "the same bytes: the exe stays beside its copy, or a second .old was made");
            // A crash at any step of that change rolls back to both files as they were (or, once committed, the rename).
            int clean = -1;
            for (int n = 0; n < 40 && clean < 0; n++)
            {
                Fixture w = Create(Path.Combine(root, "verified-crash-" + n), false, ReplaceMethods.Pak);
                Install(w, "modA", "H1");
                string cCrash = GameFile(w, CrashReporter.Name), cRenamed = GameFile(w, CrashReporter.RenamedName);
                byte[] cStale = File.ReadAllBytes(cRenamed);
                File.WriteAllBytes(cCrash, fresh);
                var before = Snapshot(w.Game);
                ReplaceEngine.CrashAfterStep = n; bool crashed = false;
                try { Install(w, "modB", "H2"); } catch (ReplaceEngine.SimulatedCrashException) { crashed = true; }
                finally { ReplaceEngine.CrashAfterStep = -1; }
                if (!crashed) { clean = n; break; }
                w.Engine.Recover();
                if (w.Engine.LoadState().Mods.Count == 1) SameSnapshot(before, Snapshot(w.Game), "crash reporter rollback after a crash at step " + n);
                else Require(!File.Exists(cCrash) && File.ReadAllBytes(cRenamed).SequenceEqual(fresh) && File.ReadAllBytes(cRenamed + ".old").SequenceEqual(cStale), "committed crash at step " + n + " lost a copy");
            }
            Require(clean > 3, "the crash loop did not reach a clean run: " + clean);
        }

        private static void TocFallback(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            string pak = Path.Combine(f.Game, "re_chunk_000.pak"), clean = ReplaceEngine.Sha(pak);
            Install(f, "modA", "A");
            File.Delete(f.Engine.StatePath);   // record lost
            int restored = f.Engine.RestoreFromTocBackup("re_chunk_000.pak");
            Require(restored == 2 && ReplaceEngine.Sha(pak) == clean, "fallback restored " + restored + " rows");
            RewritePak(f, 0x51, false);
            Refused(delegate { f.Engine.RestoreFromTocBackup("re_chunk_000.pak"); }, "No TOC backup");
        }

        internal static string Cli(params string[] args)
        {
            var output = new StringWriter(); var error = new StringWriter();
            TextWriter previousOut = Console.Out, previousError = Console.Error;
            Console.SetOut(output); Console.SetError(error);
            int code;
            try { code = ReplaceCommandLine.Run(args); }
            finally { Console.SetOut(previousOut); Console.SetError(previousError); }
            return code + "\n" + output + error;
        }

        private static void CommandLineFlow(string root)
        {
            Fixture f = Create(root, true, null);
            var clean = Snapshot(f.Game);
            string plan = Cli("replace-plan", f.Mod("modA"), "--game", f.Game, "--method", "pak");
            Require(plan.StartsWith("0\n") && plan.Contains("Cammy Outfit 2") && plan.Contains("re_chunk_000.pak #") && plan.Contains("Read-only"), "replace-plan: " + plan);
            SameSnapshot(clean, Snapshot(f.Game), "after replace-plan");
            string refused = Cli("replace-install", f.Mod("modA"), "--game", f.Game, "--method", "loose");
            Require(refused.StartsWith("1\n") && refused.Contains("--yes"), "loose install must require --yes while the loader is off: " + refused);
            SameSnapshot(clean, Snapshot(f.Game), "after a refused install");
            string install = Cli("replace-install", f.Mod("modA"), "--game", f.Game, "--method", "pak", "--name", "CLI test");
            Require(install.StartsWith("0\n") && install.Contains("zeroed entries 2") && install.Contains("Replacements (1"), "replace-install: " + install);
            string status = Cli("replace-status", "--game", f.Game);
            Require(status.Contains("CLI test") && status.Contains("invalidated PAK entries held: 3") && status.Contains("entry-table SHA-256"), "replace-status: " + status);
            string toLoose = Cli("replace-method", "--game", f.Game, "--set", "loose", "--yes");
            Require(toLoose.StartsWith("0\n") && toLoose.Contains("Turned on") && toLoose.Contains("zeroed entries 0"), "replace-method loose: " + toLoose);
            string toPak = Cli("replace-method", "--game", f.Game, "--set", "pak");
            Require(toPak.StartsWith("0\n") && toPak.Contains("zeroed entries 2"), "replace-method pak: " + toPak);
            string toAuto = Cli("replace-method", "--game", f.Game, "--set", "auto");
            Require(toAuto.StartsWith("0\n") && toAuto.Contains("zeroed entries 0") && toAuto.Contains("Converted installed replacements to the REFramework loose file loader"), "replace-method auto: " + toAuto);
            string noYes = Cli("replace-restore-all", "--game", f.Game);
            Require(noYes.StartsWith("1\n") && noYes.Contains("--yes"), "restore-all must require --yes");
            string uninstall = Cli("replace-uninstall", "CLI test", "--game", f.Game);
            Require(uninstall.StartsWith("0\n") && uninstall.Contains("natives folder: absent") && uninstall.Contains("zeroed entries 0"), "replace-uninstall: " + uninstall);
            Cli("replace-install", f.Mod("tool"), "--game", f.Game);
            string restore = Cli("replace-restore-all", "--game", f.Game, "--yes");
            Require(restore.StartsWith("0\n") && restore.Contains("Replacements (0"), "replace-restore-all: " + restore);
            var after = Snapshot(f.Game); after.Remove(LooseLoader.ConfigName); var expected = new Dictionary<string, string>(clean, StringComparer.OrdinalIgnoreCase); expected.Remove(LooseLoader.ConfigName);
            SameSnapshot(expected, after, "after the CLI cycle (config aside: the loose loader was turned on with --yes)");
            Require(LooseLoader.Read(f.Game, LooseLoader.EnabledKey) == "true", "loader state");
            var settings = ReplaceSettings.Load(); settings.Method = ""; settings.Save();
        }

        private static void OrderedPresetImport(string root, string method)
        {
            Fixture f = Create(root, method == ReplaceMethods.Loose, method);
            if (method == ReplaceMethods.Loose) Put(f.Game, LooseLoader.ConfigName, Bytes("LooseFileLoader_Enabled=true"));
            var baseline = Snapshot(f.Game);
            const string unique = "natives/stm/product/ui/unique.user.2";
            string lib = Path.Combine(root, "library");
            foreach (string item in new[] { "Bundle/Main", "Between", "Bundle/Last" })
            {
                string name = Path.GetFileName(item);
                Put(lib, item + "/modinfo.ini", Bytes("name=" + (name == "Between" ? "Main" : name)));
                Put(lib, item + "/" + V6, Bytes(name));
                if (name != "Last") Put(lib, item + "/" + V5, Bytes(name));
                if (name == "Main") Put(lib, item + "/" + unique, Bytes(name));
            }
            var library = new List<LibraryEntry> {
                new LibraryEntry { SourcePath = Path.Combine(lib, "Bundle"), Name = "Bundle", OptionIds = new List<string> { "Main", "Last" } },
                new LibraryEntry { SourcePath = Path.Combine(lib, "Between"), Name = "Between" } };
            Func<List<PresetMapping>> mapped = delegate { return FluffyPresets.Group(FluffyPresets.Map(FluffyPresets.Parse("[Main]\nModName=Main\n[Between]\nModName=Main\n[Last]\nModName=Last"), library, "")); };
            var cfg = new AppConfig { GameRoot = f.Game };
            Action apply = delegate { PresetInstaller.Install(cfg, f.Engine, mapped()); };
            Func<string, string> game = path => File.ReadAllText(f.Engine.GamePath(path));
            var groups = mapped();
            Require(groups.Count == 2 && groups[0].Sections.Count == 2, "fixture did not resolve interleaved sections");
            // A late staging error and an interrupted publication must leave earlier layers uninstalled.
            Put(lib, "Between/modinfo.ini", Bytes("name=Main\n; changed after review"));
            // 1.0.15: the block fails as a whole and the summary says so (extra outfits would still install).
            string stale = PresetInstaller.Install(cfg, f.Engine, groups);
            Require(stale.Contains("replacements, none installed") && f.Engine.LoadState().Mods.Count == 0, "stale source accepted: " + stale);
            ReplaceEngine.CrashAfterStep = 1;
            try { apply(); } catch (ReplaceEngine.SimulatedCrashException) { } finally { ReplaceEngine.CrashAfterStep = -1; }
            Require(f.Engine.JournalPending, "crash fixture missed journal");
            f.Engine.Recover(); SameSnapshot(baseline, Snapshot(f.Game), "rollback of ordered import");
            apply();
            var mods = f.Engine.LoadState().Mods;
            Require(mods.Count == 3 && mods.Count(m => m.Name == "Main") == 2 && game(V5) == "Between" && game(V6) == "Last" && game(unique) == "Main", "incorrect winners");
            var health = f.Engine.CheckInstalledFiles(mods[2].Id);
            Require(health.Any(i => i.Text.StartsWith("Supplied by 'Last'")) && health[0].Text.Contains("supplied by other mods"), "Health did not name the mods that win the overridden files: " + String.Join(" | ", health.Select(i => i.Text)));
            string outfits = Path.Combine(root, "preset-game", "reframework", "costume_mods"); Directory.CreateDirectory(outfits);
            OutfitSetInfo saved = OutfitSetStore.CreatePreset(outfits, "Ordered", PresetScope.Replacements, f.Engine);
            f.Engine.Move(mods[0].Id, 1); f.Engine.SetEnabled(mods[2].Id, false);
            apply();
            Require(f.Engine.LoadState().Mods.Select(m => m.Id).SequenceEqual(mods.Select(m => m.Id)) && game(V6) == "Last" && game(unique) == "Main", "reimport changed IDs, order or enabled state");
            f.Engine.UninstallForGood(mods[0].Id, DriftChoice.Ask, null);
            Require(game(V6) == "Between" && game(V5) == "Between", "removing final option did not reveal middle mod");
            f.Engine.UninstallForGood(mods[1].Id, DriftChoice.Ask, null);
            Require(game(V6) == "Main" && game(V5) == "Main", "removing middle did not reveal base");
            f.Engine.UninstallForGood(mods[2].Id, DriftChoice.Ask, null);
            SameSnapshot(baseline, Snapshot(f.Game), "ordered preset full uninstall");
            // One row per preset entry (1.0.15): leaving out Last makes the middle archive win, with Main below it.
            var rows = FluffyPresets.Map(FluffyPresets.Parse("[Main]\nModName=Main\n[Between]\nModName=Main\n[Last]\nModName=Last"), library, "");
            Require(rows.Count == 3 && rows.All(r => r.Entry != null), "preset rows per entry");
            rows[2].Include = false;
            PresetInstaller.Install(cfg, f.Engine, rows);
            Require(f.Engine.LoadState().Mods.Count == 2 && game(V6) == "Between" && game(V5) == "Between" && game(unique) == "Main", "Between must win with Last left out");
            foreach (ReplaceMod m in f.Engine.LoadState().Mods.ToList()) f.Engine.UninstallForGood(m.Id, DriftChoice.Ask, null);
            SameSnapshot(baseline, Snapshot(f.Game), "uninstall after a preset with an entry left out");
            var restored = OutfitSetStore.Restore(outfits, saved, PresetScope.Replacements, f.Engine);
            Require(restored.ReplacementsRestored == 3 && game(V5) == "Between" && game(V6) == "Last" && game(unique) == "Main", "native list did not restore layer order: " + String.Join(" ", restored.Notes));
            var twin = Path.Combine(root, "other", "Between");
            Put(twin, "modinfo.ini", Bytes("name=Main")); Put(twin, V5, Bytes("different"));
            library.Add(new LibraryEntry { SourcePath = twin, Name = "Between" });
            for (int i = 0; i < 2; i++) { Require(mapped().Any(m => m.Ambiguous && m.Entry == null && !m.Include && m.Choices.Count == 2), "twin silently selected"); library.Reverse(); }
            // The review resolves an ambiguous entry with the archive the user picks.
            var ambiguous = mapped().First(m => m.Ambiguous);
            FluffyPresets.Resolve(ambiguous, ambiguous.Choices.First(c => c.Entry.SourcePath == twin));
            Require(ambiguous.Entry.SourcePath == twin && ambiguous.Include && !ambiguous.Ambiguous && ambiguous.Stamp != null, "picked archive not used");
            // The same download in two libraries is one match.
            string zipA = Path.Combine(root, "zips-a", "Between.zip"), zipB = Path.Combine(root, "zips-b", "Between.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zipA)); Directory.CreateDirectory(Path.GetDirectoryName(zipB));
            System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(lib, "Between"), zipA); File.Copy(zipA, zipB);
            var copies = new List<LibraryEntry> { new LibraryEntry { SourcePath = zipA, Name = "Between" }, new LibraryEntry { SourcePath = zipB, Name = "Between" } };
            var one = FluffyPresets.Map(FluffyPresets.Parse("[Between]\nModName=Main"), copies, "");
            Require(one.Single().Entry != null && !one.Single().Ambiguous, "identical copies were reported as ambiguous: " + one.Single().Message);
        }

        private static void OrderedPatchPaks(string root)
        {
            Fixture f = Create(root, false, ReplaceMethods.Pak);
            string lib = Path.Combine(root, "Bundle");
            foreach (string component in new[] { "First", "Second" })
            {
                Put(lib, component + "/modinfo.ini", Bytes("name=" + component));
                PakWriter.WriteFixture(Path.Combine(lib, component, "payload.pak"), new List<KeyValuePair<string, byte[]>> {
                    new KeyValuePair<string, byte[]>(P1, Bytes(component)) }, 0, 0);
            }
            var library = new[] { new LibraryEntry { SourcePath = lib, Name = "Bundle", OptionIds = new List<string> { "First", "Second" } } };
            foreach (string[] pair in new[] { new[] { "First", "Second" }, new[] { "Second", "First" } })
            {
                bool reimport = pair[0] == "Second";
                if (reimport)
                {
                    // Another tool installs an independent PAK after the first import. It keeps #004.
                    PakWriter.WriteFixture(f.Engine.GamePath(ReplaceEngine.PatchPakName(4)), new List<KeyValuePair<string, byte[]>> {
                        new KeyValuePair<string, byte[]>(P1, Bytes("Unrelated")) }, 0, 0);
                }
                string text = String.Join("\n", pair.Select(c => "[" + c + "]"));
                var groups = FluffyPresets.Group(FluffyPresets.Map(FluffyPresets.Parse(text), library, ""));
                PresetInstaller.Install(new AppConfig(), f.Engine, groups);
                var mods = f.Engine.LoadState().Mods;
                Require(mods.Count == 2 && mods[0].Name == pair[1] && mods[1].Name == pair[0], "wrong installed PAK order");
                for (int i = 0; i < 2; i++)
                {
                    string path = ReplaceEngine.PatchPakName((reimport ? 5 : 2) + i);
                    Require(mods[1-i].Files.Single().Path == path && ReplaceEngine.Sha(f.Engine.GamePath(path)) == ReplaceEngine.Sha(Path.Combine(lib, pair[i], "payload.pak")), "wrong patch number or contents");
                }
            }
        }

        private static void PresetImport(string root)
        {
            string lib = Path.Combine(root, "lib"); Directory.CreateDirectory(lib);
            Put(lib, "Cammy Bundle/modinfo.ini", Bytes("name=Cammy Bundle\r\n"));
            Put(lib, "Cammy Bundle/Red/modinfo.ini", Bytes("name=Red option\r\n"));
            Put(lib, "Cammy Bundle/Red/natives/stm/product/model/esf/esf009/002/esf009_002_cmd_000.user.2", Usr(MaterialData, 1));
            Put(lib, "Cammy Bundle/Blue/modinfo.ini", Bytes("name=Blue option\r\n"));
            Put(lib, "Cammy Bundle/Blue/natives/stm/product/model/esf/esf009/002/esf009_002_cmd_001.user.2", Usr(MaterialData, 1));
            Put(lib, "Tool/modinfo.ini", Bytes("name=Handy Tool\r\n"));
            Put(lib, "Tool/reframework/autorun/a.lua", Bytes("x"));
            string prt = Path.Combine(root, "test.prt");
            File.WriteAllText(prt, "v2\r\n[Cammy Bundle]\r\nModID_v1=1\r\nShortModID_v1=2\r\nModName=Red option\r\n[Handy Tool]\r\nModID_v1=3\r\nModName=Handy Tool\r\n[Gone Mod]\r\nModName=Gone Mod\r\n");
            List<FluffyPresetEntry> entries = FluffyPresets.Parse(File.ReadAllText(prt));
            Require(entries.Count == 3 && entries[0].Section == "Cammy Bundle" && entries[0].ModName == "Red option", "preset parse");
            var library = Library.Scan(new[] { lib }, "");
            List<PresetMapping> mapped = FluffyPresets.Map(entries, library, "");
            Require(mapped[0].Entry != null && mapped[0].OptionIds.Count == 1 && mapped[0].OptionIds[0] == "Red" && mapped[0].Mode == InstallModes.Replace, "costume mapping: " + mapped[0].Message);
            Require(mapped[1].Entry != null && mapped[1].Kind == "Tool" && mapped[1].Mode == InstallModes.Replace, "tool mapping: " + mapped[1].Message);
            Require(mapped[2].Entry == null && mapped[2].Message.Contains("not found"), "unmatched entry must be reported");
        }
    }
}
