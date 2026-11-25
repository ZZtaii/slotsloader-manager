using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    internal static class LoaderResultChecks
    {
        internal static void Run(string root)
        {
            string costume=Path.Combine(root,"game","reframework","costume_mods"), ch=Path.Combine(costume,"Ingrid");
            Directory.CreateDirectory(ch);
            string folder=Path.Combine(ch,"Same label"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder,"manual.txt"),"manual source preserved");
            string archive=Path.Combine(ch,"Bundle.zip"); File.WriteAllText(archive,"manual archive is listed without extraction");
            string report=Path.Combine(root,"game","reframework","data","SF6_Costumes_Data","outfits.json"); Directory.CreateDirectory(Path.GetDirectoryName(report));
            Json.Write(report,new {generated="2026-10-13 08:00",loader="fixture",outfits=new[] {
                new {source="Ingrid/Same label",name="Outfit IV",label="Same label",fighter=32,fighter_dir="esf032"},
                new {source="Ingrid/Bundle.zip",name="Outfit I",label="First",fighter=32,fighter_dir="esf032"},
                new {source="Ingrid/Bundle.zip",name="Outfit II",label="Second",fighter=32,fighter_dir="esf032"},
                new {source="Juri/Same label",name="Outfit VI",label="Same label",fighter=16,fighter_dir="esf016"}},left_out=new object[0]});
            var listed=Installer.ListInstalled(costume);
            Require(listed.Count==2 && listed.All(o=>!o.Managed),"Manual folder/archive listing is wrong");
            Require(listed.Single(o=>o.Folder==folder).LoaderOutfitName=="Outfit IV","Mapping matched a label instead of exact source");
            var bundle=listed.Single(o=>o.Folder==archive);
            Require(bundle.LoaderOutfitName=="Outfit I, Outfit II" && bundle.LoaderStatus=="Loaded","Multiple outfits from one archive were lost");
            File.SetLastWriteTimeUtc(archive,DateTime.UtcNow.AddMinutes(1));
            listed=Installer.ListInstalled(costume);
            Require(listed.Single(o=>o.Folder==archive).LoaderStatus=="Restart SF6 to load","Changed archive was marked fresh");
            var disabled=new InstalledOutfit {Name="Same label",Character="Ingrid",Folder=Path.Combine(root,"disabled"),Enabled=false,Managed=true};
            LoaderResults.Apply(new List<InstalledOutfit>{disabled},costume);
            Require(disabled.LoaderStatus=="Disabled","Historical number presented as enabled");
            File.WriteAllText(report,"{ broken");
            listed=Installer.ListInstalled(costume);
            Require(listed.Count==2 && listed.All(o=>o.LoaderStatus=="Slotsloader result unreadable"),"Broken loader result blocked installed outfit list");
            Require(File.ReadAllText(Path.Combine(folder,"manual.txt"))=="manual source preserved","Manual source changed");
            // 1.0.22: the loader writes "source" in the ANSI code page and the rest of outfits.json in UTF-8. A slot the
            // Manager named "Mod · Option" was loaded but read "Not loaded by Slotsloader".
            string dottedName="Same label · Feet", dotted=Path.Combine(ch,dottedName), dottedSource="Ingrid/"+dottedName;
            Directory.CreateDirectory(dotted); File.WriteAllText(Path.Combine(dotted,"manual.txt"),"dotted");
            string json="{\n  \"generated\": \"2026-11-22 04:26\",\n  \"loader\": \"fixture\",\n  \"outfits\": [\n    {\"fighter\": 32, \"fighter_dir\": \"esf032\", \"costume_no\": 6, \"name\": \"Outfit VI\", \"based_on\": 0, \"label\": \""+dottedName+"\", \"kind\": \"folder\", \"source\": \"@SOURCE@\"}\n  ],\n  \"left_out\": []\n}\n";
            int at=json.IndexOf("@SOURCE@",StringComparison.Ordinal); var utf8=new UTF8Encoding(false);
            var bytes=new List<byte>(utf8.GetBytes(json.Substring(0,at)));
            bytes.AddRange(Encoding.Default.GetBytes(dottedSource)); bytes.AddRange(utf8.GetBytes(json.Substring(at+"@SOURCE@".Length)));
            File.WriteAllBytes(report,bytes.ToArray());
            listed=Installer.ListInstalled(costume);
            var feet=listed.Single(o=>o.Folder==dotted);
            Require(feet.LoaderOutfitName=="Outfit VI" && feet.LoaderStatus=="Loaded","A loaded folder with a non-ASCII name read as not loaded: "+feet.LoaderStatus+" '"+feet.LoaderOutfitName+"'");
            Require(listed.Single(o=>o.Folder==folder).LoaderStatus=="Not loaded by Slotsloader","A folder the loader did not report was matched");
            var reported=new HashSet<string>(new[]{Encoding.UTF8.GetString(Encoding.Default.GetBytes(dottedSource))},StringComparer.OrdinalIgnoreCase);
            Require(LoaderResults.Reported(reported,dottedSource) && LoaderResults.Reported(new HashSet<string>(new[]{dottedSource},StringComparer.OrdinalIgnoreCase),dottedSource) && !LoaderResults.Reported(reported,"Ingrid/Same label"),"Load order: the waiting list misreads the loader's sources");
            Console.WriteLine("PASS loader mapping: exact source, multiple archive outfits, stale/disabled states, malformed results, manual source protection and ANSI-written sources");
        }
        private static void Require(bool condition,string message) { if (!condition) throw new Exception(message); }
    }
}
