using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.20 Convert RAR and 7Z to ZIP (ArchiveConvert): every record that names the archive names the new ZIP, so
    // installed copies don't show "Source gone" or "Source changed". Modelled on SourceRename.Run, which moves the same
    // records when a mod is renamed, plus what a rename never needed (the ZIP has other bytes than the archive):
    //   in one transaction, under the costume-root and game locks, put back on any failure:
    //     - extra outfit install records (.slots-installer.json, enabled and disabled): SourcePath, and the parts added
    //       from it (Parts, OffParts)
    //     - Install all variants records (archive-installs.json: SourcePath, AddonSources)
    //     - Replace original records (replace-state.json): SourcePath, and for a replacement whose files are read back
    //       from the archive (ArchiveEntry) the archive identity: ArchiveSha / ArchiveSize / ArchiveStamp of the ZIP. The
    //       entry paths are the same, and ArchiveConvert checked every file's size and CRC.
    //     - outfit recipes (Data\outfit-recipes: Base and Parts)
    //   after it (logged, never undone, as a rename does): presets (OutfitSetStore.ConvertSource), the name typed in the
    //   Name view (LibraryNames), and a Rename history event so the mod's history and Last used follow it.
    // Left as they are: copies kept in presets, removed sets and the recovery folders (their files are byte-checked), the
    // history log, and the library index (the ZIP is listed by the next scan; the archive's record goes with it).
    internal static class ConvertRecords
    {
        internal sealed class Context
        {
            internal string CostumeRoot, DataDir;
            internal ReplaceEngine Engine;
        }

        // Why the conversion can't start now, or null: a change the records depend on is not finished.
        internal static string Blocker(Context c)
        {
            if (c == null || c.Engine == null) return null;
            try
            {
                if (c.Engine.JournalPending) return "A change to the game folder didn't finish. Restart the Manager to complete it first.";
                if (File.Exists(Path.Combine(c.Engine.DataDir, "replace-trash-pending.json"))) return "A change to the game folder didn't finish. Restart the Manager to complete it first.";
                if (File.Exists(ModMigration.Pending(c.Engine))) return "A migration from another mod manager is in progress. Finish or forget it first (Settings → Migrate installed mods).";
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return null;
        }

        // Returns how many records changed. Throws when one can't be written; everything this call changed is then back.
        internal static int Move(Context c, string from, string to)
        {
            if (c == null) return 0;
            string root = c.CostumeRoot;
            bool hasCostumes = false;
            try { hasCostumes = !String.IsNullOrWhiteSpace(root) && Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(root)) ?? ""); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            ReplaceEngine engine = c.Engine;
            bool hasState = engine != null && File.Exists(engine.StatePath);
            string fromStamp = MainForm.SourceStamp(from);   // while the archive is there: preset pointers saved with it
            int changed = 0;
            using (hasCostumes ? Installer.AcquireMutation(root) : null)
            using (hasState ? engine.Lock() : null)
            {
                if (hasState && engine.JournalPending) throw new IOException("a change to the game folder didn't finish. Restart the Manager to complete it first.");
                var undo = new Stack<Action>();
                try
                {
                    if (hasCostumes) changed += Manifests(root, from, to, undo);
                    int copies = ArchiveInstalls.RenameSource(from, to);
                    if (copies > 0) { changed += copies; undo.Push(delegate { ArchiveInstalls.RenameSource(to, from); }); }
                    if (hasState) changed += State(engine, from, to, undo);
                    if (!String.IsNullOrEmpty(c.DataDir)) changed += Recipes(c.DataDir, from, to, undo);
                }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException) throw;
                    string rollback = "";
                    while (undo.Count > 0)
                    {
                        try { undo.Pop()(); }
                        catch (Exception again) { if (again is OutOfMemoryException) throw; Program.Log(again); rollback = " Some records could not be put back; see the log."; }
                    }
                    throw new IOException("its records couldn't be updated (" + ex.Message.TrimEnd('.') + ")." + rollback, ex);
                }
            }
            // The rest follows the ZIP the way a rename's does: logged, never undone.
            string toStamp = MainForm.SourceStamp(to), sha = null; long size = 0;
            try { sha = BundleLayout.ArchiveHash(to, out size); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            if (hasCostumes) OutfitSetStore.ConvertSource(root, from, to, fromStamp, toStamp, sha, size);
            try { LibraryNames.Move(from, to); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            History.Record(null, new HistoryEvent { Kind = HistoryKinds.Rename, Source = to, PrevId = History.Normalize(from), Name = Path.GetFileNameWithoutExtension(to), Detail = "converted from " + Path.GetFileName(from) });   // never throws
            LibraryUsage.ClearCache();
            return changed;
        }

        // Extra outfits (enabled and disabled) made from the archive, or with parts added from it.
        private static int Manifests(string root, string from, string to, Stack<Action> undo)
        {
            int changed = 0;
            foreach (Installer.ManagedManifestSnapshot s in Installer.ManagedManifests(root).ToList())
            {
                InstallManifest m = s.Manifest;
                bool hit = false;
                if (Same(m.SourcePath, from)) { m.SourcePath = to; hit = true; }
                foreach (PartGraft g in (m.Parts ?? new List<PartGraft>()).Concat(m.OffParts ?? new List<PartGraft>()))
                    if (g != null && Same(g.SourcePath, from)) { g.SourcePath = to; hit = true; }
                if (!hit) continue;
                DurableFile.WriteJson(s.File, m);
                string file = s.File; byte[] before = s.Before;
                undo.Push(delegate { DurableFile.Write(file, before); });
                changed++;
            }
            return changed;
        }

        // Replace original and tool records of the archive. The caller holds the game lock.
        private static int State(ReplaceEngine engine, string from, string to, Stack<Action> undo)
        {
            ReplaceState state = engine.LoadState();
            List<ReplaceMod> mods = state.Mods.Where(m => m != null && !LoaderCostumePak.IsLoaderMod(m) && Same(m.SourcePath, from)).ToList();
            if (mods.Count == 0) return 0;
            string sha = null, stamp = null; long size = 0;
            if (mods.Any(m => m.ArchiveSha != null))
            {
                // A replacement that reads its files back from the archive: each of them must be in the ZIP at its entry path.
                using (var reader = new SourceReader(to, ""))
                    foreach (ReplaceMod m in mods.Where(m => m.ArchiveSha != null))
                        foreach (ModFile f in m.Files.Where(f => f.ArchiveEntry != null))
                        {
                            SourceEntry e;
                            if (!reader.Entries.TryGetValue(f.ArchiveEntry, out e) || e.Size != f.Size) throw new IOException("the ZIP has no " + f.ArchiveEntry + " of " + f.Size + " bytes for " + m.Name);
                        }
                sha = BundleLayout.ArchiveHash(to, out size);
                stamp = SourceReader.Stamp(to);
            }
            foreach (ReplaceMod m in mods)
            {
                m.SourcePath = to;
                if (m.ArchiveSha != null) { m.ArchiveSha = sha; m.ArchiveSize = size; m.ArchiveStamp = stamp; }
            }
            string file = engine.StatePath;
            byte[] before = File.ReadAllBytes(file);
            state.Generation++;   // like every direct state write
            DurableFile.WriteJson(file, state);
            undo.Push(delegate { DurableFile.Write(file, before); });
            return mods.Count;
        }

        // Outfit recipes with the archive as their base or one of their parts.
        private static int Recipes(string dataDir, string from, string to, Stack<Action> undo)
        {
            string folder = OutfitRecipes.Folder(dataDir);
            if (!Directory.Exists(folder)) return 0;
            int changed = 0;
            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                if (new FileInfo(file).Length > 1024 * 1024) continue;
                byte[] before = File.ReadAllBytes(file);
                OutfitRecipe r;
                try { r = Json.Read<OutfitRecipe>(file); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); continue; }   // OutfitRecipes.List skips it too
                if (r == null || r.Format != OutfitRecipes.FormatName) continue;
                bool hit = false;
                foreach (RecipeLayer l in new[] { r.Base }.Concat(r.Parts ?? new List<RecipeLayer>()))
                    if (l != null && Same(l.SourcePath, from)) { l.SourcePath = to; hit = true; }
                if (!hit) continue;
                DurableFile.WriteJson(file, r);
                string path = file;
                undo.Push(delegate { DurableFile.Write(path, before); });
                changed++;
            }
            return changed;
        }

        internal static bool Same(string a, string b)
        {
            if (String.IsNullOrWhiteSpace(a) || String.IsNullOrWhiteSpace(b)) return false;
            try { return String.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
    }
}
