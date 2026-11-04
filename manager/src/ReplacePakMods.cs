using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 2.5.6: PAK mods in Replace original. A mod's .pak (outside natives) is installed as the next patch PAK,
    // re_chunk_000.pak.patch_NNN.pak with NNN one above every patch number in use (on disk or recorded for an
    // installed mod), the way Fluffy Mod Manager adds them. It is an ordinary root file of the mod's record, so
    // Uninstall, Disable, Undo and Reapply work as for tool mods, with three rules:
    // - removing it while a higher patch PAK exists leaves Fluffy's empty 16-byte placeholder (the game reads the
    //   patch PAKs as an unbroken row from patch_001, so a gap would hide every PAK above it);
    // - an empty placeholder counts as no file (it may be filled, and it is what removal leaves);
    // - it never takes over a number another (non-empty) PAK now uses: Undo or re-enabling then asks for a reinstall.
    internal sealed partial class ReplaceEngine
    {
        private static readonly Regex PatchNumberName = new Regex(@"^re_chunk_000\.pak\.patch_(\d+)\.pak$", RegexOptions.IgnoreCase);

        internal static int PatchNumber(string relative)
        {
            Match m = PatchNumberName.Match(FluffyInstalls.Key(relative ?? ""));
            int n; return m.Success && Int32.TryParse(m.Groups[1].Value, out n) ? n : 0;
        }
        internal static string PatchPakName(int number) { return "re_chunk_000.pak.patch_" + number.ToString("D3") + ".pak"; }

        private string CurrentSha(string relative, string full)
        {
            if (IsPatchPakPath(relative) && IsFluffyDummy(full)) return null;
            return ShaOrNull(full);
        }

        private bool HigherPatchPakExists(string relative)
        {
            int mine = PatchNumber(relative);
            if (mine <= 0 || !Directory.Exists(GameRoot)) return false;
            return Directory.EnumerateFiles(GameRoot, "re_chunk_000.pak.patch_*.pak").Any(f => PatchNumber(Path.GetFileName(f)) > mine);
        }

        // New numbers start above every reservation, even on an upgrade: a disabled PAK's old number
        // can be absent from disk and already allocated to a different payload in this same plan.
        internal int NextPatchNumber(ReplaceState state)
        {
            int top = 0;
            if (Directory.Exists(GameRoot))
                foreach (string f in Directory.EnumerateFiles(GameRoot, "re_chunk_000.pak.patch_*.pak")) top = Math.Max(top, PatchNumber(Path.GetFileName(f)));
            if (state != null)
                foreach (ReplaceMod m in state.Mods)
                    foreach (ModFile f in m.Files) top = Math.Max(top, PatchNumber(f.Path));
            return top + 1;
        }

        // Plans (including addon plans) can be prepared before another install takes their numbers.
        // Under the game lock, keep numbers held exclusively by this update and allocate new ones above every claim.
        private void AllocatePreparedPaks(ReplacePlan plan, ReplaceState state, ReplaceMod existing, ReplaceMod below)
        {
            var files = plan.Files.Where(f => IsPatchPakPath(f.Path)).OrderBy(f => PatchNumber(f.Path)).ToList();
            if (files.Count == 0) return;
            var reusable = existing == null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) :
                new HashSet<string>(existing.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            reusable.ExceptWith(state.Mods.Where(m => m != existing).SelectMany(m => m.Files).Select(f => f.Path));
            int next = NextPatchNumber(state);
            // SF6 loads higher patch numbers last. An addon must stay above every main PAK,
            // including new ones added when the main archive grew. Keep its own PAK order too.
            int previous = below == null ? 0 : below.Files.Select(f => PatchNumber(f.Path)).DefaultIfEmpty(0).Max();
            var moves = new List<KeyValuePair<PreparedFile, string>>();
            foreach (PreparedFile file in files)
            {
                int proposed = PatchNumber(file.Path);
                if (reusable.Contains(file.Path) && proposed > previous) { previous = proposed; continue; }
                int number = Math.Max(Math.Max(next, proposed), checked(previous + 1));
                previous = number;
                next = checked(number + 1);
                string target = PatchPakName(number);
                if (target != file.Path) moves.Add(new KeyValuePair<PreparedFile, string>(file, target));
            }
            // All moves are upwards. Move the highest first so a shifted range cannot overwrite itself.
            foreach (var move in moves.AsEnumerable().Reverse())
            {
                string target = Safety.Under(plan.StagingFolder, move.Value);
                File.Move(Safety.Under(plan.StagingFolder, move.Key.Path), target);
                plan.Items.Add(new HealthItem("info", Path.GetFileName(move.Key.EntryPath) + " uses " + move.Value + " in the installed patch order."));
                move.Key.Path = move.Value;
                move.Key.Source = target;
            }
        }

        // 1.0.1: a missing number below the highest patch PAK (left by any tool: an older loader that moved its pak, a
        // PAK mod disabled at the top before another was added, an external manager) hides every PAK above it, and
        // the game can stop at a black screen. Each gap gets the empty 16-byte placeholder; nothing else is touched.
        // Returns the numbers filled. Callers make sure SF6 and the external mod manager are closed.
        internal List<int> FillPatchGaps()
        {
            var filled = new List<int>();
            if (!Directory.Exists(GameRoot)) return filled;
            var present = new HashSet<int>(Directory.EnumerateFiles(GameRoot, "re_chunk_000.pak.patch_*.pak").Select(f => PatchNumber(Path.GetFileName(f))).Where(n => n > 0));
            int top = present.DefaultIfEmpty(0).Max();
            if (top > 1 && Enumerable.Range(1, top - 1).Any(n => !present.Contains(n))) LinuxPlatform.RequireClosed("repair missing patch PAKs");
            for (int n = 1; n < top; n++)
            {
                if (present.Contains(n)) continue;
                string full = GamePath(PatchPakName(n)), tmp = full + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                Safety.NoReparseChain(full); Safety.NoReparseChain(tmp);
                File.WriteAllBytes(tmp, FluffyDummyPak);
                try { File.Move(tmp, full); filled.Add(n); }
                catch (IOException) { try { File.Delete(tmp); } catch { } if (!File.Exists(full)) throw; }   // another tool wrote it meanwhile
            }
            if (filled.Count > 0) Program.LogLine("Patch PAK gaps filled with empty placeholders: " + String.Join(", ", filled.Select(PatchPakName)));
            return filled;
        }
        internal static string GapText(List<int> filled)
        {
            return filled.Count == 0 ? "" : (filled.Count == 1 ? PatchPakName(filled[0]) + " was missing" : filled.Count + " patch PAKs were missing (" + String.Join(", ", filled.Take(3).Select(PatchPakName)) + (filled.Count > 3 ? ", …" : "") + ")") + ", which hides the PAKs after it: filled with an empty placeholder.";
        }

        // A mod's file that is a PAK to add (outside natives, .pak), not one of the game's own names.
        internal static bool IsModPak(string destination)
        {
            string p = PakHash.Normalize(destination ?? "").TrimStart('/').ToLowerInvariant();
            return p.EndsWith(".pak") && !p.StartsWith("natives/");
        }
    }
}
