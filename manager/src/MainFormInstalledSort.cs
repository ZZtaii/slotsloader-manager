using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.5.5: the Installed page's sort chips, like the Mod library's: Character · Installed · Played, each ↑/↓ (a press
    // on the active chip flips it). Character ↑ is the order before 2.5.5 (fighter, then name; replacements and tool
    // mods after, in priority order). Installed and Played sort every row together, newest first on the first press;
    // rows without a date go last either way. A group of slots sorts by its newest slot.
    // 1.0.1 (the user): only the Installed chip is shown; Character and Played are gone and a saved one becomes Installed ↓.
    internal enum InstalledSortKey { Character, Installed, Played }

    internal sealed partial class MainForm
    {
        private InstalledSortKey installedSort = InstalledSortKey.Installed;
        private bool installedSortAscending = false;
        private readonly Dictionary<InstalledSortKey, FlatButton> installedSortChips = new Dictionary<InstalledSortKey, FlatButton>();
        private static readonly InstalledSortKey[] InstalledSortOrder = { InstalledSortKey.Installed };

        internal InstalledSortKey InstalledSort { get { return installedSort; } }
        internal bool InstalledSortAscending { get { return installedSortAscending; } }

        internal static string InstalledSortLabel(InstalledSortKey key) { return key == InstalledSortKey.Installed ? "Installed" : key == InstalledSortKey.Played ? "Played" : "Character"; }
        private static string InstalledSortCode(InstalledSortKey key) { return key == InstalledSortKey.Installed ? "installed" : key == InstalledSortKey.Played ? "played" : "character"; }
        private static string InstalledSortDirection(InstalledSortKey key, bool ascending)
        {
            if (key == InstalledSortKey.Character) return ascending ? "A to Z" : "Z to A";
            return ascending ? "oldest first" : "newest first";
        }

        private FlowLayoutPanel BuildInstalledSortStrip()
        {
            var strip = new FlowLayoutPanel { Dock = DockStyle.Right, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Padding.Empty, Margin = Padding.Empty, Name = "installedSortStrip", AccessibleName = "Sort installed", AccessibleRole = AccessibleRole.ToolBar };
            foreach (InstalledSortKey key in InstalledSortOrder)
            {
                InstalledSortKey k = key;
                FlatButton chip = Button(InstalledSortLabel(k), false, 96, delegate { InstalledSortClicked(k); });
                chip.Height = 28; chip.Font = Palette.Small; chip.Margin = new Padding(5, 0, 0, 0); chip.Name = "installedSort" + k;
                chip.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    Keys arrow = FighterGrid.WasdArrow(e);
                    if (arrow == e.KeyCode) return;
                    strip.SelectNextControl(chip, arrow == Keys.Right || arrow == Keys.Down, true, false, true);
                    e.Handled = e.SuppressKeyPress = true;
                };
                installedSortChips[k] = chip; strip.Controls.Add(chip);
            }
            UpdateInstalledSortChips();
            return strip;
        }

        private void InstalledSortClicked(InstalledSortKey key)
        {
            if (key == installedSort) installedSortAscending = !installedSortAscending;
            else { installedSort = key; installedSortAscending = key == InstalledSortKey.Character; }   // A to Z, or newest first
            UpdateInstalledSortChips(); FillInstalled(); QueueViewSave();
        }
        internal void SetInstalledSort(InstalledSortKey key, bool ascending)
        {
            installedSort = key; installedSortAscending = ascending;
            UpdateInstalledSortChips(); FillInstalled();
        }
        private void UpdateInstalledSortChips()
        {
            foreach (var kv in installedSortChips)
            {
                bool active = kv.Key == installedSort; string label = InstalledSortLabel(kv.Key);
                string text = active ? label + (installedSortAscending ? " ↑" : " ↓") : label;
                if (kv.Value.Text != text) kv.Value.Text = text;
                if (kv.Value.Active != active) kv.Value.SetActive(active);
                kv.Value.Width = Px(kv.Key == InstalledSortKey.Character ? 100 : 90);
                kv.Value.AccessibleName = "Sort by " + label + ", " + InstalledSortDirection(kv.Key, active ? installedSortAscending : kv.Key == InstalledSortKey.Character);
                kv.Value.AccessibleDescription = active ? "Selected. Press again for " + InstalledSortDirection(kv.Key, !installedSortAscending) + "." : "";
            }
        }
        private void RestoreInstalledSort(ViewPreferences saved)
        {
            string code = saved == null ? "" : saved.InstalledSort ?? "";
            installedSort = InstalledSortKey.Installed;
            installedSortAscending = code == "installed" && String.Equals(saved.InstalledSortOrder, "asc", StringComparison.OrdinalIgnoreCase);
            UpdateInstalledSortChips();
        }

        // FillInstalled's rows in the chosen order: InstalledOutfit, OutfitGroup (its slots follow when expanded) and
        // GameModRow. `entries` arrive in Character ↑ order (fighter, name), `games` in priority order.
        private List<object> SortInstalledRows(List<object> entries, List<GameModRow> games)
        {
            if (installedSort == InstalledSortKey.Character)
            {
                if (!installedSortAscending) entries.Reverse();
                entries.AddRange(games.Cast<object>());   // replacements and tool mods keep their priority order
                return entries;
            }
            var all = entries.Concat(games.Cast<object>()).ToList();
            var keyed = all.Select((row, index) => new { Row = row, Index = index, Ticks = InstalledSortTicks(row) }).ToList();
            var dated = keyed.Where(x => x.Ticks > 0);
            dated = installedSortAscending ? dated.OrderBy(x => x.Ticks).ThenBy(x => x.Index) : dated.OrderByDescending(x => x.Ticks).ThenBy(x => x.Index);
            return dated.Concat(keyed.Where(x => x.Ticks <= 0).OrderBy(x => x.Index)).Select(x => x.Row).ToList();
        }
        // 1.0.1 (the user: no info on when it was installed; not detailed): "installed today / yesterday / 3d ago /
        // 12 Oct" after a row's meta line, from the same time the Installed sort uses; null when the row has none.
        internal string InstalledAgo(object row)
        {
            InstalledSortKey saved = installedSort; installedSort = InstalledSortKey.Installed;
            long ticks;
            try { ticks = InstalledSortTicks(row); } finally { installedSort = saved; }
            if (ticks <= 0) return null;
            DateTime local = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(), today = History.Now.ToLocalTime().Date;
            int days = (int)(today - local.Date).TotalDays;
            string when = days <= 0 ? "today" : days == 1 ? "yesterday" : days < 30 ? days + "d ago" : local.ToString(local.Year == today.Year ? "d MMM" : "MMM yyyy", CultureInfo.InvariantCulture);
            return "installed " + when;
        }
        private long InstalledSortTicks(object row)
        {
            OutfitGroup group = row as OutfitGroup;
            if (group != null) return group.Slots.Select(o => InstalledSortTicks(o)).DefaultIfEmpty(0).Max();
            InstalledOutfit outfit = row as InstalledOutfit;
            GameModRow game = row as GameModRow;
            string source = outfit != null ? (outfit.Managed || outfit.WholeArchive ? outfit.SourcePath : null) : game != null && game.Mod != null ? game.Mod.SourcePath : null;
            ModHistory history = String.IsNullOrEmpty(source) ? null : HistoryData.Peek().Find(source);   // as UsageMeta looks it up
            if (installedSort == InstalledSortKey.Played) return history != null && history.LastPlayed.HasValue ? history.LastPlayed.Value.Ticks : 0;
            if (outfit != null)
            {
                if (outfit.InstalledUtc != DateTime.MinValue) return outfit.InstalledUtc.Ticks;
                try { return File.Exists(outfit.Folder) ? File.GetCreationTimeUtc(outfit.Folder).Ticks : Directory.Exists(outfit.Folder) ? Directory.GetCreationTimeUtc(outfit.Folder).Ticks : 0; }
                catch (Exception) { return 0; }
            }
            if (game != null && game.Mod != null)
            {
                DateTime installed;
                if (DateTime.TryParse(game.Mod.InstalledUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out installed)) return installed.ToUniversalTime().Ticks;
                if (history != null && history.LastInstallOrSwitch.HasValue) return history.LastInstallOrSwitch.Value.Ticks;
            }
            return 0;
        }
    }
}
