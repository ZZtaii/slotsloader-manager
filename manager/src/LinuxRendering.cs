using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SlotsInstaller
{
    internal static class LinuxRendering
    {
        // Unmodified Microsoft x64 GDI+ from Bottles' official gdiplus dependency.
        // Downloaded by Bottles under its EULA; never distributed in our package.
        private const string RendererSha256 = "980ea189929d95eb36e35980fff0c81f7b78de9422771fde8f4ac7a779f5bd89";
        private static IntPtr renderer;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int size);

        // .NET otherwise asks for the system-directory GDI+ even when a verified copy
        // exists beside the EXE. Preload before the first System.Drawing/WinForms use.
        internal static void Initialize()
        {
            if (!LinuxPlatform.IsWine || renderer != IntPtr.Zero) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdiplus.dll");
            if (!File.Exists(path) || new FileInfo(path).Length != 2165248 || Hash(path) != RendererSha256)
                throw new IOException("The verified Linux graphics renderer is missing. Reopen the Linux package installer to repair the application runtime.");
            renderer = LoadLibrary(path);
            if (renderer == IntPtr.Zero) throw new IOException("The Linux graphics renderer could not be loaded. Choose Repair compatibility runtime from the Linux application menu.");
        }

        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static void Verify(Dictionary<string, object> report)
        {
            int hard = BlendedPixels(SmoothingMode.None), smooth = BlendedPixels(SmoothingMode.AntiAlias);
            report["renderer_antialias"] = smooth > 16 && smooth > hard;
            report["renderer_blended_pixels"] = smooth;
            if (LinuxPlatform.IsWine)
            {
                var buffer = new StringBuilder(32768);
                IntPtr module = GetModuleHandle("gdiplus.dll");
                if (module == IntPtr.Zero || GetModuleFileName(module, buffer, buffer.Capacity) == 0)
                    throw new IOException("The graphics renderer could not be checked. Reopen the Linux installer to repair the compatibility environment.");
                string loaded = Path.GetFullPath(buffer.ToString()), expected = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdiplus.dll"));
                string hash = Hash(loaded);
                report["renderer_sha256"] = hash;
                // Do not leak private module paths into the portable diagnostic report.
                if (!String.Equals(loaded, expected, StringComparison.OrdinalIgnoreCase) || hash != RendererSha256)
                    throw new IOException("The compatibility environment did not load the verified application graphics renderer. Reopen the Linux installer or choose Repair compatibility runtime.");
            }
            if (!(bool)report["renderer_antialias"])
                throw new IOException("The graphics renderer is not smoothing shapes correctly. Reopen the Linux installer or choose Repair compatibility runtime.");
        }

        private static int BlendedPixels(SmoothingMode mode)
        {
            using (var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (var pen = new Pen(Color.Black, 2.3F))
                {
                    graphics.Clear(Color.White); graphics.SmoothingMode = mode;
                    graphics.DrawEllipse(pen, 5.25F, 6.75F, 45.5F, 39.25F);
                }
                int blended = 0;
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                    { Color color = bitmap.GetPixel(x, y); if (color.R > 0 && color.R < 255 && color.R == color.G && color.G == color.B) blended++; }
                return blended;
            }
        }
    }
}
