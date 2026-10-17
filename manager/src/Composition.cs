using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    public sealed class PlannedFile { public NativeAsset Asset; public ModComponent Component; }
    public sealed class FileOverride { public string Path, Previous, Winner; }
    public sealed class CompositionPlan
    {
        public ModPackage Package;
        public List<ModComponent> Layers=new List<ModComponent>();
        public Dictionary<string,PlannedFile> Files=new Dictionary<string,PlannedFile>(StringComparer.OrdinalIgnoreCase);
        public List<FileOverride> Overrides=new List<FileOverride>();
        public bool ReplaceMode;
        public List<string> Errors=new List<string>(),Warnings=new List<string>();
        public long TotalBytes { get { return Files.Values.Sum(x=>x.Asset.Size); } }
        public bool CanInstall { get { return Errors.Count==0 && Files.Count>0; } }
    }
    public static class Composer
    {
        internal static bool IsBase(ModComponent c) { return Regex.IsMatch(c.Name ?? "",@"\b(main\s*(files?)?|base|mandatory|required)\b",RegexOptions.IgnoreCase); }
        public static List<string> DefaultSelection(ModPackage package) { return DefaultSelection(package,false); }
        // replaceMode also counts files outside natives (Fluffy-style game-root files).
        public static List<string> DefaultSelection(ModPackage package,bool replaceMode)
        {
            var real=package.Components.Where(c=>!c.IsDummy && c.HasFiles(replaceMode)).ToList();
            var bases=real.Where(IsBase).ToList();
            if (bases.Count>0) return bases.Where(c=>String.Equals(c.ParentId,bases[0].ParentId,StringComparison.OrdinalIgnoreCase)).Select(c=>c.Id).ToList();
            return real.Count==1 ? new List<string>{real[0].Id} : real.Where(c=>c.ParentId==null).Select(c=>c.Id).ToList();
        }
        public static CompositionPlan Plan(ModPackage package,IEnumerable<string> selectedIds) { return Plan(package,selectedIds,false); }
        // replaceMode: files outside natives are included (game-root destinations) and the
        // extra-outfit mesh/route rules are skipped: a replacement may be colours or textures only.
        public static CompositionPlan Plan(ModPackage package,IEnumerable<string> selectedIds,bool replaceMode)
        {
            package.PrepareChoices();
            var p=new CompositionPlan {Package=package,ReplaceMode=replaceMode};
            p.Warnings.AddRange(replaceMode ? package.Warnings.Where(w=>!w.Contains("outside natives and will not be installed") && !w.Contains("no loose natives files")) : package.Warnings);
            var selected=selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id=>package.Find(id)).ToList();
            if (selected.Any(c=>c==null)) p.Errors.Add("An option no longer exists in this mod. Reload it.");
            p.Layers=selected.Where(c=>c!=null && !c.IsDummy && c.HasFiles(replaceMode)).OrderBy(c=>IsBase(c) ? 0 : 1).ToList();
            if (p.Layers.Count==0) { p.Errors.Add("Select main files and any required options."); return p; }
            foreach (ModComponent c in p.Layers)
            {
                foreach (NativeAsset f in replaceMode ? c.Assets.Concat(c.RootAssets) : c.Assets)
                {
                    PlannedFile old;
                    if (p.Files.TryGetValue(f.DestinationPath,out old)) p.Overrides.Add(new FileOverride {Path=f.DestinationPath,Previous=old.Component.Name,Winner=c.Name});
                    p.Files[f.DestinationPath]=new PlannedFile {Asset=f,Component=c};
                }
                ModComponent parent=c.ParentId==null ? null : package.Find(c.ParentId);
                if (parent!=null && !parent.IsDummy && parent.HasFiles(replaceMode) && !p.Layers.Contains(parent)) p.Errors.Add("'"+c.Name+"' needs its parent '"+parent.Name+"'.");
            }
            foreach (ModComponent main in package.Components.Where(c=>!c.IsDummy && c.HasFiles(replaceMode) && IsBase(c) && ScopeIsActive(package,c.ParentId,p.Layers)))
                if (!p.Layers.Contains(main)) p.Errors.Add("Include main files: "+main.Name);
            foreach (ModComponent group in package.Components.Where(package.IsChoice))
            {
                if (!ScopeIsActive(package,group.ParentId,p.Layers) && !p.Layers.Any(c=>IsDescendant(package,c,group))) continue;
                int count=p.Layers.Where(c=>IsDescendant(package,c,group)).Select(c=>ChoiceBranch(package,c,group))
                    .Where(id=>package.IsChoiceBranch(group,package.Find(id))).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (count>1) p.Errors.Add("Choose one option in '"+group.Name+"'.");
                if (count==0 && group.IsChoiceGroup && !Regex.IsMatch(group.Name+" "+group.Description,@"\boptional\b",RegexOptions.IgnoreCase)) p.Errors.Add("Choose an option in '"+group.Name+"'.");
            }
            if (replaceMode)
            {
                if (p.Overrides.Count>0) p.Warnings.Add(p.Overrides.Count+" file overrides: main files go first, then options in selection order; the last one wins.");
                return p;
            }
            // Use the loader's actual body-part directory, not a guessed mesh filename.
            var bodies=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materials=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in p.Files.Keys)
            {
                Match m=Regex.Match(path,@"^natives/stm/product/model/esf/esf(\d{3})/(\d{3})/01/[^/]+\.mesh\.230110883$",RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[2].Value!="000") bodies.Add(m.Groups[1].Value+"/"+m.Groups[2].Value);
                m=Regex.Match(path,@"^natives/stm/product/model/esf/esf(\d{3})/(\d{3})/01/[^/]+\.mdf2\.\d+$",RegexOptions.IgnoreCase);
                if (m.Success) materials.Add(m.Groups[1].Value+"/"+m.Groups[2].Value);
            }
            if (bodies.Count==0)
            {
                if (p.Files.Keys.Any(x=>Regex.IsMatch(x,@"/esf/esf\d{3}/\d{3}/\d{2}/[^/]+\.mesh\.230110883$",RegexOptions.IgnoreCase))) p.Warnings.Add("Partial outfit with no body mesh: Slotsloader may combine it with vanilla parts. Check it in game.");
                else p.Errors.Add("The selection has no outfit mesh. Add the main mesh option; textures alone don't make an outfit.");
            }
            foreach (string body in bodies) if (!materials.Contains(body)) p.Warnings.Add("The mod has no body material for esf"+body+": Slotsloader may use the vanilla one. Check it in game after restarting SF6.");
            if (bodies.Count>1) p.Warnings.Add("The selection has several body meshes: Slotsloader may show more than one outfit. Review the files.");
            int ignored=p.Files.Keys.Count(x=>!KnownLoaderRoute(x));
            if (ignored>0) p.Warnings.Add((ignored==1 ? "1 file is" : ignored+" files are")+" outside outfit folders: installed, but Slotsloader may ignore "+(ignored==1 ? "it." : "them."));
            if (p.Overrides.Count>0) p.Warnings.Add(p.Overrides.Count+" file overrides: main files go first, then options in selection order; the last one wins.");
            return p;
        }
        // The choose-one groups for which Plan reports "Choose an option in ...",
        // evaluated without building the file plan. Used to mark them live in
        // the option list. The caller must have run PrepareChoices for this
        // package (FillComponents does); Plan itself is unchanged.
        internal static List<ModComponent> UnresolvedChoiceGroups(ModPackage package,IEnumerable<string> selectedIds) { return UnresolvedChoiceGroups(package,selectedIds,false); }
        internal static List<ModComponent> UnresolvedChoiceGroups(ModPackage package,IEnumerable<string> selectedIds,bool replaceMode)
        {
            var result=new List<ModComponent>();
            if (package==null) return result;
            var layers=selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id=>package.Find(id)).Where(c=>c!=null && !c.IsDummy && c.HasFiles(replaceMode)).ToList();
            if (layers.Count==0) return result;
            foreach (ModComponent group in package.Components.Where(package.IsChoice))
            {
                if (!ScopeIsActive(package,group.ParentId,layers) && !layers.Any(c=>IsDescendant(package,c,group))) continue;
                int count=layers.Where(c=>IsDescendant(package,c,group)).Select(c=>ChoiceBranch(package,c,group))
                    .Where(id=>package.IsChoiceBranch(group,package.Find(id))).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (count==0 && group.IsChoiceGroup && !Regex.IsMatch(group.Name+" "+group.Description,@"\boptional\b",RegexOptions.IgnoreCase)) result.Add(group);
            }
            return result;
        }
        private static string ChoiceBranch(ModPackage p,ModComponent child,ModComponent group)
        {
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (child != null && seen.Add(child.Id))
            {
                if (String.Equals(child.ParentId,group.Id,StringComparison.OrdinalIgnoreCase)) return child.Id;
                child=child.ParentId==null ? null : p.Find(child.ParentId);
            }
            return null;
        }
        private static bool IsDescendant(ModPackage p,ModComponent child,ModComponent group)
        {
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (child!=null && seen.Add(child.Id)) { if (String.Equals(child.ParentId,group.Id,StringComparison.OrdinalIgnoreCase)) return true; child=child.ParentId==null ? null : p.Find(child.ParentId); }
            return false;
        }
        private static bool ScopeIsActive(ModPackage p,string parentId,List<ModComponent> layers)
        {
            // A null parent is the package's implicit root. Nested selections still
            // activate its main files and required groups. Explicit bundle headers
            // retain their own scope through the ancestor check below.
            if (parentId==null) return layers.Count>0;
            ModComponent parent=p.Find(parentId);
            return parent!=null && layers.Any(c=>String.Equals(c.Id,parent.Id,StringComparison.OrdinalIgnoreCase) || IsDescendant(p,c,parent));
        }
        private static bool KnownLoaderRoute(string path)
        {
            string n=path.ToLowerInvariant();
            if (n.Contains("/product/model/esf/") || Regex.IsMatch(n,@"^natives/stm/product/model/[^/]+$") || Regex.IsMatch(n,@"^natives/stm/product/charparam/esf/esf\d{3}/esf\d{3}v\d+\.scn\.\d+$")) return true;
            return Regex.IsMatch(Path.GetFileName(n),@"esf\d{3}_\d{3}_");
        }
    }
}
