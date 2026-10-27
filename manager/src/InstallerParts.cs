using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.6: options of another mod added to a managed extra outfit (manifest Parts). SourcePath + SelectedIds give the
    // files again; Files are the installed paths they wrote.
    public sealed class PartGraft
    {
        public string SourcePath = "", Mod = "", AddedUtc = "";
        public List<string> SelectedIds = new List<string>();
        public List<string> Options = new List<string>();
        public List<string> Files = new List<string>();
    }

    // 1.0.6 (the user): Add to outfit copies exactly the options ticked in the Mod library into an extra outfit the
    // Manager installed: their natives files, later options winning, overwrite the outfit's files of the same path;
    // nothing else is added or removed. The selection needs no main files (a bundle's hair option alone is fine).
    // Materials name their textures by path, so files made for another outfit slot are refused: every model file must be
    // in a costume folder the outfit is built on, or the shared 000 folder.
    public static partial class Installer
    {
        private static readonly Regex ModelFolder = new Regex(@"^natives/stm/(?:streaming/)?product/model/esf/esf(\d{3})/(\d{3})/", RegexOptions.IgnoreCase);
        private static readonly Regex BodyMesh = new Regex(@"^natives/stm/product/model/esf/esf(\d{3})/(\d{3})/01/[^/]+\.mesh\.\d+$", RegexOptions.IgnoreCase);

        // The natives files of these options, in selection order (a later option wins a shared path). Dummies and options
        // without natives files add nothing; no main-file, parent or choose-one rule applies.
        internal static CompositionPlan SelectionFiles(ModPackage package, IEnumerable<string> ids)
        {
            var plan = new CompositionPlan { Package = package };
            foreach (string id in (ids ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ModComponent c = package.Find(id);
                if (c == null) { plan.Errors.Add("An option no longer exists in this mod. Reload it."); continue; }
                if (c.IsDummy || c.Assets.Count == 0) continue;
                plan.Layers.Add(c);
                foreach (NativeAsset f in c.Assets) plan.Files[f.DestinationPath] = new PlannedFile { Asset = f, Component = c };
            }
            if (plan.Files.Count == 0 && plan.Errors.Count == 0) plan.Errors.Add("Tick the options to add; they need files under natives.");
            return plan;
        }

        // Fighter codes and costume folders ("001", "000") the files' model paths use.
        internal static HashSet<string> SelectionCodes(CompositionPlan plan)
        {
            return new HashSet<string>(plan.Files.Keys.SelectMany(p => Regex.Matches(p, @"/esf/esf(\d{3})/", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value)), StringComparer.Ordinal);
        }
        internal static HashSet<string> SelectionFolders(CompositionPlan plan)
        {
            return new HashSet<string>(plan.Files.Keys.Select(p => ModelFolder.Match(p)).Where(m => m.Success).Select(m => m.Groups[2].Value), StringComparer.Ordinal);
        }

        // Costume folders (FFF, not 000) an installed outfit has a body for.
        internal static List<string> OutfitFolders(InstalledOutfit outfit)
        {
            var folders = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                string esf = Path.Combine(outfit.Folder, "natives", "stm", "product", "model", "esf");
                if (Directory.Exists(esf))
                    foreach (string file in Directory.EnumerateFiles(esf, "*.mesh.*", SearchOption.AllDirectories))
                    {
                        Match m = BodyMesh.Match("natives/stm/product/model/esf/" + file.Substring(esf.Length).TrimStart('\\').Replace('\\', '/'));
                        if (m.Success && m.Groups[2].Value != "000") folders.Add(m.Groups[2].Value);
                    }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return folders.ToList();
        }
        // The outfit slots ("Outfit 1") the files are made for that an outfit built on `outfitFolders` doesn't have, or null.
        internal static string FolderMismatch(IEnumerable<string> fileFolders, IList<string> outfitFolders)
        {
            var other = fileFolders.Where(f => f != "000" && !outfitFolders.Contains(f)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            return other.Count == 0 ? null : SlotText(other);
        }
        internal static string SlotText(IEnumerable<string> folders) { return String.Join(", ", folders.Select(f => "Outfit " + Int32.Parse(f))); }
        internal static string OptionText(CompositionPlan plan) { return String.Join(" + ", plan.Layers.Select(c => c.Name)); }

        // ---- Add the selection to an installed managed outfit -----------------------------------------------------------
        public static InstallResult AddToOutfit(InstalledOutfit outfit, string costumeRoot, CompositionPlan selection, string sevenZipPath)
        {
            using (AcquireMutation(costumeRoot)) return AddToOutfitLocked(outfit, costumeRoot, selection, sevenZipPath);
        }
        private static InstallResult AddToOutfitLocked(InstalledOutfit outfit, string costumeRoot, CompositionPlan selection, string sevenZipPath)
        {
            if (selection == null || selection.Files.Count == 0 || selection.Errors.Count > 0) throw new IOException(selection == null ? "Open a mod first." : String.Join(" ", selection.Errors.DefaultIfEmpty("Tick the options to add.")));
            string root = ValidateRoot(costumeRoot); InstallManifest m = ValidateOutfit(outfit, root);
            string code = Characters.ByCode.First(k => k.Value == m.Character).Key;
            var codes = SelectionCodes(selection);
            if (codes.Any(c => c != code)) throw new IOException("These files belong to " + String.Join(", ", codes.Where(c => c != code).Select(c => Characters.ByCode.ContainsKey(c) ? Characters.ByCode[c] : "esf" + c)) + ", not " + m.Character + "; the Manager can't retarget mods.");
            List<string> folders = OutfitFolders(outfit);
            string mismatch = FolderMismatch(SelectionFolders(selection), folders);
            if (mismatch != null) throw new IOException("Can't add to " + m.Name + ": these files are made for " + mismatch + (folders.Count == 0 ? "" : " and it is built on " + SlotText(folders)) + ". Materials point at their own outfit slot's files.");

            string parent = Path.GetDirectoryName(root);
            string transactionRoot = Path.Combine(parent, ".slotsloader-installer-staging"); Safety.NoReparseChain(transactionRoot); Directory.CreateDirectory(transactionRoot);
            string transaction = Safety.NewFolder(transactionRoot, "");
            string stage = Path.Combine(transaction, "outfit");
            string reviewedManifest = File.ReadAllText(Path.Combine(outfit.Folder, ManifestName));
            string backup = null; bool committed = false;
            try
            {
                // The outfit as installed (its own source may be gone), then the selection on top.
                var hashes = new Dictionary<string, string>(m.Sha256, StringComparer.OrdinalIgnoreCase);
                CopyTree(outfit.Folder, stage);
                var graft = new PartGraft { SourcePath = selection.Package.SourcePath, Mod = selection.Package.Name ?? "", AddedUtc = DateTime.UtcNow.ToString("o"), SelectedIds = selection.Layers.Select(c => c.Id).ToList(), Options = selection.Layers.Select(c => c.Name).ToList() };
                ApplyGraft(stage, hashes, graft, transaction, sevenZipPath, selection);
                // The same options of the same mod added again replace that entry.
                var grafts = (m.Parts ?? new List<PartGraft>()).Where(g => !(String.Equals(g.SourcePath, graft.SourcePath, StringComparison.OrdinalIgnoreCase) && new HashSet<string>(g.SelectedIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase).SetEquals(graft.SelectedIds))).ToList();
                grafts.Add(graft);
                var manifest = new InstallManifest { Id = m.Id, Name = m.Name, Character = m.Character, SourcePath = m.SourcePath, CreatedUtc = m.CreatedUtc, SelectedIds = m.SelectedIds, Sha256 = hashes, Parts = grafts };
                Json.Write(Path.Combine(stage, ManifestName), manifest);
                OperationContext.ThrowIfCancellationRequested();
                using (OperationContext.NonCancellable())
                {
                    InstallManifest now = ManagedManifest(outfit.Folder, m.Character);
                    if (now == null || now.Id != m.Id || File.ReadAllText(Path.Combine(outfit.Folder, ManifestName)) != reviewedManifest) throw new IOException("The outfit changed while adding. Refresh and try again.");
                    string backups = Path.Combine(parent, ".slotsloader-installer-backups"); Safety.NoReparseChain(backups); Directory.CreateDirectory(backups);
                    backup = Safety.Under(backups, Guid.NewGuid().ToString("N")); MoveDirectory(outfit.Folder, backup);
                    try { MoveDirectory(stage, outfit.Folder); committed = true; }
                    catch { if (!Directory.Exists(outfit.Folder)) MoveDirectory(backup, outfit.Folder); throw; }
                }
                OutfitHistory.Changed(HistoryKinds.Update, outfit, "Added " + OptionText(selection) + " from " + graft.Mod);
                if (backup != null) { try { Safety.DeleteGenerated(backup, Path.GetDirectoryName(backup)); backup = null; } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
                return new InstallResult { Folder = outfit.Folder, FileCount = graft.Files.Count, Message = "Added " + OptionText(selection) + " from " + graft.Mod + " to " + m.Name + " · " + ReplacePlanner.N(graft.Files.Count, "file") + ". Restart SF6 to load it." };
            }
            finally
            {
                try { Safety.DeleteGenerated(transaction, transactionRoot); } catch { }
                if (!committed && backup != null && !Directory.Exists(outfit.Folder) && Directory.Exists(backup)) MoveDirectory(backup, outfit.Folder);
            }
        }

        // Copies the graft's options from its mod into `stage`, overwriting files of the same path. `hashes` and
        // graft.Files follow. `opened` (the selection the user saw) must still match the mod; null = a reinstall.
        private static void ApplyGraft(string stage, Dictionary<string, string> hashes, PartGraft graft, string transaction, string sevenZipPath, CompositionPlan opened)
        {
            using (var reader = new SourceReader(graft.SourcePath, sevenZipPath))
            {
                ModPackage fresh = PackageReader.Load(graft.SourcePath, reader, false);
                CompositionPlan current = SelectionFiles(fresh, graft.SelectedIds);
                if (current.Errors.Count > 0) throw new IOException(String.Join(" ", current.Errors));
                if (opened != null && (current.Files.Count != opened.Files.Count || current.Files.Any(k => !opened.Files.ContainsKey(k.Key) || opened.Files[k.Key].Asset.Size != k.Value.Asset.Size || opened.Files[k.Key].Asset.EntryPath != k.Value.Asset.EntryPath)))
                    throw new IOException("The mod changed after it was opened. Reload it and try again.");
                foreach (string path in current.Files.Keys) { string file = Safety.Under(stage, path); if (File.Exists(file)) File.Delete(file); }
                string scratch = Safety.NewFolder(transaction, "a");
                Dictionary<string, string> copied = reader.CopyFiles(current.Files.Values, stage, scratch);
                foreach (var kv in copied) hashes[kv.Key] = kv.Value;
                graft.Files = copied.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                graft.Options = current.Layers.Select(c => c.Name).ToList();
                if (String.IsNullOrEmpty(graft.Mod)) graft.Mod = fresh.Name ?? "";
            }
        }
        internal static string GraftText(PartGraft g)
        {
            string options = g.Options != null && g.Options.Count > 0 ? String.Join(" + ", g.Options) : "options";
            return options + " from " + (String.IsNullOrEmpty(g.Mod) ? Path.GetFileName(g.SourcePath ?? "") : g.Mod);
        }

        // Puts the added options back on a reinstalled outfit (Edit options, a changed source), in the order they were
        // added. One whose mod is gone, changed or made for another outfit slot is left out and named in `dropped`.
        private static List<PartGraft> ReapplyParts(InstallManifest previous, string stage, Dictionary<string, string> hashes, string transaction, string sevenZipPath, List<string> dropped)
        {
            var kept = new List<PartGraft>();
            if (previous == null || previous.Parts == null) return kept;
            var folders = new List<string>();
            foreach (string path in hashes.Keys) { Match body = BodyMesh.Match(path); if (body.Success && body.Groups[2].Value != "000" && !folders.Contains(body.Groups[2].Value)) folders.Add(body.Groups[2].Value); }
            foreach (PartGraft graft in previous.Parts)
            {
                OperationContext.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(graft.SourcePath) && !Directory.Exists(graft.SourcePath)) throw new IOException("its mod is gone");
                    string mismatch = FolderMismatch((graft.Files ?? new List<string>()).Select(p => ModelFolder.Match(p)).Where(x => x.Success).Select(x => x.Groups[2].Value), folders);
                    if (mismatch != null) throw new IOException("made for " + mismatch);
                    ApplyGraft(stage, hashes, graft, transaction, sevenZipPath, null);
                    kept.Add(graft);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); dropped.Add(GraftText(graft) + " (" + ex.Message.TrimEnd('.') + ")"); }
            }
            return kept;
        }

        private static void CopyTree(string from, string to)
        {
            Safety.NoReparseChain(from);
            Directory.CreateDirectory(to);
            foreach (string directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("The outfit folder contains a link; the Manager won't copy it.");
                Directory.CreateDirectory(Path.Combine(to, directory.Substring(from.Length).TrimStart('\\')));
            }
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                OperationContext.ThrowIfCancellationRequested();
                File.Copy(file, Path.Combine(to, file.Substring(from.Length).TrimStart('\\')));
            }
        }
    }
}
