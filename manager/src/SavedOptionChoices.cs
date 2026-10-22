using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    public static partial class Composer
    {
        // Only persisted selections use this path. New multi-picks still fail Plan.
        // Never rewrite records on read, or guess an absent/reordered saved choice.
        internal static List<string> RestoreSelection(ModPackage package, IEnumerable<string> saved, out string note)
        {
            note = "";
            var original = (saved ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (original.Any(id => package.Find(id) == null))
                throw new IOException("Saved options changed. Review the selection before installing.");
            var normalized = original.ToList();
            var winners = new List<string>();
            foreach (var group in package.Components.Where(c => c.InferredChoice))
            {
                var members = package.Components.Where(c => String.Equals(c.ParentId, group.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                var memberIds = new HashSet<string>(members.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
                var picks = original.Where(memberIds.Contains).ToList();
                if (picks.Count < 2) continue;
                if (members.Any(c => IsBase(c) || !String.IsNullOrWhiteSpace(c.AddonFor))
                    || members.Any(c => !RootOptionChoices.Destinations(members[0]).SetEquals(RootOptionChoices.Destinations(c))))
                    throw new IOException("Saved options changed. Review the selection before installing.");
                string winner = picks.Last();
                normalized.RemoveAll(id => memberIds.Contains(id) && !String.Equals(id, winner, StringComparison.OrdinalIgnoreCase));
                winners.Add(package.Find(winner).Name);
            }
            if (winners.Count == 0) return original;
            // Include root files for the comparison, so the invariant covers both modes.
            // The sole bypass is the new inferred-cohort multi-pick rejection.
            var before = PlanCore(package, original, true, true);
            var after = Plan(package, normalized, true);
            if (before.Errors.Count > 0 || after.Errors.Count > 0 || before.Files.Count != after.Files.Count
                || before.Files.Any(kv => { PlannedFile f; return !after.Files.TryGetValue(kv.Key, out f) || !String.Equals(f.Asset.EntryPath, kv.Value.Asset.EntryPath, StringComparison.OrdinalIgnoreCase) || f.Component.Id != kv.Value.Component.Id; }))
                throw new IOException("Saved options changed. Review the selection before installing.");
            note = "Saved selection resolves to " + String.Join(", ", winners) + "; the earlier alternatives were overwritten.";
            return normalized;
        }
        internal static CompositionPlan SavedPlan(ModPackage package, IEnumerable<string> saved, bool replaceMode)
        {
            string note;
            var plan = Plan(package, RestoreSelection(package, saved, out note), replaceMode);
            if (note.Length > 0) plan.Warnings.Add(note);
            return plan;
        }
        internal static bool MatchesSavedSelection(ModPackage package, IEnumerable<string> current, IEnumerable<string> saved)
        {
            try { string note; return new HashSet<string>(current, StringComparer.OrdinalIgnoreCase).SetEquals(RestoreSelection(package, saved, out note)); }
            catch (IOException) { return false; }
        }
    }
}
