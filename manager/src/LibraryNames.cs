using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.6 (the user): names typed for mods in the Mod library's Name view (Rename while Name is on). They are kept in
    // Data\library-names.json by source path; the archive or folder and its modinfo.ini are never changed. A file rename
    // moves the name with the mod, a delete forgets it, and an empty name (or the mod's own name) removes it.
    internal static class LibraryNames
    {
        internal const string Format = "slotsloader-library-names-v1";
        private sealed class Store { public string Format = LibraryNames.Format; public Dictionary<string, string> Names = new Dictionary<string, string>(); }
        private static readonly object Gate = new object();
        private static Dictionary<string, string> names;
        private static string loadedFrom;
        internal static string FilePath { get { return Path.Combine(AppConfig.DataDir, "library-names.json"); } }

        private static Dictionary<string, string> Names()
        {
            string file = FilePath;
            if (names != null && String.Equals(loadedFrom, file, StringComparison.OrdinalIgnoreCase)) return names;
            names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); loadedFrom = file;
            try
            {
                if (File.Exists(file))
                {
                    Store store = Json.Read<Store>(file);
                    if (store != null && store.Format == Format && store.Names != null)
                        foreach (var kv in store.Names) if (!String.IsNullOrWhiteSpace(kv.Key) && !String.IsNullOrWhiteSpace(kv.Value) && kv.Value.Length <= 300) names[kv.Key] = kv.Value.Trim();
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return names;
        }
        private static void Save()
        {
            var store = new Store();
            foreach (var kv in names.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) store.Names[kv.Key] = kv.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            Json.Write(FilePath, store);
        }

        // Every listed entry takes its typed name (or none).
        internal static void Apply(IEnumerable<LibraryEntry> entries)
        {
            lock (Gate)
            {
                Dictionary<string, string> map = Names();
                foreach (LibraryEntry e in entries) { string n; e.CustomName = e != null && !String.IsNullOrEmpty(e.SourcePath) && map.TryGetValue(e.SourcePath, out n) ? n : null; }
            }
        }
        // Sets (or, for "" or the mod's own name, removes) the typed name of a source. Returns the name now shown.
        internal static string Set(LibraryEntry entry, string name)
        {
            string clean = System.Text.RegularExpressions.Regex.Replace(name ?? "", @"\s+", " ").Trim();
            if (clean.Length > 300) throw new IOException("Use a name of 300 characters or fewer.");
            string own = !String.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName : entry.Name;
            lock (Gate)
            {
                Dictionary<string, string> map = Names();
                if (clean.Length == 0 || String.Equals(clean, own, StringComparison.Ordinal)) map.Remove(entry.SourcePath); else map[entry.SourcePath] = clean;
                Save();
                entry.CustomName = map.ContainsKey(entry.SourcePath) ? clean : null;
            }
            return entry.ShownName(false);
        }
        internal static void Move(string from, string to)
        {
            lock (Gate)
            {
                Dictionary<string, string> map = Names(); string n;
                if (String.IsNullOrEmpty(from) || String.IsNullOrEmpty(to) || !map.TryGetValue(from, out n)) return;
                map.Remove(from); map[to] = n; Save();
            }
        }
        internal static void Forget(IEnumerable<string> sources)
        {
            lock (Gate)
            {
                Dictionary<string, string> map = Names(); bool changed = false;
                foreach (string s in sources) if (!String.IsNullOrEmpty(s) && map.Remove(s)) changed = true;
                if (changed) Save();
            }
        }
    }
}
