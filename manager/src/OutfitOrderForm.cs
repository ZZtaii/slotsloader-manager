using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (user request): "Outfit order" on the Installed page. One fighter's extra outfits in their slot order;
    // drag a row (or Move up / Move down, Alt+Up / Alt+Down) to the place it should have, then Save. The fighter
    // button opens the portrait grid, one fighter at a time. The engine is OutfitOrder.Apply; nothing is written
    // until Save, and Cancel changes nothing.
    internal sealed partial class MainForm
    {
        private FlatButton loadOrderButton;

        // The fighter folder ("esf016") of a fighter name.
        private static string FighterDir(string name) { string code = CodeOfFighter(name ?? ""); return code == null ? null : "esf" + code; }

        internal void ShowOutfitOrder()
        {
            if (busy || String.IsNullOrWhiteSpace(config.GameRoot) || LoaderFlags.SuiteLoaderInstalled) return;   // 1.0.19: the suite-line loader keeps its numbers in files of another shape
            InstalledOutfit picked = GetInstalledSelection(); OutfitGroup group = GetGroupSelection();
            string start = picked != null ? picked.Character : group != null ? group.Character : installedCharacters.FirstOrDefault();
            string game = config.GameRoot;
            Dictionary<string, int> counts = OutfitOrder.Counts(game);
            if (FighterDir(start) == null || !counts.ContainsKey(FighterDir(start)))
                start = counts.Where(kv => kv.Value > 1).Select(kv => { string n; return Characters.ByCode.TryGetValue(kv.Key.Substring(3), out n) ? n : null; }).FirstOrDefault(n => n != null) ?? start;
            bool running = false; try { running = GameProcesses.Probe.IsGameRunning(); } catch (Exception ex) { Program.Log(ex); }
            // Enabled extra outfits of a fighter that hold no slot yet: the game has not started since they were installed.
            Func<string, List<OutfitSlot>, List<string>> waiting = delegate(string fighter, List<OutfitSlot> slots)
            {
                var sources = new HashSet<string>(slots.Select(s => s.Source.Replace('\\', '/').TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
                return (installed ?? new List<InstalledOutfit>()).Where(o => o.Enabled && String.Equals(o.Character, fighter, StringComparison.OrdinalIgnoreCase)
                    && !sources.Contains(o.Character + "/" + System.IO.Path.GetFileName((o.Folder ?? "").TrimEnd('\\', '/')))).Select(o => o.Name).ToList();
            };
            string fighterDir; List<int> order;
            if (!OutfitOrderForm.Choose(this, game, start, GridFighters(), filterPanel.Grid.Cards, counts, waiting, running, out fighterDir, out order)) return;
            string name; if (!Characters.ByCode.TryGetValue(fighterDir.Substring(3), out name)) name = fighterDir;
            string backups = System.IO.Path.Combine(AppConfig.DataDir, "outfit-order-undo");
            Work("Changing " + name + "'s load order…", delegate { return OutfitOrder.Apply(game, fighterDir, order, backups) ?? ""; }, delegate(string backup)
            {
                if (backup.Length == 0) { SetPageStatus(installedPage, "The order is unchanged.", Palette.Muted); return; }
                string done = name + "'s load order changed";
                OfferUndo(done, delegate
                {
                    Work("Putting " + name + "'s load order back…", delegate { OutfitOrder.Restore(game, backup); return true; },
                        delegate(bool ok) { RefreshInstalled("✓ Undone: " + done + "."); });
                });
                RefreshInstalled("✓ " + done + ". It applies when SF6 starts next.");
            });
        }
    }

    internal sealed class OutfitOrderForm : Form
    {
        private readonly string game;
        private readonly PickerButton fighterButton;
        private readonly OrderRows rows;
        private readonly Panel listHost;
        private readonly Label note, waitingNote;
        private readonly FlatButton save, up, down;
        private readonly ToolStripDropDown popup;
        private readonly FighterGrid grid;
        private readonly Func<string, List<OutfitSlot>, List<string>> waiting;
        private readonly bool gameRunning;
        private string fighterDir = "", fighterName = "";
        private List<int> original = new List<int>();
        internal string FighterDir { get { return fighterDir; } }
        internal List<int> Order { get { return rows.Order; } }
        internal bool Changed { get { return !rows.Order.SequenceEqual(original); } }
        internal OrderRows RowsForTest { get { return rows; } }

        internal static bool Choose(IWin32Window owner, string gameRoot, string fighter, IEnumerable<KeyValuePair<string, string>> fighters, IEnumerable<FighterCard> portraits, Dictionary<string, int> counts,
            Func<string, List<OutfitSlot>, List<string>> waiting, bool gameRunning, out string fighterDir, out List<int> order)
        {
            fighterDir = null; order = null;
            if (Program.Headless) return false;
            using (var form = new OutfitOrderForm(gameRoot, fighter, fighters, portraits, counts, waiting, gameRunning))
            {
                if (form.ShowDialog(owner) != DialogResult.OK || !form.Changed) return false;
                fighterDir = form.FighterDir; order = form.Order;
                return true;
            }
        }

        internal OutfitOrderForm(string gameRoot, string fighter, IEnumerable<KeyValuePair<string, string>> fighters, IEnumerable<FighterCard> portraits, Dictionary<string, int> counts,
            Func<string, List<OutfitSlot>, List<string>> waiting, bool gameRunning)
        {
            // As tall as the fighter with the most outfits needs, between 4 and 7 rows (more scroll).
            const int rowHeight = 60; int visibleRows = Math.Max(4, Math.Min(7, (counts ?? new Dictionary<string, int>()).Values.DefaultIfEmpty(0).Max()));
            game = gameRoot; this.waiting = waiting; this.gameRunning = gameRunning;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Load order"; StartPosition = FormStartPosition.CenterParent; Name = "outfitOrderForm";
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            var title = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoEllipsis = true, Text = "Load order of extra outfits" };
            var pickRow = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 4, 0, 6) };
            fighterButton = new PickerButton { Dock = DockStyle.Left, Width = 220, Name = "outfitOrderFighter", AccessibleName = "Fighter" };
            pickRow.Controls.Add(fighterButton);
            note = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 2, 0, 0),
                Text = "Drag an outfit to the place it should have in the game's outfit list. The slots stay the same; the outfits change places." };
            rows = new OrderRows(rowHeight) { Dock = DockStyle.Top, Name = "outfitOrderRows", AccessibleName = "Extra outfits in slot order" };
            listHost = new Panel { Dock = DockStyle.Top, Height = rowHeight * visibleRows + 2, AutoScroll = true, BackColor = Palette.Background };
            listHost.Controls.Add(rows);
            waitingNote = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 40, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 6, 0, 0), AutoEllipsis = true };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 14, 0, 0) };
            save = new FlatButton("Save order", true) { Width = 130, Dock = DockStyle.Right, Font = Palette.Small, Name = "outfitOrderSave" }; save.Click += delegate { Accept(); };
            var cancel = new FlatButton("Cancel", false) { Width = 100, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            up = new FlatButton("Move up", false) { Width = 100, Dock = DockStyle.Left, Font = Palette.Small, Name = "outfitOrderUp" }; up.Click += delegate { rows.MoveSelected(-1); rows.Focus(); };
            down = new FlatButton("Move down", false) { Width = 110, Dock = DockStyle.Left, Font = Palette.Small, Name = "outfitOrderDown" }; down.Click += delegate { rows.MoveSelected(1); rows.Focus(); };
            footer.Controls.Add(new Panel { Dock = DockStyle.Fill }); footer.Controls.Add(down); footer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); footer.Controls.Add(up);
            footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(save);
            Controls.Add(footer); Controls.Add(waitingNote); Controls.Add(listHost); Controls.Add(note); Controls.Add(pickRow); Controls.Add(title);
            ClientSize = new Size(760, Padding.Vertical + title.Height + pickRow.Height + note.Height + listHost.Height + waitingNote.Height + footer.Height);

            // The fighter picker: the library's portrait grid, one fighter at a time; each card counts the outfits with a slot.
            var panel = new Panel { BackColor = Palette.Surface, Padding = new Padding(8), Name = "outfitOrderFighters" };
            grid = new FighterGrid { Dock = DockStyle.Fill, Name = "outfitOrderGrid", AccessibleName = "Fighters" };
            grid.SetFighters(fighters);
            foreach (FighterCard card in portraits ?? new FighterCard[0]) if (card.Portrait != null) grid.SetPortrait(card.Code, (Bitmap)card.Portrait.Clone(), card.PortraitNote);
            grid.SetCounts((counts ?? new Dictionary<string, int>()).Where(kv => kv.Key.Length > 3).GroupBy(kv => kv.Key.Substring(3)).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal));
            panel.Controls.Add(grid);
            var host = new ToolStripControlHost(panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            popup = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true, DropShadowEnabled = true, BackColor = Palette.Border };
            popup.Items.Add(host);
            grid.SelectionChanged += delegate
            {
                // One fighter: the card just clicked (the one that was not selected before).
                string code = grid.Selected.FirstOrDefault(c => "esf" + c != fighterDir) ?? grid.Selected.FirstOrDefault();
                if (code == null) { grid.SetSelected(new[] { fighterDir.Length > 3 ? fighterDir.Substring(3) : "" }, false); return; }
                string name; if (!Characters.ByCode.TryGetValue(code, out name)) name = FighterPortraits.DisplayName(code);
                popup.Close(); Load("esf" + code, name);
            };
            fighterButton.Click += delegate
            {
                if (popup.Visible) { popup.Close(); return; }
                int width = DpiMetrics.Px(this, 720), height = Math.Min(DpiMetrics.Px(this, 520), grid.HeightFor(width - DpiMetrics.Px(this, 16)));
                panel.Size = new Size(width, height + DpiMetrics.Px(this, 16)); host.Size = panel.Size;
                popup.Show(fighterButton, new Point(0, fighterButton.Height + DpiMetrics.Px(this, 4)));
                grid.Focus();
            };
            Disposed += delegate { popup.Dispose(); };
            rows.OrderChanged += delegate { UpdateState(); };
            rows.SelectionChanged += delegate { UpdateState(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && !popup.Visible) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter && save.Enabled && !popup.Visible) { e.Handled = true; Accept(); }
            };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            string dir = null, shown = fighter ?? "";
            foreach (var kv in Characters.ByCode) if (String.Equals(kv.Value, fighter, StringComparison.OrdinalIgnoreCase)) { dir = "esf" + kv.Key; shown = kv.Value; }
            Load(dir ?? "", shown);
            ActiveControl = rows;
        }

        internal void Load(string dir, string name)
        {
            fighterDir = dir ?? ""; fighterName = name ?? "";
            fighterButton.Text = fighterName.Length == 0 ? "Choose a fighter" : fighterName;
            if (fighterDir.Length > 3) grid.SetSelected(new[] { fighterDir.Substring(3) }, false);
            List<OutfitSlot> slots = fighterDir.Length == 0 ? new List<OutfitSlot>() : OutfitOrder.Slots(game, fighterDir);
            original = slots.Select(s => s.CostumeNo).ToList();
            rows.Empty = fighterDir.Length == 0 ? "Choose a fighter." : fighterName + " has no extra outfit with a slot yet. Install one, then start SF6 once.";
            rows.SetSlots(slots, fighterName);
            rows.Height = DpiMetrics.Px(this, 60) * Math.Max(1, slots.Count);
            List<string> later = waiting == null || fighterName.Length == 0 ? new List<string>() : waiting(fighterName, slots);
            waitingNote.Text = later.Count == 0 ? "" : ReplacePlanner.N(later.Count, "more outfit") + " get" + (later.Count == 1 ? "s" : "") + " a slot when SF6 has started once: " + String.Join(", ", later);
            UpdateState();
        }

        private void UpdateState()
        {
            bool changed = Changed;
            save.Enabled = changed && !gameRunning;
            up.Enabled = rows.CanMove(-1); down.Enabled = rows.CanMove(1);
            note.Text = gameRunning ? "Close SF6 to change the order: it reads the slots when it starts."
                : changed ? "Applies when SF6 starts next. Replays and the outfit the game itself remembers keep pointing at the slot, so they show the outfit that sits there now."
                : "Drag an outfit to the place it should have in the game's outfit list. The slots stay the same; the outfits change places.";
            note.ForeColor = gameRunning ? Palette.Accent : Palette.Muted;
        }
        private void Accept() { if (!save.Enabled) return; DialogResult = DialogResult.OK; Close(); }
    }
}
