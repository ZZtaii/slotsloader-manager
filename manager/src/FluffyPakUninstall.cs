using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 2.4.2: a Fluffy PAK install (installtype=invalidate or pakcopy_reengine_invalidate) uninstalled by the Manager.
    // Fluffy's own uninstall (snapshot 3, 2026-10-14) deletes the listed files and patch PAK and its 16-byte dummy PAKs,
    // but left the base rows of the pakcopy mod zeroed; the Manager restores them itself (PakRepair.cs proofs).
    public sealed class FluffyRemovedRow
    {
        public string Pak = "", Value = "", Path = "", How = "", PakHeader = "";   // Value: the restored 8 bytes (hex)
        public int Index;
        public long PakLength;
    }

    public sealed class FluffyRemovedPatch
    {
        // deleted: the mod's patch PAK; placeholder: replaced by Fluffy's empty dummy because a PAK above it stays;
        // dummy-deleted: one of Fluffy's empty dummies no PAK above needs any more.
        public string Name = "", Action = "", Stored = "", ShaBefore, ShaAfter;
        public int Number;
    }

    internal sealed partial class ReplaceEngine
    {
        private static readonly Regex PatchPakPath = new Regex(@"^re_chunk_000\.pak\.patch_(\d+)\.pak$", RegexOptions.IgnoreCase);
        internal static bool IsPatchPakPath(string relative) { return PatchPakPath.IsMatch(FluffyInstalls.Key(relative ?? "")); }

        // Fluffy's dummy patch PAK (seen: 16 bytes, KPKA 4.0, no entries) that keeps the patch row unbroken.
        internal static readonly byte[] FluffyDummyPak = { (byte)'K', (byte)'P', (byte)'K', (byte)'A', 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        internal static bool IsFluffyDummy(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length != PakIndex.HeaderSize) return false;
                byte[] b = File.ReadAllBytes(file);
                return b[0] == (byte)'K' && b[1] == (byte)'P' && b[2] == (byte)'K' && b[3] == (byte)'A' && b[4] == 4 && BitConverter.ToUInt32(b, 8) == 0;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }

        // The rows this mod invalidated that come back: each proven (TOC backup, Fluffy's archive list or hash(path)),
        // owned by nobody else (a path another Fluffy installed.ini lists stays invalidated; rows the Manager zeroed stay).
        // Any of its paths that could be one of the unproven zeroed rows refuses the whole uninstall.
        private List<FluffyRemovedRow> PlanFluffyRows(FluffyMod target, List<FluffyMod> remaining, ReplaceState state, FluffyInstalls fluffy)
        {
            var rows = new List<FluffyRemovedRow>();
            List<string> natives = target.Files.Select(FluffyInstalls.Key).Where(p => p.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (natives.Count == 0) return rows;
            var shared = new HashSet<string>(remaining.SelectMany(m => m.Files).Select(FluffyInstalls.Key), StringComparer.OrdinalIgnoreCase);
            ZeroedRowReport report = AnalyzeZeroedRows(state, fluffy, remaining, true, true);
            if (report.Rows.Count == 0) return rows;
            var problems = new List<string>();
            var paks = new Dictionary<string, PakIndex>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in natives)
            {
                if (shared.Contains(path)) continue;   // another Fluffy mod lists it: its entry stays invalidated
                ulong h = PakHash.PathHash(path);
                foreach (RowProof r in report.Rows)
                {
                    if (r.Proven)
                    {
                        if (r.Value != h || !r.Orphan) continue;
                        PakIndex p; if (!paks.TryGetValue(r.Pak, out p)) paks[r.Pak] = p = PakIndex.Open(GamePath(r.Pak), false);
                        rows.Add(new FluffyRemovedRow { Pak = r.Pak, Index = r.Index, Value = BitConverter.ToString(BitConverter.GetBytes(r.Value)).Replace("-", ""), Path = path, How = r.How, PakLength = p.Length, PakHeader = BitConverter.ToString(p.Header).Replace("-", "") });
                    }
                    // An unproven row this path may be (even one another Fluffy path may be too): left zeroed it would
                    // hide the game's file once the loose copy is gone, so the uninstall waits for proof.
                    else if (r.Owner != "the Manager" && r.Fits(h)) problems.Add(path + " (" + r.Where + ": " + r.Problem + ")");
                }
            }
            if (problems.Count > 0)
                throw new IOException("Couldn't prove which game entries '" + target.Name + "' invalidated: " + String.Join("; ", problems.Take(3)) + (problems.Count > 3 ? "; …" : "") + ". Nothing was changed. Uninstall it in the external mod manager instead.");
            return rows;
        }

        // The mod's patch PAKs leave the row: deleted when no other PAK sits above, else replaced by Fluffy's empty dummy
        // (other mods' PAKs, SlotsLoader's, are never renamed or renumbered). Fluffy's dummies above the highest PAK that
        // stays are deleted, as Fluffy does ("Deleted dummy file"), unless another Fluffy mod lists them.
        private List<FluffyRemovedPatch> PlanFluffyPatches(FluffyMod target, List<FluffyMod> remaining)
        {
            var result = new List<FluffyRemovedPatch>();
            var listed = new HashSet<string>(target.Files.Select(FluffyInstalls.Key).Where(IsPatchPakPath), StringComparer.OrdinalIgnoreCase);
            if (listed.Count == 0) return result;
            var others = new HashSet<string>(remaining.SelectMany(m => m.Files).Select(FluffyInstalls.Key).Where(IsPatchPakPath), StringComparer.OrdinalIgnoreCase);
            List<PatchPak> paks = LoaderCostumePak.Scan(GameRoot);
            var removing = paks.Where(p => listed.Contains(p.Name) && !others.Contains(p.Name)).ToList();
            int top = paks.Where(p => !removing.Contains(p) && !IsFluffyDummy(p.Path)).Select(p => p.Number).DefaultIfEmpty(0).Max();
            foreach (PatchPak p in removing) result.Add(new FluffyRemovedPatch { Name = p.Name, Number = p.Number, Action = p.Number < top ? "placeholder" : "deleted" });
            foreach (PatchPak p in paks.Where(p => !removing.Contains(p) && p.Number > top && !others.Contains(p.Name) && IsFluffyDummy(p.Path))) result.Add(new FluffyRemovedPatch { Name = p.Name, Number = p.Number, Action = "dummy-deleted" });
            return result;
        }

        // Writes the planned rows (undo = false: zero → the proven hash; undo = true: back to zero), verified.
        private void WriteFluffyRows(IEnumerable<FluffyRemovedRow> rows, bool undo)
        {
            foreach (var group in rows.GroupBy(r => r.Pak, StringComparer.OrdinalIgnoreCase))
            {
                string full = GamePath(group.Key);
                FluffyRemovedRow first = group.First();
                var writes = group.Select(r => new PakHashWrite { Index = r.Index, Expected = undo ? Unhex(r.Value) : new byte[8], Value = undo ? new byte[8] : Unhex(r.Value) }).ToList();
                PakWriter.WriteHashes(full, first.PakLength, Unhex(first.PakHeader), writes);
                GamePaks.Forget(full);
            }
        }

        // Undo/Recover check: every restored row still holds its restored value (or zero) in the same PAK.
        private string FluffyRowsChanged(FluffyRemovedRecord record)
        {
            foreach (var group in record.Rows.GroupBy(r => r.Pak, StringComparer.OrdinalIgnoreCase))
            {
                string full = GamePath(group.Key);
                if (!File.Exists(full)) return group.Key + " is missing";
                PakIndex p = PakIndex.Open(full, false);
                FluffyRemovedRow first = group.First();
                if (p.Length != first.PakLength || BitConverter.ToString(p.Header).Replace("-", "") != first.PakHeader) return group.Key + " was updated (a game update or a Steam file check)";
                foreach (FluffyRemovedRow r in group)
                {
                    if (r.Index >= p.Count) return group.Key + " was updated";
                    string now = BitConverter.ToString(p.Table, r.Index * PakIndex.EntrySize, 8).Replace("-", "");
                    if (now != r.Value && now != "0000000000000000") return "game entry " + group.Key + " #" + r.Index + " changed";
                }
            }
            return null;
        }

        private void RestoreFluffyPatch(string folder, FluffyRemovedPatch patch)
        {
            string full = GamePath(patch.Name);
            if (patch.Stored == null) return;
            CopyExact(Path.Combine(folder, "paks", patch.Stored), full);
        }
    }
}
