using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Installs mapped Fluffy preset entries (one per preset section) in preset order. Replacements (tool mods always)
    // go in as one ordered block; the rows of an archive set to Extra outfit become one outfit. Failures are reported.
    internal static class PresetInstaller
    {
        internal static string Install(AppConfig config, ReplaceEngine engine, List<PresetMapping> mappings)
        {
            int ok = 0, done = 0; var failed = new List<string>(); int index = 0;
            var selected = mappings.Where(m => m.Include && m.Entry != null).ToList();
            var replacements = selected.Where(m => m.Mode == InstallModes.Replace || m.Kind == "Tool").ToList();
            // All or none, so a partial block never changes which mod wins a shared file. The extra outfits still install.
            if (replacements.Count > 0)
                try { engine.ImportLayers(PresetLayer.Ordered(FluffyPresets.Group(Copies(replacements))), config.SevenZipPath); ok += replacements.Count; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (ex is OutOfMemoryException || ex is ReplaceEngine.SimulatedCrashException) throw; failed.Add("replacements, none installed: " + ex.Message); }
            var outfits = FluffyPresets.Group(Copies(selected.Except(replacements).ToList()));
            foreach (PresetMapping m in outfits)
            {
                OperationContext.ThrowIfCancellationRequested();
                OperationContext.Report("Installing " + m.DisplayName, index++, outfits.Count);
                done = m.Sections.Count;
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
                    ok += done;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; failed.Add(m.DisplayName + ": " + ex.Message); }
            }
            return "✓ Installed " + ok + " of " + selected.Count + " preset mods" + (failed.Count > 0 ? " · failed: " + String.Join(" | ", failed.Take(4)) + (failed.Count > 4 ? " …" : "") : "") + ". Restart SF6 to see them.";
        }

        // FluffyPresets.Group merges into its first mapping: work on copies, so the review's rows stay as they are.
        private static List<PresetMapping> Copies(List<PresetMapping> mappings)
        {
            return mappings.Select(m => new PresetMapping { Sections = m.Sections.ToList(), Entry = m.Entry, Package = m.Package, OptionIds = m.OptionIds.ToList(),
                Kind = m.Kind, Mode = m.Mode, Character = m.Character, Message = m.Message, Stamp = m.Stamp, Include = m.Include }).ToList();
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
            var intro = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Text = "Each row is a preset entry, numbered in preset order; later entries win shared files. Selected replacements are enabled together at the top of Installed. Double-click an outfit row to install its archive as one extra outfit, or an ambiguous row to choose its archive." };
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, BackColor = Palette.Background, ForeColor = Palette.Text, BorderStyle = BorderStyle.None, Font = Palette.Small, HideSelection = false };
            list.Columns.Add("Preset entry", 230); list.Columns.Add("Mod and options", 360); list.Columns.Add("Kind", 80); list.Columns.Add("Install as", 140); list.Columns.Add("Character", 90);
            list.OwnerDraw = true;
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (var background = new SolidBrush(Palette.Raised)) e.Graphics.FillRectangle(background, e.Bounds);
                // A divider between columns, as the native header draws one.
                if (e.ColumnIndex < list.Columns.Count - 1)
                    using (var divider = new Pen(Palette.Border)) e.Graphics.DrawLine(divider, e.Bounds.Right - 1, e.Bounds.Top + DpiMetrics.Px(list, 4), e.Bounds.Right - 1, e.Bounds.Bottom - DpiMetrics.Px(list, 5));
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
                var row = new ListViewItem(String.Join(", ", m.Sections.Select(s => (s.Order + 1).ToString())) + " · " + m.DisplayName) { Tag = m };
                row.SubItems.Add(""); row.SubItems.Add(""); row.SubItems.Add(""); row.SubItems.Add("");
                ShowRow(row);
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

        private void ShowRow(ListViewItem row)
        {
            var m = (PresetMapping)row.Tag;
            row.SubItems[1].Text = m.Message;   // no hover text in the app (UiAudit): the column holds it row.SubItems[2].Text = m.Entry == null ? (m.Ambiguous ? "Ambiguous" : "Not found") : KindLabel(m.Kind);
            row.SubItems[3].Text = ModeLabel(m); row.SubItems[4].Text = m.Character;
            row.ForeColor = m.Entry == null && !m.Ambiguous ? Palette.Muted : m.Ambiguous ? Palette.Accent : Palette.Text;
            row.Checked = m.Include && m.Entry != null;
        }

        private void Toggle(ListViewItem row)
        {
            var m = (PresetMapping)row.Tag;
            if (m.Ambiguous && m.Choices != null) { ChooseMatch(row); return; }
            if (m.Entry == null || m.Kind == "Tool") return;
            if (m.Mode == InstallModes.Replace && String.IsNullOrEmpty(m.Character)) { summary.Text = "Extra outfit needs a detected character; this mod stays a replacement."; return; }
            // One extra outfit per archive: every row of the archive changes together.
            string mode = m.Mode == InstallModes.Replace ? InstallModes.Slot : InstallModes.Replace;
            foreach (ListViewItem other in list.Items)
            {
                var o = (PresetMapping)other.Tag;
                if (o.Entry == null || o.Kind == "Tool" || !String.Equals(o.Entry.SourcePath, m.Entry.SourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                o.Mode = mode; other.SubItems[3].Text = ModeLabel(o);
            }
            UpdateSummary();
        }

        // An entry that several archives match: the user picks the archive from a menu at the row.
        private void ChooseMatch(ListViewItem row)
        {
            var m = (PresetMapping)row.Tag;
            var menu = new ContextMenuStrip { BackColor = Palette.Surface, ForeColor = Palette.Text, ShowImageMargin = false, Font = Palette.Small, Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) };
            foreach (PresetMapping choice in m.Choices)
            {
                PresetMapping picked = choice;
                menu.Items.Add(FluffyPresets.ChoiceText(choice), null, delegate
                {
                    FluffyPresets.Resolve(m, picked);
                    // An archive already set to Extra outfit keeps that mode for this row too.
                    var sibling = list.Items.Cast<ListViewItem>().Select(i => (PresetMapping)i.Tag).FirstOrDefault(o => o != m && o.Entry != null && o.Kind != "Tool" && String.Equals(o.Entry.SourcePath, m.Entry.SourcePath, StringComparison.OrdinalIgnoreCase));
                    if (sibling != null && m.Kind != "Tool") m.Mode = sibling.Mode;
                    ShowRow(row); UpdateSummary();
                });
            }
            menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
            menu.Show(list, new Point(row.Bounds.Left + DpiMetrics.Px(list, 24), row.Bounds.Bottom));
        }

        private void UpdateSummary()
        {
            var chosen = mappings.Where(m => m.Include && m.Entry != null).ToList();
            int missing = mappings.Count(m => m.Entry == null && !m.Ambiguous), ambiguous = mappings.Count(m => m.Ambiguous);
            int outfits = chosen.Where(m => m.Kind != "Tool" && m.Mode == InstallModes.Slot).Select(m => m.Entry.SourcePath.ToUpperInvariant()).Distinct().Count();
            summary.Text = chosen.Count + " to install (" + ReplacePlanner.N(chosen.Count(m => m.Kind == "Tool"), "tool mod") + ", " + ReplacePlanner.N(chosen.Count(m => m.Kind != "Tool" && m.Mode == InstallModes.Replace), "replacement") + ", " + ReplacePlanner.N(outfits, "extra outfit") + ")" +
                (missing > 0 ? " · " + missing + " not in your libraries" : "") + (ambiguous > 0 ? " · " + ambiguous + " to choose (double-click)" : "");
        }
    }
}
