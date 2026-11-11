using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (the user, after trying the first, flat list: "it's hard to tell since there's no groups and it's all
    // under the same level", and "allow image previews too"): the options of one mod for the recipe builder, laid out
    // as the mod lays them out. A group is a heading with its options under it; a choose-one group has round marks
    // and ticking one option unticks the others; the heading says "Required · choose one" when one is needed. The
    // right side shows the image of the option under the pointer (else the selected one), as the Mod library does.
    // For the base, `validate` is the mod's own rules: the reason shows under the list and Use these waits for it.
    internal sealed class OptionPickForm : Form
    {
        private sealed class PickRow { internal bool Header, Radio; internal string Text = "", Tag = ""; internal ModComponent Option, Group; }
        private readonly List<PickRow> rows = new List<PickRow>();
        private readonly List<ModComponent> options;
        private readonly HashSet<string> ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Image> images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly ModPackage package;
        private readonly string sevenZip;
        private readonly Func<List<string>, string> validate;
        private readonly ListBox list;
        private readonly PictureBox preview;
        private readonly Label previewCaption, status;
        private readonly FlatButton ok;
        private int hover = -1; private string shownId;

        // The ticked options, in the mod's menu order.
        internal List<string> Picked { get { return options.Where(c => ticked.Contains(c.Id)).Select(c => c.Id).ToList(); } }
        internal int RowCountForTest { get { return rows.Count; } }
        internal string RowTextForTest(int index) { PickRow r = rows[index]; return (r.Header ? "[" + r.Text + (r.Tag.Length > 0 ? " · " + r.Tag : "") + "]" : (r.Radio ? "( ) " : "[ ] ") + r.Text); }
        internal string StatusForTest { get { return status.Text; } }
        internal bool CanAcceptForTest { get { return ok.Enabled; } }

        // fits: when given, only the options it accepts are listed (the ones made for the base's outfit), and the hint
        // says how many are left out.
        internal static List<string> Choose(IWin32Window owner, string title, string hint, ModPackage package, ICollection<string> ticked, string sevenZip = "", Func<List<string>, string> validate = null, Func<ModComponent, bool> fits = null)
        {
            if (Program.Headless) return null;
            using (var form = new OptionPickForm(title, hint, package, ticked, sevenZip, validate, fits))
                return form.ShowDialog(owner) == DialogResult.OK ? form.Picked : null;
        }

        internal OptionPickForm(string title, string hint, ModPackage package, ICollection<string> preTicked, string sevenZip = "", Func<List<string>, string> validate = null, Func<ModComponent, bool> fits = null)
        {
            this.package = package; this.sevenZip = sevenZip ?? ""; this.validate = validate;
            List<ModComponent> every = RecipeDraft.Options(package);
            options = fits == null ? every : every.Where(fits).ToList();
            int leftOut = every.Count - options.Count;
            if (leftOut > 0) hint = (hint ?? "") + " " + ReplacePlanner.N(leftOut, "option") + " made for other outfits " + (leftOut == 1 ? "is" : "are") + " not shown.";
            foreach (ModComponent c in options) if (preTicked != null && preTicked.Contains(c.Id, StringComparer.OrdinalIgnoreCase)) ticked.Add(c.Id);
            // Options outside any group first, then each group (in the mod's order) with its options under it.
            Func<ModComponent, ModComponent> groupOf = delegate(ModComponent c) { ModComponent p = c.ParentId == null ? null : package.Find(c.ParentId); return p != null && p.IsDummy && p.ParentId != null ? p : null; };
            foreach (ModComponent c in options.Where(c => groupOf(c) == null)) rows.Add(new PickRow { Option = c, Text = c.Name });
            foreach (ModComponent group in options.Select(groupOf).Where(g => g != null).Distinct())
            {
                bool choice = package.IsChoice(group);
                rows.Add(new PickRow { Header = true, Group = group, Text = group.Name, Tag = choice ? (group.RequiresChoice ? "Required · choose one" : "Choose one") : "Any of these" });
                foreach (ModComponent c in options.Where(c => groupOf(c) == group)) rows.Add(new PickRow { Option = c, Group = group, Radio = choice, Text = c.Name });
            }

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Options"; StartPosition = FormStartPosition.CenterParent; Name = "optionPickForm";
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            ClientSize = new Size(920, 560);
            var heading = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoEllipsis = true, Text = title };
            var note = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 40, ForeColor = Palette.Muted, Font = Palette.Small, Text = hint };
            var side = new Panel { Dock = DockStyle.Right, Width = 340, Padding = new Padding(14, 0, 0, 0) };
            previewCaption = new Label { UseMnemonic = false, Dock = DockStyle.Bottom, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 6, 0, 0), AutoEllipsis = true };
            preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Palette.Background, Name = "optionPickPreview", AccessibleName = "Preview of the option" };
            side.Controls.Add(preview); side.Controls.Add(previewCaption);
            list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 32, BorderStyle = BorderStyle.None, BackColor = Palette.Background, ForeColor = Palette.Text, Font = Palette.Small, IntegralHeight = false, Name = "optionPickList", AccessibleName = "Options of this mod" };
            foreach (PickRow r in rows) list.Items.Add(r.Text);
            status = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Accent, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 14, 0, 0) };
            ok = new FlatButton("Use these", true) { Width = 120, Dock = DockStyle.Right, Font = Palette.Small, Name = "optionPickOk" }; ok.Click += delegate { if (ok.Enabled) { DialogResult = DialogResult.OK; Close(); } };
            var cancel = new FlatButton("Cancel", false) { Width = 96, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(status); footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(ok);
            Controls.Add(list); Controls.Add(side); Controls.Add(footer); Controls.Add(note); Controls.Add(heading);

            list.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Toggle(list.IndexFromPoint(e.Location)); };
            list.MouseMove += delegate(object s, MouseEventArgs e) { int at = list.IndexFromPoint(e.Location); if (at != hover) { hover = at; ShowPreview(at); } };
            list.MouseLeave += delegate { hover = -1; ShowPreview(list.SelectedIndex); };
            list.SelectedIndexChanged += delegate { if (hover < 0) ShowPreview(list.SelectedIndex); };
            list.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Toggle(list.SelectedIndex); e.Handled = e.SuppressKeyPress = true; } };
            list.DrawItem += DrawRow;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            Disposed += delegate { preview.Image = null; foreach (Image image in images.Values) if (image != null) image.Dispose(); images.Clear(); };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            list.BackColor = preview.BackColor = Palette.Background; list.ItemHeight = Math.Min(255, DpiMetrics.Px(this, 32));   // a ListBox's item height is in pixels
            int first = rows.FindIndex(r => !r.Header); if (first >= 0) list.SelectedIndex = first;
            ShowPreview(first); Check();
        }

        internal void Toggle(int index)
        {
            if (index < 0 || index >= rows.Count || rows[index].Header) return;
            PickRow row = rows[index]; string id = row.Option.Id;
            if (!row.Radio) { if (!ticked.Remove(id)) ticked.Add(id); }
            else if (ticked.Contains(id)) { if (!row.Group.RequiresChoice) ticked.Remove(id); }   // a required choice can only be changed, not emptied
            else { foreach (PickRow other in rows.Where(r => !r.Header && r.Group == row.Group)) ticked.Remove(other.Option.Id); ticked.Add(id); }
            list.Invalidate(); Check();
        }
        // By option name (tests and screenshots).
        internal void ToggleByName(string name) { Toggle(rows.FindIndex(r => !r.Header && String.Equals(r.Text, name, StringComparison.OrdinalIgnoreCase))); }

        private void Check()
        {
            List<string> picked = Picked; string why = null;
            if (picked.Count == 0) why = "Tick at least one option.";
            else if (validate != null) { try { why = validate(picked); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; why = ex.Message; } }
            status.Text = why ?? ""; ok.Enabled = why == null;
        }

        private void ShowPreview(int index)
        {
            ModComponent option = index >= 0 && index < rows.Count ? (rows[index].Option ?? rows[index].Group) : null;
            string id = option == null ? null : option.Id;
            if (id == shownId) return;
            shownId = id;
            Image image = null;
            if (id != null && !images.TryGetValue(id, out image))
            {
                try
                {
                    byte[] bytes = package.PreviewBytes(id, sevenZip);
                    using (PreviewImageResult decoded = PreviewImageResult.Decode(bytes)) image = decoded.TakeImage();
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                images[id] = image;
            }
            preview.Image = image;
            previewCaption.Text = option == null ? "" : image == null ? "No preview image for " + option.Name + "." : option.Name;
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= rows.Count) return;
            PickRow row = rows[e.Index]; Rectangle b = e.Bounds; Graphics g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) != 0 && !row.Header;
            using (Brush fill = new SolidBrush(selected ? Palette.Selected : Palette.Background)) g.FillRectangle(fill, b);
            const TextFormatFlags one = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int pad = DpiMetrics.Px(this, 10);
            if (row.Header)
            {
                int tagWidth = TextRenderer.MeasureText(g, row.Tag, Palette.Tiny, new Size(int.MaxValue, b.Height), one).Width + pad;
                TextRenderer.DrawText(g, row.Text, Palette.Strong, new Rectangle(b.Left + pad, b.Top, Math.Max(1, b.Width - tagWidth - pad * 2), b.Height), Palette.Text, one);
                TextRenderer.DrawText(g, row.Tag, Palette.Tiny, new Rectangle(b.Right - tagWidth - pad, b.Top, tagWidth, b.Height), row.Tag.StartsWith("Required", StringComparison.Ordinal) ? Palette.Accent : Palette.Muted, one | TextFormatFlags.Right);
                using (var pen = new Pen(Palette.Border)) g.DrawLine(pen, b.Left + pad, b.Top, b.Right - pad, b.Top);
                return;
            }
            bool on = ticked.Contains(row.Option.Id);
            int box = DpiMetrics.Px(this, 16), indent = row.Group == null ? pad : pad + DpiMetrics.Px(this, 18);
            var mark = new Rectangle(b.Left + indent, b.Top + (b.Height - box) / 2, box, box);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(on ? Palette.Accent : Palette.Muted, Math.Max(1F, DeviceDpi / 96F * 1.4F)))
            { if (row.Radio) g.DrawEllipse(pen, mark); else g.DrawRectangle(pen, mark); }
            if (on) using (Brush inner = new SolidBrush(Palette.Accent))
            { Rectangle dot = Rectangle.Inflate(mark, -DpiMetrics.Px(this, 4), -DpiMetrics.Px(this, 4)); if (row.Radio) g.FillEllipse(inner, dot); else g.FillRectangle(inner, dot); }
            string files = ReplacePlanner.N(row.Option.Assets.Count, "file");
            int filesWidth = TextRenderer.MeasureText(g, files, Palette.Tiny, new Size(int.MaxValue, b.Height), one).Width + pad;
            TextRenderer.DrawText(g, row.Text, Palette.Small, new Rectangle(mark.Right + pad, b.Top, Math.Max(1, b.Right - mark.Right - pad * 2 - filesWidth), b.Height), on ? Palette.Text : Palette.Muted, one);
            TextRenderer.DrawText(g, files, Palette.Tiny, new Rectangle(b.Right - filesWidth - pad, b.Top, filesWidth, b.Height), Palette.Muted, one | TextFormatFlags.Right);
        }
    }
}
