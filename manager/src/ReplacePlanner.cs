using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // Prepares a composed selection for Replace-original installs: normalizes paths,
    // applies the compatibility fixes, and reports what the install would change.
    internal static class ReplacePlanner
    {
        private static readonly Regex ModelFolder = new Regex(@"^natives/stm/(?:streaming/)?product/model/esf/esf(\d{3})/(\d{3})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VersionSuffix = new Regex(@"^(?<stem>.+\.(?<ext>[a-z0-9]+))\.(?<ver>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "1 file" / "2 files"; 2.4.0: "1 PAK entry" / "2 PAK entries", "1 copy" / "2 copies", "1 mesh" / "2 meshes".
        internal static string N(long n, string noun) { return n + " " + (n == 1 ? noun : Plural(noun)); }
        // 2.4.1: "the same mod" = the same source archive or folder (full path, case-insensitive).
        internal static bool SameSource(string a, string b)
        {
            if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b)) return false;
            try { return String.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
        // "all 3 of its files", or "its only file" for one.
        internal static string AllOfIts(int files) { return files == 1 ? "its only file" : "all " + files + " of its files"; }
        internal static string Plural(string noun)
        {
            if (String.IsNullOrEmpty(noun)) return noun;
            int i = noun.Length - 1;
            if (noun[i] == 'y' && i > 0 && "aeiou".IndexOf(noun[i - 1]) < 0) return noun.Substring(0, i) + "ies";
            if (noun.EndsWith("sh") || noun.EndsWith("ch") || noun.EndsWith("x") || noun.EndsWith("ss")) return noun + "es";
            return noun + "s";
        }

        // Stages the selection into `staging` (inside the store for installs, a scratch folder for checks).
        internal static ReplacePlan Prepare(CompositionPlan composition, string sevenZip, ReplaceEngine engine, string staging)
        {
            return Prepare(composition, sevenZip, engine, staging, null, null);
        }

        // cache: the automatic check keeps extracted and fixed files per package, so a re-check with the
        // same files never extracts again. updateId: the replacement being updated in place (keeps its priority).
        internal static ReplacePlan Prepare(CompositionPlan composition, string sevenZip, ReplaceEngine engine, string staging, PrepareCache cache, string updateId)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (composition == null) throw new ArgumentNullException("composition");
            var plan = new ReplacePlan { ModName = composition.Package.Name, SourcePath = composition.Package.SourcePath, SelectedIds = composition.Layers.Select(c => c.Id).ToList(), StagingFolder = staging };
            foreach (string e in composition.Errors) plan.Items.Add(new HealthItem("error", e));
            foreach (string w in composition.Warnings) plan.Items.Add(new HealthItem("info", w));
            if (composition.Package.RootFilesIgnored > 0) plan.Items.Add(new HealthItem("info", "Skipped " + N(composition.Package.RootFilesIgnored, "file") + " outside the mod's option folders."));
            // 1.0.1: only the Manager's own Slotsloader (LoaderCostumePak.IsUpstreamBuild).
            PlannedFile plugin = composition.Files.Values.FirstOrDefault(f => String.Equals(PakHash.Normalize(f.Asset.DestinationPath).TrimStart('/'), LoaderCostumePak.PluginPath, StringComparison.OrdinalIgnoreCase));
            if (plugin != null && plugin.Component != null && LoaderCostumePak.IsUpstreamBuild(plugin.Component.Name, plugin.Component.Version, plugin.Component.Author))
                plan.Items.Add(new HealthItem("error", "This is upstream's Slots Loader" + (String.IsNullOrEmpty(plugin.Component.Version) ? "" : " " + plugin.Component.Version) + ", not the Manager's Slotsloader Fork. Install the fork instead: Settings → Install Slotsloader " + UpdateCheck.ShortVersion(LoaderRelease.Pin.Version) + "."));
            ReplaceState state = engine.LoadState();
            plan.Method = engine.CurrentMethod(state);
            GamePaks paks = GamePaks.Load(engine.GameRoot);
            GameReference reference = GameReference.Load(engine.GameRoot, paks.Base, paks.Dlc);
            reference.SwapRepairs = engine.Settings.SwapRepairs;   // 1.0.5: Settings, off by default
            FluffyInstalls fluffy = engine.Fluffy();
            string externalLock = plugin == null ? ReplaceEngine.ExternalLockReason(fluffy) : null;   // 1.0.1: Slotsloader still installs
            if (externalLock != null) plan.Items.Add(new HealthItem("error", externalLock));
            paks.SetModPaks(state.Mods);   // 2.5.7: installed PAK mods are mods, not the game's patch PAKs
            if (reference.CurrentCrcs.Count == 0) plan.Items.Add(new HealthItem("warn", "Couldn't read the game's colour-file signatures (" + reference.CrcSource + "); outdated colour files are not fixed."));

            // 1. Destinations: lower-case natives paths, old texture suffixes upgraded.
            var chosen = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var modPaks = new List<PlannedFile>();   // 2.5.6: PAK mods (ReplacePakMods.cs)
            foreach (PlannedFile f in composition.Files.Values)
            {
                if (ReplaceEngine.IsModPak(f.Asset.DestinationPath)) { modPaks.Add(f); continue; }
                string dest = PakHash.Normalize(f.Asset.DestinationPath).TrimStart('/');
                bool native = dest.StartsWith("natives/", StringComparison.OrdinalIgnoreCase);
                if (native) dest = ModFixes.NormalizeNativePath(dest);
                string upgraded = native ? ModFixes.UpgradeTexSuffix(dest, reference.TexVersion) : null;
                if (upgraded == null && native) upgraded = UpgradeVersionSuffix(dest, reference, paks, state);
                if (upgraded != null)
                {
                    if (composition.Files.Keys.Any(k => String.Equals(ModFixes.NormalizeNativePath(k), upgraded, StringComparison.OrdinalIgnoreCase))) { plan.Items.Add(new HealthItem("info", "Skipped " + Path.GetFileName(dest) + ": the mod also ships the current-version file.")); continue; }
                    renamed[upgraded] = dest; dest = upgraded;
                }
                if (ReplaceEngine.SkippedProgram(dest, engine.GameRoot)) { plan.Items.Add(new HealthItem("info", "Skipped " + dest + ": programs aren't installed into the game folder.")); continue; }
                try { ReplaceEngine.ValidateDestination(dest); }
                catch (IOException ex) { plan.Items.Add(new HealthItem("error", ex.Message)); continue; }
                if (chosen.ContainsKey(dest)) { plan.Items.Add(new HealthItem("warn", "Two selected files map to " + dest + "; the later option wins.")); }
                chosen[dest] = new PlannedFile { Component = f.Component, Asset = new NativeAsset { EntryPath = f.Asset.EntryPath, DestinationPath = dest, Size = f.Asset.Size } };
            }
            // 2.5.6: each .pak becomes the next patch PAK, after every patch number in use (the game reads them in order).
            if (modPaks.Count > 0)
            {
                int number = engine.NextPatchNumber(state, updateId);
                // 2.5.7: reinstalling or updating an installed PAK mod (same source, or the record being updated) keeps its numbers.
                List<string> reuse = state.Mods.Where(m => SameSource(m.SourcePath, plan.SourcePath) || (!String.IsNullOrEmpty(updateId) && String.Equals(m.Id, updateId, StringComparison.OrdinalIgnoreCase)))
                    .SelectMany(m => m.Files).Select(x => x.Path).Where(ReplaceEngine.IsPatchPakPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(ReplaceEngine.PatchNumber).ToList();
                int reused = 0;
                foreach (PlannedFile f in modPaks.OrderBy(x => x.Asset.DestinationPath, StringComparer.OrdinalIgnoreCase))
                {
                    bool same = reused < reuse.Count;
                    string dest = same ? reuse[reused++] : ReplaceEngine.PatchPakName(number++);
                    chosen[dest] = new PlannedFile { Component = f.Component, Asset = new NativeAsset { EntryPath = f.Asset.EntryPath, DestinationPath = dest, Size = f.Asset.Size } };
                    plan.Items.Add(new HealthItem("info", Path.GetFileName(f.Asset.DestinationPath) + (same ? " replaces its installed copy " + dest + " in place." : " is added as " + dest + ", after the patch PAKs already there. A PAK made for an older game version can stop SF6 from starting: uninstalling it puts the game back.")));
                }
            }
            int texRenamed = renamed.Keys.Count(k => k.Contains(".tex."));
            if (texRenamed > 0) plan.Items.Add(new HealthItem("fix", N(texRenamed, "texture") + " renamed from .tex.143230113 to .tex." + reference.TexVersion + " (current game version)."));
            if (renamed.Count > texRenamed) plan.Items.Add(new HealthItem("fix", (renamed.Count - texRenamed == 1 ? "1 mesh" : (renamed.Count - texRenamed) + " meshes") + " from an older game version renamed to the current .mesh suffix."));

            // 2. Copy and fix.
            Dictionary<string, string> hashes = null;
            Dictionary<string, PreparedFile> cached = null;
            bool useCache = cache != null && cache.Usable(composition.Package.SourcePath);
            using (var reader = new SourceReader(composition.Package.SourcePath, sevenZip, composition.Package.PreviewEntries, composition.Package.PreviewSourceStamp))
            {
                foreach (PlannedFile f in chosen.Values)
                {
                    SourceEntry entry;
                    if (!reader.Entries.TryGetValue(f.Asset.EntryPath, out entry) || entry.Size != f.Asset.Size) throw new SourceChangedException();
                }
                if (useCache) cached = cache.Stage(chosen, reader, reference, ReferenceKey(reference));
                else
                {
                    Directory.CreateDirectory(staging);
                    string scratch = Path.Combine(staging, ".scratch-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(scratch);
                    try { hashes = reader.CopyFiles(chosen.Values, staging, scratch); }
                    finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { } }
                }
            }
            int fixedFiles = 0;
            if (cached != null)
                foreach (var pair in chosen.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    PreparedFile prepared = cached[pair.Key];
                    if (prepared.Fixes.Any(x => x.Changed)) fixedFiles++;
                    plan.Files.Add(prepared);
                }
            else foreach (var pair in chosen.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                OperationContext.ThrowIfCancellationRequested();
                string staged = Safety.Under(staging, pair.Key);
                var prepared = new PreparedFile { Path = pair.Key, Source = staged, EntryPath = pair.Value.Asset.EntryPath, Component = pair.Value.Component.Name, Size = new FileInfo(staged).Length, Sha256 = hashes[pair.Key] };
                string low = pair.Key.ToLowerInvariant();
                List<FixResult> quick;
                if (prepared.Native && low.Contains(".tex.") && ModFixes.TextureFileOk(staged, out quick)) prepared.Fixes = quick;   // 1.0.1: header only
                else if (prepared.Native && (low.Contains(".user.") || low.Contains(".tex.") || (reference != null && reference.SwapRepairs && low.EndsWith(".scn.20"))) && prepared.Size <= 256L * 1024 * 1024)
                {
                    byte[] data = File.ReadAllBytes(staged);
                    prepared.Fixes = ModFixes.UpgradeModFile(ref data, reference, pair.Key);
                    if (prepared.Fixes.Any(x => x.Changed))
                    {
                        File.WriteAllBytes(staged, data);
                        prepared.Sha256 = ReplaceEngine.Sha(data); prepared.Size = data.Length; fixedFiles++;
                    }
                }
                plan.Files.Add(prepared);
            }
            plan.Fixed = fixedFiles + renamed.Count;
            foreach (var group in plan.Files.SelectMany(f => f.Fixes.Select(x => new { File = f, Fix = x })).GroupBy(x => x.Fix.Kind))
            {
                var first = group.First();
                string level = group.Any(x => x.Fix.Warning) ? "warn" : "fix";
                string text = group.Key == "crc" ? N(group.Count(), "colour file") + " with an outdated type signature updated (colours now load)."
                    : group.Key == "layout" ? N(group.Count(), "colour file") + " from 2023 converted to the current layout."
                    : group.Key == "mip" ? N(group.Count(), "texture") + " with a wrong mip-table row pitch repaired."
                    : group.Key == "badmip" ? N(group.Count(), "texture") + " " + (group.Count() == 1 ? "has" : "have") + " a broken mip table that could not be repaired (" + Path.GetFileName(first.File.Path) + (group.Count() > 1 ? " …" : "") + "); " + (group.Count() == 1 ? "it" : "they") + " may not load."
                    : group.Key == "stale" ? first.Fix.Message + " (" + group.Count() + " file" + (group.Count() == 1 ? "" : "s") + ", e.g. " + Path.GetFileName(first.File.Path) + ")."
                    : group.Key == "swatch" ? N(group.Count(), "outdated colour swatch file") + " rebuilt for the current game (the mod's swatch colours kept)."   // 2.5.5
                    : group.Key == "swatch-stale" ? first.Fix.Message + " (" + Path.GetFileName(first.File.Path) + (group.Count() > 1 ? " …" : "") + ")."
                    : first.Fix.Message;
                plan.Items.Add(new HealthItem(level, text));
            }

            // 3. What it replaces, conflicts and likely mistakes.
            var affects = new List<string>(); var newUnknownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int newOther = 0;
            foreach (PreparedFile f in plan.Files)
            {
                plan.Bytes += f.Size;
                if (!f.Native) { plan.RootFiles++; continue; }
                Match m = ModelFolder.Match(f.Path);
                if (m.Success) { string label = reference.DescribeFolder(m.Groups[1].Value, m.Groups[2].Value); if (!affects.Contains(label)) affects.Add(label); }
                PakIndex modPak = paks.ModPakContaining(f.Path);   // 2.5.7: an installed PAK mod holds it too: it wins in game
                if (modPak != null)
                {
                    ReplaceMod pakOwner = state.Mods.FirstOrDefault(mod => mod.Files.Any(x => String.Equals(Path.GetFileName(x.Path), Path.GetFileName(modPak.Path), StringComparison.OrdinalIgnoreCase)));
                    plan.Items.Add(new HealthItem("warn", Path.GetFileName(f.Path) + " is also in the PAK mod '" + (pakOwner == null ? Path.GetFileName(modPak.Path) : pakOwner.Name) + "' (" + Path.GetFileName(modPak.Path) + "), which wins in game while it's installed."));
                }
                PakIndex patch = paks.PatchContaining(f.Path);
                if (patch != null) plan.Items.Add(new HealthItem(plan.Method == ReplaceMethods.Pak ? "error" : "warn", f.Path + " is also in the patch PAK " + Path.GetFileName(patch.Path) + (plan.Method == ReplaceMethods.Pak ? ", which is never modified: the PAK method can't replace it." : "; the loose file loader may still prefer the loose copy.")));
                var rows = paks.Rows(f.Path);
                DeployedFile already = state.Deployment(f.Path);
                int heldRows = already == null ? 0 : already.Zeroed.Count;
                if (rows.Count + heldRows > 0)
                {
                    plan.VanillaReplaced++; plan.RowsToInvalidate += rows.Count + heldRows;
                    if (plan.InvalidationPreview.Count < 200) foreach (var r in rows) plan.InvalidationPreview.Add(Path.GetFileName(r.Key.Path) + " #" + r.Value + " " + f.Path);
                }
                else
                {
                    plan.NewFiles++;
                    if (m.Success && m.Groups[2].Value != "000" && !reference.CostumeByFolder.ContainsKey("esf" + m.Groups[1].Value + "/" + m.Groups[2].Value)) newUnknownFolders.Add("esf" + m.Groups[1].Value + "/" + m.Groups[2].Value);
                    else newOther++;
                    Match v = VersionSuffix.Match(f.Path);
                    string current;
                    if (v.Success && reference.Suffixes.TryGetValue(v.Groups["ext"].Value, out current) && current != v.Groups["ver"].Value && IsVanilla(paks, state, v.Groups["stem"].Value + "." + current))
                        plan.Items.Add(new HealthItem("warn", Path.GetFileName(f.Path) + " is ignored: the game loads ." + v.Groups["ext"].Value + "." + current + ", not ." + v.Groups["ext"].Value + "." + v.Groups["ver"].Value + "."));
                }
            }
            plan.Affects = affects;
            foreach (string folder in newUnknownFolders)
                plan.Items.Add(new HealthItem("warn", folder + " is not a vanilla outfit folder, so a replacement there never loads. Use Extra outfit mode, or check the folder number."));
            if (newOther > 0) plan.Items.Add(new HealthItem("info", N(newOther, "new file") + " the game doesn't ship (custom textures or materials the mod uses)."));

            // Streaming twins: a replaced texture whose high-resolution copy the mod does not ship.
            var provided = new HashSet<string>(plan.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var twins = plan.Files.Where(f => f.Native && f.Path.StartsWith("natives/stm/product/", StringComparison.OrdinalIgnoreCase) && f.Path.Contains(".tex."))
                .Select(f => "natives/stm/streaming/" + f.Path.Substring("natives/stm/".Length)).Where(t => !provided.Contains(t) && IsVanilla(paks, state, t)).ToList();
            if (twins.Count > 0) plan.Items.Add(new HealthItem("warn", N(twins.Count, "replaced texture") + " " + (twins.Count == 1 ? "has" : "have") + " a high-res streaming copy the mod doesn't ship (e.g. " + Path.GetFileName(twins[0]) + "); up close, SF6 may show the original texture."));

            // Fluffy ownership, one line per Fluffy mod (named, so the footer shows which one).
            foreach (var group in plan.Files.Select(f => new { File = f, Owner = state.Deployment(f.Path) == null ? fluffy.Owner(f.Path) : null }).Where(x => x.Owner != null).GroupBy(x => x.Owner.Name, StringComparer.OrdinalIgnoreCase))
            {
                int count = group.Count();
                plan.Items.Add(new HealthItem("error", (count == 1 ? "1 file belongs" : count + " files belong") + " to the mod '" + group.Key + "' (e.g. " + Path.GetFileName(group.First().File.Path) + "), installed by an external mod manager. Disable it there, then check again."));
            }
            // Loose files already in the game folder that neither this app nor Fluffy installed.
            plan.UnknownLoose = plan.Files.Where(f => f.Native && state.Deployment(f.Path) == null && fluffy.Owner(f.Path) == null && File.Exists(engine.GamePath(f.Path))).Select(f => f.Path).ToList();
            if (plan.UnknownLoose.Count > 0)
                plan.Items.Add(new HealthItem("info", (plan.UnknownLoose.Count == 1 ? "1 file is" : plan.UnknownLoose.Count + " files are") + " already in the game folder, not installed by the Manager (e.g. " + Path.GetFileName(plan.UnknownLoose[0]) + "): backed up now, put back on uninstall."));
            // Other installed replacements: same files, same outfit or both.
            plan.ConflictList = AnalyzeConflicts(plan.Files.Select(f => f.Path), state, updateId, reference, plan.SourcePath, plan.SelectedIds);
            plan.Conflicts = plan.ConflictList.Where(x => x.Enabled).Sum(x => x.Shared);
            foreach (ReplaceConflict conflict in plan.ConflictList) plan.Items.Add(new HealthItem(conflict.Level, conflict.Text));
            // 2.4.1: one neutral line for the switch (another variant of this mod replaces the installed one in place).
            // The record being updated (Update, a SlotsLoader from another zip) is named too: the line says what happens.
            List<ReplaceMod> sameSource = state.Mods.Where(m => SameSource(m.SourcePath, plan.SourcePath)).ToList();
            ReplaceMod updating = String.IsNullOrEmpty(updateId) ? null : state.Find(updateId);
            if (updating != null && !sameSource.Contains(updating)) sameSource.Insert(0, updating);
            if (sameSource.Count > 0) plan.Items.Add(new HealthItem("info", "Replaces installed: " + String.Join(", ", sameSource.Select(m => m.Name)) + (sameSource.Count > 1 ? " (they become one)" : "")));

            // Root/tool files.
            if (plan.RootFiles > 0)
            {
                int existing = plan.Files.Count(f => !f.Native && File.Exists(engine.GamePath(f.Path)) && state.Deployment(f.Path) == null);
                plan.Items.Add(new HealthItem("info", (plan.RootFiles == 1 ? "1 file goes" : plan.RootFiles + " files go") + " to the game folder (" + String.Join(", ", plan.Files.Where(f => !f.Native).Select(f => f.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Take(4)) + ")." + (existing > 0 ? " " + (existing == 1 ? "1 existing file is" : existing + " existing files are") + " backed up and restored on uninstall." : "")));
                if (plan.Files.Any(f => f.Path.Equals("amd_ags_x64.dll", StringComparison.OrdinalIgnoreCase))) plan.Items.Add(new HealthItem("info", "Slotsloader: the game's own amd_ags_x64.dll is backed up and restored on uninstall."));
                if (plan.Files.Any(f => f.Path.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase))) plan.Items.Add(new HealthItem("info", "REFramework: any existing dinput8.dll is backed up and restored on uninstall."));
            }
            plan.Kind = plan.RootFiles == 0 ? "Replacement" : plan.RootFiles == plan.Files.Count ? "Tool" : "Mixed";
            if (plan.Files.Count > 0 && plan.Files.All(x => ReplaceEngine.IsPatchPakPath(x.Path))) plan.Kind = "Pak";   // 2.5.6
            if (plan.Files.Any(f => f.Native))
            {
                string detail; LooseLoaderState loader = LooseLoader.State(engine.GameRoot, out detail);
                if (plan.Method == ReplaceMethods.Loose && loader != LooseLoaderState.Enabled)
                    // 2.4.4: the app never installs loose files the game would ignore; it uses the PAK method instead.
                    plan.Items.Add(new HealthItem("info", (loader == LooseLoaderState.ReFrameworkMissing ? "REFramework not installed" : loader == LooseLoaderState.Unreadable ? "REFramework config unreadable" : "Loose file loader off")
                        + ": the app installs these with the PAK method instead."));
                else plan.Items.Add(new HealthItem("info", plan.Method == ReplaceMethods.Pak ? "Method: PAK method · " + N(plan.RowsToInvalidate, "PAK entry") + " to invalidate (restored exactly on uninstall)." : "Method: REFramework loose file loader · no PAK changes."));
            }
            if (plan.Files.Count == 0) plan.Items.Add(new HealthItem("error", "The selection has no files to install."));
            plan.Milliseconds = clock.ElapsedMilliseconds;
            return plan;
        }

        // Fluffy renames mesh files from old (beta) SF6 versions: only when the current-version
        // path is a vanilla file, so a new custom file is never renamed onto an unrelated path.
        private static string UpgradeVersionSuffix(string dest, GameReference reference, GamePaks paks, ReplaceState state)
        {
            Match v = VersionSuffix.Match(dest);
            string current;
            if (!v.Success || !v.Groups["ext"].Value.Equals("mesh", StringComparison.OrdinalIgnoreCase) || !reference.Suffixes.TryGetValue("mesh", out current) || current == v.Groups["ver"].Value) return null;
            string candidate = v.Groups["stem"].Value + "." + current;
            return IsVanilla(paks, state, candidate) && !IsVanilla(paks, state, dest) ? candidate : null;
        }

        // In the game: in a base/DLC PAK, or invalidated there by one of our replacements.
        private static bool IsVanilla(GamePaks paks, ReplaceState state, string path)
        {
            if (paks.Vanilla(path)) return true;
            DeployedFile d = state.Deployment(path);
            return d != null && d.Zeroed.Count > 0;
        }

        // Health check: prepare into a scratch folder and delete it afterwards.
        internal static ReplacePlan Check(CompositionPlan composition, string sevenZip, ReplaceEngine engine) { return Check(composition, sevenZip, engine, null, null); }
        internal static ReplacePlan Check(CompositionPlan composition, string sevenZip, ReplaceEngine engine, PrepareCache cache, string updateId)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "sf6-health-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try { return Prepare(composition, sevenZip, engine, scratch, cache, updateId); }
            finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { } }
        }

        internal static bool IsReFrameworkComposition(CompositionPlan composition)
        {
            return composition != null && composition.Files.Keys.Any(k => String.Equals(PakHash.Normalize(k).TrimStart('/'), "dinput8.dll", StringComparison.OrdinalIgnoreCase));
        }
        // 1.0.8: the record a REFramework install replaces in place: the one whose dinput8.dll is in the game (highest
        // priority), when it and the new package are plain REFramework (dinput8.dll and its revision note). A mod that
        // bundles dinput8.dll with scripts or plugins is never replaced: the new build goes above it, and the bundle's
        // files stay (its dinput8.dll comes back if the new one is uninstalled). Null = a normal install.
        private static readonly string[] ReFrameworkFiles = { "dinput8.dll", "reframework_revision.txt" };
        private static bool PlainReFramework(IEnumerable<string> paths)
        {
            return paths.All(p => ReFrameworkFiles.Contains(PakHash.Normalize(p ?? "").TrimStart('/'), StringComparer.OrdinalIgnoreCase));
        }
        internal static string ReFrameworkUpdateTarget(IEnumerable<ReplaceMod> mods, IEnumerable<string> newFiles)
        {
            if (!PlainReFramework(newFiles)) return null;
            ReplaceMod refw = mods.FirstOrDefault(m => m.Files != null && m.Files.Any(x => String.Equals(PakHash.Normalize(x.Path ?? "").TrimStart('/'), "dinput8.dll", StringComparison.OrdinalIgnoreCase)));
            return refw != null && PlainReFramework(refw.Files.Select(x => x.Path)) ? refw.Id : null;
        }
        internal static bool IsLoaderComposition(CompositionPlan composition)
        {
            return composition != null && composition.Files.Keys.Any(k => String.Equals(PakHash.Normalize(k).TrimStart('/'), LoaderCostumePak.PluginPath, StringComparison.OrdinalIgnoreCase));
        }

        // Install: prepare inside the store, then publish through the engine.
        internal static ReplaceMod PrepareAndInstall(CompositionPlan composition, string sevenZip, ReplaceEngine engine, string name, bool test, string replaceId, out ReplacePlan plan)
        {
            string root = engine.StoreRoot(engine.LoadState());
            Directory.CreateDirectory(root);
            string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            plan = null;
            // 2.4.1: installing a mod that is already installed (same source archive or folder, any options) is a switch:
            // the highest-priority record is replaced in place (priority, enabled state and method kept) and any other
            // records of that source are uninstalled recoverably, so one record remains. Test installs never collapse.
            List<ReplaceMod> others = new List<ReplaceMod>();
            if (!test)
            {
                List<ReplaceMod> same = engine.LoadState().Mods.Where(m => SameSource(m.SourcePath, composition.Package.SourcePath)).ToList();
                if (String.IsNullOrEmpty(replaceId) && same.Count > 0) replaceId = same[0].Id;
                // SlotsLoader from another zip (a newer build, a renamed copy) updates the installed SlotsLoader in place:
                // one loader record, and the backup of the game's own amd_ags_x64.dll stays with it.
                if (String.IsNullOrEmpty(replaceId) && IsLoaderComposition(composition))
                {
                    ReplaceMod loader = engine.LoadState().Mods.FirstOrDefault(LoaderCostumePak.IsLoaderMod);
                    if (loader != null) replaceId = loader.Id;
                }
                // 1.0.8: another REFramework build (a newer pinned nightly) updates the REFramework this app installed in place.
                if (String.IsNullOrEmpty(replaceId) && IsReFrameworkComposition(composition))
                    replaceId = ReFrameworkUpdateTarget(engine.LoadState().Mods, composition.Files.Keys);
                string keep = replaceId;
                others = same.Where(m => !String.Equals(m.Id, keep, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            try
            {
                plan = Prepare(composition, sevenZip, engine, staging, null, replaceId);
                if (!plan.CanInstall) throw new IOException(String.Join(" ", plan.Errors));
                ReplaceMod installed = engine.Install(plan, name, test, replaceId);
                foreach (ReplaceMod extra in others) engine.UninstallForGood(extra.Id, DriftChoice.Ask, null);   // 1.0.1: no kept copy
                return installed;
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
        }

        // Game facts that change what the fixers write; part of every cache key.
        internal static string ReferenceKey(GameReference r)
        {
            return (r.SwapRepairs ? "swap1|" + r.OfficialStamp : "swatch1") + "|" + r.TexVersion + "|" + r.CrcSource + "|" + String.Join(",", r.CurrentCrcs.OrderBy(kv => kv.Key).Select(kv => kv.Key.ToString("X8") + ":" + kv.Value.ToString("X8")));
        }

        // The area a file belongs to. Outfit folders (esfNNN/CCC) and non-costume folders are "slots":
        // two mods for one slot replace the same thing in game. A character's shared parts (CCC = 000)
        // are used by every outfit, so sharing only those is reported as an override, not a slot clash.
        internal static string AreaKey(string path, out bool slot)
        {
            string p = PakHash.Normalize(path ?? "").TrimStart('/').ToLowerInvariant();
            Match m = ModelFolder.Match(p);
            if (m.Success) { slot = m.Groups[2].Value != "000"; return "esf" + m.Groups[1].Value + "/" + m.Groups[2].Value; }
            slot = true;
            string[] parts = p.Split('/');
            if (parts.Length == 1) return "game folder";
            if (parts[0] == "natives") return String.Join("/", parts.Take(Math.Min(5, parts.Length - 1)));
            return String.Join("/", parts.Take(Math.Min(2, parts.Length - 1)));
        }
        internal static HashSet<string> SlotAreas(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths) { bool slot; string key = AreaKey(p, out slot); if (slot) set.Add(key); }
            return set;
        }
        internal static string AreaLabel(string key, GameReference reference)
        {
            Match m = Regex.Match(key ?? "", @"^esf(\d{3})/(\d{3})$");
            if (m.Success) return (reference ?? GameReference.FromCmd(null)).DescribeFolder(m.Groups[1].Value, m.Groups[2].Value);
            return key == "game folder" ? "the game folder" : key + " files";
        }
        // "Cammy Outfit 2" from "Cammy Outfit 2 (esf009/002)".
        internal static string ShortArea(string label) { int at = (label ?? "").IndexOf(" ("); return at > 0 ? label.Substring(0, at) : label ?? ""; }

        internal static string When(string utc)
        {
            DateTime when;
            if (!DateTime.TryParse(utc ?? "", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out when)) return "";
            return when.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Every installed replacement that shares files or a slot with `paths`. A new install goes to the
        // top of the list, so it wins every shared file; an update in place keeps its own priority.
        // The same source with the same options is not a conflict: Install offers to update it in place.
        internal static List<ReplaceConflict> AnalyzeConflicts(IEnumerable<string> paths, ReplaceState state, string updateId, GameReference reference) { return AnalyzeConflicts(paths, state, updateId, reference, null, null); }
        internal static List<ReplaceConflict> AnalyzeConflicts(IEnumerable<string> paths, ReplaceState state, string updateId, GameReference reference, string sourcePath, IEnumerable<string> selectedIds)
        {
            var selection = selectedIds == null ? null : new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
            var mine = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var mySlots = SlotAreas(mine);
            int updateIndex = String.IsNullOrEmpty(updateId) ? -1 : state.Mods.FindIndex(m => String.Equals(m.Id, updateId, StringComparison.OrdinalIgnoreCase));
            var result = new List<ReplaceConflict>();
            for (int i = 0; i < state.Mods.Count; i++)
            {
                ReplaceMod other = state.Mods[i];
                if (i == updateIndex) continue;
                // 2.4.1: the same source (another variant or option set of this mod) is a switch, not a conflict:
                // Install replaces it in place (PrepareAndInstall), so it never warns or offers an Uninstall link.
                if (!String.IsNullOrEmpty(sourcePath) && SameSource(other.SourcePath, sourcePath)) continue;
                var shared = other.Files.Select(f => f.Path).Where(mine.Contains).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                var common = SlotAreas(other.Files.Select(f => f.Path)); common.IntersectWith(mySlots);
                if (shared.Count == 0 && common.Count == 0) continue;
                var c = new ReplaceConflict { ModId = other.Id, ModName = other.Name, InstalledUtc = other.InstalledUtc, Enabled = other.Enabled, Priority = i + 1, TheirFiles = other.Files.Count, Shared = shared.Count, SharedPaths = shared, NewWins = updateIndex < 0 || updateIndex < i };
                if (common.Count > 0) { c.Kind = shared.Count > 0 ? "slot" : "stack"; c.Areas = common.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).Select(a => AreaLabel(a, reference)).ToList(); }
                else
                {
                    c.Kind = "files";
                    c.Areas = shared.Select(p => { bool slot; return AreaKey(p, out slot); }).Distinct(StringComparer.OrdinalIgnoreCase).Select(a => AreaLabel(a, reference)).ToList();
                }
                c.Level = c.Kind == "stack" ? "info" : "warn";
                c.Text = ConflictText(c);
                result.Add(c);
            }
            return result.OrderBy(x => x.Kind == "slot" ? 0 : x.Kind == "files" ? 1 : 2).ThenBy(x => x.Priority).ToList();
        }

        // A few words for queued action titles: "overrides 1 of 2 files of 'Cammy Alt'".
        internal static string ConflictBrief(ReplaceConflict c)
        {
            if (c.Kind == "stack") return "stacks with '" + c.ModName + "' (no shared files)";
            if (!c.Enabled) return "shares " + N(c.Shared, "file") + " with disabled '" + c.ModName + "'";
            if (!c.NewWins) return "'" + c.ModName + "' keeps " + N(c.Shared, "shared file");
            return "overrides " + (c.TheirFiles == 1 ? "the only file" : (c.Shared == c.TheirFiles ? "all " + c.TheirFiles : c.Shared + " of " + c.TheirFiles) + " files") + " of '" + c.ModName + "'";
        }

        internal static string ConflictText(ReplaceConflict c)
        {
            string area = c.Areas.Count == 0 ? "This outfit" : ShortArea(c.Areas[0]) + (c.Areas.Count > 1 ? " (+" + (c.Areas.Count - 1) + ")" : "");
            string when = When(c.InstalledUtc);
            string facts = (c.Enabled ? "enabled" : "disabled") + (when.Length > 0 ? ", installed " + when : "") + ", " + N(c.TheirFiles, "file");
            int rest = c.TheirFiles - c.Shared;
            if (c.Kind == "stack")
                return "Also on " + area + ": '" + c.ModName + "' (" + (c.Enabled ? "enabled" : "disabled") + ", no shared files) — both stay installed.";
            if (c.Kind == "files")
            {
                string where = c.Areas.Count == 0 ? "" : " · " + String.Join(", ", c.Areas.Take(2).Select(ShortArea));
                if (!c.Enabled) return "'" + c.ModName + "' (disabled) shares " + N(c.Shared, "file") + " with this install" + where + ".";
                return c.NewWins ? "Overrides " + N(c.Shared, "file") + " from '" + c.ModName + "' (this install wins)" + where + "."
                    : "'" + c.ModName + "' keeps " + N(c.Shared, "shared file") + " (higher in the list; change the order on the Installed page)" + where + ".";
            }
            if (!c.Enabled)
                return area + " also has '" + c.ModName + "' (" + facts + ") · " + N(c.Shared, "shared file") + (rest > 0 ? "; if re-enabled, its other " + (rest == 1 ? "file" : rest + " files") + " would mix in." : "; it stays hidden below this one.");
            if (!c.NewWins)
                return area + " already has '" + c.ModName + "' (" + facts + ") · it is higher in the list and keeps " + N(c.Shared, "shared file") + "; change the order on the Installed page.";
            return area + " already has '" + c.ModName + "' (" + facts + ") · this install overrides " + (rest == 0 ? AllOfIts(c.TheirFiles) : c.Shared + " of its " + c.TheirFiles + " files; its other " + (rest == 1 ? "file stays" : rest + " stay") + " active (the outfit may mix both mods)") + ".";
        }

        // 1.0.8 (the user: easier to read, two paragraphs where the text is ours): the inspector's REPLACES note as a short
        // headline (what is there) and the details (its state and what this install does). Report and Assistant keep Text.
        internal static string[] ConflictParagraphs(ReplaceConflict c)
        {
            string area = c.Areas.Count == 0 ? "This outfit" : ShortArea(c.Areas[0]) + (c.Areas.Count > 1 ? " (+" + (c.Areas.Count - 1) + ")" : "");
            string when = When(c.InstalledUtc), name = "'" + c.ModName + "'";
            string facts = (c.Enabled ? "Enabled" : "Disabled") + (when.Length > 0 ? " · installed " + when : "") + " · " + N(c.TheirFiles, "file");
            int rest = c.TheirFiles - c.Shared;
            if (c.Kind == "stack")
                return new[] { "Also on " + area + ": " + name + ".", (c.Enabled ? "Enabled" : "Disabled") + " · no shared files, so both stay installed." };
            if (c.Kind == "files")
            {
                string where = c.Areas.Count == 0 ? "" : " · " + String.Join(", ", c.Areas.Take(2).Select(ShortArea));
                if (!c.Enabled) return new[] { "Shares " + N(c.Shared, "file") + " with " + name + ".", "It is disabled" + where + "." };
                return c.NewWins ? new[] { "Overrides " + N(c.Shared, "file") + " from " + name + ".", "This install wins" + where + "." }
                    : new[] { name + " keeps " + N(c.Shared, "shared file") + ".", "It is higher in the list; change the order on the Installed page" + where + "." };
            }
            if (!c.Enabled)
                return new[] { area + " also has " + name + ".", facts + " · " + N(c.Shared, "shared file") + ". " + (rest > 0 ? "If re-enabled, its other " + (rest == 1 ? "file" : rest + " files") + " would mix in." : "It stays hidden below this one.") };
            if (!c.NewWins)
                return new[] { area + " already has " + name + ", higher in the list.", facts + ". It keeps " + N(c.Shared, "shared file") + "; change the order on the Installed page." };
            return new[] { area + " already has " + name + ".", facts + ". This install overrides " + (rest == 0 ? AllOfIts(c.TheirFiles) + "." : c.Shared + " of them; the other " + (rest == 1 ? "one stays" : rest + " stay") + " active, so the outfit may mix both mods.") };
        }

        internal static string Report(ReplacePlan plan)
        {
            var lines = new List<string>();
            lines.Add((plan.CanInstall ? "Ready to install" : "Cannot install") + " · " + plan.ModName);
            lines.Add("Replaces: " + plan.AffectsText);
            lines.Add(N(plan.Files.Count, "file") + " · " + Utilities.FriendlyBytes(plan.Bytes) + " · " + (plan.VanillaReplaced == 1 ? "1 replaces a game file, " : plan.VanillaReplaced + " replace game files, ") + plan.NewFiles + " new" + (plan.RootFiles > 0 ? ", " + plan.RootFiles + " in the game folder" : "") + " · " + plan.Fixed + " auto-fixed");
            foreach (HealthItem i in plan.Items.OrderBy(i => i.Level == "error" ? 0 : i.Level == "warn" ? 1 : i.Level == "fix" ? 2 : 3)) lines.Add((i.Level == "error" ? "ERROR " : i.Level == "warn" ? "WARN  " : i.Level == "fix" ? "FIX   " : "INFO  ") + i.Text);
            return String.Join(Environment.NewLine, lines);
        }
    }

    // Extracted and fixed files for the automatic health check of one package. Keyed by entry, size,
    // destination and the game facts the fixers use; cleared when the archive changes on disk.
    internal sealed class PrepareCache : IDisposable
    {
        private sealed class Item { internal string File, Sha256; internal long Size; internal List<FixResult> Fixes; }
        private readonly object gate = new object();
        private readonly Dictionary<string, Item> items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        private string stamp;
        private int counter;
        internal readonly string Folder;
        internal int Hits, Misses, Extractions, Extracted;   // Extracted (2.4.2): files copied or extracted into the cache
        internal PrepareCache() { Folder = Path.Combine(Path.GetTempPath(), "sf6-check-cache-" + Guid.NewGuid().ToString("N").Substring(0, 10)); }

        // 2.4.2: folder sources too: each file is keyed by its size and write time, so a re-check copies only the
        // files that changed (2.0.1 copied a folder source again on every check). Archives: a changed size/date clears it.
        internal bool Usable(string source)
        {
            if (String.IsNullOrEmpty(source)) return false;
            if (Directory.Exists(source)) return true;
            if (!System.IO.File.Exists(source)) return false;
            string now = SourceReader.Stamp(source);
            lock (gate)
            {
                if (stamp != null && stamp != now) Clear();
                stamp = now;
            }
            return true;
        }

        internal Dictionary<string, PreparedFile> Stage(Dictionary<string, PlannedFile> chosen, SourceReader reader, GameReference reference, string referenceKey)
        {
            lock (gate)
            {
                var result = new Dictionary<string, PreparedFile>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<KeyValuePair<string, PlannedFile>>();
                Func<KeyValuePair<string, PlannedFile>, string> key = delegate(KeyValuePair<string, PlannedFile> p) { SourceEntry e; long ticks = reader.Entries.TryGetValue(p.Value.Asset.EntryPath, out e) ? e.WriteTicks : 0; return referenceKey + "|" + p.Value.Asset.EntryPath + "|" + p.Value.Asset.Size + "|" + ticks + "|" + p.Key; };
                foreach (var pair in chosen)
                {
                    Item item;
                    if (items.TryGetValue(key(pair), out item) && System.IO.File.Exists(item.File)) { result[pair.Key] = Prepared(pair, item); Hits++; }
                    else missing.Add(pair);
                }
                if (missing.Count == 0) return result;
                Misses += missing.Count; Extractions++;
                if (items.Values.Sum(i => i.Size) > 4L * 1024 * 1024 * 1024) Clear();
                Directory.CreateDirectory(Folder);
                var synthetic = new List<KeyValuePair<KeyValuePair<string, PlannedFile>, PlannedFile>>();
                foreach (var pair in missing)
                {
                    string leaf = Regex.Replace(Path.GetFileName(pair.Key), @"[^A-Za-z0-9._-]", "_");
                    synthetic.Add(new KeyValuePair<KeyValuePair<string, PlannedFile>, PlannedFile>(pair, new PlannedFile { Component = pair.Value.Component, Asset = new NativeAsset { EntryPath = pair.Value.Asset.EntryPath, DestinationPath = "f" + (++counter) + "/" + leaf, Size = pair.Value.Asset.Size } }));
                }
                string scratch = Path.Combine(Folder, ".scratch-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(scratch);
                // 2.4.2: each file enters the cache as soon as it is complete, so a check cancelled by a newer request
                // (opening the Installed page, saving Settings, another option) keeps what it already extracted.
                var byDestination = synthetic.ToDictionary(s => s.Value.Asset.DestinationPath, s => s.Key, StringComparer.OrdinalIgnoreCase);
                try
                {
                    reader.CopyFilesEach(synthetic.Select(s => s.Value), Folder, scratch, delegate(PlannedFile copied, string sha)
                    {
                        KeyValuePair<string, PlannedFile> pair = byDestination[copied.Asset.DestinationPath];
                        string file = Safety.Under(Folder, copied.Asset.DestinationPath);
                        var item = new Item { File = file, Sha256 = sha, Size = new FileInfo(file).Length, Fixes = new List<FixResult>() };
                        string low = pair.Key.ToLowerInvariant();
                        List<FixResult> quick;
                        if (low.StartsWith("natives/") && low.Contains(".tex.") && ModFixes.TextureFileOk(file, out quick)) item.Fixes = quick;   // 1.0.1: header only
                        else if (low.StartsWith("natives/") && (low.Contains(".user.") || low.Contains(".tex.") || (reference != null && reference.SwapRepairs && low.EndsWith(".scn.20"))) && item.Size <= 256L * 1024 * 1024)
                        {
                            byte[] data = System.IO.File.ReadAllBytes(file);
                            item.Fixes = ModFixes.UpgradeModFile(ref data, reference, pair.Key);
                            if (item.Fixes.Any(x => x.Changed)) { System.IO.File.WriteAllBytes(file, data); item.Sha256 = ReplaceEngine.Sha(data); item.Size = data.Length; }
                        }
                        items[key(pair)] = item; Extracted++;
                        result[pair.Key] = Prepared(pair, item);
                    }, true);
                }
                finally
                {
                    try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
                    if (disposed) Clear();
                }
                return result;
            }
        }

        private static PreparedFile Prepared(KeyValuePair<string, PlannedFile> pair, Item item)
        {
            return new PreparedFile { Path = pair.Key, Source = item.File, EntryPath = pair.Value.Asset.EntryPath, Component = pair.Value.Component.Name, Size = item.Size, Sha256 = item.Sha256, Fixes = item.Fixes };
        }

        private void Clear()
        {
            items.Clear();
            try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch (Exception ex) { Program.Log(ex); }
        }
        private volatile bool disposed;
        // Never blocks the UI: if a check is still extracting, the folder is removed when it finishes.
        public void Dispose()
        {
            disposed = true;
            if (!System.Threading.Monitor.TryEnter(gate)) return;
            try { Clear(); } finally { System.Threading.Monitor.Exit(gate); }
        }
    }
}
