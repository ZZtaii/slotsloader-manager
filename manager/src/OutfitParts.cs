using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.18 (the user: "detect hair mods too even if they don't have hair keyword ... mods having 02 folder", and
    // mods where the hair "is integrated into same mod folder ... only the hair mod could be used from that full
    // mod"): the parts of an outfit, read from where its files go. SF6 keeps a fighter's model in numbered folders
    // under esfNNN/<outfit>/: 00 is the head, 01 the body, 02 the hair (30 and up are accessories). A name that says
    // what an option is wins over the folders. A recipe layer can take only some parts of a mod (Keep).
    internal static class OutfitParts
    {
        internal const string Body = "body", Hair = "hair", Head = "head", Colours = "colours", Other = "other";
        internal static readonly string[] Order = { Body, Hair, Head, Colours, Other };
        internal static string Label(string part)
        {
            switch (part) { case Body: return "Body"; case Hair: return "Hair"; case Head: return "Head"; case Colours: return "Colours"; default: return "Other files"; }
        }
        // "Hair", "Body + Hair", "Hair + Other files"
        internal static string Labels(IEnumerable<string> parts) { return String.Join(" + ", Sorted(parts).Select(Label)); }
        internal static List<string> Sorted(IEnumerable<string> parts)
        {
            var set = new HashSet<string>(parts ?? new string[0], StringComparer.OrdinalIgnoreCase);
            return Order.Where(set.Contains).ToList();
        }

        private static readonly Regex ModelFolder = new Regex(@"^natives/stm/(?:streaming/)?product/model/esf/esf\d{3}/\d{3}/(\d{2})/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ColourFile = new Regex(@"^natives/stm/product/model/esf/esf\d{3}/\d{3}/[^/]+\.user\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Version = new Regex(@"\.\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex Material = new Regex(@"\.mdf2\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // The part a file belongs to, by its place in the game.
        internal static string Of(string path)
        {
            string p = ModFixes.NormalizeNativePath(path ?? "");
            Match m = ModelFolder.Match(p);
            if (m.Success) return m.Groups[1].Value == "00" ? Head : m.Groups[1].Value == "01" ? Body : m.Groups[1].Value == "02" ? Hair : Other;
            return ColourFile.IsMatch(p) ? Colours : Other;
        }
        internal static List<string> In(IEnumerable<string> paths) { return Sorted((paths ?? new string[0]).Select(Of)); }

        // What a name says the option is, or null when it says nothing ("Long hair", "Ponytail B", "Face paint").
        private static readonly Regex HairWord = new Regex(@"(?<![a-z])(hair|hairs|hairstyle|hairstyles|ponytail|pigtails?|twintails?|bangs|braids?|bun|wig)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HeadWord = new Regex(@"(?<![a-z])(head|face|makeup|make-up|eyes?|eyeshadow|lipstick)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static string Named(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return null;
            if (HairWord.IsMatch(name)) return Hair;
            return HeadWord.IsMatch(name) ? Head : null;
        }
        // What an option (or a mod) is, for a label: the name wins, else the folders its files go to. "Other files"
        // and colours are left out while a model part is there, so a body with its textures reads "Body".
        internal static List<string> Kinds(string name, IEnumerable<string> paths)
        {
            string named = Named(name);
            if (named != null) return new List<string> { named };
            List<string> found = In(paths);
            List<string> model = found.Where(p => p == Body || p == Hair || p == Head).ToList();
            return model.Count > 0 ? model : found;
        }

        // Keeps only these parts of the plan's files (nothing when `parts` is empty: the whole selection stays). A part
        // travels whole: its model, material file and physics are in its folder, and the textures its material files
        // name come along from wherever the mod keeps them (a hair's textures are often in a folder of their own).
        internal static void Keep(CompositionPlan plan, IList<string> parts, SourceReader reader)
        {
            if (plan == null || parts == null || parts.Count == 0) return;
            var wanted = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
            var keep = new HashSet<string>(plan.Files.Keys.Where(k => wanted.Contains(Of(k))), StringComparer.OrdinalIgnoreCase);
            // path without "streaming/" and the version -> the plan's files (the texture and its streaming copy)
            Func<string, string> bare = k => Version.Replace(ModFixes.NormalizeNativePath(k).Replace("natives/stm/streaming/", "natives/stm/"), "");
            ILookup<string, string> byBare = plan.Files.Keys.ToLookup(bare, StringComparer.OrdinalIgnoreCase);
            if (reader != null)
                foreach (string material in keep.Where(k => Material.IsMatch(k)).ToList())
                {
                    PlannedFile file = plan.Files[material];
                    if (file.Asset.Size > 64 * 1024 * 1024) continue;
                    foreach (string texture in MaterialCheck.Textures(reader.ReadBytes(file.Asset.EntryPath, 64 * 1024 * 1024)))
                        foreach (string path in byBare[texture]) keep.Add(path);
                }
            foreach (string path in plan.Files.Keys.Where(k => !keep.Contains(k)).ToList()) plan.Files.Remove(path);
            var used = new HashSet<string>(plan.Files.Values.Select(f => f.Component.Id), StringComparer.OrdinalIgnoreCase);
            plan.Layers.RemoveAll(c => !used.Contains(c.Id));
            plan.Overrides.RemoveAll(o => !plan.Files.ContainsKey(o.Path));
            plan.OnlyParts = Sorted(parts);
            if (plan.Files.Count == 0 && plan.Errors.Count == 0) plan.Errors.Add("These options have no " + Labels(parts).ToLowerInvariant() + " files.");
        }
    }
}
