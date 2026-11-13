using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 1.0.19 (13 Nov 2026, the user's in-game costume options: the parts added with "Add to outfit" are to be switched
    // in the game too). "Add to outfit" copies a part's files into the outfit's own folder, so the loader cannot tell a
    // part from the outfit. For a Slots Loader of the SF6 Mod Suite line that this Manager installed, while the switch
    // "Costume options in the game" is on (LoaderFlags.cs), the Manager says which is which in
    //   <game>\reframework\data\SF6_Costumes_Data\manager_parts.json     the list (format "sf6-manager-parts-1")
    //   <game>\reframework\data\SF6_Costumes_Data\manager_parts\<outfit>\<part id>\natives\...   every file of a part
    //   <game>\reframework\data\SF6_Costumes_Data\manager_parts\<outfit>\base\natives\...        the outfit's own files
    //                                                                    that a part which is on replaced
    // as the loader's docs/MANAGER_CONTRACT.md has it ("added parts as in-game options"). The loader then offers every
    // listed part as On / Off in the game's costume options. What holds, and what the loader checks by file size: the
    // outfit's folder is the outfit's own files with the parts that are on laid over them in the listed order.
    //
    // Kept in step where the loader's other settings are (LoaderFlags.Apply: after every committed change of the game
    // files, at start-up, at every refresh, which follows every change of an outfit, and when the switch changes).
    // Not wanted (the switch off, no such loader, SF6_Costumes_Data not there): the list and the folder are removed,
    // and nothing else, so the game folder is as it was. Never fails a change: a problem is logged once and that
    // outfit is not listed.
    //
    // Where the files come from. A file of a part that is on and that no later part replaced is in the outfit's
    // folder: copied from there (so it has the size the loader checks). Everything else is read from the mods the
    // install record names: the files of a part that is off, a file a later part replaced, and the outfit's own file
    // under a part (base). The outfit's own file list is its mod read again with the options of the record; it is
    // trusted only when it and the files of the parts that are on give exactly the files the record lists, each of
    // the outfit's own files that is still in the folder at the mod's size (a rebuilt colour swatch aside). A mod that
    // is gone or no longer matches leaves its outfit out of the list. Nothing is guessed.
    //
    // A second sync touches nothing: each outfit's folder keeps built.json (what was made, from what), and a file
    // that is already the right one is neither copied again nor given a new date (the loader's fingerprint holds the
    // path, size and date of these files; a new date would rebuild the game's outfits at its next start).
    // What ManagerParts keeps beside an outfit's folders (built.json): the entry as written and the files it stands on,
    // each with what it was made from.
    public sealed class ManagerPartsBuilt
    {
        public string Stamp = "", Entry = "";
        public Dictionary<string, ManagerPartsFile> Files = new Dictionary<string, ManagerPartsFile>();
    }
    public sealed class ManagerPartsFile { public long Size; public string From = ""; }

    internal static class ManagerParts
    {
        internal const string Format = "sf6-manager-parts-1", FileName = "manager_parts.json", FolderName = "manager_parts", BuiltName = "built.json", BaseName = "base";
        private const string Builder = "1";   // raised when the rules below change: every outfit is then made again
        internal static string FilePath(string gameRoot) { return Path.Combine(ManagerInstalls.Folder(gameRoot), FileName); }
        internal static string FolderPath(string gameRoot) { return Path.Combine(ManagerInstalls.Folder(gameRoot), FolderName); }
        // Checks: outfits read from their mods again (not taken from built.json), and files copied, since the start.
        internal static int Builds, Copies;
        // 7-Zip for a part kept in a RAR or 7Z archive: the one of the settings, asked for only when a mod is read.
        internal static Func<string> SevenZip = delegate
        {
            try { return File.Exists(Path.Combine(AppConfig.DataDir, "settings.json")) ? AppConfig.Load().SevenZipPath ?? "" : ""; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return ""; }
        };
        private static readonly HashSet<string> said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        // "Hair B from Hair Bundle"; a part that is its whole mod is named once; "· Hair only" for a part of the options.
        // The Installed page's list of added parts shows the same words (MainFormAddParts.cs).
        internal static string Title(PartGraft g)
        {
            string mod = String.IsNullOrEmpty(g.Mod) ? Path.GetFileNameWithoutExtension(g.SourcePath ?? "") : g.Mod;
            bool whole = g.Options == null || g.Options.Count == 0 || (g.Options.Count == 1 && String.Equals(g.Options[0], mod, StringComparison.OrdinalIgnoreCase));
            return (whole ? mod : Installer.GraftText(g)) + (g.Parts != null && g.Parts.Count > 0 ? " · " + OutfitParts.Labels(g.Parts) + " only" : "");
        }

        // A part's name in the list: 8 hex digits made from what the part is (its mod's file name, its options, the
        // parts taken of them), never from its place, its name or its outfit, so the choice a player made in the game
        // follows it through a reorder, a rename of the outfit and a moved library. `whole`: with the mod's full path
        // (two mods of one file name on one outfit).
        internal static string Id(PartGraft g, bool whole)
        {
            string source = (g.SourcePath ?? "").TrimEnd('\\', '/');
            string key = (whole ? source : Path.GetFileName(source)).ToLowerInvariant()
                + "\n" + String.Join("\n", (g.SelectedIds ?? new List<string>()).Select(s => (s ?? "").ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal))
                + "\n#" + String.Join(",", (g.Parts ?? new List<string>()).Select(s => (s ?? "").ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal));
            return ReplaceEngine.Sha(Encoding.UTF8.GetBytes(key)).Substring(0, 8);
        }
        internal static string[] Ids(IList<PartGraft> parts)
        {
            var ids = new string[parts.Count];
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Count; i++)
            {
                string id = Id(parts[i], false);
                if (taken.Contains(id)) id = Id(parts[i], true);
                for (int n = 2; taken.Contains(id); n++) id = Id(parts[i], true) + "-" + n;
                taken.Add(id); ids[i] = id;
            }
            return ids;
        }

        // The parts in the order applied, each with whether it is on: the record's Parts as they are, a part that is off
        // where it was when it was turned off (PartGraft.Place: so many parts that are on before it; unknown = last).
        internal static List<KeyValuePair<PartGraft, bool>> Ordered(InstallManifest m)
        {
            var list = (m.Parts ?? new List<PartGraft>()).Where(g => g != null && g.Files != null && g.Files.Count > 0).Select(g => new KeyValuePair<PartGraft, bool>(g, true)).ToList();
            foreach (PartGraft off in (m.OffParts ?? new List<PartGraft>()).Where(g => g != null))
            {
                if (list.Any(p => Installer.SamePart(p.Key, off))) continue;   // on again: the record of "off" is stale
                int at = list.Count;
                if (off.Place >= 0)
                {
                    int before = 0;
                    for (at = 0; at < list.Count && (before < off.Place || !list[at].Value); at++) if (list[at].Value) before++;
                }
                list.Insert(at, new KeyValuePair<PartGraft, bool>(off, false));
            }
            return list;
        }

        // The list and the folders follow the installed outfits; true when something in the game folder changed.
        // `wanted`: a suite-line loader of this Manager is installed and "Costume options in the game" is on.
        internal static bool Apply(string gameRoot, bool wanted)
        {
            try
            {
                if (String.IsNullOrEmpty(gameRoot)) return false;
                string data = ManagerInstalls.Folder(gameRoot), costumes = Path.Combine(gameRoot, "reframework", "costume_mods");
                if (!Directory.Exists(data)) return false;
                if (!wanted || !Directory.Exists(costumes)) return Remove(gameRoot);
                IDisposable hold;
                try { hold = Installer.AcquireMutation(costumes); }
                catch (IOException) { return false; }   // an outfit is being changed right now: the refresh after it comes here again
                using (hold) return Write(gameRoot, costumes);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.LogLine(FileName + " not written (" + ex.GetType().Name + ": " + ex.Message + ")."); return false; }
        }

        private static bool Remove(string gameRoot)
        {
            string data = ManagerInstalls.Folder(gameRoot), file = FilePath(gameRoot), folder = FolderPath(gameRoot);
            bool changed = false;
            if (File.Exists(file)) { Safety.NoReparseChain(file); File.Delete(file); changed = true; }
            if (Directory.Exists(folder)) { Safety.DeleteGenerated(folder, data); changed = true; }
            return changed;
        }

        private sealed class Listed { internal string Source, Key, Entry; }

        private static bool Write(string gameRoot, string costumes)
        {
            string root = Path.GetFullPath(costumes).TrimEnd('\\') + "\\", folder = FolderPath(gameRoot), file = FilePath(gameRoot);
            // The Manager's own outfits that are enabled (a folder two levels below costume_mods) and have a part
            var outfits = new List<KeyValuePair<Installer.ManagedManifestSnapshot, string>>();
            foreach (Installer.ManagedManifestSnapshot s in Installer.ManagedManifests(costumes))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(s.File));
                if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // a disabled outfit is no folder of costume_mods
                string rel = dir.Substring(root.Length).Replace('\\', '/');
                if (rel.Split('/').Length != 2 || Ordered(s.Manifest).Count == 0) continue;
                outfits.Add(new KeyValuePair<Installer.ManagedManifestSnapshot, string>(s, rel));
            }
            // Short folder names keep deep texture paths under Windows' limit; two outfits with the same first 12 digits
            // of their id (never seen) get the whole id.
            var shortIds = outfits.GroupBy(o => o.Key.Manifest.Id.Substring(0, 12), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            var listed = new List<Listed>();
            string sevenZip = null;
            Func<string> zip = delegate { return sevenZip ?? (sevenZip = SevenZip() ?? ""); };
            foreach (var o in outfits.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
            {
                InstallManifest m = o.Key.Manifest;
                string key = shortIds[m.Id.Substring(0, 12)] == 1 ? m.Id.Substring(0, 12) : m.Id, home = Path.Combine(folder, key);
                try
                {
                    string stamp = ReplaceEngine.Sha(Encoding.UTF8.GetBytes(Builder + "|" + o.Value + "|" + key + "|" + ReplaceEngine.Sha(o.Key.Before)));
                    ManagerPartsBuilt before = ReadBuilt(home);
                    if (before != null && before.Stamp == stamp && !String.IsNullOrEmpty(before.Entry) && before.Files != null && before.Files.Count > 0 && before.Files.All(f => f.Value != null && SizeOf(Safety.Under(home, f.Key)) == f.Value.Size))
                    { listed.Add(new Listed { Source = o.Value, Key = key, Entry = before.Entry }); continue; }
                    ManagerPartsBuilt made = Build(o.Key, o.Value, home, key, stamp, before, zip, ref changed);
                    listed.Add(new Listed { Source = o.Value, Key = key, Entry = made.Entry });
                }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException) throw;
                    // Not listed: the loader then leaves the outfit as its folder has it. Said once per run and reason.
                    bool first; lock (said) first = said.Add(m.Id + "|" + ex.Message);
                    if (first) Program.LogLine("Added parts of " + o.Value + " are not offered in the game (" + FileName + "): " + ex.Message.TrimEnd('.') + ".");
                    try { if (Directory.Exists(home)) { Safety.DeleteGenerated(home, folder); changed = true; } }
                    catch (Exception cleanup) { if (cleanup is OutOfMemoryException) throw; Program.Log(cleanup); }
                }
            }
            // Folders of outfits that are gone, off, disabled or without parts now
            if (Directory.Exists(folder))
            {
                var keep = new HashSet<string>(listed.Select(l => l.Key), StringComparer.OrdinalIgnoreCase);
                foreach (string entry in Directory.EnumerateFileSystemEntries(folder).ToList())
                {
                    if (keep.Contains(Path.GetFileName(entry))) continue;
                    Safety.NoReparse(entry);
                    if (Directory.Exists(entry)) Safety.DeleteGenerated(entry, folder); else File.Delete(entry);
                    changed = true;
                }
            }
            if (listed.Count == 0) return Remove(gameRoot) || changed;
            string text = "{\n  \"format\": " + Quote(Format) + ",\n  \"outfits\": [\n" + String.Join(",\n", listed.Select(l => l.Entry)) + "\n  ]\n}\n";
            if (File.Exists(file) && File.ReadAllText(file, Encoding.UTF8) == text) return changed;
            Safety.NoReparseChain(file);
            DurableFile.Write(file, new UTF8Encoding(false).GetBytes(text));
            return true;
        }

        private static long SizeOf(string file) { var info = new FileInfo(file); return info.Exists ? info.Length : -1; }
        private static ManagerPartsBuilt ReadBuilt(string home)
        {
            try { string file = Path.Combine(home, BuiltName); return File.Exists(file) ? Json.Read<ManagerPartsBuilt>(file) : null; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }
        private static string Norm(string path) { return (path ?? "").Replace('\\', '/').TrimStart('/'); }

        private sealed class Opened { internal SourceReader Reader; internal ModPackage Package; internal bool Folder; internal string Stamp; }
        // One file to have under the outfit's folder: from the outfit's folder (Copy) or from a mod (Planned of Source).
        private sealed class Want { internal string Rel, Target, From, Copy; internal long Size; internal PlannedFile Planned; internal Opened Source; internal string Group; }

        private static ManagerPartsBuilt Build(Installer.ManagedManifestSnapshot snapshot, string rel, string home, string key, string stamp, ManagerPartsBuilt before, Func<string> sevenZip, ref bool changed)
        {
            Builds++;
            InstallManifest m = snapshot.Manifest;
            string outfit = Path.GetDirectoryName(Path.GetFullPath(snapshot.File));
            List<KeyValuePair<PartGraft, bool>> parts = Ordered(m);
            string[] ids = Ids(parts.Select(p => p.Key).ToList());
            var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in m.Sha256) recorded[Norm(kv.Key)] = kv.Value ?? "";
            // The last part that is on and wrote a path is the one whose file the folder holds
            var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < parts.Count; k++) if (parts[k].Value) foreach (string path in parts[k].Key.Files) owner[Norm(path)] = k;

            var open = new Dictionary<string, Opened>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Func<string, string, Opened> source = delegate(string path, string whose)
                {
                    Opened o;
                    if (String.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) throw new IOException(whose + " is no longer where it was (" + Path.GetFileName(path ?? "") + ")");
                    if (open.TryGetValue(path, out o)) return o;
                    o = new Opened { Folder = Directory.Exists(path) };
                    o.Reader = new SourceReader(path, o.Folder || Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? "" : sevenZip());
                    open[path] = o;
                    o.Package = PackageReader.Load(path, o.Reader, false);
                    o.Stamp = o.Folder ? "" : SourceReader.Stamp(path);
                    return o;
                };
                Func<Opened, PlannedFile, string> from = delegate(Opened o, PlannedFile f)
                {
                    return "mod|" + o.Reader.Source.ToLowerInvariant() + "|" + f.Asset.EntryPath + "|" + f.Asset.Size + "|" + (o.Folder ? o.Reader.Entries[f.Asset.EntryPath].WriteTicks.ToString() : o.Stamp);
                };
                var wants = new Dictionary<string, Want>(StringComparer.OrdinalIgnoreCase);
                Action<string, string, Opened, PlannedFile> fromMod = delegate(string dir, string path, Opened o, PlannedFile f)
                {
                    string r = dir + "/" + path;
                    wants[r] = new Want { Rel = r, Target = Safety.Under(home, r), From = from(o, f), Size = f.Asset.Size, Planned = f, Source = o, Group = dir + "|" + o.Reader.Source };
                };

                // The outfit's own files (only asked for when a part is on: only then can a file of it be under a part)
                string baseDir = null;
                if (owner.Count > 0)
                {
                    Opened own = source(m.SourcePath, "the outfit's own mod");
                    List<string> selected = m.SelectedIds == null || m.SelectedIds.Count == 0 ? Composer.DefaultSelection(own.Package, false) : m.SelectedIds;
                    if (selected.Any(id => own.Package.Find(id) == null)) throw new IOException("the outfit's own mod no longer has the options it was installed with");
                    CompositionPlan plan = Composer.Plan(own.Package, selected);
                    var ownFiles = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in plan.Files) ownFiles[Norm(kv.Key)] = kv.Value;
                    if (ownFiles.Count == 0 || !new HashSet<string>(recorded.Keys, StringComparer.OrdinalIgnoreCase).SetEquals(ownFiles.Keys.Concat(owner.Keys)))
                        throw new IOException("the outfit's own mod no longer gives the files of the install record");
                    foreach (var kv in ownFiles.Where(f => !owner.ContainsKey(f.Key)))
                    {
                        long size = SizeOf(Safety.Under(outfit, kv.Key));
                        bool rebuilt = m.RebuiltSwatches != null && m.RebuiltSwatches.Keys.Any(s => String.Equals(Norm(s), kv.Key, StringComparison.OrdinalIgnoreCase));
                        if (size < 0 || (size != kv.Value.Asset.Size && !rebuilt)) throw new IOException("the outfit's own mod changed since the install (" + Path.GetFileName(kv.Key) + ")");
                    }
                    foreach (var kv in ownFiles.Where(f => owner.ContainsKey(f.Key))) { baseDir = BaseName; fromMod(BaseName, kv.Key, own, kv.Value); }
                }

                for (int k = 0; k < parts.Count; k++)
                {
                    PartGraft g = parts[k].Key; bool on = parts[k].Value;
                    string label = "the mod of the part " + Title(g);
                    Dictionary<string, PlannedFile> files = null;   // the part's files in its mod, read when one is needed
                    Func<Dictionary<string, PlannedFile>> read = delegate
                    {
                        if (files != null) return files;
                        Opened o = source(g.SourcePath, label);
                        CompositionPlan selection = Installer.SelectionFiles(o.Package, g.SelectedIds);
                        if (selection.Errors.Count == 0 && g.Parts != null && g.Parts.Count > 0) OutfitParts.Keep(selection, g.Parts, o.Reader);
                        if (selection.Errors.Count > 0) throw new IOException(label + " no longer has what was added (" + String.Join(" ", selection.Errors).TrimEnd('.') + ")");
                        files = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in selection.Files) files[Norm(kv.Key)] = kv.Value;
                        return files;
                    };
                    if (!on) { foreach (var kv in read()) fromMod(ids[k], kv.Key, open[g.SourcePath], kv.Value); continue; }
                    foreach (string raw in g.Files)
                    {
                        string path = Norm(raw), r = ids[k] + "/" + path;
                        if (owner[path] == k)
                        {
                            string inOutfit = Safety.Under(outfit, path), sha;
                            long size = SizeOf(inOutfit);
                            if (size < 0 || !recorded.TryGetValue(path, out sha)) throw new IOException("the outfit's folder no longer has " + Path.GetFileName(path) + " of the part " + Title(g));
                            wants[r] = new Want { Rel = r, Target = Safety.Under(home, r), From = "outfit|" + sha.ToLowerInvariant(), Size = size, Copy = inOutfit };
                        }
                        else
                        {
                            PlannedFile f;
                            if (!read().TryGetValue(path, out f)) throw new IOException(label + " no longer has " + Path.GetFileName(path));
                            fromMod(ids[k], path, open[g.SourcePath], f);
                        }
                    }
                }
                if (wants.Count == 0) throw new IOException("its parts have no files");
                LongPaths.Check(wants.Values.Select(w => w.Target));

                // What is there and not wanted goes; what is wanted and already the right file stays as it is
                Directory.CreateDirectory(home);
                Safety.NoReparseChain(home);
                foreach (string existing in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories).ToList())
                {
                    string r = existing.Substring(home.TrimEnd('\\').Length + 1).Replace('\\', '/');
                    if (wants.ContainsKey(r)) continue;
                    File.Delete(existing);
                    if (!r.Equals(BuiltName, StringComparison.OrdinalIgnoreCase)) changed = true;
                }
                var make = new List<Want>();
                foreach (Want w in wants.Values)
                {
                    ManagerPartsFile was;
                    if (before != null && before.Files != null && before.Files.TryGetValue(w.Rel, out was) && was != null && was.From == w.From && was.Size == w.Size && SizeOf(w.Target) == w.Size) continue;
                    make.Add(w);
                }
                if (make.Count > 0)
                {
                    string stage = Safety.NewFolder(Path.GetDirectoryName(home), ".t");   // beside the outfits' folders: the shortest paths
                    try
                    {
                        foreach (Want w in make.Where(x => x.Copy != null))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(w.Target));
                            string temporary = Path.Combine(stage, "c");
                            File.Copy(w.Copy, temporary, true);   // keeps the date of the outfit's file
                            if (SizeOf(temporary) != w.Size) throw new IOException("the outfit's folder changed while its parts were read");
                            if (File.Exists(w.Target)) File.Delete(w.Target);
                            File.Move(temporary, w.Target);
                            Copies++;
                        }
                        int n = 0;
                        foreach (var group in make.Where(x => x.Copy == null).GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase))
                        {
                            string to = Path.Combine(stage, "g" + n), scratch = Path.Combine(stage, "s" + n++);
                            Directory.CreateDirectory(to); Directory.CreateDirectory(scratch);
                            group.First().Source.Reader.CopyFiles(group.Select(x => x.Planned), to, scratch);
                            foreach (Want w in group)
                            {
                                string got = Safety.Under(to, w.Planned.Asset.DestinationPath);
                                if (SizeOf(got) != w.Size) throw new IOException("a mod changed while its part was read");
                                Directory.CreateDirectory(Path.GetDirectoryName(w.Target));
                                if (File.Exists(w.Target)) File.Delete(w.Target);
                                File.Move(got, w.Target);
                                Copies++;
                            }
                        }
                    }
                    finally { try { Safety.DeleteGenerated(stage, Path.GetDirectoryName(home)); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
                    changed = true;
                }
                PruneEmpty(home);

                var entry = new StringBuilder();
                entry.Append("    {\n      \"source\": ").Append(Quote(rel)).Append(",\n      \"base\": ").Append(baseDir == null ? "null" : Quote(FolderName + "/" + key + "/" + BaseName)).Append(",\n      \"parts\": [\n");
                for (int k = 0; k < parts.Count; k++)
                    entry.Append("        { \"id\": ").Append(Quote(ids[k])).Append(", \"name\": ").Append(Quote(Title(parts[k].Key))).Append(", \"on\": ").Append(parts[k].Value ? "true" : "false")
                         .Append(", \"dir\": ").Append(Quote(FolderName + "/" + key + "/" + ids[k])).Append(" }").Append(k < parts.Count - 1 ? ",\n" : "\n");
                entry.Append("      ]\n    }");
                var built = new ManagerPartsBuilt { Stamp = stamp, Entry = entry.ToString() };
                foreach (Want w in wants.Values) built.Files[w.Rel] = new ManagerPartsFile { Size = w.Size, From = w.From };
                Json.Write(Path.Combine(home, BuiltName), built);
                return built;
            }
            finally { foreach (Opened o in open.Values) if (o.Reader != null) o.Reader.Dispose(); }
        }

        private static void PruneEmpty(string folder)
        {
            foreach (string child in Directory.EnumerateDirectories(folder).ToList())
            {
                Safety.NoReparse(child);
                PruneEmpty(child);
                if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            }
        }
        // As ManagerInstalls writes its texts: only `"`, `\` and control characters escaped, the rest as UTF-8.
        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
