using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.6 (the user): the row under the library search. "Deep search" lets name words match a bundle's option
    // folders and option names; Name · File switches the list between each mod's own name (modinfo.ini) and its
    // file name (Ctrl+Shift+N). Both are remembered in view-settings.json. Filter and search changes scroll the
    // list back to the top.
    internal sealed partial class MainForm
    {
        internal static readonly string[] LibraryNameModes = { "Name", "File" };
        private bool libraryFileNames = true;
        private SegmentedChoice libraryNameChoice;
        private DeepSearchCheckBox deepSearch;
        private bool libraryToTop;
        private int quietFilters;   // > 0 while the app itself changes the Filters panel (refresh, restore): no scroll

        internal bool LibraryFileNames { get { return libraryFileNames; } }
        internal bool DeepSearchOn { get { return deepSearch != null && deepSearch.Checked; } }

        private Panel BuildSearchOptions(Panel page)
        {
            var row = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Rhythm.Related), Padding = Padding.Empty, Name = "searchOptions" };
            deepSearch = new DeepSearchCheckBox(page) { Text = "Deep search", Dock = DockStyle.Left, AutoSize = true, ForeColor = Palette.Muted, Font = Palette.Small, Name = "deepSearch", AccessibleName = "Deep search: also match option folders and option names", Padding = Padding.Empty, Margin = Padding.Empty };
            Deactivate += delegate { deepSearch.DismissHint(); };
            deepSearch.CheckedChanged += delegate { libraryToTop = true; searchTimer.Stop(); ApplyFilters(); QueueViewSave(); };
            libraryNameChoice = new SegmentedChoice(LibraryNameModes) { Dock = DockStyle.Right, Width = 112, Name = "libraryNames", AccessibleName = "Show mod names or file names", CheckMark = false };
            libraryNameChoice.Descriptions[0] = "The name in the mod's modinfo.ini (the file name when it has none)";
            libraryNameChoice.Descriptions[1] = "The archive or folder name. Shortcut: " + Shortcuts.Display(ShortcutAction.ToggleNames);
            libraryNameChoice.SelectedIndex = libraryFileNames ? 1 : 0;
            libraryNameChoice.SelectionChanged += delegate { SetLibraryFileNames(libraryNameChoice.SelectedIndex == 1, true); };
            row.Controls.Add(deepSearch); row.Controls.Add(libraryNameChoice);
            // A narrow list gives the switch what the check box leaves (never under it).
            Action fit = delegate { int room = row.ClientSize.Width - deepSearch.PreferredSize.Width - Px(6); int width = Math.Max(Px(72), Math.Min(Px(112), room)); if (libraryNameChoice.Width != width) libraryNameChoice.Width = width; };
            row.Resize += delegate { fit(); }; dpiLayouts.Add(fit);
            return row;
        }

        // Name shown for a library row (and used for its type-ahead and A–Z ties).
        internal string LibraryRowName(LibraryEntry entry) { return entry == null ? "" : entry.Recipe != null ? entry.Recipe.Name : entry.ShownName(libraryFileNames); }   // a recipe has no file name

        internal void SetLibraryFileNames(bool fileNames, bool save)
        {
            if (libraryNameChoice != null && libraryNameChoice.SelectedIndex != (fileNames ? 1 : 0)) libraryNameChoice.SelectedIndex = fileNames ? 1 : 0;
            if (libraryFileNames == fileNames) return;
            libraryFileNames = fileNames;
            if (modList != null)
            {
                bool previous = selectingMod; selectingMod = true;
                modList.BeginUpdate();
                try { foreach (ListViewItem item in modList.Items) { string name = LibraryRowName(item.Tag as LibraryEntry); if (item.Text != name) item.Text = name; } }
                finally { modList.EndUpdate(); selectingMod = previous; }
                searchTimer.Stop(); ApplyFilters();   // ties sort by the shown name
                modList.Invalidate();
            }
            if (save) QueueViewSave();
        }

        private void RestoreLibraryView(ViewPreferences saved)
        {
            SetLibraryFileNames(!String.Equals(saved == null ? null : saved.LibraryNames, "name", StringComparison.OrdinalIgnoreCase), false);
            if (deepSearch != null) deepSearch.Checked = saved != null && saved.DeepSearch;
        }

        // The search box's query with the Deep search choice.
        private LibrarySearch.Query LibraryQuery() { return LibrarySearch.Parse(search.Text, SearchCharacterTerm, DeepSearchOn); }

        // Called by ApplyFilters after the rows are in place: a search or filter change shows the first match.
        private void ScrollLibraryToTopIfAsked()
        {
            if (!libraryToTop) return;
            libraryToTop = false;
            if (modList != null && modList.Items.Count > 0) { modList.EnsureVisible(0); try { modList.TopItem = modList.Items[0]; } catch (NullReferenceException) { } }
        }
    }
    internal sealed class DeepSearchCheckBox : CheckBox
    {
        private const string HintText = "Also searches option names and folders.";
        private readonly Timer hoverDelay = new Timer { Interval = 1500 };
        private readonly Label hint = new Label { AutoSize = true, UseMnemonic = false, Visible = false, Text = HintText, AccessibleRole = AccessibleRole.ToolTip };

        internal DeepSearchCheckBox(Panel page)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AccessibleDescription = HintText;
            page.Controls.Add(hint);
            hoverDelay.Tick += delegate
            {
                hoverDelay.Stop();
                if (!Visible || !Enabled || MouseButtons != MouseButtons.None || !ClientRectangle.Contains(PointToClient(MousePosition))) return;
                hint.Font = Font;
                hint.Padding = new Padding(DpiMetrics.Px(this, 10), DpiMetrics.Px(this, 6), DpiMetrics.Px(this, 10), DpiMetrics.Px(this, 6));
                hint.BackColor = SystemInformation.HighContrast ? SystemColors.Info : Palette.Raised;
                hint.ForeColor = SystemInformation.HighContrast ? SystemColors.InfoText : Palette.Text;
                Point below = page.PointToClient(PointToScreen(new Point(0, Height + DpiMetrics.Px(this, 4))));
                hint.Location = new Point(Math.Max(0, Math.Min(below.X, page.ClientSize.Width - hint.PreferredWidth)), below.Y);
                hint.BringToFront();
                hint.Visible = true;
            };
            hint.Paint += delegate(object sender, PaintEventArgs e)
            {
                ControlPaint.DrawBorder(e.Graphics, hint.ClientRectangle, SystemInformation.HighContrast ? SystemColors.InfoText : Palette.Border, ButtonBorderStyle.Solid);
            };
            MouseEnter += delegate { ArmHint(); };
            MouseMove += delegate { ArmHint(); };
            MouseLeave += delegate { DismissHint(); };
            MouseDown += delegate { DismissHint(); };
            MouseUp += delegate(object sender, MouseEventArgs e) { if (ClientRectangle.Contains(e.Location)) ArmHint(); };
            VisibleChanged += delegate { if (!Visible) DismissHint(); };
            EnabledChanged += delegate { if (!Enabled) DismissHint(); };
        }

        private void ArmHint()
        {
            if (!hoverDelay.Enabled && !hint.Visible && MouseButtons == MouseButtons.None) hoverDelay.Start();
        }

        internal void DismissHint()
        {
            hoverDelay.Stop();
            hint.Visible = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hoverDelay.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
            // ButtonBase can skip the background pass when the checkbox paints itself.
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            int size = DpiMetrics.Px(this, 13), gap = DpiMetrics.Px(this, 4), y = (Height - size) / 2;
            var box = new RectangleF(0.5f, y + 0.5f, size - 1, size - 1);
            Glyphs.FillRounded(e.Graphics, Checked ? Palette.Selected : BackColor, box, DpiMetrics.Px(this, 2));
            Glyphs.DrawRounded(e.Graphics, Enabled ? Palette.Muted : Palette.Border, DpiMetrics.Px(this, 1), box, DpiMetrics.Px(this, 2));
            if (Checked)
            {
                var smoothing = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Enabled ? Palette.Text : Palette.Muted, 1.4f * size / 13f))
                    e.Graphics.DrawLines(pen, new[] { new PointF(size * .23f, y + size * .51f),
                        new PointF(size * .44f, y + size * .71f), new PointF(size * .79f, y + size * .29f) });
                e.Graphics.SmoothingMode = smoothing;
            }
            var label = new Rectangle(size + gap, 0, Math.Max(0, Width - size - gap), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, label, Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, label, ForeColor, BackColor);
        }
    }
}
