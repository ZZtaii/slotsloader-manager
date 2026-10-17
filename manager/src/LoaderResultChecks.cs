using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            Console.WriteLine("PASS loader mapping: exact source, multiple archive outfits, stale/disabled states, malformed results and manual source protection");
        }
        private static void Require(bool condition,string message) { if (!condition) throw new Exception(message); }
    }
}
