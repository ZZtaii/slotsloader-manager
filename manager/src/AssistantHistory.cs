using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 2.4.1: the Assistant's get_history tool. Read-only; only names, ids and dates leave the app (no paths).
    internal static class AssistantHistory
    {
        internal const string Description = "Read the Manager's local install and play history per mod: first and last install, last switch (other options or variant), last uninstall, last played, play count and last_used (last played, else the last install or switch). Plays come from game launches the Manager saw while it was open and from Slotsloader's log (the extra outfits it built at each launch); a replacement or tool mod counts as played when it was installed and enabled during a launch. Currently installed mods are always listed, also when they predate the history; uninstalled ones and mods whose source was deleted keep their rows. Use it for rotation requests (\"swap Cammy outfits for ones not used in a month\"): installed_ids / replacement_ids identify current installs for set_outfit_enabled, uninstall_outfits and change_game_mod; mod_id (when the mod is still in a library) feeds get_mod_options, check_outfit, install_outfit and install_replacement. Read-only.";

        internal static ToolParam[] Params()
        {
            return new[] {
                new ToolParam { Name = "character", Type = "string", Description = "Exact roster character name (see get_overview), or empty string for every character." },
                new ToolParam { Name = "kind", Type = "string", Enum = new[] { "", "extra_outfit", "replace", "tool", "external" }, Description = "Empty string = every kind; extra_outfit, replace (Replace original), tool (tool mods) or external (mods an external mod manager installed and the Manager uninstalled)." },
                new ToolParam { Name = "install_state", Type = "string", Enum = new[] { "", "installed", "not_installed" }, Description = "Empty string = both; installed = installed now (enabled or disabled); not_installed = uninstalled since." },
                new ToolParam { Name = "not_used_days", Type = "integer", Description = "Only mods whose last_used is at least this many days ago, or never. 0 = no age filter." },
                new ToolParam { Name = "used_within_days", Type = "integer", Description = "Only mods used in the last this many days. 0 = no filter." },
                new ToolParam { Name = "query", Type = "string", Description = "Words that must all appear in the mod's name or character (case-insensitive). Empty string = no text filter." },
                new ToolParam { Name = "order", Type = "string", Enum = new[] { "", "least_recent", "most_recent", "most_played" }, Description = "Empty string or least_recent = least recently used first (never used first); most_recent = most recent first; most_played = highest play_count first." },
                new ToolParam { Name = "limit", Type = "integer", Description = "Maximum mods to return, 1-200. Use 0 for the default of 50." } };
        }

        private static string When(DateTime? utc) { return utc.HasValue ? utc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : null; }

        private sealed class Row
        {
            internal ModHistory H;
            internal List<InstalledOutfit> Outfits = new List<InstalledOutfit>();
            internal List<ReplaceMod> Replacements = new List<ReplaceMod>();
            internal bool Installed { get { return Outfits.Count > 0 || Replacements.Count > 0 || (H.Mode == HistoryModes.Fluffy && H.Installed); } }
            internal bool Enabled { get { return Outfits.Any(o => o.Enabled) || Replacements.Any(m => m.Enabled); } }
            internal DateTime? LastUsed;
        }

        internal static JsonObj Run(AssistantWorkspace workspace, HistoryIndex index, string character, string kind, string installState, long notUsedDays, long usedWithinDays, string query, string order, long limit, DateTime nowUtc, out string activity)
        {
            string error; string who = AssistantWorkspace.Character(character, out error);
            if (who == null) throw new ArgumentException(error);
            if (notUsedDays < 0 || usedWithinDays < 0) throw new ArgumentException("not_used_days and used_within_days must be 0 or more.");
            if (limit <= 0) limit = 50; if (limit > 200) limit = 200;
            List<InstalledOutfit> installed = workspace.Installed();
            List<ReplaceMod> replacements = new List<ReplaceMod>();
            try { if (Directory.Exists(workspace.Config.GameRoot)) replacements = workspace.Engine().LoadState().Mods; } catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; }
            var library = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (LibraryEntry e in workspace.Library()) if (!String.IsNullOrEmpty(e.SourcePath)) library.Add(e.SourcePath); } catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; }

            // Every mod the history knows, plus current installs it does not know yet (installed before 2.4.1).
            var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (ModHistory h in index.Mods.Values) if (h.Mode.Length > 0 || h.Source.Length > 0) rows[h.Key] = new Row { H = h };
            foreach (InstalledOutfit o in installed)
            {
                string key = o.Managed && !String.IsNullOrEmpty(o.SourcePath) ? History.Normalize(o.SourcePath) : HistoryIndex.Key("", HistoryModes.Extra, o.Character + "/" + Path.GetFileName(o.Folder), "");
                Row row;
                if (!rows.TryGetValue(key, out row))
                {
                    var h = new ModHistory { Key = key, Source = o.Managed ? History.Normalize(o.SourcePath) : "", Name = o.Managed ? o.Name : Path.GetFileNameWithoutExtension(o.Folder), Mode = HistoryModes.Extra, Character = o.Character, LastInstalled = o.InstalledUtc == DateTime.MinValue ? (DateTime?)null : o.InstalledUtc };
                    h.FirstInstalled = h.LastInstalled;
                    row = new Row { H = h }; rows[key] = row;
                }
                row.Outfits.Add(o);
            }
            foreach (ReplaceMod m in replacements)
            {
                if (LoaderCostumePak.IsLoaderMod(m) && String.IsNullOrEmpty(m.SourcePath)) continue;
                string key = String.IsNullOrEmpty(m.SourcePath) ? "id:" + m.Id : History.Normalize(m.SourcePath);
                Row row;
                if (!rows.TryGetValue(key, out row))
                {
                    HistoryEvent shape = History.ForReplacement(HistoryKinds.Install, m);
                    DateTime at = LibraryUsage.ParseUtc(m.InstalledUtc);
                    var h = new ModHistory { Key = key, Source = shape.Source.Length > 0 ? History.Normalize(shape.Source) : "", Name = shape.Name, Mode = shape.Mode, Character = shape.Character, LastInstalled = at == DateTime.MinValue ? (DateTime?)null : at };
                    h.FirstInstalled = h.LastInstalled;
                    row = new Row { H = h }; rows[key] = row;
                }
                row.Replacements.Add(m);
            }

            var words = Regex.Split((query ?? "").Trim(), @"\s+").Where(w => w.Length > 0).ToList();
            var list = new List<Row>();
            foreach (Row row in rows.Values)
            {
                ModHistory h = row.H;
                row.LastUsed = h.LastUsed;
                if (who.Length > 0 && !(h.Character ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).Any(c => String.Equals(c, who, StringComparison.OrdinalIgnoreCase))) continue;
                if (kind.Length > 0 && h.Mode != (kind == "external" ? HistoryModes.Fluffy : kind)) continue;   // 2.5.1: "external" names the persisted mode
                if (installState == "installed" && !row.Installed) continue;
                if (installState == "not_installed" && row.Installed) continue;
                if (notUsedDays > 0 && row.LastUsed.HasValue && nowUtc - row.LastUsed.Value < TimeSpan.FromDays(notUsedDays)) continue;
                if (usedWithinDays > 0 && (!row.LastUsed.HasValue || nowUtc - row.LastUsed.Value >= TimeSpan.FromDays(usedWithinDays))) continue;
                if (words.Any(w => (h.Name ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0 && (h.Character ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                list.Add(row);
            }
            IEnumerable<Row> ordered;
            if (order == "most_recent") ordered = list.OrderByDescending(r => r.LastUsed.HasValue ? r.LastUsed.Value.Ticks : 0).ThenBy(r => r.H.Name, StringComparer.OrdinalIgnoreCase);
            else if (order == "most_played") ordered = list.OrderByDescending(r => r.H.Plays).ThenByDescending(r => r.LastUsed.HasValue ? r.LastUsed.Value.Ticks : 0).ThenBy(r => r.H.Name, StringComparer.OrdinalIgnoreCase);
            else ordered = list.OrderBy(r => r.LastUsed.HasValue ? r.LastUsed.Value.Ticks : 0).ThenBy(r => r.H.Name, StringComparer.OrdinalIgnoreCase);

            var results = new List<JsonObj>();
            foreach (Row row in ordered.Take((int)limit))
            {
                ModHistory h = row.H;
                var item = new JsonObj()
                    .Add("mod_id", h.Source.Length > 0 ? AssistantWorkspace.ModId(h.Source) : "")
                    .Add("name", h.Name).Add("character", String.IsNullOrEmpty(h.Character) ? "unknown" : h.Character).Add("kind", String.IsNullOrEmpty(h.Mode) ? "unknown" : h.Mode == HistoryModes.Fluffy ? "external" : h.Mode)
                    .Add("in_library", h.Source.Length > 0 && library.Contains(h.Source))
                    .Add("installed", row.Installed).Add("enabled", row.Enabled)
                    .Add("installed_ids", row.Outfits.Select(AssistantWorkspace.InstalledId).ToArray())
                    .Add("replacement_ids", row.Replacements.Select(AssistantWorkspace.ReplacementId).ToArray())
                    .Add("first_installed", When(h.FirstInstalled)).Add("last_installed", When(h.LastInstalled)).Add("last_switched", When(h.LastUpdated))
                    .Add("last_uninstalled", When(h.LastUninstalled)).Add("last_played", When(h.LastPlayed) ?? "never").Add("play_count", h.Plays)
                    .Add("last_used", When(row.LastUsed) ?? "never")
                    .Add("days_since_used", row.LastUsed.HasValue ? (object)(int)Math.Floor((nowUtc - row.LastUsed.Value).TotalDays) : null);
                results.Add(item);
            }
            activity = "Read history · " + ReplacePlanner.N(list.Count, "mod") + (who.Length > 0 ? " · " + who : "");
            return new JsonObj()
                .Add("history_since", When(index.Since))
                .Add("launches_recorded", index.Launches).Add("slot_uses", index.SlotUses)
                .Add("total_matches", list.Count).Add("returned", results.Count).Add("results", results)
                .Add("note", "Dates are UTC. last_used = last_played, else the last install or switch. Mods with play_count 0 were never seen in a launch.");
        }
    }
}
