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
        private CheckBox deepSearch;
        private bool libraryToTop;
        private int quietFilters;   // > 0 while the app itself changes the Filters panel (refresh, restore): no scroll

        internal bool LibraryFileNames { get { return libraryFileNames; } }
        internal bool DeepSearchOn { get { return deepSearch != null && deepSearch.Checked; } }

        private Panel BuildSearchOptions()
        {
            var row = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Rhythm.Related), Padding = Padding.Empty, Name = "searchOptions" };
            deepSearch = new CheckBox { Text = "Deep search", Dock = DockStyle.Left, AutoSize = true, ForeColor = Palette.Muted, Font = Palette.Small, Name = "deepSearch", AccessibleName = "Deep search: also match option folders and option names", Padding = Padding.Empty, Margin = Padding.Empty };
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
        internal string LibraryRowName(LibraryEntry entry) { return entry == null ? "" : entry.ShownName(libraryFileNames); }

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
}
