using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.16 --filter-check: the REFramework & code tile (LibraryCode), which replaced "Tool mods": the classifier on
    // synthetic archives, the one-time index backfill, and the tile with its chips in hidden windows.
    internal static partial class FilterChecks
    {
        private static string Code(string name, params string[] paths) { return String.Join("+", LibraryCode.Classify(paths, name)); }

        private static void CheckCodeKinds()
        {
            const string mesh = "Main/natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883";
            Action<string, string> expect = delegate(string got, string wanted) { Require(got == wanted, "expected '" + wanted + "', got '" + got + "'"); };
            // SF6 REFramework scripts and plugins (wrapper folders allowed, \ separators too).
            expect(Code("Hitbox Viewer", "reframework/autorun/hitbox.lua", "reframework/autorun/hitbox/util.lua"), "script");
            expect(Code("Frame Meter", "Frame Meter v2/reframework/plugins/meter.dll", "Frame Meter v2/readme.txt"), "plugin");
            expect(Code("Back", @"Mod\reframework\autorun\x.lua"), "script");
            // REFramework itself: dinput8.dll with a revision file, a reframework/ folder or the name; a lone dinput8.dll is a loader.
            expect(Code("SF6", "dinput8.dll", "reframework_revision.txt"), "reframework");
            expect(Code("REFramework-73-Nightly01179", "SF6/dinput8.dll"), "reframework");
            expect(Code("Bundle", "dinput8.dll", "reframework/fonts/ui.ttf"), "reframework");
            expect(Code("Proxy Loader", "dinput8.dll", "readme.txt"), "injector");
            // ReShade, Slotsloader's package (amd_ags_x64.dll + its plugin + data), ASI loaders.
            expect(Code("ReShade SF6", "dxgi.dll", "ReShade.ini", "reshade-shaders/Shaders/Bloom.fx"), "injector");
            expect(Code("Preset", "SF6 Preset/reshade-shaders/Textures/x.png"), "injector");
            expect(Code("SF6_SlotsLoader_v1.11.0", "amd_ags_x64.dll", "reframework/plugins/SlotsLoader.dll", "reframework/data/SF6_Costumes_Data/outfits.json"), "plugin+reframework+injector");
            expect(Code("Ultimate ASI Loader", "version.dll", "scripts/fps.asi"), "injector");
            expect(Code("Lone ASI", "Mod/fps.asi"), "injector");
            expect(Code("Deep", "a/b/c/dxgi.dll", "a/b/c/d/e.asi"), "");
            // Other games: SKSE plugins (with or without Data/), Engine Fixes (both parts), ENB with dxgi.dll, Mod Engine,
            // 7-Zip patches; a plain natives outfit and a costume mod with REFramework data or fighter colour data.
            expect(Code("Engine Fixes - Main File", "SKSE/Plugins/EngineFixes.dll", "SKSE/Plugins/EngineFixes.toml"), "");
            expect(Code("Plugin", "Data/SKSE/Plugins/x.dll", "Data/x.esp"), "");
            expect(Code("Engine Fixes Part 2", "d3dx9_42.dll", "tbb.dll", "tbbmalloc.dll"), "");
            expect(Code("enbseries_skyrimse_v0503", "WrapperVersion/d3d11.dll", "WrapperVersion/dxgi.dll", "WrapperVersion/enbseries.ini", "WrapperVersion/enblocal.ini"), "");
            expect(Code("ENB", "dxgi.dll", "enbseries/effect.fx"), "");
            expect(Code("Seamless", "SeamlessCoop/ersc.dll", "dinput8.dll", "modengine2_launcher.exe"), "");
            expect(Code("Fallout", "f4se_loader.exe", "Data/F4SE/Plugins/buffout.dll"), "");
            expect(Code("7z2501-x64-patch", "7-zip.dll", "7z.dll", "7zFM.exe"), "");
            expect(Code("Outfit", mesh, "Main/modinfo.ini"), "");
            expect(Code("Colours", mesh, "Main/reframework/data/MMDK/settings.json"), "");
            expect(Code("Juri Colours", "reframework/data/SF6ColorSync/Juri/c1.json"), "");
            expect(Code("Mac", mesh, "__MACOSX/reframework/autorun/._x.lua", "Thumbs.db"), "");
            // Another game's marker beside reframework/ content keeps the SF6 kinds.
            expect(Code("Mixed", "reframework/autorun/x.lua", "Data/x.esp"), "script");
            expect(Code("Fonts", "Fonts/reframework/fonts/ui.ttf", "Fonts/reframework/data/settings.json"), "reframework");
            // Until an entry's kinds are known, ToolKinds give a conservative guess (a plain DLL is no guess).
            Func<List<string>, int, LibraryEntry> old = delegate(List<string> tools, int options) { return new LibraryEntry { ToolKinds = tools, OptionCount = options }; };
            Require(!LibraryCode.Any(old(new List<string> { "dll" }, 0)) && LibraryCode.Has(old(new List<string> { "script", "dll" }, 0), LibraryCode.Script) && !LibraryCode.Has(old(new List<string> { "script", "dll" }, 0), LibraryCode.Injector)
                && LibraryCode.Has(old(new List<string> { "plugin" }, 0), LibraryCode.Plugin) && LibraryCode.Any(old(new List<string> { "reframework" }, 0)) && !LibraryCode.Any(old(new List<string> { "reframework" }, 1)), "guess from ToolKinds");
            Require(!LibraryCode.Any(new LibraryEntry { ToolKinds = new List<string> { "script" }, CodeKinds = new List<string>() }) && LibraryCode.Has(new LibraryEntry { CodeKinds = new List<string> { "injector" } }, LibraryCode.Injector), "known kinds win over the guess");
            // Search: "injector(s)" lists the Code injectors kind; the tool words are unchanged.
            Require(LibrarySearch.ToolKeyword("injectors") == LibraryCode.Injector && LibrarySearch.ToolKeyword("Code  Injector") == LibraryCode.Injector && LibrarySearch.ToolKeyword("inj") == null && LibrarySearch.ToolKeyword("reframework") == LibrarySearch.AnyTool, "search words");
        }

        // An index from before 1.0.16 has no CodeKinds: ScanQuick keeps the identity without opening the archive (the
        // window reads it in the background, IndexEntry), a record without tool kinds gets none without a read.
        private static void CheckCodeBackfill(string root)
        {
            string lib = Path.Combine(root, "code-backfill"); Directory.CreateDirectory(lib);
            string proxy = Path.Combine(lib, "Proxy Loader.zip"), skse = Path.Combine(lib, "Engine Fixes.zip"), outfit = Path.Combine(lib, "Outfit.zip"), script = Path.Combine(lib, "Hitbox Viewer.zip");
            // 1.0.16 (PR #22 review, F4): an ASI-only and a ReShade-only archive have no tool kinds and no natives options:
            // their old records are read once too (the shortcut gave them no kind, so they never showed as Code injectors).
            string asi = Path.Combine(lib, "FPS unlock.zip"), reshade = Path.Combine(lib, "SF6 ReShade preset.zip");
            Zip(proxy, "dinput8.dll", "amd_ags_x64.dll", "readme.txt");
            Zip(skse, "SKSE/Plugins/EngineFixes.dll", "d3dx9_42.dll");
            Zip(outfit, Mesh);
            Zip(script, "reframework/autorun/hitbox.lua");
            Zip(asi, "Mod/fps.asi");
            Zip(reshade, "ReShade.ini", "reshade-shaders/Shaders/Bloom.fx");
            string previous = Library.IdentityCacheDirectory, cache = Path.Combine(root, "code-backfill-index");
            try
            {
                Library.IdentityCacheDirectory = cache;
                List<LibraryEntry> scanned = Library.Scan(new[] { lib }, "");
                Func<List<LibraryEntry>, string, LibraryEntry> at = delegate(List<LibraryEntry> list, string path) { return list.Single(e => String.Equals(e.SourcePath, path, StringComparison.OrdinalIgnoreCase)); };
                Require(String.Join("+", at(scanned, proxy).CodeKinds) == "injector" && at(scanned, skse).CodeKinds.Count == 0 && at(scanned, outfit).CodeKinds.Count == 0 && String.Join("+", at(scanned, script).CodeKinds) == "script" && String.Join("+", at(scanned, asi).CodeKinds) == "injector" && String.Join("+", at(scanned, reshade).CodeKinds) == "injector", "fresh scan: " + String.Join("; ", scanned.Select(e => e.Name + "=" + String.Join("+", e.CodeKinds ?? new List<string> { "null" }))));
                string file = Path.Combine(cache, Library.CacheFilename);
                Require(Json.Read<LibraryIdentityCache>(file).Records.All(r => r.CodeKinds != null), "kinds not stored in the index");
                // The records as an older Manager wrote them (no CodeKinds), read as on a restart.
                LibraryIdentityCache records = Json.Read<LibraryIdentityCache>(file);
                foreach (LibraryIdentityRecord r in records.Records) r.CodeKinds = null;
                Json.Write(file, records);
                Require(!File.ReadAllText(file).Contains("\"CodeKinds\":["), "the old index still has kinds");
                Library.IdentityCacheDirectory = Path.Combine(root, "code-backfill-other"); Library.IdentityCacheDirectory = cache;
                List<LibraryEntry> pending, names, quick;
                int misses = Library.CacheMisses;
                // Start-up is not blocked: no archive is opened (they are locked here) and every mod keeps its identity.
                var locks = new[] { proxy, skse, outfit, script, asi, reshade }.Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
                try { quick = Library.ScanQuick(new[] { lib }, "", out pending, out names); }
                finally { foreach (FileStream s in locks) s.Dispose(); }
                Require(Library.CacheMisses == misses && quick.All(e => !e.Pending && e.Detection != "Error" && e.OptionCount >= 0), "quick listing lost identity: " + String.Join("; ", quick.Select(e => e.Name + " " + e.Detection)));
                LibraryEntry qProxy = at(quick, proxy), qSkse = at(quick, skse), qOutfit = at(quick, outfit), qScript = at(quick, script), qAsi = at(quick, asi), qReshade = at(quick, reshade);
                Require(qProxy.CodeKinds == null && qSkse.CodeKinds == null && qScript.CodeKinds == null && pending.Contains(qProxy) && pending.Contains(qSkse) && pending.Contains(qScript), "records with tool kinds are not read again in the background");
                Require(qAsi.CodeKinds == null && qReshade.CodeKinds == null && pending.Contains(qAsi) && pending.Contains(qReshade), "an ASI-only or ReShade-only record (no tool kinds, no natives options) was not queued: " + (qAsi.CodeKinds == null ? "null" : "listed") + " / " + (qReshade.CodeKinds == null ? "null" : "listed"));
                Require(qOutfit.CodeKinds != null && qOutfit.CodeKinds.Count == 0 && !pending.Contains(qOutfit), "a natives record without tool kinds was queued: " + (qOutfit.CodeKinds == null ? "null" : "listed"));
                // Until then the tile guesses from the tool kinds: the script counts, the plain DLLs don't.
                Require(LibraryCode.Any(qScript) && !LibraryCode.Any(qProxy) && !LibraryCode.Any(qSkse) && !LibraryCode.Any(qOutfit), "guess before the background read");
                // A failed background read keeps the identity and leaves the kinds for the next start.
                LibraryEntry copy = Library.IndexCopy(qProxy);
                using (var locked = new FileStream(proxy, FileMode.Open, FileAccess.Read, FileShare.None)) Library.IndexEntry(copy, "");
                Require(copy.CodeKinds == null && copy.Detection != "Error" && copy.OptionCount == 0, "a failed backfill erased identity or invented kinds");
                foreach (LibraryEntry e in new[] { qProxy, qSkse, qScript, qAsi, qReshade }) { LibraryEntry c = Library.IndexCopy(e); Library.IndexEntry(c, ""); Library.TakeIdentity(e, c); }
                Library.SaveIndex();
                Require(String.Join("+", qProxy.CodeKinds) == "injector" && qSkse.CodeKinds.Count == 0 && String.Join("+", qScript.CodeKinds) == "script" && String.Join("+", qAsi.CodeKinds) == "injector" && String.Join("+", qReshade.CodeKinds) == "injector" && LibraryCode.Any(qProxy) && Library.CacheMisses == misses, "background backfill: " + String.Join("+", qProxy.CodeKinds ?? new List<string> { "null" }) + ", misses " + (Library.CacheMisses - misses));
                // Saved: the next listing has every kind and queues nothing.
                Library.IdentityCacheDirectory = Path.Combine(root, "code-backfill-other"); Library.IdentityCacheDirectory = cache;
                quick = Library.ScanQuick(new[] { lib }, "", out pending, out names);
                Require(pending.Count == 0 && quick.All(e => e.CodeKinds != null) && String.Join("+", at(quick, proxy).CodeKinds) == "injector" && String.Join("+", at(quick, reshade).CodeKinds) == "injector", "after the backfill: " + pending.Count + " queued");
            }
            finally { Library.IdentityCacheDirectory = previous; }
        }

        // SearchFixture (no CodeKinds: Frame Meter, Hitbox Viewer and Cammy Scripted count by their tool kinds) and mods
        // whose kinds the listing decided, one of them another game's DLL mod, one an old record with only a DLL.
        internal static List<LibraryEntry> CodeFixture()
        {
            var lib = SearchFixture();
            Func<string, string[], string[], LibraryEntry> e = delegate(string name, string[] tools, string[] code) { return new LibraryEntry { Name = name, Kind = "ZIP", OptionCount = 0, SourcePath = @"C:\fixture\filters\" + name + ".zip", ToolKinds = tools.ToList(), CodeKinds = code == null ? null : code.ToList() }; };
            lib.Add(e("Engine Fixes", new[] { "dll" }, new string[0]));
            lib.Add(e("Old Index DLL", new[] { "dll" }, null));
            lib.Add(e("REFramework Nightly", new[] { "dll" }, new[] { "reframework" }));
            lib.Add(e("Slotsloader Package", new[] { "plugin", "dll", "reframework" }, new[] { "plugin", "reframework", "injector" }));
            lib.Add(e("ReShade Preset", new[] { "dll" }, new[] { "injector" }));
            return lib;
        }
        private static FlatButton CodeChip(FilterPanel panel, string kind) { return panel.CodeChips.Controls.OfType<FlatButton>().Single(b => (string)b.Tag == kind); }

        private static string CheckCodeTile(string root)
        {
            var notes = new List<string>();
            MethodInfo capture = typeof(MainForm).GetMethod("CaptureView", PrivateInstance), restore = typeof(MainForm).GetMethod("RestoreView", PrivateInstance);
            const string all = "Cammy Scripted, Frame Meter, Hitbox Viewer, REFramework Nightly, ReShade Preset, Slotsloader Package";
            string file = Path.Combine(root, "view-code.json");
            foreach (Size logical in new[] { new Size(1110, 610), new Size(1460, 920) })
            using (Window w = Open(root, null, logical, CodeFixture()))
            {
                MainForm form = w.Form; Control host = w.Host; string ctx = w.Context;
                FilterPanel panel = Get<FilterPanel>(form, "filterPanel"); FighterGrid kinds = panel.Kinds;
                Func<string> order = delegate { return String.Join(",", panel.Body.Controls.Cast<Control>().Select(c => (c.AccessibleName ?? "").Split(' ')[0])); };
                form.ShowFilters(true, false); Settle(host);
                Func<string, int> tile = delegate(string code) { return kinds.Cards.Single(c => c.Code == code).Count; };
                FighterCard card = kinds.Cards[0];
                Require(card.Code == FilterPanel.ToolsKind && card.Name == "REFramework & code" && panel.KindsTitle.Text == "REFRAMEWORK & CODE, MISC, INSTALLED" && kinds.AccessibilityObject.GetChild(0).Name == "REFramework & code, 6 mods" && tile(FilterPanel.ToolsKind) == 6, ctx + ": tile " + card.Name + " / " + kinds.AccessibilityObject.GetChild(0).Name);
                // The label is two lines inside the name band, under the art and inside the card.
                string[] lines = kinds.NameLines(card); Font font = kinds.LinesFont(lines);
                int band = kinds.BandHeight(lines); Rectangle box = kinds.ItemBounds(0);
                Require(lines.SequenceEqual(new[] { "REFramework", "& code" }) && kinds.CardNameFits(card) && font.SizeInPoints >= 6.5F && band <= box.Height * 0.36, ctx + ": label " + String.Join(" / ", lines) + " in " + font.SizeInPoints + " pt, band " + band + " of " + box.Height);
                Require(kinds.Cards.All(kinds.CardNameFits), ctx + ": a tile name is cut");
                notes.Add(ctx + ": tile label " + String.Join(" / ", lines) + " at " + font.SizeInPoints + " pt in a " + kinds.CardWidth + " px card (band " + band + " px)");
                // Chips appear with the tile, under it and above the Misc chips, unclipped; none = every kind.
                Require(!panel.CodeChips.Visible && Listed(form).Count == 15, ctx + ": chips without the tile / list " + Listed(form).Count);
                Toggle(kinds, FilterPanel.ToolsKind); Toggle(kinds, FilterPanel.MiscKind); Settle(host);
                Require(panel.CodeChips.Visible && panel.MiscChips.Visible && In(panel.CodeChips, host).Top >= In(kinds, host).Bottom && In(panel.MiscChips, host).Top >= In(panel.CodeChips, host).Bottom, ctx + ": chip rows out of order: tiles " + In(kinds, host) + ", code " + In(panel.CodeChips, host) + ", misc " + In(panel.MiscChips, host) + ", order " + order());
                Rectangle chips = In(panel.CodeChips, host);
                foreach (Control chip in panel.CodeChips.Controls) { Rectangle r = In(chip, host); Require(r.Left >= chips.Left && r.Right <= chips.Right && r.Bottom <= chips.Bottom && r.Width >= TextRenderer.MeasureText(chip.Text, chip.Font).Width, ctx + ": chip " + chip.Text + " clipped " + r); }
                if (panel.VerticalScroll.Visible) { panel.ScrollControlIntoView(panel.CodeChips); Settle(host); Rectangle p = In(panel, host), c = In(panel.CodeChips, host); Require(c.Top >= p.Top && c.Bottom <= p.Bottom, ctx + ": the code chips cannot be scrolled into view " + c); }
                using (var bmp = new Bitmap(host.Width, host.Height)) { host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height)); bmp.Save(Path.Combine(root, "code-tile-" + logical.Width + "x" + logical.Height + ".png"), ImageFormat.Png); }
                panel.AutoScrollPosition = Point.Empty; Toggle(kinds, FilterPanel.MiscKind); Settle(host);
                Require(Names(form) == all && form.ActiveFilterCount() == 1, ctx + ": REFramework & code lists " + Names(form));
                Require(String.Join(", ", panel.CodeChips.Controls.Cast<Control>().Select(c => c.Text)) == "Scripts 2, Plugins 2, REFramework 2, Code injectors 2" && panel.CodeChips.Controls.Cast<Control>().All(c => c.AccessibleName.StartsWith("Code kind ")), ctx + ": chips " + String.Join(", ", panel.CodeChips.Controls.Cast<Control>().Select(c => c.Text)));
                CodeChip(panel, LibraryCode.Injector).PerformClick();
                Require(Names(form) == "ReShade Preset, Slotsloader Package" && tile(FilterPanel.ToolsKind) == 6 && form.ActiveFilterCount() == 1, ctx + ": Code injectors: " + Names(form));
                CodeChip(panel, LibraryCode.Script).PerformClick();
                Require(Names(form) == "Cammy Scripted, Hitbox Viewer, ReShade Preset, Slotsloader Package", ctx + ": Scripts + Code injectors (OR): " + Names(form));
                // Fighters narrow the tile and the chip counts follow them.
                panel.Grid.SetSelected(new[] { "009" }, true);
                Require(Names(form) == "Cammy Scripted" && tile(FilterPanel.ToolsKind) == 1 && CodeChip(panel, LibraryCode.Script).Text == "Scripts 1" && CodeChip(panel, LibraryCode.Plugin).Text == "Plugins 0", ctx + ": Cammy + chips: " + Names(form));
                panel.Grid.SetSelected(new string[0], true);
                // Saved and restored (the restored chips wait for the library, as at start-up).
                CodeChip(panel, LibraryCode.Script).PerformClick(); CodeChip(panel, LibraryCode.Injector).PerformClick(); CodeChip(panel, LibraryCode.Plugin).PerformClick();
                ViewPreferences saved = (ViewPreferences)capture.Invoke(form, null);
                Require(saved.Kinds.SequenceEqual(new[] { "tools" }) && saved.CodeKinds.SequenceEqual(new[] { "plugin" }), ctx + ": saved kinds " + String.Join(",", saved.Kinds) + " / chips " + String.Join(",", saved.CodeKinds));
                saved.Save(file);
                Get<FlatButton>(form, "filterClearButton").PerformClick();
                Require(panel.CodeSelected.Count == 0 && !panel.CodeChips.Visible && !panel.ToolsOn && Listed(form).Count == 15, ctx + ": Clear left a code chip");
            }
            ViewPreferences loaded = ViewPreferences.Load(file);
            Require(loaded.CodeKinds.SequenceEqual(new[] { "plugin" }) && File.ReadAllText(file).Contains("\"CodeKinds\":[\"plugin\"]"), "view file: " + File.ReadAllText(file));
            using (Window w = Open(root, null, new Size(1232, 640), null))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                restore.Invoke(form, new object[] { loaded });
                F("library").SetValue(form, CodeFixture()); Apply(form);
                Require(panel.ToolsOn && panel.CodeChips.Visible && panel.CodeSelected.SetEquals(new[] { "plugin" }) && Names(form) == "Frame Meter, Slotsloader Package", "restored chips: " + Names(form));
                ViewPreferences again = (ViewPreferences)capture.Invoke(form, null);
                Require(again.CodeKinds.SequenceEqual(new[] { "plugin" }), "round trip lost the chip");
            }
            // A view saved before 1.0.16 (Kinds ["tools"], no CodeKinds) is the whole tile; unknown chip kinds are dropped.
            File.WriteAllText(file, "{\"Format\":\"sf6-installer-view-v2\",\"FiltersOpen\":true,\"Kinds\":[\"tools\"],\"MiscAreas\":[]}");
            foreach (string text in new[] { File.ReadAllText(file), "{\"Format\":\"sf6-installer-view-v2\",\"FiltersOpen\":true,\"Kinds\":[\"tools\"],\"CodeKinds\":[\"dll\",\"nonsense\"]}" })
            {
                File.WriteAllText(file, text);
                using (Window w = Open(root, null, new Size(1232, 640), CodeFixture()))
                {
                    MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                    restore.Invoke(form, new object[] { ViewPreferences.Load(file) }); Apply(form);
                    Require(panel.ToolsOn && panel.CodeSelected.Count == 0 && Names(form) == all, "older view (" + text + "): " + Names(form));
                }
            }
            return String.Join("; ", notes);
        }
    }
}
