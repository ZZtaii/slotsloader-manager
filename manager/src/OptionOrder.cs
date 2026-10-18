using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.4 option order (the user). Display only: install layering follows selection order (Composer.Plan).
    // Per sibling set: Fluffy's MenuPriority= when the author set it (higher first, unset = 0); name order when
    // the author numbered the names; otherwise
    // the default choice first (its files are identical to the main files'), plain options before groups,
    // a tweak right after the bigger option whose files it overlaps, then natural name.
    internal static class OptionOrder
    {
        internal static void Apply(ModPackage p, SourceReader r)
        {
            p.Components = p.Components.OrderBy(c => PackageReader.NaturalKey(c.Name), StringComparer.OrdinalIgnoreCase).ToList();
            p.PrepareChoices();
            var same = new SameFile(r);
            MarkDefaults(p, same);
            var children = p.Components.Where(c => c.ParentId != null).GroupBy(c => c.ParentId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var ordered = new List<ModComponent>(); var emitted = new HashSet<ModComponent>();
            Action<ModComponent, List<ModComponent>> emit = null;
            emit = (parent, set) =>
            {
                foreach (ModComponent c in Siblings(p, parent, set))
                {
                    if (!emitted.Add(c)) continue;
                    ordered.Add(c);
                    List<ModComponent> kids; if (children.TryGetValue(c.Id, out kids)) emit(c, kids);
                }
            };
            emit(null, p.Components.Where(c => c.ParentId == null).ToList());
            ordered.AddRange(p.Components.Where(c => !emitted.Contains(c)));   // never drop a component
            p.Components = ordered;
            p.PrepareChoices();
        }

        private static List<ModComponent> Siblings(ModPackage p, ModComponent parent, List<ModComponent> set)
        {
            // Same as Fluffy: higher first, unset = 0, ties by name (set is already in natural name order).
            if (set.Any(c => PackageReader.MenuPriority(c).HasValue))
                return set.OrderByDescending(c => PackageReader.MenuPriority(c) ?? 0).ToList();
            // Authors number names to order them ("1) Main files", "2! Skin Options", "03A1 Shoes"): keep name order.
            if (set.Count(c => Regex.IsMatch(c.Name ?? "", @"^\s*\d")) >= 2) return set;
            List<ModComponent> sorted = set.OrderBy(c => c.IsDefaultChoice ? 0 : 1).ThenBy(c => c.IsDummy ? 1 : 0).ToList();   // stable: name order inside
            if (parent != null && p.IsChoice(parent)) return sorted;   // alternatives, not tweaks
            var plain = sorted.Where(c => !c.IsDummy).ToList();
            var anchor = new Dictionary<ModComponent, ModComponent>();
            foreach (ModComponent b in plain)
            {
                var dests = Destinations(b);
                if (dests.Count == 0) continue;
                ModComponent best = null; int bestOverlap = 0; bool tie = false;
                foreach (ModComponent a in plain)
                {
                    // A tweak changes part of a much bigger option; alternatives of a similar size stay in name order.
                    if (a == b || Files(a) < 4 * Files(b)) continue;
                    int overlap = Destinations(a).Count(dests.Contains);
                    if (overlap * 2 < dests.Count) continue;
                    if (overlap > bestOverlap) { best = a; bestOverlap = overlap; tie = false; }
                    else if (overlap == bestOverlap) tie = true;
                }
                if (best != null && !tie) anchor[b] = best;   // two equal candidates: unclear which it tweaks, leave it
            }
            var result = new List<ModComponent>();
            Action<ModComponent> place = null;
            place = a => { result.Add(a); foreach (ModComponent t in plain.Where(t => { ModComponent x; return anchor.TryGetValue(t, out x) && x == a; })) place(t); };
            foreach (ModComponent a in plain.Where(c => !anchor.ContainsKey(c))) place(a);
            result.AddRange(sorted.Where(c => c.IsDummy));
            return result;
        }

        // A choose-one option whose every file is already in the main files, byte for byte, is what
        // the mod installs when nothing in the group is picked.
        private static void MarkDefaults(ModPackage p, SameFile same)
        {
            foreach (ModComponent c in p.Components) c.IsDefaultChoice = false;
            var main = new Dictionary<string, NativeAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (ModComponent c in p.Components.Where(c => !c.IsDummy && Composer.IsBase(c)))
                foreach (var a in Keyed(c)) if (!main.ContainsKey(a.Key)) main.Add(a.Key, a.Value);
            if (main.Count == 0) return;
            foreach (ModComponent group in p.Components.Where(p.IsChoice))
            {
                var matches = p.Components.Where(b => !b.IsDummy && !Composer.IsBase(b) && p.IsChoiceBranch(group, b) && Files(b) > 0
                    && Keyed(b).All(a => { NativeAsset m; return main.TryGetValue(a.Key, out m) && same.Equal(m, a.Value); })).ToList();
                if (matches.Count == 1) matches[0].IsDefaultChoice = true;
            }
        }

        private static int Files(ModComponent c) { return c.Assets.Count + c.RootAssets.Count; }
        private static IEnumerable<KeyValuePair<string, NativeAsset>> Keyed(ModComponent c)
        {
            return c.Assets.Select(a => new KeyValuePair<string, NativeAsset>(a.DestinationPath, a))
                .Concat(c.RootAssets.Select(a => new KeyValuePair<string, NativeAsset>("root:" + a.DestinationPath, a)));
        }
        private static HashSet<string> Destinations(ModComponent c) { return new HashSet<string>(Keyed(c).Select(a => a.Key), StringComparer.OrdinalIgnoreCase); }

        // Same size and checksum (ZIP / 7-Zip listing); folders hash the two files, only when sizes match.
        private sealed class SameFile
        {
            private readonly SourceReader r; private readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal SameFile(SourceReader reader) { r = reader; }
            internal bool Equal(NativeAsset x, NativeAsset y)
            {
                SourceEntry a, b;
                if (!r.Entries.TryGetValue(x.EntryPath, out a) || !r.Entries.TryGetValue(y.EntryPath, out b) || a.Size != b.Size) return false;
                uint? ca = a.ZipChecksum ?? a.ListedCrc, cb = b.ZipChecksum ?? b.ListedCrc;
                if (ca.HasValue && cb.HasValue) return ca.Value == cb.Value;
                string ha = Hash(a), hb = Hash(b);
                return ha != null && ha == hb;
            }
            private string Hash(SourceEntry e)
            {
                if (!Path.IsPathRooted(e.Original ?? "") || !File.Exists(e.Original)) return null;   // folder sources only
                string h; if (hashes.TryGetValue(e.Original, out h)) return h;
                try { using (var sha = FastHash.Sha256()) using (var s = File.OpenRead(e.Original)) h = BitConverter.ToString(sha.ComputeHash(s)); }
                catch (IOException) { h = null; } catch (UnauthorizedAccessException) { h = null; }
                hashes[e.Original] = h; return h;
            }
        }
    }
}
