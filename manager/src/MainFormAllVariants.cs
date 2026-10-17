using System;
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

        private Panel BuildAllVariantsRow()
        {
            allVariantsRow = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 6, 0, 2), Visible = false };
            allVariantsButton = Button("Install all variants", false, 200, delegate { InstallAllVariants(); });
            allVariantsButton.Dock = DockStyle.Fill; allVariantsButton.Font = Palette.Small; allVariantsButton.Margin = Padding.Empty;
            allVariantsButton.AccessibleDescription = "Copies the whole archive into costume_mods, like dropping it there";
            allVariantsRow.Controls.Add(allVariantsButton);
            return allVariantsRow;
        }

        private int VariantOptionCount { get { return package == null ? 0 : package.Components.Count(c => !c.IsDummy && c.Assets.Count > 0); } }

        private void UpdateAllVariants()
        {
            if (allVariantsRow == null) return;
            bool show = !IsReplaceMode && package != null && VariantOptionCount > 1 && ArchiveInstalls.IsArchive(package.SourcePath);
            if (allVariantsRow.Visible != show) allVariantsRow.Visible = show;
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
