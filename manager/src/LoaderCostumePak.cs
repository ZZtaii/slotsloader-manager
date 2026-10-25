using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 2.4.0: the costume pak SlotsLoader generates (re_chunk_000.pak.patch_NNN.pak beside re_chunk_000.pak).
    // Recognised exactly as the loader recognises its own (loader_core.cpp classify_patch_paks, common/pak.cpp
    // classify_patch_pak): a patch pak whose entry table holds the path hash of
    // natives/stm/sf6_costume_slots.marker. With one entry it is a marker-only placeholder; the stage loader's
    // pak holds natives/stm/sf6_stage_slots.marker; anything else (a mod's, Fluffy's, unreadable) is foreign
    // and never touched. The game reads the patch paks as an unbroken row from patch_001, so a loader pak with a
    // foreign pak above it is replaced in place by the loader's own marker-only placeholder (retire_pak) and
    // deleted only when nothing foreign sits above it; the loader removes such placeholders itself later.
    internal enum PatchPakKind { Unreadable, Mod, Costume, Stage }

    internal sealed class PatchPak
    {
        internal int Number; internal string Path; internal PatchPakKind Kind; internal uint Entries; internal long Length;
        internal bool Placeholder { get { return Kind == PatchPakKind.Costume && Entries == 1; } }
        internal string Name { get { return System.IO.Path.GetFileName(Path); } }
    }

    internal static class LoaderCostumePak
    {
        internal const string CostumeMarker = "natives/stm/sf6_costume_slots.marker", StageMarker = "natives/stm/sf6_stage_slots.marker";
        internal static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes("SF6_CostumeSlots v1\n");   // the bytes the loader writes
        internal const string RegistryRelative = @"reframework\data\SF6_Costumes_Data\registry.json";
        private static readonly Regex PatchName = new Regex(@"^re_chunk_000\.pak\.patch_(\d+)\.pak$", RegexOptions.IgnoreCase);

        internal static List<PatchPak> Scan(string gameRoot)
        {
            var list = new List<PatchPak>();
            if (String.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return list;
            foreach (string file in Directory.EnumerateFiles(gameRoot, "re_chunk_000.pak.patch_*.pak"))
            {
                Match m = PatchName.Match(System.IO.Path.GetFileName(file));
                if (!m.Success) continue;
                var pak = new PatchPak { Number = Int32.Parse(m.Groups[1].Value), Path = file };
                uint entries; pak.Kind = Classify(file, out entries, out pak.Length); pak.Entries = entries;
                list.Add(pak);
            }
            return list.OrderBy(p => p.Number).ToList();
        }

        // Header (16 bytes) and the entry table, read like the loader does (no decryption: an encrypted or odd
        // table simply never matches the marker hash, so such a pak counts as a mod's).
        internal static PatchPakKind Classify(string path, out uint entries, out long length)
        {
            entries = 0; length = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new BinaryReader(fs))
                {
                    length = fs.Length;
                    if (fs.Length < 16) return PatchPakKind.Mod;
                    byte[] header = r.ReadBytes(16);
                    entries = BitConverter.ToUInt32(header, 8);
                    ulong costume = PakHash.PathHash(CostumeMarker), stage = PakHash.PathHash(StageMarker);
                    for (uint i = 0; i < entries; i++)
                    {
                        byte[] e = r.ReadBytes(48);
                        if (e.Length < 48) break;
                        ulong h = ((ulong)BitConverter.ToUInt32(e, 4) << 32) | BitConverter.ToUInt32(e, 0);
                        if (h == costume) return PatchPakKind.Costume;
                        if (h == stage) return PatchPakKind.Stage;
                    }
                    return PatchPakKind.Mod;
                }
            }
            catch (IOException) { return PatchPakKind.Unreadable; }
            catch (UnauthorizedAccessException) { return PatchPakKind.Unreadable; }
        }

        // Real loader costume paks (not placeholders): they make extra outfits load.
        internal static List<PatchPak> Generated(string gameRoot) { return Scan(gameRoot).Where(p => p.Kind == PatchPakKind.Costume && p.Entries > 1).ToList(); }

        // Takes the loader's costume paks out of the row (placeholder where a foreign pak sits above, deleted
        // otherwise; stale placeholders above every foreign pak deleted too) and clears the registry's
        // fingerprint so a reinstalled loader rebuilds. Never touches another pak, costume_mods or the
        // owned-outfit flag file. Returns one line for the user ("" when there was nothing to do).
        internal static string Remove(string gameRoot)
        {
            List<PatchPak> paks = Scan(gameRoot);
            var ours = paks.Where(p => p.Kind == PatchPakKind.Costume).ToList();
            if (ours.Count == 0) return "";
            // 1.0.1: an unreadable pak (locked, in use) counts as foreign: deleting ours below it would leave a gap.
            int topForeign = paks.Where(p => p.Kind == PatchPakKind.Mod || p.Kind == PatchPakKind.Unreadable || (p.Kind == PatchPakKind.Stage && p.Entries != 1)).Select(p => p.Number).DefaultIfEmpty(0).Max();
            var removed = new List<string>(); var kept = new List<string>(); var failed = new List<string>();
            foreach (PatchPak p in ours)
            {
                try
                {
                    if (p.Number > topForeign) { File.Delete(p.Path); if (!p.Placeholder) removed.Add(p.Name); }
                    else if (!p.Placeholder) { WritePlaceholder(p.Path); kept.Add(p.Name); }
                }
                catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException)) throw; failed.Add(p.Name + " (" + ex.Message.TrimEnd('.') + ")"); }
            }
            bool stamp = ClearFingerprint(gameRoot);
            var parts = new List<string>();
            if (removed.Count > 0) parts.Add("Removed Slotsloader's costume pak (" + String.Join(", ", removed) + ")");
            if (kept.Count > 0) parts.Add("Emptied Slotsloader's costume pak (" + String.Join(", ", kept) + "): it stays as a placeholder so another mod's pak after it still loads");
            if (failed.Count > 0) parts.Add("Couldn't remove " + String.Join(", ", failed) + ": close SF6, then click Remove it on the Installed page");
            string line = parts.Count == 0 ? "" : String.Join(". ", parts) + "." + (stamp ? " A reinstalled Slotsloader rebuilds it." : "");
            if (line.Length > 0) Program.LogLine("Slotsloader costume pak: " + line);
            return line;
        }

        // The loader's marker-only pak (common/pak.cpp write_marker_pak): one stored entry, the marker.
        internal static void WritePlaceholder(string path)
        {
            string tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            PakWriter.WriteFixture(tmp, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(CostumeMarker, MarkerBytes) }, 0, 0);
            DurableFile.Publish(tmp, path);
        }

        // The loader rebuilds when the fingerprint in registry.json differs (slot numbers and possession stay).
        internal static bool ClearFingerprint(string gameRoot)
        {
            string path = System.IO.Path.Combine(gameRoot, RegistryRelative);
            try
            {
                if (!File.Exists(path)) return false;
                string text = File.ReadAllText(path);
                string next = Regex.Replace(text, "(\"fingerprint\"\\s*:\\s*\")[^\"]*(\")", "$1$2");
                if (next != text) File.WriteAllText(path, next, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) { Program.Log(ex); return false; } throw; }
        }

        // A package or an installed record that is the SlotsLoader tool mod (its plugin).
        internal const string PluginPath = "reframework/plugins/SF6_CostumeSlotsNative.dll";
        internal static bool IsLoaderMod(ReplaceMod m) { return m != null && m.Files.Any(f => String.Equals(f.Path.Replace('\\', '/'), PluginPath, StringComparison.OrdinalIgnoreCase)); }

        // Identifies upstream builds; Prepare separately accepts their declared Manager contract >= 2.
        // The Manager's own Slotsloader (this fork) is released as "Slotsloader Fork" (earlier
        // "Slotsloader", author ZZtaii) or a "-custom" development build. Upstream's own builds (author Wael, none of
        // those marks) lack the fork's fixes (e.g. a PAK-method replacement of Outfit 1 leaves its slots without a
        // scene), so a package of one is refused with a pointer to the Manager's.
        internal static bool IsUpstreamBuild(string name, string version, string author)
        {
            bool fork = (name ?? "").Trim().StartsWith("Slotsloader", StringComparison.OrdinalIgnoreCase)
                || (author ?? "").IndexOf("ZZtaii", StringComparison.OrdinalIgnoreCase) >= 0
                || (version ?? "").IndexOf("-custom", StringComparison.OrdinalIgnoreCase) >= 0;
            return !fork && (author ?? "").IndexOf("Wael", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Package-declared compatibility for manual installs; the bundled/default loader pin is unchanged.
        internal const int KnownContract = 2;
        internal static bool HonoursContract(ModComponent c)
        {
            string v; int n;
            return c != null && c.Metadata != null && c.Metadata.TryGetValue("SlotsloaderContract", out v)
                && Int32.TryParse((v ?? "").Trim(), out n) && n >= KnownContract;
        }

        // SlotsLoader isn't installed (no proxy/plugin/loader data in the game folder) but its costume pak is.
        internal static List<PatchPak> Orphaned(string gameRoot)
        {
            if (String.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return new List<PatchPak>();
            if (LoaderPresent(gameRoot)) return new List<PatchPak>();
            return Generated(gameRoot);
        }
        // A SlotsLoader is in the game folder (complete, or at least its plugin: it still builds/uses the pak).
        internal static bool LoaderPresent(string gameRoot) { return ToolStatus.Detect(gameRoot).SlotsLoader || File.Exists(System.IO.Path.Combine(gameRoot, PluginPath.Replace('/', '\\'))); }
        internal const string OrphanText = "Slotsloader isn't installed, but its costume pak is still in the game folder, so old extra outfits still load";
    }

    // 2.4.0, technical: the per-user marker Data\show_unowned_slots.txt names (first line) a file the Manager
    // keeps in <game>\reframework\data\SF6_CostumeSlots_data after a SlotsLoader install or update and after
    // Reapply. The marker's content is the only place the name exists; it is never logged. The target is never
    // deleted and never recorded as a deployed file. A missing or invalid marker does nothing.
    internal static class UnownedSlotsFlag
    {
        internal const string MarkerName = "show_unowned_slots.txt";
        internal const string TargetFolder = @"reframework\data\SF6_CostumeSlots_data";
        private static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        // The plain file name in the marker's first line, or null (no marker, or not a plain name).
        internal static string TargetName(out bool invalid)
        {
            invalid = false;
            string marker = Path.Combine(AppConfig.DataDir, MarkerName);
            if (!File.Exists(marker)) return null;
            string name;
            using (var r = new StreamReader(marker)) name = (r.ReadLine() ?? "").Trim();
            if (!Valid(name)) { invalid = true; return null; }
            return name;
        }
        internal static bool Valid(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 64 || name.Contains("..") || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (name.Trim('.', ' ').Length == 0 || name.EndsWith(".") || name.EndsWith(" ")) return false;
            string stem = name.Split('.')[0].ToUpperInvariant();
            return !Reserved.Contains(stem);
        }

        // True when the file had to be created again.
        internal static bool Ensure(string gameRoot)
        {
            try
            {
                bool invalid; string name = TargetName(out invalid);
                if (invalid) { Program.LogLine("Owned-outfit opt-out marker ignored: its first line is not a plain file name."); return false; }
                if (name == null || String.IsNullOrEmpty(gameRoot) || !Directory.Exists(Path.Combine(gameRoot, "reframework"))) return false;
                string folder = Path.Combine(gameRoot, TargetFolder), target = Path.Combine(folder, name);
                if (File.Exists(target)) return false;
                Directory.CreateDirectory(folder);
                using (File.Create(target)) { }
                Program.LogLine("Owned-outfit opt-out marker restored.");
                return true;
            }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) { Program.LogLine("Owned-outfit opt-out marker could not be restored (" + ex.GetType().Name + ")."); return false; } throw; }
        }
    }
}
