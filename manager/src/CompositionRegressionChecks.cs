using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    internal static class CompositionRegressionChecks
    {
        private const string Mesh = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";
        private const string Material = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mdf2.40";
        private const string Texture = "natives/stm/product/model/esf/esf032/001/01/skin.tex.28";

        internal static int Run(string fixtureRoot)
        {
            string full = Path.GetFullPath(fixtureRoot);
            Safety.NoReparseChain(full);
            string run = Path.Combine(full,"composition-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            Console.WriteLine("Composition fixture root: "+run);
            int failures = 0;
            Check("nested selections retain implicit-root main and required groups",delegate { RootRequirements(Path.Combine(run,"root-requirements")); },ref failures);
            Check("main files and a replacing overlay compose and install together",delegate { BaseOverlay(Path.Combine(run,"base-overlay")); },ref failures);
            Check("inferred alternatives exclude main files but still reject peer conflicts",delegate { InferredAlternatives(Path.Combine(run,"inferred-alternatives")); },ref failures);
            Check("independent bundle requirements remain scoped to the selected branch",delegate { IndependentBranches(Path.Combine(run,"independent-branches")); },ref failures);
            Check("nested choice branches count their parent and child as one alternative",delegate { NestedBranches(Path.Combine(run,"nested-branches")); },ref failures);
            Check("case-insensitive IDs, dependencies and previews retain canonical identities",delegate { CaseHandling(Path.Combine(run,"case-handling")); },ref failures);
            Check("options follow MenuPriority (higher first), else plain options before groups, then name",delegate { OptionOrder(Path.Combine(run,"option-order")); },ref failures);
            Check("an option named after the game's main menu is not the mod's main files",delegate { MainMenuName(Path.Combine(run,"main-menu-name")); },ref failures);
            Console.WriteLine("Composition regression checks: "+(8-failures)+" passed, "+failures+" failed.");
            return failures;
        }

        private static void Check(string name,Action test,ref int failures)
        {
            try { test(); Console.WriteLine("PASS "+name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL "+name+": "+ex); }
        }
        private static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Put(string root,string path,string contents)
        {
            string file=Safety.Under(root,path); Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,contents,new UTF8Encoding(false));
        }
        private static ModPackage Load(string root) { return PackageReader.Load(root,""); }
        private static ModComponent Named(ModPackage package,string name) { return package.Components.Single(c=>c.Name==name); }
        private static CompositionPlan Plan(ModPackage package,params string[] ids) { return Composer.Plan(package,ids); }
        private static string Errors(CompositionPlan plan) { return String.Join(" | ",plan.Errors); }
        private static void InstallAndCheck(CompositionPlan plan,string root,string expectedMaterial)
        {
            Require(plan.CanInstall,"Valid fixture was rejected: "+Errors(plan));
            var result=Installer.Install(plan,Path.Combine(root,"game","reframework","costume_mods"),"Ingrid","Fixture outfit","",false);
            Require(File.ReadAllText(Safety.Under(result.Folder,Material))==expectedMaterial,"Installed material did not match the winning source component.");
            Require(result.FileCount==plan.Files.Count,"Installed file count differs from reviewed composition.");
        }
        private static void OptionOrder(string root)
        {
            string source=Path.Combine(root,"source");
            Put(source,"modinfo.ini","name=Bundle\ndummymod=true\n");
            Put(source,"Hair style/modinfo.ini","name=Hair style\naddonfor=Bundle\ndummymod=true\n");
            Put(source,"Hair style/Combined/modinfo.ini","name=Combined Hair\naddonfor=Hair style\n"); Put(source,"Hair style/Combined/"+Texture,"combined");
            Put(source,"Hair style/WT/modinfo.ini","name=WT Hair\naddonfor=Hair style\n"); Put(source,"Hair style/WT/"+Texture,"wt");
            // Main files carries the default hair (WT) and outfit (Full) byte for byte.
            Put(source,"Main files/modinfo.ini","name=Main files\naddonfor=Bundle\n"); Put(source,"Main files/"+Material,"main"); Put(source,"Main files/"+Mesh,"full"); Put(source,"Main files/"+Texture,"wt");
            Put(source,"Outfit/modinfo.ini","name=Outfit\naddonfor=Bundle\ndummymod=true\ndescription=Choose one\n");
            Put(source,"Outfit/Barefoot/modinfo.ini","name=Barefoot\naddonfor=Outfit\n"); Put(source,"Outfit/Barefoot/"+Mesh,"bare");
            Put(source,"Outfit/Full/modinfo.ini","name=Full\naddonfor=Outfit\n"); Put(source,"Outfit/Full/"+Mesh,"full");
            Put(source,"Extras/modinfo.ini","name=Character Select\nMenuPriority=1\n"); Put(source,"Extras/"+Mesh.Replace("esf032_001_01.mesh","extra.mesh"),"cs");
            Put(source,"Extras2/modinfo.ini","name=BattleHud\nMenuPriority=2\n"); Put(source,"Extras2/"+Mesh.Replace("esf032_001_01.mesh","hud.mesh"),"hud");
            Put(source,"Extras3/modinfo.ini","name=Credits\nMenuPriority=-1\n"); Put(source,"Extras3/"+Mesh.Replace("esf032_001_01.mesh","credits.mesh"),"credits");
            ModPackage package=Load(source);
            Func<string,string> children=parent=>String.Join(",",package.Components.Where(c=>c.ParentId==package.Components.Single(x=>x.Name==parent).Id).Select(c=>c.Name));
            // Fluffy's MenuPriority: higher first, unset = 0, ties by name.
            Require(children("Bundle")=="BattleHud,Character Select,Hair style,Main files,Outfit,Credits","Bundle order: "+children("Bundle"));
            Require(children("Hair style")=="WT Hair,Combined Hair","Hair order (default first): "+children("Hair style"));
            Require(children("Outfit")=="Full,Barefoot","Outfit order (default first): "+children("Outfit"));
            Require(package.Components.Where(c=>c.IsDefaultChoice).Select(c=>c.Name).OrderBy(n=>n).SequenceEqual(new[]{"Full","WT Hair"}),"Default options: "+String.Join(",",package.Components.Where(c=>c.IsDefaultChoice).Select(c=>c.Name)));
            Require(package.Name=="Bundle","Bundle name changed: "+package.Name);
            // A small option that changes files of a bigger one sits right after it.
            string flat=Path.Combine(root,"flat");
            Func<string,string> ui=n=>"natives/stm/product/ui/"+n+".tex.28";
            Put(flat,"Character Select/modinfo.ini","name=Character Select\n"); foreach (string n in new[]{"select","cart","frame","grid"}) Put(flat,"Character Select/"+ui(n),n);
            Put(flat,"BattleHud/modinfo.ini","name=BattleHud\n"); Put(flat,"BattleHud/"+ui("hud"),"hud"); Put(flat,"BattleHud/"+ui("gauge"),"gauge");
            Put(flat,"CFN/modinfo.ini","name=CFN and Devices\n"); Put(flat,"CFN/"+ui("cfn"),"cfn");
            Put(flat,"Cart/modinfo.ini","name=Cart Icon Remover\n"); Put(flat,"Cart/"+ui("cart"),"empty");   // by name it would come second
            ModPackage flatPackage=Load(flat);
            string top=String.Join(",",flatPackage.Components.Where(c=>c.ParentId==null).Select(c=>c.Name));
            Require(top=="BattleHud,CFN and Devices,Character Select,Cart Icon Remover","Flat order: "+top);
            // Numbered names are the author's order: a numbered group stays above a later plain option.
            string numbered=Path.Combine(root,"numbered");
            Put(numbered,"modinfo.ini","name=Cammy edits\ndummymod=true\n");
            Put(numbered,"Variants/modinfo.ini","name=1) Outfit Variants\ndummymod=true\n");
            Put(numbered,"Variants/Skimpy/modinfo.ini","name=Skimpy\n"); Put(numbered,"Variants/Skimpy/"+Mesh,"skimpy");
            Put(numbered,"Jiggle/modinfo.ini","name=2) Optional - Extra Jiggle\n"); Put(numbered,"Jiggle/"+ui("jiggle"),"jiggle");
            ModPackage numberedPackage=Load(numbered);
            string kids=String.Join(",",numberedPackage.Components.Where(c=>c.ParentId=="").Select(c=>c.Name));
            Require(kids=="1) Outfit Variants,2) Optional - Extra Jiggle","Numbered order: "+kids);
        }
        private static void RootRequirements(string root)
        {
            string source=Path.Combine(root,"source");
            Put(source,"main/modinfo.ini","name=Main files\n"); Put(source,"main/"+Material,"required shared material");
            Put(source,"outfits/modinfo.ini","name=Outfit choices\ndummymod=true\ndescription=Choose one outfit\n");
            Put(source,"outfits/dress/modinfo.ini","name=Dress\n"); Put(source,"outfits/dress/"+Mesh,"dress mesh");
            Put(source,"skins/modinfo.ini","name=Skin choices\ndummymod=true\ndescription=Choose one skin\n");
            Put(source,"skins/default/modinfo.ini","name=Default skin\n"); Put(source,"skins/default/"+Texture,"skin texture");
            ModPackage package=Load(source);
            CompositionPlan missing=Plan(package,"outfits/dress");
            // Main files guessed from the name are a warning, not an error; the required skin choice still blocks.
            Require(!missing.CanInstall && missing.Errors.All(e=>!e.Contains("Main files")) && missing.Warnings.Any(w=>w.Contains("'Main files' looks like")) && missing.MissingMains.Count==1 && missing.Errors.Any(e=>e.Contains("Skin choices")),"Nested outfit bypassed implicit-root requirements: "+Errors(missing));
            Require(Plan(package,"outfits/dress","skins/default").CanInstall,"Main files guessed from a name still blocked an install.");
            Require(!Plan(package,"main","outfits/dress").CanInstall,"Required skin choice was not enforced.");
            InstallAndCheck(Plan(package,"main","outfits/dress","skins/default"),root,"required shared material");
            Require(File.ReadAllText(Safety.Under(source,"main/"+Material))=="required shared material","Source material changed.");
        }
        private static void BaseOverlay(string root)
        {
            string source=Path.Combine(root,"source");
            Put(source,"modinfo.ini","name=Outfit bundle\ndummymod=true\n");
            Put(source,"main/modinfo.ini","name=Main files\n"); Put(source,"main/"+Mesh,"base mesh"); Put(source,"main/"+Material,"base material");
            Put(source,"blue/modinfo.ini","name=Blue recolor\n"); Put(source,"blue/"+Material,"blue material");
            ModPackage package=Load(source);
            Require(!package.IsChoice(package.Find("")),"A main/overlay pair was classified as alternatives.");
            CompositionPlan plan=Plan(package,"blue","main");
            Require(plan.Overrides.Count==1 && plan.Files[Material].Component.Id=="blue","Main-first overlay precedence changed.");
            InstallAndCheck(plan,root,"blue material");
        }
        private static void MainMenuName(string root)
        {
            foreach (string name in new[]{"Main","Main files","MAIN FILE","Mainfiles","Main mod","Base","Required"})
                Require(Composer.IsBase(new ModComponent {Name=name}),"'"+name+"' is no longer main files.");
            foreach (string name in new[]{"Base costume","C1 XXL - Base","!BASE: Mai C1 ORIGINAL TEXTURE FILES","MAIN: Mai C1 L","(Main) Belly Dancer 2.0"})
                Require(Composer.IsBase(new ModComponent {Name=name}),"'"+name+"' is no longer main files.");
            foreach (string name in new[]{"Faster StartUp (MAIN MENU)","Main-menu music","Main screen","Main theme","Training","Regular - shorts (base costume boots) no tights"})
                Require(!Composer.IsBase(new ModComponent {Name=name}),"'"+name+"' was taken for main files.");
            // Sizes of one mesh named "base" are alternatives; main files of two outfits are both still required.
            string sizes=Path.Combine(root,"sizes");
            foreach (string size in new[]{"L","XL"}) { Put(sizes,"C1 "+size+"/modinfo.ini","name=C1 "+size+" base\n"); Put(sizes,"C1 "+size+"/"+Mesh,size+" mesh"); }
            Put(sizes,"C1 chest/modinfo.ini","name=C1 L + Chest\n"); Put(sizes,"C1 chest/"+Material,"chest");
            ModPackage sized=Load(sizes);
            Require(!Composer.IsBase(Named(sized,"C1 L base")) && !Composer.IsBase(Named(sized,"C1 XL base")),"Two sizes of one mesh were both main files.");
            Require(Plan(sized,Named(sized,"C1 XL base").Id).Errors.All(e=>!e.StartsWith("Include main files")),"One size asked for the other: "+Errors(Plan(sized,Named(sized,"C1 XL base").Id)));
            // The plain size is the one ticked by default, not whichever name sorts first ("C1 L + Chest").
            foreach (string size in new[]{"L","XL"}) { Put(sizes,"C1 "+size+" + Skirt/modinfo.ini","name=C1 "+size+" + Skirt\n"); Put(sizes,"C1 "+size+" + Skirt/"+Mesh,size+" skirt mesh"); }
            ModPackage sizedAll=Load(sizes);
            var ticked=Composer.DefaultSelection(sizedAll).Select(id=>sizedAll.Find(id).Name).OrderBy(n=>n,StringComparer.Ordinal).ToList();
            Require(ticked.SequenceEqual(new[]{"C1 L + Chest","C1 L base"}),"Default size: "+String.Join(" | ",ticked));
            string outfits=Path.Combine(root,"outfits");
            Put(outfits,"C1/modinfo.ini","name=C1 - Main files\n"); Put(outfits,"C1/"+Mesh,"c1");
            Put(outfits,"C2/modinfo.ini","name=C2 - Main files\n"); Put(outfits,"C2/"+Material,"c2");
            ModPackage two=Load(outfits);
            Require(two.Components.Count(Composer.IsBase)==2,"Main files of two outfits are no longer both main files.");
            foreach (string mode in new[]{"mainmenu","training"})
            {
                Put(root,"startup/"+mode+"/modinfo.ini","name=Faster StartUp ("+(mode=="mainmenu" ? "MAIN MENU" : "TRAINING")+")\n");
                Put(root,"startup/"+mode+"/reframework/autorun/faster.lua","-- "+mode);
            }
            ModPackage package=Load(Path.Combine(root,"startup"));
            CompositionPlan plan=Composer.Plan(package,new[]{"training"},true);
            Require(plan.CanInstall,"The TRAINING option alone was rejected: "+Errors(plan));
        }
        private static void InferredAlternatives(string root)
        {
            Put(root,"modinfo.ini","name=Outfit bundle\ndummymod=true\n");
            Put(root,"main/modinfo.ini","name=Main files\n"); Put(root,"main/"+Mesh,"base mesh"); Put(root,"main/"+Material,"base material");
            foreach (string color in new[]{"blue","red"}) { Put(root,color+"/modinfo.ini","name="+color+" recolor\n"); Put(root,color+"/"+Material,color+" material"); }
            ModPackage package=Load(root); ModComponent group=package.Find("");
            Require(package.IsChoice(group) && !package.IsChoiceBranch(group,package.Find("main")) && package.IsChoiceBranch(group,package.Find("blue")),"Inferred branch membership includes main files or excludes a peer alternative.");
            Require(Plan(package,"main","blue").CanInstall && Plan(package,"main","red").CanInstall,"A base with one variant was rejected.");
            Require(!Plan(package,"main","blue","red").CanInstall,"Conflicting peer variants were accepted.");
        }
        private static void IndependentBranches(string root)
        {
            foreach (string branch in new[]{"A","B"})
            {
                Put(root,branch+"-header/modinfo.ini","name=Bundle "+branch+"\ndummymod=true\n");
                Put(root,branch+"-main/modinfo.ini","name="+branch+" Main files\naddonfor=Bundle "+branch+"\n"); Put(root,branch+"-main/"+Mesh,branch+" mesh");
                Put(root,branch+"-skins/modinfo.ini","name="+branch+" skin options\naddonfor=Bundle "+branch+"\ndummymod=true\ndescription=Choose one skin\n");
                Put(root,branch+"-skin/modinfo.ini","name="+branch+" skin\naddonfor="+branch+" skin options\n"); Put(root,branch+"-skin/"+Material,branch+" material");
            }
            ModPackage package=Load(root);
            Require(Plan(package,"A-main","A-skin").CanInstall,"Inactive Bundle B blocked a complete Bundle A.");
            CompositionPlan missing=Plan(package,"A-main");
            Require(!missing.CanInstall && missing.Errors.Any(e=>e.Contains("A skin")) && missing.Errors.All(e=>!e.Contains("B skin")),"Independent branch requirements escaped their scope.");
            // 1.0.18: A's required skin group gets its option ticked too; nothing of Bundle B.
            Require(Composer.DefaultSelection(package).SequenceEqual(new[]{"A-main","A-skin"}),"Default selection crossed bundle branches: "+String.Join(",",Composer.DefaultSelection(package)));
        }
        private static void NestedBranches(string root)
        {
            Put(root,"modinfo.ini","name=Outfit alternatives\ndummymod=true\ndescription=Choose one\n");
            foreach (string branch in new[]{"A","B"}) { Put(root,branch+"/modinfo.ini","name="+branch+" outfit\n"); Put(root,branch+"/"+Mesh,branch+" mesh"); }
            Put(root,"A/child/modinfo.ini","name=Attached texture\n"); Put(root,"A/child/"+Texture,"extra texture");
            ModPackage package=Load(root);
            Require(Plan(package,"A","A/child").CanInstall,"A branch and its child counted as two alternatives.");
            Require(!Plan(package,"A","B","A/child").CanInstall,"Competing nested choice branches were accepted.");
            Require(!Plan(package,"A/child").CanInstall,"A real contributing parent was not required.");
        }
        private static void CaseHandling(string root)
        {
            Put(root,"Header/modinfo.ini","name=Bundle Header\ndummymod=true\n"); Put(root,"Header/screenshot.PNG","preview marker");
            Put(root,"Main/modinfo.ini","name=Main files\naddonfor=  bUnDlE hEaDeR  \n"); Put(root,"Main/"+Material,"main material");
            Put(root,"Choices/modinfo.ini","name=Outfit choices\naddonfor=BUNDLE HEADER\ndummymod=true\ndescription=Choose one\n");
            Put(root,"Dress/modinfo.ini","name=Dress\naddonfor=OUTFIT CHOICES\n"); Put(root,"Dress/"+Mesh,"dress mesh");
            ModPackage package=Load(root);
            Require(package.Find("main").Id=="Main" && package.Find("HEADER").Id=="Header","Find did not resolve canonical IDs.");
            // Public fixture callers may supply mixed-case ParentIds. A new plan
            // rebuilds the indexes and must apply the same path identity rules.
            package.Find("Main").ParentId="hEaDeR"; package.Find("Choices").ParentId="HEADER"; package.Find("Dress").ParentId="cHoIcEs";
            CompositionPlan plan=Plan(package,"mAiN","DRESS","dress");
            Require(plan.CanInstall && plan.Layers.Select(c=>c.Id).SequenceEqual(new[]{"Main","Dress"}),"Mixed-case IDs changed selection identity: "+Errors(plan));
            Require(Plan(package,"dress").MissingMains.Select(c=>c.Id).SequenceEqual(new[]{"Main"}),"Mixed-case ancestry lost the in-scope main files.");
            Require(Encoding.UTF8.GetString(package.PreviewBytes("dress",""))=="preview marker","Ancestor preview lookup failed after indexing.");
            Require(Named(package,"Dress").ParentId=="cHoIcEs","Indexing unexpectedly rewrote model identity.");
        }
    }
}
