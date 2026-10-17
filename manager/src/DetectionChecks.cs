using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 2.4.3: first-start detection on generated folders only (never the real registry or drives).
    internal static class DetectionChecks
    {
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

        internal static void Run(string root)
        {
            string steam = Path.Combine(root, "Steam"), lib = Path.Combine(root, "Library Two");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common", "SF6 Custom Dir"));
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib.Replace("\\", "\\\\") + "\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"1364780\"\t\t\"123\"\n\t\t}\n\t}\n}\n");
            File.WriteAllText(Path.Combine(lib, "steamapps", "appmanifest_1364780.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1364780\"\n\t\"installdir\"\t\t\"SF6 Custom Dir\"\n}\n");
            // The app manifest's installdir wins; the library comes from libraryfolders.vdf.
            string game = InstallDetection.FindGame(new[] { steam });
            Require(String.Equals(game, Path.Combine(lib, "steamapps", "common", "SF6 Custom Dir"), StringComparison.OrdinalIgnoreCase), "manifest library: " + game);
            // Without a manifest: steamapps\common\Street Fighter 6 in any known library.
            File.Delete(Path.Combine(lib, "steamapps", "appmanifest_1364780.acf"));
            Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common", "Street Fighter 6"));
            game = InstallDetection.FindGame(new[] { steam });
            Require(String.Equals(game, Path.Combine(lib, "steamapps", "common", "Street Fighter 6"), StringComparison.OrdinalIgnoreCase), "default folder: " + game);
            // Nothing found: null (the app leaves the game folder empty and says where to choose it).
            Require(InstallDetection.FindGame(new[] { Path.Combine(root, "no steam"), "" }) == null, "a missing Steam must find nothing");
            // Both library entries of the vdf are read.
            Require(InstallDetection.LibraryFolders(Path.Combine(steam, "steamapps", "libraryfolders.vdf")).Count == 2, "vdf libraries");
            // Fluffy: the Fluffy folder, its Games\SF6 or the Mods folder itself; only real <Fluffy>\Games\SF6\Mods count.
            string fluffyA = Path.Combine(root, "Fluffy"), fluffyB = Path.Combine(root, "Games", "Fluffy - Copy");
            Directory.CreateDirectory(Path.Combine(fluffyA, "Games", "SF6", "Mods")); Directory.CreateDirectory(Path.Combine(fluffyB, "Games", "SF6", "Mods"));
            Directory.CreateDirectory(Path.Combine(root, "Not Fluffy", "Mods"));
            List<string> libraries = InstallDetection.FindLibraries(new[] { fluffyA, Path.Combine(fluffyB, "Games", "SF6"), Path.Combine(fluffyA, "Games", "SF6", "Mods"), Path.Combine(root, "Not Fluffy"), Path.Combine(root, "missing") });
            Require(libraries.Count == 2 && libraries.All(l => l.EndsWith(@"Games\SF6\Mods", StringComparison.OrdinalIgnoreCase)), "Fluffy libraries: " + String.Join(", ", libraries));
            // No settings.json: Detected() is used; checks (SLOTSLOADER_NO_DETECT=1 from test.ps1) get empty folders.
            if (AppConfig.DetectionOff) { AppConfig none = AppConfig.Detected(); Require(none.GameRoot == "" && none.Libraries.Count == 0 && none.CostumeRoot == "", "detection must be off in checks"); }
            // Compiled defaults are empty: nothing personal is in the exe.
            var blank = new AppConfig();
            Require(blank.GameRoot == "" && blank.Libraries.Count == 0 && blank.CostumeRoot == "", "AppConfig defaults must be empty");
        }
    }
}
