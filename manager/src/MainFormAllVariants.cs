using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.5.4: Install all variants (Extra outfit mode, an archive with several options): the archive is copied unchanged
    // into costume_mods\<character>\ like a manual drop, and SlotsLoader builds every variant (ArchiveInstalls.cs).
    // It is linked to its library mod: ✓ Full, the footer's Uninstall / Ctrl and Undo remove it.
    internal sealed partial class MainForm
    {
        private Panel allVariantsRow;
        private FlatButton allVariantsButton;
        internal int AllVariantsInstalls;   // tests: copies that finished

        // 1.0.6: the row holds Install all variants and Add to outfit… (MainFormAddParts.cs) side by side; one alone fills it.
        private Panel BuildAllVariantsRow()
        {
            allVariantsRow = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 6, 0, 2), Visible = false };
            allVariantsButton = Button("Install all variants", false, 200, delegate { InstallAllVariants(); });
            allVariantsButton.Font = Palette.Small; allVariantsButton.Margin = Padding.Empty;
            allVariantsButton.AccessibleDescription = "Copies the whole archive into costume_mods, like dropping it there";
            allVariantsRow.Controls.Add(allVariantsButton); allVariantsRow.Controls.Add(BuildAddPartsButton());
            allVariantsRow.Resize += delegate { LayoutVariantRow(); }; dpiLayouts.Add(LayoutVariantRow);
            return allVariantsRow;
        }
        // What each button wants (Control.Visible reads false while the row itself is hidden).
        private bool showAllVariants, showAddParts;
        private void LayoutVariantRow()
        {
            if (allVariantsRow == null) return;
            if (allVariantsButton.Visible != showAllVariants) allVariantsButton.Visible = showAllVariants;
            if (addPartsButton != null && addPartsButton.Visible != showAddParts) addPartsButton.Visible = showAddParts;
            var shown = new List<FlatButton>(); if (showAllVariants) shown.Add(allVariantsButton); if (showAddParts && addPartsButton != null) shown.Add(addPartsButton);
            bool any = shown.Count > 0;
            if (allVariantsRow.Visible != any) allVariantsRow.Visible = any;
            Rectangle area = allVariantsRow.DisplayRectangle; int gap = Px(8);
            for (int i = 0; i < shown.Count; i++)
            {
                int left = area.Left + (area.Width + gap) * i / shown.Count, right = area.Left + (area.Width + gap) * (i + 1) / shown.Count - gap;
                shown[i].SetBounds(left, area.Top, Math.Max(1, right - left), area.Height);
            }
        }

        private int VariantOptionCount { get { return package == null ? 0 : package.Components.Count(c => !c.IsDummy && c.Assets.Count > 0); } }

        private void UpdateAllVariants()
        {
            if (allVariantsRow == null) return;
            bool show = !IsReplaceMode && package != null && VariantOptionCount > 1 && ArchiveInstalls.IsArchive(package.SourcePath);
            if (showAllVariants != show) { showAllVariants = show; LayoutVariantRow(); }
            if (!show) return;
            bool installedAll = LinkedOutfits(package.SourcePath).Any(o => o.WholeArchive);
            string text = installedAll ? "Reinstall all variants" : "Install all variants";
            if (allVariantsButton.Text != text) allVariantsButton.Text = text;
            allVariantsButton.Enabled = !busy && targetCharacter.SelectedIndex >= 0;
        }

        private void InstallAllVariants()
        {
            if (busy || package == null || IsReplaceMode || targetCharacter.SelectedIndex < 0) return;
            WithFreshPackage(InstallAllVariantsFresh);
        }
        private void InstallAllVariantsFresh()
        {
            string source = package.SourcePath, name = package.Name, character = Convert.ToString(targetCharacter.SelectedItem), root = config.CostumeRoot;
            Work("Installing every variant of " + name + "…", delegate { return ArchiveInstalls.Install(source, root, character, name); }, delegate(InstallResult result)
            {
                AllVariantsInstalls++;
                RefreshInstalled("✓ " + result.Message);
            });
        }
    }
}
