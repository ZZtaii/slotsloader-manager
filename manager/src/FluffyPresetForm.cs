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
            foreach (PresetMapping m in mappings)
            {
                OperationContext.ThrowIfCancellationRequested();
                OperationContext.Report("Installing " + m.DisplayName, index++, mappings.Count);
                try
                {
                    string name = CleanName(m.DisplayName);
                    if (m.Mode == InstallModes.Replace || m.Kind == "Tool")
                    {
                        CompositionPlan composition = Composer.Plan(m.Package, m.OptionIds, true);
                        if (!composition.CanInstall) throw new IOException(String.Join(" ", composition.Errors));
                        ReplacePlan plan;
                        ReplacePlanner.PrepareAndInstall(composition, config.SevenZipPath, engine, name, false, null, out plan);
                    }
                    else
                    {
                        if (String.IsNullOrEmpty(m.Character)) throw new IOException("no character detected for an extra outfit");
                        CompositionPlan composition = Composer.Plan(m.Package, m.OptionIds);
                        if (!composition.CanInstall) throw new IOException(String.Join(" ", composition.Errors));
                        var installed = Installer.ListInstalled(config.CostumeRoot);
                        string unique = name; int n = 2;
                        while (installed.Any(o => String.Equals(o.Character, m.Character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, unique, StringComparison.OrdinalIgnoreCase))) unique = name + " " + n++;
                        Installer.Install(composition, config.CostumeRoot, m.Character, unique, config.SevenZipPath, false);
                    }
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; failed.Add(m.DisplayName + ": " + ex.Message); }
            }
            return "✓ Installed " + ok + " of " + mappings.Count + " preset mods" + (failed.Count > 0 ? " · failed: " + String.Join(" | ", failed.Take(4)) + (failed.Count > 4 ? " …" : "") : "") + ". Restart SF6 to see them.";
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
            var intro = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Text = "Entries are matched to library mods by folder, archive or mod name; untick a row to skip it. Outfits install as Replace original (double-click a row for Extra outfit); tool mods go into the game folder." };
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, BackColor = Palette.Background, ForeColor = Palette.Text, BorderStyle = BorderStyle.None, Font = Palette.Small, HideSelection = false };
            list.Columns.Add("Preset entry", 230); list.Columns.Add("Mod and options", 360); list.Columns.Add("Kind", 80); list.Columns.Add("Install as", 140); list.Columns.Add("Character", 90);
            NativeDpiFont.Attach(list);
            foreach (PresetMapping m in mappings)
            {
                var row = new ListViewItem(m.DisplayName) { Tag = m, Checked = m.Include && m.Entry != null };
                row.SubItems.Add(m.Message); row.SubItems.Add(m.Entry == null ? "Not found" : KindLabel(m.Kind)); row.SubItems.Add(ModeLabel(m)); row.SubItems.Add(m.Character);
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
            if (m.Entry == null || m.Kind == "Tool") return;
            if (m.Mode == InstallModes.Replace && String.IsNullOrEmpty(m.Character)) { summary.Text = "Extra outfit needs a detected character; this mod stays a replacement."; return; }
            m.Mode = m.Mode == InstallModes.Replace ? InstallModes.Slot : InstallModes.Replace;
            row.SubItems[3].Text = ModeLabel(m); UpdateSummary();
        }

        private void UpdateSummary()
        {
            int selected = mappings.Count(m => m.Include && m.Entry != null), missing = mappings.Count(m => m.Entry == null);
            summary.Text = selected + " to install (" + ReplacePlanner.N(mappings.Count(m => m.Include && m.Entry != null && m.Kind == "Tool"), "tool mod") + ", " + ReplacePlanner.N(mappings.Count(m => m.Include && m.Entry != null && m.Kind != "Tool" && m.Mode == InstallModes.Replace), "replacement") + ", " + ReplacePlanner.N(mappings.Count(m => m.Include && m.Entry != null && m.Kind != "Tool" && m.Mode == InstallModes.Slot), "extra outfit") + ")" + (missing > 0 ? " · " + missing + " not in your libraries" : "");
        }
    }
}
