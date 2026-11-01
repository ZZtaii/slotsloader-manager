using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Installs mapped Fluffy preset entries in preset order. Each entry uses its chosen mode:
    // Replace original (tool mods always) or Extra outfit. Failures are reported and skipped.
    internal static class PresetInstaller
    {
        internal static string Install(AppConfig config, ReplaceEngine engine, List<PresetMapping> mappings)
        {
            int ok = 0; var failed = new List<string>(); int index = 0;
            var selected = mappings.Where(m => m.Include && m.Entry != null).ToList();
            var replacements = selected.Where(m => m.Mode == InstallModes.Replace || m.Kind == "Tool").ToList();
            engine.ImportLayers(PresetLayer.Ordered(replacements), config.SevenZipPath);
            ok = replacements.Count;
            foreach (PresetMapping m in selected.Except(replacements))
            {
                OperationContext.ThrowIfCancellationRequested();
                OperationContext.Report("Installing " + m.DisplayName, index++, selected.Count);
                try
                {
                    string name = CleanName(m.DisplayName);
                    if (String.IsNullOrEmpty(m.Character)) throw new IOException("no character detected for an extra outfit");
                    CompositionPlan composition = Composer.Plan(m.Package, m.OptionIds);
                    if (!composition.CanInstall) throw new IOException(String.Join(" ", composition.Errors));
                    var installed = Installer.ListInstalled(config.CostumeRoot);
                    string unique = name; int n = 2;
                    while (installed.Any(o => String.Equals(o.Character, m.Character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, unique, StringComparison.OrdinalIgnoreCase))) unique = name + " " + n++;
                    Installer.Install(composition, config.CostumeRoot, m.Character, unique, config.SevenZipPath, false);
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; failed.Add(m.DisplayName + ": " + ex.Message); }
            }
            return "✓ Installed " + ok + " of " + selected.Count + " preset mods" + (failed.Count > 0 ? " · failed: " + String.Join(" | ", failed.Take(4)) + (failed.Count > 4 ? " …" : "") : "") + ". Restart SF6 to see them.";
        }

        internal static string CleanName(string name)
        {
            string cleaned = (name ?? "Imported mod").Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(invalid.ToString(), " ");
            cleaned = cleaned.Trim().TrimEnd('.').Trim();
            if (cleaned.StartsWith(".")) cleaned = cleaned.TrimStart('.');
            return cleaned.Length == 0 ? "Imported mod" : cleaned;
        }
    }

    // Review of a mapped Fluffy preset: include/exclude entries and choose each costume's mode.
    internal sealed class FluffyPresetForm : Form
    {
        private readonly List<PresetMapping> mappings;
        private readonly ListView list;
        private readonly Label summary;

        internal static bool Review(IWin32Window owner, string presetName, List<PresetMapping> mappings)
        {
            if (Program.Headless) return false;
            using (var form = new FluffyPresetForm(presetName, mappings)) return form.ShowDialog(owner) == DialogResult.OK;
        }

        internal FluffyPresetForm(string presetName, List<PresetMapping> mappings)
        {
            this.mappings = mappings;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Import external preset · " + presetName; ClientSize = new Size(960, 600); MinimumSize = new Size(700, 420); StartPosition = FormStartPosition.CenterParent;
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(16);
            AppBranding.Apply(this);
            var intro = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Text = "Each row is an archive. Selected replacements are enabled together at the top of Installed. Later preset positions win shared files. Double-click an outfit row to install it as one extra outfit." };
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, ShowItemToolTips = true, CheckBoxes = true, FullRowSelect = true, BackColor = Palette.Background, ForeColor = Palette.Text, BorderStyle = BorderStyle.None, Font = Palette.Small, HideSelection = false };
            list.Columns.Add("Preset entry", 230); list.Columns.Add("Mod and options", 360); list.Columns.Add("Kind", 80); list.Columns.Add("Install as", 140); list.Columns.Add("Character", 90);
            list.OwnerDraw = true;
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (var background = new SolidBrush(Palette.Raised)) e.Graphics.FillRectangle(background, e.Bounds);
                int inset = DpiMetrics.Px(list, 5);
                Rectangle text = Rectangle.Inflate(e.Bounds, -inset, 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, text, Palette.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            // The native header paints any space beyond its last column using the Windows window colour.
            // Let Character fill that space, including after resizing a column or the window.
            list.ClientSizeChanged += delegate { FitLastHeader(); };
            list.ColumnWidthChanged += delegate { FitLastHeader(); };
            Shown += delegate { FitLastHeader(); };
            NativeDpiFont.Attach(list);
            foreach (PresetMapping m in mappings)
            {
                var row = new ListViewItem(String.Join(", ", m.Sections.Select(s => (s.Order + 1).ToString())) + " · " + m.DisplayName) { Tag = m, ToolTipText = m.Message, Checked = m.Include && m.Entry != null };
                row.SubItems.Add(m.Message); row.SubItems.Add(m.Entry == null ? (m.Ambiguous ? "Ambiguous" : "Not found") : KindLabel(m.Kind)); row.SubItems.Add(ModeLabel(m)); row.SubItems.Add(m.Character);
                if (m.Entry == null) row.ForeColor = Palette.Muted;
                list.Items.Add(row);
            }
            list.ItemCheck += delegate(object s, ItemCheckEventArgs e) { var m = (PresetMapping)list.Items[e.Index].Tag; if (m.Entry == null) e.NewValue = CheckState.Unchecked; };
            list.ItemChecked += delegate(object s, ItemCheckedEventArgs e) { ((PresetMapping)e.Item.Tag).Include = e.Item.Checked; UpdateSummary(); };
            list.DoubleClick += delegate { if (list.SelectedItems.Count == 1) Toggle(list.SelectedItems[0]); };
            summary = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Text, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 10, 0, 0) };
            var install = new FlatButton("Install selected", true) { Width = 160, Dock = DockStyle.Right, Font = Palette.Small }; install.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            var cancel = new FlatButton("Cancel", false) { Width = 110, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(summary); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(install);
            Controls.Add(list); Controls.Add(footer); Controls.Add(intro);
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            UpdateSummary();
        }

        private void FitLastHeader()
        {
            if (!list.IsHandleCreated || list.Columns.Count == 0) return;
            ColumnHeader last = list.Columns[list.Columns.Count - 1];
            int remaining = list.ClientSize.Width - list.Columns.Cast<ColumnHeader>().Where(c => c != last).Sum(c => c.Width);
            int width = Math.Max(DpiMetrics.Px(list, 90), remaining);
            if (last.Width != width) last.Width = width;
        }

        // Display text of PresetMapping.Kind (the value itself is compared, never this text).
        private static string KindLabel(string kind)
        {
            if (kind == "Costume") return "Outfit";
            if (kind == "Tool") return "Tool mod";
            return kind;
        }

        private static string ModeLabel(PresetMapping m)
        {
            if (m.Entry == null) return "";
            if (m.Kind == "Tool") return "Game folder";
            return m.Mode == InstallModes.Replace ? "Replace original" : "Extra outfit";
        }

        private void Toggle(ListViewItem row)
        {
            var m = (PresetMapping)row.Tag;
            if (m.Ambiguous)
            {
                ThemedMessageBox.Show(this, m.Message, "Preset entry has multiple matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (m.Entry == null || m.Kind == "Tool") return;
            if (m.Mode == InstallModes.Replace && String.IsNullOrEmpty(m.Character)) { summary.Text = "Extra outfit needs a detected character; this mod stays a replacement."; return; }
            m.Mode = m.Mode == InstallModes.Replace ? InstallModes.Slot : InstallModes.Replace;
            row.SubItems[3].Text = ModeLabel(m); UpdateSummary();
        }

        private void UpdateSummary()
        {
            int selected = mappings.Count(m => m.Include && m.Entry != null), missing = mappings.Count(m => m.Entry == null);
            summary.Text = selected + " to install (" + ReplacePlanner.N(mappings.Count(m => m.Include && m.Entry != null && m.Kind == "Tool"), "tool mod") + ", " + ReplacePlanner.N(mappings.Count(m => m.Include && m.Entry != null && m.Kind != "Tool" && m.Mode == InstallModes.Replace), "replacement") + ", " + ReplacePlanner.N(mappings.Count(m => m.Include && m.Entry != null && m.Kind != "Tool" && m.Mode == InstallModes.Slot), "extra outfit") + ")" + (missing > 0 ? " · " + missing + " missing or ambiguous" : "");
        }
    }
}
