using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 2.4.3: the real-archive checks read the developer's own folders from an untracked local file instead of
    // compiled-in paths. test.ps1 passes its location in SLOTSLOADER_TEST_CONFIG (default <project>\tests.local.json,
    // listed in .gitignore and the publish exclusions); tests.local.example.json shows the keys. Without it the real
    // checks say what is missing and fail, and nothing personal is ever part of the source or the exes.
    internal sealed class LocalTestConfig
    {
        public string Workspace = "", GameRoot = "", SevenZip = @"C:\Program Files\7-Zip\7z.exe";
        public List<string> Libraries = new List<string>(), FluffyRoots = new List<string>();
        // Named archives the real checks open: juri, ingrid, viperHair, numericOutfit, smallReplacement.
        public Dictionary<string, string> Archives = new Dictionary<string, string>();
        // replace-real fix cases: [kind, archive, entry, expected SHA-256 after the fix].
        public List<List<string>> ReplaceCases = new List<List<string>>();

        internal const string Variable = "SLOTSLOADER_TEST_CONFIG";
        private static LocalTestConfig current;
        internal static string Path { get { return Environment.GetEnvironmentVariable(Variable) ?? ""; } }
        internal static bool Available { get { return File.Exists(Path); } }
        internal static LocalTestConfig Current
        {
            get
            {
                if (current != null) return current;
                if (!Available) throw new InvalidOperationException("Real-archive checks need the local test config: copy tests.local.example.json to tests.local.json and fill in your folders (" + Variable + " = '" + Path + "').");
                current = Json.Serializer().Deserialize<LocalTestConfig>(File.ReadAllText(Path, Encoding.UTF8)) ?? new LocalTestConfig();
                return current;
            }
        }
        internal string Archive(string key)
        {
            string path;
            if (Archives == null || !Archives.TryGetValue(key, out path) || String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("tests.local.json has no archive '" + key + "'.");
            return path;
        }
    }
}
