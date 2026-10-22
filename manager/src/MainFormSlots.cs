using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.5.0: several variants of one mod as extra slots. Each install is one slot; the outfit name suggests the next
    // slot from the ticked options ("<mod> · Leon Hair"), or the installed slot whose options match (Install updates it).
    // The ticks are never reset by an install or a refresh, so the next variant starts from the current selection.
    // On the Installed page the slots of one source group under one row; Reset options puts the mod's defaults back.
    internal sealed class OutfitGroup
    {
        internal string Key, Source, Character, Name;
        internal List<InstalledOutfit> Slots = new List<InstalledOutfit>();
        internal string Label { get { return Name + " · " + ReplacePlanner.N(Slots.Count, "slot"); } }
    }

    internal sealed partial class MainForm
    {
        private bool outfitNameTyped, outfitNameLocked, settingOutfitName;
        private ToolTip slotHintTip;
        private FlatButton resetOptionsButton;
        internal string SlotHintText = "";
        internal const int SlotNamesShown = 2, SlotNameLimit = 120;
        private readonly HashSet<string> expandedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<InstalledOutfit, OutfitGroup> groupedSlots = new Dictionary<InstalledOutfit, OutfitGroup>();

        // ---- Outfit name
        private void SetOutfitName(string name) { settingOutfitName = true; try { outfitName.Text = name ?? ""; } finally { settingOutfitName = false; } }
        // A name the user typed is kept until the mod is opened again (clearing the field gives the suggestion back).
        private void OutfitNameEdited() { if (!settingOutfitName) outfitNameTyped = outfitName.Text.Trim().Length > 0; }

        // Extra outfits of the open mod for this character, installed by the Manager (enabled or disabled).
        internal List<InstalledOutfit> ModSlots(string character)
        {
            if (package == null || installed == null || String.IsNullOrEmpty(character)) return new List<InstalledOutfit>();
            return installed.Where(o => o.Managed && String.Equals(o.Character, character, StringComparison.OrdinalIgnoreCase) && ReplacePlanner.SameSource(o.SourcePath, package.SourcePath))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private string TargetCharacter() { return Convert.ToString(targetCharacter.SelectedItem); }

        // The ticked options that make this variant: not main files, not the implicit root, not a parent of another tick.
        internal List<string> VariantOptionNames()
        {
            if (package == null) return new List<string>();
            var ticked = new HashSet<string>(selectedOrder, StringComparer.OrdinalIgnoreCase);
            var chosen = package.Components.Where(c => ticked.Contains(c.Id) && !c.IsDummy && c.HasFiles(false) && c.Id.Length > 0 && !Composer.IsBase(c)).ToList();
            return chosen.Where(c => !chosen.Any(d => d != c && IsAncestor(c, d))).Select(c => (c.Name ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        private bool IsAncestor(ModComponent ancestor, ModComponent child)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (ModComponent c = child; c != null && c.ParentId != null && seen.Add(c.Id); c = package.Find(c.ParentId))
                if (String.Equals(c.ParentId, ancestor.Id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Nothing of this mod installed: the plain mod name (as before 2.5.0). The same options as an installed slot:
        // that slot's name (Install updates it). Otherwise "<mod> · <options joined with ' + '>", " 2", " 3"… when taken.
        internal string SuggestedOutfitName()
        {
            if (package == null) return "";
            string plain = CleanName(package.Name), character = TargetCharacter();
            List<InstalledOutfit> slots = ModSlots(character);
            if (slots.Count == 0) return plain;
            var selection = new HashSet<string>(selectedOrder, StringComparer.OrdinalIgnoreCase);
            InstalledOutfit same = slots.FirstOrDefault(o => o.Enabled && o.SelectedIds != null && Composer.MatchesSavedSelection(package, selection, o.SelectedIds));
            if (same != null) return same.Name;
            List<string> options = VariantOptionNames();
            string name = CleanName(options.Count == 0 ? plain : plain + " · " + String.Join(" + ", options));
            if (name.Length > SlotNameLimit) name = name.Substring(0, SlotNameLimit).Trim().TrimEnd('.');
            string candidate = name;
            for (int n = 2; installed.Any(o => String.Equals(o.Character, character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++) candidate = name + " " + n;
            return candidate;
        }
        // Called when the mod opens, the options or the character change, the mode switches or the installed list refreshes.
        private void ApplySuggestedName()
        {
            if (package != null && !IsReplaceMode && !outfitNameTyped && !outfitNameLocked && outfitName != null)
            {
                string name = SuggestedOutfitName();
                if (!String.Equals(outfitName.Text, name, StringComparison.Ordinal)) SetOutfitName(name);
            }
            UpdateSlotHint();
        }

        // ---- Hint (the user's explicit 2.5.0 request: the one tooltip in the app, on the outfit name and Install)
        internal string SlotHint()
        {
            if (package == null || IsReplaceMode || outfitName == null || installed == null) return "";
            string character = TargetCharacter(), name = outfitName.Text.Trim();
            if (name.Length == 0 || String.IsNullOrEmpty(character)) return "";
            if (installed.Any(o => o.Managed && o.Enabled && String.Equals(o.Character, character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))) return "Updates this slot.";
            List<InstalledOutfit> slots = ModSlots(character);
            if (slots.Count == 0 || installed.Any(o => String.Equals(o.Character, character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))) return "";
            return "Adds a new slot. Keeps: " + String.Join(", ", slots.Take(SlotNamesShown).Select(o => o.Name)) + (slots.Count > SlotNamesShown ? " +" + (slots.Count - SlotNamesShown) + " more" : "") + ".";
        }
        private ToolTip SlotHintTip()
        {
            if (slotHintTip == null) { slotHintTip = new ToolTip { InitialDelay = 400, ShowAlways = true }; Disposed += delegate { slotHintTip.Dispose(); }; }
            return slotHintTip;
        }
        private void UpdateSlotHint()
        {
            string text = SlotHint();
            if (text == SlotHintText) return;
            SlotHintText = text;
            if (outfitName == null || installButton == null) return;
            ToolTip tip = SlotHintTip();
            string shown = text.Length == 0 ? null : text;
            tip.SetToolTip(outfitName, shown); tip.SetToolTip(outfitName.Editor, shown); tip.SetToolTip(installButton, shown);
        }

        // ---- Reset options (both modes): the mod's defaults, as when it was opened
        private FlatButton BuildResetOptions()
        {
            resetOptionsButton = Button("Reset options", false, 112, delegate { ResetOptions(); });
            resetOptionsButton.Navigation = true; resetOptionsButton.Font = Palette.Small; resetOptionsButton.Dock = DockStyle.Right; resetOptionsButton.Margin = Padding.Empty; resetOptionsButton.Visible = false;
            resetOptionsButton.AccessibleName = "Reset options to the mod's defaults";
            return resetOptionsButton;
        }
        internal List<string> DefaultOptionIds() { return package == null ? new List<string>() : Composer.DefaultSelection(package, IsReplaceMode || IsToolPackage(package)); }
        internal bool OptionsDifferFromDefaults()
        {
            return package != null && !new HashSet<string>(selectedOrder, StringComparer.OrdinalIgnoreCase).SetEquals(DefaultOptionIds());
        }
        private void UpdateResetOptions() { if (resetOptionsButton != null) { resetOptionsButton.Visible = OptionsDifferFromDefaults(); resetOptionsButton.Enabled = !busy; } }
        internal void ResetOptions()
        {
            if (busy || package == null) return;
            FillComponents(DefaultOptionIds()); ResetPlan();
            foreach (string id in selectedOrder) { OptionNode node; if (componentNodes.TryGetValue(id, out node)) foreach (OptionNode ancestor in Ancestors(node)) ancestor.Expand(); }
            componentTree.Invalidate();
        }

        // ---- Installed page: slots of one source grouped under one row
        private static string GroupKey(InstalledOutfit o) { return (o.Character ?? "") + "|" + (o.SourcePath ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant(); }
        private string GroupName(string source)
        {
            LibraryEntry entry = library == null ? null : library.FirstOrDefault(e => ReplacePlanner.SameSource(e.SourcePath, source));
            if (entry != null && !String.IsNullOrEmpty(entry.Name)) return entry.Name;
            string trimmed = (source ?? "").TrimEnd('\\', '/');
            return File.Exists(trimmed) ? Path.GetFileNameWithoutExtension(trimmed) : Path.GetFileName(trimmed);
        }
        // Managed slots from one source and character, two or more after the page's filters.
        private Dictionary<string, OutfitGroup> BuildGroups(IEnumerable<InstalledOutfit> shown)
        {
            return shown.Where(o => o.Managed && !String.IsNullOrEmpty(o.SourcePath)).GroupBy(GroupKey).Where(g => g.Count() > 1)
                .ToDictionary(g => g.Key, g => new OutfitGroup { Key = g.Key, Source = g.First().SourcePath, Character = g.First().Character, Name = GroupName(g.First().SourcePath), Slots = g.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList() });
        }
        // Two or more slot rows of one expanded group (and nothing else): "Uninstall 2 slots".
        private OutfitGroup SlotsOfOneGroup()
        {
            if (installedList == null || installedList.SelectedItems.Count < 2) return null;
            OutfitGroup found = null;
            foreach (ListViewItem item in installedList.SelectedItems)
            {
                InstalledOutfit slot = item.Tag as InstalledOutfit; OutfitGroup group;
                if (slot == null || !groupedSlots.TryGetValue(slot, out group) || (found != null && found != group)) return null;
                found = group;
            }
            return found;
        }
        private OutfitGroup GetGroupSelection() { return installedList != null && installedList.SelectedItems.Count == 1 ? installedList.SelectedItems[0].Tag as OutfitGroup : null; }
        internal void ToggleGroup(OutfitGroup group, bool? expand = null)
        {
            if (group == null) return;
            bool open = expand ?? !expandedGroups.Contains(group.Key);
            if (open == expandedGroups.Contains(group.Key)) return;
            if (open) expandedGroups.Add(group.Key); else expandedGroups.Remove(group.Key);
            FillInstalled();
        }
        private void WireGroupRows()
        {
            installedList.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || busy) return;
                ListViewItem row = installedList.GetItemAt(e.X, e.Y); OutfitGroup group = row == null ? null : row.Tag as OutfitGroup;
                if (group != null && e.X < row.Bounds.Left + Px(34)) BeginInvoke((Action)delegate { ToggleGroup(group); });   // the chevron
            };
            installedList.DoubleClick += delegate { OutfitGroup group = GetGroupSelection(); if (group != null && !busy) ToggleGroup(group); };
            installedList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                Keys k = e.KeyCode;
                // 2.5.2: W/S move like Up/Down, A/D like Left/Right; none of the four starts the type-ahead jump.
                if (e.Modifiers == Keys.None && (k == Keys.W || k == Keys.A || k == Keys.S || k == Keys.D))
                {
                    e.Handled = e.SuppressKeyPress = true;
                    if (k == Keys.W || k == Keys.S) { MoveInstalledSelection(k == Keys.W ? -1 : 1); return; }
                    k = k == Keys.D ? Keys.Right : Keys.Left;
                }
                OutfitGroup group = GetGroupSelection();
                if (group == null || busy || (k != Keys.Right && k != Keys.Left)) return;
                ToggleGroup(group, k == Keys.Right); e.Handled = true;
            };
        }
        // Options of a slot for its row: the last folder of each non-main option id without its number ("02 Variants/03 Leon Hair" -> "Leon Hair").
        internal static string SlotOptions(InstalledOutfit o)
        {
            var names = (o.SelectedIds ?? new List<string>()).Where(id => !String.IsNullOrEmpty(id)).Select(id => id.Replace('\\', '/').TrimEnd('/')).Select(id => System.Text.RegularExpressions.Regex.Replace(id.Substring(id.LastIndexOf('/') + 1), @"^\s*\d+\s*[\).:\-]?\s*", "").Trim())
                .Where(n => n.Length > 0 && !Composer.IsBase(new ModComponent { Name = n })).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names.Count == 0 ? "Main files" : String.Join(" + ", names);
        }
        private void DrawGroupRow(DrawListViewSubItemEventArgs e, OutfitGroup group)
        {
            bool selected = e.Item.Selected; Rectangle r = e.Bounds;
            using (Brush bg = new SolidBrush(selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(bg, r);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Glyphs.Chevron(e.Graphics, new PointF(r.Left + Px(20), r.Top + Px(22)), Px(4), expandedGroups.Contains(group.Key) ? ChevronDirection.Down : ChevronDirection.Right, Palette.Muted, 1.6F * DeviceDpi / 96F);
            TextRenderer.DrawText(e.Graphics, group.Label, Palette.Strong, new Rectangle(r.Left + Px(34), r.Top + Px(10), r.Width - Px(45), Px(24)), Palette.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            int enabled = group.Slots.Count(o => o.Enabled);
            string sub = "Extra   ·   " + group.Character + "   ·   " + (enabled == group.Slots.Count ? "Enabled" : enabled + " of " + group.Slots.Count + " enabled") + "   ·   Managed";
            Rectangle line = new Rectangle(r.Left + Px(34), r.Top + Px(36), r.Width - Px(45), Px(20));
            TextRenderer.DrawText(e.Graphics, sub, Palette.Small, line, enabled > 0 ? Palette.Green : Palette.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            DrawUsageAfter(e.Graphics, sub, InstalledAgo(group), Palette.Small, line);   // 1.0.1: the newest slot's install time
        }
        // 2.5.9: "by Author" from the source's library entry (its modinfo.ini), when the library lists the source.
        internal void AddAuthorChip(List<ChipStrip.Chip> chips, string source)
        {
            if (String.IsNullOrEmpty(source) || library == null) return;
            LibraryEntry entry = library.FirstOrDefault(e => ReplacePlanner.SameSource(e.SourcePath, source));
            string text = entry == null ? "" : Library.AuthorText(entry.Authors);
            if (text.Length > 0) chips.Add(ChipStrip.Neutral("by " + text));
        }
        // The group in the inspector: every slot with its options and SlotsLoader slot; Uninstall removes them all.
        private void ShowGroupDetails(OutfitGroup group)
        {
            installedTitle.Text = group.Label;
            var chips = new List<ChipStrip.Chip> { ChipStrip.Neutral(group.Character), ChipStrip.Neutral(ReplacePlanner.N(group.Slots.Count, "slot")) };
            AddAuthorChip(chips, group.Source);
            int disabled = group.Slots.Count(o => !o.Enabled); if (disabled > 0) chips.Add(ChipStrip.Quiet(disabled + " disabled"));
            chips.Add(ChipStrip.Neutral("Managed"));
            installedChips.SetChips(chips);
            installedDetail.Text = group.Source; installedPathRow.Visible = true;
            installedSectionCaption.Text = "SLOTS";
            installedHelp.Text = String.Join(Environment.NewLine + Environment.NewLine, group.Slots.Select(o => o.Name + Environment.NewLine + SlotOptions(o) + (String.IsNullOrEmpty(o.LoaderOutfitName) ? "" : "  ·  " + o.LoaderOutfitName) + (o.Enabled ? "" : "  ·  disabled")))
                + Environment.NewLine + Environment.NewLine + "Uninstall deletes every slot; reinstall from the Mod library. Expand the row to pick one.";
            installedHelp.Select(0, 0);
        }
    }
}
