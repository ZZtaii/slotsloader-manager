using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.5.1 (--wording-check, test.ps1 suite "wording"): nothing the user sees names another product. The app says
    // "external mod manager". Covers the string literals of the app, the CLI help and output, the Assistant's prompt,
    // knowledge pack, tool definitions and results, and the window's texts with an external manager's install present.
    // The docs are checked at build time (tools\Check-Wording.ps1).
    internal static class WordingChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        internal const string Word = "fluffy";

        internal static int Run(string output)
        {
            string root = Path.Combine(Path.GetFullPath(output), "wording-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Safety.NoReparseChain(Path.GetFullPath(output)); Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action action)
            {
                try { action(); report.Add("PASS " + name); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
            };
            // A game folder and an external mod manager folder (named neutrally: paths are data) next to a library.
            string game = Path.Combine(root, "game"), manager = Path.Combine(root, "ModManager", "Games", "SF6"), mods = Path.Combine(manager, "Mods");
            Directory.CreateDirectory(Path.Combine(game, "reframework", "costume_mods")); Directory.CreateDirectory(mods);
            File.WriteAllText(Path.Combine(game, "StreetFighter6.exe"), "exe");
            File.WriteAllText(Path.Combine(manager, "installed.ini"), "[Sakura Pack]\r\nModName=Sakura Pack\r\nfile=natives/stm/product/model/esf/esf009/002/01/sakura.mesh.230110883\r\n");
            File.WriteAllText(Path.Combine(manager, "Sakura Preset.prt"), "[Sakura Pack]\r\n");
            var config = new AppConfig { GameRoot = game, Libraries = new List<string> { mods }, SevenZipPath = "" };
            IProcessProbe previousProbe = GameProcesses.Probe; var probe = new FakeProcessProbe { Fluffy = true }; GameProcesses.Probe = probe;
            try
            {
                check("string literals: no app string outside the allow-list (detection names, persisted Data values, the internal row kind, old CLI/Assistant aliases, fixture sample data) names the other product", delegate
                {
                    int scanned; List<string> found = UiAudit.LiteralFindings(Word, out scanned);
                    Require(scanned > 500 && found.Count == 0, "literals: " + String.Join(" | ", found));
                });
                check("names: the app is Slotsloader Manager and the loader Slotsloader; no app string spells them the old ways (SlotsLoader, Slotloader); allowed: the old loader zip names (SF6-SlotsLoader, SF6_SlotsLoader_v*), the DPAPI id, the Assistant's 'previously …' note", delegate
                {
                    var allowed = new System.Text.RegularExpressions.Regex(@"SF6[ _-]SlotsLoader|previously SlotsLoader Manager|called SlotsLoader before");
                    int scanned; List<string> found = UiAudit.LiteralFindings("SlotsLoader", out scanned).Where(t => allowed.Replace(t.Substring(t.IndexOf(": ", StringComparison.Ordinal) + 2), "").IndexOf("SlotsLoader", StringComparison.Ordinal) >= 0).ToList();   // the text only, not the "Type.Method: " it was found in
                    // 1.0.1: the loader is Slotsloader too; "Slotloader" (internal 2.5.9 and 1.0.0) must not come back.
                    found.AddRange(UiAudit.LiteralFindings("Slotloader", out scanned));
                    Require(scanned > 500 && found.Count == 0, "old spelling: " + String.Join(" | ", found));
                });
                check("CLI: --help has no old name and lists manager-status/-uninstall/-restore; fluffy-* still run as aliases (same output); manager-status output is clean", delegate { CommandLineText(game); });
                check("Assistant: system prompt + knowledge pack, every tool definition, and get_app_status / list_game_mods / list_history results with an external manager install and preset present", delegate { Assistant(config); });
                check("window: every page's texts (Mod library, Installed with the external manager's row selected and with the Installed type filter, both Settings tabs, History), menus and combo items while the external manager runs", delegate { Window(config, root); });
            }
            catch (Exception ex) { failures++; report.Add("FAIL setup: " + ex); }
            finally { GameProcesses.Probe = previousProbe; }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "wording-check.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            return failures == 0 ? 0 : 1;
        }

        // Paths are data (a Data folder's persisted fluffy-removed records, the user's own folders): left out.
        private static bool Clean(string text) { return System.Text.RegularExpressions.Regex.Replace(text ?? "", @"[A-Za-z]:\\[^·|\r\n]*", "").IndexOf(Word, StringComparison.OrdinalIgnoreCase) < 0; }
        private static string Lines(string text) { return String.Join(" | ", (text ?? "").Split('\n').Where(l => !Clean(l)).Select(l => l.Trim()).Take(10)); }

        private static int Cli(out string text, params string[] args)
        {
            var writer = new StringWriter(); TextWriter oldOut = Console.Out, oldErr = Console.Error;
            Console.SetOut(writer); Console.SetError(writer);
            try { return CommandLine.Run(args); }
            finally { Console.SetOut(oldOut); Console.SetError(oldErr); text = writer.ToString(); }
        }
        private static void CommandLineText(string game)
        {
            string help, status, alias;
            Cli(out help, "--help");
            Require(Clean(help), "help: " + Lines(help));
            Require(help.Contains("manager-status") && help.Contains("manager-uninstall") && help.Contains("manager-restore"), "help lacks the manager-* commands");
            int a = Cli(out status, "manager-status", "--game", game), b = Cli(out alias, "fluffy-status", "--game", game);
            Require(a == 0 && b == 0 && status == alias && Clean(status), "manager-status: " + a + "/" + b + " " + Lines(status));
            string missing;
            Require(Cli(out missing, "manager-uninstall") == 2 && Clean(missing) && missing.Contains("manager-status"), "manager-uninstall usage: " + missing);
            Require(Cli(out missing, "fluffy-uninstall") == 2 && Clean(missing), "alias usage: " + missing);
        }

        private static void Assistant(AppConfig config)
        {
            Require(Clean(AssistantPrompt.SystemJson()), "system prompt / knowledge: " + Lines(AssistantPrompt.SystemJson()));
            var ws = new AssistantWorkspace(config, new AssistantSettings());
            var tools = new AssistantToolbox(ws);
            foreach (ToolSpec spec in tools.Specs) { string json = AssistantJson.Write(spec.Definition()); Require(Clean(json), spec.Name + ": " + json); }
            foreach (string name in new[] { "get_app_status", "list_game_mods" })
            {
                ToolOutcome result = tools.Execute(name, new Dictionary<string, object>());
                string json = result.Content is JsonObj ? AssistantJson.Write((JsonObj)result.Content) : Convert.ToString(result.Content);
                Require(!result.IsError && Clean(json) && Clean(result.Activity), name + ": " + json + " / " + result.Activity);
                if (name == "list_game_mods") Require(json.Contains("external_manager_mods") && json.Contains("Sakura Pack") && json.Contains("external_presets"), "list_game_mods keys: " + json);
            }
        }

        private static void Window(AppConfig config, string root)
        {
            SynchronizationContext previous = SynchronizationContext.Current; bool previousAuto = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false; bool previousHeadless = Program.Headless; Program.Headless = true;
            bool previousRefresh = MainForm.FluffyAutoRefresh; MainForm.FluffyAutoRefresh = false;
            try
            {
                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    using (var form = new MainForm(config))
                    {
                        form.PortraitCacheDirectory = Path.Combine(root, "portraits");
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        var found = new List<string>();
                        Action<string> collect = delegate(string where) { Application.DoEvents(); found.AddRange(UiAudit.TextFindings(form, Word).Select(f => where + " · " + f)); };
                        Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); WaitIdle(form); collect("library");
                        Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                        var list = Get<ListView>(form, "installedList");
                        ListViewItem external = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is GameModRow && ((GameModRow)i.Tag).Fluffy != null);
                        Require(external != null, "the external manager's row is missing");
                        foreach (ListViewItem i in list.Items) i.Selected = false; external.Selected = true; Application.DoEvents(); Call(form, "ShowInstalledSelection");
                        collect("installed (external row)");
                        // 1.0.1: the type filter is a segmented All / Extra / Installed toggle; Installed holds the external rows.
                        ReplaceUiChecks.Kind(form, "Installed");
                        Require(list.Items.Count > 0 && list.Items.Cast<ListViewItem>().All(i => i.Tag is GameModRow) && list.Items.Cast<ListViewItem>().Any(i => ((GameModRow)i.Tag).Fluffy != null), "the Installed filter");
                        collect("installed (Installed filter)");
                        ReplaceUiChecks.Kind(form, "All");
                        Call(form, "ShowPage", Get<Panel>(form, "settingsPage")); Call(form, "ShowSettingsTab", true); collect("settings (game files)");
                        Call(form, "ShowSettingsTab", false); collect("settings (general)");
                        Call(form, "ShowPage", Get<Panel>(form, "libraryPage"));
                        try { Call(form, "ShowHistory"); WaitIdle(form); collect("history"); } catch (MissingMethodException) { }
                        Require(found.Count == 0, String.Join(" | ", found.Distinct().Take(15)));
                        form.Close();
                    }
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAuto; Program.Headless = previousHeadless; MainForm.FluffyAutoRefresh = previousRefresh; }
        }

        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m == null) throw new MissingMethodException(name);
            return m.Invoke(o, args);
        }
        private static void WaitIdle(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(10); if (!form.Working) { Application.DoEvents(); if (!form.Working) return; } } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("Background work did not finish.");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
