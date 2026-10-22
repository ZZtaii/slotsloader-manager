using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.6 (the user): in Extra outfit mode, "Add to outfit…" (end of the destination line) copies exactly the ticked
    // options of the open mod into an extra outfit that is already installed, instead of a new slot. AddPartsForm picks
    // the outfit. The engine is Installer.AddToOutfit (InstallerParts.cs).
    internal sealed partial class MainForm
    {
        private FlatButton addPartsButton;

        private FlatButton BuildAddPartsButton()
        {
            addPartsButton = Button("Add to outfit…", false, 128, delegate { AddPartsToOutfit(); });
            addPartsButton.Font = Palette.Small; addPartsButton.Margin = Padding.Empty; addPartsButton.Name = "addPartsButton";
            addPartsButton.AccessibleName = "Add the ticked options to an installed outfit";
            addPartsButton.AccessibleDescription = "Copies the files of the ticked options into an extra outfit you already have.";
            addPartsButton.Visible = false;
            return addPartsButton;
        }

        // The ticked options' files (no main-file or choose-one rule: a lone hair option is fine).
        private CompositionPlan AddSelection() { return package == null ? null : Installer.SelectionFiles(package, selectedOrder); }

        // Shown in Extra outfit mode for an open mod; usable once a ticked option has natives files.
        private void UpdateAddPartsButton()
        {
            if (addPartsButton == null) return;
            bool show = !IsReplaceMode && package != null;
            if (showAddParts != show) { showAddParts = show; LayoutVariantRow(); }
            CompositionPlan selection = show ? AddSelection() : null;
            addPartsButton.Enabled = show && !busy && missingRestoredOptions.Count == 0 && selection != null && selection.Errors.Count == 0 && selection.Files.Count > 0;
        }

        internal void AddPartsToOutfit()
        {
            if (busy || package == null) return;
            CompositionPlan selection = AddSelection();
            if (selection == null || selection.Errors.Count > 0 || selection.Files.Count == 0) { SetPageStatus(libraryPage, selection == null ? "Open a mod first." : String.Join(" ", selection.Errors.DefaultIfEmpty("Tick the options to add.")), Palette.Accent); return; }
            HashSet<string> codes = Installer.SelectionCodes(selection);
            string character = codes.Count == 1 && Characters.ByCode.ContainsKey(codes.First()) ? Characters.ByCode[codes.First()] : codes.Count == 0 ? package.Character : null;
            if (String.IsNullOrEmpty(character)) { SetPageStatus(libraryPage, codes.Count > 1 ? "These options change several fighters; tick one fighter's options." : "No fighter found in these options.", Palette.Accent); return; }
            // Outfits the Manager composed (enabled or disabled); manual drops and Install all variants copies are not rewritten.
            List<InstalledOutfit> outfits = installed.Where(o => o.Managed && String.Equals(o.Character, character, StringComparison.OrdinalIgnoreCase)).OrderByDescending(o => o.Enabled).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            InstalledOutfit target;
            if (!AddPartsForm.Choose(this, package.Name, Installer.OptionText(selection), selection.Files.Count, character, Installer.SelectionFolders(selection), outfits, out target)) return;
            string root = config.CostumeRoot, sevenZip = config.SevenZipPath;
            Work("Adding " + Installer.OptionText(selection) + " to " + target.Name + "…", delegate { return Installer.AddToOutfit(target, root, selection, sevenZip); }, delegate(InstallResult result)
            {
                RefreshInstalled("✓ " + result.Message);
            });
        }
    }

    // The outfit picker: the fighter's extra outfits as rows (name, the outfit slot it's built on). An outfit built on
    // another slot than the files' is shown muted with the reason and can't be picked.
    internal sealed class AddPartsForm : Form
    {
        private readonly OutfitRows rows;
        private readonly Label summary;
        private readonly FlatButton addButton;
        internal InstalledOutfit Target { get { return rows.Selected; } }

        internal static bool Choose(IWin32Window owner, string modName, string options, int fileCount, string character, HashSet<string> fileFolders, List<InstalledOutfit> outfits, out InstalledOutfit target)
        {
            target = null;
            if (Program.Headless) return false;
            using (var form = new AddPartsForm(modName, options, fileCount, character, fileFolders, outfits))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return false;
                target = form.Target;
                return target != null;
            }
        }

        internal AddPartsForm(string modName, string options, int fileCount, string character, HashSet<string> fileFolders, List<InstalledOutfit> outfits)
        {
            const int rowHeight = 52, visibleRows = 6;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Add to an installed outfit"; StartPosition = FormStartPosition.CenterParent;
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            var title = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font(UiFonts.Family, 12F, FontStyle.Bold), AutoEllipsis = true, Text = options };
            var source = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 22, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, Text = ReplacePlanner.N(fileCount, "file") + " from " + modName };
            var note = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 6, 0, 0), Text = "Copied into the outfit you pick; its files with the same names are replaced. Edit options and reinstalls of that outfit keep them." };
            var caption = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 28, Font = new Font(UiFonts.Family, 8F, FontStyle.Bold), ForeColor = Palette.Muted, Text = character.ToUpperInvariant() + " OUTFITS", TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4) };
            rows = new OutfitRows(rowHeight) { Dock = DockStyle.Top, Name = "addPartsOutfits", AccessibleName = character + " outfits", Empty = "No " + character + " outfit installed by the Manager yet. Install one first." };
            foreach (InstalledOutfit o in outfits)
            {
                List<string> built = Installer.OutfitFolders(o);
                string mismatch = Installer.FolderMismatch(fileFolders, built);
                string slot = built.Count == 0 ? "No outfit body" : String.Join(", ", built.Select(f => "Outfit " + Int32.Parse(f)));
                rows.Add(o, o.Name, slot + (o.Enabled ? "" : " · disabled") + (mismatch == null ? "" : " · these files are for " + mismatch), mismatch == null);
            }
            var listHost = new Panel { Dock = DockStyle.Top, Height = rowHeight * Math.Max(1, Math.Min(visibleRows, outfits.Count)) + 2, AutoScroll = outfits.Count > visibleRows, BackColor = Palette.Background };
            rows.Height = rowHeight * Math.Max(1, outfits.Count);
            listHost.Controls.Add(rows);
            summary = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(0, 14, 0, 0) };
            addButton = new FlatButton("Add " + ReplacePlanner.N(fileCount, "file"), true) { Width = 150, Dock = DockStyle.Right, Font = Palette.Small, Name = "addPartsAdd" }; addButton.Click += delegate { Accept(); };
            var cancel = new FlatButton("Cancel", false) { Width = 100, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(summary); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(addButton);
            Controls.Add(footer); Controls.Add(listHost); Controls.Add(caption); Controls.Add(note); Controls.Add(source); Controls.Add(title);
            ClientSize = new Size(520, Padding.Vertical + title.Height + source.Height + note.Height + caption.Height + listHost.Height + footer.Height);
            rows.SelectionChanged += delegate { UpdateSummary(); };
            rows.Activated += delegate { Accept(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } else if (e.KeyCode == Keys.Enter && addButton.Enabled) { e.Handled = true; Accept(); } };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            rows.SelectFirstUsable();
            ActiveControl = rows;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            InstalledOutfit target = Target;
            addButton.Enabled = target != null;
            summary.Text = target == null ? (rows.Count == 0 ? "" : "Choose an outfit.") : "Into " + target.Name;
        }

        private void Accept()
        {
            if (!addButton.Enabled) return;
            DialogResult = DialogResult.OK; Close();
        }

        // Rows drawn like the library list: name, then the slot line; the chosen row has the selection colour and an
        // accent rail. Click or Up/Down choose (only rows that can take the files), double-click or Enter adds.
        internal sealed class OutfitRows : Control
        {
            private sealed class Row { internal InstalledOutfit Outfit; internal string Title, Detail; internal bool Usable; }
            private readonly List<Row> items = new List<Row>();
            private readonly int logicalRow;
            private int selected = -1, hover = -1;
            internal string Empty = "";
            internal event EventHandler SelectionChanged, Activated;
            internal OutfitRows(int rowHeight)
            {
                logicalRow = rowHeight;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true; BackColor = Palette.Background;
            }
            internal int Count { get { return items.Count; } }
            internal InstalledOutfit Selected { get { return selected >= 0 && selected < items.Count && items[selected].Usable ? items[selected].Outfit : null; } }
            internal void Add(InstalledOutfit outfit, string title, string detail, bool usable) { items.Add(new Row { Outfit = outfit, Title = title ?? "", Detail = detail ?? "", Usable = usable }); Invalidate(); }
            internal void SelectFirstUsable() { int i = items.FindIndex(r => r.Usable); if (i >= 0) Select(i); }
            private int RowHeight { get { return DpiMetrics.Px(this, logicalRow); } }
            private int IndexAt(int y) { int i = y / Math.Max(1, RowHeight); return i >= 0 && i < items.Count ? i : -1; }
            private void Select(int index)
            {
                if (index < 0 || index >= items.Count || !items[index].Usable || index == selected) return;
                selected = index; Invalidate();
                // Keep the row in view when the host scrolls.
                var host = Parent as ScrollableControl;
                if (host != null && host.AutoScroll)
                {
                    int top = index * RowHeight, view = -host.AutoScrollPosition.Y;
                    if (top < view) host.AutoScrollPosition = new Point(0, top);
                    else if (top + RowHeight > view + host.ClientSize.Height) host.AutoScrollPosition = new Point(0, top + RowHeight - host.ClientSize.Height);
                }
                var handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty);
            }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) Select(IndexAt(e.Y)); }
            protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); int i = IndexAt(e.Y); if (i >= 0 && i == selected) { var handler = Activated; if (handler != null) handler(this, EventArgs.Empty); } }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = IndexAt(e.Y); if (h != hover) { hover = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
            protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                int step = e.KeyCode == Keys.Up || e.KeyCode == Keys.W ? -1 : e.KeyCode == Keys.Down || e.KeyCode == Keys.S ? 1 : 0;
                if (step == 0) return;
                for (int i = selected + step; i >= 0 && i < items.Count; i += step) if (items[i].Usable) { Select(i); break; }
                e.Handled = true;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics; int h = RowHeight;
                using (Brush bg = new SolidBrush(Palette.Background)) g.FillRectangle(bg, ClientRectangle);
                const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                if (items.Count == 0) { TextRenderer.DrawText(g, Empty, Palette.Small, new Rectangle(DpiMetrics.Px(this, 12), 0, Width - DpiMetrics.Px(this, 24), Height), Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix); return; }
                for (int i = 0; i < items.Count; i++)
                {
                    var r = new Rectangle(0, i * h, Width, h); if (!r.IntersectsWith(e.ClipRectangle)) continue;
                    Row row = items[i]; bool on = i == selected;
                    Color fill = on ? Palette.Selected : i == hover && row.Usable ? Palette.Raised : Palette.Background;
                    using (Brush b = new SolidBrush(fill)) g.FillRectangle(b, r);
                    using (Brush rail = new SolidBrush(on ? Palette.Accent : Palette.Border)) g.FillRectangle(rail, r.Left + DpiMetrics.Px(this, 2), r.Top + DpiMetrics.Px(this, 10), DpiMetrics.Px(this, 3), h - DpiMetrics.Px(this, 20));
                    int x = r.Left + DpiMetrics.Px(this, 14), w = r.Width - DpiMetrics.Px(this, 24);
                    TextRenderer.DrawText(g, row.Title, Palette.Strong, new Rectangle(x, r.Top + DpiMetrics.Px(this, 6), w, DpiMetrics.Px(this, 22)), row.Usable ? Palette.Text : Palette.Muted, one);
                    TextRenderer.DrawText(g, row.Detail, Palette.Tiny, new Rectangle(x, r.Top + DpiMetrics.Px(this, 28), w, DpiMetrics.Px(this, 18)), Palette.Muted, one);
                    if (i < items.Count - 1) using (var pen = new Pen(Palette.Border)) g.DrawLine(pen, r.Left + DpiMetrics.Px(this, 12), r.Bottom - 1, r.Right - DpiMetrics.Px(this, 12), r.Bottom - 1);
                }
                if (Focused && ShowFocusCues && selected >= 0) using (var pen = new Pen(Palette.Text)) g.DrawRectangle(pen, 0, selected * h, Width - 1, h - 1);
            }
        }
    }
}
