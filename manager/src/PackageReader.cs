using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    public static class PackageReader
    {
        public static ModPackage Load(string sourcePath, string sevenZipPath)
        {
            return Load(sourcePath,sevenZipPath,false);
        }
        public static ModPackage Load(string sourcePath, string sevenZipPath, bool preloadPreviews)
        {
            using (var reader=new SourceReader(sourcePath,sevenZipPath)) return Load(sourcePath,reader,preloadPreviews);
        }
        internal static ModPackage Load(string sourcePath, SourceReader r, bool preloadPreviews)
        {
            var p = new ModPackage { SourcePath=Path.GetFullPath(sourcePath),Name=Path.GetFileNameWithoutExtension(sourcePath),Character=Characters.Guess(Path.GetFileName(sourcePath)) };
            var componentsById = new Dictionary<string,ModComponent>(StringComparer.OrdinalIgnoreCase);
            {
                List<SourceEntry> metadata = r.Entries.Values.Where(e=>Path.GetFileName(e.Path).Equals("modinfo.ini",StringComparison.OrdinalIgnoreCase)).OrderBy(e=>e.Path,StringComparer.OrdinalIgnoreCase).ToList();
                foreach (SourceEntry e in metadata) if (e.Size > 1024 * 1024) throw new IOException("Metadata or preview is too large: " + e.Path);
                List<SourceEntry> previews = preloadPreviews && r.UsesSevenZip ? r.Entries.Values.Where(e=>e.Size <= 25L * 1024 * 1024 && Regex.IsMatch(e.Path,@"\.(jpg|jpeg|png|bmp|gif)$",RegexOptions.IgnoreCase)).ToList() : new List<SourceEntry>();
                long previewBudget = Math.Min(16L*1024*1024,Math.Max(0,64L*1024*1024-metadata.Sum(e=>e.Size)));
                var bounded = new List<SourceEntry>();
                foreach (var image in previews.OrderByDescending(e=>Path.GetFileNameWithoutExtension(e.Path).Equals("screenshot",StringComparison.OrdinalIgnoreCase)).ThenBy(e=>e.Path,StringComparer.OrdinalIgnoreCase))
                    if (bounded.Count < 16 && image.Size <= previewBudget) { bounded.Add(image); previewBudget -= image.Size; }
                previews = bounded;
                Dictionary<string,byte[]> smallFiles = r.ReadManyBytes(metadata.Concat(previews),25 * 1024 * 1024);
                foreach (SourceEntry e in previews) p.PreloadedPreviews[e.Path] = smallFiles[e.Path];
                if (r.UsesSevenZip) { p.PreviewEntries = new Dictionary<string,SourceEntry>(r.Entries,StringComparer.OrdinalIgnoreCase); p.PreviewSourceStamp = r.ListingStamp; }
                foreach (SourceEntry e in metadata)
                {
                    string id = Dir(e.Path);
                    Dictionary<string,string> meta = ParseIni(smallFiles[e.Path]);
                    var c = new ModComponent { Id=id,Metadata=meta,Name=Get(meta,"name",Last(id,p.Name)),Description=Get(meta,"description",""),Author=Get(meta,"author",""),Version=Get(meta,"version",""),AddonFor=Get(meta,"addonfor",""),IsDummy=Get(meta,"dummymod","").Equals("true",StringComparison.OrdinalIgnoreCase) };
                    p.Components.Add(c);
                    componentsById.Add(c.Id,c);
                }
                foreach (SourceEntry e in r.Entries.Values)
                {
                    string[] parts=e.Path.Split('/'); int natives=Array.FindIndex(parts,s=>s.Equals("natives",StringComparison.OrdinalIgnoreCase));
                    if (natives < 0) continue;
                    string owner=String.Join("/",parts.Take(natives));
                    // Each natives root is its own selectable unit. Never absorb nested backups into a parent.
                    ModComponent c;
                    if (!componentsById.TryGetValue(owner,out c))
                    {
                        c=new ModComponent {Id=owner,Name=Last(owner,p.Name),Description="Native files (no modinfo.ini)",Author="",Version="",AddonFor=""};
                        p.Components.Add(c); componentsById.Add(owner,c);
                    }
                    string dst="natives/"+String.Join("/",parts.Skip(natives+1));
                    c.Assets.Add(new NativeAsset {EntryPath=e.Path,DestinationPath=dst,Size=e.Size});
                    foreach (string code in Library.FighterCodes("/"+dst))   // 1.0.1: the fighter folder decides (Library.FighterCodes)
                        if (!p.CharacterCodes.Contains(code)) p.CharacterCodes.Add(code);
                }
                CollectRootFiles(p, r, componentsById, metadata.Select(e => Dir(e.Path)).ToList());
                p.RebuildIndexes();
                var componentsByName = p.Components.GroupBy(c=>c.Name.Trim(),StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
                var previewByDirectory = r.Entries.Keys.Where(x=>Regex.IsMatch(x,@"\.(jpg|jpeg|png|bmp|gif)$",RegexOptions.IgnoreCase))
                    .GroupBy(Dir,StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g=>g.Key,g=>g.OrderByDescending(x=>Path.GetFileNameWithoutExtension(x).Equals("screenshot",StringComparison.OrdinalIgnoreCase)).ThenBy(x=>x).First(),StringComparer.OrdinalIgnoreCase);
                foreach (ModComponent c in p.Components)
                {
                    if (!String.IsNullOrWhiteSpace(c.AddonFor))
                    {
                        List<ModComponent> named;
                        var parents=componentsByName.TryGetValue(c.AddonFor.Trim(),out named) ? named.Where(x=>x!=c).ToList() : new List<ModComponent>();
                        if (parents.Count==1) c.ParentId=parents[0].Id;
                        else p.Warnings.Add("Cannot uniquely resolve group '"+c.AddonFor+"' for "+c.Name+".");
                    }
                    else
                    {
                        ModComponent physical=PhysicalParent(c.Id,componentsById);
                        if (physical != null) c.ParentId=physical.Id;
                    }
                    string preview=Get(c.Metadata,"screenshot","").Replace('\\','/');
                    string refPath=(c.Id.Length==0 ? "" : c.Id+"/")+preview;
                    if (preview.Length>0 && r.Entries.ContainsKey(refPath)) c.PreviewEntry=refPath;
                    else
                    {
                        string image;
                        if (previewByDirectory.TryGetValue(c.Id,out image)) c.PreviewEntry=image;
                    }
                    // A malformed dependency cycle must never hang the tree or traversal.
                    var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); ModComponent ancestor=c;
                    while (ancestor != null)
                    {
                        if (!seen.Add(ancestor.Id)) { c.ParentId=null; p.Warnings.Add("Circular group removed for "+c.Name+"."); break; }
                        ancestor=ancestor.ParentId==null ? null : p.Find(ancestor.ParentId);
                    }
                }
                int extras=r.Entries.Keys.Count(x=>!x.Split('/').Any(s=>s.Equals("natives",StringComparison.OrdinalIgnoreCase)) && Regex.IsMatch(x,@"\.(lua|dll|pak|exe)$",RegexOptions.IgnoreCase));
                if (extras>0) p.Warnings.Add(extras+" script/plugin/archive files are outside natives and will not be installed; install them separately if the mod needs them.");
            }
            p.Components=p.Components.OrderBy(x=>NaturalKey(x.Name),StringComparer.OrdinalIgnoreCase).ToList();
            ModComponent root=p.Components.FirstOrDefault(x=>x.ParentId==null && x.IsDummy) ?? p.Components.FirstOrDefault(x=>x.ParentId==null);
            // 2.3.1: sibling options without a dummy root that name their bundle (Fluffy's NameAsBundle) are that
            // bundle, not their first option ("Character Back Spotlight Adjuster", not "1. 125%").
            string bundleName=root==null || root.IsDummy ? "" : p.Components.Where(x=>x.ParentId==null && x.Metadata!=null).Select(x=>Get(x.Metadata,"nameasbundle","").Trim()).FirstOrDefault(s=>s.Length>0) ?? "";
            if (bundleName.Length>0) p.Name=bundleName;
            else if (root != null) p.Name=root.Name;
            if (p.CharacterCodes.Count>0) p.Character=p.CharacterCodes.Count==1 && Characters.ByCode.ContainsKey(p.CharacterCodes[0]) ? Characters.ByCode[p.CharacterCodes[0]] : "";
            if (p.Components.Sum(x=>x.Assets.Count)==0) p.Warnings.Add("This mod has no loose natives files, so it can't be an extra outfit.");
            p.PrepareChoices();
            return p;
        }
        // Fluffy layout: files beside a modinfo.ini go to the game root relative to that
        // folder. Without any modinfo.ini, single wrapper folders are stripped. Previews,
        // modinfo.ini and readme/licence files at a component's top level are not installed.
        private static void CollectRootFiles(ModPackage p, SourceReader r, Dictionary<string,ModComponent> componentsById, List<string> iniDirs)
        {
            var iniSet = new HashSet<string>(iniDirs, StringComparer.OrdinalIgnoreCase);
            var outside = r.Entries.Values.Where(e => !e.Path.Split('/').Any(s => s.Equals("natives", StringComparison.OrdinalIgnoreCase))).ToList();
            if (outside.Count == 0) return;
            string wrapper = null;
            if (iniSet.Count == 0)
            {
                wrapper = "";
                while (true)
                {
                    string prefix = wrapper.Length == 0 ? "" : wrapper + "/";
                    var rest = r.Entries.Keys.Select(k => k.Substring(prefix.Length)).ToList();
                    if (rest.Any(k => k.IndexOf('/') < 0)) break;
                    var tops = rest.Select(k => k.Substring(0, k.IndexOf('/'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (tops.Count != 1 || tops[0].Equals("natives", StringComparison.OrdinalIgnoreCase) || tops[0].Equals("reframework", StringComparison.OrdinalIgnoreCase)) break;
                    wrapper = prefix + tops[0];
                }
            }
            int ignored = 0;
            foreach (SourceEntry e in outside)
            {
                string owner = null;
                if (wrapper != null) { if (wrapper.Length == 0 || e.Path.StartsWith(wrapper + "/", StringComparison.OrdinalIgnoreCase)) owner = wrapper; }
                else { string dir = Dir(e.Path); while (true) { if (iniSet.Contains(dir) || componentsById.ContainsKey(dir)) { owner = dir; break; } if (dir.Length == 0) break; dir = Dir(dir); } }
                if (owner == null) { ignored++; continue; }
                string rel = owner.Length == 0 ? e.Path : e.Path.Substring(owner.Length + 1);
                string leaf = rel.Split('/').Last();
                if (Regex.IsMatch(leaf, @"^(desktop\.ini|thumbs\.db|\.ds_store)$", RegexOptions.IgnoreCase) || rel.Split('/').Any(s => s.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))) continue;
                if (rel.IndexOf('/') < 0 && (leaf.Equals("modinfo.ini", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(leaf, @"\.(jpg|jpeg|png|bmp|gif|webp|txt|url|blend|blend1)$", RegexOptions.IgnoreCase) || Regex.IsMatch(leaf, @"^(readme|read me|lisezmoi|license|licence)([ ._-].*)?$", RegexOptions.IgnoreCase) || leaf.StartsWith("."))) continue;
                ModComponent c;
                if (!componentsById.TryGetValue(owner, out c))
                {
                    // Never add a new tree node to a native-file package: that would change
                    // its option hierarchy for extra-outfit installs.
                    if (componentsById.Count > 0) { ignored++; continue; }
                    c = new ModComponent { Id = owner, Name = Last(owner, p.Name), Description = "Game files (no modinfo.ini)", Author = "", Version = "", AddonFor = "" };
                    p.Components.Add(c); componentsById.Add(owner, c);
                }
                c.RootAssets.Add(new NativeAsset { EntryPath = e.Path, DestinationPath = rel, Size = e.Size });
            }
            // A costume option's other files are usually notes or tool leftovers (colour-tool backups):
            // only REFramework files accompany native files into the game folder.
            foreach (ModComponent c in p.Components.Where(x => x.Assets.Count > 0 && x.RootAssets.Count > 0))
            {
                int before = c.RootAssets.Count;
                c.RootAssets = c.RootAssets.Where(a => a.DestinationPath.StartsWith("reframework/", StringComparison.OrdinalIgnoreCase)).ToList();
                ignored += before - c.RootAssets.Count;
            }
            p.RootFilesIgnored = ignored;
        }

        internal static string NaturalKey(string value) { return Regex.Replace(value ?? "",@"\d+",m=>m.Value.PadLeft(10,'0')); }
        internal static Dictionary<string,string> ParseIni(byte[] bytes)
        {
            string text;
            if (bytes.Length>1 && bytes[0]==0xFF && bytes[1]==0xFE) text=Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF');
            else { try { text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF'); } catch (DecoderFallbackException) { text=Encoding.GetEncoding(1252).GetString(bytes); } }
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Replace("\r","").Split('\n'))
            {
                string s=line.Trim(); if (s.StartsWith(";") || s.StartsWith("#") || s.StartsWith("[")) continue;
                int eq=s.IndexOf('='); if (eq<1) eq=s.IndexOf(':'); if (eq<1) continue;
                result[s.Substring(0,eq).Trim()]=s.Substring(eq+1).Trim().Replace("\\n",Environment.NewLine);
            }
            return result;
        }
        private static string Get(Dictionary<string,string> d,string key,string def) { string v; return d.TryGetValue(key,out v) ? v : def; }
        private static string Dir(string path) { int i=path.LastIndexOf('/'); return i<0 ? "" : path.Substring(0,i); }
        private static string Last(string path,string fallback) { return path.Length==0 ? fallback : path.Split('/').Last(); }
        private static ModComponent PhysicalParent(string id,Dictionary<string,ModComponent> components)
        {
            if (id.Length==0) return null;
            string parent=Dir(id);
            while (true)
            {
                ModComponent component;
                if (components.TryGetValue(parent,out component)) return component;
                if (parent.Length==0) return null;
                parent=Dir(parent);
            }
        }
    }
}
