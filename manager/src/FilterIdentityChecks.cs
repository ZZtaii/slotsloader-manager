using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    internal static partial class FilterChecks
    {
        private static void CheckIdentityAgreement(string root)
        {
            string libraryRoot = Path.Combine(root, "identity-agreement-library"); Directory.CreateDirectory(libraryRoot);
            string cache = Path.Combine(root, "identity-agreement-cache");
            string native = "Pack/natives/stm/product/model/esf/esf009/001/01/esf027_001_01.mesh.230110883";
            string art1 = "Pack/natives/stm/product/gui/data/character_image/001/pub/tex_001_cfn_iam.tex.241101895";
            string art9 = "Pack/natives/stm/product/gui/data/character_image/009/battlehud/tex_battlehud_009_1p_iam.tex.143230113";
            string colours = "Pack/reframework/data/SF6ColorSync/Juri/c1_colors.json";
            var expected = new Dictionary<string, string[]> {
                { "Native only", new[] { "009" } },
                { "Palette library", new[] { "016" } },
                { "Single portrait", new[] { "001" } },
                { "Portrait collection", new string[0] },
                { "Native and palette", new[] { "009", "016" } },
                { "Native and portrait", new[] { "001", "009" } },
                { "Native and portrait collection", new[] { "009" } },
                { "Palette and portrait collection", new[] { "016" } },
                { "Palette and helper", new[] { "016" } }
            };
            Zip(Path.Combine(libraryRoot, "Native only.zip"), native);
            Zip(Path.Combine(libraryRoot, "Palette library.zip"), colours);
            Zip(Path.Combine(libraryRoot, "Single portrait.zip"), art1);
            Zip(Path.Combine(libraryRoot, "Portrait collection.zip"), art1, art9);
            Zip(Path.Combine(libraryRoot, "Native and palette.zip"), native, colours);
            Zip(Path.Combine(libraryRoot, "Native and portrait.zip"), native, art1);
            Zip(Path.Combine(libraryRoot, "Native and portrait collection.zip"), native, art1, art9);
            Zip(Path.Combine(libraryRoot, "Palette and portrait collection.zip"), colours, art1, art9);
            Zip(Path.Combine(libraryRoot, "Palette and helper.zip"), colours, "Pack/reframework/autorun/helper.lua");

            string previous = Library.IdentityCacheDirectory;
            List<LibraryEntry> listed = null;
            try
            {
                Library.IdentityCacheDirectory = cache;
                for (int pass = 0; pass < 2; pass++)
                {
                    int misses = Library.CacheMisses;
                    listed = Library.Scan(new[] { libraryRoot }, "");
                    Require(Library.CacheMisses - misses == (pass == 0 ? expected.Count : 0), "identity scan did not reuse its cache on pass " + pass);
                    Require(listed.Count == expected.Count, "identity source count");
                    foreach (LibraryEntry entry in listed)
                    {
                        string[] wanted = expected[entry.Name];
                        Require(entry.CharacterCodes.SequenceEqual(wanted), entry.Name + " listed codes: " + String.Join(",", entry.CharacterCodes));
                        ModPackage package = PackageReader.Load(entry.SourcePath, "");
                        Require(package.CharacterCodes.SequenceEqual(wanted), entry.Name + " opened codes: " + String.Join(",", package.CharacterCodes));
                        string character = wanted.Length == 1 ? Characters.ByCode[wanted[0]] : "";
                        Require(package.Character == character && entry.Character == character, entry.Name + " changed its detected character on open");
                        Require(ModCategories.IsCharacter(entry) == (wanted.Length > 0), entry.Name + " character category");
                        Require(ModCategories.IsMisc(entry) == (entry.Name == "Portrait collection"), entry.Name + " Misc category changed");
                        Require(ModCategories.IsTool(entry) == (entry.Name == "Palette and helper"), entry.Name + " Code tools category changed");
                    }
                    // Exercise the persisted cache, not just records already present in memory.
                    Library.IdentityCacheDirectory = cache;
                }
            }
            finally { Library.IdentityCacheDirectory = previous; }

            // The real LoadPackage completion overwrites the library row's identity. Keep that UI boundary
            // under test so a future separate assignment cannot undo the shared path rules.
            using (Window window = Open(root, null, new Size(1232, 640), listed))
            {
                PaddedTextField search = Get<PaddedTextField>(window.Form, "search");
                search.Text = "is:character"; Apply(window.Form);
                foreach (LibraryEntry entry in listed)
                {
                    string category = ModCategories.Describe(entry);
                    typeof(MainForm).GetMethod("LoadPackage", PrivateInstance, null, new[] { typeof(LibraryEntry) }, null).Invoke(window.Form, new object[] { entry });
                    Pump(() => !window.Form.Working, 30, "opening identity fixture " + entry.Name);
                    Require(Get<ModPackage>(window.Form, "package").SourcePath == entry.SourcePath, "identity fixture was not opened: " + entry.Name);
                    Require(entry.CharacterCodes.SequenceEqual(expected[entry.Name]), entry.Name + " lost fighter codes after opening in the window");
                    Require(MainForm.FighterCodes(entry).SetEquals(expected[entry.Name]), entry.Name + " changed fighter-card membership after opening");
                    Require(ModCategories.Describe(entry) == category, entry.Name + " changed categories after opening");
                    Require(Listed(window.Form).Contains(entry.Name) == (expected[entry.Name].Length > 0), entry.Name + " changed is:character search membership after opening");
                }
            }
        }
    }
}
