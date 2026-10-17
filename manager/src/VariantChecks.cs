using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.5.0 (--variants-check, test.ps1 suite "variants"): variant on/off for archives and loose multi-option folders
    // dropped into costume_mods, on a generated game folder. SlotsLoader's variants.json is written by the fixture.
    internal static class VariantChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Pack = "Luke/Luke Pack.zip", Loose = "Luke/Loose Pack", Other = "Other/Keep.zip";

        internal static int Run(string output)
        {
            string root = Path.Combine(Path.GetFullPath(output), "variants-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Safety.NoReparseChain(Path.GetFullPath(output)); Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action action)
            {
                try { action(); report.Add("PASS " + name); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
            };
            IProcessProbe previousProbe = GameProcesses.Probe; var probe = new FakeProcessProbe(); GameProcesses.Probe = probe;
            try
            {
                check("choices file: missing = all on; off writes only false (version 1) and keeps other archives' entries; on again removes the archive's entry; Restore puts an entry back exactly (round trip); temp file + rename (no .tmp left); the loader's spelling of the source", delegate { ChoicesFile(Path.Combine(root, "choices")); });
                check("choices file: a malformed file is read as all on and replaced by the next change; an unknown version is read as all on and never overwritten; a missing or broken variants.json shows nothing", delegate { Malformed(Path.Combine(root, "malformed")); });
                check("Installed listing: a manual archive and a loose folder with several variants get them; a single variant, a Manager-composed outfit and a disabled outfit don't; no variants.json with several Slotsloader outfits = an older Slotsloader", delegate { Listing(Path.Combine(root, "listing")); });
                check("CLI variants: lists archives, on/off and slots; --set off/on writes the file (also while SF6 runs, saying so); an unknown source or label is exit 1; bad arguments exit 2; no variants.json says Slotsloader is too old", delegate { CommandLineFlow(Path.Combine(root, "cli")); });
                check("Assistant list_installed: variants read-only (label, on, slot, applies_next_launch); the too-old note; no write tool", delegate { Assistant(Path.Combine(root, "assistant")); });
                check("Installed page: a dropped archive shows VARIANTS with its rows and slots; a click writes at once, the status says it applies at the next launch with Undo; Undo restores the file exactly; while SF6 runs it still writes and says it applies after a restart; a loose folder too; no toggles for a single variant or a managed outfit; an older Slotsloader shows one line; renders", delegate { Window(Path.Combine(root, "window"), probe); });
            }
            finally { GameProcesses.Probe = previousProbe; }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "variants-check.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            Console.WriteLine("Fixtures and renders: " + root);
            return failures == 0 ? 0 : 1;
        }

        // ---- Fixtures
        private static string CostumeRoot(string game) { return Path.Combine(game, "reframework", "costume_mods"); }
        private static object V(string label, bool enabled, string slot) { return new Dictionary<string, object> { { "label", label }, { "enabled", enabled }, { "slot", slot } }; }
        private static void WriteReport(string game, params object[] archives)
        {
            string file = VariantChoices.ReportFile(CostumeRoot(game)); Directory.CreateDirectory(Path.GetDirectoryName(file));
            Json.Write(file, new Dictionary<string, object> { { "generated", "2026-10-15 09:00" }, { "loader", "fixture-1.9.9" }, { "archives", archives } });
        }
        private static object A(string source, string fighter, params object[] variants) { return new Dictionary<string, object> { { "source", source }, { "fighter_dir", fighter }, { "variants", variants } }; }
        private static string Game(string root)
        {
            string game = Path.Combine(root, "game"), costume = CostumeRoot(game);
            Directory.CreateDirectory(Path.Combine(costume, "Luke", "Loose Pack", "01 Red", "natives"));
            File.WriteAllText(Path.Combine(costume, "Luke", "Loose Pack", "01 Red", "natives", "fixture.bin"), "loose");
            File.WriteAllText(Path.Combine(costume, "Luke", "Luke Pack.zip"), "opaque archive fixture");
            File.WriteAllText(Path.Combine(costume, "Luke", "Single.zip"), "opaque single fixture");
            string managed = Path.Combine(costume, "Luke", "Managed One"); Directory.CreateDirectory(Path.Combine(managed, "natives"));
            File.WriteAllText(Path.Combine(managed, "natives", "fixture.bin"), "managed bytes");
            Json.Write(Path.Combine(managed, ".slots-installer.json"), new InstallManifest { Id = Guid.NewGuid().ToString("N"), Character = "Luke", Name = "Managed One", SourcePath = "fixture-source.zip", CreatedUtc = DateTime.UtcNow.ToString("o"), SelectedIds = new List<string> { "main" }, Sha256 = new Dictionary<string, string> { { "natives/fixture.bin", new string('a', 64) } } });
            WriteReport(game,
                A(Pack, "esf002", V("Red", true, "Outfit V"), V("Blue", true, "Outfit VI"), V("Green", false, null)),
                A(Loose, "esf002", V("01 Red", true, "Outfit VII"), V("02 Black", true, "Outfit VIII")),
                A("Luke/Single.zip", "esf002", V("Only", true, "Outfit IX")),
                A("Luke/Managed One", "esf002", V("X", true, "Outfit X"), V("Y", true, "Outfit XI")));
            return game;
        }
        private static string Text(string game) { return File.ReadAllText(VariantChoices.ChoicesFile(CostumeRoot(game))); }
        private static void WriteChoices(string game, string json) { string f = VariantChoices.ChoicesFile(CostumeRoot(game)); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, json); }
        private static Dictionary<string, object> Parsed(string game) { return Json.Serializer().Deserialize<Dictionary<string, object>>(Text(game)); }
        private static Dictionary<string, object> Entry(string game, string source)
        {
            var archives = (Dictionary<string, object>)Parsed(game)["archives"]; object e;
            return archives.TryGetValue(source, out e) ? (Dictionary<string, object>)e : null;
        }

        // ---- Backend
        private static void ChoicesFile(string root)
        {
            string game = Game(root), costume = CostumeRoot(game);
            VariantReport report = VariantChoices.ReadReport(costume);
            Require(report.Present && report.Archives.Count == 4 && report.Loader == "fixture-1.9.9", "report: " + report.Archives.Count);
            ArchiveVariants pack = report.Find("luke\\luke pack.zip");
            Require(pack != null && pack.Variants.Count == 3 && pack.Variants[2].Slot == "" && !pack.Variants[2].Enabled && pack.Variants[0].Slot == "Outfit V", "report parsing");
            VariantChoiceFile none = VariantChoices.ReadChoices(costume);
            Require(!none.Malformed && none.Archives.Count == 0 && VariantChoices.States(pack, none).All(v => v.On), "missing file is not all on");
            Require(VariantChoices.States(pack, none).Single(v => v.Label == "Green").Pending, "Green (built off, default on) should apply at the next launch");
            WriteChoices(game, "{\"version\":1,\"archives\":{\"" + Other + "\":{\"Z\":false,\"W\":true}}}");
            VariantChoices.Set(costume, pack, "Blue", false);
            var e = Entry(game, Pack);
            Require(e != null && e.Count == 1 && false.Equals(e["Blue"]) && (int)Parsed(game)["version"] == 1, "off not written: " + Text(game));
            var other = Entry(game, Other); Require(other != null && false.Equals(other["Z"]) && true.Equals(other["W"]), "other archive's entry lost: " + Text(game));
            Require(!VariantChoices.ReadChoices(costume).IsOn("Luke/Luke Pack.zip", "Blue") && VariantChoices.ReadChoices(costume).IsOn("Luke/Luke Pack.zip", "Red"), "read back");
            Dictionary<string, bool> before = VariantChoices.Entry(costume, Pack);
            VariantChoices.Set(costume, pack, "Red", false);
            Require(Entry(game, Pack).Count == 2, "second off");
            VariantChoices.Restore(costume, Pack, before);
            Require(Entry(game, Pack).Count == 1 && false.Equals(Entry(game, Pack)["Blue"]), "Restore round trip: " + Text(game));
            VariantChoices.Set(costume, pack, "Blue", true);
            Require(Entry(game, Pack) == null && Entry(game, Other) != null, "all on: the entry stays: " + Text(game));
            VariantChoices.Restore(costume, Pack, null);
            Require(Entry(game, Pack) == null && Entry(game, Other).Count == 2, "Restore(null)");
            // A differently cased key in the file is replaced by the loader's spelling, never duplicated.
            WriteChoices(game, "{\"version\":1,\"archives\":{\"luke/LUKE PACK.zip\":{\"Green\":false}}}");
            VariantChoices.Set(costume, pack, "Red", false);
            var keys = ((Dictionary<string, object>)Parsed(game)["archives"]).Keys.ToList();
            Require(keys.Count == 1 && keys[0] == Pack && Entry(game, Pack).Count == 2, "source spelling: " + Text(game));
            Require(!Directory.GetFiles(VariantChoices.DataFolder(costume), "*.tmp").Any(), "a temporary file was left");
            Refused(delegate { VariantChoices.Set(costume, pack, "Purple", false); }, "Purple");
        }

        private static void Malformed(string root)
        {
            string game = Game(root), costume = CostumeRoot(game);
            ArchiveVariants pack = VariantChoices.ReadReport(costume).Find(Pack);
            WriteChoices(game, "{ broken");
            VariantChoiceFile broken = VariantChoices.ReadChoices(costume);
            Require(broken.Malformed && VariantChoices.States(pack, broken).All(v => v.On), "malformed not read as all on");
            VariantChoices.Set(costume, pack, "Red", false);
            Require(Entry(game, Pack).Count == 1 && !VariantChoices.ReadChoices(costume).Malformed, "malformed not replaced: " + Text(game));
            WriteChoices(game, "{\"version\":2,\"archives\":{\"" + Pack + "\":{\"Red\":false}}}");
            Require(VariantChoices.ReadChoices(costume).IsOn(Pack, "Red"), "unknown version applied");
            Refused(delegate { VariantChoices.Set(costume, pack, "Blue", false); }, "newer version");
            Require(Text(game).Contains("\"version\":2"), "a newer file was overwritten");
            WriteChoices(game, "{\"version\":1,\"archives\":{\"" + Pack + "\":{\"Red\":\"no\",\"Blue\":false}}}");
            VariantChoiceFile odd = VariantChoices.ReadChoices(costume);
            Require(odd.IsOn(Pack, "Red") && !odd.IsOn(Pack, "Blue"), "non-bool values must be ignored");
            File.Delete(VariantChoices.ReportFile(costume));
            Require(!VariantChoices.ReadReport(costume).Present, "missing report");
            File.WriteAllText(VariantChoices.ReportFile(costume), "{ broken");
            VariantReport bad = VariantChoices.ReadReport(costume);
            Require(!bad.Present && bad.Archives.Count == 0 && bad.Error != null, "broken report");
        }

        private static void Listing(string root)
        {
            string game = Game(root), costume = CostumeRoot(game);
            var disabled = Path.Combine(costume, "Luke", "Disabled.zip"); File.WriteAllText(disabled, "x");
            List<InstalledOutfit> listed = Installer.ListInstalled(costume);
            Func<string, InstalledOutfit> row = delegate(string leaf) { return listed.Single(o => Path.GetFileName(o.Folder) == leaf); };
            Require(row("Luke Pack.zip").Variants != null && row("Luke Pack.zip").Variants.Variants.Count == 3, "archive variants");
            Require(row("Loose Pack").Variants != null && row("Loose Pack").Variants.Variants.Count == 2, "loose folder variants");
            Require(row("Single.zip").Variants == null && !row("Single.zip").VariantsUnsupported, "single variant got toggles");
            Require(row("Managed One").Managed && row("Managed One").Variants == null, "a Manager-composed outfit got toggles");
            Require(row("Disabled.zip").Variants == null, "no report entry");
            // An older SlotsLoader: outfits.json maps two outfits to the archive, no variants.json.
            File.Delete(VariantChoices.ReportFile(costume));
            Json.Write(Path.Combine(VariantChoices.DataFolder(costume), "outfits.json"), new { generated = "2026-10-15 09:00", loader = "1.9.8", outfits = new[] {
                new { source = Pack, name = "Outfit V", label = "Red", fighter = 2, fighter_dir = "esf002" }, new { source = Pack, name = "Outfit VI", label = "Blue", fighter = 2, fighter_dir = "esf002" },
                new { source = "Luke/Single.zip", name = "Outfit IX", label = "Only", fighter = 2, fighter_dir = "esf002" } }, left_out = new object[0] });
            listed = Installer.ListInstalled(costume);
            Require(row("Luke Pack.zip").Variants == null && row("Luke Pack.zip").VariantsUnsupported, "older Slotsloader not noted");
            Require(!row("Single.zip").VariantsUnsupported && !row("Managed One").VariantsUnsupported, "older-Slotsloader note on a single outfit");
        }

        private static int Cli(out string text, params string[] args)
        {
            var writer = new StringWriter(); var error = new StringWriter(); TextWriter oldOut = Console.Out, oldErr = Console.Error;
            Console.SetOut(writer); Console.SetError(error);
            try { return CommandLine.Run(args); }
            finally { Console.SetOut(oldOut); Console.SetError(oldErr); text = writer.ToString() + error.ToString(); }
        }
        private static void CommandLineFlow(string root)
        {
            string game = Game(root), costume = CostumeRoot(game), text;
            Require(CommandLine.Handles("variants"), "the CLI does not know variants");
            Require(Cli(out text, "variants", "--game", game) == 0 && text.Contains("Luke/Luke Pack.zip  esf002") && text.Contains("[on ] Red  · Outfit V") && text.Contains("[on ] Green  · On at next launch"), "list: " + text);
            Require(Cli(out text, "variants", "--game", game, "--set", Pack, "Blue", "off") == 0 && text.Contains("'Blue' off for Luke/Luke Pack.zip · applies at next launch") && !VariantChoices.ReadChoices(costume).IsOn(Pack, "Blue"), "set off: " + text);
            Require(Cli(out text, "variants", "--game", game) == 0 && text.Contains("[off] Blue  · Off at next launch"), "list after set: " + text);
            probe(true);
            try { Require(Cli(out text, "variants", "--game", game, "--set", "luke\\luke pack.zip", "blue", "ON") == 0 && text.Contains("applies after SF6 restarts") && VariantChoices.ReadChoices(costume).Find(Pack) == null, "set on while SF6 runs: " + text); }
            finally { probe(false); }
            Require(Cli(out text, "variants", "--game", game, "--set", "Luke/Nope.zip", "Red", "off") == 1 && text.Contains("lists no archive"), "unknown source: " + text);
            Require(Cli(out text, "variants", "--game", game, "--set", Pack, "Purple", "off") == 1 && text.Contains("no variant 'Purple'"), "unknown label: " + text);
            Require(Cli(out text, "variants", "--game", game, "--set", Pack, "Red") == 2, "missing value: " + text);
            Require(Cli(out text, "variants", "--game", game, "--set", Pack, "Red", "maybe") == 2, "bad value: " + text);
            File.Delete(VariantChoices.ReportFile(costume));
            Require(Cli(out text, "variants", "--game", game) == 0 && text.Contains("too old"), "too old: " + text);
            Require(Cli(out text, "variants", "--game", game, "--set", Pack, "Red", "off") == 1, "set without a report");
        }
        private static void probe(bool running) { var p = GameProcesses.Probe as FakeProcessProbe; if (p != null) p.Game = running; }

        private static void Assistant(string root)
        {
            string game = Game(root), costume = CostumeRoot(game);
            VariantChoices.Set(costume, VariantChoices.ReadReport(costume).Find(Pack), "Blue", false);
            var ws = new AssistantWorkspace(new AppConfig { GameRoot = game, Libraries = new List<string>(), SevenZipPath = "" }, new AssistantSettings());
            var tools = new AssistantToolbox(ws);
            ToolOutcome result = tools.Execute("list_installed", new Dictionary<string, object> { { "character", "" } });
            string content = AssistantJson.Write(result.Content);
            Require(!result.IsError, "list_installed failed: " + content);
            var data = Json.Serializer().Deserialize<Dictionary<string, object>>(content);
            var rows = NativeModCheck.Rows(data, "outfits");
            var pack = rows.Single(r => NativeModCheck.Value(r, "name").StartsWith("Luke Pack"));
            var variants = NativeModCheck.Rows(pack, "variants");
            Require(variants.Count == 3, "variants missing: " + content);
            var blue = variants.Single(v => NativeModCheck.Value(v, "label") == "Blue"); var red = variants.Single(v => NativeModCheck.Value(v, "label") == "Red");
            Require(false.Equals(blue["on"]) && true.Equals(blue["applies_next_launch"]) && NativeModCheck.Value(blue, "slot") == "", "Blue row: " + content);
            Require(true.Equals(red["on"]) && NativeModCheck.Value(red, "slot") == "Outfit V", "Red row: " + content);
            Require(!rows.Single(r => NativeModCheck.Value(r, "name") == "Managed One").ContainsKey("variants"), "managed outfit lists variants");
            Require(!new AssistantToolbox(ws).Specs.Any(s => s.Name.IndexOf("variant", StringComparison.OrdinalIgnoreCase) >= 0), "a variant write tool exists");
            File.Delete(VariantChoices.ReportFile(costume));
            Json.Write(Path.Combine(VariantChoices.DataFolder(costume), "outfits.json"), new { generated = "x", loader = "1.9.8", outfits = new[] { new { source = Pack, name = "Outfit V", label = "Red" }, new { source = Pack, name = "Outfit VI", label = "Blue" } } });
            content = AssistantJson.Write(tools.Execute("list_installed", new Dictionary<string, object> { { "character", "" } }).Content);
            Require(content.Contains(MainForm.VariantsTooOld), "too-old note missing: " + content);
        }

        // ---- The Installed page in a hidden window
        private static void Window(string root, FakeProcessProbe probe)
        {
            string game = Game(root), costume = CostumeRoot(game);
            WriteChoices(game, "{\"version\":1,\"archives\":{\"" + Pack + "\":{\"Green\":false},\"" + Other + "\":{\"Z\":false}}}");
            string initial = Text(game);
            SynchronizationContext previous = SynchronizationContext.Current; bool previousAuto = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false; bool previousHeadless = Program.Headless; Program.Headless = true;
            try
            {
                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    var config = new AppConfig { GameRoot = game, Libraries = new List<string>(), SevenZipPath = "" };
                    using (var form = new MainForm(config))
                    {
                        form.PortraitCacheDirectory = Path.Combine(root, "portraits");
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                        var list = Get<ListView>(form, "installedList"); Panel panel = Get<Panel>(form, "variantsPanel"); VariantList rows = form.VariantListForTest;
                        Func<string, Label> caption = delegate(string n) { return Get<Label>(form, n); };
                        Action<string> select = delegate(string leaf)
                        {
                            foreach (ListViewItem i in list.Items) i.Selected = false;
                            list.Items.Cast<ListViewItem>().First(i => i.Tag is InstalledOutfit && Path.GetFileName(((InstalledOutfit)i.Tag).Folder) == leaf).Selected = true;
                            Application.DoEvents(); Call(form, "ShowInstalledSelection"); Application.DoEvents();
                        };
                        Func<string> status = delegate { return StatusText(form); };
                        select("Luke Pack.zip");
                        Require(panel.Visible && caption("variantsCaption").Text == "VARIANTS · 2 OF 3 ON" && rows.Rows.Count == 3, "panel: " + caption("variantsCaption").Text);
                        Require(rows.Rows[0].Note == "Outfit V" && rows.Rows[1].Note == "Outfit VI" && rows.Rows[2].Note == "" && !rows.Rows[2].On, "rows: " + String.Join("|", rows.Rows.Select(r => r.Label + ":" + r.Note)));
                        Require(panel.Height == DpiMetrics.Px(form, 34) + 3 * rows.RowHeight, "panel height " + panel.Height);
                        Require(UiAudit.ToolTipFindings(form).Count == 0, "a tooltip");
                        Render(form, Path.Combine(root, "installed-variants-" + form.DeviceDpi + ".png"));
                        // A click: written at once, inline status, Undo.
                        rows.Toggle(1);
                        Require(Entry(game, Pack).Count == 2 && false.Equals(Entry(game, Pack)["Blue"]) && Entry(game, Other) != null, "click not written: " + Text(game));
                        Require(status() == "Blue off · applies at next launch" && form.UndoAvailable && form.UndoLabel == "Blue off", "status: " + status());
                        Require(rows.Rows[1].Note == "Off at next launch" && rows.Rows[1].NoteAccent && caption("variantsCaption").Text == "VARIANTS · 1 OF 3 ON", "row after click");
                        Render(form, Path.Combine(root, "installed-variants-changed-" + form.DeviceDpi + ".png"));
                        form.PerformUndo(); Application.DoEvents();
                        Require(Text(game) == initial || (Entry(game, Pack).Count == 1 && false.Equals(Entry(game, Pack)["Green"])), "Undo: " + Text(game));
                        Require(status() == "✓ Undone: Blue off." && rows.Rows[1].On && !form.UndoAvailable, "after Undo: " + status());
                        // Back on: the archive's entry goes when every variant is on again.
                        rows.Toggle(2);
                        Require(Entry(game, Pack) == null && Entry(game, Other) != null && rows.Rows[2].Note == "On at next launch", "all on: " + Text(game));
                        // While SF6 runs: still written, applies after a restart.
                        probe.Game = true;
                        try
                        {
                            rows.Toggle(0);
                            Require(!VariantChoices.ReadChoices(costume).IsOn(Pack, "Red") && status() == "Red off · applies after SF6 restarts", "running: " + status());
                        }
                        finally { probe.Game = false; }
                        // Keyboard: Down + Space toggles the next row.
                        rows.Focus(); Application.DoEvents();
                        Call(rows, "OnKeyDown", new KeyEventArgs(Keys.Down)); Call(rows, "OnKeyDown", new KeyEventArgs(Keys.Space));
                        Require(!VariantChoices.ReadChoices(costume).IsOn(Pack, "Blue"), "Space did not toggle");
                        Require(rows.AccessibilityObject.GetChildCount() == 3 && rows.AccessibilityObject.GetChild(0).Role == AccessibleRole.CheckButton && (rows.AccessibilityObject.GetChild(0).State & AccessibleStates.Checked) == 0, "accessibility");
                        select("Loose Pack");
                        Require(panel.Visible && rows.Rows.Count == 2 && rows.Rows[1].Label == "02 Black", "loose folder");
                        rows.Toggle(1); Require(!VariantChoices.ReadChoices(costume).IsOn(Loose, "02 Black"), "loose folder not written");
                        select("Single.zip"); Require(!panel.Visible, "single variant shows toggles");
                        select("Managed One"); Require(!panel.Visible, "managed outfit shows toggles");
                        // An older SlotsLoader: one line, no toggles.
                        File.Delete(VariantChoices.ReportFile(costume));
                        Json.Write(Path.Combine(VariantChoices.DataFolder(costume), "outfits.json"), new { generated = "2026-10-15 09:00", loader = "1.9.8", outfits = new[] { new { source = Pack, name = "Outfit V", label = "Red", fighter_dir = "esf002" }, new { source = Pack, name = "Outfit VI", label = "Blue", fighter_dir = "esf002" } } });
                        Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                        select("Luke Pack.zip");
                        Require(panel.Visible && !rows.Visible && Get<Label>(form, "variantsNote").Visible && Get<Label>(form, "variantsNote").Text == MainForm.VariantsTooOld, "older Slotsloader line");
                        Render(form, Path.Combine(root, "installed-variants-old-loader-" + form.DeviceDpi + ".png"));
                        select("Loose Pack"); Require(!panel.Visible, "a folder Slotsloader didn't split shows the line");
                        form.Close();
                    }
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAuto; Program.Headless = previousHeadless; }
        }
        private static string StatusText(MainForm form) { object s = form.GetType().GetField("statusText", PrivateInstance).GetValue(form); return (string)s.GetType().GetProperty("Text").GetValue(s, null); }

        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).First(x => x.Name == name && x.GetParameters().Length == args.Length);
            return m.Invoke(o, args);
        }
        private static void WaitIdle(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(10); if (!form.Working) { Application.DoEvents(); if (!form.Working) return; } } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("Background work did not finish.");
        }
        private static void Render(MainForm form, string file)
        {
            form.PerformLayout(); Application.DoEvents();
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, ImageFormat.Png); }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Refused(Action action, string contains)
        {
            try { action(); }
            catch (Exception ex) { if (ex.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return; throw new InvalidOperationException("Refused with an unexpected message: " + ex.Message); }
            throw new InvalidOperationException("Expected a refusal mentioning '" + contains + "'.");
        }
    }
}
