using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Framework fonts use point units, but the HFONT passed to certain native
    // edit/list windows can retain the creation-time 96-DPI height. Materialize
    // that font for the control's actual device context on every DPI change.
    internal sealed class NativeDpiFont
    {
        [StructLayout(LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private sealed class LogFont
        {
            public int Height,Width,Escapement,Orientation,Weight;
            public byte Italic,Underline,StrikeOut,CharSet,OutPrecision,ClipPrecision,Quality,PitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string FaceName;
        }
        [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateFontIndirect(LogFont font);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        private readonly Control control;
        private IntPtr font;
        private NativeDpiFont(Control control)
        {
            this.control=control;
            control.HandleCreated+=delegate {Apply();};control.FontChanged+=delegate {Apply();};
            control.DpiChangedAfterParent+=delegate {Apply();};
            control.HandleDestroyed+=delegate {Release();};control.Disposed+=delegate {Release();};
            Apply();
        }
        internal static void Attach(Control control) {new NativeDpiFont(control);}
        private void Apply()
        {
            if(!control.IsHandleCreated || control.IsDisposed) return;
            var description=new LogFont();
            using(Graphics graphics=control.CreateGraphics()) control.Font.ToLogFont(description,graphics);
            // Some framework builds obtain the font from a system-DPI cache.
            // Point size is the stable input; HFONT height is a device value.
            description.Height=-(int)Math.Round(control.Font.SizeInPoints*control.DeviceDpi/72.0);
            IntPtr next=CreateFontIndirect(description);if(next==IntPtr.Zero)return;
            SendMessage(control.Handle,0x30,next,new IntPtr(1));
            IntPtr previous=font;font=next;if(previous!=IntPtr.Zero)DeleteObject(previous);
        }
        private void Release() {if(font!=IntPtr.Zero) {DeleteObject(font);font=IntPtr.Zero;}}
    }
}
