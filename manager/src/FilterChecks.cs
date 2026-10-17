using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.3.0 --filter-check <fixture root> (test.ps1 suite "filters"): block decoders against bcdec reference
    // blocks, the .tex reader, portrait candidates/resolution/cache on generated PAKs, the stage-name message
    // reader, library categories and index v4, and the Filters panel in hidden windows (Opacity 0).
    internal static partial class FilterChecks
    {
        // The band is drawn over a white surface: its first row must be the lightest and every row at most as light
        // as the one above (the 2.3.0 brush painted row 0 in the end colour, a dark line across each portrait).
        private static void CheckNameGuess(string root)
        {
            string lib = Path.Combine(root, "name-guess-lib"); Directory.CreateDirectory(lib);
            Action<string, string[]> zip = delegate(string name, string[] entries)
            {
                using (var archive = ZipFile.Open(Path.Combine(lib, name + ".zip"), ZipArchiveMode.Create))
                    foreach (string e in entries) using (var w = new StreamWriter(archive.CreateEntry(e).Open())) w.Write("x " + e);
            };
            zip("Back Spotlight Adjuster - Alex Patch", new[] { "Spot/natives/stm/product/light/scene/ess/ess0000_00/ess0000_00_Light_00.scn.20", "Spot/natives/stm/product/light/scene/ess/ess0100_00/ess0100_00_Light_00.scn.20" });
            zip("Cammy Helper Script", new[] { "reframework/autorun/cammy_helper.lua" });
            zip("Alex Gloves", new[] { "Gloves/natives/stm/product/model/esf/esf031/001/01/esf031_001_01.mesh.2109108288" });
            zip("Ryu Barefoot Pack", new[] { "Ryu/natives/stm/product/model/esf/esf001/002/01/esf001_002_01.mesh.2109108288" });
            string previous = Library.IdentityCacheDirectory; Library.IdentityCacheDirectory = Path.Combine(root, "name-guess-index");
            List<LibraryEntry> library;
            try { library = Library.Scan(new[] { lib }, ""); }
            finally { Library.IdentityCacheDirectory = previous; }
            Func<string, LibraryEntry> entry = delegate(string name) { return library.Single(e => e.Name == name); };
            LibraryEntry spot = entry("Back Spotlight Adjuster - Alex Patch"), tool = entry("Cammy Helper Script"), gloves = entry("Alex Gloves"), ryu = entry("Ryu Barefoot Pack");
            Require(ModCategories.IsStage(spot) && spot.FilenameCharacter == "Alex" && spot.Character == "" && spot.Detection == "Unknown" && MainForm.FighterCodes(spot).Count == 0, "stage mod: " + spot.Character + " / " + spot.Detection + " / " + String.Join(",", MainForm.FighterCodes(spot)));
            Require(ModCategories.IsTool(tool) && tool.FilenameCharacter == "Cammy" && tool.Character == "" && MainForm.FighterCodes(tool).Count == 0, "tool mod: " + tool.Character);
            Require(gloves.Character == "Alex" && gloves.Detection == "Native" && MainForm.FighterCodes(gloves).SetEquals(new[] { "031" }) && ryu.Character == "Ryu", "fighter mods: " + gloves.Character + " / " + ryu.Character);
            Require(MainForm.LibraryCharacterLabel(spot) == "No character detected", "row: " + MainForm.LibraryCharacterLabel(spot));
            Func<string, string> find = delegate(string text)
            {
                LibrarySearch.Query q = LibrarySearch.Parse(text, MainForm.SearchCharacterTerm); var codes = LibrarySearch.CodesFor(q.Characters);
                return String.Join(", ", library.Where(e => LibrarySearch.MatchesText(e, q, MainForm.EntryMatchesCharacter, MainForm.EntryMatchesName) && LibrarySearch.CostumeRank(e, q, codes, MainForm.EntryMatchesCharacter) >= 0).Select(e => e.Name).OrderBy(n => n));
            };
            Require(find("alex") == "Alex Gloves, Back Spotlight Adjuster - Alex Patch", "plain alex: " + find("alex"));
            Require(find("alex, patch") == "", "the fighter term still matches the stage mod: " + find("alex, patch"));
            Require(find("alex, gloves") == "Alex Gloves" && find("spotlight, patch") == "Back Spotlight Adjuster - Alex Patch" && find("cammy") == "Cammy Helper Script" && find("cammy, script") == "", "other searches: " + find("alex, gloves") + " / " + find("spotlight, patch") + " / " + find("cammy") + " / " + find("cammy, script"));
            // Opening the mod in the window must not bring the file-name fighter back.
            using (Window w = Open(root, null, new Size(1232, 640), library))
            {
                typeof(MainForm).GetMethod("LoadPackage", PrivateInstance, null, new[] { typeof(LibraryEntry) }, null).Invoke(w.Form, new object[] { spot });
                Pump(() => !w.Form.Working, 30, "opening the stage mod");
                Require(spot.Character == "" && spot.Detection == "Unknown" && MainForm.FighterCodes(spot).Count == 0, "after opening: " + spot.Character + " / " + spot.Detection);
            }
        }

        private static void CheckBundleName(string root)
        {
            Func<string, bool, string> make = delegate(string name, bool bundle)
            {
                string zip = Path.Combine(root, name + ".zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    foreach (var option in new[] { new[] { "Back 125", "1. 125%", "0000" }, new[] { "Back 75", "2. 75%", "0100" } })
                    {
                        using (var w = new StreamWriter(archive.CreateEntry("Spot/" + option[0] + "/modinfo.ini").Open())) w.Write("name=" + option[1] + "\r\ncategory=!Other > Stages\r\n" + (bundle ? "NameAsBundle=Spot Adjuster\r\n" : ""));
                        using (var w = new StreamWriter(archive.CreateEntry("Spot/" + option[0] + "/natives/stm/product/light/scene/ess/ess" + option[2] + "_00/ess" + option[2] + "_00_Light_00.scn.20").Open())) w.Write("light " + option[1]);
                    }
                return zip;
            };
            ModPackage named = PackageReader.Load(make("spot-bundle", true), "");
            Require(named.Name == "Spot Adjuster" && named.Components.Count == 2, "NameAsBundle: " + named.Name);
            ModPackage plain = PackageReader.Load(make("spot-plain", false), "");
            Require(plain.Name == "1. 125%", "without NameAsBundle: " + plain.Name);
        }
        private static void CheckBandGradient()
        {
            foreach (int height in new[] { 28, 84, 66 })
                using (var b = new Bitmap(40, height + 10))
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.White);
                    var band = new Rectangle(0, 5, 40, height);
                    using (Brush shade = FighterGrid.VerticalGradient(band, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(215, 8, 9, 12))) g.FillRectangle(shade, band);
                    int previous = 255;
                    for (int y = band.Top; y < band.Bottom; y++)
                    {
                        int v = b.GetPixel(20, y).R;
                        if (v > previous + 1 || (y == band.Top && v < 240)) throw new InvalidOperationException("gradient seam at row " + (y - band.Top) + " of " + height + ": " + v + " after " + previous);
                        previous = v;
                    }
                }
        }
        private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static int Run(string root)
        {
            root = Path.GetFullPath(root); Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action body)
            {
                var clock = Stopwatch.StartNew();
                try { body(); report.Add("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)"); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex.Message); Program.Log(ex); }
            };
            Fixture game = null;
            check("BC1/BC3/BC7 blocks decode exactly like bcdec v0.985 (" + Vectors.Length + " reference blocks incl. punch-through, both BC3 alpha modes, every BC7 mode and the reserved mode)", CheckVectors);
            check("BC surfaces: edge blocks clipped, row pitch honoured, data outside the file refused", CheckSurfaces);
            check("RE .tex header and mip table parsing; bad magic, empty size, mip table or level outside the file, unsupported format refused", CheckTex);
            check("portrait candidates: face first (030+ 'characterselect' spelling first, older fighters 'caracterselect'), then cfn, 1k, card, new_challenger, battlehud; current suffix then 143230113", CheckCandidates);
            check("portraits from generated PAKs: patch over DLC over base, zeroed rows skipped, deflate entries, broken texture falls back, missing fighter = name tile, new fighter found, stage names", delegate { game = CheckResolution(root); });
            check("portrait cache keyed by PAK identity: hit reads only PNGs, stale after a PAK change, miss after a lost PNG or bad manifest, nothing cached when the game changes during the read, no game folder = name tiles", delegate { CheckCache(root, game); });
            check("5:7 tile cropped around the alpha centroid", CheckCrop);
            check("GMSG stage-name reader: decryption, entry names, English text, stage keys; other files refused", CheckMessages);
            check("categories from the listing pass (costume, fighter VFX/colours/art, costume+UI, fighter on a stage, stage = Misc kind, tool, UI, sound, avatar, shared files, PAK-only, readme-only, friendly labels; Misc only when nothing else fits) and index v4 reuse/upgrade", delegate { CheckCategories(root); });
            check("fighter identity agrees between fresh/cached library scans and opened packages: native paths, colour data, single/multi-fighter art and mixed sources; opening preserves the character filter and category", delegate { CheckIdentityAgreement(root); });
            check("category search terms (stage, misc, is:stage, is:misc, is:character) and row labels", CheckSearchTerms);
            check("name band gradient (2.3.1): no dark seam at its top row (the line across every portrait); luminance falls monotonically", CheckBandGradient);
            check("no file-name fighter for Misc / Code tools mods (2.3.1): row reads No character detected, no fighter card count, plain name search still finds them, the fighter term does not; mods with fighter files and other mods keep their fighter; also after the mod is opened", delegate { CheckNameGuess(root); });
            check("package name (2.3.1): sibling options that name their bundle (NameAsBundle) are that bundle, not their first option; without it the first option still names the package", delegate { CheckBundleName(root); });
            check("Added time (2.4.1): creation time, or the write time when the creation time is missing or later (temp file created after its write time); folders too; stored in index v4, and a record without it is filled in without relisting", delegate { CheckAddedTime(root); });
            SynchronizationContext previous = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            using (var ui = new WindowsFormsSynchronizationContext())
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                try
                {
                    foreach (Size size in new[] { new Size(1232, 640), new Size(1460, 920), new Size(1110, 610) })
                    {
                        Size s = size;
                        check("Filters panel at " + s.Width + "x" + s.Height + " logical: one screen beside the list (list stays): Clear in the FIGHTERS line, grid, face-sized Code tools/Misc/Installed/library tiles with the library gap, Misc chips, no dropdowns or checkboxes left, no overlap with the inspector or footer, no scrolling at 1460x920 with Misc chips, closes back to the options", delegate { report.Add("  " + CheckLayout(root, game, s)); });
                    }
                    check("filter combinations in the window: fighters (multi), kind tiles narrowing fighters, Misc chips, the Installed tile (AND, never deselects), search tokens of the removed dropdowns (c1, is:bundle, is:normal, is:unreadable, format:), Clear, card and tile counts", delegate { CheckCombinations(root, game); });
                    check("library tiles: hidden with one library (saved selection ignored), OR-ed with each other and AND-ed with fighters and kinds, counts, tooltips, saved, 2.2.0 dropdown migrated only while configured, dropped when removed, duplicate folder names", delegate { CheckLibraries(root, game); });
                    check("fighter grid and kind tiles: keyboard (arrows and W/A/S/D, also on the Misc chips but not in the search box; Home/End, Space), click toggles, Shift+click range, accessible names and states, dimmed cards without mods, placeholder art gray/colour", delegate { CheckGrid(root, game); });
                    check("portraits arrive asynchronously: name tiles first, synthetic cached PNGs then extraction from generated PAKs, discovered fighter added", delegate { CheckAsync(root, game); });
                    check("a mod picked in the list (mouse click, Down, Enter, top search match) closes the panel and shows its options; filter/search/programmatic selections keep it open; reopening keeps the selections", delegate { CheckPickClosesPanel(root, game); });
                    check("view settings keep the panel open/closed, fighters, kinds, Misc chips and Installed; values of the removed dropdowns are neither written nor applied; a 2.2.0 Character and a test-build tab are migrated", delegate { CheckViewSettings(root, game); });
                    check("sort chips (2.4.1): default Installed ↓; Added, Last used and Installed in both directions (a new sort starts ↓, a second click flips it); ties by name; never used last/first; the latest record wins; search keeps relevance under Installed only; Enter opens the top row; filters, Clear and the selection survive; keyboard, accessible names, no tooltips; one 26 px row with Refresh at its end (also in the narrowest column); saved and restored, old files = Installed ↓", delegate { CheckSorts(root, game, report); });
                    check("sort timing (2.4.1): 2,667 synthetic mods, every sort both ways, no record read during a sort", delegate { report.Add("  " + CheckSortTiming(root)); });
                    check("Refresh in the sort row (2.4.1): F5's refresh (same scan and status), disabled while it runs, filters, sort and selection kept", delegate { CheckSortRefresh(root); });
                    check("sort strip screenshots (2.4.1)", delegate { report.Add("  " + CheckSortScreens(root)); });
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall; }
            }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "filter-check.txt"), report);
            Directory.CreateDirectory(AppConfig.DataDir);
            File.WriteAllLines(Path.Combine(AppConfig.DataDir, "filter-check.txt"), report);
            return failures == 0 ? 0 : 1;
        }

        // ---- Decoders ------------------------------------------------------------------------------------
        private static byte[] Hex(string s) { var b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16); return b; }

        private static void CheckVectors()
        {
            int checkedBlocks = 0;
            foreach (string line in Vectors)
            {
                string[] f = line.Split(' ');
                int fmt = f[0] == "BC1" ? 1 : f[0] == "BC3" ? 3 : 7;
                byte[] block = Hex(f[1]), rgba = Hex(f[2]), bgra = new byte[64];
                BcDecoder.DecodeBlock(fmt, block, 0, bgra);
                for (int p = 0; p < 16; p++)
                {
                    int r = bgra[p * 4 + 2], g = bgra[p * 4 + 1], b = bgra[p * 4], a = bgra[p * 4 + 3];
                    Require(r == rgba[p * 4] && g == rgba[p * 4 + 1] && b == rgba[p * 4 + 2] && a == rgba[p * 4 + 3], f[0] + " block " + f[1] + " pixel " + p + ": got " + r + "," + g + "," + b + "," + a + " expected " + rgba[p * 4] + "," + rgba[p * 4 + 1] + "," + rgba[p * 4 + 2] + "," + rgba[p * 4 + 3]);
                }
                checkedBlocks++;
            }
            Require(checkedBlocks == Vectors.Length && Vectors.Count(v => v.StartsWith("BC7")) >= 40, "vector set incomplete");
        }

        private static void CheckSurfaces()
        {
            // Four different BC1 blocks in a 2x2 grid; a 6x6 surface clips the right/bottom blocks.
            string[] blocks = Vectors.Where(v => v.StartsWith("BC1")).Take(4).Select(v => v.Split(' ')[1]).ToArray();
            byte[] src = Hex(String.Concat(blocks));
            byte[] surface = BcDecoder.DecodeSurface(1, src, 0, 6, 6, 0);
            Require(surface.Length == 6 * 6 * 4, "surface size");
            var one = new byte[64];
            for (int b = 0; b < 4; b++)
            {
                BcDecoder.DecodeBlock(1, Hex(blocks[b]), 0, one);
                int bx = b % 2, by = b / 2;
                for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
                {
                    int px = bx * 4 + x, py = by * 4 + y; if (px >= 6 || py >= 6) continue;
                    for (int c = 0; c < 4; c++) Require(surface[(py * 6 + px) * 4 + c] == one[(y * 4 + x) * 4 + c], "surface pixel " + px + "," + py);
                }
            }
            // Row pitch with 8 bytes of padding per block row and a leading offset.
            var padded = new byte[4 + 2 * 24];
            Buffer.BlockCopy(src, 0, padded, 4, 16); Buffer.BlockCopy(src, 16, padded, 4 + 24, 16);
            byte[] pitched = BcDecoder.DecodeSurface(1, padded, 4, 6, 6, 24);
            Require(pitched.SequenceEqual(surface), "row pitch not honoured");
            bool refused = false;
            try { BcDecoder.DecodeSurface(1, src, 8, 8, 8, 0); } catch (InvalidDataException) { refused = true; }
            Require(refused, "surface data outside the buffer was read");
            refused = false;
            try { BcDecoder.DecodeSurface(1, padded, 0, 8, 8, 8); } catch (InvalidDataException) { refused = true; }
            Require(refused, "a pitch shorter than a block row was accepted");
        }

        // A BC1 texture: right half of each level opaque `color` (RGB565), left half transparent (punch-through).
        internal static byte[] Tex(int w, int h, int levels, ushort color565, string version)
        {
            var data = new MemoryStream(); var table = new List<long[]>();
            int header = ReTex.HeaderBytes + 16 * levels; long offset = header;
            var payload = new MemoryStream();
            for (int l = 0; l < levels; l++)
            {
                int lw = Math.Max(1, w >> l), lh = Math.Max(1, h >> l), bw = (lw + 3) / 4, bh = (lh + 3) / 4;
                for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++)
                {
                    bool opaque = bx >= bw / 2;
                    byte[] block = opaque
                        ? new byte[] { (byte)color565, (byte)(color565 >> 8), (byte)color565, (byte)(color565 >> 8), 0, 0, 0, 0 }
                        : new byte[] { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };   // c0 == c1: index 3 = transparent black
                    payload.Write(block, 0, 8);
                }
                table.Add(new long[] { offset, bw * 8, bw * bh * 8 });
                offset += bw * bh * 8;
            }
            var w8 = new BinaryWriter(data);
            w8.Write(new byte[] { (byte)'T', (byte)'E', (byte)'X', 0 }); w8.Write(version == "143230113" ? 143230113 : 241101895);
            w8.Write((ushort)w); w8.Write((ushort)h); w8.Write((ushort)1); w8.Write((byte)1); w8.Write((byte)(16 * levels));
            w8.Write(71); w8.Write(-1); w8.Write(0); w8.Write((ushort)0); w8.Write((ushort)0); w8.Write(new byte[8]);
            foreach (long[] t in table) { w8.Write(t[0]); w8.Write((int)t[1]); w8.Write((int)t[2]); }
            w8.Write(payload.ToArray()); w8.Flush();
            return data.ToArray();
        }

        private static void CheckTex()
        {
            byte[] good = Tex(64, 32, 2, 0xF800, "241101895");
            ReTex t = ReTex.Parse(good);
            Require(t.Width == 64 && t.Height == 32 && t.MipCount == 2 && t.Format == 71 && t.FormatName == "BC1_UNORM" && t.MipOffset[0] == 72 && t.MipPitch[0] == 128 && t.MipSize[1] == 4 * 8 * 8, "header/mip table: " + t.Width + "x" + t.Height + " mips " + t.MipCount + " off " + t.MipOffset[0]);
            Require(t.PickLevel(32, 16) == 1 && t.PickLevel(64, 32) == 0 && t.PickLevel(128, 128) == 0, "level choice");
            byte[] level1 = t.DecodeLevel(1);
            Require(level1.Length == 32 * 16 * 4 && level1[(5 * 32 + 30) * 4 + 3] == 255 && level1[(5 * 32 + 2) * 4 + 3] == 0 && level1[(5 * 32 + 30) * 4 + 2] == 255, "level 1 pixels");
            Func<byte[], Type> fails = delegate(byte[] bytes) { try { ReTex.Parse(bytes).DecodeLevel(0); return null; } catch (Exception ex) { return ex.GetType(); } };
            byte[] bad = (byte[])good.Clone(); bad[0] = (byte)'X';
            Require(fails(bad) == typeof(InvalidDataException), "bad magic accepted");
            bad = (byte[])good.Clone(); bad[8] = 0; bad[9] = 0;
            Require(fails(bad) == typeof(InvalidDataException), "zero width accepted");
            bad = (byte[])good.Clone(); bad[15] = 0;
            Require(fails(bad) == typeof(InvalidDataException), "empty mip table accepted");
            bad = (byte[])good.Clone(); bad[15] = 0xF0;
            Require(fails(bad) == typeof(InvalidDataException), "mip table beyond the file accepted");
            bad = (byte[])good.Clone(); Buffer.BlockCopy(BitConverter.GetBytes((long)good.Length - 8), 0, bad, ReTex.HeaderBytes, 8);
            Require(fails(bad) == typeof(InvalidDataException), "level outside the file accepted");
            bad = good.Take(good.Length - 100).ToArray();
            Require(fails(bad) == typeof(InvalidDataException), "truncated file accepted");
            bad = (byte[])good.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(95), 0, bad, 16, 4);
            Require(!ReTex.Supported(95) && fails(bad) == typeof(NotSupportedException) && ReTex.Supported(99) && ReTex.Supported(72), "BC6H must be unsupported, BC7/BC1 sRGB supported");
            Require(fails(new byte[] { 1, 2, 3 }) == typeof(InvalidDataException) && fails(null) == typeof(InvalidDataException), "short input accepted");
        }

        private static void CheckCandidates()
        {
            var versions = new List<string> { "241101895", "143230113" };
            var old = FighterPortraits.Candidates("009", versions);
            Require(old.Count == FighterPortraits.Templates.Length * 2, "candidate count");
            Require(old[0].Value == "natives/stm/product/gui/data/character_image/009/caracterselect/tex_esf009_face_l_l_iam.tex.241101895" && old[1].Value.EndsWith("caracterselect/tex_esf009_face_l_l_iam.tex.143230113") && old[2].Value.Contains("/009/characterselect/"), "009 order: " + old[0].Value + " / " + old[2].Value);
            var newer = FighterPortraits.Candidates("030", versions);
            Require(newer[0].Value.Contains("/030/characterselect/tex_esf030_face_l_l_iam.tex.241101895") && newer[2].Value.Contains("/030/caracterselect/"), "030 must try 'characterselect' first");
            Require(String.Join(",", old.Select(c => c.Key).Distinct()) == "face,cfn,1k,card,new_challenger,battlehud", "kind order " + String.Join(",", old.Select(c => c.Key).Distinct()));
            Require(old.Any(c => c.Value == "natives/stm/product/gui/data/character_image/009/pub/tex_009_cfn_iam.tex.241101895") && old.Any(c => c.Value == "natives/stm/product/gui/data/character_image/009/new_challenger/tex_nextchallenger_esf009_00_00_iam.tex.143230113") && old.Last().Value == "natives/stm/product/gui/data/character_image/009/battlehud/tex_battlehud_009_1p_iam.tex.143230113", "paths");
            List<string> fromGame = FighterPortraits.TexVersions(Path.Combine(Path.GetTempPath(), "no-such-game-" + Guid.NewGuid().ToString("N")));
            Require(fromGame.SequenceEqual(new[] { GameTables.Suffixes["tex"], "143230113" }), "suffixes without a game: " + String.Join(",", fromGame));
            Require(FighterPortraits.DisplayName("034") == "Arjun" && FighterPortraits.DisplayName("041") == "esf041", "names");
        }

        // ---- Generated game ------------------------------------------------------------------------------
        internal sealed class Fixture
        {
            internal string Game, Root;
            internal List<string> Known = new List<string> { "001", "002", "003", "004", "005", "006", "007", "008", "009", "030" };
        }
        internal sealed class PakFile { internal string Path; internal byte[] Data; internal bool Deflate, Zeroed; internal PakFile(string p, byte[] d) { Path = p; Data = d; } }

        internal static void WritePak(string file, IList<PakFile> files, uint fingerprint)
        {
            int n = files.Count; long offset = PakIndex.HeaderSize + (long)n * PakIndex.EntrySize;
            var stored = files.Select(f => f.Deflate ? Deflate(f.Data) : f.Data).ToList();
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(new byte[] { (byte)'K', (byte)'P', (byte)'K', (byte)'A', 4, 0 }); w.Write((ushort)0); w.Write((uint)n); w.Write(fingerprint);
                for (int i = 0; i < n; i++)
                {
                    ulong h = files[i].Zeroed ? 0 : PakHash.PathHash(files[i].Path);
                    w.Write(PakHash.Lower(h)); w.Write(PakHash.Upper(h)); w.Write(offset); w.Write((long)stored[i].Length); w.Write((long)files[i].Data.Length); w.Write(files[i].Deflate ? 1L : 0L); w.Write(0L);
                    offset += stored[i].Length;
                }
                foreach (byte[] s in stored) w.Write(s);
            }
        }
        private static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionMode.Compress, true)) z.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }
        private static string Face(string id, bool newSpelling, string version) { return FighterPortraits.Root + id + (newSpelling ? "/characterselect/" : "/caracterselect/") + "tex_esf" + id + "_face_l_l_iam.tex." + version; }
        private static string Art(string id, string kind) { return FighterPortraits.Root + id + "/pub/tex_" + id + "_" + kind + "_iam.tex.241101895"; }
        internal const ushort Red = 0xF800, Green = 0x07E0, Blue = 0x001F, White = 0xFFFF;

        // Stage-name message (GMSG layout of .msg.21): entries StageNameMessage0 / 41000 / 80000.
        internal static byte[] Gmsg(IList<KeyValuePair<string, string>> entries)
        {
            const int languages = 3; int count = entries.Count;
            long entryTable = 0x48 + 8L * count; long entrySize = 40 + 8L * languages;
            long data = entryTable + entrySize * count;
            var strings = new MemoryStream(); var offsets = new List<long[]>();
            Func<string, long> add = delegate(string s) { long at = data + strings.Length; byte[] b = Encoding.Unicode.GetBytes(s + "\0"); strings.Write(b, 0, b.Length); return at; };
            foreach (var e in entries) offsets.Add(new[] { add(e.Key), add(e.Value.ToUpperInvariant()), add(e.Value), add("x") });
            var file = new MemoryStream(); var w = new BinaryWriter(file);
            w.Write(0x16); w.Write(new byte[] { (byte)'G', (byte)'M', (byte)'S', (byte)'G' }); w.Write(0x10L);
            w.Write(count); w.Write(0); w.Write(languages); w.Write(0); w.Write(data); w.Write(0L); w.Write(0L); w.Write(0L); w.Write(0L);
            for (int k = 0; k < count; k++) w.Write(entryTable + entrySize * k);
            for (int k = 0; k < count; k++) { w.Write(new byte[16]); w.Write(0); w.Write(0); w.Write(offsets[k][0]); w.Write(0L); for (int l = 0; l < languages; l++) w.Write(offsets[k][1 + l]); }
            w.Flush();
            byte[] plain = strings.ToArray(), key = { 0xCF, 0xCE, 0xFB, 0xF8, 0xEC, 0x0A, 0x33, 0x66, 0x93, 0xA9, 0x1D, 0x93, 0x50, 0x39, 0x5F, 0x09 };
            byte previous = 0;
            for (int i = 0; i < plain.Length; i++) { byte c = (byte)(plain[i] ^ previous ^ key[i & 15]); plain[i] = c; previous = c; }
            w.Write(plain); w.Flush();
            return file.ToArray();
        }

        internal static Fixture MakeGame(string root)
        {
            var f = new Fixture { Root = root, Game = Path.Combine(root, "game-" + Guid.NewGuid().ToString("N").Substring(0, 6)) };
            Directory.CreateDirectory(Path.Combine(f.Game, "dlc"));
            string v = "241101895";
            var stages = Gmsg(new[] { new KeyValuePair<string, string>("StageNameMessage0", "Fixture Room"), new KeyValuePair<string, string>("StageNameMessage41000", "Fixture Cafe"), new KeyValuePair<string, string>("StageNameMessage80000", "Fixture Hut"), new KeyValuePair<string, string>("OtherMessage", "ignored") });
            var basePak = new List<PakFile> {
                new PakFile(Face("001", false, v), Tex(64, 64, 1, Red, v)),
                new PakFile(Art("002", "1k"), Tex(64, 64, 1, Green, v)),
                new PakFile(Face("003", false, "143230113"), Tex(64, 64, 1, Blue, "143230113")),
                new PakFile(Face("005", false, v), Tex(64, 64, 1, Green, v)) { Deflate = true },
                new PakFile(Face("006", false, v), Encoding.ASCII.GetBytes("not a texture")),
                new PakFile(Art("006", "cfn"), Tex(64, 64, 1, White, v)),
                new PakFile(Face("007", false, v), Tex(64, 64, 1, Red, v)) { Zeroed = true },     // invalidated by a PAK-method mod
                new PakFile(Face("008", false, v), Tex(64, 64, 1, Red, v)) { Zeroed = true },
                new PakFile(Face("009", false, v), Tex(64, 64, 1, Red, v)), new PakFile(Face("009", true, v), Tex(64, 64, 1, Blue, v)),
                new PakFile(Face("030", false, v), Tex(64, 64, 1, Red, v)), new PakFile(Face("030", true, v), Tex(64, 64, 1, Blue, v)),
                new PakFile(Face("035", true, v), Tex(64, 64, 1, Green, v)),
                new PakFile(FighterPortraits.StageMessagePath + GameTables.Suffixes["msg"], stages),
                new PakFile("natives/stm/product/other/file.user.2", new byte[] { 1, 2, 3 }) };
            WritePak(Path.Combine(f.Game, "re_chunk_000.pak"), basePak, 0x28376913);
            WritePak(Path.Combine(f.Game, "dlc", "re_dlc_stm_1.pak"), new List<PakFile> { new PakFile(Face("001", false, v), Tex(64, 64, 1, Green, v)) }, 0x39ED4FD3);
            WritePak(Path.Combine(f.Game, "re_chunk_000.pak.patch_001.pak"), new List<PakFile> { new PakFile(Face("001", false, v), Tex(64, 64, 1, Blue, v)), new PakFile(Face("007", false, v), Tex(64, 64, 1, Green, v)), new PakFile(Face("002", false, v), Tex(64, 64, 1, Red, v)) { Zeroed = true } }, 0x11111111);
            File.WriteAllBytes(Path.Combine(f.Game, "re_chunk_000.pak.patch_002.pak"), Encoding.ASCII.GetBytes("JUNKJUNKJUNKJUNK"));   // unsupported: skipped
            return f;
        }

        private static Color Center(Bitmap b) { return b.GetPixel(b.Width * 3 / 4, b.Height / 2); }
        private static bool IsColor(Color c, ushort rgb565)
        {
            int r = ((rgb565 >> 11) & 31) * 255 / 31, g = ((rgb565 >> 5) & 63) * 255 / 63, b = (rgb565 & 31) * 255 / 31;
            return c.A > 200 && Math.Abs(c.R - r) < 12 && Math.Abs(c.G - g) < 12 && Math.Abs(c.B - b) < 12;
        }

        private static Fixture CheckResolution(string root)
        {
            Fixture f = MakeGame(root);
            string cache = Path.Combine(root, "cache-resolution");
            var tiles = new Dictionary<string, Bitmap>();
            PortraitReport r = FighterPortraits.Load(f.Game, f.Known, cache, CancellationToken.None, delegate(PortraitInfo info, Bitmap b) { tiles[info.Id] = b; }, null);
            try
            {
                Func<string, PortraitInfo> row = delegate(string id) { return r.Fighters.Single(x => x.Id == id); };
                Require(r.Cache == "miss" && r.PakCount == 3 && !r.Discarded, "cache " + r.Cache + ", PAKs " + r.PakCount);
                Require(row("001").Ok && row("001").Pak == "re_chunk_000.pak.patch_001.pak" && IsColor(Center(tiles["001"]), Blue), "001: patch must win over DLC and base (" + row("001").Pak + ")");
                Require(row("002").Ok && row("002").Candidate == "1k" && IsColor(Center(tiles["002"]), Green), "002: key art fallback (zeroed patch row ignored): " + row("002").Candidate);
                Require(row("003").Ok && row("003").Path.EndsWith(".tex.143230113"), "003: 2023 suffix");
                Require(row("004").Status == "missing" && tiles["004"] == null, "004: name tile");
                Require(row("005").Ok && IsColor(Center(tiles["005"]), Green), "005: deflate entry");
                Require(row("006").Ok && row("006").Candidate == "cfn" && IsColor(Center(tiles["006"]), White), "006: broken face must fall back to the key art: " + row("006").Candidate + " " + row("006").Note);
                Require(row("007").Ok && row("007").Pak == "re_chunk_000.pak.patch_001.pak" && IsColor(Center(tiles["007"]), Green), "007: zeroed base row, patch copy");
                Require(row("008").Status == "missing", "008: zeroed row only must be missing");
                Require(row("009").Path.Contains("/caracterselect/") && IsColor(Center(tiles["009"]), Red), "009: old spelling first");
                Require(row("030").Path.Contains("/characterselect/") && IsColor(Center(tiles["030"]), Blue), "030: new spelling first");
                Require(r.Fighters.Any(x => x.Id == "035" && x.Ok && x.Name == "esf035") && tiles.ContainsKey("035"), "new fighter 035 not discovered");
                Require(!r.Fighters.Any(x => x.Id == "040"), "a code without a fighter was listed");
                Require(r.StageNames.Count == 3 && r.StageNames["0000"] == "Fixture Room" && r.StageNames["0410"] == "Fixture Cafe" && r.StageNames["0800"] == "Fixture Hut", "stage names: " + String.Join(", ", r.StageNames.Select(kv => kv.Key + "=" + kv.Value)));
                Require(tiles.Values.Where(b => b != null).All(b => b.Width == FighterPortraits.TileWidth && b.Height == FighterPortraits.TileHeight), "tile size");
                Require(File.Exists(Path.Combine(cache, "portraits.tsv")) && File.Exists(Path.Combine(cache, "001.png")) && !File.Exists(Path.Combine(cache, "004.png")) && Directory.GetFiles(cache, "*.tmp").Length == 0, "cache files");
                FighterPortraits.Manifest m = FighterPortraits.ReadManifest(cache);
                Require(m != null && m.Identity == GamePakReader.Open(f.Game).Identity && m.Rows["004"].Status == "missing" && m.Rows["035"].Ok && m.Stages["0410"] == "Fixture Cafe", "manifest");
            }
            finally { foreach (Bitmap b in tiles.Values) if (b != null) b.Dispose(); }
            return f;
        }

        private static void CheckCache(string root, Fixture f)
        {
            Require(f != null, "no fixture game");
            string cache = Path.Combine(root, "cache-states");
            Func<PortraitReport> load = delegate { return FighterPortraits.Load(f.Game, f.Known, cache, CancellationToken.None, delegate(PortraitInfo i, Bitmap b) { if (b != null) b.Dispose(); }, null); };
            PortraitReport first = load(); Require(first.Cache == "miss", "first " + first.Cache);
            PortraitReport hit = load(); Require(hit.Cache == "hit" && hit.ResolveMs == 0 && hit.Fighters.Count(x => x.Ok) == first.Fighters.Count(x => x.Ok) && hit.StageNames.Count == 3, "second " + hit.Cache);
            string basePak = Path.Combine(f.Game, "re_chunk_000.pak");
            File.SetLastWriteTimeUtc(basePak, File.GetLastWriteTimeUtc(basePak).AddMinutes(1));   // a PAK-method install or game update
            PortraitReport stale = load(); Require(stale.Cache == "stale", "after a PAK change " + stale.Cache);
            Require(load().Cache == "hit", "re-extracted cache not used");
            File.Delete(Path.Combine(cache, "001.png"));
            Require(load().Cache == "miss" && File.Exists(Path.Combine(cache, "001.png")), "lost PNG not re-extracted");
            string manifest = Path.Combine(cache, "portraits.tsv");
            File.WriteAllText(manifest, "#format\tsomething-else\r\n#identity\tx\r\n");
            Require(load().Cache == "miss", "foreign manifest accepted");
            // The game changes while reading: tiles still arrive but nothing is cached.
            File.Delete(manifest);
            int calls = 0; int tiles = 0;
            PortraitReport discarded = FighterPortraits.Load(f.Game, f.Known, cache, CancellationToken.None, delegate(PortraitInfo i, Bitmap b) { tiles++; if (b != null) b.Dispose(); }, delegate { return ++calls >= 2; });
            Require(discarded.Discarded && !File.Exists(manifest) && tiles > 0, "a read overlapping a change was cached");
            // A change in progress before the read defers it.
            using (var cts = new CancellationTokenSource(1500))
            {
                bool cancelled = false;
                try { FighterPortraits.Load(f.Game, f.Known, cache, cts.Token, null, delegate { return true; }); } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && !File.Exists(manifest), "a read started while a change held the lock");
            }
            int nameTiles = 0;
            PortraitReport none = FighterPortraits.Load(Path.Combine(root, "no-game"), f.Known, Path.Combine(root, "cache-none"), CancellationToken.None, delegate(PortraitInfo i, Bitmap b) { if (b == null) nameTiles++; else b.Dispose(); }, null);
            Require(none.Cache == "none" && nameTiles == f.Known.Count && !Directory.Exists(Path.Combine(root, "cache-none")), "without a game folder: " + none.Cache);
        }

        private static void CheckCrop()
        {
            int w = 128, h = 128; var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 96; x < 128; x++) { int o = (y * w + x) * 4; px[o] = 255; px[o + 3] = 255; }   // opaque blue strip on the right
            double cx = FighterPortraits.AlphaCentroidX(px, w, h, 0, h);
            Require(cx > 105 && cx < 113, "centroid " + cx);
            using (Bitmap tile = FighterPortraits.CropTile(px, w, h, true))
            {
                Require(tile.Width == 300 && tile.Height == 420, "tile size");
                // Window 91 px wide (5:7 of 128) ends at the right edge: the strip fills its right third.
                Require(tile.GetPixel(290, 210).A == 255 && tile.GetPixel(10, 210).A == 0, "crop window not moved to the figure");
            }
            Require(FighterPortraits.AlphaCentroidX(new byte[16 * 16 * 4], 16, 16, 0, 16) == 8, "no opaque pixels: centre");
        }

        private static void CheckMessages()
        {
            byte[] msg = Gmsg(new[] { new KeyValuePair<string, string>("StageNameMessage0", "Fixture Room"), new KeyValuePair<string, string>("StageNameMessage10100", "Arena"), new KeyValuePair<string, string>("StageNameMessage10101", "Arena Night"), new KeyValuePair<string, string>("StageNameMessage500000", "Hub") });
            var names = GameMessages.StageNames(msg);
            Require(names.Count == 4 && names["0000"] == "Fixture Room" && names["0101"] == "Arena" && names["0101_01"] == "Arena Night" && names["5000"] == "Hub", "names: " + String.Join(", ", names.Select(kv => kv.Key + "=" + kv.Value)));
            var english = GameMessages.Entries(msg, 1); var first = GameMessages.Entries(msg, 0);
            Require(english[0].Value == "Fixture Room" && first[0].Value == "FIXTURE ROOM", "language columns");
            bool refused = false; try { GameMessages.Entries(Encoding.ASCII.GetBytes("not a message file at all, long enough to read a header from here........"), 1); } catch (InvalidDataException) { refused = true; }
            Require(refused, "a non-GMSG file was parsed");
            byte[] bad = (byte[])msg.Clone(); Buffer.BlockCopy(BitConverter.GetBytes((long)bad.Length + 100), 0, bad, 0x48, 8);
            refused = false; try { GameMessages.Entries(bad, 1); } catch (InvalidDataException) { refused = true; }
            Require(refused, "entry outside the file accepted");
            Require(ModCategories.StageName("0410", names) == "Stage ess0410_00" && ModCategories.StageName("0000", names) == "Fixture Room" && ModCategories.StageName("other", names) == "Other stage files", "stage display names");
        }

        // ---- Categories ----------------------------------------------------------------------------------
        private static void Zip(string path, params string[] entries)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (string entry in entries)
                    using (var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8)) writer.Write("fixture " + entry);
        }
        private const string Mesh = "natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883";

        private static void CheckCategories(string root)
        {
            string lib = Path.Combine(root, "categories"); Directory.CreateDirectory(lib);
            Zip(Path.Combine(lib, "Costume.zip"), Mesh);
            Zip(Path.Combine(lib, "Stage Pack.zip"), "Hut/natives/stm/product/environment/stage/resource/ess/ess0800_00/ess0800_00_01.mesh.230110883", "Hut/natives/stm/product/light/scene/ess/ess0800_00/ess0800_00_light_00.scn.20", "Hut/natives/stm/product/sound/wwise/bgm_battle_ess0800_00.sbnk.1.x64", "Hut/natives/stm/product/environment/props/resource/sm0x/sm05/sm05_304_light/sm05_304_light_04_a.mdf2.31", "Hut/natives/stm/product/model/mob/mob008/00/mob008_00.mdf2.31", "Hut/modinfo.ini");
            Zip(Path.Combine(lib, "Streaming Stage.zip"), "natives/stm/streaming/product/environment/texture/ess1400_00_d_albd.tex.241101895");
            Zip(Path.Combine(lib, "Renamed Stage.zip"), "natives/stm/product/message/cmn/001/wordlist/stagenamemessage.msg.21");
            Zip(Path.Combine(lib, "Hitbox Tool.zip"), "reframework/autorun/hitbox.lua");
            Zip(Path.Combine(lib, "New Challenger Screens.zip"), "natives/stm/product/gui/data/character_image/013/new_challenger/tex_nextchallenger_esf013_00_00_iam.tex.143230113");
            Zip(Path.Combine(lib, "Louder Impact.zip"), "natives/stm/product/sound/wwise/battle_cmn_m.sbnk.1.x64");
            Zip(Path.Combine(lib, "Mixed.zip"), Mesh, "reframework/autorun/mixed.lua", "natives/stm/product/gui/ui/icon.tex.241101895");
            Zip(Path.Combine(lib, "Costume With Shared.zip"), Mesh, "natives/stm/product/model/cmn/cmn000/fdm_base001_nrrc.tex.143230113", "natives/stm/Product/MasterMaterial/texture/BlueNoise_MSK1.tex.143230113", "natives/stm/systems/rendering/NullBlack.tex.143230113", "natives/stm/product/model/1.tex.143230113");
            Zip(Path.Combine(lib, "Shared Only.zip"), "natives/stm/product/model/cmn/cmn000/fdm_base001_nrrc.tex.143230113");
            Zip(Path.Combine(lib, "Pak Only.zip"), "Pak Only/modinfo.ini", "Pak Only/burning.pak");
            Zip(Path.Combine(lib, "Readme Only.zip"), "readme.txt");
            Zip(Path.Combine(lib, "Wind.zip"), "natives/stm/product/weather/wind/esf/esf001/esf001_wind.user.2");
            Zip(Path.Combine(lib, "Avatar.zip"), "natives/stm/product/model/wcs/wcs002/000/000/wcs002_000_000_w00.mesh.230110883");
            // Revision: fighter VFX and colours are the fighter's; a costume or a tool is never Misc as well.
            Zip(Path.Combine(lib, "Juri Colors.zip"), "Juri Colors/reframework/data/SF6ColorSync/Juri/c1_colors.json", "Juri Colors/readme.txt");
            Zip(Path.Combine(lib, "Cammy Drive VFX.zip"), "natives/stm/product/vfx/character/fgm/esf009/effecteditor/efd_10_esf009_1000_00.efx.3474371");
            Zip(Path.Combine(lib, "Costume UI.zip"), Mesh, "natives/stm/product/gui/ui/icon.tex.241101895", "natives/stm/product/sound/wwise/cammy_voice.sbnk.1.x64");
            Zip(Path.Combine(lib, "Fighter On Stage.zip"), Mesh, "natives/stm/product/environment/stage/resource/ess/ess0800_00/ess0800_00_01.mesh.230110883");
            Zip(Path.Combine(lib, "Stage Script.zip"), "reframework/autorun/stage.lua", "natives/stm/product/environment/stage/resource/ess/ess0800_00/ess0800_00_02.mesh.230110883");
            Zip(Path.Combine(lib, "Viewer Scenes.zip"), "natives/stm/escape/character/ch01/x.scn.20", "natives/x64/scene/masterscene.scn.19");
            // 1.0.5: portrait art for several fighters is a UI mod, not a multi-character costume (the user's Comic Art Bundle).
            Zip(Path.Combine(lib, "Comic Art.zip"), "Comic/modinfo.ini", "Comic/natives/stm/product/gui/data/character_image/001/pub/tex_001_cfn_iam.tex.241101895", "Comic/natives/stm/product/gui/data/character_image/009/battlehud/tex_battlehud_009_1p_iam.tex.143230113");
            string folder = Path.Combine(lib, "Folder UI"); Directory.CreateDirectory(Path.Combine(folder, "natives", "stm", "product", "gui", "ui"));
            File.WriteAllText(Path.Combine(folder, "natives", "stm", "product", "gui", "ui", "x.tex.241101895"), "fixture");
            string cache = Path.Combine(root, "categories-cache"); Directory.CreateDirectory(cache);
            string v3 = "{\"Format\":\"sf6-native-library-index-v3\",\"Records\":[]}";
            File.WriteAllText(Path.Combine(cache, "library-native-index-v3.json"), v3);
            string previous = Library.IdentityCacheDirectory;
            try
            {
                Library.IdentityCacheDirectory = cache;
                int misses = Library.CacheMisses;
                List<LibraryEntry> listed = Library.Scan(new[] { lib }, "");
                Require(Library.CacheMisses - misses == 22, "first v4 scan listed " + (Library.CacheMisses - misses) + " of 22 sources");
                Func<string, string> cat = delegate(string name) { LibraryEntry e = listed.Single(x => x.Name == name); return ModCategories.Describe(e) + (e.StageIds.Count > 0 ? " [" + String.Join(",", e.StageIds) + "]" : "") + (e.CharacterCodes.Count > 0 ? " {" + String.Join(",", e.CharacterCodes) + "}" : ""); };
                var expected = new Dictionary<string, string> {
                    { "Costume", "Characters {009}" }, { "Stage Pack", "Misc (Stage) [0800,other]" }, { "Streaming Stage", "Misc (Stage) [1400]" }, { "Renamed Stage", "Misc (Stage) [other]" },
                    { "Hitbox Tool", "Code tools" }, { "New Challenger Screens", "Characters {013}" }, { "Louder Impact", "Misc (Sound)" }, { "Mixed", "Characters, Code tools {009}" },
                    { "Costume With Shared", "Characters {009}" }, { "Shared Only", "Misc (Shared character files)" }, { "Pak Only", "Misc (PAK only)" }, { "Readme Only", "Misc (Other)" },
                    { "Wind", "Characters {001}" }, { "Avatar", "Misc (World Tour avatar)" }, { "Folder UI", "Misc (UI)" },
                    { "Juri Colors", "Characters {016}" }, { "Cammy Drive VFX", "Characters {009}" }, { "Costume UI", "Characters {009}" },
                    { "Fighter On Stage", "Characters [0800] {009}" }, { "Stage Script", "Code tools [0800]" }, { "Viewer Scenes", "Misc (Other)" }, { "Comic Art", "Misc (UI)" } };
                foreach (var kv in expected) Require(cat(kv.Key) == kv.Value, kv.Key + ": " + cat(kv.Key) + " (expected " + kv.Value + ")");
                Require(listed.Single(x => x.Name == "Juri Colors").ToolKinds.Count == 0 && listed.Single(x => x.Name == "Juri Colors").Character == "Juri", "colour data must not be a tool file and must name the fighter");
                Require(listed.All(x => !(ModCategories.IsMisc(x) && (ModCategories.IsCharacter(x) || ModCategories.IsTool(x)))), "a Characters or Code tools mod is also Misc");
                misses = Library.CacheMisses; Library.IdentityCacheDirectory = cache;   // reload from disk
                listed = Library.Scan(new[] { lib }, "");
                Require(Library.CacheMisses - misses == 1 && cat("Stage Pack") == expected["Stage Pack"] && cat("Juri Colors") == expected["Juri Colors"], "cached v4 rescan listed " + (Library.CacheMisses - misses) + " (only the folder is listed again)");
                string file = Path.Combine(cache, Library.CacheFilename);
                Require(Library.CacheFilename == "library-native-index-v4.json" && Json.Read<LibraryIdentityCache>(file).Format == "sf6-native-library-index-v4" && File.ReadAllText(Path.Combine(cache, "library-native-index-v3.json")) == v3, "v4 file / v3 kept");
                LibraryIdentityCache records = Json.Read<LibraryIdentityCache>(file);
                records.Records.Single(r => Path.GetFileName(r.SourcePath) == "Avatar.zip").MiscAreas = null;
                records.Records.Single(r => Path.GetFileName(r.SourcePath) == "Costume.zip").StageIds = null;
                Json.Write(file, records);
                misses = Library.CacheMisses; Library.IdentityCacheDirectory = cache;
                listed = Library.Scan(new[] { lib }, "");
                Require(Library.CacheMisses - misses == 3 && cat("Avatar") == expected["Avatar"], "records without categories were not listed again: " + (Library.CacheMisses - misses));
                records = Json.Read<LibraryIdentityCache>(file);
                records.Records.Single(r => Path.GetFileName(r.SourcePath) == "Costume.zip").StageIds = new List<string> { "../x" };
                Json.Write(file, records);
                misses = Library.CacheMisses; Library.IdentityCacheDirectory = cache;
                listed = Library.Scan(new[] { lib }, "");
                Require(Library.CacheMisses - misses == 2 && cat("Costume") == expected["Costume"], "an invalid stage key was trusted");
            }
            finally { Library.IdentityCacheDirectory = previous; }
            // Rules directly.
            Require(ModCategories.StageOf("stm/product/vfx/resource/material/mat_environment/ess0200_00/ma_ess0020_water_000_01.mdf2.31", false) == "0200" && ModCategories.StageOf("stm/product/environment/props/resource/sm0x/sm06/sm06_009_ess0101/sm06_009_ess0101_05.mesh.230110883", false) == "0101", "first stage code wins; mesh index ignored");
            Require(ModCategories.StageOf("stm/product/model/esf/esf009/001/dress0001.tex", true) == null && ModCategories.StageOf("stm/product/gui/address0001.tex", false) == null, "words ending in ess are not stage codes");
            Require(ModCategories.MiscLabel("stm/product/light/localcubemap/mode/mode0101_00/x.tex") == "Lighting" && ModCategories.MiscLabel("stm/escape/character/x.scn") == "Other" && ModCategories.MiscLabel("stm/product/charparam/cmn/x.user") == "Other" && ModCategories.MiscLabel("stm/Product/MasterMaterial/t.tex") == "Materials" && ModCategories.MiscLabel("stm/product/model/mob/mob008/x.mdf2") == "NPCs", "friendly labels");
            Require(ModCategories.ColourDataFighter("reframework/data/SF6ColorSync/Chun-Li/a.json") == "004" && ModCategories.ColourDataFighter("Pack/reframework/data/colours/esf030_c2.json") == "030" && ModCategories.ColourDataFighter("reframework/data/colors/aki.json") == "013"
                && ModCategories.ColourDataFighter("reframework/data/MMDK/PlayerData/Cammy/moves.json") == null && ModCategories.ColourDataFighter("reframework/data/colors/settings.json") == null && ModCategories.ColourDataFighter("data/colors/juri.json") == null, "colour data rules");
            Require(ModCategories.FighterArtCode("stm/product/gui/data/character_image/034/pub/x.tex") == "034" && ModCategories.FighterArtCode("stm/product/gui/data/area_image/ess/x.tex") == null, "fighter art");
        }

        internal static List<LibraryEntry> SearchFixture()
        {
            Func<string, LibraryEntry> e = delegate(string name) { return new LibraryEntry { Name = name, Kind = "ZIP", OptionCount = 1, SourcePath = @"C:\fixture\filters\" + name + ".zip" }; };
            var cammy1 = e("Cammy Barefoot"); cammy1.Character = "Cammy"; cammy1.CharacterCodes = new List<string> { "009" }; cammy1.CostumeKeys = new List<string> { "009/001" };
            var cammy2 = e("Cammy Boots"); cammy2.Character = "Cammy"; cammy2.CharacterCodes = new List<string> { "009" }; cammy2.CostumeKeys = new List<string> { "009/002" };
            var juri = e("Juri Jacket"); juri.Character = "Juri"; juri.CharacterCodes = new List<string> { "016" }; juri.CostumeKeys = new List<string> { "016/001" };
            var hut = e("Ranger Hut Night"); hut.StageIds = new List<string> { "0800" }; hut.MiscAreas = new List<string> { "Stage" };
            var lighting = e("Lighting Overhaul"); lighting.StageIds = new List<string> { "0000", "0800", ModCategories.OtherStage }; lighting.MiscAreas = new List<string> { "Stage" };
            var plugin = e("Frame Meter"); plugin.OptionCount = 0; plugin.ToolKinds = new List<string> { "plugin" };
            var script = e("Hitbox Viewer"); script.OptionCount = 0; script.ToolKinds = new List<string> { "script" };
            var ui = e("Select Icons"); ui.MiscAreas = new List<string> { "UI" };
            var sound = e("Louder Impact"); sound.MiscAreas = new List<string> { "Sound" };
            var mixed = e("Cammy Scripted"); mixed.Character = "Cammy"; mixed.CharacterCodes = new List<string> { "009" }; mixed.CostumeKeys = new List<string> { "009/001" }; mixed.ToolKinds = new List<string> { "script" };
            return new List<LibraryEntry> { cammy1, cammy2, juri, hut, lighting, plugin, script, ui, sound, mixed };
        }

        private static string Find(List<LibraryEntry> library, string text)
        {
            LibrarySearch.Query q = LibrarySearch.Parse(text, MainForm.SearchCharacterTerm); var codes = LibrarySearch.CodesFor(q.Characters);
            return String.Join(", ", library.Where(e => LibrarySearch.MatchesText(e, q, MainForm.EntryMatchesCharacter, MainForm.EntryMatchesName) && LibrarySearch.CostumeRank(e, q, codes, MainForm.EntryMatchesCharacter) >= 0 && MainForm.MatchesModType(e, q.Type)).Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }

        private static void CheckSearchTerms()
        {
            var lib = SearchFixture();
            Require(Find(lib, "stage") == "Lighting Overhaul, Ranger Hut Night" && Find(lib, "stages") == Find(lib, "stage") && Find(lib, "is:stage") == Find(lib, "stage"), "stage: " + Find(lib, "stage"));
            Require(Find(lib, "misc") == "Lighting Overhaul, Louder Impact, Ranger Hut Night, Select Icons" && Find(lib, "is:misc") == Find(lib, "misc"), "misc: " + Find(lib, "misc"));
            Require(Find(lib, "is:character") == "Cammy Barefoot, Cammy Boots, Cammy Scripted, Juri Jacket" && Find(lib, "is:misc, cammy") == "" && Find(lib, "cammy, is:tool") == "Cammy Scripted", "is:character / combined: " + Find(lib, "cammy, is:tool"));
            Require(Find(lib, "is:tool") == "Cammy Scripted, Frame Meter, Hitbox Viewer" && Find(lib, "cam, c1") == "Cammy Barefoot, Cammy Scripted", "2.0.x terms unchanged: " + Find(lib, "cam, c1"));
            Require(ModCategories.Keyword("sta") == null && ModCategories.Keyword("Stage Mods") == ModCategories.Stages, "keywords");
            var names = new Dictionary<string, string> { { "0800", "Ranger's Hut" }, { "0000", "Training Room" } };
            var saved = MainForm.StageNames;
            try
            {
                MainForm.StageNames = names;
                Require(MainForm.LibraryRowLabel(lib[3], null) == "Stage · Ranger's Hut" && MainForm.LibraryRowLabel(lib[4], null) == "Stage · Training Room +1" && MainForm.LibraryRowLabel(lib[8], null) == "Sound" && MainForm.LibraryRowLabel(lib[0], null) == "C1" && MainForm.LibraryRowLabel(lib[5], null) == "REFramework plugin", "row labels: " + MainForm.LibraryRowLabel(lib[4], null));
            }
            finally { MainForm.StageNames = saved; }
            // 2.5.9 authors: by:name / by:"two words" keep that author's mods; a plain word or comma term also matches the author.
            lib[1].Authors = new List<string> { "ZZtai" }; lib[2].Authors = new List<string> { "Nyx Moon", "ZZtai" };
            Require(Find(lib, "by:zztai") == "Cammy Boots, Juri Jacket" && Find(lib, "by:\"nyx moon\"") == "Juri Jacket" && Find(lib, "by:nobody") == "", "by: " + Find(lib, "by:zztai"));
            Require(Find(lib, "zztai") == "Cammy Boots, Juri Jacket" && Find(lib, "cammy, zztai") == "Cammy Boots" && Find(lib, "by:zztai c2") == "Cammy Boots" && Find(lib, "Barefoot") == "Cammy Barefoot", "author words: " + Find(lib, "zztai") + " / " + Find(lib, "by:zztai c2"));
            Require(Library.AuthorText(new List<string> { "A", "B", "C", "D" }) == "A, B +2" && Library.AuthorText(null) == "" && String.Join("|", Library.CleanAuthors(new[] { " A  b ", "a B", "", null, "C" })) == "A b|C", "author text");
        }

        // Reference blocks decoded by bcdec.h v0.985 itself (MSVC build of the upstream header, 2026-10-14):
        // format, block bytes, 16 RGBA pixels. Random blocks per BC7 mode 0-7 plus the reserved mode, BC1 in
        // four- and three-colour (punch-through) modes, BC3 with both alpha interpolation modes.
        private static readonly string[] Vectors = {
            "BC1 00F81F00E4E4E4E4 FF0000FF0000FFFFAA0055FF5500AAFFFF0000FF0000FFFFAA0055FF5500AAFFFF0000FF0000FFFFAA0055FF5500AAFFFF0000FF0000FFFFAA0055FF5500AAFF",
            "BC1 1F0000F81B1B1B1B 00000000800080FFFF0000FF0000FFFF00000000800080FFFF0000FF0000FFFF00000000800080FFFF0000FF0000FFFF00000000800080FFFF0000FF0000FFFF",
            "BC1 341234126D9A9C82 1045A5FF000000001045A5FF1045A5FF1045A5FF1045A5FF1045A5FF1045A5FF1045A5FF000000001045A5FF1045A5FF1045A5FF1045A5FF1045A5FF1045A5FF",
            "BC1 EDC1CD6969986C34 6B396BFFA73B6BFFA73B6BFF6B396BFFC53D6BFFA73B6BFF6B396BFFA73B6BFFC53D6BFF893A6BFFA73B6BFF6B396BFFC53D6BFF6B396BFF893A6BFFC53D6BFF",
            "BC1 787FD0C073E7260C 000000007BEFC5FF00000000C51884FF00000000C51884FFA084A5FF00000000A084A5FFC51884FFA084A5FF7BEFC5FF7BEFC5FF000000007BEFC5FF7BEFC5FF",
            "BC1 564894E02F9AC4ED 0000000000000000980CADFF4A08B5FF980CADFF980CADFFE610A5FF980CADFF4A08B5FFE610A5FF4A08B5FF00000000E610A5FF00000000980CADFF00000000",
            "BC1 41A81EE038492A4E AD0808FFCA0480FF00000000AD0808FFE600F7FFCA0480FFAD0808FFE600F7FFCA0480FFCA0480FFCA0480FFAD0808FFCA0480FF00000000AD0808FFE600F7FF",
            "BC1 FD2A10306D86A02E 310084FF000000002D2FB9FF310084FF2D2FB9FF310084FF295DEFFF2D2FB9FF295DEFFF295DEFFF2D2FB9FF2D2FB9FF2D2FB9FF000000002D2FB9FF295DEFFF",
            "BC3 FF0079D2FA78FB9C9E87FFC2ADB31833 C55DFF00AF8FFC249AC1FA009AC1FA00AF8FFC6D84F3F76DAF8FFC489AC1FA2484F3F7FF9AC1FA24C55DFF6D84F3F76DAF8FFC2484F3F700AF8FFC2484F3F791",
            "BC3 00FF355B2DC8F4718623A771B764CE50 584937CC73353A00584937993C5D34CC217131CC73353A333C5D346673353AFF3C5D3400584937FF2171316658493733217131FF2171316673353A9973353A66",
            "BC3 2828371FF65AEBE97B77C3BB76F53906 8CC89CFFBD791900A5A15A28BD7919FFBD791928BD791928A5A15A28A5A15AFFBD7919288CC89C28A5A15A2873EFDE288CC89C00BD79192873EFDE2873EFDEFF",
            "BC3 53F3DED05E6F214442BE06D8E66CBEB3 C8871B00DE003193C8871B93D3432653BDCA10D3D34326D3C8871BFFDE003173C8871BFFD34326D3D34326D3C8871B53D3432673BDCA1053D34326F3C8871B73",
            "BC3 FE07B842B791FD4823BDEE93B036DD38 BDA619FEBDA6192AA28B55DAAF983707AF983794947D734DA28B5570BDA61970947D7307A28B55DA947D734DA28B554DBDA6192AAF983707A28B55DABDA619DA",
            "BC3 E73284772D83E535335575AC537BE75F 8F96A79952A69CE7AD8EAD65AD8EADB38F96A74B709EA2CD8F96A7B3AD8EAD328F96A7B3AD8EADE7709EA2658F96A7CD8F96A7658F96A7B3AD8EAD7FAD8EAD32",
            "BC3 419B15C5351810EBAAB376FADA0FED9B CE687389CE687353FF4DB577E65A9453E65A9477E65A9465B5755289B575529BFF4DB541E65A9465CE687341E65A9441E65A949BCE687300FF4DB553CE6873FF",
            "BC3 A9DB677896BF3B09ADE8BA41673983D9 7B2AB2FF4235D6C7B51F8FDB4235D6C74235D6FFB51F8FC77B2AB2D1EF146BC77B2AB2FFEF146BFFEF146B00B51F8FD14235D6BDB51F8FB34235D6B37B2AB2A9",
            "BC7 91E133CA7D9EBB12F2D3B043C096D797 91E967FF3DED87FF76EA71FFC6E752FFABE85CFFC6E752FF58EC7DFF3DED87FFF0541CFFF0541CFF94F794FFB2C26DFF49D4A2FF52D694FF23CBDBFF23CBDBFF",
            "BC7 83D437FF01E935130D2F64040996A61C 4AFF9CFF709399FF7F6C97FFE78484FF4AFF9CFF57DB9BFFE07B7FFFE78484FFC1D880FFB2E281FFD9717BFFD26876FFB2E281FFA3ED83FFFFAD7BFFE78484FF",
            "BC7 81202A8D43DD555726A69EB7A70B7DF5 2B7EB0FF1F60B2FF52E731FF28BD2BFF1545B3FF0010B5FF3ACF2EFF4ADF30FF1545B3FF9CEF18FF8EDC21FF52E731FF87D326FF79C030FF72B634FF87D326FF",
            "BC7 F18991B18A9E66E7D0FE734606C31CDA CC5A98FFB35A85FF635A4CFFFF5ABDFFE65AAAFFB35A85FFFF5ABDFF635A4CFFCE4A7BFFA8B385FFC5637DFF8CFF8CFFCE4A6BFFB94392FFAF3FA6FFAF3FA6FF",
            "BC7 D9CE0F9E16A040097F47F80BF361B666 6D767FFF7300F7FFB600B5FF2346A2FF718970FFD70094FFB600B5FFF70031FF6342A5FF8300E7FFA400C6FFB21756FF749A64FF8300E7FFA400C6FF2346A2FF",
            "BC7 EE0B7BE5AAA9CDBC35FFD452500C1C5C 3FA7DBFF8C9E83FF8C9E83FFABA2E9FF8384DBFF2CA9F1FF51A5C6FF3FA7DBFFD2BFF6FF5C68CDFF5C68CDFFB19958FF2CA9F1FFABA2E9FF9693E1FF6F76D4FF",
            "BC7 8EA3276E5FFCD8E849D2CA3AE73B9370 8791A0FF81A69FFF76AFC1FF72C5CAFF8A86A0FF78C59DFF7A99B8FF6EDBD3FF87549CFF6EDBD3FF81A69FFF8A86A0FF87549CFF87549CFF81A69FFF849B9FFF",
            "BC7 7626A99B092319E49B7779C84BD37E00 99299FFF9332BFFF9B2693FF99299FFFA41F84FFC734B5FFDE41D5FFB02694FFDE41D5FFD33BC5FFC734B5FF991874FF9332BFFF9B2693FF9B2693FF9B2693FF",
            "BC7 BA1362794C5774ABCF137C0D522D5221 394DCDFF787410FF612FB2FF394DCDFF4C30ADFF612FB2FF612FB2FF2C61E4FF746730FF4043C2FF3357D9FF6B4C6FFF612FB2FF463AB8FF3357D9FF5814F1FF",
            "BC7 3EBF6638464E078C8DCE75069B423DAF EA374AFF6AE7DBFFA99194FFEA374AFFFF1A32FFC07079FFC07079FFD55462FFEA374AFFA99194FFD55462FF6AE7DBFF1DB4A7FF3804CDFF2F3EC1FF2197ADFF",
            "BC7 8CADC3BE9C3C3ABD43A03729D1B959A9 B5CE10FF2E877AFF391821FF2E877AFFB5CE10FFF7A59CFFD6D786FFF7A59CFFB5CE10FF391821FF344E4CFF344E4CFFB5CE10FFD6D786FFD6D786FFD6D786FF",
            "BC7 C4506A4104EE85B933BF6533957D6054 42E7CEFF4B1B78FF0BA7B2FF0DB2C1FF47F296FF4B1B78FF089CA5FF0DB2C1FF42E7CEFF42E7CEFF282697FF4B1B78FF42E7CEFF45ECB3FF45ECB3FF45ECB3FF",
            "BC7 940ACB609CA8980AEADFA1F2EEC83665 3C79E9FF5065D3FF6352BDFF298CFFFF44583FFFCE6318FF3952DEFF2E3AD9FF8A5D2BFF44583FFF2320D3FF2E3AD9FF44583FFFCE6318FF3952DEFF1808CEFF",
            "BC7 5C7BA8550E731BE6E9BD3C57EB953D74 A35F7BFF548F7BFF184A6BFF6BB360FFAD89BEFFAD89BEFF94E75AFF184A6BFF548F7BFF08BD7BFF6BB360FF94E75AFFADA8ACFFADA8ACFF184A6BFF94E75AFF",
            "BC7 CCD9A8570358B90ACF065B15C8DEDB85 4A917CFF6384B5FF007B00FF46551BFF18AD08FF4A917CFFBD52ADFFBD52ADFF31A041FF4A917CFFB2B6B2FFBD52ADFF31A041FF6384B5FFD60852FF902E37FF",
            "BC7 2864C1DC851907EAF8CAD3535632A655 B7AE96FF4C3D6CFF4C3D6CFFB840A6FFB7AE96FF4C3D6CFF833E89FFB840A6FFC070CAFFB840A6FF833E89FF173B4FFFBC8FB1FF4C3D6CFF4C3D6CFFB840A6FF",
            "BC7 C89CDA48E9D93CE3A905FE088E029BD8 7CCD90FF7CCD90FF4ECED4FF976CB5FF7CCD90FF9066FCFF9066FCFF9E736AFF976CB5FFA57923FF9066FCFF976CB5FF9066FCFFA57923FF9E736AFF976CB5FF",
            "BC7 080C4020ED4A03667F3F6DD3F5A335BC 1A4C95FF2E4069FFB4D84CFFB4D84CFF1A4C95FF0757BFFF67C9ACFFB4D84CFF2E4069FF2E4069FF67C9ACFF41C1DBFF2E4069FF41353FFF67C9ACFF67C9ACFF",
            "BC7 D8C75CB9A6DBF9ADE23E138E717BB11C 62DC70FF5FB24FFF80B82DFF80B82DFF5D9D3FFF5FB24FFF9AAA38FF80B82DFF80B82DFF73BF27FF9AAA38FF8DB132FF62DC70FF5D9D3FFF60C760FF62DC70FF",
            "BC7 D8E272105DDC1273EA6906A1E7B3CCA6 71A7C6FF70E2F4FF759D85FF759D85FF3D7634FF598A5EFF71A7C6FF71A7C6FF598A5EFF3D7634FF726897FF71A7C6FF732D69FF70E2F4FF3D7634FF3D7634FF",
            "BC7 900ED4C7828E6A4826B5005D27DF3EE4 73AD63EB73AD63D331902EA2108215EB208922EB108215D363A656BA63A656EB007B08A242983DEB42983DD3007B08BA42983DBA73AD63BA63A656D3007B08D3",
            "BC7 D098B3C035F98E1D393D5C9421E9306A CFA0E249D4A0E03DCB92E556CFBEE249CBB0E556D4BEE03DC692E763CBB0E556CB92E556DEBEDB22D4A0E03DC6B0E763D4B0E03DD9BEDD2ECFA0E249D492E03D",
            "BC7 301AE0832F92986BA8E51CA08F1A89D8 21C6C6D62239BD0020C6C6D62039BD002198C3902498C3902239BD0022C6C6D621C6C6D62298C3902298C3902239BD002067C04621C6C6D62339BD002398C390",
            "BC7 102429A8B33E810C171604EBAC4D6D5B 2152D6BF2152D6EB2152D6902E62D3793D74D1632E62D3D52152D6A83D74D1794A84CE793D74D1D52152D6792152D6634A84CE633D74D1632152D6632152D6BF",
            "BC7 9030D230DC7167C422E61A05EB6692C2 858F255C87633E1C894A4DDF8679321C84A5189F8B1E661C8679329F8C08735C8B1E665C894A4D1C858F255C858F251C858F25DF8A34591C84A518DF8B1E665C",
            "BC7 204EBDF2BFDAE85F12BFABF08B530919 9D9556DAD8DC41B79D9556FAD8DC41B7F5FF3697F5FF36FABAB84CDAF5FF36DABAB84CDABAB84CB7BAB84CFABAB84CFA9D9556DAD8DC41B7F5FF36DAF5FF36FA",
            "BC7 207E42D7A8C0A80A990F47CF5972EA09 FDBB14AA088D3064FDBB1488088D3088088D3064ADAC1DAAFDBB1442589C2788088D3064FDBB1464589C2764589C2742088D3088ADAC1D64589C27AA088D30AA",
            "BC7 E0DF6727FA5DB5F0F25ABB3868ECD85C BF3A2DBFAA818D789FA38D56B55C5C9DB55C2D9D9FA3BC56AA818D78AA81BC78B55C2D9D9FA38D56B55C5C9DB55CBC9DBF3A2DBF9FA3BC56B55C5C9DBF3A5CBF",
            "BC7 E0FD7A1CC08A6772E7D089549DE1290A F6999986FBE3DC58EB00AFE3F699C686FBE3AF58F04A99B5F04AC6B5EB00DCE3FBE3AF58F699C686FBE3C658F6999986F04AC6B5F04AC6B5F04A99B5F04A99B5",
            "BC7 6087499352587ACB218E46A1A266BD02 BB9B0A0EDE9B0A0E95753816959B0A0E95289726BB753816959B0A0EBB753816BB289726729B0A0E724E691E954E691E959B0A0EDE9B0A0EDE753816DE753816",
            "BC7 C0D7D8EB8C46DB01BB856419BF0BFFB1 80B2A293ABA5A33C80B2A29396ACA3687AB4A2A189AFA2839CAAA35A65BAA2CDC79DA303ABA5A33CABA5A33C5EBCA2DAC79DA303C79DA30365BAA2CDABA5A33C",
            "BC7 40BDA8FE026C3969976A86D03A622F41 D0CE25578C976D947E8D7BA0ADB24A77ADB24A7797A0618AF4EA00385E739DBD7E8D7BA0D0CE2557DBD61A4EADB24A77455FB7D3DBD61A4EE9E10B42C6C53161",
            "BC7 C0338BD104269D6FBE147B2BC6FDABF0 975588BB77778DCCAF3B85AEC72081A077778DCC975588BB77778DCCBD2A83A59E4D87B770808ED169888FD5589A92DE77778DCC7F6F8CC8CE18809C589A92DE",
            "BC7 C07FCF81D2556EF33FFB00579E804BA0 C13552A7E42866879E433FC77B512BE7FF1D756FFF1D756FC13552A7D42E5D96834E30E0B13C49B6FF1D756FB9394EAF9E433FC7DC2B618FFF1D756FA64043C0",
            "BC7 C056CD669382B5D3CCE99DCF64E738C0 616DB0AF676CBBA9646CB5AC696CBEA7686CBCA8646CB5AC6A6CC0A6676CBBA95F6DACB1616DB0AF626DB2AE696CBEA7636CB3AD5E6DAAB25B6DA5B5676CBBA9",
            "BC7 80F331345AC1C47608AC2953FE73B435 3880BB713010719A3010719A3010719A3880BB71952D18643010719A3CB6DF5DA26141929B482D7B8E14044D344695863880BB719B482D7B3010719A3CB6DF5D",
            "BC7 80962AB49777E2C1A101AF699F684301 464568522879C3F3552C3C0446456852552C3C04464568522879C3F3376097A5BE572CC7552C3C04552C3C0446456852BE572CC7A63C0CD7552C3C04552C3C04",
            "BC7 804F1FB72C84A8087EB494C80AAA0490 EF5D146DAA551163EF5D146DEF5D146DAA551163AA551163AA551163AA551163614D0F58EF5D146DEF5D146DEF5D146DBA41F3495B856330BA41F3498B62AC3D",
            "BC7 80FD2CCB6FCB427EE42032E7DBA54F2C 9EDFCF452CB67D245D65249EF70C3C9E79D2B43A79D2B43AC429349EC429349EF70C3C9EF70C3C9E5D65249E90482C9E9EDFCF452CB67D2451C3982F9EDFCF45",
            "BC7 80AE5F0FDF440C9556F632EC194F83DE F7BEA6EFC33092B27D24B61C90729B6C5D4D962C9428AA4DAC2C9E8190729B6C9428AA4DF7BEA6EFF7BEA6EF9428AA4DC33092B25D4D962C90729B6C9428AA4D",
            "BC7 009F6052764E916475C71B4F53F0DE79 00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        };

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
