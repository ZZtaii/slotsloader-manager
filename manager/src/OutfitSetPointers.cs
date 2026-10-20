using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.8 (the user: "presets should just be a small pointers file that has info on how to install everything to how
    // it is right now"): a preset (format v3) lists each enabled extra outfit and replacement by its library source,
    // options, name and order; restoring installs them again from the library. Only what has no library source left (a
    // manual drop, a deleted source, a part added from a mod that's gone) is still copied, the way v1/v2 presets copy
    // everything (Entries / Replacements of the record). Restore goes item by item: a missing source, a different outfit
    // already in the place, or a failed install is listed in the result and the rest carries on.
    public sealed class PresetPointers
    {
        public List<PresetOutfitPointer> Outfits = new List<PresetOutfitPointer>();
        public List<PresetReplacementPointer> Replacements = new List<PresetReplacementPointer>();   // priority order, 0 = highest
        // 1.0.10: why each copy was kept ("manual drop", "not found at <path>", "" = no library mod), in the order of the
        // record's Entries and Replacements.Mods. Null in presets saved before (OutfitSetInfo.Copies then reads the entry).
        public List<string> CopiedOutfitReasons, CopiedReplacementReasons;
    }
    // 1.0.10: a library archive as the Mod library listed it, for finding a manual drop's identical twin at save.
    internal sealed class LibraryArchive { internal string Path; internal long Size; }
    public sealed class PresetOutfitPointer
    {
        public string Character = "", Folder = "", Name = "", Mode = "", SourcePath = "", SourceStamp = "";   // Mode: composed | all_variants
        public List<string> SelectedIds = new List<string>();
        public List<PresetPartPointer> Parts = new List<PresetPartPointer>();   // Add to outfit, in the order they were added
    }
    public sealed class PresetPartPointer { public string SourcePath = "", SourceStamp = "", Mod = ""; public List<string> SelectedIds = new List<string>(); }
    public sealed class PresetReplacementPointer
    {
        public string Name = "", SourcePath = "", SourceStamp = "", Kind = "";
        public List<string> SelectedIds = new List<string>();
        public int Order = -1;   // its place among all saved replacements, copied ones included (-1: saved before it was kept)
    }

    internal static partial class OutfitSetStore
    {
        internal const string ModeComposed = "composed", ModeAllVariants = "all_variants";
        private const string FormatV3 = "sf6-slotsloader-outfit-set-v3";

        // Cancel and out-of-memory stop the restore; anything else fails only its item.
        private static bool Fatal(Exception ex) { return ex is OutOfMemoryException || ex is OperationCanceledException || ex is System.Threading.ThreadAbortException; }
        private static long RecordBytes(string text) { return System.Text.Encoding.UTF8.GetByteCount(text ?? ""); }
        private static bool SourceExists(string path)
        {
            try { return !String.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path) && (File.Exists(path) || System.IO.Directory.Exists(path)); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        private static bool SameIds(IEnumerable<string> a, IEnumerable<string> b)
        {
            return new HashSet<string>(a ?? new string[0], StringComparer.OrdinalIgnoreCase).SetEquals(b ?? new string[0]);
        }

        // An enabled outfit as a pointer, or null when it has to be copied (no library source, or a part's source is gone);
        // `why` then says which (CopiedOutfitReasons).
        private static PresetOutfitPointer OutfitPointer(InstalledOutfit o, out string why)
        {
            why = "";
            if (o == null || !o.Enabled) return null;
            if (!HasSource(o)) { why = !o.Managed && !o.WholeArchive ? ManualDrop : SourceExists(o.SourcePath) ? "" : NotFound(o.SourcePath); return null; }
            string source = System.IO.Path.GetFullPath(o.SourcePath), folder = System.IO.Path.GetFileName(o.Folder.TrimEnd('\\'));
            if (o.WholeArchive)
                return new PresetOutfitPointer { Character = o.Character, Folder = folder, Name = folder, Mode = ModeAllVariants, SourcePath = source, SourceStamp = MainForm.SourceStamp(source) ?? "" };
            if (!o.Managed || o.SelectedIds == null || o.SelectedIds.Count == 0) return null;
            var pointer = new PresetOutfitPointer { Character = o.Character, Folder = folder, Name = String.IsNullOrWhiteSpace(o.Name) ? folder : o.Name, Mode = ModeComposed, SourcePath = source, SourceStamp = MainForm.SourceStamp(source) ?? "", SelectedIds = o.SelectedIds.ToList() };
            InstallManifest manifest;
            try { manifest = Json.Read<InstallManifest>(System.IO.Path.Combine(o.Folder, ".slots-installer.json")); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
            foreach (PartGraft part in (manifest == null ? null : manifest.Parts) ?? new List<PartGraft>())
            {
                if (part == null || !SourceExists(part.SourcePath) || part.SelectedIds == null || part.SelectedIds.Count == 0)
                {
                    if (part != null && !SourceExists(part.SourcePath) && !String.IsNullOrWhiteSpace(part.SourcePath)) why = "added part not found at " + part.SourcePath;
                    return null;
                }
                pointer.Parts.Add(new PresetPartPointer { SourcePath = System.IO.Path.GetFullPath(part.SourcePath), SourceStamp = MainForm.SourceStamp(part.SourcePath) ?? "", Mod = part.Mod ?? "", SelectedIds = part.SelectedIds.ToList() });
            }
            return pointer;
        }
        private static PresetReplacementPointer ReplacementPointer(ReplaceMod m, out string why)
        {
            why = "";
            if (m == null) return null;
            // 1.0.10: a recorded source that is gone (renamed or moved outside the Manager) is not guessed; it is copied and says so.
            if (!SourceExists(m.SourcePath)) { why = NotFound(m.SourcePath); return null; }
            string source = System.IO.Path.GetFullPath(m.SourcePath);
            return new PresetReplacementPointer { Name = m.Name ?? "", SourcePath = source, SourceStamp = MainForm.SourceStamp(source) ?? "", Kind = m.Kind ?? "", SelectedIds = (m.SelectedIds ?? new List<string>()).ToList() };
        }
        internal const string ManualDrop = "manual drop";
        internal static string NotFound(string path) { return String.IsNullOrWhiteSpace(path) ? "" : "not found at " + path; }

        // 1.0.10: the ZIP/RAR/7Z entries of an already listed library (the Mod library's list; nothing is read from
        // disk). Call it where the list is safe to read (the UI thread for MainForm's).
        internal static List<LibraryArchive> LibraryArchives(IEnumerable<LibraryEntry> entries)
        {
            var result = new List<LibraryArchive>();
            foreach (LibraryEntry e in entries ?? new LibraryEntry[0])
                if (e != null && e.Size > 0 && IsArchiveName(e.SourcePath)) result.Add(new LibraryArchive { Path = e.SourcePath, Size = e.Size });
            return result;
        }
        private static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z" };
        private static bool IsArchiveName(string path)
        {
            try { return !String.IsNullOrEmpty(path) && System.IO.Path.IsPathRooted(path) && ArchiveExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }
        private static bool Inside(string path, string folder)
        {
            try { string p = System.IO.Path.GetFullPath(path).TrimEnd('\\'), f = System.IO.Path.GetFullPath(folder).TrimEnd('\\'); return String.Equals(p, f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }

        // 1.0.10 (the user: a manual drop identical to a library archive was still copied): a ZIP/RAR/7Z in costume_mods
        // with no library source (a manual drop, or an Install all variants copy whose source is gone) whose bytes equal
        // a listed library archive becomes an all_variants pointer to it. Only same-size archives are hashed (a same
        // name first), and the drop only when there is one. `hashes` keeps each file's SHA-256 for this save.
        private static PresetOutfitPointer IdenticalArchivePointer(string root, InstalledOutfit o, List<LibraryArchive> library, Dictionary<string, string> hashes)
        {
            if (o == null || !o.Enabled || o.Managed || library == null || library.Count == 0 || !IsArchiveName(o.Folder) || !Characters.Names.Contains(o.Character)) return null;
            FileInfo drop;
            try { drop = new FileInfo(o.Folder); if (!drop.Exists || drop.Length == 0 || drop.Name.StartsWith(".")) return null; }   // a dot name is no valid pointer place
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
            List<LibraryArchive> candidates = library.Where(a => a.Size == drop.Length && String.Equals(System.IO.Path.GetExtension(a.Path), drop.Extension, StringComparison.OrdinalIgnoreCase) && !Inside(a.Path, root))
                .OrderBy(a => String.Equals(System.IO.Path.GetFileName(a.Path), drop.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            string dropHash = null;
            foreach (LibraryArchive a in candidates)
            {
                string path;
                try { path = System.IO.Path.GetFullPath(a.Path); var info = new FileInfo(path); if (!info.Exists || info.Length != drop.Length) continue; }   // the list can be older than the disk
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; continue; }
                if (dropHash == null && (dropHash = FileHash(drop.FullName, hashes)) == null) return null;
                if (FileHash(path, hashes) == dropHash)
                    return new PresetOutfitPointer { Character = o.Character, Folder = drop.Name, Name = drop.Name, Mode = ModeAllVariants, SourcePath = path, SourceStamp = MainForm.SourceStamp(path) ?? "" };
            }
            return null;
        }
        // A file's SHA-256 (hex), read once per `hashes`; null when it can't be read. Cancellable between 1 MB reads.
        private static string FileHash(string path, Dictionary<string, string> hashes)
        {
            string hash;
            if (hashes.TryGetValue(path, out hash)) return hash;
            try
            {
                using (var sha = FastHash.Sha256())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16))
                {
                    var buffer = new byte[1 << 20]; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { OperationContext.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, null, 0); }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    hash = BitConverter.ToString(sha.Hash).Replace("-", "");
                }
            }
            catch (Exception ex) { if (Fatal(ex)) throw; Program.Log(ex); hash = null; }
            hashes[path] = hash;
            return hash;
        }
        // Two files with the same bytes (the size first; hashed only when it matches).
        private static bool SameBytes(string a, string b)
        {
            try
            {
                var x = new FileInfo(a); var y = new FileInfo(b);
                if (!x.Exists || !y.Exists || x.Length != y.Length) return false;
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string h = FileHash(x.FullName, hashes);
                return h != null && h == FileHash(y.FullName, hashes);
            }
            catch (Exception ex) { if (Fatal(ex)) throw; return false; }
        }

        // v3 save: pointers for everything with a library source, copies (SavePreset) for the rest. 1.0.10: `library`
        // (null = none) gives manual archives their twin (IdenticalArchivePointer); each copy keeps why.
        private static OutfitSetInfo SavePointerPreset(string root, string name, List<InstalledOutfit> enabled, List<ReplaceMod> mods, Func<ReplaceMod, ModFile, string> storeFile, string method, List<LibraryArchive> library)
        {
            var pointers = new PresetPointers { CopiedOutfitReasons = new List<string>(), CopiedReplacementReasons = new List<string>() };
            var copyOutfits = new List<InstalledOutfit>(); var copyMods = new List<ReplaceMod>();
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < enabled.Count; i++)
            {
                OperationContext.Report("Listing outfit " + (i + 1) + " of " + enabled.Count, i, enabled.Count);
                string why;
                PresetOutfitPointer p = OutfitPointer(enabled[i], out why) ?? IdenticalArchivePointer(root, enabled[i], library, hashes);
                if (p != null) pointers.Outfits.Add(p); else { copyOutfits.Add(enabled[i]); pointers.CopiedOutfitReasons.Add(why); }
            }
            List<ReplaceMod> all = mods ?? new List<ReplaceMod>();
            for (int i = 0; i < all.Count; i++)
            {
                string why;
                PresetReplacementPointer p = ReplacementPointer(all[i], out why);
                if (p != null) { p.Order = i; pointers.Replacements.Add(p); } else { copyMods.Add(all[i]); pointers.CopiedReplacementReasons.Add(why); }
            }
            if (pointers.Outfits.Count + pointers.Replacements.Count + copyOutfits.Count + copyMods.Count == 0) throw new IOException("Nothing enabled to save.");
            return SavePreset(root, name, copyOutfits, copyMods, storeFile, method, pointers);
        }

        // Record checks of v3 pointers (Read).
        private static void ValidatePointers(string root, OutfitSetRecord record, HashSet<string> destinations)
        {
            if (record.Pointers == null || record.Pointers.Outfits == null || record.Pointers.Replacements == null) throw new IOException("Invalid saved-set record; left untouched.");
            foreach (PresetOutfitPointer p in record.Pointers.Outfits)
            {
                if (p == null || (p.Mode != ModeComposed && p.Mode != ModeAllVariants) || !Rooted(p.SourcePath) || p.SelectedIds == null || p.Parts == null || (p.Mode == ModeComposed && p.SelectedIds.Count == 0) || (p.Mode == ModeAllVariants && p.Parts.Count > 0)) throw new IOException("Invalid saved outfit entry.");
                Safety.Segment(p.Character); Safety.Segment(p.Folder);
                if (p.Character.StartsWith(".") || p.Folder.StartsWith(".") || String.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 300) throw new IOException("Invalid saved outfit destination.");
                if (!destinations.Add(Safety.Under(root, p.Character + "/" + p.Folder))) throw new IOException("Invalid or repeated saved outfit entry.");
                foreach (PresetPartPointer part in p.Parts) if (part == null || !Rooted(part.SourcePath) || part.SelectedIds == null || part.SelectedIds.Count == 0) throw new IOException("Invalid saved outfit part.");
            }
            foreach (PresetReplacementPointer p in record.Pointers.Replacements)
                if (p == null || String.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 300 || !Rooted(p.SourcePath) || p.SelectedIds == null) throw new IOException("Invalid saved replacement; left untouched.");
        }
        private static bool Rooted(string path)
        {
            try { return !String.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path) && System.IO.Path.GetFullPath(path) == path; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }

        private static string SevenZip(string sevenZip)
        {
            if (!String.IsNullOrEmpty(sevenZip)) return sevenZip;
            try { AppConfig config = AppConfig.Load(); return config == null ? null : config.SevenZipPath; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return null; }
        }
        private static List<string> MapIds(ModPackage package, IEnumerable<string> ids, out List<string> dropped)
        {
            return MainForm.MapSelection(package, (ids ?? new string[0]).Select(id => new KeyValuePair<string, string>(id, "")).ToList(), out dropped);
        }
        private static string ChangedNote(string stamp, string source)
        {
            string now = MainForm.SourceStamp(source);
            return !String.IsNullOrEmpty(stamp) && now != null && now != stamp ? "the mod changed since the preset was saved; its current version was installed" : null;
        }

        // Replacements first (like v1/v2), then extra outfits. Each item is installed on its own; problems become notes.
        // `restored` (replacements): saved index -> id of each replacement installed here.
        private static void RestorePointers(string root, OutfitSetRecord record, bool outfits, bool replacements, ReplaceEngine engine, string sevenZip, OutfitSetRestoreResult result, Dictionary<int, string> restored)
        {
            PresetPointers pointers = record.Pointers;
            if (replacements && pointers.Replacements.Count > 0) RestoreReplacementPointers(pointers.Replacements, engine, SevenZip(sevenZip), result, restored ?? new Dictionary<int, string>());
            if (outfits && pointers.Outfits.Count > 0) RestoreOutfitPointers(root, pointers.Outfits, SevenZip(sevenZip), result);
        }
        private static bool SameName(string a, string b) { return String.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase); }

        private static void RestoreReplacementPointers(List<PresetReplacementPointer> saved, ReplaceEngine engine, string sevenZip, OutfitSetRestoreResult result, Dictionary<int, string> restored)
        {
            if (engine == null) throw new IOException("Replacements can't be restored without the game folder.");
            bool checkedGame = false;
            // Lowest priority first, each on top; ArrangeReplacements then puts them next to their saved neighbours.
            for (int i = saved.Count - 1, n = 0; i >= 0; i--, n++)
            {
                PresetReplacementPointer p = saved[i];
                OperationContext.Report("Restoring replacement " + (n + 1) + " of " + saved.Count, n, saved.Count);
                List<ReplaceMod> installed = engine.LoadState().Mods;
                ReplaceMod same = installed.FirstOrDefault(m => ReplacePlanner.SameSource(m.SourcePath, p.SourcePath));
                if (same != null)
                {
                    if (SameIds(same.SelectedIds, p.SelectedIds)) result.ReplacementsSkipped++;
                    else result.ReplacementConflicts.Add(p.Name);
                    continue;
                }
                // Another archive or version under the saved name: skipped and reported, like a copied one (ClassifyPreset).
                if (installed.Any(m => SameName(m.Name, p.Name))) { result.ReplacementConflicts.Add(p.Name); continue; }
                if (!SourceExists(p.SourcePath)) { result.Notes.Add(p.Name + ": not in your libraries any more (" + p.SourcePath + ")."); continue; }
                if (!checkedGame)
                {
                    GameProcesses.RequireGameClosed("restore replacements");
                    string locked = engine.ExternalLockReason();
                    if (locked != null) throw new IOException(locked);
                    // The PAK method waits for the external mod manager to close, as for an old-format preset.
                    if (GameProcesses.Probe.IsFluffyRunning() && engine.CurrentMethod(engine.LoadState()) == ReplaceMethods.Pak && saved.Any(x => x.Kind != "Tool")) throw new IOException(GameProcesses.FluffyPakMessage("restore replacements"));
                    checkedGame = true;
                }
                try
                {
                    ModPackage package = PackageReader.Load(p.SourcePath, sevenZip);
                    List<string> dropped; List<string> ids = MapIds(package, p.SelectedIds, out dropped);
                    CompositionPlan composition = Composer.Plan(package, ids, true);
                    if (!composition.CanInstall) throw new IOException(String.Join(" ", composition.Errors.DefaultIfEmpty("Its options changed.")));
                    ReplacePlan plan;
                    ReplaceMod done = ReplacePlanner.PrepareAndInstall(composition, sevenZip, engine, p.Name, false, null, out plan);
                    if (done != null) restored[i] = done.Id;
                    result.ReplacementsRestored++;
                    if (dropped.Count > 0) result.Notes.Add(p.Name + ": options no longer in the mod were left out (" + String.Join(", ", dropped) + ").");
                    string changed = ChangedNote(p.SourceStamp, p.SourcePath); if (changed != null) result.Notes.Add(p.Name + ": " + changed + ".");
                }
                catch (Exception ex) { if (Fatal(ex)) throw; result.Notes.Add(p.Name + ": not restored. " + ex.Message); }
            }
        }

        // The saved priority order of a v3 preset's replacements, listed and copied together: (listed?, index in its
        // list). A listed one sits at its Order; the copies fill the other places in their own order. Presets saved
        // before Order was kept: the listed ones first.
        private static List<KeyValuePair<bool, int>> SavedReplacementOrder(OutfitSetRecord record)
        {
            List<PresetReplacementPointer> listed = record.Pointers.Replacements;
            int total = listed.Count + (record.Replacements == null ? 0 : record.Replacements.Mods.Count);
            bool ordered = listed.All(p => p.Order >= 0 && p.Order < total) && listed.Select(p => p.Order).Distinct().Count() == listed.Count;
            var slots = new KeyValuePair<bool, int>?[total];
            for (int i = 0; i < listed.Count; i++) slots[ordered ? listed[i].Order : i] = new KeyValuePair<bool, int>(true, i);
            for (int k = 0, copy = 0; k < total; k++) if (slots[k] == null) slots[k] = new KeyValuePair<bool, int>(false, copy++);
            return slots.Select(x => x.Value).ToList();
        }
        // After a restore: the replacements it installed (listed ones went on top, copied ones among the copies) move
        // next to their saved neighbours (ReplaceEngine.ArrangeRestored), so the saved priority order holds.
        // `copied`: the copies' restore (null when none); `listed`: saved pointer index -> installed id.
        private static void ArrangeReplacements(OutfitSetRecord record, PresetReplacementPlan copied, Dictionary<int, string> listed, ReplaceEngine engine, OutfitSetRestoreResult result)
        {
            try
            {
                List<ReplaceMod> installed = engine.LoadState().Mods;
                var saved = new List<string>(); var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<bool, int> slot in SavedReplacementOrder(record))
                {
                    string id = null;
                    if (slot.Key)
                    {
                        if (listed.TryGetValue(slot.Value, out id)) restored.Add(id);
                        else { PresetReplacementPointer p = record.Pointers.Replacements[slot.Value]; ReplaceMod same = installed.FirstOrDefault(m => ReplacePlanner.SameSource(m.SourcePath, p.SourcePath)); id = same == null ? null : same.Id; }
                    }
                    else if (copied != null)
                    {
                        if (copied.Created.TryGetValue(slot.Value, out id)) restored.Add(id);
                        else copied.Matched.TryGetValue(slot.Value, out id);
                    }
                    saved.Add(id);
                }
                string message = engine.ArrangeRestored(saved, restored);
                if (!String.IsNullOrEmpty(message)) result.ReplaceMessage = ((result.ReplaceMessage ?? "") + " " + message).Trim();
            }
            catch (Exception ex) { if (Fatal(ex)) throw; result.Notes.Add("The restored replacements couldn't be put in their saved order: " + ex.Message); }
        }

        private static void RestoreOutfitPointers(string root, List<PresetOutfitPointer> saved, string sevenZip, OutfitSetRestoreResult result)
        {
            for (int i = 0; i < saved.Count; i++)
            {
                PresetOutfitPointer p = saved[i]; string label = p.Character + "/" + p.Folder;
                OperationContext.Report("Restoring outfit " + (i + 1) + " of " + saved.Count, i, saved.Count);
                List<InstalledOutfit> installed = Installer.ListInstalled(root);
                string destination = Safety.Under(root, p.Character + "/" + p.Folder);
                InstalledOutfit there = installed.FirstOrDefault(o => String.Equals(System.IO.Path.GetFullPath(o.Folder).TrimEnd('\\'), destination, StringComparison.OrdinalIgnoreCase));
                bool alreadyCopied = p.Mode == ModeAllVariants && installed.Any(o => o.WholeArchive && o.Enabled && String.Equals(o.Character, p.Character, StringComparison.OrdinalIgnoreCase) && ReplacePlanner.SameSource(o.SourcePath, p.SourcePath));
                if (there != null || Exists(destination) || alreadyCopied)
                {
                    bool same = alreadyCopied || (there != null && there.Enabled && SamePointed(there, p));
                    if (same && !alreadyCopied && p.Mode == ModeComposed && !SameParts(there, p.Parts)) result.Notes.Add(label + ": installed, but its added parts differ from the preset; left as is.");
                    else if (same) result.Skipped++;
                    else result.Notes.Add(label + ": a different outfit is installed there; left as is.");
                    continue;
                }
                if (!SourceExists(p.SourcePath)) { result.Notes.Add(label + ": its mod is not in your libraries any more (" + p.SourcePath + ")."); continue; }
                try
                {
                    var notes = new List<string>();
                    if (p.Mode == ModeAllVariants) ArchiveInstalls.Install(p.SourcePath, root, p.Character, p.Name, SavedArchiveName(p));
                    else
                    {
                        ModPackage package = PackageReader.Load(p.SourcePath, sevenZip);
                        List<string> dropped; List<string> ids = MapIds(package, p.SelectedIds, out dropped);
                        CompositionPlan plan = Composer.Plan(package, ids);
                        if (!plan.CanInstall) throw new IOException(String.Join(" ", plan.Errors.DefaultIfEmpty("Its options changed.")));
                        InstallResult done = Installer.Install(plan, root, p.Character, p.Name, sevenZip, false);
                        if (dropped.Count > 0) notes.Add("options no longer in the mod were left out (" + String.Join(", ", dropped) + ")");
                        foreach (PresetPartPointer part in p.Parts)
                        {
                            string partName = String.IsNullOrWhiteSpace(part.Mod) ? System.IO.Path.GetFileName(part.SourcePath) : part.Mod;
                            if (!SourceExists(part.SourcePath)) { notes.Add("the part from " + partName + " was left out: its mod is not in your libraries any more"); continue; }
                            try
                            {
                                InstalledOutfit fresh = Installer.ListInstalled(root).FirstOrDefault(o => String.Equals(System.IO.Path.GetFullPath(o.Folder).TrimEnd('\\'), System.IO.Path.GetFullPath(done.Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                                if (fresh == null) throw new IOException("the outfit was not found after installing it.");
                                Installer.AddToOutfit(fresh, root, Installer.SelectionFiles(PackageReader.Load(part.SourcePath, sevenZip), part.SelectedIds), sevenZip);
                            }
                            catch (Exception ex) { if (Fatal(ex)) throw; notes.Add("the part from " + partName + " was not added: " + ex.Message); }
                        }
                    }
                    result.Restored++;
                    string changed = ChangedNote(p.SourceStamp, p.SourcePath); if (changed != null) notes.Add(changed);
                    if (notes.Count > 0) result.Notes.Add(label + ": " + String.Join("; ", notes) + ".");
                }
                catch (Exception ex) { if (Fatal(ex)) throw; result.Notes.Add(label + ": not restored. " + ex.Message); }
            }
        }

        // The installed outfit in a pointer's place is the saved one: same source (and options); for all_variants also a
        // manual archive with the source's bytes (1.0.10: a manual drop saved as a pointer to its library twin).
        private static bool SamePointed(InstalledOutfit there, PresetOutfitPointer p)
        {
            if (p.Mode == ModeAllVariants) return (there.WholeArchive && ReplacePlanner.SameSource(there.SourcePath, p.SourcePath)) || (!there.Managed && SameBytes(there.Folder, p.SourcePath));
            return there.Managed && ReplacePlanner.SameSource(there.SourcePath, p.SourcePath) && SameIds(there.SelectedIds, p.SelectedIds);
        }
        // 1.0.10: an all_variants outfit comes back under its saved file name (Slotsloader's variant choices are keyed by
        // it, and a twin can be named differently in the library); null = the source's name.
        private static string SavedArchiveName(PresetOutfitPointer p)
        {
            try { return String.Equals(System.IO.Path.GetExtension(p.Folder), System.IO.Path.GetExtension(p.SourcePath), StringComparison.OrdinalIgnoreCase) ? p.Folder : null; }
            catch (ArgumentException) { return null; }
        }

        // The parts added to an installed outfit (its manifest) are the saved ones: same sources and options, same order.
        private static bool SameParts(InstalledOutfit o, List<PresetPartPointer> saved)
        {
            string path = System.IO.Path.Combine(o.Folder, ".slots-installer.json");
            List<PartGraft> parts;
            try
            {
                if (!File.Exists(path)) return saved.Count == 0;
                InstallManifest manifest = Json.Read<InstallManifest>(path);
                parts = ((manifest == null ? null : manifest.Parts) ?? new List<PartGraft>()).Where(x => x != null).ToList();
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
            if (parts.Count != saved.Count) return false;
            for (int i = 0; i < parts.Count; i++)
                if (!ReplacePlanner.SameSource(parts[i].SourcePath, saved[i].SourcePath) || !SameIds(parts[i].SelectedIds, saved[i].SelectedIds)) return false;
            return true;
        }

        // Preview of v3 replacement pointers: not installed yet (by source) or already there; a same-named other mod is a conflict.
        private static void PreviewPointers(OutfitSetRecord record, PresetScope scope, ReplaceEngine engine, string root, OutfitSetPreview preview)
        {
            if (PresetScopes.Outfits(scope))
                foreach (PresetOutfitPointer p in record.Pointers.Outfits) (Exists(Safety.Under(root, p.Character + "/" + p.Folder)) ? preview.ExistingOutfits : preview.AddOutfits).Add(p.Character + "/" + p.Folder);
            if (!PresetScopes.Replacements(scope) || record.Pointers.Replacements.Count == 0) return;
            try
            {
                if (engine == null) throw new IOException("No game folder.");
                List<ReplaceMod> installed = engine.LoadState().Mods;
                foreach (PresetReplacementPointer p in record.Pointers.Replacements)
                {
                    ReplaceMod same = installed.FirstOrDefault(m => ReplacePlanner.SameSource(m.SourcePath, p.SourcePath));
                    if (same == null && installed.Any(m => SameName(m.Name, p.Name))) preview.ConflictReplacements.Add(p.Name);
                    else if (same == null) { preview.AddReplacements.Add(p.Name); if (p.Kind != "Tool" && p.Kind != "Pak") preview.InstallsNatives = true; }
                    else if (SameIds(same.SelectedIds, p.SelectedIds)) preview.SameReplacements.Add(p.Name);
                    else preview.ConflictReplacements.Add(p.Name);
                }
            }
            catch (IOException ex) { preview.ReplacementError = ex.Message; preview.AddReplacements.AddRange(record.Pointers.Replacements.Select(p => p.Name)); preview.InstallsNatives = true; }
        }

        // 1.0.8: a library rename (SourceRename) moves the presets' pointers with the mod.
        internal static void RenameSource(string costumeRoot, string from, string to)
        {
            if (String.IsNullOrEmpty(from) || String.IsNullOrEmpty(to)) return;
            string root, container;
            try { root = Root(costumeRoot); container = Container(root, OutfitSetKind.Preset); if (!System.IO.Directory.Exists(container)) return; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return; }
            foreach (string folder in System.IO.Directory.EnumerateDirectories(container))
            {
                try
                {
                    string text; OutfitSetRecord record = Read(root, folder, OutfitSetKind.Preset, false, out text);
                    if (record.Pointers == null) continue;
                    bool changed = false;
                    Func<string, string> moved = delegate(string path) { string next = Moved(path, from, to); if (next != path) changed = true; return next; };
                    foreach (PresetOutfitPointer p in record.Pointers.Outfits) { p.SourcePath = moved(p.SourcePath); foreach (PresetPartPointer part in p.Parts) part.SourcePath = moved(part.SourcePath); }
                    foreach (PresetReplacementPointer p in record.Pointers.Replacements) p.SourcePath = moved(p.SourcePath);
                    if (!changed) continue;
                    string file = System.IO.Path.Combine(folder, RecordName), temp = file + ".tmp";
                    Json.Write(temp, record);
                    File.Replace(temp, file, null);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
        }
        // The path with `from` (a file or folder) replaced by `to`; the path itself, or one inside the folder.
        private static string Moved(string path, string from, string to)
        {
            if (String.IsNullOrEmpty(path)) return path;
            string f = from.TrimEnd('\\'), t = to.TrimEnd('\\');
            if (String.Equals(path, f, StringComparison.OrdinalIgnoreCase)) return t;
            if (path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) return t + path.Substring(f.Length);
            return path;
        }
    }
}
