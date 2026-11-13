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
        // In-scope main files that are not selected (each is also in Needs).
        internal List<ModComponent> MissingMains=new List<ModComponent>();
        // 1.0.19: what the selection leaves out that the mod marks as needed, or that looks needed (MissingNeed).
        internal List<MissingNeed> Needs=new List<MissingNeed>();
        // The errors plus what Needs was an error for until 1.0.18, and the warnings without those (the Assistant).
        internal List<string> StrictErrors() { return Errors.Concat(Needs.Where(n=>n.Strict!=null).Select(n=>n.Strict)).Distinct().ToList(); }
        internal List<string> StrictWarnings() { return Warnings.Where(w=>!Needs.Any(n=>n.Strict!=null && n.Text==w)).ToList(); }
        // 1.0.18: only these parts of the selection were kept (OutfitParts.Keep); empty = all of it.
        internal List<string> OnlyParts=new List<string>();
    }
    // 1.0.19 (the user, 13 Nov 2026: "i also want to relax requirements. when a mod has some files tagged as requirement
    // or guessed etc. then we should show a popup (less annoying though) around the image that it may not work without x
    // mod and highlight the required mod temporarily but install the mod anyways. this helps to mix and match options"):
    // an option the selection leaves out although another option names it (its parent, Requirement=), the bundle marks
    // it Required, its name looks like main files, or a "Required · choose one" group has nothing picked (Needed is the
    // group then). It is a note in Warnings and by the preview picture, never an error. Still errors: nothing ticked,
    // an option that is gone, two picks in a choose-one group, and an Extra outfit with no mesh at all.
    // Strict is the error this was until 1.0.18 (null for main files guessed from a name, which were a note already):
    // the Assistant still refuses on it (CompositionPlan.StrictErrors), because a model that forgets a required choice
    // looks the same as a user who left it out on purpose.
    internal sealed class MissingNeed { internal ModComponent Needed, By; internal string Text, Strict; }
    public static partial class Composer
    {
        // "Main menu", "main screen" and "main theme" name a place in the game, not the mod's main files
        // ("TRUE Faster StartUp (MAIN MENU)" next to "(TRAINING)": two alternatives, neither needs the other).
        // 13 Nov 2026 (the user, of "Marisa Base Model - Hair" and its add-on "Marisa Base Model - Clean", both labelled
        // Main files, the add-on lit up as left out: "why this mod has highlights for this? its a wrong guess", then
        // "I guess we should remove the base as a main files keyword"): "base" in a name is no longer a sign of main
        // files. "Main", "main files", "mandatory" and "required" still are; an option named "Base" is an ordinary option.
        internal static bool IsBase(ModComponent c) { return c.BundleRule == "Required" || (c.BundleRule != "Optional" && (c.AuthorBase ?? NamedLikeMain(c.Name))); }
        internal static bool NamedLikeMain(string name) { return Regex.IsMatch(name ?? "",@"\b(main(?![\s_-]*(menu|screen|theme)\b)\s*(files?)?|mandatory|required)\b",RegexOptions.IgnoreCase); }
        // The plain one among options that write the same files ("C1 L base" beside "C1 L + Chest"): named like main
        // files, or "base". Only WithoutAlternatives asks, to tick the plain one first; it is no main-files guess.
        // "(base costume boots)" describes which boots an option keeps.
        internal static bool NamedLikePlain(string name) { return NamedLikeMain(name) || Regex.IsMatch(name ?? "",@"\bbase(?!\s+(costume|outfit|game)\s+\w)\b",RegexOptions.IgnoreCase); }
        // Main files the bundle's own layout marks Required. IsBase also holds for a name that only looks like main
        // files: that guess orders layers, ticks defaults and shows a "Main files" label, but never blocks an install.
        internal static bool IsRequired(ModComponent c) { return c.BundleRule == "Required"; }
        // Options named like main files that write exactly the same files as a sibling are alternatives ("C1 L main",
        // "C1 XL main", "C1 XXL - Main": three sizes of one mesh), so none of them is required with the others.
        // Main files of different outfits ("C1 - Main files", "C2 - Main files") write different files and stay.
        internal static void DropTwinMains(ModPackage package)
        {
            var named=package.Components.Where(c=>!c.IsDummy && c.AuthorBase==null && c.BundleRule==null && IsBase(c)).ToList();
            foreach (var siblings in named.GroupBy(c=>c.ParentId ?? "\0",StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))
            {
                var files=siblings.ToDictionary(c=>c,RootOptionChoices.Destinations);
                foreach (ModComponent c in siblings.Where(c=>files[c].Count>0 && siblings.Any(o=>o!=c && files[c].SetEquals(files[o]))).ToList()) c.AuthorBase=false;
            }
        }
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
            // Among alternatives, one named "base" or "main" is the plain one ("C1 L base" before "C1 L + Chest"), so it is
            // the one ticked, although it is not main files (NamedLikePlain; "base" is no main-files word since 13 Nov 2026).
            foreach (ModComponent c in options.Select((c,i)=>new{c,i}).OrderByDescending(x=>x.c.IsDefaultChoice).ThenByDescending(x=>NamedLikePlain(x.c.Name)).ThenBy(x=>x.i).Select(x=>x.c))
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
            }
            FindNeeds(p);
            foreach (MissingNeed need in p.Needs) if (!p.Warnings.Contains(need.Text)) p.Warnings.Add(need.Text);
            foreach (ModComponent group in package.Components.Where(package.IsChoice))
            {
                if (!ScopeIsActive(package,group.ParentId,p.Layers) && !p.Layers.Any(c=>IsDescendant(package,c,group))) continue;
                if (ChoicesIn(package,group,p.Layers)>1 && !(savedWinnerCheck && group.InferredChoice)) p.Errors.Add("Choose one option in '"+group.Name+"'.");
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
        // 1.0.19: fills p.Needs (and p.MissingMains) from p.Layers. Nothing here blocks an install (see MissingNeed).
        private static void FindNeeds(CompositionPlan p)
        {
            ModPackage package=p.Package; bool replaceMode=p.ReplaceMode;
            Action<ModComponent,ModComponent,string,string> add=delegate(ModComponent needed,ModComponent by,string text,string strict)
            {
                if (!p.Needs.Any(n=>n.Needed==needed && n.By==by)) p.Needs.Add(new MissingNeed {Needed=needed,By=by,Text=text,Strict=strict});
            };
            foreach (ModComponent c in p.Layers)
            {
                ModComponent parent=c.ParentId==null ? null : package.Find(c.ParentId);
                while (parent != null && parent.IsDummy && (package.HasBundleLayout || parent.Id.StartsWith(":hair-choice:", StringComparison.Ordinal)) && parent.ParentId != null) parent = package.Find(parent.ParentId);
                if (parent!=null && !parent.IsDummy && parent.HasFiles(replaceMode) && !p.Layers.Contains(parent)) add(parent,c,"'"+c.Name+"' may not work without '"+parent.Name+"'.","'"+c.Name+"' needs its parent '"+parent.Name+"'.");
                foreach (ModComponent need in package.RequiredOptions(c))
                    if (need.HasFiles(replaceMode) && !IsRequired(need) && !p.Layers.Contains(need)) add(need,c,"'"+c.Name+"' may not work without '"+need.Name+"'.","'"+c.Name+"' needs '"+need.Name+"'.");
            }
            // Main files: marked Required by the bundle, or guessed from a name ("(MAIN MENU)" was a wrong guess once).
            foreach (ModComponent main in package.Components.Where(c=>!c.IsDummy && c.HasFiles(replaceMode) && IsBase(c) && ScopeIsActive(package,c.ParentId,p.Layers)))
            {
                if (p.Layers.Contains(main)) continue;
                p.MissingMains.Add(main);
                if (IsRequired(main)) add(main,null,"This selection may not work without '"+main.Name+"' (the mod marks it Required).","Include main files: "+main.Name);
                else add(main,null,"'"+main.Name+"' looks like this mod's main files and is not selected. Most options need them.",null);
            }
            foreach (ModComponent group in package.Components.Where(package.IsChoice))
            {
                if (!group.RequiresChoice) continue;
                if (!ScopeIsActive(package,group.ParentId,p.Layers) && !p.Layers.Any(c=>IsDescendant(package,c,group))) continue;
                if (ChoicesIn(package,group,p.Layers)==0) add(group,null,"This selection may not work without an option from '"+group.Name+"'.","Choose an option in '"+group.Name+"'.");
            }
        }
        // How many branches of a choose-one group the layers pick.
        private static int ChoicesIn(ModPackage package,ModComponent group,List<ModComponent> layers)
        {
            return layers.Where(c=>IsDescendant(package,c,group)).Select(c=>ChoiceBranch(package,c,group))
                .Where(id=>package.IsChoiceBranch(group,package.Find(id))).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }
        // 1.0.19: the same list Plan puts in Needs, without building the file plan: the Library shows it by the preview
        // picture as soon as a tick changes. Empty while nothing with files is ticked (Plan reports that as an error).
        internal static List<MissingNeed> MissingNeeds(ModPackage package,IEnumerable<string> selectedIds,bool replaceMode)
        {
            var p=new CompositionPlan {Package=package,ReplaceMode=replaceMode};
            if (package==null) return p.Needs;
            package.PrepareChoices();
            p.Layers=selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id=>package.Find(id)).Where(c=>c!=null && !c.IsDummy && c.HasFiles(replaceMode)).ToList();
            if (p.Layers.Count>0) FindNeeds(p);
            return p.Needs;
        }
        // The "Required · choose one" groups with nothing picked, evaluated without
        // building the file plan. Used to mark them live in the option list and to
        // fill the default ticks. Since 1.0.19 an open group blocks an install only
        // while nothing at all is ticked; with a selection it is a note (FindNeeds).
        // The caller must have run PrepareChoices for this package (FillComponents does).
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
                if (group.RequiresChoice && ChoicesIn(package,group,layers)==0) result.Add(group);
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
        // The Required options a selection brings in, ticked for the user. 1.0.19: not the ones in leftOut (the user
        // unticked them by hand; Plan then names them in Needs), nor what only those would bring in.
        internal static IEnumerable<ModComponent> RequiredForSelection(ModPackage package,List<string> ids,bool replace,ICollection<string> leftOut=null)
        {
            var layers=ids.Select(package.Find).Where(c=>c!=null).ToList();
            foreach (var item in package.Components.Where(c=>c.BundleRule=="Required" && c.HasFiles(replace)).OrderBy(c=>Depth(package,c)))
                if (!layers.Contains(item) && (leftOut==null || !leftOut.Contains(item.Id)) && ScopeIsActive(package,item.ParentId,layers))
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
