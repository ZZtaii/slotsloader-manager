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
    public static partial class Composer
    {
        internal static bool IsBase(ModComponent c) { return c.BundleRule == "Required" || (c.BundleRule != "Optional" && (c.AuthorBase ?? Regex.IsMatch(c.Name ?? "",@"\b(main\s*(files?)?|base|mandatory|required)\b",RegexOptions.IgnoreCase))); }
        public static List<string> DefaultSelection(ModPackage package) { return DefaultSelection(package,false); }
        // replaceMode also counts files outside natives (Fluffy-style game-root files).
        public static List<string> DefaultSelection(ModPackage package,bool replaceMode)
        {
            var real=package.Components.Where(c=>!c.IsDummy && c.HasFiles(replaceMode)).ToList();
            if (package.HasBundleLayout)
            {
                var original = replaceMode ? package.AuthorReplaceDefaults : package.AuthorSlotDefaults;
                var selected = real.Where(c => c.BundleRule=="Original" && original.Contains(c.Id,StringComparer.OrdinalIgnoreCase)).ToList();
                // A moved default must not pick a branch on the user's behalf or require an unchecked parent.
                var authors = package.AuthorOptions.ToDictionary(o=>o.Id,StringComparer.OrdinalIgnoreCase);
                selected.RemoveAll(c => {
                    for (var child=c; child!=null; child=package.Find(child.ParentId))
                    {
                        BundleOption old;
                        if (!authors.TryGetValue(child.Id,out old) || !String.Equals(child.ParentId,old.Parent,StringComparison.OrdinalIgnoreCase)) return true;
                        var parent=package.Find(child.ParentId);
                        if (parent!=null && (parent.BundleRule=="Choose one" || parent.BundleRule=="Choose up to one" || parent.BundleRule=="Optional")) return true;
                    }
                    return false;
                });
                foreach (var c in real.Where(c => c.BundleRule=="Required").OrderBy(c=>Depth(package,c)))
                    if (!selected.Contains(c) && ScopeIsActive(package,c.ParentId,selected,true)) selected.Add(c);
                return selected.Select(c=>c.Id).ToList();
            }
            var bases=real.Where(IsBase).ToList();
            List<string> ids;
            if (bases.Count>0) ids=bases.Where(c=>String.Equals(c.ParentId,bases[0].ParentId,StringComparison.OrdinalIgnoreCase)).Select(c=>c.Id).ToList();
            else ids=real.Count==1 ? new List<string>{real[0].Id} : WithoutAlternatives(package,real.Where(c=>c.ParentId==null).ToList(),replaceMode).Select(c=>c.Id).ToList();
            return WithRequiredChoices(package,ids,replaceMode);
        }
        // 1.0.18 (the user: "the mods that are required tag be ticked by default"): each "Required · choose one" group in
        // play gets its "· Default" option, else its first option with files (looking into sub-groups), as the install
        // queue did. With nothing ticked yet, the required groups under menus only are in play. Groups the Manager only
        // inferred from shared files ("Choose an option") stay for the user to pick.
        private static List<string> WithRequiredChoices(ModPackage package,List<string> ids,bool replaceMode)
        {
            for (int round=0; round<10; round++)
            {
                IEnumerable<ModComponent> open = ids.Count>0 ? UnresolvedChoiceGroups(package,ids,replaceMode)
                    : package.Components.Where(g => package.IsChoice(g) && g.RequiresChoice && UnderMenusOnly(package,g));
                bool added=false;
                foreach (ModComponent group in open.Where(g => !g.InferredChoice).ToList())
                {
                    ModComponent pick=FirstChoice(package,group,replaceMode);
                    if (pick!=null && !ids.Contains(pick.Id,StringComparer.OrdinalIgnoreCase)) { ids.Add(pick.Id); added=true; }
                }
                if (!added) break;
            }
            return ids;
        }
        private static bool UnderMenusOnly(ModPackage package,ModComponent group)
        {
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (ModComponent p=package.Find(group.ParentId); p!=null && seen.Add(p.Id); p=package.Find(p.ParentId))
                if (!p.IsDummy || package.IsChoice(p)) return false;
            return true;
        }
        // A choose-one group's default choice, else its first option with files (looking into sub-groups), in menu order.
        internal static ModComponent FirstChoice(ModPackage package,ModComponent group,bool replaceMode)
        {
            var children=package.Components.Where(c=>String.Equals(c.ParentId,group.Id,StringComparison.OrdinalIgnoreCase)).ToList();
            ModComponent best=children.FirstOrDefault(c=>c.IsDefaultChoice && !c.IsDummy && c.HasFiles(replaceMode)) ?? children.FirstOrDefault(c=>!c.IsDummy && c.HasFiles(replaceMode));
            if (best!=null) return best;
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase){group.Id};
            foreach (ModComponent child in children.Where(c=>c.IsDummy))
                if (seen.Add(child.Id)) { ModComponent deeper=FirstChoice(package,child,replaceMode); if (deeper!=null) return deeper; }
            return null;
        }
        // 1.0.18 (the user: "enabling all options automatically ... when they are not all supposed to be enabled"):
        // top-level options that write the same files are alternatives (two collars, two hairstyles), so only the first in
        // menu order (a "· Default" one first) is ticked; the others stay unticked for the user to pick. An option that
        // names a ticked one in Requirement= goes on top of it and is ticked too. Options with files of their own stay.
        internal static List<ModComponent> WithoutAlternatives(ModPackage package,List<ModComponent> options,bool replaceMode)
        {
            if (options.Count<2) return options;
            var kept=new List<ModComponent>(); var taken=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModComponent c in options.Select((c,i)=>new{c,i}).OrderByDescending(x=>x.c.IsDefaultChoice).ThenBy(x=>x.i).Select(x=>x.c))
            {
                var mine=(replaceMode ? c.Assets.Concat(c.RootAssets) : c.Assets).Select(a=>a.DestinationPath).ToList();
                if (kept.Count>0 && mine.Any(taken.Contains) && !package.RequiredOptions(c).Any(kept.Contains)) continue;
                kept.Add(c); taken.UnionWith(mine);
            }
            return options.Where(kept.Contains).ToList();
        }
        // 1.0.18: an option named in another selected option's Requirement= goes under it, whatever the tick order,
        // so the one needing it wins the files they share (Alt hairstyle over Swimsuit Sakura C2's hair).
        private static List<ModComponent> RequirementsFirst(ModPackage package,List<ModComponent> layers)
        {
            var result=new List<ModComponent>(); var visiting=new HashSet<ModComponent>();
            Action<ModComponent> place=null;
            place=c => { if (!visiting.Add(c)) return; foreach (ModComponent need in package.RequiredOptions(c)) if (layers.Contains(need)) place(need); result.Add(c); };
            foreach (ModComponent c in layers) place(c);
            return result;
        }
        public static CompositionPlan Plan(ModPackage package,IEnumerable<string> selectedIds) { return Plan(package,selectedIds,false); }
        // replaceMode: files outside natives are included (game-root destinations) and the
        // extra-outfit mesh/route rules are skipped: a replacement may be colours or textures only.
        public static CompositionPlan Plan(ModPackage package,IEnumerable<string> selectedIds,bool replaceMode)
        {
            return PlanCore(package, selectedIds, replaceMode, false);
        }
        private static CompositionPlan PlanCore(ModPackage package,IEnumerable<string> selectedIds,bool replaceMode,bool savedWinnerCheck)
        {
            package.PrepareChoices();
            var p=new CompositionPlan {Package=package,ReplaceMode=replaceMode};
            p.Warnings.AddRange(replaceMode ? package.Warnings.Where(w=>!w.Contains("outside natives and will not be installed") && !w.Contains("no loose natives files")) : package.Warnings);
            var selected=selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id=>package.Find(id)).ToList();
            if (selected.Any(c=>c==null)) p.Errors.Add("An option no longer exists in this mod. Reload it.");
            p.Layers=RequirementsFirst(package,selected.Where(c=>c!=null && !c.IsDummy && c.HasFiles(replaceMode)).OrderBy(c=>IsBase(c) ? 0 : 1).ToList());
            if (package.HasBundleLayout)
            {
                var byId = p.Layers.ToDictionary(c=>c.Id,StringComparer.OrdinalIgnoreCase);
                var ordered = new List<ModComponent>();
                var emitted = new HashSet<ModComponent>();
                foreach (var layer in p.Layers)
                {
                    var branch = new Stack<ModComponent>();
                    for (var c=layer; c!=null; c=package.Find(c.ParentId))
                        if (byId.ContainsKey(c.Id) && emitted.Add(c)) branch.Push(c);
                    ordered.AddRange(branch);
                }
                p.Layers=ordered;
            }
            if (p.Layers.Count==0)
            {
                var choices = UnresolvedChoiceGroups(package,new string[0],replaceMode);
                p.Errors.Add(choices.Count == 0 ? "Select main files and any required options." : "Choose an option in '" + String.Join("' or '", choices.Select(c => c.Name)) + "'.");
                return p;
            }
            foreach (ModComponent c in p.Layers)
            {
                foreach (NativeAsset f in replaceMode ? c.Assets.Concat(c.RootAssets) : c.Assets)
                {
                    PlannedFile old;
                    if (p.Files.TryGetValue(f.DestinationPath,out old)) p.Overrides.Add(new FileOverride {Path=f.DestinationPath,Previous=old.Component.Name,Winner=c.Name});
                    p.Files[f.DestinationPath]=new PlannedFile {Asset=f,Component=c};
                }
                ModComponent parent=c.ParentId==null ? null : package.Find(c.ParentId);
                while (parent != null && parent.IsDummy && (package.HasBundleLayout || parent.Id.StartsWith(":hair-choice:", StringComparison.Ordinal)) && parent.ParentId != null) parent = package.Find(parent.ParentId);
                if (parent!=null && !parent.IsDummy && parent.HasFiles(replaceMode) && !p.Layers.Contains(parent)) p.Errors.Add("'"+c.Name+"' needs its parent '"+parent.Name+"'.");
                foreach (ModComponent need in package.RequiredOptions(c))
                    if (need.HasFiles(replaceMode) && !IsBase(need) && !p.Layers.Contains(need)) p.Errors.Add("'"+c.Name+"' needs '"+need.Name+"'.");
            }
            foreach (ModComponent main in package.Components.Where(c=>!c.IsDummy && c.HasFiles(replaceMode) && IsBase(c) && ScopeIsActive(package,c.ParentId,p.Layers)))
                if (!p.Layers.Contains(main)) p.Errors.Add("Include main files: "+main.Name);
            foreach (ModComponent group in package.Components.Where(package.IsChoice))
            {
                if (!ScopeIsActive(package,group.ParentId,p.Layers) && !p.Layers.Any(c=>IsDescendant(package,c,group))) continue;
                int count=p.Layers.Where(c=>IsDescendant(package,c,group)).Select(c=>ChoiceBranch(package,c,group))
                    .Where(id=>package.IsChoiceBranch(group,package.Find(id))).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (count>1 && !(savedWinnerCheck && group.InferredChoice)) p.Errors.Add("Choose one option in '"+group.Name+"'.");
                if (count==0 && group.RequiresChoice) p.Errors.Add("Choose an option in '"+group.Name+"'.");
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
            if (layers.Count==0) return package.Components.Where(c =>
                (c.InferredChoice && package.Components.Any(m => m.ParentId == c.Id && m.HasFiles(replaceMode))) ||
                (package.HasBundleLayout && c.RequiresChoice && ScopeIsActive(package,c.ParentId,layers,true))).ToList();
            foreach (ModComponent group in package.Components.Where(package.IsChoice))
            {
                if (!ScopeIsActive(package,group.ParentId,layers) && !layers.Any(c=>IsDescendant(package,c,group))) continue;
                int count=layers.Where(c=>IsDescendant(package,c,group)).Select(c=>ChoiceBranch(package,c,group))
                    .Where(id=>package.IsChoiceBranch(group,package.Find(id))).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (count==0 && group.RequiresChoice) result.Add(group);
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
        private static bool ScopeIsActive(ModPackage p,string parentId,List<ModComponent> layers,bool starting=false)
        {
            // A null parent is the package's implicit root. Nested selections still
            // activate its main files and required groups. Explicit bundle headers
            // retain their own scope through the ancestor check below.
            if (parentId==null) return starting || layers.Count>0;
            ModComponent parent=p.Find(parentId);
            if (parent != null && p.HasBundleLayout && parent.IsDummy && !p.IsChoice(parent)) return ScopeIsActive(p,parent.ParentId,layers,starting);
            return parent!=null && layers.Any(c=>String.Equals(c.Id,parent.Id,StringComparison.OrdinalIgnoreCase) || IsDescendant(p,c,parent));
        }
        private static int Depth(ModPackage package,ModComponent c)
        {
            int depth=0;
            while ((c=package.Find(c.ParentId))!=null) depth++;
            return depth;
        }
        internal static IEnumerable<ModComponent> RequiredForSelection(ModPackage package,List<string> ids,bool replace)
        {
            var layers=ids.Select(package.Find).Where(c=>c!=null).ToList();
            foreach (var item in package.Components.Where(c=>c.BundleRule=="Required" && c.HasFiles(replace)).OrderBy(c=>Depth(package,c)))
                if (!layers.Contains(item) && ScopeIsActive(package,item.ParentId,layers))
                {
                    layers.Add(item);
                    yield return item;
                }
        }
        private static bool KnownLoaderRoute(string path)
        {
            string n=path.ToLowerInvariant();
            if (n.Contains("/product/model/esf/") || Regex.IsMatch(n,@"^natives/stm/product/model/[^/]+$") || Regex.IsMatch(n,@"^natives/stm/product/charparam/esf/esf\d{3}/esf\d{3}v\d+\.scn\.\d+$")) return true;
            return Regex.IsMatch(Path.GetFileName(n),@"esf\d{3}_\d{3}_");
        }
    }
}
