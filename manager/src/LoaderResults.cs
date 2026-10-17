using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    internal static class LoaderResults
    {
        internal static void Apply(List<InstalledOutfit> outfits, string costumeRoot)
        {
            foreach (var outfit in outfits) { outfit.LoaderOutfitName=""; outfit.LoaderStatus=outfit.Enabled ? "Start SF6 to load" : "Disabled"; outfit.LoaderGenerated=""; outfit.LoaderBuild=""; outfit.LoaderOutfitDetails=""; outfit.Variants=null; outfit.VariantsUnsupported=false; }
            string file=Path.Combine(Path.GetDirectoryName(costumeRoot),"data","SF6_Costumes_Data","outfits.json");
            try
            {
                if (!File.Exists(file)) return;
                Safety.NoReparseChain(file);
                if (new FileInfo(file).Length > 16L*1024*1024) throw new IOException("Loader result exceeds the supported size.");
                var report=Json.Read<Dictionary<string,object>>(file);
                var rows=NativeModCheck.Rows(report,"outfits");
                string generated=NativeModCheck.Value(report,"generated"), build=NativeModCheck.Value(report,"loader");
                DateTime written=File.GetLastWriteTimeUtc(file);
                var sources=rows.Where(r=>!String.IsNullOrEmpty(NativeModCheck.Value(r,"source")))
                    .GroupBy(r=>Normalize(NativeModCheck.Value(r,"source")),StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
                foreach (var outfit in outfits)
                {
                    outfit.LoaderGenerated=generated; outfit.LoaderBuild=build;
                    string relative=outfit.Enabled && NativeModCheck.IsWithin(outfit.Folder,costumeRoot)
                        ? Path.GetFullPath(outfit.Folder).Substring(Path.GetFullPath(costumeRoot).TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar)
                        : outfit.Character+"/"+outfit.Name;
                    List<Dictionary<string,object>> matches;
                    if (sources.TryGetValue(Normalize(relative),out matches))
                    {
                        outfit.LoaderOutfitName=String.Join(", ",matches.Select(r=>NativeModCheck.Value(r,"name")).Where(n=>n.Length>0).Distinct());
                        outfit.LoaderOutfitDetails=String.Join(Environment.NewLine,matches.Select(r=>NativeModCheck.Value(r,"fighter_dir")+" · "+NativeModCheck.Value(r,"name")+" · "+NativeModCheck.Value(r,"label")));
                    }
                    if (!outfit.Enabled) continue;
                    DateTime sourceWrite=outfit.InstalledUtc;
                    if (File.Exists(outfit.Folder)) sourceWrite=File.GetLastWriteTimeUtc(outfit.Folder);
                    else if (Directory.Exists(outfit.Folder) && Directory.GetLastWriteTimeUtc(outfit.Folder)>sourceWrite) sourceWrite=Directory.GetLastWriteTimeUtc(outfit.Folder);
                    // Older labels are retained as historical information, never presented as a fresh mapping.
                    outfit.LoaderStatus=sourceWrite>written ? "Restart SF6 to load" : String.IsNullOrEmpty(outfit.LoaderOutfitName) ? "Not loaded by Slotsloader" : "Loaded";
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                foreach (var outfit in outfits) if (outfit.Enabled) { outfit.LoaderOutfitName=""; outfit.LoaderStatus="Slotsloader result unreadable"; }
            }
            finally { ApplyVariants(outfits,costumeRoot); }   // never throws (VariantChoices.ReadReport logs)
        }
        // 2.5.0: a manual archive or folder SlotsLoader split into several variants can turn them on/off (Installed page).
        // Outfits the Manager composed are one variant each and get no toggles.
        private static void ApplyVariants(List<InstalledOutfit> outfits, string costumeRoot)
        {
            VariantReport report=VariantChoices.ReadReport(costumeRoot);
            foreach (var outfit in outfits.Where(o=>!o.Managed && o.Enabled))
            {
                ArchiveVariants archive=report.Present ? report.Find(VariantChoices.SourceOf(outfit,costumeRoot)) : null;
                if (archive!=null && archive.Variants.Count>1) outfit.Variants=archive;
                else if (!report.Present && (outfit.LoaderOutfitName ?? "").Contains(", ")) outfit.VariantsUnsupported=true;
            }
        }
        private static string Normalize(string source) { return source.Replace('\\','/').Trim('/'); }
    }
}
