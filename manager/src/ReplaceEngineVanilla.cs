using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.1 (the user): gear → Restore → "Restore game to vanilla". Beyond removing this Manager's mods it also clears
    // the external mod manager's installs and record, every patch PAK (any tool's) and the natives folder, then puts
    // back the game entries left invalidated (each with a proof). REFramework, costume_mods and the extra outfits stay
    // (1.0.11: also this Manager's own REFramework and Slotsloader records; the external manager's go with its mods).
    // Nothing is kept for an Undo: the click is a confirmed reset. Order: the external manager's mods first (newest
    // first, as they were stacked over this Manager's), then this Manager's (Restore original for any file another
    // tool changed), then the leftovers. Each step is its own complete change, so a stop leaves a consistent game.
    internal sealed partial class ReplaceEngine
    {
        internal string RestoreVanilla()
        {
            GameProcesses.RequireIdle("restore the game to vanilla");
            if (GameProcesses.Probe.IsFluffyRunning()) throw new IOException(FluffyRunningReason);
            var done = new List<string>();

            // 1. The external mod manager's installs, the way it uninstalls them; its record ends empty.
            List<FluffyMod> fluffyMods = Fluffy().Mods.OrderByDescending(m => m.Index).ToList();
            int fluffyRemoved = 0;
            foreach (FluffyMod m in fluffyMods)
            {
                string kept;
                try { UninstallFluffy(m.Root, m.Section, out kept, true); fluffyRemoved++; }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException) throw;
                    throw new IOException("Stopped at the external mod manager's mod '" + m.Name + "': " + ex.Message + (done.Count > 0 || fluffyRemoved > 0 ? " Done before that: " + ReplacePlanner.N(fluffyRemoved, "of its mods") + " uninstalled." : ""), ex);
                }
                if (kept != null) try { DeleteFluffyRemoved(kept); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
            if (fluffyRemoved > 0) done.Add("Uninstalled the external mod manager's " + ReplacePlanner.N(fluffyRemoved, "mod") + ".");

            // 2. This Manager's mods, originals back (another tool's changed file gives way to the original).
            // 1.0.11 (the user): its REFramework and Slotsloader stay, as the confirmation says (KeptByVanilla).
            ReplaceState before = LoadState();
            int mine = before.Mods.Count(m => !KeptByVanilla(m));
            bool keptRefw = before.Mods.Any(m => KeptByVanilla(m) && !LoaderCostumePak.IsLoaderMod(m)), keptLoader = before.Mods.Any(m => KeptByVanilla(m) && LoaderCostumePak.IsLoaderMod(m));
            string restored = keptRefw || keptLoader
                ? Change("Restore game to vanilla", true, delegate(ReplaceState s) { s.Mods.RemoveAll(m => !KeptByVanilla(m)); return s; }, new ChangeOptions { Choice = DriftChoice.RestoreOriginal, IncludeReleased = true }, null, null)
                : RestoreAll(DriftChoice.RestoreOriginal);
            if (mine > 0) done.Add("Removed this Manager's " + ReplacePlanner.N(mine, "mod") + ".");

            // 3. Every patch PAK and the natives folder, whoever put them there.
            int paks = 0, natives = 0;
            using (Lock())
            {
                foreach (string pak in Directory.GetFiles(GameRoot, "re_chunk_000.pak.patch_*.pak"))
                {
                    Safety.NoReparse(pak); File.SetAttributes(pak, FileAttributes.Normal); File.Delete(pak); GamePaks.Forget(pak); paks++;
                }
                string nativesDir = GamePath("natives");
                if (Directory.Exists(nativesDir))
                {
                    Safety.NoReparseChain(nativesDir);
                    natives = Directory.GetFiles(nativesDir, "*", SearchOption.AllDirectories).Length;
                    foreach (string f in Directory.GetFiles(nativesDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(nativesDir, true);
                }
            }
            if (paks > 0) done.Add("Deleted " + ReplacePlanner.N(paks, "patch PAK") + ".");
            if (keptLoader) LoaderCostumePak.ClearFingerprint(GameRoot);   // the kept Slotsloader rebuilds its costume pak at the next start
            if (natives > 0) done.Add("Deleted the natives folder (" + ReplacePlanner.N(natives, "file") + ").");

            // 4. Entries still invalidated point at files that are gone now: each one proven goes back.
            string repair;
            try
            {
                PakRepairResult r = RepairOrphanedRows(true);
                repair = r.Repaired.Count > 0 ? "Restored " + ReplacePlanner.N(r.Repaired.Count, "game entry") + " in the game PAKs." : "";
                if (r.Unproven.Count > 0) repair = (repair + " " + ReplacePlanner.N(r.Unproven.Count, "game entry") + " couldn't be proven: verify SF6's files in Steam to restore " + (r.Unproven.Count == 1 ? "it" : "them") + ".").Trim();
            }
            catch (IOException ex) { repair = ex.Message; }
            if (repair.Length > 0) done.Add(repair);

            PakIndex basePak = File.Exists(GamePath("re_chunk_000.pak")) ? GamePaks.Cached(GamePath("re_chunk_000.pak")) : null;
            int zeroed = basePak == null ? 0 : basePak.CountZeroed();
            string stay = keptRefw && keptLoader ? "REFramework, Slotsloader and your extra outfits stay" : keptRefw ? "REFramework and your extra outfits stay" : keptLoader ? "Slotsloader and your extra outfits stay" : "your extra outfits stay";
            done.Add(zeroed == 0 ? "The game is vanilla (" + stay + ")." : "The base PAK still has " + ReplacePlanner.N(zeroed, "invalidated entry") + ": verify SF6's files in Steam.");
            Program.LogLine("Restore to vanilla: " + String.Join(" ", done) + (String.IsNullOrEmpty(restored) ? "" : " | " + restored));
            return String.Join(" ", done);
        }

        // 1.0.11: this Manager's REFramework (a plain dinput8.dll install) and Slotsloader survive Restore game to vanilla,
        // as long as none of their files is under natives or a patch PAK (step 3 deletes those).
        internal static bool KeptByVanilla(ReplaceMod m)
        {
            if (m == null || m.Files.Any(f => IsPatchPakPath(f.Path) || PakHash.Normalize(f.Path ?? "").TrimStart('/').StartsWith("natives/", StringComparison.OrdinalIgnoreCase))) return false;
            return LoaderCostumePak.IsLoaderMod(m) || ReplacePlanner.IsPlainReFramework(m.Files.Select(f => f.Path));
        }
    }
}
