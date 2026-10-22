using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.11: no white blocks at start. The window used to appear before its controls had painted: the Shown handlers
    // (view restore, splitters, mode, game poll, the first scan) run before the first WM_PAINT, so for about half a
    // second most of the window was the bare white surface. The window is now cloaked (DWM keeps it off screen but it
    // still lays out and paints) from the constructor until everything has painted once, then shown in one frame.
    internal sealed partial class MainForm
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
        private const int DwmCloak = 13;
        private bool startCloaked;
        private Timer revealFallback;

        // Called by the constructor once the handle exists (it measures the screen, so it always does by then).
        private void CloakUntilPainted()
        {
            if (Program.Headless || !IsHandleCreated) return;
            if (!SetCloak(true)) return;
            startCloaked = true;
            // Never leave the window invisible: whatever happens before Shown, it appears within two seconds.
            revealFallback = new Timer { Interval = 2000 };
            revealFallback.Tick += delegate { RevealWindow(false); };
            revealFallback.Start();
            Shown += delegate { BeginInvoke((MethodInvoker)delegate { RevealWindow(true); }); };   // after every Shown handler
        }

        // paintFirst: paint the whole tree now, while still cloaked, so the first visible frame is complete.
        private void RevealWindow(bool paintFirst)
        {
            if (revealFallback != null) { revealFallback.Dispose(); revealFallback = null; }
            if (!startCloaked || IsDisposed) return;
            startCloaked = false;
            const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
            if (paintFirst) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            SetCloak(false);
        }

        private bool SetCloak(bool on)
        {
            try { int value = on ? 1 : 0; return DwmSetWindowAttribute(Handle, DwmCloak, ref value, 4) == 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}
