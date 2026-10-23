using System;
using System.Drawing;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.12 (23 Oct) (the user): a first start has no mod libraries, so the library list was just blank. Once a scan finds no
    // mods, the list gives way to a short note and a link to Settings → Mod libraries (its Add… button focused).
    internal sealed partial class MainForm
    {
        private Panel libraryEmpty;
        private Label libraryEmptyTitle, libraryEmptyText, libraryEmptyLink;
        private FlatButton addLibraryButton;
        private bool libraryScanned;

        private Panel BuildLibraryEmpty()
        {
            libraryEmpty = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(4, 18, 4, 0), Visible = false, Name = "libraryEmpty" };
            libraryEmptyTitle = new Label { UseMnemonic = false, Dock = DockStyle.Top, AutoSize = false, ForeColor = Palette.Text, Font = AboutCard.TitleFont, Name = "libraryEmptyTitle" };
            libraryEmptyText = new Label { UseMnemonic = false, Dock = DockStyle.Top, AutoSize = false, ForeColor = Palette.Muted, Font = Palette.Normal, Padding = new Padding(0, Rhythm.Related, 0, 0), Name = "libraryEmptyText" };
            libraryEmptyLink = new Label { UseMnemonic = false, Dock = DockStyle.Top, AutoSize = false, ForeColor = Palette.Accent, Font = Palette.Normal, Cursor = Cursors.Hand, Padding = new Padding(0, Rhythm.Related + 4, 0, 0), Name = "libraryEmptyLink", AccessibleRole = AccessibleRole.Link, TabStop = true };
            libraryEmptyLink.MouseEnter += delegate { libraryEmptyLink.ForeColor = Palette.Text; }; libraryEmptyLink.MouseLeave += delegate { libraryEmptyLink.ForeColor = Palette.Accent; };
            libraryEmptyLink.Click += delegate { ShowModLibraries(); };
            libraryEmpty.Controls.Add(libraryEmptyLink); libraryEmpty.Controls.Add(libraryEmptyText); libraryEmpty.Controls.Add(libraryEmptyTitle);
            libraryEmpty.Resize += delegate { FitLibraryEmpty(); };
            return libraryEmpty;
        }

        // Called after every scan (RefreshLibrary): no mods at all shows the note in place of the list.
        private void UpdateLibraryEmpty()
        {
            if (libraryEmpty == null) return;
            bool show = libraryScanned && library.Count == 0;
            if (show)
            {
                bool none = config.Libraries.Count == 0;
                libraryEmptyTitle.Text = none ? "No mod libraries yet" : "No mods found";
                libraryEmptyText.Text = none ? "Add the folder where you keep your mods." : "Your mod libraries are empty.";
                libraryEmptyLink.Text = none ? "Add a mod library →" : "Open Mod libraries →";
                libraryEmptyLink.AccessibleName = "Open Settings, Mod libraries";
                FitLibraryEmpty();
            }
            if (libraryEmpty.Visible != show) { libraryEmpty.Visible = show; modList.Visible = !show; }
        }

        private void FitLibraryEmpty()
        {
            int width = Math.Max(40, libraryEmpty.ClientSize.Width - libraryEmpty.Padding.Horizontal);
            foreach (Label label in new[] { libraryEmptyTitle, libraryEmptyText, libraryEmptyLink })
                label.Height = TextRenderer.MeasureText(label.Text.Length == 0 ? " " : label.Text, label.Font, new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak).Height + label.Padding.Vertical + 2;
        }

        private void ShowModLibraries()
        {
            ShowPage(settingsPage); LoadSettingsFields(); ShowSettingsTab(false);
            if (addLibraryButton != null && addLibraryButton.CanFocus) addLibraryButton.Focus();
        }
    }
}
