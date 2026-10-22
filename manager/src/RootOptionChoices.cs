using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // Deliberately narrower than dummy-parent inference: every member replaces the
    // complete same set of files, in either install mode. Never infer from titles alone.
    internal static class RootOptionChoices
    {
        internal static string Bundle(ModComponent c)
        {
            string name;
            return c.Metadata != null && c.Metadata.TryGetValue("NameAsBundle", out name)
                ? Regex.Replace((name ?? "").Trim(), @"\s+", " ") : "";
        }
        internal static HashSet<string> Destinations(ModComponent c)
        {
            return new HashSet<string>(c.Assets.Select(a => a.DestinationPath)
                .Concat(c.RootAssets.Select(a => "root:" + a.DestinationPath)), StringComparer.OrdinalIgnoreCase);
        }
        internal static List<List<ModComponent>> Find(ModPackage package)
        {
            var result = new List<List<ModComponent>>();
            var parents = new HashSet<string>(package.Components.Where(c => c.ParentId != null).Select(c => c.ParentId), StringComparer.OrdinalIgnoreCase);
            foreach (var cohort in package.Components.Where(c => c.ParentId == null && Bundle(c).Length > 0).GroupBy(Bundle, StringComparer.OrdinalIgnoreCase))
            {
                var members = cohort.ToList();
                if (members.Count < 2 || members.Any(c => c.IsDummy || Composer.IsBase(c) || parents.Contains(c.Id) || !String.IsNullOrWhiteSpace(c.AddonFor))) continue;
                var paths = Destinations(members[0]);
                if (paths.Count > 0 && members.Skip(1).All(c => paths.SetEquals(Destinations(c)))) result.Add(members);
            }
            return result;
        }
        internal static void Add(ModPackage package)
        {
            foreach (var members in Find(package))
            {
                string bundle = Bundle(members[0]);
                // ':' is forbidden in a source component's relative path. Encoding the
                // normalized bundle also makes IDs stable across reordering and JSON safe.
                string id = ":root-choice:" + BitConverter.ToString(Encoding.UTF8.GetBytes(bundle.ToUpperInvariant())).Replace("-", "");
                if (package.Components.Any(c => String.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                var group = new ModComponent { Id = id, Name = "Choose one: " + bundle, Description = "Alternatives replacing the same files.", IsDummy = true, InferredChoice = true };
                int position = package.Components.IndexOf(members[0]);
                package.Components.Insert(position, group);
                foreach (var member in members) member.ParentId = id;
            }
        }
        internal static int Audit(string output)
        {
            Directory.CreateDirectory(output);
            List<LibraryEntry> pending;
            var entries = Library.ScanQuick(LocalTestConfig.Current.Libraries, "", out pending);
            var lines = new List<string>(); int matched = 0, cohorts = 0, errors = 0, inspected = 0;
            foreach (var entry in entries)
            {
                try
                {
                    var package = PackageReader.Load(entry.SourcePath, "");
                    var found = Find(package);
                    found.AddRange(package.Components.Where(c => c.InferredChoice).Select(g => package.Components.Where(c => c.ParentId == g.Id).ToList()));
                    if (found.Count > 0) { matched++; cohorts += found.Count; }
                    foreach (var members in found) lines.Add("MATCH\t" + package.Name + "\t" + members.Count + "\t" + Destinations(members[0]).Count + "\t" + String.Join(" | ", members.Select(c => c.Name)) + "\t" + entry.SourcePath);
                    if (package.Name.IndexOf("Stage Lighting Overhaul", StringComparison.OrdinalIgnoreCase) >= 0) lines.Add("CONTROL\t" + package.Name + "\t" + found.Count);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; errors++; lines.Add("ERROR\t" + entry.SourcePath + "\t" + ex.Message); }
                if (++inspected % 20 == 0) File.WriteAllLines(Path.Combine(output, "root-options-audit-progress.txt"), lines);
            }
            lines.Insert(0, "Sources=" + entries.Count + "; matched=" + matched + "; cohorts=" + cohorts + "; errors=" + errors);
            File.WriteAllLines(Path.Combine(output, "root-options-audit.txt"), lines);
            return errors == 0 ? 0 : 1;
        }
    }
}
