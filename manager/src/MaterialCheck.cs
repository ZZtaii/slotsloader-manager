using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (the user installed a recipe, Goth Juri with Swimsuit Juri's "skin" option on top, and the outfit did not
    // show in game): a material file (.mdf2) names its textures by path. An option that is only a material file works
    // with the textures its own mod brings; put on top of another mod, it takes the place of that mod's material file
    // and points at textures nobody installs. This reads the material files that win in a recipe and names the ones
    // that land on another mod's model and use textures that neither the game nor the recipe has. It never blocks an
    // install: it is a note.
    internal static class MaterialCheck
    {
        internal sealed class Layer
        {
            internal string Shown = "", Mod = "", Source = "";
            internal List<string> Ids = new List<string>();
            internal List<string> Parts = new List<string>();   // only these parts of the mod (OutfitParts); empty = all
        }

        private static readonly Regex TexturePath = new Regex(@"[A-Za-z0-9_][A-Za-z0-9_\.\-@ ]*/[A-Za-z0-9_/\.\-@ ]+\.tex(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
        private static readonly Regex MaterialFile = new Regex(@"\.mdf2\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ModelFile = new Regex(@"\.mesh\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VersionSuffix = new Regex(@"\.\d+$", RegexOptions.CultureInvariant);

        // The textures a material file names, as natives paths without the version ("natives/stm/product/.../x_albd.tex").
        internal static List<string> Textures(byte[] mdf2)
        {
            var found = new List<string>();
            if (mdf2 == null || mdf2.Length < 4) return found;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The names are UTF-16; a table that starts on an odd byte is read by the second pass.
            for (int start = 0; start < 2; start++)
            {
                string text = Encoding.Unicode.GetString(mdf2, start, (mdf2.Length - start) & ~1);
                foreach (Match m in TexturePath.Matches(text))
                {
                    string p = "natives/stm/" + m.Value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
                    if (seen.Add(p)) found.Add(p);
                }
            }
            return found;
        }

        // Whether the game has a texture: in one of its PAKs, or as a loose file another mod installed. Null when the
        // game can't be read (then nothing is judged).
        internal static Func<string, bool> GameLookup(string gameRoot)
        {
            if (String.IsNullOrWhiteSpace(gameRoot) || !File.Exists(Path.Combine(gameRoot, "re_chunk_000.pak"))) return null;
            GamePaks paks = GamePaks.Load(gameRoot);
            if (paks.Base == null) return null;
            string version = GameReference.Tables(gameRoot).TexVersion;
            List<PakIndex> all = paks.Writable.Concat(paks.Patches).ToList();
            return delegate(string texture)
            {
                string full = texture + "." + version;
                ulong hash = PakHash.PathHash(full);
                if (all.Any(p => p.Contains(hash))) return true;
                try { return File.Exists(Path.Combine(gameRoot, full.Replace('/', Path.DirectorySeparatorChar))); }
                catch (ArgumentException) { return false; }
            };
        }

        internal static List<Layer> Layers(OutfitRecipe recipe)
        {
            var layers = new List<Layer>();
            recipe = recipe == null || recipe.Base == null ? null : recipe.Effective();   // what is turned off is not judged
            if (recipe == null) return layers;
            foreach (RecipeLayer l in Enumerable.Reverse(new[] { recipe.Base }.Concat(recipe.Parts ?? new List<RecipeLayer>())))
            {
                List<string> options = l.ShownOptions.ToList();
                bool whole = options.Count == 0 || options.Count > 2 || (options.Count == 1 && String.Equals(options[0], l.ModName, StringComparison.OrdinalIgnoreCase));
                layers.Add(new Layer { Shown = whole ? l.ModName : String.Join(" + ", options) + " from " + l.ModName, Mod = l.ModName, Source = l.SourcePath ?? "", Ids = (l.SelectedIds ?? new List<string>()).ToList(), Parts = (l.Parts ?? new List<string>()).ToList() });
            }
            return layers;
        }
        internal static List<Layer> Layers(RecipeDraft draft)
        {
            if (draft == null) return new List<Layer>();
            return draft.Layers.Select(l => new Layer { Shown = RecipeDraft.Shown(l), Mod = String.IsNullOrEmpty(l.Mod) ? Path.GetFileNameWithoutExtension(l.SourcePath ?? "") : l.Mod, Source = l.SourcePath ?? "", Ids = l.SelectedIds.ToList(), Parts = (l.Parts ?? new List<string>()).ToList() }).ToList();
        }
        // Changes when the layers, their options, their order or a source file change.
        internal static string Key(IList<Layer> topFirst, string gameRoot)
        {
            var text = new StringBuilder(gameRoot ?? "");
            foreach (Layer l in topFirst ?? new List<Layer>())
            {
                string stamp = "";
                try { if (File.Exists(l.Source)) stamp = SourceReader.Stamp(l.Source); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                text.Append('\n').Append(l.Source).Append('|').Append(stamp).Append('|').Append(String.Join("\t", l.Ids)).Append('|').Append(String.Join(",", l.Parts));
            }
            return text.ToString();
        }

        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, List<string>> cache = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // The notes for these layers (the winner first); empty when nothing is wrong or nothing can be judged.
        internal static List<string> Notes(IList<Layer> topFirst, string sevenZip, string gameRoot)
        {
            if (topFirst == null || topFirst.Count == 0) return new List<string>();
            string key = Key(topFirst, gameRoot);
            lock (cacheLock) { List<string> known; if (cache.TryGetValue(key, out known)) return known.ToList(); }
            List<string> notes = Read(topFirst, sevenZip, GameLookup(gameRoot));
            lock (cacheLock) { if (cache.Count > 32) cache.Clear(); cache[key] = notes; }
            return notes.ToList();
        }

        private static string Bare(string path) { return VersionSuffix.Replace(ModFixes.NormalizeNativePath(path), ""); }

        internal static List<string> Read(IList<Layer> topFirst, string sevenZip, Func<string, bool> inGame)
        {
            var notes = new List<string>();
            if (inGame == null) return notes;
            var packages = new List<ModPackage>();
            var plans = new List<Dictionary<string, PlannedFile>>();
            foreach (Layer layer in topFirst)
            {
                ModPackage package = PackageReader.Load(layer.Source, sevenZip, false);
                List<string> ids = layer.Ids.Count > 0 ? layer.Ids : Composer.DefaultSelection(package, false);
                var files = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in Installer.SelectionFiles(package, ids.Where(id => package.Find(id) != null), layer.Parts, sevenZip).Files) files[ModFixes.NormalizeNativePath(pair.Key)] = pair.Value;
                packages.Add(package); plans.Add(files);
            }
            // Every file of the recipe, without its version: a texture counts whichever layer brings it.
            var brought = new HashSet<string>(plans.SelectMany(p => p.Keys).Select(Bare), StringComparer.OrdinalIgnoreCase);
            var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < topFirst.Count; i++)
            {
                List<KeyValuePair<string, PlannedFile>> materials = plans[i].Where(f => MaterialFile.IsMatch(f.Key) && !decided.Contains(f.Key)).ToList();
                foreach (string path in plans[i].Keys) decided.Add(path);
                if (materials.Count == 0) continue;
                var missing = new List<string>(); var files = new List<string>(); string replaced = null;
                using (var reader = new SourceReader(topFirst[i].Source, sevenZip))
                    foreach (var material in materials)
                    {
                        OperationContext.ThrowIfCancellationRequested();
                        if (material.Value.Asset.Size > 64 * 1024 * 1024) continue;
                        List<string> textures = Textures(reader.ReadBytes(material.Value.Asset.EntryPath, 64 * 1024 * 1024));
                        // A file none of whose textures the game knows was not read right: say nothing about it.
                        if (!textures.Any(inGame)) continue;
                        List<string> gone = textures.Where(t => !brought.Contains(t) && !inGame(t)).ToList();
                        if (gone.Count == 0) continue;
                        // Only when it lands on another mod's model: it takes the place of that mod's material file, or
                        // the model beside it is another mod's. A mod's own material file with a texture name that
                        // leads nowhere is common and loads fine (the game falls back), so that alone says nothing.
                        string other = null;
                        for (int below = i + 1; below < topFirst.Count && other == null; below++)
                            if (plans[below].ContainsKey(material.Key) && !ReplacePlanner.SameSource(topFirst[below].Source, topFirst[i].Source)) other = topFirst[below].Mod;
                        if (other == null)
                        {
                            string folder = material.Key.Substring(0, material.Key.LastIndexOf('/') + 1);
                            for (int j = 0; j < topFirst.Count && other == null; j++)
                                if (plans[j].Keys.Any(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && k.IndexOf('/', folder.Length) < 0 && ModelFile.IsMatch(k)))
                                { if (ReplacePlanner.SameSource(topFirst[j].Source, topFirst[i].Source)) break; other = topFirst[j].Mod; }
                        }
                        if (other == null) continue;
                        if (replaced == null) replaced = other;
                        files.Add(Path.GetFileName(material.Key));
                        foreach (string t in gone) if (!missing.Contains(t, StringComparer.OrdinalIgnoreCase)) missing.Add(t);
                    }
                if (missing.Count == 0) continue;
                // The options of the same mod that bring those textures (the ones the recipe left out).
                var used = new HashSet<string>(plans[i].Values.Select(f => f.Component.Id), StringComparer.OrdinalIgnoreCase);
                var wanted = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
                List<string> bringers = packages[i].Components.Where(c => !c.IsDummy && !used.Contains(c.Id) && c.Assets.Any(a => wanted.Contains(Bare(a.DestinationPath))))
                    .Select(c => RecipeDraft.NameOf(c, packages[i])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> names = missing.Select(t => Path.GetFileNameWithoutExtension(t)).ToList();
                string list = String.Join(", ", names.Take(3)) + (names.Count > 3 ? " and " + (names.Count - 3) + " more" : "");
                string note = topFirst[i].Shown + " is " + (files.Count == 1 ? "a material file" : "material files") + " made for another model. Here it goes on " + replaced + "'s model, and it uses "
                    + ReplacePlanner.N(missing.Count, "texture") + " that neither the game nor this recipe has (" + list + "). " + replaced + " will not look right in game and may not show at all.";
                if (bringers.Count > 0) note += " Those textures come with " + (bringers.Count <= 2 ? String.Join(" and ", bringers) : ReplacePlanner.N(bringers.Count, "other option")) + " of " + topFirst[i].Mod + ".";
                note += " Take it off, or use a look that " + replaced + " brings itself.";
                notes.Add(note);
            }
            return notes;
        }

        // Reads off the UI thread and answers on it. A failure is logged and answers with no notes.
        internal static void Begin(Control owner, List<Layer> topFirst, string sevenZip, string gameRoot, Action<List<string>> done)
        {
            if (owner == null || !owner.IsHandleCreated || topFirst == null || topFirst.Count == 0 || String.IsNullOrWhiteSpace(gameRoot)) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> notes;
                try { notes = Notes(topFirst, sevenZip, gameRoot); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); notes = new List<string>(); }
                try { owner.BeginInvoke((MethodInvoker)delegate { if (!owner.IsDisposed) done(notes); }); }
                catch (InvalidOperationException) { }
            });
        }
    }
}
