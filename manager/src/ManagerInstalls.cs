using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.9 (the user, for other costume loaders): <game>\reframework\data\SF6_Costumes_Data\manager_installs.json lists
    // this app's Replace-original installs, so a costume loader can tell their loose files and patch PAKs from mods to
    // turn into extra outfits without searching the PC for the Data folder (replace-state.json). Format
    // "sf6-manager-installs-1", as the upstream loader's author proposed it:
    //   mods: every replacement and tool mod in priority order (first = the one whose file is in the game), each with
    //     enabled, its loose "files" and its patch "paks" (path relative to the game folder with "/", sha256, size);
    //     files left to another tool with "Keep current file" (Released) are not listed;
    //   generated, manager, id, installed_utc: only for logs and support.
    // A loader matches files by size, so the lower mods of a shared path claim nothing. Written whole (temporary file
    // in the same folder, then replaced), UTF-8 without BOM, after every committed change (ReplaceEngine.Execute) and
    // at start-up and every refresh (MainForm); "mods" is empty when nothing is installed. Only written where a
    // costume loader keeps its data (the folder exists), and only when something besides "generated" changed. Never
    // fails a change: a problem is logged. The JSON is written by hand in a fixed layout because loaders read it with
    // a small parser: keys in this order, only `"` `\` and control characters escaped.
    internal static class ManagerInstalls
    {
        internal const string Format = "sf6-manager-installs-1", FileName = "manager_installs.json";
        private static readonly Regex PatchPak = new Regex(@"^re_chunk_000\.pak\.patch_\d+\.pak$", RegexOptions.IgnoreCase);
        private static readonly Regex GeneratedLine = new Regex("^  \"generated\": \"[^\"]*\",\\r?\\n", RegexOptions.Multiline);
        internal static string Folder(string gameRoot) { return Path.Combine(gameRoot, @"reframework\data\SF6_Costumes_Data"); }
        internal static string FilePath(string gameRoot) { return Path.Combine(Folder(gameRoot), FileName); }

        // Writes the list for `state`; true when the file changed. Never throws (but out of memory).
        internal static bool Write(string gameRoot, ReplaceState state)
        {
            try
            {
                if (String.IsNullOrEmpty(gameRoot) || state == null || !Directory.Exists(Folder(gameRoot))) return false;
                string path = FilePath(gameRoot), text = Text(state, DateTime.UtcNow);
                if (File.Exists(path) && SameContent(File.ReadAllText(path, Encoding.UTF8), text)) return false;
                DurableFile.Write(path, new UTF8Encoding(false).GetBytes(text));
                return true;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.LogLine("manager_installs.json not written (" + ex.GetType().Name + ": " + ex.Message + ")."); return false; }
        }
        // Start-up and every refresh: the list follows the state whatever changed it (an interrupted change recovered,
        // an older Manager, a game folder the loader was installed into since).
        internal static bool Sync(ReplaceEngine engine)
        {
            try { return engine != null && Write(engine.GameRoot, engine.LoadState()); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.LogLine("manager_installs.json not synced (" + ex.GetType().Name + ")."); return false; }
        }
        private static bool SameContent(string a, string b) { return GeneratedLine.Replace(a, "") == GeneratedLine.Replace(b, ""); }

        internal static string Text(ReplaceState state, DateTime generatedUtc)
        {
            var released = new HashSet<string>((state.Released ?? new List<ReleasedFile>()).Select(r => Normal(r.Path)), StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"format\": ").Append(Quote(Format)).Append(",\n");
            sb.Append("  \"generated\": ").Append(Quote(generatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"manager\": ").Append(Quote("Slotsloader Manager " + UpdateCheck.ShortVersion(SafeHttp.AppVersion()))).Append(",\n");
            List<ReplaceMod> mods = state.Mods ?? new List<ReplaceMod>();
            sb.Append("  \"mods\": [").Append(mods.Count == 0 ? "]\n" : "\n");
            for (int i = 0; i < mods.Count; i++)
            {
                ReplaceMod m = mods[i];
                List<ModFile> listed = (m.Files ?? new List<ModFile>()).Where(f => f != null && !String.IsNullOrEmpty(f.Path) && !released.Contains(Normal(f.Path))).ToList();
                sb.Append("    {\n");
                sb.Append("      \"id\": ").Append(Quote(m.Id)).Append(",\n");
                sb.Append("      \"name\": ").Append(Quote(m.Name)).Append(",\n");
                sb.Append("      \"installed_utc\": ").Append(Quote(Utc(m.InstalledUtc))).Append(",\n");
                sb.Append("      \"enabled\": ").Append(m.Enabled ? "true" : "false").Append(",\n");
                AppendFiles(sb, "files", listed.Where(f => !PatchPak.IsMatch(Normal(f.Path))).ToList(), true);
                AppendFiles(sb, "paks", listed.Where(f => PatchPak.IsMatch(Normal(f.Path))).ToList(), false);
                sb.Append("    }").Append(i < mods.Count - 1 ? ",\n" : "\n");
            }
            if (mods.Count > 0) sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }
        private static void AppendFiles(StringBuilder sb, string key, List<ModFile> files, bool more)
        {
            sb.Append("      \"").Append(key).Append("\": [").Append(files.Count == 0 ? "]" : "\n");
            for (int i = 0; i < files.Count; i++)
                sb.Append("        { \"path\": ").Append(Quote(Normal(files[i].Path))).Append(", \"sha256\": ").Append(Quote((files[i].Sha256 ?? "").ToLowerInvariant()))
                  .Append(", \"size\": ").Append(files[i].Size.ToString(CultureInfo.InvariantCulture)).Append(" }").Append(i < files.Count - 1 ? ",\n" : "\n");
            if (files.Count > 0) sb.Append("      ]");
            sb.Append(more ? ",\n" : "\n");
        }
        private static string Normal(string path) { return (path ?? "").Replace('\\', '/').TrimStart('/'); }
        private static string Utc(string iso)
        {
            DateTime when;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when) ? when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "";
        }
        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
