using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.1 (the user): the Installed page's character filter is the library's portrait grid in a popup instead of a
    // dropdown. Several fighters can be picked (OR-ed, like the library); each card counts the installed outfits of
    // that fighter. The popup stays open while picking; a click outside or Esc closes it. Portraits are copies of the
    // library grid's (EnsurePortraits fills both).
    internal sealed partial class MainForm
    {
        private PickerButton installedCharacterButton;
        private ToolStripDropDown characterPopup;
        private FighterGrid installedFighters;
        private FlatButton characterPopupClear;
        private readonly HashSet<string> installedCharacters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // fighter names
        internal IEnumerable<string> InstalledCharacterFilter { get { return installedCharacters.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); } }

        private Control BuildInstalledCharacterPicker()
        {
            installedCharacterButton = new PickerButton { Width = 170, Height = 30, Margin = new Padding(0, 0, 8, 0), Name = "installedCharacterButton", AccessibleName = "Character filter" };
            // The press that closed the open popup (Closing, AppClicked over this button) must not reopen it on release.
            // Decided per click: every press takes the flag, so it can never swallow a later click.
            installedCharacterButton.MouseDown += delegate { suppressCharacterClick = characterPopupClosedByButton; characterPopupClosedByButton = false; };
            installedCharacterButton.Click += delegate { bool skip = suppressCharacterClick; suppressCharacterClick = false; if (!skip) ToggleCharacterPopup(); };
            UpdateCharacterButton();
            // 1.0.1 (the user: the first open lagged): build the popup and pre-render its cards once the app is idle.
            Shown += delegate
            {
                if (Program.Headless) return;
                EventHandler idle = null;
                idle = delegate { Application.Idle -= idle; WarmCharacterPicker(); };
                Application.Idle += idle;
            };
            return installedCharacterButton;
        }

        private static string CodeOfFighter(string name)
        {
            foreach (var kv in Characters.ByCode) if (String.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }

        private void UpdateCharacterButton()
        {
            if (installedCharacterButton == null) return;
            List<string> names = InstalledCharacterFilter.ToList();
            installedCharacterButton.Text = names.Count == 0 ? "All characters" : names.Count == 1 ? names[0] : names[0] + " + " + (names.Count - 1);
            installedCharacterButton.Active = names.Count > 0;
            installedCharacterButton.AccessibleDescription = names.Count == 0 ? "All characters" : String.Join(", ", names);
        }

        // A click on the button while the popup is open first auto-closes it (an outside click), then reaches the
        // button: that click must not open it again (the user: pressing All characters again didn't close it).
        private bool characterPopupClosedByButton, suppressCharacterClick;
        private void ToggleCharacterPopup()
        {
            if (characterPopup != null && characterPopup.Visible) { characterPopup.Close(); return; }
            PrepareCharacterPopup();
            characterPopup.Show(installedCharacterButton, new Point(0, installedCharacterButton.Height + Px(4)));
            installedFighters.Focus();
            portraitsRequested = true; EnsurePortraits(false);
        }

        // Fighters, portraits (copies of the library grid's), counts, selection and size: before every open and at warm-up.
        private void PrepareCharacterPopup()
        {
            EnsureCharacterPopup();
            installedFighters.SetFighters(GridFighters());
            foreach (FighterCard card in filterPanel.Grid.Cards)   // portraits the library grid already has
                if (card.Portrait != null && !installedFighters.Cards.Any(c => c.Code == card.Code && c.Portrait != null)) installedFighters.SetPortrait(card.Code, (Bitmap)card.Portrait.Clone(), card.PortraitNote);
            installedFighters.SetCounts(InstalledCharacterCounts());
            installedFighters.SetSelected(installedCharacters.Select(CodeOfFighter).Where(c => c != null), false);
            characterPopupClear.Enabled = installedCharacters.Count > 0;
            int width = Px(720), gridHeight = Math.Min(Px(560), installedFighters.HeightFor(width - Px(16)));
            if (installedFighters.Size != new Size(width - Px(16), gridHeight)) installedFighters.Size = new Size(width - Px(16), gridHeight);
            var host = (ToolStripControlHost)characterPopup.Items[0];
            Size panel = new Size(width, gridHeight + Px(52));
            if (host.Control.Size != panel) { host.Control.Size = panel; host.Size = panel; }
        }

        // Window handles now, then every card's scaled portrait (grey, then colour for hover and selection) a few per
        // tick, so the first open neither creates windows nor scales 32 portraits before it can paint.
        private Timer characterWarmTimer;
        private void WarmCharacterPicker()
        {
            if (IsDisposed || filterPanel == null) return;
            try
            {
                PrepareCharacterPopup();
                IntPtr created = characterPopup.Handle; created = ((ToolStripControlHost)characterPopup.Items[0]).Control.Handle; created = installedFighters.Handle;
                portraitsRequested = true; EnsurePortraits(false);
                QueueCharacterRenders();
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }
        internal void QueueCharacterRenders()
        {
            if (installedFighters == null || IsDisposed) return;
            if (characterWarmTimer == null)
            {
                characterWarmTimer = new Timer { Interval = 15 };
                Disposed += delegate { characterWarmTimer.Dispose(); };
                characterWarmTimer.Tick += delegate
                {
                    int done = 0;
                    foreach (bool colour in new[] { false, true })
                        foreach (FighterCard card in installedFighters.Cards)
                        {
                            if (card.Portrait == null || installedFighters.IsRendered(card, colour)) continue;
                            installedFighters.Rendered(card, colour);
                            if (++done >= 3) return;   // the rest on the next ticks
                        }
                    characterWarmTimer.Stop();
                };
            }
            characterWarmTimer.Start();
        }

        private void EnsureCharacterPopup()
        {
            if (characterPopup != null) return;
            var panel = new Panel { BackColor = Palette.Surface, Padding = new Padding(Px(8)), Name = "characterPopupPanel" };
            var top = new Panel { Dock = DockStyle.Top, Height = Px(36) };
            var caption = new Label { UseMnemonic = false, Text = "CHARACTERS", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleLeft };
            characterPopupClear = Button("Clear", false, 72, delegate { SetInstalledCharacters(new string[0]); installedFighters.SetSelected(new string[0], false); characterPopupClear.Enabled = false; });
            characterPopupClear.Dock = DockStyle.Right; characterPopupClear.Font = Palette.Small; characterPopupClear.Margin = Padding.Empty; characterPopupClear.Height = Px(28); characterPopupClear.Width = Px(72);
            top.Controls.Add(caption); top.Controls.Add(characterPopupClear);
            installedFighters = new FighterGrid { Dock = DockStyle.Fill, Name = "installedFighters", AccessibleName = "Characters" };
            installedFighters.SelectionChanged += delegate
            {
                var names = installedFighters.Selected.Select(code => { string n; return Characters.ByCode.TryGetValue(code, out n) ? n : FighterPortraits.DisplayName(code); });
                SetInstalledCharacters(names);
                characterPopupClear.Enabled = installedCharacters.Count > 0;
            };
            panel.Controls.Add(installedFighters); panel.Controls.Add(top); installedFighters.BringToFront();
            var host = new ToolStripControlHost(panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            characterPopup = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true, DropShadowEnabled = true, BackColor = Palette.Border };
            characterPopup.Items.Add(host);
            characterPopup.Closing += delegate(object sender, ToolStripDropDownClosingEventArgs e)
            {
                characterPopupClosedByButton = e.CloseReason == ToolStripDropDownCloseReason.AppClicked
                    && installedCharacterButton.ClientRectangle.Contains(installedCharacterButton.PointToClient(Cursor.Position));
                // That press reaches the button's MouseDown in the same message; if Windows drops it, this clears the flag first.
                if (characterPopupClosedByButton) BeginInvoke((MethodInvoker)delegate { characterPopupClosedByButton = false; });
            };
            characterPopup.Closed += delegate { if (installedCharacterButton.CanFocus) installedCharacterButton.Focus(); };
            Disposed += delegate { characterPopup.Dispose(); };
        }

        private void SetInstalledCharacters(IEnumerable<string> names)
        {
            var next = new HashSet<string>(names.Where(n => !String.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            if (next.SetEquals(installedCharacters)) return;
            installedCharacters.Clear(); installedCharacters.UnionWith(next);
            UpdateCharacterButton();
            if (!fillingInstalledFilters) FillInstalled();
            QueueViewSave();
        }

        // Installed outfits per fighter code (all of them, whatever the other filters say).
        private Dictionary<string, int> InstalledCharacterCounts()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (InstalledOutfit o in installed ?? new List<InstalledOutfit>())
            {
                string code = CodeOfFighter(o.Character ?? ""); if (code == null) continue;
                int n; counts.TryGetValue(code, out n); counts[code] = n + 1;
            }
            return counts;
        }

        // Saved as "Ryu|Ken"; a pre-1.0.1 single name (or "All characters") reads too.
        private string SavedInstalledCharacters() { return String.Join("|", InstalledCharacterFilter); }
        private void RestoreInstalledCharacters(string saved)
        {
            var names = (saved ?? "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0 && CodeOfFighter(s) != null);
            installedCharacters.Clear(); installedCharacters.UnionWith(names); UpdateCharacterButton();
        }
    }

    // A dropdown-looking button: the label on the left, a chevron on the right (the Installed page's character filter).
    internal sealed class PickerButton : Control
    {
        private bool hot;
        internal bool Active;
        internal PickerButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand; Font = Palette.Small; ForeColor = Palette.Text; AccessibleRole = AccessibleRole.ButtonDropDown;
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Enter || k == Keys.Space || k == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || (e.KeyCode == Keys.Down && e.Alt) || e.KeyCode == Keys.F4) { e.Handled = e.SuppressKeyPress = true; OnClick(EventArgs.Empty); }
            base.OnKeyDown(e);
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (CanFocus) Focus(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush outside = new SolidBrush(Parent == null ? Palette.Background : Parent.BackColor)) g.FillRectangle(outside, ClientRectangle);
            float dpi = DeviceDpi / 96F, line = Math.Max(1F, dpi);
            var face = new RectangleF(line / 2, line / 2, Width - line, Height - line);
            Glyphs.FillRounded(g, hot ? Palette.Border : Palette.Raised, face, 4 * dpi);
            if (Focused && ShowFocusCues) Glyphs.DrawRounded(g, Palette.Text, Math.Max(1.5F, 2 * dpi), face, 4 * dpi);
            int pad = (int)(10 * dpi), chevron = (int)(22 * dpi);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(pad, 0, Math.Max(1, Width - pad - chevron), Height), Active ? Palette.Accent : ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            Glyphs.Chevron(g, new PointF(Width - chevron / 2F - 2 * dpi, Height / 2F), 4.5F * dpi, ChevronDirection.Down, Palette.Text, 1.6F * dpi);
        }
    }
}
