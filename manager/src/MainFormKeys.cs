using System;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Uninstall keys. A tap of Ctrl on its own (pressed and released with no other key or click in between) presses
    // Uninstall: the footer's in the Mod library (2.5.2), the inspector's on the Installed page (2.5.3; 2.5.2 used
    // Space there). Both act exactly like a click on that button: one click, no confirmation, Undo.
    // W/A/S/D work like the arrows in the Installed list (2.5.2).
    internal sealed partial class MainForm
    {
        private CtrlTapFilter ctrlTapFilter;
        private bool ctrlTapArmed;
        internal int CtrlUninstalls, InstalledCtrlUninstalls;   // tests: taps that reached the Uninstall buttons
        internal bool CtrlTapRegistered;   // tests: the filter is in place once the constructor returns (2.5.5)

        private void WireUninstallKeys()
        {
            // 2.5.5: registered at once. The constructor has already created the window handle by now (it measures the
            // screen), so a HandleCreated hook never ran and 2.5.2–2.5.4 never saw a Ctrl tap in the real app.
            ctrlTapFilter = new CtrlTapFilter(this);
            Application.AddMessageFilter(ctrlTapFilter); CtrlTapRegistered = true;
            Disposed += delegate { Application.RemoveMessageFilter(ctrlTapFilter); };
            Deactivate += delegate { ctrlTapArmed = false; };
        }

        // Ctrl down (not an auto-repeat) arms; any other key, mouse button or wheel disarms; Ctrl up while armed fires.
        internal void CtrlTapMessage(int msg, int key, bool repeat)
        {
            const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, VK_CONTROL = 0x11;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) { if (key != VK_CONTROL) ctrlTapArmed = false; else if (!repeat) ctrlTapArmed = true; return; }
            if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                if (key != VK_CONTROL) return;
                bool fire = ctrlTapArmed; ctrlTapArmed = false;
                if (!fire) return;
                if (CtrlUninstallsHere(libraryPage)) PressUninstall(libraryUninstallButton, ref CtrlUninstalls);
                else if (CtrlUninstallsHere(installedPage)) PressUninstall(removeInstalledButton, ref InstalledCtrlUninstalls);
                return;
            }
            ctrlTapArmed = false;   // a click or wheel turn: Ctrl was a modifier
        }
        // Where a Ctrl tap means Uninstall: the Mod library or Installed page, outside text entry, the Filters panel and toasts.
        internal bool CtrlUninstallsHere(Panel page)
        {
            if (page == null || currentPage != page || !page.Visible) return false;
            Control focused = FocusedControl();
            if (focused == null) return true;
            if (filterPanel != null && filterPanel.Visible && filterPanel.Contains(focused)) return false;
            if (toastHost != null && toastHost.Contains(focused)) return false;
            if (!page.Contains(focused)) return false;
            return !(focused is TextBoxBase || focused is ComboBox || focused is UpDownBase);
        }
        // A disabled or hidden button does nothing, like a click on it; never while a task runs.
        private void PressUninstall(FlatButton button, ref int count)
        {
            if (busy || button == null || !button.Enabled || !button.Visible) return;
            count++;
            button.PerformClick();
        }

        // W/S in the Installed list: the previous or next row, like Up/Down (one row selected, nothing past the ends).
        private void MoveInstalledSelection(int delta)
        {
            if (installedList.Items.Count == 0) return;
            int index = installedList.FocusedItem != null ? installedList.FocusedItem.Index : installedList.SelectedIndices.Count > 0 ? installedList.SelectedIndices[0] : -1;
            int next = index < 0 ? 0 : Math.Max(0, Math.Min(installedList.Items.Count - 1, index + delta));
            ListViewItem item = installedList.Items[next];
            if (next == index && item.Selected && installedList.SelectedItems.Count == 1) return;
            foreach (ListViewItem other in installedList.SelectedItems) if (other != item) other.Selected = false;
            item.Selected = true; item.Focused = true; item.EnsureVisible();
        }

        private sealed class CtrlTapFilter : IMessageFilter
        {
            private readonly MainForm form;
            internal CtrlTapFilter(MainForm form) { this.form = form; }
            public bool PreFilterMessage(ref Message m)
            {
                int msg = m.Msg;
                bool key = msg >= 0x0100 && msg <= 0x0105;
                bool mouse = msg == 0x0201 || msg == 0x0204 || msg == 0x0207 || msg == 0x020A || msg == 0x020B || msg == 0x00A1 || msg == 0x00A4;
                if (!key && !mouse) return false;
                // Tests: a hidden window is never the active form; FocusOverride stands in for the focus there.
                if (Form.ActiveForm != form && form.FocusOverride == null) { form.ctrlTapArmed = false; return false; }
                form.CtrlTapMessage(msg, (int)(long)m.WParam, key && ((long)m.LParam & 0x40000000L) != 0);
                return false;   // never swallows the message
            }
        }
    }
}
