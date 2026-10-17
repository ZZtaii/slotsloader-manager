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
    // 2.4.2 window checks (part of --replace-ui-check): the Installed banner's Repair for game entries another tool
    // left invalidated (no popup, refusals inline), both Fluffy instances watched, and a Fluffy PAK install
    // uninstalled from the Installed page with Undo. Hidden window; renders into the fixture folder.
    internal static class FluffyPakUiChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args) { return o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).First(x => x.Name == name && x.GetParameters().Length == args.Length).Invoke(o, args); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void WaitIdle(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(10); if (!form.Working) { Application.DoEvents(); if (!form.Working) return; } } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("Background work did not finish.");
        }
        private static void Render(MainForm form, string file)
        {
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, ImageFormat.Png); }
        }
        private static string Status(MainForm form) { return Get<Label>(form, "statusText").Text; }

        internal static void Run(string root, FakeProcessProbe probe)
        {
            string dir = Path.Combine(root, "fluffy-pak-ui");
            var extra = new Dictionary<string, byte[]> { { FluffyPakChecks.AkiMesh, FluffyPakChecks.Mesh("vanilla aki") }, { FluffyPakChecks.AkiMdf, FluffyPakChecks.Mdf("vanilla aki") }, { FluffyPakChecks.ChunMesh, FluffyPakChecks.Mesh("vanilla chun") } };
            ReplaceChecks.Fixture f = FluffyPakChecks.SortedGame(Path.Combine(dir, "fixture"), extra, 2000);
            string pak = FluffyPakChecks.BasePak(f), cleanSha = ReplaceEngine.Sha(pak);
            // Two Fluffy instances, like <drive>\Fluffy and <drive>\Games\Fluffy - Copy: one with a PAK install, one with none yet.
            var fluffyA = new FluffyLinkChecks.FakeFluffy(Path.Combine(dir, "Fluffy", "Games", "SF6"), f.Game);
            string rootB = Path.Combine(dir, "Games", "Fluffy - Copy", "Games", "SF6");
            Directory.CreateDirectory(Path.Combine(rootB, "Mods")); Directory.CreateDirectory(Path.Combine(rootB, "Backup"));
            fluffyA.Install("Chun Li Bare Legs C1 - No head ribbon", "Chun Li Bare Legs - No head ribbon", "invalidate", new Dictionary<string, byte[]> { { FluffyPakChecks.ChunMesh, FluffyPakChecks.Mesh("bare legs") } }, false, null);
            int chunRow = FluffyPakChecks.Zero(f, FluffyPakChecks.ChunMesh);
            // Another tool left the A.K.I outfit's two entries zeroed; the game's own index knows the paths.
            FluffyPakChecks.WriteTsv(f.Game, new[] { FluffyPakChecks.AkiMesh, FluffyPakChecks.AkiMdf });
            int akiMesh = FluffyPakChecks.Zero(f, FluffyPakChecks.AkiMesh), akiMdf = FluffyPakChecks.Zero(f, FluffyPakChecks.AkiMdf);
            var config = new AppConfig { GameRoot = f.Game, Libraries = new List<string> { f.Mods, Path.Combine(fluffyA.Root, "Mods"), Path.Combine(rootB, "Mods") }, SevenZipPath = "" };
            int popups = 0;
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { popups++; return DialogResult.OK; };
            ReplaceSettings saved = ReplaceSettings.Load();
            string state = Path.Combine(AppConfig.DataDir, "replace-state.json"), keptState = state + ".pakui-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            bool setAside = File.Exists(state);
            if (setAside) File.Move(state, keptState);
            bool autoRefresh = MainForm.FluffyAutoRefresh;
            try
            {
                new ReplaceSettings { Method = ReplaceMethods.Pak, LastMode = InstallModes.Replace }.Save();
                using (var form = new MainForm(config))
                {
                    form.PortraitCacheDirectory = Path.Combine(dir, "portraits");
                    Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                    form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                    Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
                    Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                    // The banner: one compact row with Repair.
                    NoticeBanner banner = Get<Panel>(form, "gameBanner") as NoticeBanner;
                    FlatButton repair = Get<FlatButton>(form, "bannerRepair");
                    Require(banner != null && banner.Visible && banner.Message.Text == "2 game entries left invalidated by another tool." && repair.Visible && repair.Enabled && repair.Text == "Repair", "banner: " + (banner == null ? "none" : banner.Visible + " '" + banner.Message.Text + "'") + ", Repair visible " + repair.Visible);
                    Require(banner.Height == DpiMetrics.Px(form, NoticeBanner.RowHeight) && !repair.Primary && repair.Height == DpiMetrics.Px(form, NoticeBanner.ButtonHeight) && TextRenderer.MeasureText(banner.Message.Text, banner.Message.Font).Width <= banner.Message.Width, "banner layout at " + form.DeviceDpi + " DPI");
                    Require(banner.Actions.Controls.OfType<FlatButton>().Count(b => b.Visible) == 1 && String.IsNullOrEmpty(repair.AccessibleDescription), "only Repair is offered (no reason while idle)");
                    Render(form, Path.Combine(dir, "installed-banner-repair-" + form.DeviceDpi + ".png"));
                    // Both Fluffy instances watched.
                    FluffyWatcher watcher = Get<FluffyWatcher>(form, "fluffyWatcher");
                    Require(watcher != null && watcher.Watched.Count == 2 && watcher.Watched.Any(r => r.EndsWith("Fluffy - Copy\\Games\\SF6", StringComparison.OrdinalIgnoreCase)), "watched roots: " + (watcher == null ? "none" : String.Join(", ", watcher.Watched)));
                    // SF6 or Fluffy running: Repair refuses inline (the reason in the status strip and on the button), nothing written.
                    probe.Game = true; Call(form, "PollGame");
                    Require(repair.AccessibleDescription == MainForm.RepairGameRunning, "reason while SF6 runs: " + repair.AccessibleDescription);
                    repair.PerformClick(); WaitIdle(form);
                    Require(Status(form) == MainForm.RepairGameRunning && FluffyPakChecks.RowHash(pak, akiMesh) == 0, "SF6 running: " + Status(form));
                    probe.Game = false; probe.Fluffy = true; Call(form, "PollGame");
                    repair.PerformClick(); WaitIdle(form);
                    Require(Status(form) == MainForm.RepairFluffyRunning && FluffyPakChecks.RowHash(pak, akiMdf) == 0, "Fluffy running: " + Status(form));
                    probe.Fluffy = false; Call(form, "PollGame");
                    // Repair: one click, the result inline, no popup; the Fluffy-owned entry stays.
                    repair.PerformClick(); WaitIdle(form);
                    Require(Status(form) == "✓ Repaired 2 game entries." && FluffyPakChecks.RowHash(pak, akiMesh) == FluffyPakChecks.AkiMeshHash && FluffyPakChecks.RowHash(pak, akiMdf) == FluffyPakChecks.AkiMdfHash && FluffyPakChecks.RowHash(pak, chunRow) == 0, "Repair: " + Status(form));
                    Require(!banner.Visible && !repair.Visible && popups == 0, "after Repair: banner " + banner.Visible + ", popups " + popups);
                    Render(form, Path.Combine(dir, "installed-banner-repaired-" + form.DeviceDpi + ".png"));
                    // 1.0.1: the external manager's PAK install is uninstalled there: the window says so and changes nothing.
                    GameModRow row = Get<List<GameModRow>>(form, "gameRows").Single(r => r.Fluffy != null);
                    Require(form.FluffyUninstallBlocker(row.Fluffy) == "Uninstall it in the external mod manager.", "blocker: " + form.FluffyUninstallBlocker(row.Fluffy));
                    Call(form, "UninstallFluffyRow", row); WaitIdle(form);
                    Require(FluffyPakChecks.RowHash(pak, chunRow) == 0 && File.Exists(Path.Combine(f.Game, FluffyPakChecks.ChunMesh.Replace('/', '\\'))) && Status(form) == "Uninstall it in the external mod manager." && popups == 0, "refused external uninstall: " + Status(form));
                    // An unreadable Manager record (another game folder) still lists and watches both Fluffy instances.
                    File.WriteAllText(state, "{\"Format\":\"sf6-replace-state-v1\",\"GameRoot\":\"X:\\\\Other\\\\Game\",\"Mods\":[{\"Id\":\"x\",\"Name\":\"x\"}]}");
                    watcher.Dispose(); Set(form, "fluffyWatcher", null);
                    Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                    watcher = Get<FluffyWatcher>(form, "fluffyWatcher");
                    Require(watcher != null && watcher.Watched.Count == 2 && Get<List<GameModRow>>(form, "gameRows").Any(r => r.Fluffy != null), "with an unreadable record: " + (watcher == null ? "no watcher" : watcher.Watched.Count + " roots"));
                    File.Delete(state);
                    form.Close();
                }
            }
            finally
            {
                ThemedMessageBox.TestResponder = null; probe.Game = false; probe.Fluffy = false; MainForm.FluffyAutoRefresh = autoRefresh;
                saved.Save();
                if (File.Exists(state)) File.Delete(state);
                if (setAside) File.Move(keptState, state);
            }
        }
    }
}
