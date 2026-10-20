using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.10 (the user: "do we prompt user to reapply all if game is updated?"): the Installed page banner was the
    // only sign that the game files no longer match the replacements (an SF6 update, a Steam verify, a deleted file),
    // so someone who stays in the Mod library saw nothing while their replacements quietly stopped showing in game.
    // The same notice is now a toast on every other page (the update toasts' host, bottom right) with Reapply, a way to
    // the Installed page for the details, and ✕ for this session (it comes back when the notice changes). Launch SF6
    // with a Reapply pending says so first; a second click within LaunchConfirmSeconds starts the game anyway.
    // Nothing is reapplied without a click: an update can change formats, and Reapply refixes known ones first.
    internal sealed partial class MainForm
    {
        internal const string GameFilesToast = "game-files";
        internal const int LaunchConfirmSeconds = 15;
        private string gameNoticeDismissed;   // the summary dismissed with ✕ (this session only)
        private DateTime launchWarnedUtc = DateTime.MinValue;
        private bool GameFilesNeedReapply { get { return gameStatus != null && (gameStatus.NeedsReapply || gameStatus.BaseChanged); } }

        // Called with the banner (UpdateBanner) and on every page switch.
        private void UpdateGameFilesToast()
        {
            bool show = GameFilesNeedReapply && currentPage != installedPage && gameStatus.Summary != gameNoticeDismissed;
            Panel old = UpdateToast(GameFilesToast);
            if (!show)
            {
                if (old == null) return;
                toastHost.Controls.Remove(old); old.Dispose(); toastHost.Visible = toastHost.Controls.Count > 0; PlaceToasts(); return;
            }
            string text = GameFilesToastText();
            bool idle = !busy && !gameRunning && !FluffyBlocksPak;
            if (old != null && old.AccessibleName == text)
            {
                FlatButton existing = old.Controls.Find("toastReapply", true).OfType<FlatButton>().FirstOrDefault();
                if (existing != null) existing.Enabled = idle;
                return;
            }
            if (toastHost == null) BuildToastHost();
            if (old != null) { toastHost.Controls.Remove(old); old.Dispose(); }
            var toast = new Panel { Tag = GameFilesToast, Width = Px(470), Height = Px(86), BackColor = Palette.Raised, Padding = new Padding(Px(12), Px(8), Px(8), Px(8)), Margin = new Padding(0, Px(8), 0, 0), AccessibleName = text, AccessibleDescription = String.Join("; ", gameStatus.Details), AccessibleRole = AccessibleRole.Alert };
            toast.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(Palette.Accent, Math.Max(1F, Px(1)))) e.Graphics.DrawRectangle(pen, 0, 0, toast.Width - 1, toast.Height - 1); };
            var label = new Label { UseMnemonic = false, Text = text, Dock = DockStyle.Top, Height = Px(28), ForeColor = Palette.Text, Font = Palette.Normal, AutoEllipsis = true, Name = "toastText" };
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty };
            FlatButton reapply = Button("Reapply", true, 92, delegate { if (!busy) ReapplyAll(); });
            FlatButton details = Button("Show details", false, 120, delegate { ShowPage(installedPage); RefreshInstalled(); });
            FlatButton close = Button("✕", false, 40, delegate { gameNoticeDismissed = gameStatus == null ? null : gameStatus.Summary; UpdateGameFilesToast(); });
            reapply.Name = "toastReapply"; reapply.Enabled = idle;
            reapply.AccessibleName = "Reapply your replacements to the game files"; details.AccessibleName = "Show the details on the Installed page"; close.AccessibleName = "Dismiss this notice until it changes";
            foreach (FlatButton b in new[] { reapply, details, close }) { b.Width = Px(b.Width); b.Height = Px(32); b.Font = Palette.Small; b.Margin = new Padding(0, 0, Px(6), 0); row.Controls.Add(b); }
            toast.Controls.Add(row); toast.Controls.Add(label);
            toastHost.Controls.Add(toast);
            toastHost.Visible = true; PlaceToasts();
        }
        private string GameFilesToastText()
        {
            if (gameRunning) return "Close SF6 to reapply your replacements: the game files changed.";
            if (FluffyBlocksPak) return "Close the external mod manager to reapply your replacements.";
            return gameStatus.Summary;
        }

        // LaunchGame: true when the click may start SF6. With a Reapply pending, the first click explains (and brings a
        // dismissed notice back); a second one within LaunchConfirmSeconds goes ahead.
        private bool LaunchConfirmed()
        {
            if (gameStatus == null || !gameStatus.NeedsReapply || (DateTime.UtcNow - launchWarnedUtc).TotalSeconds <= LaunchConfirmSeconds) { launchWarnedUtc = DateTime.MinValue; return true; }
            launchWarnedUtc = DateTime.UtcNow; gameNoticeDismissed = null; UpdateGameFilesToast();
            SetPageStatus(currentPage, "The game files changed, so your replacements won't show in game until you reapply them. Reapply first, or click Launch SF6 again to start anyway.", Palette.Accent);
            return false;
        }
    }
}
