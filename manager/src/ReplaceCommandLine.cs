using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // Replace-original commands for SlotsLoader CLI.exe. They use the same engine,
    // Data folder (beside the executable) and safety checks as the app window.
    internal static class ReplaceCommandLine
    {
        internal static readonly string[] Commands = { "replace-plan", "replace-install", "replace-status", "replace-restore-all", "replace-vanilla", "replace-method", "replace-uninstall", "replace-reapply", "preset-map", "replace-restore-original", "manager-status", "manager-uninstall", "manager-restore", "loader-fetch", "loader-pak-remove", "update-check", "update-install", "update-rollback", "pak-repair", "variants", "fluffy-status", "fluffy-uninstall", "fluffy-restore" };
        // 2.5.1: the fluffy-* names keep working (left out of the help).
        internal static readonly Dictionary<string, string> Aliases = new Dictionary<string, string> { { "fluffy-status", "manager-status" }, { "fluffy-uninstall", "manager-uninstall" }, { "fluffy-restore", "manager-restore" } };

        private sealed class Args
        {
            internal string Command, Source, Game, Name, Method, Set, Json, Target, Root, SevenZip = @"C:\Program Files\7-Zip\7z.exe", SelectionFile;
            internal List<string> Selected = new List<string>(), Libraries = new List<string>();
            internal bool HasSelection, Yes, NoLoader, RestoreOriginals, KeepCurrent, NoDownload, IncludePrereleases;
        }

        internal static int Run(string[] raw)
        {
            if (raw[0].TrimStart('-') == "variants") return VariantsCommandLine.Run(raw);   // 2.5.0 (VariantChoices.cs)
            Args a;
            try { a = Parse(raw); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
            using (var cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                try
                {
                    using (OperationContext.Begin(cancellation.Token, null)) return Execute(a);
                }
                catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. No game file was changed by the cancelled step."); return 130; }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); Program.Log(ex); return 1; }
                finally { Console.CancelKeyPress -= cancel; }
            }
        }

        private static Args Parse(string[] args)
        {
            var a = new Args { Command = args[0].TrimStart('-') };
            string renamed; if (Aliases.TryGetValue(a.Command, out renamed)) a.Command = renamed;
            int i = 1;
            if ((a.Command == "replace-plan" || a.Command == "replace-install" || a.Command == "preset-map") && args.Length > 1 && !args[1].StartsWith("--")) { a.Source = Path.GetFullPath(args[1]); i = 2; }
            if ((a.Command == "replace-uninstall" || a.Command == "update-install" || a.Command == "manager-uninstall" || a.Command == "manager-restore" || a.Command == "replace-restore-original") && args.Length > 1 && !args[1].StartsWith("--")) { a.Target = args[1]; i = 2; }
            for (; i < args.Length; i++)
            {
                string key = args[i];
                if (key == "--yes") { a.Yes = true; continue; }
                if (key == "--no-loader") { a.NoLoader = true; continue; }
                if (key == "--restore-originals") { a.RestoreOriginals = true; continue; }
                if (key == "--keep-current") { a.KeepCurrent = true; continue; }
                if (key == "--no-download") { a.NoDownload = true; continue; }
                if (key == "--include-prereleases") { a.IncludePrereleases = true; continue; }
                if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + key);
                string value = args[++i];
                switch (key)
                {
                    case "--game": a.Game = Path.GetFullPath(value); break;
                    case "--select": a.HasSelection = true; a.Selected.Add(value); break;
                    case "--selection": a.HasSelection = true; a.SelectionFile = Path.GetFullPath(value); break;
                    case "--name": a.Name = value; break;
                    case "--method": a.Method = value.ToLowerInvariant(); break;
                    case "--set": a.Set = value.ToLowerInvariant(); break;
                    case "--json": a.Json = value == "-" ? value : Path.GetFullPath(value); break;
                    case "--sevenzip": a.SevenZip = Path.GetFullPath(value); break;
                    case "--root": a.Root = Path.GetFullPath(value); break;
                    case "--library": a.Libraries.Add(Path.GetFullPath(value)); break;
                    default: throw new ArgumentException("Unknown option: " + key);
                }
            }
            if ((a.Command == "replace-plan" || a.Command == "replace-install" || a.Command == "preset-map") && a.Source == null) throw new ArgumentException(a.Command + " requires a mod archive/folder (or a .prt preset for preset-map).");
            if (a.Command == "replace-uninstall" && a.Target == null) throw new ArgumentException("replace-uninstall requires a replacement id or name (see replace-status).");
            if (a.Command == "manager-uninstall" && a.Target == null) throw new ArgumentException("manager-uninstall requires the name or installed.ini section of a mod an external mod manager installed (see manager-status).");
            if (a.Command == "manager-restore" && a.Target == null) throw new ArgumentException("manager-restore requires the name or folder of a mod uninstalled with manager-uninstall (see manager-status).");
            if (a.Command == "replace-restore-original" && a.Target == null) throw new ArgumentException("replace-restore-original requires a kept game file path (see replace-status).");
            if (a.RestoreOriginals && a.KeepCurrent) throw new ArgumentException("Use either --restore-originals or --keep-current, not both.");
            if (a.Method != null && a.Method != ReplaceMethods.Loose && a.Method != ReplaceMethods.Pak) throw new ArgumentException("--method must be loose or pak.");
            if (a.Set != null && a.Set != ReplaceMethods.Loose && a.Set != ReplaceMethods.Pak && a.Set != "auto") throw new ArgumentException("--set must be loose, pak or auto.");
            return a;
        }

        private static ReplaceEngine Engine(Args a, out AppConfig config)
        {
            config = File.Exists(Path.Combine(AppConfig.DataDir, "settings.json")) ? AppConfig.Load() : AppConfig.Detected();
            if (a.Game != null) config.GameRoot = a.Game;
            if (!Directory.Exists(config.GameRoot)) throw new IOException("Game folder not found: " + config.GameRoot);
            ReplaceEngine engine = ReplaceEngine.ForConfig(config);
            if (a.Method != null) engine.Settings.Method = a.Method;
            return engine;
        }

        private static int Execute(Args a)
        {
            AppConfig config;
            if (a.Command == "preset-map") return PresetMap(a);
            if (a.Command == "loader-fetch") return LoaderFetch(a);
            if (a.Command == "update-check") return UpdateCheckCommand(a);
            if (a.Command == "update-rollback") { AppUpdater.Rollback(MainForm.AppDir, AppConfig.DataDir); Console.WriteLine("Rolled back to the previous Slotsloader Manager (" + MainForm.AppDir + "). Data was not changed."); return 0; }
            if (a.Command == "update-install" && !String.Equals(a.Target, "loader", StringComparison.OrdinalIgnoreCase)) return UpdateInstallManager(a);
            ReplaceEngine engine = Engine(a, out config);
            // A moved app folder: corrected Data paths are saved before a change (replace-status stays read-only).
            if (new[] { "replace-install", "replace-uninstall", "replace-reapply", "replace-restore-all", "replace-vanilla", "replace-restore-original", "manager-uninstall", "manager-restore" }.Contains(a.Command) || (a.Command == "replace-method" && a.Set != null))
            { string moved = engine.PersistRelocation(); if (moved != null) Console.WriteLine(moved); }
            switch (a.Command)
            {
                case "replace-status": return Status(engine, config);
                case "loader-pak-remove": { Console.WriteLine(engine.RemoveOrphanedCostumePak()); return 0; }
                case "pak-repair": return PakRepairCommandLine.Run(engine, a.Yes);   // 2.4.2 (PakRepair.cs)
                case "update-install": return UpdateInstallLoader(a, engine);
                case "replace-plan":
                {
                    CompositionPlan composition = Compose(a);
                    ReplacePlan plan = ReplacePlanner.Check(composition, a.SevenZip, engine);
                    Console.WriteLine(ReplacePlanner.Report(plan));
                    Console.WriteLine("PAK entries that would be invalidated (PAK method):");
                    foreach (string row in plan.InvalidationPreview) Console.WriteLine("  " + row);
                    if (a.Json != null) WriteJson(a.Json, PlanJson(plan));
                    Console.WriteLine("Read-only: nothing in the game folder was changed.");
                    return plan.CanInstall ? 0 : 1;
                }
                case "replace-install":
                {
                    ReplaceState state = engine.LoadState();
                    if (a.Method != null && (state.Mods.Count > 0 || state.Deployed.Count > 0) && engine.CurrentMethod(state) != a.Method) throw new IOException("Installed replacements use the " + ReplaceMethods.Label(engine.CurrentMethod(state)) + ". Switch with replace-method --set " + a.Method + " first.");
                    string method = engine.CurrentMethod(state);
                    if (a.Yes && a.NoLoader) throw new ArgumentException("Use either --yes (turn the loose file loader on) or --no-loader (install with it off), not both.");
                    CompositionPlan composition = Compose(a);
                    // 2.1.0: only natives files need the loose file loader; tool-only installs never ask.
                    bool natives = composition.Files.Keys.Any(k => k.StartsWith("natives/", StringComparison.OrdinalIgnoreCase));
                    if (method == ReplaceMethods.Loose && natives)
                    {
                        string detail; LooseLoaderState loader = LooseLoader.State(engine.GameRoot, out detail);
                        if (loader != LooseLoaderState.Enabled && a.NoLoader && loader != LooseLoaderState.ReFrameworkMissing) Console.WriteLine("Installing with REFramework's loose file loader off (--no-loader): the game ignores the replaced files until it is turned on (replace-method --set loose --yes, or the app's Settings).");
                        else RequireLoader(engine, a.Yes);
                    }
                    ReplacePlan plan;
                    string name = String.IsNullOrWhiteSpace(a.Name) ? composition.Package.Name : a.Name.Trim();
                    ReplaceMod mod = ReplacePlanner.PrepareAndInstall(composition, a.SevenZip, engine, name, false, null, out plan);
                    if (LoaderCostumePak.IsLoaderMod(mod)) UnownedSlotsFlag.Ensure(engine.GameRoot);
                    Console.WriteLine(ReplacePlanner.Report(plan));
                    Console.WriteLine("Installed '" + mod.Name + "' (id " + mod.Id.Substring(0, 8) + ") with the " + ReplaceMethods.Label(method) + ": " + ReplacePlanner.N(mod.Files.Count, "file") + ".");
                    return Status(engine, config);
                }
                case "replace-uninstall":
                {
                    ReplaceState state = engine.LoadState();
                    ReplaceMod mod = state.Mods.FirstOrDefault(m => m.Id.StartsWith(a.Target, StringComparison.OrdinalIgnoreCase) || String.Equals(m.Name, a.Target, StringComparison.OrdinalIgnoreCase));
                    if (mod == null) throw new IOException("No installed replacement matches '" + a.Target + "'.");
                    string message = WithDrift(a, delegate(DriftChoice c) { return engine.Uninstall(mod.Id, c); });
                    if (message == null) return 1;
                    Console.WriteLine("Uninstalled '" + mod.Name + "'. " + message);
                    return Status(engine, config);
                }
                case "replace-restore-original":
                {
                    string message = engine.RestoreReleasedOriginal(FluffyInstalls.Key(a.Target));
                    Console.WriteLine("Restored the original " + FluffyInstalls.Key(a.Target) + ". " + message);
                    return Status(engine, config);
                }
                case "manager-status": return FluffyStatus(engine);
                case "manager-uninstall":
                {
                    FluffyInstalls fluffy = engine.Fluffy();
                    var matches = fluffy.Mods.Where(m => (a.Root == null || String.Equals(Path.GetFullPath(m.Root).TrimEnd('\\'), a.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) && (String.Equals(m.Section, a.Target, StringComparison.OrdinalIgnoreCase) || String.Equals(m.Name, a.Target, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (matches.Count == 0) throw new IOException("No mod named '" + a.Target + "' is installed by an external mod manager (see manager-status).");
                    if (matches.Count > 1) throw new IOException("'" + a.Target + "' is installed in several mod manager folders: add --root <manager folder>\\Games\\SF6 (" + String.Join("; ", matches.Select(m => m.Root)) + ").");
                    string folder;
                    string message = engine.UninstallFluffy(matches[0].Root, matches[0].Section, out folder);
                    Console.WriteLine(message);
                    Console.WriteLine("Recoverable copy: " + folder + " (manager-restore \"" + matches[0].Name + "\" puts it back).");
                    return FluffyStatus(engine);
                }
                case "manager-restore":
                {
                    var removed = engine.ListFluffyRemoved();
                    FluffyRemoved match = removed.FirstOrDefault(r => String.Equals(Path.GetFullPath(r.Folder).TrimEnd('\\'), SafeFull(a.Target), StringComparison.OrdinalIgnoreCase))
                        ?? removed.FirstOrDefault(r => String.Equals(r.Name, a.Target, StringComparison.OrdinalIgnoreCase) || String.Equals(r.Record.Section, a.Target, StringComparison.OrdinalIgnoreCase));
                    if (match == null) throw new IOException("No mod uninstalled with manager-uninstall matches '" + a.Target + "' (see manager-status).");
                    Console.WriteLine(engine.RestoreFluffyRemoved(match.Folder));
                    return FluffyStatus(engine);
                }
                case "replace-reapply":
                    Console.WriteLine("Reapplied. " + engine.Reapply()); UnownedSlotsFlag.Ensure(engine.GameRoot);
                    return Status(engine, config);
                case "replace-restore-all":
                {
                    if (!a.Yes) throw new IOException("replace-restore-all uninstalls every replacement and restores the original game files. Add --yes to confirm.");
                    string message = WithDrift(a, delegate(DriftChoice c) { return engine.RestoreAll(c); });
                    if (message == null) return 1;
                    Console.WriteLine("Restored the original game files. " + message);
                    return Status(engine, config);
                }
                case "replace-vanilla":   // 1.0.1: gear → Restore → Restore game to vanilla
                {
                    if (!a.Yes) throw new IOException("replace-vanilla removes every mod this Manager and the external mod manager installed, all patch PAKs and the natives folder, with no Undo. Add --yes to confirm.");
                    Console.WriteLine(engine.RestoreVanilla());
                    return Status(engine, config);
                }
                case "replace-method":
                {
                    ReplaceState state = engine.LoadState();
                    string detail; LooseLoaderState loader = LooseLoader.State(engine.GameRoot, out detail);
                    Console.WriteLine("Current method: " + ReplaceMethods.Label(engine.CurrentMethod(state)) + (state.Mods.Count > 0 ? " (" + state.Mods.Count + " replacements installed)" : ""));
                    Console.WriteLine("Setting: " + (String.IsNullOrEmpty(engine.Settings.Method) ? "automatic (loose while REFramework's loose file loader is on, else the PAK method)" : ReplaceMethods.Label(engine.Settings.Method)));
                    Console.WriteLine("REFramework loose file loader: " + detail);
                    if (a.Set == null) return 0;
                    engine.Settings.Method = a.Set == "auto" ? "" : a.Set;
                    string target = engine.DesiredMethod();
                    if (target == ReplaceMethods.Loose && (state.Mods.Count > 0 || a.Set == ReplaceMethods.Loose)) RequireLoader(engine, a.Yes);
                    engine.Settings.Save();
                    if ((state.Mods.Count > 0 || state.Deployed.Count > 0) && target != engine.CurrentMethod(state)) { string message = engine.SwitchMethod(target); Console.WriteLine("Converted installed replacements to the " + ReplaceMethods.Label(target) + ". " + message); }
                    else Console.WriteLine("Method setting: " + (a.Set == "auto" ? "automatic (" + ReplaceMethods.Label(target) + " on this game)" : ReplaceMethods.Label(target)) + ".");
                    return Status(engine, config);
                }
            }
            throw new ArgumentException("Unknown command.");
        }

        private static string SafeFull(string path) { try { return Path.GetFullPath(path).TrimEnd('\\'); } catch { return path ?? ""; } }

        // 2.2.0: game files another tool changed: --restore-originals / --keep-current decide; without either the
        // files and their changers are listed and nothing changes (exit 1).
        private static string WithDrift(Args a, Func<DriftChoice, string> action)
        {
            DriftChoice choice = a.RestoreOriginals ? DriftChoice.RestoreOriginal : a.KeepCurrent ? DriftChoice.KeepCurrent : DriftChoice.Ask;
            try { return action(choice); }
            catch (DriftException ex)
            {
                Console.Error.WriteLine("Another tool changed game files this app installed; nothing was changed:");
                foreach (DriftItem item in ex.Items) Console.Error.WriteLine("  " + item.Text + (item.Kept ? " (kept earlier)" : ""));
                Console.Error.WriteLine("Run again with --restore-originals (the genuine game file comes back; the other file is kept in Data) or --keep-current (the other file stays; this app stops managing it and keeps the original restorable).");
                return null;
            }
        }

        // Fluffy instances, their installed mods and the Fluffy mods uninstalled here (read-only).
        private static int FluffyStatus(ReplaceEngine engine)
        {
            FluffyInstalls fluffy = engine.Fluffy();
            Console.WriteLine("External mod manager: " + (fluffy.Roots.Count == 0 ? "none detected next to the libraries" : String.Join("; ", fluffy.Roots)) + " · " + (GameProcesses.Probe.IsFluffyRunning() ? "running" : "closed"));
            foreach (string w in fluffy.Warnings) Console.WriteLine("  warning: " + w);
            foreach (string root in fluffy.Roots) if (!File.Exists(Path.Combine(root, "installed.ini"))) Console.WriteLine("  " + root + ": no installed.ini (nothing installed)");
            Console.WriteLine("Installed by the external mod manager (" + fluffy.Mods.Count + "):");
            foreach (FluffyMod m in fluffy.Mods)
            {
                Console.WriteLine("  [" + m.Section + "] " + m.Name + " · " + (String.IsNullOrEmpty(m.InstallType) ? "normal" : m.InstallType) + " · " + ReplacePlanner.N(m.Files.Count, "file") + " · " + (m.UninstallBlocker ?? "can be uninstalled here (manager-uninstall)"));
                foreach (string file in m.Files) { var owners = fluffy.OwnersOf(file); Console.WriteLine("      " + file + (owners.Count > 1 ? "  (also listed by " + String.Join(", ", owners.Where(o => o != m).Select(o => "'" + o.Name + "'")) + ")" : "")); }
            }
            var removed = engine.ListFluffyRemoved();
            Console.WriteLine("Uninstalled here, recoverable (" + removed.Count + "):");
            foreach (FluffyRemoved r in removed) Console.WriteLine("  " + r.Name + " · " + r.Record.RemovedUtc + " · " + r.Folder);
            return 0;
        }

        // The loose method needs REFramework's loader on; turning it on edits re2_fw_config.txt.
        private static void RequireLoader(ReplaceEngine engine, bool yes)
        {
            string detail; LooseLoaderState state = LooseLoader.State(engine.GameRoot, out detail);
            if (state == LooseLoaderState.Enabled) return;
            if (state == LooseLoaderState.ReFrameworkMissing) throw new IOException("The loose file loader method needs REFramework (dinput8.dll). Use --method pak, or install REFramework first.");
            if (!yes) throw new IOException("REFramework's loose file loader is off (" + detail + "). Add --yes to turn it on (recommended; re2_fw_config.txt is backed up first and only LooseFileLoader_Enabled changes), --no-loader to install with it off, or use --method pak.");
            LooseLoader.Write(engine.GameRoot, LooseLoader.EnabledKey, "true", engine.ConfigBackupDir);
            engine.Settings.LooseLoaderConsent = true; engine.Settings.Save();
            Console.WriteLine("Turned on REFramework's loose file loader (backup in " + engine.ConfigBackupDir + ").");
        }

        private static CompositionPlan Compose(Args a)
        {
            ModPackage package = PackageReader.Load(a.Source, a.SevenZip);
            List<string> selection;
            if (a.SelectionFile != null) selection = Json.Serializer().Deserialize<List<string>>(File.ReadAllText(a.SelectionFile, Encoding.UTF8));
            else selection = a.HasSelection ? a.Selected : Composer.DefaultSelection(package, true);
            CompositionPlan plan = Composer.Plan(package, selection, true);
            if (!plan.CanInstall) throw new IOException("The selection cannot be installed: " + String.Join(" ", plan.Errors) + " Run list-options to choose --select options.");
            return plan;
        }

        private static int Status(ReplaceEngine engine, AppConfig config)
        {
            if (engine.JournalPending) Console.WriteLine("Interrupted change pending: " + engine.JournalPath + " (it is completed or rolled back by the next change, or when the app starts).");
            ReplaceState state = engine.LoadState();
            Console.WriteLine("Game folder: " + engine.GameRoot);
            string basePath = Path.Combine(engine.GameRoot, "re_chunk_000.pak");
            if (File.Exists(basePath))
            {
                PakIndex p = PakIndex.Open(basePath);
                string tableSha;
                using (var sha = FastHash.Sha256()) tableSha = BitConverter.ToString(sha.ComputeHash(p.Table)).Replace("-", "");
                Console.WriteLine("Base PAK: KPKA " + p.Major + "." + p.Minor + ", features " + p.Features + ", " + p.Count + " entries, fingerprint 0x" + p.Fingerprint.ToString("X8") + ", zeroed entries " + p.CountZeroed());
                Console.WriteLine("  entry-table SHA-256 " + tableSha + " (prefix " + tableSha.Substring(0, 16) + "); header+table SHA-256 " + p.TocSha256().Substring(0, 16));
            }
            foreach (string dlc in Directory.Exists(Path.Combine(engine.GameRoot, "dlc")) ? Directory.GetFiles(Path.Combine(engine.GameRoot, "dlc"), "*.pak") : new string[0])
            {
                PakIndex p = PakIndex.Open(dlc); Console.WriteLine("DLC PAK " + Path.GetFileName(dlc) + ": " + p.Count + " entries, zeroed " + p.CountZeroed());
            }
            Console.WriteLine("natives folder: " + (Directory.Exists(Path.Combine(engine.GameRoot, "natives")) ? Directory.GetFiles(Path.Combine(engine.GameRoot, "natives"), "*", SearchOption.AllDirectories).Length + " files" : "absent"));
            string detail; LooseLoader.State(engine.GameRoot, out detail);
            ToolStatus tools = ToolStatus.Detect(engine.GameRoot);
            Console.WriteLine("Method: " + ReplaceMethods.Label(engine.CurrentMethod(state)) + " · " + detail);
            Console.WriteLine(tools.ReFrameworkDetail + " · " + tools.SlotsLoaderDetail + " · " + tools.CrashReporterState);
            // 2.4.0: the patch paks and which one SlotsLoader generated (by its marker, as the loader does).
            var patches = LoaderCostumePak.Scan(engine.GameRoot);
            // 1.0.11: a patch PAK no mod manager lists is a leftover (ReplaceLeftoverPaks.cs); the empty placeholder says so.
            var leftovers = new HashSet<string>(engine.LeftoverPaks().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("Patch PAKs: " + (patches.Count == 0 ? "none" : String.Join("; ", patches.Select(p => p.Name + " (" + (p.Kind == PatchPakKind.Costume ? (p.Placeholder ? "Slotsloader placeholder" : "generated by Slotsloader, " + p.Entries + " entries, " + Utilities.FriendlyBytes(p.Length)) : p.Kind == PatchPakKind.Stage ? "stage slots pak" : leftovers.Contains(p.Name) ? "leftover: no mod manager lists it, " + Utilities.FriendlyBytes(p.Length) : p.Kind == PatchPakKind.Mod && ReplaceEngine.IsFluffyDummy(p.Path) ? "empty placeholder" : p.Kind == PatchPakKind.Mod ? "another mod's, left alone" : "unreadable") + ")"))));
            FluffyInstalls fluffy = engine.Fluffy();
            Console.WriteLine("External mod manager: " + (fluffy.Roots.Count == 0 ? "none detected" : String.Join("; ", fluffy.Roots)) + " · " + ReplacePlanner.N(fluffy.Mods.Count, "mod") + " installed by it");
            Console.WriteLine("Replacements (" + state.Mods.Count + ", top wins):");
            int n = 1;
            foreach (ReplaceMod m in state.Mods) Console.WriteLine("  #" + (n++) + " " + m.Id.Substring(0, 8) + " " + (m.Enabled ? "on " : "off") + " " + m.Kind + " · " + m.Name + " · " + ReplacePlanner.N(m.Files.Count, "file") + (m.Test ? " · test" : ""));
            Console.WriteLine("Deployed files: " + state.Deployed.Count + " · invalidated PAK entries held: " + state.Deployed.Sum(d => d.Zeroed.Count) + " · originals backed up: " + state.Originals.Count);
            foreach (ReleasedFile r in state.Released) Console.WriteLine("Kept from another tool: " + r.Path + " (" + r.Changer + ")" + (r.Original != null ? " · original restorable: replace-restore-original \"" + r.Path + "\"" : ""));
            foreach (ForeignCopy c in state.ForeignCopies) Console.WriteLine("Other tool's file kept in Data: " + c.Path + " (" + c.Changer + ") · " + c.Copy);
            GameFilesStatus status = engine.Status();
            Console.WriteLine("App data: " + engine.DataDir);
            Console.WriteLine("Backups: " + status.BackupsRecorded + " recorded, " + status.BackupsMissing + " missing on disk" + (status.BackupsMissing > 0 ? " (first: " + status.FirstMissingBackup + ")" : "") + " · stored copies missing: " + status.StoredMissing + (status.StoredMissing > 0 ? " (first: " + status.FirstMissingStored + ")" : ""));
            Console.WriteLine("Relocation: " + RelocationText(status.Relocation));
            if (status.OrphanCostumePaks.Count > 0) Console.WriteLine(LoaderCostumePak.OrphanText + ": " + String.Join(", ", status.OrphanCostumePaks) + " (remove it: loader-pak-remove, or the Installed page's Remove it).");
            if (status.OrphanedRows > 0) Console.WriteLine(PakRepair.OrphanText(status.OrphanedRows) + ": " + String.Join(", ", status.OrphanedRowsWhere.Take(12)) + (status.OrphanedRowsWhere.Count > 12 ? ", …" : "") + " (repair: pak-repair, or the Installed page's Repair).");   // 2.4.2
            if (status.LeftoverPaks.Count > 0) Console.WriteLine(ReplaceEngine.LeftoverStatusText(status.LeftoverPaks) + " (remove: select it on the Installed page, then Remove PAK).");   // 1.0.11
            Console.WriteLine("Status: " + (status.Summary.Length == 0 ? "OK" : status.Summary) + (status.Details.Count > 1 ? " · " + String.Join(" · ", status.Details) : ""));
            return 0;
        }

        private static string RelocationText(RelocationReport r)
        {
            if (r == null || !r.Any) return "none (no recorded path points into another app folder's Data)";
            var parts = new List<string>();
            if (r.Rebased > 0) parts.Add(r.Rebased + " recorded path" + (r.Rebased == 1 ? "" : "s") + " from the previous app folder (" + String.Join(", ", r.OldRoots) + ") resolve in this Data folder; saved with the next change or when the app window opens");
            if (r.Missing > 0) parts.Add(r.Missing + " recorded path" + (r.Missing == 1 ? "" : "s") + " from a moved Data folder " + (r.Missing == 1 ? "exists" : "exist") + " in neither place (first: " + r.FirstMissing + ")");
            if (r.Foreign > 0) parts.Add(r.Foreign + " recorded path" + (r.Foreign == 1 ? "" : "s") + " still " + (r.Foreign == 1 ? "points" : "point") + " into another app folder's Data that exists (" + String.Join(", ", r.ForeignRoots) + "): this copy uses those files; delete or rename the old folder if the app was moved");
            return String.Join(" · ", parts);
        }

        private static int PresetMap(Args a)
        {
            AppConfig config = File.Exists(Path.Combine(AppConfig.DataDir, "settings.json")) ? AppConfig.Load() : AppConfig.Detected();
            var library = Library.Scan(config.Libraries, a.SevenZip);
            var map = FluffyPresets.Map(FluffyPresets.Parse(File.ReadAllText(a.Source)), library, a.SevenZip);
            foreach (PresetMapping m in map) Console.WriteLine((m.Entry == null ? "UNMATCHED " : "ok        ") + m.Sections[0].Section + " -> " + m.Message + (m.Entry == null ? "" : " [" + m.Kind + "]"));
            Console.WriteLine(map.Count(m => m.Entry != null) + " of " + map.Count + " sections mapped; " + FluffyPresets.Group(map).Count(g => g.Entry != null) + " installs. Read-only.");
            return 0;
        }

        private static object PlanJson(ReplacePlan plan)
        {
            return new Dictionary<string, object> {
                { "format", "sf6-replace-plan-v1" }, { "mod", plan.ModName }, { "source", plan.SourcePath }, { "can_install", plan.CanInstall }, { "method", plan.Method },
                { "affects", plan.Affects }, { "files", plan.Files.Select(f => new Dictionary<string, object> { { "path", f.Path }, { "bytes", f.Size }, { "sha256", f.Sha256 }, { "fixes", f.Fixes.Select(x => x.Message).ToList() } }).ToList() },
                { "vanilla_replaced", plan.VanillaReplaced }, { "new_files", plan.NewFiles }, { "root_files", plan.RootFiles }, { "rows_to_invalidate", plan.RowsToInvalidate },
                { "invalidation_preview", plan.InvalidationPreview }, { "items", plan.Items.Select(i => i.Level + ": " + i.Text).ToList() } };
        }
        private static void WriteJson(string path, object value)
        {
            if (path == "-") { Console.WriteLine(Json.Serializer().Serialize(value)); return; }
            if (File.Exists(path)) throw new IOException("Report already exists; choose a new --json path.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); Json.WriteNew(path, value);
        }

        // 2.3.1: the pinned SlotsLoader build, found by SHA-256 in the libraries or Data\downloads, else downloaded and
        // verified (unless --no-download). Never installs anything and never writes into a library.
        private static int LoaderFetch(Args a)
        {
            LoaderPin pin = LoaderRelease.Pin;
            List<string> libraries = a.Libraries.Count > 0 ? a.Libraries : (File.Exists(Path.Combine(AppConfig.DataDir, "settings.json")) ? AppConfig.Load() : AppConfig.Detected()).Libraries;
            Console.WriteLine("Pinned: Slotsloader " + pin.Version + " · " + pin.Asset + " · " + pin.Size + " bytes · SHA-256 " + pin.Sha256);
            Console.WriteLine("Release: " + pin.ReleasePage);
            Console.WriteLine("Libraries: " + (libraries.Count == 0 ? "(none)" : String.Join("; ", libraries)));
            int hashedBefore = LoaderRelease.HashedFiles;
            LoaderFile file = LoaderRelease.FindLocal(pin, libraries, LoaderRelease.DownloadDir);
            Console.WriteLine("Hashed " + (LoaderRelease.HashedFiles - hashedBefore) + " library file(s) of the pinned size.");
            if (file == null)
            {
                if (a.NoDownload) { Console.Error.WriteLine("Not found in the libraries or in " + LoaderRelease.DownloadDir + " (--no-download: nothing was downloaded)."); return 1; }
                Console.WriteLine("Not in the libraries or " + LoaderRelease.DownloadDir + "; downloading " + LoaderRelease.Description(pin) + "…");
                file = LoaderRelease.Download(pin, LoaderRelease.DownloadDir);
            }
            Console.WriteLine("Source: " + file.SourceLabel);
            Console.WriteLine("Path: " + file.Path);
            Console.WriteLine("SHA-256: " + file.Sha256 + " (matches the pin, " + file.Size + " bytes)");
            return 0;
        }

        // ---- 2.4.0 updates
        private static UpdateCheckResult CheckOrThrow(Args a)
        {
            bool pre = a.IncludePrereleases || UpdateSettings.Load().IncludePrereleases;
            UpdateCheckResult r = UpdateCheck.Check(pre);
            UpdateSettings s = UpdateSettings.Load(); s.LastCheckUtc = r.CheckedUtc; if (r.Ok) s.Last = r; s.Save();
            if (!r.Ok) throw new IOException("Could not check for updates: " + r.Reason + ".");
            return r;
        }
        private static string Describe(ReleaseOffer o) { return o == null ? "none published" : o.Version + (o.Prerelease ? " (pre-release)" : "") + " · " + o.Tag + " · " + o.Asset + " · " + o.Size + " bytes · SHA-256 " + o.Sha256 + " (" + o.DigestSource + ") · " + o.HtmlUrl; }
        private static int UpdateCheckCommand(Args a)
        {
            UpdateCheckResult r = CheckOrThrow(a);
            AppConfig config = File.Exists(Path.Combine(AppConfig.DataDir, "settings.json")) ? AppConfig.Load() : AppConfig.Detected();
            if (a.Game != null) config.GameRoot = a.Game;
            string installed = null, source = "";
            try { installed = InstalledLoader.Version(InstalledLoader.Record(ReplaceEngine.ForConfig(config).LoadState()), out source); } catch (Exception ex) { Program.Log(ex); }
            Console.WriteLine("Source: " + UpdateCheck.ApiUrl);
            Console.WriteLine("Slotsloader installed: " + (installed == null ? "not installed by this app" : installed + " (from the " + source + ")") + " · latest: " + Describe(r.Loader));
            Console.WriteLine("Slotsloader Manager installed: " + SafeHttp.AppVersion() + " · latest: " + Describe(r.Manager));
            foreach (string refused in r.Refused) Console.WriteLine("Refused: " + refused);
            ReleaseOffer loader = UpdateCheck.LoaderUpdate(r, installed), manager = UpdateCheck.ManagerUpdate(r, UpdateCheck.Running);
            Console.WriteLine(loader == null && manager == null ? "Up to date." : (loader != null ? "Slotsloader " + loader.Version + " is available (update-install loader). " : "") + (manager != null ? "Slotsloader Manager " + manager.Version + " is available (update-install manager)." : ""));
            return 0;
        }
        private static int UpdateInstallLoader(Args a, ReplaceEngine engine)
        {
            UpdateCheckResult r = CheckOrThrow(a);
            ReplaceMod record = InstalledLoader.Record(engine.LoadState());
            string source; string installed = InstalledLoader.Version(record, out source);
            LoaderPin pin;
            if (record == null) pin = UpdateCheck.InstallPin(r);
            else { ReleaseOffer offer = UpdateCheck.LoaderUpdate(r, installed); if (offer == null) { Console.WriteLine("Slotsloader is up to date (" + installed + ")."); return 0; } pin = offer.AsPin(); }
            Console.WriteLine((record == null ? "Install Slotsloader " : "Update Slotsloader " + installed + " -> ") + pin.Version + ": " + pin.Asset + " (" + pin.Size + " bytes) from " + pin.Url);
            if (!a.Yes) { Console.WriteLine("Nothing changed: add --yes to install."); return 1; }
            LoaderFile file = LoaderRelease.FindLocal(pin, new string[0], LoaderRelease.DownloadDir) ?? LoaderRelease.Download(pin, LoaderRelease.DownloadDir);
            a.Source = file.Path;
            CompositionPlan composition = Compose(a);
            ReplacePlan plan;
            ReplaceMod mod = ReplacePlanner.PrepareAndInstall(composition, a.SevenZip, engine, composition.Package.Name, false, record == null ? null : record.Id, out plan);
            UnownedSlotsFlag.Ensure(engine.GameRoot);
            Console.WriteLine(ReplacePlanner.Report(plan));
            Console.WriteLine((record == null ? "Installed" : "Updated") + " Slotsloader " + pin.Version + " (id " + mod.Id.Substring(0, 8) + ", " + ReplacePlanner.N(mod.Files.Count, "file") + ").");
            return 0;
        }
        private static int UpdateInstallManager(Args a)
        {
            if (!String.Equals(a.Target, "manager", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("update-install needs loader or manager.");
            UpdateCheckResult r = CheckOrThrow(a);
            ReleaseOffer offer = UpdateCheck.ManagerUpdate(r, UpdateCheck.Running);
            if (offer == null) { Console.WriteLine("Slotsloader Manager is up to date (" + SafeHttp.AppVersion() + ")."); return 0; }
            Console.WriteLine("Update Slotsloader Manager " + SafeHttp.AppVersion() + " -> " + offer.Version + ": " + offer.Asset + " (" + offer.Size + " bytes) from " + offer.Url);
            if (!a.Yes) { Console.WriteLine("Nothing changed: add --yes to install."); return 1; }
            string zip;
            try { zip = SafeHttp.Download(offer.Url, LoaderRelease.DownloadDir, offer.Asset, offer.Size, offer.Sha256, "Downloading Slotsloader Manager " + offer.Version, "published"); }
            catch (DownloadFailure f) { throw new IOException("Couldn't download Slotsloader Manager " + offer.Version + ": " + f.Reason + "."); }
            string staged = AppUpdater.Stage(zip, offer.Version, AppConfig.DataDir);
            AppUpdater.Apply(MainForm.AppDir, staged, AppUpdater.PreviousDir(AppConfig.DataDir), SafeHttp.AppVersion(), offer.Version, AppConfig.DataDir);
            Console.WriteLine("Updated to Slotsloader Manager " + offer.Version + " in " + MainForm.AppDir + ". Start it again; the previous version is kept (update-rollback).");
            return 0;
        }

        internal static void Help()
        {
            Console.WriteLine("Replace original commands — same engine and safety checks as the app window:");
            Console.WriteLine("  replace-plan <source> [--select <id> ...] [--game <SF6>] [--method loose|pak] [--json <new.json|->]   read-only health check");
            Console.WriteLine("  replace-install <source> [--select <id> ...] [--name <name>] --game <SF6> [--method loose|pak] [--yes | --no-loader]   loose method, loader off, natives files: --yes turns it on, --no-loader installs without; tool-only mods need neither");
            Console.WriteLine("  replace-status [--game <SF6>]        read-only: base PAK table hash, zeroed entries, natives folder, installed replacements, missing backups, moved-folder paths");
            Console.WriteLine("  replace-uninstall <id|name> --game <SF6> [--restore-originals | --keep-current]   when another tool changed its files");
            Console.WriteLine("  replace-reapply --game <SF6>         after a game update or a Steam file check");
            Console.WriteLine("  replace-restore-all --game <SF6> --yes [--restore-originals | --keep-current]");
            Console.WriteLine("  replace-restore-original <path> --game <SF6>   the genuine game file behind a file kept with --keep-current");
            Console.WriteLine("  manager-status [--game <SF6>]        read-only: external mod manager folders, the mods they installed (files, shared files), mods uninstalled here");
            Console.WriteLine("  manager-uninstall <name|section> [--root <manager folder>\\Games\\SF6]   uninstall a mod an external mod manager installed, as it would (manager closed; PAK installs too: their game entries are restored, each proven)");
            Console.WriteLine("  manager-restore <name|folder>        undo a manager-uninstall: files, the manager's backups and the installed.ini entry come back");
            Console.WriteLine("  replace-method [--game <SF6>] [--set loose|pak|auto] [--yes]   --yes allows turning on REFramework's loose file loader");
            Console.WriteLine("  preset-map <file.prt>                read-only: map an external preset (.prt) to the configured libraries");
            Console.WriteLine("  loader-pak-remove [--game <SF6>]     remove the costume pak Slotsloader generated when Slotsloader is not installed (a placeholder keeps the patch row unbroken)");
            Console.WriteLine("  variants [--game <SF6>]              read-only: each archive or folder in costume_mods with variants (Slotsloader's variants.json), on/off and the slot each got");
            Console.WriteLine("  variants [--game <SF6>] --set \"<source>\" \"<label>\" on|off   turn one variant on or off (variant_choices.json; applies at the next SF6 launch)");
            Console.WriteLine("  pak-repair [--game <SF6>] [--yes]    game entries another tool left invalidated: lists each with its proof (dry run); --yes restores the proven ones");
            Console.WriteLine("  update-check [--include-prereleases]   installed vs latest Slotsloader and Slotsloader Manager (GitHub releases), with the asset, size and SHA-256");
            Console.WriteLine("  update-install loader|manager [--yes] [--include-prereleases]   download, verify and install the newer release (loader: in place, in Replace mode)");
            Console.WriteLine("  update-rollback                      put back the Slotsloader Manager files kept before the last update");
            Console.WriteLine("  loader-fetch [--library <dir> ...] [--no-download]   the pinned Slotsloader " + LoaderRelease.Pin.Version + " zip: found by SHA-256 in the libraries or Data\\downloads, else downloaded from GitHub and verified; prints the source, path and hash (installs nothing)");
            Console.WriteLine("Changes refuse while SF6 runs (loose natives files excepted); PAK-method changes and manager-uninstall/-restore refuse while the external mod manager runs. The app's Data folder beside this executable is used.");
        }
    }
}
