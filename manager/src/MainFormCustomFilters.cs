using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.8 (the user): MY FILTERS in the Filters panel (LibraryFilters.cs). A click cycles a chip Off → Only (show only
    // its matches; Only chips are OR-ed) → Hide (leave its matches out) → Off; right-click (or the menu key) offers the
    // other states and Edit; F2 edits. "+ New filter" and Edit open the inline editor (CustomFilterEditor.cs), whose
    // typed keywords are previewed in the list until Save or Cancel. States are saved with the other filters.
    internal sealed partial class MainForm
    {
        private List<CustomFilter> customFilters = new List<CustomFilter>();
        private readonly Dictionary<string, CustomFilterState> customStates = new Dictionary<string, CustomFilterState>(StringComparer.Ordinal);
        private CustomFilter customDraft;      // the editor's keywords, previewed as an Only filter (null = no preview)
        private string customEditingId;        // the filter being edited ("" = a new one; null = editor closed)
        private Timer customDraftTimer;
        private ContextMenuStrip customMenu;
        internal const string NewFilterTag = "+new";

        internal IList<CustomFilter> CustomFilters { get { return customFilters; } }
        internal bool CustomEditorOpen { get { return customEditingId != null; } }

        private void BuildCustomFilters()
        {
            customFilters = LibraryFilters.All();
            customDraftTimer = new Timer { Interval = 200 };
            customDraftTimer.Tick += delegate { customDraftTimer.Stop(); ApplyCustomDraft(); };
            CustomFilterEditor editor = filterPanel.Editor;
            editor.DraftChanged += delegate { if (customEditingId == null) return; customDraftTimer.Stop(); customDraftTimer.Start(); };
            editor.SaveRequested += delegate { SaveCustomFilter(); };
            editor.CancelRequested += delegate { EndCustomFilterEdit(); };
            editor.DeleteRequested += delegate { DeleteCustomFilter(); };
            customMenu = new ContextMenuStrip { BackColor = Palette.Surface, ForeColor = Palette.Text, ShowImageMargin = false, Font = Palette.Normal, Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) };
            customMenu.Opening += delegate(object sender, CancelEventArgs e) { e.Cancel = !FillCustomMenu(customMenu.SourceControl as FlatButton); };
            // The chips are made with the Misc chips (RefreshFacetRows), after the window has scaled itself: sized in
            // pixels here, they would be scaled a second time.
        }

        internal CustomFilterState CustomState(string id)
        {
            CustomFilterState s;
            return id != null && customStates.TryGetValue(id, out s) ? s : CustomFilterState.Off;
        }
        internal void SetCustomState(string id, CustomFilterState state)
        {
            if (CustomState(id) == state) return;
            if (state == CustomFilterState.Off) customStates.Remove(id); else customStates[id] = state;
            UpdateCustomChipLooks();
            filterPanel.RaiseChanged();
        }
        private static CustomFilterState NextState(CustomFilterState s)
        {
            return s == CustomFilterState.Off ? CustomFilterState.Only : s == CustomFilterState.Only ? CustomFilterState.Hide : CustomFilterState.Off;
        }

        // One chip per filter, then "+ New filter". Counts and looks come from ApplyFilters / UpdateCustomChipLooks.
        private void FillCustomChips()
        {
            FlowLayoutPanel chips = filterPanel.CustomChips;
            foreach (string gone in customStates.Keys.Where(id => !customFilters.Any(f => f.Id == id)).ToList()) customStates.Remove(gone);
            chips.SuspendLayout();
            try
            {
                foreach (Control old in chips.Controls.Cast<Control>().Where(c => c != filterPanel.CustomTitle).ToList()) { chips.Controls.Remove(old); old.Dispose(); }
                // The caption: as tall as a chip, first in the row.
                Label title = filterPanel.CustomTitle;
                title.Size = new Size(TextRenderer.MeasureText(title.Text, title.Font).Width + DpiMetrics.Px(filterPanel, 8), DpiMetrics.Px(filterPanel, 26));
                title.Margin = new Padding(0, 0, DpiMetrics.Px(filterPanel, 4), 6);   // the chips' bottom margin
                foreach (CustomFilter filter in customFilters)
                {
                    string id = filter.Id;
                    FlatButton chip = CustomChip(filter.Name + " 9999", id);
                    chip.ContextMenuStrip = customMenu;
                    chip.Click += delegate { SetCustomState(id, NextState(CustomState(id))); };
                    chip.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.F2 && e.Modifiers == Keys.None) { e.Handled = true; BeginCustomFilterEdit(id); } };
                    chips.Controls.Add(chip);
                }
                FlatButton add = CustomChip("+ New filter", NewFilterTag);
                add.Text = "+ New filter"; add.AccessibleName = "New filter";
                add.Click += delegate { BeginCustomFilterEdit(""); };
                chips.Controls.Add(add);
            }
            finally { chips.ResumeLayout(); }
            UpdateCustomChipLooks();
            filterPanel.Arrange();
        }
        private FlatButton CustomChip(string widest, string tag)
        {
            FlowLayoutPanel chips = filterPanel.CustomChips;
            var chip = new FlatButton(widest, false) { Tag = tag, AutoSize = false, Font = Palette.Small, Margin = new Padding(0, 0, 6, 6), Padding = new Padding(6, 0, 6, 0) };
            chip.Height = DpiMetrics.Px(filterPanel, 26);
            chip.Width = TextRenderer.MeasureText("Hide " + widest, chip.Font).Width + DpiMetrics.Px(filterPanel, 18);
            // Arrow keys move between the chips (WinForms); W/A/S/D do the same, like the Misc chips.
            chip.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                Keys arrow = FighterGrid.WasdArrow(e);
                if (arrow == e.KeyCode) return;
                chips.SelectNextControl(chip, arrow == Keys.Right || arrow == Keys.Down, true, false, true);
                e.Handled = e.SuppressKeyPress = true;
            };
            return chip;
        }
        private readonly Dictionary<string, int> customCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // Off: "NSFW 158"; Only: the active (accent) look; Hide: "Hide NSFW 158" in the danger colour.
        private void UpdateCustomChipLooks()
        {
            foreach (FlatButton chip in filterPanel.CustomChips.Controls.OfType<FlatButton>())
            {
                string id = chip.Tag as string;
                CustomFilter filter = customFilters.FirstOrDefault(f => f.Id == id);
                if (filter == null) continue;
                CustomFilterState state = CustomState(id);
                int n; customCounts.TryGetValue(id, out n);
                string text = (state == CustomFilterState.Hide ? "Hide " : "") + filter.Name + " " + n;
                if (chip.Text != text) chip.Text = text;
                bool editing = customEditingId == id;
                chip.Danger = state == CustomFilterState.Hide && !editing;
                if (chip.Active != (state == CustomFilterState.Only || editing)) chip.SetActive(state == CustomFilterState.Only || editing); else chip.Invalidate();
                chip.AccessibleName = "Filter " + filter.Name + ", " + (n == 1 ? "1 mod" : n + " mods") + (editing ? ", editing" : state == CustomFilterState.Only ? ", showing only these" : state == CustomFilterState.Hide ? ", hidden" : "");
            }
        }

        // The menu of a filter chip: the two other states, then Edit.
        private bool FillCustomMenu(FlatButton chip)
        {
            string id = chip == null ? null : chip.Tag as string;
            CustomFilter filter = customFilters.FirstOrDefault(f => f.Id == id);
            if (filter == null) return false;
            customMenu.Items.Clear();
            CustomFilterState state = CustomState(id);
            if (state != CustomFilterState.Only) customMenu.Items.Add("Show only these", null, delegate { SetCustomState(id, CustomFilterState.Only); });
            if (state != CustomFilterState.Hide) customMenu.Items.Add("Hide these", null, delegate { SetCustomState(id, CustomFilterState.Hide); });
            if (state != CustomFilterState.Off) customMenu.Items.Add("Turn off", null, delegate { SetCustomState(id, CustomFilterState.Off); });
            customMenu.Items.Add(new ToolStripSeparator());
            customMenu.Items.Add("Edit filter", null, delegate { BeginCustomFilterEdit(id); });
            RoomyMenu(customMenu);
            return true;
        }

        // ---- Editor ---------------------------------------------------------------------------------------------
        // id: an existing filter, or "" for a new one.
        internal void BeginCustomFilterEdit(string id)
        {
            CustomFilter filter = String.IsNullOrEmpty(id) ? null : customFilters.FirstOrDefault(f => f.Id == id);
            if (!String.IsNullOrEmpty(id) && filter == null) return;
            if (filter == null && customFilters.Count >= LibraryFilters.MaxFilters) { SetPageStatus(libraryPage, "Up to " + LibraryFilters.MaxFilters + " filters. Delete one to add another.", Palette.Red); return; }
            if (!filterPanel.Visible) ShowFilters(true, false);
            customEditingId = filter == null ? "" : filter.Id;
            CustomFilterEditor editor = filterPanel.Editor;
            editor.Begin(filter == null ? "" : filter.Name, filter == null ? "" : LibraryFilters.KeywordText(filter.Keywords), filter != null);
            editor.Visible = true;
            filterPanel.Arrange(); filterPanel.ShowEditor();
            customDraftTimer.Stop(); ApplyCustomDraft();
            UpdateCustomChipLooks();
            if (filter == null) editor.NameField.Focus(); else { editor.KeywordsBox.Focus(); editor.KeywordsBox.Select(editor.KeywordsBox.TextLength, 0); }
        }
        // The typed keywords become the list's preview; the count says how many mods it lists.
        private void ApplyCustomDraft()
        {
            if (customEditingId == null) return;
            List<string> keywords = LibraryFilters.ParseKeywords(filterPanel.Editor.KeywordsBox.Text);
            customDraft = keywords.Count == 0 ? null : new CustomFilter { Id = "draft", Name = "draft", Keywords = keywords };
            libraryToTop = true; ApplyFilters();
            filterPanel.Editor.SetStatus(customDraft == null ? "Type keywords to list their mods." : (captionShown == 1 ? "1 mod listed" : captionShown + " mods listed"), false);
        }
        private void EndCustomFilterEdit()
        {
            if (customEditingId == null) return;
            string id = customEditingId;
            bool hadFocus = filterPanel.Editor.ContainsFocus;
            customEditingId = null; customDraft = null; customDraftTimer.Stop();
            filterPanel.Editor.Visible = false; filterPanel.Arrange();
            UpdateCustomChipLooks();
            libraryToTop = true; ApplyFilters();
            if (hadFocus)
            {
                Control chip = filterPanel.CustomChips.Controls.Cast<Control>().FirstOrDefault(c => (c.Tag as string) == (id.Length == 0 ? NewFilterTag : id)) ?? filterPanel.CustomChips.Controls.Cast<Control>().LastOrDefault();
                if (chip != null && chip.CanFocus) chip.Focus();
            }
        }
        private void SaveCustomFilter()
        {
            if (customEditingId == null) return;
            CustomFilterEditor editor = filterPanel.Editor;
            string name = LibraryFilters.CleanName(editor.NameField.Text);
            List<string> keywords = LibraryFilters.ParseKeywords(editor.KeywordsBox.Text);
            if (name.Length == 0) { editor.SetStatus("Name the filter.", true); editor.NameField.Focus(); return; }
            if (keywords.Count == 0) { editor.SetStatus("Add at least one keyword.", true); editor.KeywordsBox.Focus(); return; }
            string editing = customEditingId;
            if (customFilters.Any(f => f.Id != editing && String.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) { editor.SetStatus("A filter named " + name + " exists.", true); editor.NameField.Focus(); return; }
            var list = customFilters.Select(f => f.Copy()).ToList();
            CustomFilter target = list.FirstOrDefault(f => f.Id == editing);
            if (target == null) { target = new CustomFilter { Id = LibraryFilters.NewId(list) }; list.Add(target); }
            target.Name = name; target.Keywords = keywords;
            try { LibraryFilters.Save(list); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); editor.SetStatus("Could not save: " + ex.Message, true); return; }
            customFilters = LibraryFilters.All();
            string savedId = target.Id;
            customEditingId = savedId;   // EndCustomFilterEdit puts the focus on this chip
            FillCustomChips();
            EndCustomFilterEdit();
            SetPageStatus(libraryPage, "Saved filter " + name + ".");
        }
        private void DeleteCustomFilter()
        {
            if (String.IsNullOrEmpty(customEditingId)) return;
            string id = customEditingId;
            CustomFilter filter = customFilters.FirstOrDefault(f => f.Id == id);
            if (filter == null) { EndCustomFilterEdit(); return; }
            try { LibraryFilters.Save(customFilters.Where(f => f.Id != id).ToList()); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); filterPanel.Editor.SetStatus("Could not delete: " + ex.Message, true); return; }
            customFilters = LibraryFilters.All();
            customStates.Remove(id);
            customEditingId = "";   // focus goes to "+ New filter"
            FillCustomChips();
            EndCustomFilterEdit();
            SetPageStatus(libraryPage, "Deleted filter " + filter.Name + ".");
            QueueViewSave();
        }
        // ProcessCmdKey: inside the editor Esc cancels, Enter in the name and Ctrl+Enter anywhere save, and Tab moves
        // between its fields (before the window's Esc = close the panel and Tab = INSTALL AS).
        private bool CustomFilterKey(Keys keyData)
        {
            if (customEditingId == null || filterPanel == null || !filterPanel.Editor.Visible) return false;
            CustomFilterEditor editor = filterPanel.Editor;
            Control focused = FocusedControl();
            if (focused == null || !editor.Contains(focused)) return false;
            if (keyData == Keys.Escape) { EndCustomFilterEdit(); return true; }
            if (keyData == (Keys.Control | Keys.Enter) || (keyData == Keys.Enter && editor.NameField.Contains(focused))) { SaveCustomFilter(); return true; }
            if (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)) { editor.SelectNextControl(focused, keyData == Keys.Tab, true, true, true); return true; }
            return false;
        }

        // ---- View settings ----------------------------------------------------------------------------------------
        private List<string> CustomIds(CustomFilterState state) { return customFilters.Where(f => CustomState(f.Id) == state).Select(f => f.Id).ToList(); }
        private void RestoreCustomStates(ViewPreferences saved)
        {
            customStates.Clear();
            foreach (string id in saved.FiltersOnly ?? new List<string>()) if (customFilters.Any(f => f.Id == id)) customStates[id] = CustomFilterState.Only;
            foreach (string id in saved.FiltersHidden ?? new List<string>()) if (customFilters.Any(f => f.Id == id)) customStates[id] = CustomFilterState.Hide;
            UpdateCustomChipLooks();
        }
    }
}
