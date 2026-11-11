using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (user request): "Added parts" on the Installed page. The parts other mods added to one outfit, the
    // winner on top (as "#1 wins" on the Installed page): a part overwrites the files it shares with the parts below
    // it, and the outfit's own files are always underneath. Drag a row (or Move up / Move down, Alt+Up / Alt+Down)
    // to change which one wins; Remove takes the selected part off. Nothing is written until Save.
    // The install record keeps the parts in the order they are applied (the last one wins), so the list here is that
    // order reversed.
    internal sealed class AddedPartsForm : Form
    {
        private readonly PartRows rows;
        private readonly Label note;
        private readonly FlatButton save, up, down, remove;
        private readonly List<PartGraft> original;
        // The parts to keep, in the order they are applied (the last one wins): Installer.SetParts' input.
        internal List<PartGraft> Wanted { get { List<PartGraft> list = rows.Parts; list.Reverse(); return list; } }
        internal bool Changed { get { return !Wanted.SequenceEqual(original); } }
        internal PartRows RowsForTest { get { return rows; } }

        internal static bool Choose(IWin32Window owner, string outfitName, List<PartGraft> parts, Func<PartGraft, bool> sourceGone, Func<List<PartGraft>, string> saveRecipe, out List<PartGraft> wanted, string ownMod = null, string ownOptions = null)
        {
            wanted = null;
            if (Program.Headless) return false;
            using (var form = new AddedPartsForm(outfitName, parts, sourceGone, saveRecipe, ownMod, ownOptions))
            {
                if (form.ShowDialog(owner) != DialogResult.OK || !form.Changed) return false;
                wanted = form.Wanted;
                return true;
            }
        }

        private string said;   // what Save as recipe answered, shown until the list changes
        // saveRecipe: writes the outfit with this list of parts (in the order applied) down as a recipe and returns
        // what to say; null = no Save as recipe button. It changes nothing on the installed outfit.
        internal string OwnModTextForTest { get; private set; }
        // ownMod / ownOptions: the mod the outfit itself is installed from and the options it has, for the last row.
        internal AddedPartsForm(string outfitName, List<PartGraft> parts, Func<PartGraft, bool> sourceGone, Func<List<PartGraft>, string> saveRecipe = null, string ownMod = null, string ownOptions = null)
        {
            const int rowHeight = 52; int visibleRows = Math.Max(3, Math.Min(7, parts.Count));
            original = parts.ToList();
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Added parts"; StartPosition = FormStartPosition.CenterParent; Name = "addedPartsForm";
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            var title = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font(UiFonts.Family, 12F, FontStyle.Bold), AutoEllipsis = true, Text = "Added to " + outfitName };
            note = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 2, 0, 0) };
            rows = new PartRows(rowHeight, sourceGone) { Dock = DockStyle.Top, Name = "addedPartsRows", AccessibleName = "Added parts, the winner first" };
            var shown = parts.ToList(); shown.Reverse();   // the last one applied wins: it is the top row
            rows.SetParts(shown);
            var listHost = new Panel { Dock = DockStyle.Top, Height = rowHeight * visibleRows + 2, AutoScroll = true, BackColor = Palette.Background };
            rows.Height = rowHeight * Math.Max(1, parts.Count);
            listHost.Controls.Add(rows);
            // 1.0.18 (the user: "it should list the original mod too"): the outfit's own mod is the last row of the
            // list, drawn like the parts above it with its place in the order. It is always at the bottom and can't be
            // moved or removed, which its line says; the parts above it win the files they share with it.
            string ownName = String.IsNullOrWhiteSpace(ownMod) ? outfitName : ownMod;
            string ownDetail = "The outfit's own mod" + (String.IsNullOrWhiteSpace(ownOptions) ? "" : " · " + ownOptions) + " · always at the bottom";
            var baseRow = new Panel { Dock = DockStyle.Top, Height = rowHeight + 2, BackColor = Palette.Background, Name = "addedPartsOwnMod", AccessibleName = ownName + ", " + ownDetail };
            baseRow.Paint += delegate(object s, PaintEventArgs e)
            {
                Func<int, int> px = v => DpiMetrics.Px(this, v);
                const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                using (Pen line = new Pen(Palette.Border)) e.Graphics.DrawLine(line, px(14), 0, baseRow.Width, 0);
                int rank = px(44), x = px(14), w = baseRow.Width - px(28) - rank;
                TextRenderer.DrawText(e.Graphics, "#" + (rows.Count + 1), Palette.Small, new Rectangle(x, 0, rank, baseRow.Height), Palette.Muted, one | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(e.Graphics, ownName, Palette.Strong, new Rectangle(x + rank, px(7), w, px(22)), Palette.Muted, one);
                TextRenderer.DrawText(e.Graphics, ownDetail, Palette.Tiny, new Rectangle(x + rank, px(29), w, px(18)), Palette.Muted, one);
            };
            OwnModTextForTest = ownName + " | " + ownDetail;
            var recipeRow = new Panel { Dock = DockStyle.Top, Height = saveRecipe == null ? 0 : 40, Padding = new Padding(0, 6, 0, 0), Visible = saveRecipe != null };
            var recipe = new FlatButton("Save as recipe", false) { Dock = DockStyle.Left, Width = 150, Font = Palette.Small, Name = "addedPartsRecipe" };
            recipe.Click += delegate
            {
                try { said = saveRecipe(Wanted); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; said = ex.Message; }
                UpdateState();
            };
            recipeRow.Controls.Add(recipe);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 14, 0, 0) };
            save = new FlatButton("Save", true) { Width = 110, Dock = DockStyle.Right, Font = Palette.Small, Name = "addedPartsSave" }; save.Click += delegate { Accept(); };
            var cancel = new FlatButton("Cancel", false) { Width = 96, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            up = new FlatButton("Move up", false) { Width = 96, Dock = DockStyle.Left, Font = Palette.Small, Name = "addedPartsUp" }; up.Click += delegate { rows.MoveSelected(-1); rows.Focus(); };
            down = new FlatButton("Move down", false) { Width = 104, Dock = DockStyle.Left, Font = Palette.Small, Name = "addedPartsDown" }; down.Click += delegate { rows.MoveSelected(1); rows.Focus(); };
            remove = new FlatButton("Remove", false) { Width = 92, Dock = DockStyle.Left, Font = Palette.Small, Name = "addedPartsRemove", Danger = true }; remove.Click += delegate { rows.RemoveSelected(); rows.Focus(); };
            footer.Controls.Add(new Panel { Dock = DockStyle.Fill });
            footer.Controls.Add(remove); footer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); footer.Controls.Add(down); footer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); footer.Controls.Add(up);
            footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(save);
            Controls.Add(footer); Controls.Add(recipeRow); Controls.Add(baseRow); Controls.Add(listHost); Controls.Add(note); Controls.Add(title);
            ClientSize = new Size(600, Padding.Vertical + title.Height + note.Height + listHost.Height + baseRow.Height + recipeRow.Height + footer.Height);
            rows.Changed += delegate { said = null; rows.Height = DpiMetrics.Px(this, rowHeight) * Math.Max(1, rows.Count); baseRow.Invalidate(); UpdateState(); };
            rows.SelectionChanged += delegate { UpdateState(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter && save.Enabled) { e.Handled = true; Accept(); }
                else if (e.KeyCode == Keys.Delete && remove.Enabled) { e.Handled = true; rows.RemoveSelected(); }
            };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            ActiveControl = rows;
            UpdateState();
        }

        private void UpdateState()
        {
            save.Enabled = Changed;
            up.Enabled = rows.CanMove(-1); down.Enabled = rows.CanMove(1); remove.Enabled = rows.SelectedIndex >= 0;
            int removed = original.Count - rows.Count;
            note.Text = said != null ? said : removed > 0 ? ReplacePlanner.N(removed, "part") + " will be removed. Save puts the outfit together again from its own mod with the parts listed here."
                : Changed ? "Save puts the outfit together again from its own mod with the parts in this order."
                : "The top part wins: it overwrites the files it shares with the parts below it. Drag to change the order, or remove a part.";
        }
        private void Accept() { if (!save.Enabled) return; DialogResult = DialogResult.OK; Close(); }
    }
}
