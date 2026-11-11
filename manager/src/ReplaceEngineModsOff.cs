using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.18 (user request): "Turn outfit mods off" in Settings, to play vanilla for a while and get everything back.
    // Outfit mods only: every enabled replacement and PAK mod is disabled in one change (the game's own files come
    // back, as Disable does), and tool mods stay. Extra outfits are not moved or renamed: the Manager's Slotsloader
    // record is disabled (its few files leave the game folder) and the costume pak it generated is taken out of the
    // row, so the game shows no extra slots; costume_mods and the slot numbers in registry.json stay as they are.
    // The record of what was switched off is saved with the state, so turning back on enables exactly those again,
    // also after the app was closed. Slotsloader builds its pak again at the next SF6 start.
    internal sealed partial class ReplaceEngine
    {
        // A tool mod that also replaces outfit files ("Mixed") is one record: it can't be half off, so it stays on.
        internal static bool IsOutfitMod(ReplaceMod m) { return m != null && !LoaderCostumePak.IsLoaderMod(m) && m.Kind != "Tool" && m.Kind != "Mixed"; }

        internal string SetModsOff(bool off)
        {
            if (off)
            {
                ModsOffRecord record = null;
                string result = Change("Turn outfit mods off", true, delegate(ReplaceState s)
                {
                    if (s.ModsOff != null) throw new IOException("Outfit mods are already off.");
                    record = new ModsOffRecord { SinceUtc = DateTime.UtcNow.ToString("o") };
                    foreach (ReplaceMod m in s.Mods.Where(m => m.Enabled))
                    {
                        if (LoaderCostumePak.IsLoaderMod(m)) { record.LoaderId = m.Id; m.Enabled = false; }
                        else if (IsOutfitMod(m)) { record.ReplacementIds.Add(m.Id); m.Enabled = false; }
                        else if (m.Kind == "Mixed") record.KeptOn.Add(m.Name);
                    }
                    s.ModsOff = record; return s;
                }, false, null);
                string pak = record.LoaderId.Length > 0 ? AfterLoaderRemoved() : "";
                var notes = new List<string>();
                if (record.LoaderId.Length == 0 && LoaderCostumePak.LoaderPresent(GameRoot)) notes.Add("Slotsloader was not installed by this Manager, so extra outfits stay on.");
                if (record.KeptOn.Count > 0) notes.Add("Left on, because " + (record.KeptOn.Count == 1 ? "it is" : "they are") + " also a tool mod: " + String.Join(", ", record.KeptOn) + ".");
                return String.Join(" ", new[] { result, pak }.Concat(notes).Where(t => !String.IsNullOrWhiteSpace(t)));
            }
            int back = 0, gone = 0; bool loader = false;
            string on = Change("Turn outfit mods back on", true, delegate(ReplaceState s)
            {
                if (s.ModsOff == null) throw new IOException("Outfit mods are not off.");
                foreach (string id in s.ModsOff.ReplacementIds) { ReplaceMod m = s.Find(id); if (m == null) gone++; else if (!m.Enabled) { m.Enabled = true; back++; } }
                // Only one Slotsloader may be on: another one installed meanwhile wins and the earlier record stays off.
                ReplaceMod own = s.ModsOff.LoaderId.Length > 0 ? s.Find(s.ModsOff.LoaderId) : null;
                if (own != null && !s.Mods.Any(m => m.Enabled && LoaderCostumePak.IsLoaderMod(m))) { own.Enabled = true; loader = true; }
                s.ModsOff = null; return s;
            }, false, null);
            var said = new List<string> { on };
            if (loader) said.Add("Slotsloader builds its costume pak again when SF6 starts.");
            if (gone > 0) said.Add(ReplacePlanner.N(gone, "mod") + " uninstalled meanwhile " + (gone == 1 ? "was" : "were") + " skipped.");
            return String.Join(" ", said.Where(t => !String.IsNullOrWhiteSpace(t)));
        }
    }
}
