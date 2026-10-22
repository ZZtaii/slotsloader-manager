using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.0: update checks (UpdateCheck), the in-app update toasts, the Settings "Updates" tab, the SlotsLoader
    // update in place and the Manager self-update (AppUpdater).
    internal sealed partial class MainForm
    {
        private FlatButton settingsUpdatesTab;
        private Panel settingsUpdatesPanel;
        private CheckBox settingsAutoUpdate, settingsPrereleases;
        private FlatButton settingsCheckUpdates;
        private Label settingsUpdateResult, settingsUpdateSkipped;
        private FlowLayoutPanel toastHost;
        private Panel statusPanel;
        private System.Windows.Forms.Timer updateTimer;
        internal static int UpdateStartDelayMs = 4000;
        internal static string AppDirOverride;          // tests: a temporary copy of the app folder
        internal static bool NoRelaunch;                 // tests
        internal string LastUpdateMessage = "";          // tests: the Settings result text
        internal UpdateCheckResult LastUpdateResult;     // tests
        internal int UpdateChecksRun;                    // tests
        private sealed class PendingLoaderUpdate { internal string ModId, From, To, Path; }
        private PendingLoaderUpdate pendingLoaderUpdate;

        internal static string AppDir { get { return AppDirOverride ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }
        private LoaderPin CurrentInstallPin() { return LoaderRelease.Pin != LoaderPin.Release ? LoaderRelease.Pin : UpdateCheck.InstallPin(UpdateSettings.Load().Last); }

        // ---- Start-up: clean up after a self-update, then the background check (never blocks the window).
        private void StartUpdates()
        {
            try { string line = AppUpdater.CleanupAfterUpdate(AppDir, AppConfig.DataDir); if (line != null) SetSharedStatus("✓ " + line + ".", Palette.Green); }
            catch (Exception ex) { Program.Log(ex); }
            updateTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, UpdateStartDelayMs) };
            updateTimer.Tick += delegate
            {
                updateTimer.Stop();
                UpdateSettings s = UpdateSettings.Load();
                if (s.AutoCheck && s.Due(DateTime.UtcNow)) RunUpdateCheck(false);
                else ShowUpdateToasts();
            };
            updateTimer.Start();
        }

        // Background (manual = false: a failure is one log line, no toast or dialog) or from Settings.
        internal void RunUpdateCheck(bool manual)
        {
            bool pre = UpdateSettings.Load().IncludePrereleases;
            if (manual && settingsCheckUpdates != null) { settingsCheckUpdates.Enabled = false; settingsUpdateResult.Text = "Checking…"; settingsUpdateSkipped.Text = ""; }
            var thread = new Thread(delegate()
            {
                UpdateCheckResult r;
                try { r = UpdateCheck.Check(pre); }
                catch (Exception ex) { Program.Log(ex); r = new UpdateCheckResult { Reason = ex.Message, CheckedUtc = DateTime.UtcNow.ToString("o") }; }
                try { if (!IsDisposed) BeginInvoke((Action)delegate { ApplyUpdateResult(r, manual); }); } catch (InvalidOperationException) { }
            }) { IsBackground = true, Name = "update check" };
            thread.Start();
        }

        private void ApplyUpdateResult(UpdateCheckResult r, bool manual)
        {
            UpdateChecksRun++; LastUpdateResult = r;
            UpdateSettings s = UpdateSettings.Load();
            s.LastCheckUtc = r.CheckedUtc;
            if (r.Ok) { s.Last = r; s.DismissedLoader = ""; s.DismissedManager = ""; }
            else Program.LogLine("Update check: " + r.Reason + (r.Status == 404 ? " (expected while the repository is private)" : "") + ".");
            try { s.Save(); } catch (Exception ex) { Program.Log(ex); }
            string summary = UpdateSummary(r), skipped = SkippedSummary(r);
            LastUpdateMessage = summary + (skipped.Length > 0 ? "\n" + skipped : "");
            if (settingsUpdateResult != null) { ShowUpdateResult(summary, skipped, r.Ok); settingsCheckUpdates.Enabled = true; }
            if (manual) SetPageStatus(currentPage, summary, r.Ok ? Palette.Muted : Palette.Red);
            ShowUpdateToasts();
        }

        // One line: "Up to date · Slotsloader 1.9.8 · Manager 2.4.0", "Update available · Slotsloader 1.9.9 (you have 1.9.8) · Manager 2.4.0".
        internal string UpdateSummary(UpdateCheckResult r)
        {
            if (!r.Ok) return "Couldn't check for updates: " + r.Reason + ".";
            string source; string installed = InstalledLoaderVersion(out source);
            ReleaseOffer loader = UpdateCheck.LoaderUpdate(r, installed), manager = UpdateCheck.ManagerUpdate(r, UpdateCheck.Running);
            string running = UpdateCheck.ShortVersion(SafeHttp.AppVersion());
            string loaderPart = loader != null ? "Slotsloader " + loader.Version + " (you have " + UpdateCheck.ShortVersion(installed) + ")"
                : installed != null ? "Slotsloader " + UpdateCheck.ShortVersion(installed)
                : "Slotsloader not installed by the Manager" + (r.Loader != null ? " (newest " + r.Loader.Version + ")" : "");
            if (LinuxPlatform.IsLinux) return "Linux package " + running + " · " + loaderPart + ". Update the app through its Linux installer.";
            string managerPart = "Manager " + (manager != null ? manager.Version + " (you have " + running + ")" : running);
            int updates = (loader != null ? 1 : 0) + (manager != null ? 1 : 0);
            string lead = updates == 2 ? "Updates available" : updates == 1 ? "Update available" : installed != null ? "Up to date" : "Manager up to date";
            if (installed == null && manager == null) return lead + " (" + running + ") · " + loaderPart;
            return lead + " · " + (installed != null || loader != null ? loaderPart + " · " + managerPart : managerPart + " · " + loaderPart);
        }
        // Releases without a checksum: one short muted line; the full reason is in Data\log.txt (UpdateCheck logs it).
        internal static string SkippedSummary(UpdateCheckResult r)
        {
            if (r == null || !r.Ok || r.Refused.Count == 0) return "";
            return "Skipped " + String.Join(", ", r.Refused.Select(x => x.Split(':')[0])) + ": no checksum";
        }
        private void ShowUpdateResult(string summary, string skipped, bool ok)
        {
            settingsUpdateResult.Text = summary; settingsUpdateResult.ForeColor = ok ? Palette.Text : Palette.Red;
            settingsUpdateSkipped.Text = skipped;
        }

        private string InstalledLoaderVersion(out string source)
        {
            source = "";
            try { return InstalledLoader.Version(InstalledLoader.Record(Engine().LoadState()), out source); }
            catch (Exception ex) { Program.Log(ex); return null; }
        }

        // ---- Toasts: bottom-right above the status strip, one per product, until acted on.
        internal void ShowUpdateToasts()
        {
            if (toastHost == null) BuildToastHost();
            UpdateSettings s = UpdateSettings.Load();
            UpdateCheckResult r = s.Last;
            string source; string installed = InstalledLoaderVersion(out source);
            ReleaseOffer loader = UpdateCheck.LoaderUpdate(r, installed), manager = UpdateCheck.ManagerUpdate(r, UpdateCheck.Running);
            SetToast(UpdateProducts.Loader, loader != null && loader.Version != s.SkipLoader && loader.Version != s.DismissedLoader ? loader : null, installed);
            SetToast(UpdateProducts.Manager, manager != null && manager.Version != s.SkipManager && manager.Version != s.DismissedManager ? manager : null, SafeHttp.AppVersion());
        }

        private void BuildToastHost()
        {
            toastHost = new FlowLayoutPanel { FlowDirection = FlowDirection.BottomUp, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Palette.Background, Padding = Padding.Empty, Margin = Padding.Empty, Visible = false, AccessibleName = "Updates available", AccessibleRole = AccessibleRole.Grouping };
            Controls.Add(toastHost); toastHost.BringToFront();
            Resize += delegate { PlaceToasts(); };
            toastHost.SizeChanged += delegate { PlaceToasts(); };
        }
        private void PlaceToasts()
        {
            if (toastHost == null) return;
            int bottom = statusPanel != null && statusPanel.Parent == this ? statusPanel.Top : ClientSize.Height;
            toastHost.Location = new Point(Math.Max(0, ClientSize.Width - toastHost.Width - Px(16)), Math.Max(0, bottom - toastHost.Height - Px(12)));
            toastHost.BringToFront();
        }
        internal Panel UpdateToast(string product) { return toastHost == null ? null : toastHost.Controls.OfType<Panel>().FirstOrDefault(p => (string)p.Tag == product); }

        private void SetToast(string product, ReleaseOffer offer, string installed)
        {
            Panel old = UpdateToast(product);
            if (offer == null) { if (old != null) { toastHost.Controls.Remove(old); old.Dispose(); } toastHost.Visible = toastHost.Controls.Count > 0; PlaceToasts(); return; }
            string name = product == UpdateProducts.Loader ? "Slotsloader" : "Slotsloader Manager";
            string text = name + " " + offer.Version + " is available" + (String.IsNullOrEmpty(installed) ? "" : " (you have " + UpdateCheck.ShortVersion(installed) + ")") + (offer.Prerelease ? " · test build" : "");
            if (old != null && (string)old.AccessibleDescription == offer.Version) return;
            if (old != null) { toastHost.Controls.Remove(old); old.Dispose(); }
            var toast = new Panel { Tag = product, Width = Px(470), Height = Px(86), BackColor = Palette.Raised, Padding = new Padding(Px(12), Px(8), Px(8), Px(8)), Margin = new Padding(0, Px(8), 0, 0), AccessibleName = text, AccessibleDescription = offer.Version, AccessibleRole = AccessibleRole.Alert };
            toast.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(Palette.Accent, Math.Max(1F, Px(1)))) e.Graphics.DrawRectangle(pen, 0, 0, toast.Width - 1, toast.Height - 1); };
            var label = new Label { UseMnemonic = false, Text = text, Dock = DockStyle.Top, Height = Px(28), ForeColor = Palette.Text, Font = Palette.Normal, AutoEllipsis = true, Name = "toastText" };
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty };
            FlatButton update = Button("Update", true, 92, delegate { if (product == UpdateProducts.Loader) StartLoaderUpdate(offer, installed); else StartManagerUpdate(offer); });
            FlatButton notes = Button("What's new", false, 110, delegate { OpenReleasePage(offer.HtmlUrl); });
            FlatButton skip = Button("Skip this version", false, 140, delegate { UpdateSettings s = UpdateSettings.Load(); if (product == UpdateProducts.Loader) s.SkipLoader = offer.Version; else s.SkipManager = offer.Version; s.Save(); SetToast(product, null, null); });
            FlatButton close = Button("✕", false, 40, delegate { UpdateSettings s = UpdateSettings.Load(); if (product == UpdateProducts.Loader) s.DismissedLoader = offer.Version; else s.DismissedManager = offer.Version; s.Save(); SetToast(product, null, null); });
            update.AccessibleName = "Update " + name + " to " + offer.Version; notes.AccessibleName = "What's new in " + name + " " + offer.Version + " (opens the release page)";
            skip.AccessibleName = "Skip " + name + " " + offer.Version; close.AccessibleName = "Dismiss the " + name + " update until the next check";
            foreach (FlatButton b in new[] { update, notes, skip, close }) { b.Width = Px(b.Width); b.Height = Px(32); b.Font = Palette.Small; b.Margin = new Padding(0, 0, Px(6), 0); row.Controls.Add(b); }
            toast.Controls.Add(row); toast.Controls.Add(label);
            toastHost.Controls.Add(toast);
            toastHost.Visible = true; PlaceToasts();
        }
        internal void SetToastText(string product, string text) { Panel t = UpdateToast(product); if (t == null) return; Label l = t.Controls.Find("toastText", true).OfType<Label>().FirstOrDefault(); if (l != null) l.Text = text; }

        internal static Func<string, bool> OpenUrlHook;   // tests
        private void OpenReleasePage(string url)
        {
            if (String.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (OpenUrlHook != null) { OpenUrlHook(url); return; }
            try { if (LinuxPlatform.IsLinux) LinuxPlatform.OpenUrl(url); else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Error(ex); }
        }

        // ---- SlotsLoader update: download + verify, then the tool-mod install in Replace mode, in place.
        private void StartLoaderUpdate(ReleaseOffer offer, string installed)
        {
            if (busy) { SetPageStatus(currentPage, "Wait for the current task to finish.", Palette.Red); return; }
            if (gameRunning) { SetToastText(UpdateProducts.Loader, "Close SF6 to update Slotsloader to " + offer.Version + "."); return; }
            ReplaceMod record;
            try { record = InstalledLoader.Record(Engine().LoadState()); } catch (Exception ex) { Error(ex); return; }
            if (record == null) { SetToast(UpdateProducts.Loader, null, null); return; }
            string downloads = LoaderRelease.DownloadDir;
            Work("Downloading Slotsloader " + offer.Version + "…", delegate
            {
                LoaderPin pin = offer.AsPin();
                LoaderFile local = LoaderRelease.FindLocal(pin, new string[0], downloads);
                return local ?? LoaderRelease.Download(pin, downloads);
            }, delegate(LoaderFile file)
            {
                pendingLoaderUpdate = new PendingLoaderUpdate { ModId = record.Id, From = UpdateCheck.ShortVersion(installed), To = offer.Version, Path = file.Path };
                Program.LogLine("Slotsloader update " + installed + " -> " + offer.Version + ": " + file.Path + " · SHA-256 " + file.Sha256);
                OpenLoader(file);
            });
        }

        // After the health check of the downloaded loader: no warnings -> one confirmation and install in place;
        // warnings -> the report stays and Install updates in place.
        private void ContinueLoaderUpdate()
        {
            PendingLoaderUpdate p = pendingLoaderUpdate;
            if (p == null || package == null || !String.Equals(package.SourcePath, p.Path, StringComparison.OrdinalIgnoreCase) || !IsReplaceMode || ReviewPending || busy || replacePlan == null) return;
            bool warnings = !replacePlan.CanInstall || replacePlan.Items.Any(i => i.Level == "warn" || i.Level == "error");
            if (warnings) { SetPageStatus(libraryPage, "Review the health check. Install then updates Slotsloader " + p.From + " → " + p.To + " in place.", Palette.Accent); return; }
            if (gameRunning) { SetPageStatus(libraryPage, "Close SF6 to update Slotsloader.", Palette.Red); return; }
            // 2.4.1: the toast's Update was the consent: install in place without asking (warnings stay inline above).
            SetPageStatus(libraryPage, "Updating Slotsloader " + p.From + " → " + p.To + "…", Palette.Muted);
            InstallReplacement();
        }
        // InstallReplacement: the pending update installs over the recorded SlotsLoader without asking again.
        private string PendingLoaderReplaceId() { PendingLoaderUpdate p = pendingLoaderUpdate; return p != null && package != null && String.Equals(package.SourcePath, p.Path, StringComparison.OrdinalIgnoreCase) ? p.ModId : null; }
        private void LoaderUpdateInstalled() { if (pendingLoaderUpdate == null) return; Program.LogLine("Slotsloader updated " + pendingLoaderUpdate.From + " -> " + pendingLoaderUpdate.To + "."); pendingLoaderUpdate = null; SetToast(UpdateProducts.Loader, null, null); }

        // ---- Manager self-update.
        private void StartManagerUpdate(ReleaseOffer offer)
        {
            if (LinuxPlatform.IsLinux) { SetPageStatus(currentPage, LinuxPlatform.UpdateMessage, Palette.Accent); return; }
            if (busy) { SetPageStatus(currentPage, "Wait for the current task to finish.", Palette.Red); return; }
            string data = AppConfig.DataDir, downloads = LoaderRelease.DownloadDir;
            Work("Downloading Slotsloader Manager " + offer.Version + "…", delegate
            {
                string zip;
                try { zip = SafeHttp.Download(offer.Url, downloads, offer.Asset, offer.Size, offer.Sha256, "Downloading Slotsloader Manager " + offer.Version, "published"); }
                catch (DownloadFailure f) { throw new IOException("Couldn't download Slotsloader Manager " + offer.Version + ": " + f.Reason + ".\n\nGet it from " + offer.HtmlUrl + "."); }
                return AppUpdater.Stage(zip, offer.Version, data);
            }, delegate(string staged)
            {
                // 2.4.1: no confirmation (Update was the consent); the restart follows without asking. To go back: CLI update-rollback.
                string app = AppDir;
                Work("Updating Slotsloader Manager…", delegate { AppUpdater.Apply(app, staged, AppUpdater.PreviousDir(data), SafeHttp.AppVersion(), offer.Version, data); return app; }, delegate(string folder)
                {
                    SetToast(UpdateProducts.Manager, null, null);
                    if (NoRelaunch) { SetPageStatus(currentPage, "✓ Updated to " + offer.Version + "; restart to use it.", Palette.Green); return; }
                    try { Process.Start(new ProcessStartInfo(Path.Combine(folder, "Slotsloader Manager.exe")) { UseShellExecute = false, WorkingDirectory = folder }); Close(); }
                    catch (Exception ex) { Error(new IOException("Updated to " + offer.Version + " but couldn't restart: " + ex.Message + " Start Slotsloader Manager again.")); }
                });
            });
        }

        // ---- Settings → Updates (the tab names it: no heading inside)
        private Panel BuildUpdatesSettings()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(0, 8, 0, 0) };
            var column = new Panel { Dock = DockStyle.Left, Width = 640 };
            Action<Control> add = delegate(Control c) { c.Dock = DockStyle.Top; column.Controls.Add(c); c.BringToFront(); };
            settingsAutoUpdate = new CheckBox { Text = "Check automatically (daily)", Height = 30, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0), AccessibleName = "Check for updates automatically (daily)" };
            settingsPrereleases = new CheckBox { Text = "Include test builds", Height = 30, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0), AccessibleName = "Include test builds (pre-releases)" };
            settingsAutoUpdate.CheckedChanged += delegate { if (loadingUpdateFields) return; UpdateSettings s = UpdateSettings.Load(); s.AutoCheck = settingsAutoUpdate.Checked; s.Save(); };
            settingsPrereleases.CheckedChanged += delegate { if (loadingUpdateFields) return; UpdateSettings s = UpdateSettings.Load(); s.IncludePrereleases = settingsPrereleases.Checked; s.Save(); };
            add(settingsAutoUpdate); add(settingsPrereleases); add(Rhythm.Gap(Rhythm.Related));
            settingsCheckUpdates = Button("Check now", false, 110, delegate { RunUpdateCheck(true); });
            settingsCheckUpdates.Height = 32; settingsCheckUpdates.Font = Palette.Small; settingsCheckUpdates.Margin = Padding.Empty; settingsCheckUpdates.AccessibleName = "Check for updates now";
            var row = new Panel { Height = 32 }; settingsCheckUpdates.Dock = DockStyle.Left; row.Controls.Add(settingsCheckUpdates); add(row);
            add(Rhythm.Gap(Rhythm.AfterAction));
            settingsUpdateResult = new NoteLabel { Height = 26, MinLines = 1, ForeColor = Palette.Text, Font = Palette.Small, Name = "settingsUpdateResult" };
            add(settingsUpdateResult);
            settingsUpdateSkipped = new NoteLabel { Height = 0, ForeColor = Palette.Muted, Font = Palette.Small, Name = "settingsUpdateSkipped" };
            add(settingsUpdateSkipped);
            add(Rhythm.Gap(Rhythm.Group));
            add(new NoteLabel { Height = LinuxPlatform.IsLinux ? 52 : 26, ForeColor = Palette.Muted, Font = Palette.Small, Name = "settingsUpdatePrivacy", Text = "Checks GitHub (" + LoaderPin.Repo + ") once a day. Nothing installs without your OK." + (LinuxPlatform.IsLinux ? " Update the app using its Linux installer; Slotsloader updates work here." : "") });
            panel.Controls.Add(column);
            return panel;
        }
        private bool loadingUpdateFields;
        private void ShowUpdatesTab()
        {
            foreach (Control c in settingsGeneralParts) c.Visible = false;
            if (settingsSubtitle != null) settingsSubtitle.Visible = false;
            settingsReplacePanel.Visible = false; settingsUpdatesPanel.Visible = true; settingsUpdatesPanel.BringToFront();
            settingsGeneralTab.SetActive(false); settingsReplaceTab.SetActive(false); settingsUpdatesTab.SetActive(true);
            HideAboutTab();   // 2.5.9
            UpdateSettings s = UpdateSettings.Load();
            loadingUpdateFields = true; settingsAutoUpdate.Checked = s.AutoCheck; settingsPrereleases.Checked = s.IncludePrereleases; loadingUpdateFields = false;
            if (settingsUpdateResult.Text.Length > 0) return;
            if (s.Last == null) ShowUpdateResult(s.LastCheckUtc.Length == 0 ? "Not checked yet." : "The last check didn't reach GitHub.", "", true);
            else ShowUpdateResult(UpdateSummary(s.Last), SkippedSummary(s.Last), true);
        }
    }
}
