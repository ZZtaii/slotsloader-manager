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

        // The first patch number above every one in use: on disk (placeholders included) and recorded for installed
        // mods (a disabled PAK mod keeps its number). `releasing`: a record being replaced (its numbers may be reused).
        internal int NextPatchNumber(ReplaceState state, string releasing)
        {
            int top = 0;
            if (Directory.Exists(GameRoot))
                foreach (string f in Directory.EnumerateFiles(GameRoot, "re_chunk_000.pak.patch_*.pak")) top = Math.Max(top, PatchNumber(Path.GetFileName(f)));
            if (state != null)
                foreach (ReplaceMod m in state.Mods)
                    if (!String.Equals(m.Id, releasing, StringComparison.OrdinalIgnoreCase))
                        foreach (ModFile f in m.Files) top = Math.Max(top, PatchNumber(f.Path));
            return top + 1;
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
            for (int n = 1; n < top; n++)
            {
                if (present.Contains(n)) continue;
                string full = GamePath(PatchPakName(n)), tmp = full + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
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
