using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SlotsInstaller
{
    // Both renderers must see the same app-local font: WinForms/TextRenderer uses GDI,
    // while our illustrated cards use GDI+. Keep the collection alive for the process.
    // No fonts or desktop preferences are installed or changed on Windows.
    internal static class UiFonts
    {
        private static readonly object gate = new object();
        private static PrivateFontCollection collection;
        private static FontFamily family, monospace;
        private static bool initialized;
        internal static bool Ready { get { return initialized; } }
        internal static bool Bundled { get { return LinuxPlatform.IsWine; } }
        internal static FontFamily Family { get { Initialize(); return family; } }
        internal static FontFamily Monospace { get { Initialize(); return monospace; } }
        internal static string FamilyName { get { return Family.Name; } }
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceEx(string path, uint flags, IntPtr reserved);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextFace(IntPtr dc, int count, StringBuilder face);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetGlyphIndices(IntPtr dc, string text, int length, ushort[] indices, uint flags);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SetParameter(uint action, uint value, IntPtr parameter, uint flags);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool GetParameter(uint action, uint value, out uint parameter, uint flags);

        internal static void Initialize()
        {
            lock (gate)
            {
                if (initialized) return;
                if (!Bundled) { family = new FontFamily("Segoe UI"); monospace = new FontFamily("Consolas"); initialized = true; return; }
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts");
                var fonts = new PrivateFontCollection();
                foreach (string name in new[] { "Inter", "LiberationMono" })
                foreach (string style in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
                {
                    string file = Path.Combine(root, name + "-" + style + ".ttf");
                    if (!File.Exists(file) || AddFontResourceEx(file, 0x10, IntPtr.Zero) == 0)
                        throw new IOException("The Linux interface font is missing or could not be loaded. Run the Linux package installer again to repair the application files.");
                    fonts.AddFontFile(file);
                }
                foreach (FontFamily candidate in fonts.Families)
                {
                    if (String.Equals(candidate.Name, "Inter", StringComparison.Ordinal)) family = candidate;
                    if (String.Equals(candidate.Name, "Liberation Mono", StringComparison.Ordinal)) monospace = candidate;
                }
                foreach (FontFamily face in new[] { family, monospace })
                    if (face == null || !face.IsStyleAvailable(FontStyle.Regular) || !face.IsStyleAvailable(FontStyle.Bold) || !face.IsStyleAvailable(FontStyle.Italic) || !face.IsStyleAvailable(FontStyle.Bold | FontStyle.Italic))
                        throw new IOException("The Linux interface font is incomplete. Reinstall the Linux package to repair it.");
                collection = fonts;
                // Wine's defaults can disable smoothing even with scalable fonts. These
                // changes affect only the Manager's dedicated compatibility environment.
                SetParameter(0x004B, 1, IntPtr.Zero, 1);             // SPI_SETFONTSMOOTHING
                SetParameter(0x200B, 0, new IntPtr(2), 1);           // SPI_SETFONTSMOOTHINGTYPE: ClearType
                initialized = true;
            }
        }

        internal static uint SmoothingType
        {
            get { uint value; return GetParameter(0x200A, 0, out value, 0) ? value : 0; }
        }
        internal static bool SmoothingEnabled
        {
            get { uint value; return GetParameter(0x004A, 0, out value, 0) && value != 0; }
        }

        // Detect silent GDI substitution, which otherwise looks like successful setup.
        internal static string Verify()
        {
            Initialize();
            string result = VerifyFace(Family, "↑↓→×…•↗✓");
            if (Bundled)
            {
                VerifyFace(Monospace, "MW0=()");
                if (!SmoothingEnabled || SmoothingType != 2) throw new IOException("Font smoothing could not be enabled in the Manager environment. Use Repair compatibility runtime in the Linux setup window.");
            }
            return result;
        }
        private static string VerifyFace(FontFamily expected, string symbols)
        {
            using (var bitmap = new Bitmap(8, 8))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var font = new Font(expected, 10F))
            {
                IntPtr dc = graphics.GetHdc(), handle = font.ToHfont(), previous = SelectObject(dc, handle);
                try
                {
                    var face = new StringBuilder(128); GetTextFace(dc, face.Capacity, face);
                    if (Bundled)
                    {
                        var indices = new ushort[symbols.Length];
                        if (!String.Equals(face.ToString(), expected.Name, StringComparison.OrdinalIgnoreCase) || GetGlyphIndices(dc, symbols, symbols.Length, indices, 1) == UInt32.MaxValue || Array.IndexOf(indices, (ushort)0xffff) >= 0)
                            throw new IOException("The Linux interface font did not initialize correctly. Use Repair compatibility runtime in the Linux setup window.");
                    }
                    return face.ToString();
                }
                finally { SelectObject(dc, previous); DeleteObject(handle); graphics.ReleaseHdc(dc); GC.KeepAlive(collection); }
            }
        }
    }
}
