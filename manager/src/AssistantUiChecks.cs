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
    // Hidden-window checks for the Assistant tab and the Settings Claude section,
    // driven by the scripted mock conversation on generated fixtures.
    internal static class AssistantUiChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string UiKey = "sk-ant-ui-check-fake-key-1234ABCD";

        internal static int Run(string fixtureRoot)
        {
            string root = Path.Combine(Path.GetFullPath(fixtureRoot), "assistant-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var report = new List<string>();
            bool previousHeadless = Program.Headless; Program.Headless = true;
            SynchronizationContext previous = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            int failures = 0;
            Action<string, Action> check = delegate(string name, Action action)
            {
                try { action(); report.Add("PASS: " + name); }
                catch (Exception ex) { failures++; report.Add("FAIL: " + name + ": " + ex); }
            };
            try
            {
                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    var config = new AppConfig { GameRoot = Path.Combine(root, "no-game"), Libraries = new List<string>(), SevenZipPath = "" };
                    // 2.4.1: the "Show Assistant tab" option first, while nothing in this process has built a page.
                    TabDefaults(check, root);
                    HiddenTab(check, config, root);
                    using (var form = new MainForm(config))
                    {
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        report.Add("Hidden native window at " + form.DeviceDpi + " DPI; generated fixtures under " + root + "; mock Messages API on 127.0.0.1.");
                        AssistantPage page = null;
                        AssistantDemo demo = null;
                        check("the Assistant page is built on its first show, not with the window", delegate { Require(Get<AssistantPage>(form, "assistantPage") == null, "The page was built at startup."); });
                        check("Assistant tab sits between Installed outfits and the gear, opens with Ctrl+3 and its nav tab stays inside the header", delegate { page = Navigation(form); });
                        check("empty state explains the Assistant and the key status", delegate { EmptyState(form, page); });
                        check("scripted conversation shows tool activity, thinking, no image thumbnail and a Plan with queued installs; switchers lock during the run", delegate { demo = AssistantDemo.Attach(page, Path.Combine(root, "demo")); demo.FirstReplyDelayMs = 700; Conversation(form, page, demo); });
                        foreach (Size size in new[] { new Size(1110, 610), new Size(1232, 640), new Size(1460, 920) })
                        {
                            Size s = size;
                            check("Assistant layout fits at " + s.Width + "x" + s.Height + " logical (no clipped or overlapping controls)", delegate { Layout(form, page, s, root); });
                        }
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        check("switching model mid-conversation adds a cache hint, keeps history and saves the last-used selection", delegate { Switch(form, page); });
                        check("Discard and Apply selected run through the window's busy lock and report results; the next message carries the [App] note", delegate { ApplyAndNote(form, page, demo); });
                        check("Stop cancels an in-flight request promptly and the page recovers", delegate { StopRequest(form, page, demo); });
                        check("auto-apply is per session with a visible warning", delegate { AutoApplyToggle(page); });
                        check("New chat clears the transcript and the Plan", delegate { NewChat(page); });
                        check("Settings Claude section: masked key field, Save/Remove with masked status, defaults saved, no overlap with the library list", delegate { Settings(form, page, root); });
                        if (demo != null) demo.Dispose();
                        check("no file written by the checks contains a test key", delegate
                        {
                            var hits = AssistantChecks.FilesContaining(root, UiKey).Concat(AssistantChecks.FilesContaining(root, AssistantDemo.DemoKey)).ToList();
                            if (Directory.Exists(AppConfig.DataDir)) hits.AddRange(AssistantChecks.FilesContaining(AppConfig.DataDir, UiKey));
                            Require(hits.Count == 0, "Key found in " + String.Join(", ", hits.Select(Path.GetFileName)));
                        });
                        form.Close();
                    }
                }
            }
            catch (Exception error) { failures++; report.Add("FAIL: " + error); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
                Program.Headless = previousHeadless;
            }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "assistant-ui-checks.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            return failures == 0 ? 0 : 1;
        }

        // 2.4.1 "Show Assistant tab": the default is decided once per Data folder (generated folders here; the
        // harness Data already holds settings.json, so every suite runs as an update with the tab shown).
        private static void TabDefaults(Action<string, Action> check, string root)
        {
            check("a new install (empty Data folder) hides the Assistant tab and stores that once, leaving model and effort on their defaults", delegate
            {
                string data = Path.Combine(root, "data-new-install"); Directory.CreateDirectory(data);
                var settings = AssistantSettings.Load(data);
                Require(settings.ShowTab == null, "A missing file already has a decision.");
                Require(!AssistantSettings.ResolveShowTab(data, settings) && settings.ShowTab == false, "A new install shows the tab.");
                string text = File.ReadAllText(Path.Combine(data, AssistantSettings.FileName));
                Require(text == "{\"show_tab\":false}", "Stored decision: " + text);
                var again = AssistantSettings.Load(data);
                Require(again.ShowTab == false && again.Model == AssistantModels.DefaultModel && again.Effort == AssistantModels.DefaultEffort && !again.AutoApply, "Reloaded settings differ.");
                // Decided once: files a later run leaves (settings.json after Save settings) do not decide again.
                File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
                Require(!AssistantSettings.ResolveShowTab(data, AssistantSettings.Load(data)), "The stored decision was decided again.");
            });
            check("an update (settings.json without the new key) shows the Assistant tab, stores that and keeps every other setting", delegate
            {
                string data = Path.Combine(root, "data-update"); Directory.CreateDirectory(data);
                string settingsJson = "{\"GameRoot\":\"C:\\\\SF6\",\"Libraries\":[],\"SevenZipPath\":\"\"}";
                File.WriteAllText(Path.Combine(data, "settings.json"), settingsJson);
                var settings = AssistantSettings.Load(data);
                Require(settings.ShowTab == null && AssistantSettings.ResolveShowTab(data, settings) && settings.ShowTab == true, "An update hides the tab.");
                Require(AssistantSettings.Load(data).ShowTab == true, "The decision was not stored.");
                Require(File.ReadAllText(Path.Combine(data, "settings.json")) == settingsJson, "settings.json changed.");
                string other = Path.Combine(root, "data-update-assistant"); Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, AssistantSettings.FileName), "{\"model\":\"claude-sonnet-5-5\",\"effort\":\"medium\",\"auto_apply\":true}");
                Require(AssistantSettings.ResolveShowTab(other, AssistantSettings.Load(other)), "An earlier Assistant settings file hides the tab.");
                var kept = AssistantSettings.Load(other);
                Require(kept.ShowTab == true && kept.Model == "claude-sonnet-5-5" && kept.Effort == "medium" && kept.AutoApply, "The earlier Assistant settings were lost.");
                kept.ShowTab = false; kept.Save(other);
                string text = File.ReadAllText(Path.Combine(other, AssistantSettings.FileName));
                Require(text == "{\"model\":\"claude-sonnet-5-5\",\"effort\":\"medium\",\"auto_apply\":true,\"show_tab\":false}", "Save format: " + text);
            });
        }

        private static readonly string[] ClaudeDetailFields = { "settingsApiKey", "saveKeyButton", "removeKeyButton", "testKeyButton", "settingsKeyStatus", "settingsModel", "settingsEffort", "settingsAutoApply", "settingsClaudeStatus" };

        // A window that starts with the tab off (the harness Data's choice is restored afterwards).
        private static void HiddenTab(Action<string, Action> check, AppConfig config, string root)
        {
            string data = AppConfig.DataDir, file = Path.Combine(data, AssistantSettings.FileName);
            string before = File.Exists(file) ? File.ReadAllText(file) : null;
            var keys = new AssistantKeyStore(data);
            bool hadKey = keys.HasSavedKey;
            FieldInfo knowledge = typeof(AssistantPrompt).GetField("knowledge", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                AssistantSettings.SaveShowTab(data, false);
                if (!hadKey) keys.Save(UiKey);
                using (var form = new MainForm(config))
                {
                    Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                    form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                    var library = Get<FlatButton>(form, "navLibrary"); var installed = Get<FlatButton>(form, "navInstalled"); var assistant = Get<FlatButton>(form, "navAssistant"); var gear = Get<FlatButton>(form, "navSettings");
                    var option = Get<CheckBox>(form, "settingsShowAssistant");
                    Panel libraryPage = Get<Panel>(form, "libraryPage"), settingsPage = Get<Panel>(form, "settingsPage");
                    Func<AssistantPage> built = delegate { return Get<AssistantPage>(form, "assistantPage"); };
                    int forms = Application.OpenForms.Count, collapsed = 0;
                    check("tab off: the header shows Mod library · Installed · gear with the usual gap", delegate
                    {
                        Require(!assistant.Visible && library.Visible && installed.Visible && gear.Visible, "The Assistant tab still shows.");
                        Require(library.Right <= installed.Left && installed.Left - library.Right == gear.Left - installed.Right, "The gear does not follow Installed: " + library.Bounds + " " + installed.Bounds + " " + gear.Bounds);
                        foreach (var b in new[] { library, installed, gear }) Require(b.Parent.ClientRectangle.Contains(b.Bounds), b.AccessibleName + " is clipped by the header.");
                        SaveImage(library.Parent.Parent, root, "header-assistant-hidden-" + form.DeviceDpi);
                    });
                    check("tab off: Ctrl+3, the shortcut list and Test key do nothing; no page, conversation, tools or knowledge pack is built", delegate
                    {
                        Require(!Press(form, Keys.Control | Keys.D3) && libraryPage.Visible && built() == null, "Ctrl+3 acted while the tab is off.");
                        Require(!(bool)Call(form, "RunShortcut", ShortcutAction.AssistantPage) && built() == null, "RunShortcut opened the Assistant.");
                        Call(form, "ShowAssistant"); Require(built() == null && libraryPage.Visible, "ShowAssistant built the page.");
                        Call(form, "TestApiKey"); Require(built() == null, "Test key built the page.");
                        typeof(ContextMenuStrip).GetMethod("OnOpening", PrivateInstance).Invoke(gear.ContextMenuStrip, new object[] { new System.ComponentModel.CancelEventArgs() });
                        Require(!Get<ToolStripItem>(form, "assistantShortcutItem").Available, "Keyboard shortcuts still lists the Assistant.");
                        Require(knowledge != null && knowledge.GetValue(null) == null, "The knowledge pack was loaded.");
                    });
                    check("tab off: a saved view on the Assistant opens Mod library", delegate
                    {
                        Call(form, "ShowPage", settingsPage);
                        Call(form, "RestoreView", new ViewPreferences { Page = "Assistant" });
                        Require(libraryPage.Visible && built() == null && Get<string>(form, "lastMainPage") == "Library", "The saved Assistant view was not redirected.");
                    });
                    check("tab off: Settings → General shows only the option and its note; the saved key stays stored and is not read", delegate
                    {
                        Require(Press(form, Keys.Control | Keys.Oemcomma), "Ctrl+, failed."); Call(form, "ShowSettingsTab", false); Application.DoEvents();
                        Require(option.Visible && !option.Checked && option.Text == "Show Assistant tab", "The option is missing or on.");
                        Unclipped(option, "Show Assistant tab");
                        foreach (string name in ClaudeDetailFields) Require(!Get<Control>(form, name).Visible, name + " shows while the tab is off.");
                        collapsed = option.Parent.Controls.Cast<Control>().Where(c => c.Visible).Sum(c => c.Height);
                        Require(collapsed <= DpiMetrics.Px(form, 140), "The collapsed card still takes " + collapsed + " px.");
                        Require(keys.HasSavedKey && Get<Label>(form, "settingsKeyStatus").Text.Length == 0, "The key is gone, or was read while the card is collapsed.");
                        SaveImage(form, root, "settings-assistant-off-" + form.DeviceDpi);
                    });
                    check("turning the option on shows the tab and the whole card at once, with no popup, the saved key and no page yet", delegate
                    {
                        option.Checked = true; Application.DoEvents();
                        Require(assistant.Visible && library.Right <= installed.Left && installed.Right <= assistant.Left && assistant.Right <= gear.Left, "The tab is not back between Installed and the gear.");
                        foreach (string name in ClaudeDetailFields) { Control c = Get<Control>(form, name); Require(c.Visible, name + " did not come back."); if (name != "settingsClaudeStatus") Unclipped(c, name); }
                        // The rows come back in their built order: option, key field, key buttons, key status, model/effort, auto-apply, privacy note.
                        int[] tops = { option.Top, Get<Control>(form, "settingsApiKey").Top, Get<Control>(form, "saveKeyButton").Parent.Top, Get<Control>(form, "settingsKeyStatus").Top, Get<Control>(form, "settingsModel").Parent.Top, Get<Control>(form, "settingsAutoApply").Top, Get<Control>(form, "settingsClaudeStatus").Top };
                        for (int i = 1; i < tops.Length; i++) Require(tops[i] > tops[i - 1], "Card rows out of order after turning the option on: " + String.Join(", ", tops));
                        int expanded = option.Parent.Controls.Cast<Control>().Where(c => c.Visible).Sum(c => c.Height);
                        Require(expanded >= collapsed + DpiMetrics.Px(form, 250), "The card did not expand: " + collapsed + " → " + expanded + " px.");
                        Require(Get<Label>(form, "settingsKeyStatus").Text == "Saved · …ABCD" && keys.HasSavedKey, "Key status: " + Get<Label>(form, "settingsKeyStatus").Text);
                        Require(AssistantSettings.Load(data).ShowTab == true, "The option was not stored.");
                        Require(Application.OpenForms.Count == forms, "A window opened.");
                        Require(built() == null, "Turning the option on built the page before it was shown.");
                        SaveImage(form, root, "settings-assistant-on-" + form.DeviceDpi);
                    });
                    check("turning it off while on the Assistant goes to Mod library; no API client is created while it is off; key and choice are kept", delegate
                    {
                        Require(Press(form, Keys.Control | Keys.D3) && built() != null && built().Visible, "Ctrl+3 did not open the Assistant after turning it on.");
                        AssistantPage page = built();
                        int clients = 0; Func<string, ClaudeClient> real = page.ClientFactory;
                        page.ClientFactory = delegate(string key) { clients++; return real(key); };
                        Call(form, "SetAssistantTabShown", false, true); Application.DoEvents();
                        Require(libraryPage.Visible && !page.Visible && !assistant.Visible && Get<string>(form, "lastMainPage") == "Library", "Turning it off on the Assistant did not go to Mod library.");
                        Require(!Press(form, Keys.Control | Keys.D3) && libraryPage.Visible, "Ctrl+3 acted after turning it off.");
                        Require(Press(form, Keys.Control | Keys.Oemcomma) && !option.Checked && !Get<Control>(form, "settingsApiKey").Visible, "The Settings card did not collapse again.");
                        Call(form, "TestApiKey"); Application.DoEvents();
                        Require(clients == 0, "An API client was created while the tab is off.");
                        Require(keys.HasSavedKey && AssistantSettings.Load(data).ShowTab == false, "The key or the choice was not kept.");
                    });
                    form.Close();
                }
            }
            finally
            {
                if (!hadKey) try { keys.Remove(); } catch (IOException) { }
                if (before == null) File.Delete(file); else File.WriteAllText(file, before);
            }
        }

        private static void SaveImage(Control control, string root, string name)
        {
            Size size = control is Form ? ((Form)control).ClientSize : control.Size;
            using (var bitmap = new Bitmap(size.Width, size.Height))
            {
                control.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(Path.Combine(root, name + ".png"), ImageFormat.Png);
            }
        }

        private static bool Press(MainForm form, Keys keys)
        {
            MethodInfo process = typeof(MainForm).GetMethod("ProcessCmdKey", PrivateInstance);
            var message = new Message(); return (bool)process.Invoke(form, new object[] { message, keys });
        }

        private static AssistantPage Navigation(MainForm form)
        {
            Require(Press(form, Keys.Control | Keys.D3), "Ctrl+3 was not handled.");
            var page = Get<AssistantPage>(form, "assistantPage");
            Require(page != null && page.Visible, "Ctrl+3 did not open the Assistant.");
            // Built after the window scaled its pages: its own sizes and paddings must carry the window's DPI.
            Control header = page.ModelCombo.Parent.Parent, composer = page.Prompt.Parent.Parent;
            Func<int, int, bool> near = delegate(int actual, int logical) { return Math.Abs(actual - DpiMetrics.Px(form, logical)) <= 1; };
            Require(near(header.Height, 40) && near(composer.Height, 132) && near(composer.Padding.Left, 16) && near(page.SendButton.Height, 38), "The page built on first show is not scaled to " + form.DeviceDpi + " DPI: header " + header.Height + ", composer " + composer.Height + "/" + composer.Padding + ", Send " + page.SendButton.Height + ".");
            var library = Get<FlatButton>(form, "navLibrary"); var installed = Get<FlatButton>(form, "navInstalled"); var assistant = Get<FlatButton>(form, "navAssistant"); var gear = Get<FlatButton>(form, "navSettings");
            Require(assistant.Active && !library.Active && !installed.Active, "Assistant tab not marked active.");
            Require(library.Right <= installed.Left && installed.Right <= assistant.Left && assistant.Right <= gear.Left, "Tab order is not Library, Installed, Assistant, gear.");
            foreach (var b in new[] { library, installed, assistant, gear }) Require(b.Parent.ClientRectangle.Contains(b.Bounds), b.Text + " is clipped by the header.");
            Require(!Get<Control>(form, "headerTools").Visible, "Library header tools show on the Assistant page.");
            Require(Shortcuts.Display(ShortcutAction.AssistantPage) == "Ctrl+3", "Ctrl+3 not listed.");
            Require(Press(form, Keys.Control | Keys.D1) && !page.Visible && Press(form, Keys.Control | Keys.D3) && page.Visible, "Switching back and forth failed.");
            Require(Get<AssistantPage>(form, "assistantPage") == page, "A second page was built.");
            return page;
        }

        private static void EmptyState(MainForm form, AssistantPage page)
        {
            Require(page.Transcript.Entries.Count == 0 && page.Transcript.EmptyTitle.Length > 0 && page.Transcript.EmptyBody.Contains("Nothing is installed until you apply it"), "Empty state text missing.");
            Require(page.Transcript.EmptyStatus.Contains("No API key") || page.Transcript.EmptyStatus.Contains("effort"), "Key status not shown: " + page.Transcript.EmptyStatus);
            Require(page.ActionList.Items.Count == 0 && !page.ApplyButton.Enabled && !page.DiscardButton.Enabled && page.PlanCaption.Text == "PLAN", "Plan not empty.");
            Require(form.DefaultStatus(page).Contains("Plan"), "Status strip default for the Assistant page missing.");
            if (page.Transcript.EmptyStatus.Contains("No API key"))
            {
                Require(!page.Send("hello"), "Send without a key started a run.");
                Require(page.Transcript.Entries.Count == 1 && page.Transcript.Entries[0].Kind == TranscriptKind.Error && page.Transcript.Entries[0].Text.Contains("Settings"), "Missing-key guidance not shown.");
                page.Transcript.Clear();
            }
        }

        private static void Pump(Func<bool> done, int milliseconds, string what)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!done()) { if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Timed out waiting for " + what + "."); Application.DoEvents(); Thread.Sleep(10); }
            Application.DoEvents();
        }

        private static void Conversation(MainForm form, AssistantPage page, AssistantDemo demo)
        {
            Require(page.Send(AssistantDemo.Prompt), "Send did not start.");
            Require(page.Running && !page.ModelCombo.Enabled && !page.EffortCombo.Enabled && !page.SendButton.Enabled && page.StopButton.Enabled && !page.NewChatButton.Enabled, "Controls not locked during the run.");
            Pump(delegate { return !page.Running; }, 30000, "the scripted conversation");
            var entries = page.Transcript.Entries;
            Require(entries.First().Kind == TranscriptKind.User && entries.First().Text == AssistantDemo.Prompt, "User message missing.");
            Require(entries.Count(e => e.Kind == TranscriptKind.Thinking) == 2 && entries.Count(e => e.Kind == TranscriptKind.Tool) >= 10, "Thinking or tool activity missing (" + entries.Count + " entries: " + String.Join(", ", entries.Select(e => e.Kind + (e.Kind == TranscriptKind.Tool || e.Kind == TranscriptKind.Error || e.Kind == TranscriptKind.Notice ? " '" + (e.Text.Length > 80 ? e.Text.Substring(0, 80) : e.Text) + "'" : ""))) + ").");
            Require(entries.All(e => e.Thumb == null), "An image thumbnail was shown.");
            Require(entries.Last().Kind == TranscriptKind.Claude && entries.Last().Text.Contains("queued two outfits"), "Final reply missing.");
            Require(page.ActionList.Items.Count == 2 && page.ActionList.Checked.SetEquals(new[] { 1, 2 }) && page.ApplyButton.Enabled && page.ApplyButton.Text == "Apply 2 selected" && page.PlanCaption.Text.Contains("2 TO APPROVE"), "Plan does not show two checked queued installs.");
            Require(!Directory.Exists(Path.Combine(demo.Fixture.CostumeRoot, "Cammy", "Barefoot Black")), "A queued install ran without approval.");
            Require(page.ModelCombo.Enabled && page.SendButton.Enabled && !page.StopButton.Enabled, "Controls not unlocked after the run.");
            Require(page.UsageLabel.Text.Contains("$") && page.UsageLabel.Text.Contains("estimate"), "Usage meter missing: " + page.UsageLabel.Text);
            Require(Get<Label>(form, "statusText").Text.Contains("waiting for approval"), "Status strip does not mention the planned changes.");
        }

        private static Rectangle BoundsIn(Control control, Control ancestor)
        {
            Rectangle r = control.Bounds; Control parent = control.Parent;
            while (parent != null && parent != ancestor) { r.Offset(parent.Left, parent.Top); parent = parent.Parent; }
            return r;
        }
        private static void Unclipped(Control control, string name)
        {
            Require(control.Visible && control.Width > 0 && control.Height > 0, name + " is hidden or empty.");
            Control c = control; Rectangle r = control.Bounds;
            while (c.Parent != null && !(c.Parent is Form))
            {
                Require(c.Parent.ClientRectangle.Contains(r), name + " is clipped by " + c.Parent.GetType().Name + " (" + r + " in " + c.Parent.ClientRectangle + ").");
                r.Offset(c.Parent.Left, c.Parent.Top); c = c.Parent;
            }
        }

        private static void Layout(MainForm form, AssistantPage page, Size logical, string root)
        {
            form.ClientSize = new Size(DpiMetrics.Px(form, logical.Width), DpiMetrics.Px(form, logical.Height)); Application.DoEvents();
            Call(form, "ShowPage", page); form.PerformLayout(); Application.DoEvents();
            Func<int, int> px = delegate(int v) { return DpiMetrics.Px(form, v); };
            foreach (var pair in new Dictionary<string, Control> { { "model", page.ModelCombo }, { "effort", page.EffortCombo }, { "new chat", page.NewChatButton }, { "usage", page.UsageLabel }, { "transcript", page.Transcript }, { "prompt", page.Prompt }, { "send", page.SendButton }, { "stop", page.StopButton }, { "apply", page.ApplyButton }, { "discard", page.DiscardButton }, { "plan list", page.ActionList }, { "auto-apply", page.AutoApply }, { "auto-apply warning", page.AutoApplyWarning }, { "plan caption", page.PlanCaption } })
                Unclipped(pair.Value, pair.Key);
            Require(page.Transcript.Height >= px(200) && page.Transcript.Width >= px(380), "Transcript too small: " + page.Transcript.Size);
            Require(page.Split.Panel2.Width >= px(300) && page.Split.Panel2.Width <= px(380), "Plan width out of range: " + page.Split.Panel2.Width);
            Require(page.ActionList.Height >= px(130), "Plan list too short: " + page.ActionList.Height);
            Require(page.Prompt.Height >= px(60), "Prompt too short.");
            Require(page.UsageLabel.Width >= px(150), "Usage meter too narrow.");
            Rectangle apply = BoundsIn(page.ApplyButton, page), discard = BoundsIn(page.DiscardButton, page), model = BoundsIn(page.ModelCombo, page), effort = BoundsIn(page.EffortCombo, page), newChat = BoundsIn(page.NewChatButton, page);
            Require(!apply.IntersectsWith(discard) && !model.IntersectsWith(effort) && !effort.IntersectsWith(newChat), "Controls overlap.");
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(Path.Combine(root, "assistant-ui-" + logical.Width + "x" + logical.Height + "-" + form.DeviceDpi + ".png"), ImageFormat.Png);
            }
        }

        private static void Switch(MainForm form, AssistantPage page)
        {
            int before = page.Transcript.Entries.Count;
            // 2.4.0: Haiku 5.5 is the default (index 0); switch to Sonnet 5.5 and back.
            Require(page.Conversation.Model == "claude-haiku-5-5" && page.ModelCombo.SelectedIndex == 0, "The conversation did not start on the default Claude Haiku 5.5: " + page.Conversation.Model);
            page.ModelCombo.SelectedIndex = AssistantModels.All.FindIndex(m => m.Id == "claude-sonnet-5-5"); Application.DoEvents();
            Require(page.Conversation.Model == "claude-sonnet-5-5" && page.Transcript.Entries.Count == before + 1 && page.Transcript.Entries.Last().Kind == TranscriptKind.Hint && page.Transcript.Entries.Last().Text.StartsWith("Switched to Claude Sonnet 5.5"), "Switch hint missing.");
            Require(AssistantSettings.Load(AppConfig.DataDir).Model == "claude-sonnet-5-5", "Last-used model not saved.");
            page.EffortCombo.SelectedIndex = Array.IndexOf(AssistantModels.Efforts, "medium"); Application.DoEvents();
            Require(page.Conversation.Effort == "medium" && page.Transcript.Entries.Last().Text.StartsWith("Effort set to Medium"), "Effort hint missing.");
            page.ModelCombo.SelectedIndex = 0; page.EffortCombo.SelectedIndex = Array.IndexOf(AssistantModels.Efforts, "high"); Application.DoEvents();
            Require(page.Conversation.Model == "claude-haiku-5-5" && page.Conversation.Effort == "high", "Switch back failed.");
        }

        private static void ApplyAndNote(MainForm form, AssistantPage page, AssistantDemo demo)
        {
            page.ActionList.Toggle(0);
            Require(page.ActionList.Checked.SetEquals(new[] { 2 }) && page.ApplyButton.Text == "Apply selected", "Unchecking #1 failed.");
            page.DiscardSelected(); Application.DoEvents();
            Require(page.Conversation.Actions[1].State == AssistantActionState.Discarded && page.ActionList.Checked.Count == 0 && !page.ApplyButton.Enabled, "Discard failed.");
            page.ActionList.Toggle(0);
            page.ApplySelected();
            Require(page.Applying && Get<bool>(form, "busy") && page.CancelApplyButton.Visible && !page.SendButton.Enabled, "Apply did not lock the window.");
            Pump(delegate { return !page.Applying && !Get<bool>(form, "busy"); }, 30000, "the approved action");
            Require(page.Conversation.Actions[0].State == AssistantActionState.Succeeded, "Approved install failed: " + page.Conversation.Actions[0].Result);
            Require(Directory.Exists(Path.Combine(demo.Fixture.CostumeRoot, "Cammy", "Barefoot Black")) && !Directory.Exists(Path.Combine(demo.Fixture.CostumeRoot, "Juri", "Barefoot Midnight")), "Applied/discarded installs wrong on disk.");
            Pump(delegate { return page.Transcript.Entries.Any(e => e.Kind == TranscriptKind.App); }, 5000, "the apply note in the transcript");
            Require(page.ApplyStatus.Text.StartsWith("✓ Applied 1 of 1"), "Apply result not shown: " + page.ApplyStatus.Text);
            int requests = demo.Server.Snapshot().Count;
            Require(page.Send("Thanks"), "Follow-up did not start.");
            Pump(delegate { return !page.Running; }, 30000, "the follow-up");
            MockRequest last = demo.Server.Snapshot().Last();
            string note = AssistantJson.Str(last.LastContent.First(), "text") ?? "";
            Require(demo.Server.Snapshot().Count == requests + 1 && note.StartsWith("[App] ") && note.Contains("discarded action #2") && note.Contains("Applied 1 of 1"), "Follow-up note wrong: " + note);
        }

        private static void StopRequest(MainForm form, AssistantPage page, AssistantDemo demo)
        {
            demo.Server.Handler = delegate(MockRequest r, int i) { return new MockReply { Body = "{\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5-5\",\"content\":[{\"type\":\"text\",\"text\":\"late\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}", DelayMs = 8000 }; };
            Require(page.Send("This one will be stopped"), "Send did not start.");
            for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(10); }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            page.Stop();
            Pump(delegate { return !page.Running; }, 4000, "the stop");
            Require(clock.ElapsedMilliseconds < 4000 && page.Transcript.Entries.Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("Stopped")), "Stop did not cancel promptly.");
            Require(page.SendButton.Enabled && page.ModelCombo.Enabled, "Page did not recover after Stop.");
        }

        private static void AutoApplyToggle(AssistantPage page)
        {
            Require(!page.AutoApply.Checked && !page.Session.AutoApply && page.AutoApplyWarning.Text.StartsWith("Off"), "Auto-apply should start off.");
            page.AutoApply.Checked = true; Application.DoEvents();
            Require(page.Session.AutoApply && page.AutoApplyWarning.Text.StartsWith("On") && page.AutoApplyWarning.ForeColor == Palette.Red, "Auto-apply warning missing.");
            Require(!AssistantSettings.Load(AppConfig.DataDir).AutoApply, "The session toggle changed the saved default.");
            page.AutoApply.Checked = false; Application.DoEvents();
            Require(!page.Session.AutoApply, "Auto-apply did not turn off.");
        }

        private static void NewChat(AssistantPage page)
        {
            page.NewChat(); Application.DoEvents();
            Require(page.Conversation == null && page.Transcript.Entries.Count == 0 && page.ActionList.Items.Count == 0 && page.PlanCaption.Text == "PLAN", "New chat did not reset.");
        }

        private static void Settings(MainForm form, AssistantPage page, string root)
        {
            form.ClientSize = new Size(DpiMetrics.Px(form, 1110), DpiMetrics.Px(form, 610)); Application.DoEvents();
            Require(Press(form, Keys.Control | Keys.Oemcomma), "Ctrl+, failed."); Application.DoEvents();
            var key = Get<PaddedTextField>(form, "settingsApiKey"); var status = Get<Label>(form, "settingsKeyStatus");
            Require(key.Editor.UseSystemPasswordChar && key.Text.Length == 0, "Key field is not masked or not empty.");
            foreach (string name in new[] { "settingsApiKey", "saveKeyButton", "removeKeyButton", "testKeyButton", "settingsKeyStatus", "settingsModel", "settingsEffort", "settingsAutoApply" })
                Unclipped(Get<Control>(form, name), name);
            Control card = Get<Control>(form, "settingsApiKey").Parent;
            var layout = (TableLayoutPanel)Get<PaddedTextField>(form, "settingsGame").Parent;
            Panel settingsPage = Get<Panel>(form, "settingsPage");
            Require(!BoundsIn(card, settingsPage).IntersectsWith(BoundsIn(layout, settingsPage)), "Claude section overlaps the folder settings.");
            Require(Get<ListBox>(form, "settingsLibraries").Height >= DpiMetrics.Px(form, 100), "Library list became too short.");
            foreach (string caption in new[] { "Game folder", "7-Zip (RAR and 7Z)", "Mod libraries", "Assistant", "API key", "Default model", "Default effort" })
            {
                Label label = All(settingsPage).OfType<Label>().FirstOrDefault(l => l.Text == caption);
                Require(label != null, "Caption missing: " + caption);
                Unclipped(label, caption);
                Require(label.Width >= TextRenderer.MeasureText(caption, label.Font).Width, "Caption truncated: " + caption);
            }
            key.Text = UiKey; Call(form, "SaveApiKey");
            Require(key.Text.Length == 0 && status.Text == "Saved · …ABCD" && page.KeyStore.HasSavedKey, "Save key status wrong: " + status.Text);
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(Path.Combine(root, "settings-claude-1110x610-" + form.DeviceDpi + ".png"), ImageFormat.Png);
            }
            Call(form, "RemoveApiKey");
            Require(!page.KeyStore.HasSavedKey && (status.Text == "No key" || status.Text.StartsWith("Using ANTHROPIC_API_KEY")), "Remove key status wrong: " + status.Text);
            // 2.0.2: the preview-image option is gone.
            Require(!All(settingsPage).OfType<CheckBox>().Any(c => c.Text.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0), "An image checkbox is still in Settings.");
            var model = Get<ComboBox>(form, "settingsModel");
            Require(model.Items.Count == 2 && (string)model.Items[0] == "Claude Haiku 5.5 (default)" && (string)model.Items[1] == "Claude Sonnet 5.5", "Settings model list: " + String.Join(" / ", model.Items.Cast<object>()));
            model.SelectedIndex = AssistantModels.All.FindIndex(m => m.Id == "claude-sonnet-5-5"); Application.DoEvents();
            var saved = AssistantSettings.Load(AppConfig.DataDir);
            Require(saved.Model == "claude-sonnet-5-5", "Defaults not saved or not applied.");
            model.SelectedIndex = 0; Application.DoEvents();
            Require(AssistantSettings.Load(AppConfig.DataDir).Model == "claude-haiku-5-5", "Default model not restored.");
        }

        private static IEnumerable<Control> All(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (Control d in All(child)) yield return d; } }
        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        {
            var methods = o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToArray();
            if (methods.Length == 0) throw new MissingMethodException(name);
            return methods[0].Invoke(o, args);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Assistant UI regression: " + message); }
    }
}
