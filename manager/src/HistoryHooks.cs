using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 2.4.1: history events from the places every change goes through (window, CLI and Assistant alike).
    // Each hook runs after its change committed and never throws.
    internal sealed partial class ReplaceEngine
    {
        // Called by Change() after Execute: what the committed state change did to each record.
        // Installs, switches (another variant/options of the same source in place), uninstalls, restores,
        // enables and disables are derived from the two states; priority moves are recorded by Move().
        private void RecordStateChange(string description, ReplaceState before, ReplaceState after)
        {
            try
            {
                var events = new List<HistoryEvent>();
                var was = before.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
                var now = after.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
                bool restore = (description ?? "").StartsWith("Restore ", StringComparison.Ordinal) && description != "Restore original game files" && description != "Restore replacements";
                var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ReplaceMod added in after.Mods.Where(m => !was.ContainsKey(m.Id)))
                {
                    int at = after.Mods.IndexOf(added);
                    ReplaceMod old = before.Mods.FirstOrDefault(m => !now.ContainsKey(m.Id) && before.Mods.IndexOf(m) == at && ReplacePlanner.SameSource(m.SourcePath, added.SourcePath));
                    HistoryEvent e;
                    if (old != null)
                    {
                        replaced.Add(old.Id);
                        e = History.ForReplacement(HistoryKinds.Update, added); e.PrevId = old.Id; e.PrevOptions = (old.SelectedIds ?? new List<string>()).ToList();
                    }
                    else e = History.ForReplacement(restore ? HistoryKinds.Recover : HistoryKinds.Install, added);
                    if (description == "Restore replacements") e.Detail = "from a preset";
                    events.Add(e);
                }
                foreach (ReplaceMod gone in before.Mods.Where(m => !now.ContainsKey(m.Id) && !replaced.Contains(m.Id)))
                {
                    HistoryEvent e = History.ForReplacement(HistoryKinds.Uninstall, gone);
                    if (description == "Restore original game files") e.Detail = "Restore original game files";
                    events.Add(e);
                }
                foreach (ReplaceMod m in after.Mods.Where(x => was.ContainsKey(x.Id) && was[x.Id].Enabled != x.Enabled))
                    events.Add(History.ForReplacement(m.Enabled ? HistoryKinds.Enable : HistoryKinds.Disable, m));
                History.Record(DataDir, events);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private void RecordMove(string id, int from, int to)
        {
            try
            {
                if (from == to) return;
                ReplaceMod m = LoadState().Find(id); if (m == null) return;
                HistoryEvent e = History.ForReplacement(HistoryKinds.Reorder, m); e.Detail = "#" + (from + 1) + " → #" + (to + 1);
                History.Record(DataDir, e);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // Journal recovery at the next start or change: a change that had been interrupted.
        private void RecordRecovery(ReplaceJournal journal, bool finished)
        {
            try
            {
                History.Record(DataDir, new HistoryEvent { Kind = HistoryKinds.Recover, Mode = HistoryModes.Replace, Name = journal.Description ?? "", Via = "journal", Detail = finished ? "Interrupted change finished" : "Interrupted change rolled back" });
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private void RecordFluffy(string kind, FluffyRemovedRecord record, string detail)
        {
            try
            {
                if (record == null) return;
                History.Record(DataDir, new HistoryEvent { Kind = kind, Mode = HistoryModes.Fluffy, Name = String.IsNullOrEmpty(record.ModName) ? record.Section : record.ModName, Slot = record.Section ?? "", Detail = detail ?? "" });
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    // Extra outfits (Installer and OutfitSetStore are static and write the app's Data history).
    internal static class OutfitHistory
    {
        internal static void Installed(InstallManifest manifest, InstallManifest previous, string modName)
        {
            try
            {
                if (manifest == null) return;
                HistoryEvent e = History.ForOutfit(previous == null ? HistoryKinds.Install : HistoryKinds.Update, manifest.SourcePath, manifest.Name, manifest.Character, manifest.Id, manifest.SelectedIds);
                e.Mod = modName ?? "";
                if (previous != null) e.PrevOptions = (previous.SelectedIds ?? new List<string>()).ToList();
                History.Record(null, e);
            }
            catch (Exception ex) { Program.Log(ex); }
        }
        internal static void Changed(string kind, InstalledOutfit outfit, string detail)
        {
            try
            {
                if (outfit == null) return;
                HistoryEvent e = History.ForOutfit(kind, outfit.Managed ? outfit.SourcePath : "", outfit.Name, outfit.Character, outfit.Managed ? outfit.Id : "", outfit.Managed ? outfit.SelectedIds : null);
                if (!outfit.Managed) { e.Slot = outfit.Character + "/" + Path.GetFileName(outfit.Folder); e.Name = Path.GetFileNameWithoutExtension(outfit.Folder); }
                e.Detail = detail ?? "";
                History.Record(null, e);
            }
            catch (Exception ex) { Program.Log(ex); }
        }
        // A restored entry ("Character/Folder"): managed ones carry their manifest.
        internal static void Restored(string kind, string destination, string relative, bool managed, string detail)
        {
            try
            {
                InstallManifest m = null;
                if (managed)
                {
                    string file = Path.Combine(destination, ".slots-installer.json");
                    if (File.Exists(file) && new FileInfo(file).Length < 1024 * 1024) m = Json.Read<InstallManifest>(file);
                }
                string[] parts = (relative ?? "").Split('/');
                string character = m != null ? m.Character : parts[0], name = m != null ? m.Name : parts.Length > 1 ? Path.GetFileNameWithoutExtension(parts[1]) : relative;
                HistoryEvent e = History.ForOutfit(kind, m == null ? "" : m.SourcePath, name, character, m == null ? "" : m.Id, m == null ? null : m.SelectedIds);
                if (m == null) e.Slot = relative ?? "";
                e.Detail = detail ?? "";
                History.Record(null, e);
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }
}
