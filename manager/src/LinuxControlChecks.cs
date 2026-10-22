using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Synthetic native controls only: no application settings, game files or host helper.
    internal static class LinuxControlChecks
    {
        [StructLayout(LayoutKind.Sequential)] private struct Range
        { internal int Size, Mask, Min, Max, Page, Position, Track; }
        [StructLayout(LayoutKind.Sequential)] private struct ComboInfo
        { internal int Size; internal LinuxControls.Rect Item, Button; internal int State; internal IntPtr Combo, Edit, List; }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref Range info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out LinuxControls.Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref ComboInfo info);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("ntdll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr wine_get_version();

        private static bool InWine()
        { try { return wine_get_version() != IntPtr.Zero; } catch (DllNotFoundException) { return false; } catch (EntryPointNotFoundException) { return false; } }

        private static void Require(bool value, string text) { if (!value) throw new Exception(text); }
        private static int Position(Control c, bool vertical)
        { var info = new Range { Size = Marshal.SizeOf(typeof(Range)), Mask = 4 }; Require(GetScrollInfo(c.Handle, vertical ? 1 : 0, ref info), "native scroll position unavailable"); return info.Position; }
        private static void Scroll(Control c, int request, bool vertical)
        { SendMessage(c.Handle, vertical ? 0x115 : 0x114, new IntPtr(request), IntPtr.Zero); Application.DoEvents(); }
        private static void Key(Control c, Keys key)
        { c.Focus(); SendMessage(c.Handle, 0x100, new IntPtr((int)key), IntPtr.Zero); SendMessage(c.Handle, 0x101, new IntPtr((int)key), IntPtr.Zero); Application.DoEvents(); }
        private static void DarkTrack(IntPtr window, bool vertical)
        {
            LinuxControls.ScrollInfo info; LinuxControls.Rect bounds;
            Require(LinuxControls.Scrollbar(window, vertical, out info) && GetWindowRect(window, out bounds), "scrollbar not visible");
            GetWindowRect(window, out bounds);
            IntPtr dc = GetWindowDC(window);
            try
            {
                int arrow = LinuxControls.ArrowExtent(info, vertical);
                int x = info.Bounds.Left - bounds.Left + (vertical ? 1 : arrow + 1);
                int y = info.Bounds.Top - bounds.Top + (vertical ? arrow + 1 : 1);
                uint pixel = GetPixel(dc, x, y);
                Require(pixel != UInt32.MaxValue && (pixel & 255) < 100 && ((pixel >> 8) & 255) < 100 && ((pixel >> 16) & 255) < 100, "native scrollbar track remains bright: " + pixel.ToString("X8"));
            }
            finally { ReleaseDC(window, dc); }
        }

        private static void VisibleParts(IntPtr window, bool vertical)
        {
            LinuxControls.ScrollInfo info; LinuxControls.Rect bounds;
            Require(LinuxControls.Scrollbar(window, vertical, out info) && GetWindowRect(window, out bounds), "scrollbar not visible");
            GetWindowRect(window, out bounds);
            int arrow = LinuxControls.ArrowExtent(info, vertical), length = vertical ? info.Bounds.Bottom - info.Bounds.Top : info.Bounds.Right - info.Bounds.Left;
            Require(info.ThumbEnd > info.ThumbStart && info.ThumbStart >= arrow && info.ThumbEnd <= length - arrow, "native thumb does not fit the track");
            IntPtr dc = GetWindowDC(window);
            try
            {
                int centerX = (info.Bounds.Left + info.Bounds.Right) / 2 - bounds.Left;
                int centerY = (info.Bounds.Top + info.Bounds.Bottom) / 2 - bounds.Top;
                uint edge = GetPixel(dc, info.Bounds.Left - bounds.Left, info.Bounds.Top - bounds.Top);
                Require(PalettePixel(edge, Palette.Surface) || PalettePixel(edge, Palette.Raised), "native light chrome remains at the scrollbar corner");
                edge = GetPixel(dc, vertical ? info.Bounds.Left - bounds.Left : centerX, vertical ? centerY : info.Bounds.Top - bounds.Top);
                Require(PalettePixel(edge, Palette.Surface), "native light chrome remains along the scrollbar edge");
                int thumb = (info.ThumbStart + info.ThumbEnd) / 2;
                uint pixel = GetPixel(dc, vertical ? centerX : info.Bounds.Left - bounds.Left + thumb, vertical ? info.Bounds.Top - bounds.Top + thumb : centerY);
                Require(VisibleInk(pixel), "thumb is not painted at the native hit area");
                foreach (bool end in new[] { false, true })
                {
                    int offset = end ? length - arrow / 2 : arrow / 2, ink = 0;
                    int x = vertical ? centerX : info.Bounds.Left - bounds.Left + offset;
                    int y = vertical ? info.Bounds.Top - bounds.Top + offset : centerY;
                    for (int dx = -3; dx <= 3; dx++) for (int dy = -3; dy <= 3; dy++) if (VisibleInk(GetPixel(dc, x + dx, y + dy))) ink++;
                    Require(ink > 0, "arrow is not painted at the native track end");
                }
            }
            finally { ReleaseDC(window, dc); }
        }
        private static bool PalettePixel(uint pixel, Color color)
        { return pixel == ((uint)color.R | ((uint)color.G << 8) | ((uint)color.B << 16)); }
        private static bool VisibleInk(uint pixel)
        {
            int red = (int)(pixel & 255), green = (int)((pixel >> 8) & 255), blue = (int)((pixel >> 16) & 255);
            return pixel != UInt32.MaxValue && red < 240 && green < 240 && blue < 240 &&
                Math.Abs(red - Palette.Surface.R) + Math.Abs(green - Palette.Surface.G) + Math.Abs(blue - Palette.Surface.B) > 18;
        }

        internal static int Run(string output)
        {
            string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
            bool? previous = LinuxPlatform.TestLinux; bool actualLinux = InWine();
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action action)
            { try { action(); report.Add("PASS " + name); } catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex.Message); } Console.WriteLine(report[report.Count - 1]); };
            LinuxPlatform.TestLinux = true;
            try
            {
                using (var form = new Form { Text = "Linux control fixture", ClientSize = new Size(780, 560), BackColor = Palette.Background, StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi })
                using (var list = new BufferedModList { Bounds = new Rectangle(16, 16, 240, 350), View = View.Details, BackColor = Palette.Surface, ForeColor = Palette.Text, BorderStyle = BorderStyle.FixedSingle, Font = Palette.Normal, FullRowSelect = true, HideSelection = false })
                using (var panel = new Panel { Bounds = new Rectangle(276, 16, 210, 350), BackColor = Palette.Surface, AutoScroll = true, BorderStyle = BorderStyle.None })
                using (var text = new TextBox { Bounds = new Rectangle(506, 16, 252, 350), Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Palette.Raised, ForeColor = Palette.Text, BorderStyle = BorderStyle.Fixed3D, Font = Palette.Normal })
                using (var checkbox = new CheckBox { Bounds = new Rectangle(16, 390, 240, 32), Text = "Deep search", Font = Palette.Normal, BackColor = Palette.Surface, ForeColor = Palette.Text })
                using (var combo = new DarkCombo { Bounds = new Rectangle(276, 390, 210, 32), DropDownStyle = ComboBoxStyle.DropDownList, Font = Palette.Normal, MaxDropDownItems = 6 })
                {
                    list.Columns.Add("Fixture rows", 340);
                    for (int i = 0; i < 80; i++) { list.Items.Add("Item " + i); combo.Items.Add("Choice " + i); }
                    var content = new Label { Location = Point.Empty, Size = new Size(380, 1200), BackColor = Palette.Surface, ForeColor = Palette.Text, Text = "Scrollable fixture\r\n" + String.Join("\r\n", Enumerable.Range(0, 60).Select(i => "Row " + i)), Font = Palette.Normal };
                    panel.Controls.Add(content); text.Lines = Enumerable.Range(0, 70).Select(i => "Long fixture line " + i + "    " + new string('.', 65)).ToArray(); combo.SelectedIndex = 0;
                    form.Controls.AddRange(new Control[] { list, panel, text, checkbox, combo });
                    DarkTheme.Apply(form); form.Show(); Application.DoEvents(); form.Refresh(); Application.DoEvents();
                    check("native list, panel and text scrollbar tracks use the dark palette", delegate { foreach (Control c in new Control[] { list, panel, text }) { DarkTrack(c.Handle, true); DarkTrack(c.Handle, false); } });
                    check("thumbs and arrow glyphs are painted at their native hit areas", delegate { foreach (Control c in new Control[] { list, panel, text }) { VisibleParts(c.Handle, true); VisibleParts(c.Handle, false); } });
                    check("native page and line scrolling remain active", delegate
                    {
                        foreach (Control c in new Control[] { list, panel, text })
                        {
                            Scroll(c, 6, true); int top = Position(c, true); Scroll(c, 3, true); int page = Position(c, true);
                            Require(page > top, c.GetType().Name + " PageDown did not advance"); Scroll(c, 0, true); Require(Position(c, true) < page, "line up did not move");
                            Scroll(c, 3, false); Require(Position(c, false) > 0, "horizontal PageRight did not move");
                        }
                    });
                    check("mouse wheel and keyboard navigation still reach the native list", delegate
                    {
                        Scroll(list, 6, true); int before = Position(list, true);
                        SendMessage(list.Handle, 0x20A, new IntPtr(unchecked((int)0xFF880000)), IntPtr.Zero); Application.DoEvents();
                        Require(Position(list, true) > before, "mouse wheel did not advance");
                        list.Items[0].Selected = true; list.Items[0].Focused = true; Key(list, Keys.End);
                        Require(list.FocusedItem != null && list.FocusedItem.Index == 79 && Position(list, true) > 0, "End did not focus/reveal the last row");
                        Key(list, Keys.Home); Require(list.FocusedItem.Index == 0 && Position(list, true) == 0, "Home did not restore the first row");
                    });
                    check("native thumb geometry and Linux pointer dragging remain functional", delegate
                    {
                        LinuxControls.ScrollInfo initial; Require(LinuxControls.Scrollbar(list.Handle, true, out initial) && initial.ThumbEnd > initial.ThumbStart, "native thumb geometry missing");
                        if (!actualLinux) return;   // never move the Windows user's pointer for a fixture
                        Scroll(list, 6, true); LinuxControls.ScrollInfo bar; Require(LinuxControls.Scrollbar(list.Handle, true, out bar), "thumb missing");
                        int x = (bar.Bounds.Left + bar.Bounds.Right) / 2, y = bar.Bounds.Top + (bar.ThumbStart + bar.ThumbEnd) / 2;
                        Point previousCursor; GetCursorPos(out previousCursor); Exception dragError = null;
                        var move = new Thread(delegate()
                        {
                            Thread.Sleep(120); SetCursorPos(x, y + 100); Thread.Sleep(120);
                            try { DarkTrack(list.Handle, true); } catch (Exception ex) { dragError = ex; }
                            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                        }) { IsBackground = true };
                        try
                        {
                            SetCursorPos(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); move.Start();
                            var wait = Stopwatch.StartNew(); while (wait.ElapsedMilliseconds < 700) { Application.DoEvents(); Thread.Sleep(10); }
                            Require(move.Join(1000), "thumb drag did not finish"); if (dragError != null) throw dragError;
                            Require(Position(list, true) > 0, "native pointer drag did not move the thumb");
                        }
                        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(previousCursor.X, previousCursor.Y); }
                    });
                    check("native checkbox retains keyboard toggling, focus and accessible state", delegate
                    {
                        Key(checkbox, Keys.Space); Require(checkbox.Checked && checkbox.Focused, "Space did not toggle/focus checkbox");
                        Require((checkbox.AccessibilityObject.State & AccessibleStates.Checked) != 0, "checked accessibility state missing");
                        Key(checkbox, Keys.Space); Require(!checkbox.Checked, "second Space did not toggle off");
                    });
                    check("autosized checkbox keeps the complete label within native preferred bounds", delegate
                    {
                        using (var sized = new CheckBox { AutoSize = true, Text = "Deep search", Font = Palette.Small, BackColor = Palette.Surface, ForeColor = Palette.Text, Location = new Point(16, 440) })
                        {
                            form.Controls.Add(sized);
                            foreach (Font font in new[] { Palette.Small, Palette.Normal })
                            foreach (RightToLeft direction in new[] { RightToLeft.No, RightToLeft.Yes })
                            {
                                sized.Font = font; sized.RightToLeft = direction; sized.Size = sized.PreferredSize; Application.DoEvents();
                                Rectangle mark, label; LinuxControls.CheckBoxLayout(sized, out mark, out label);
                                Size measured = TextRenderer.MeasureText(sized.Text, sized.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                                Require(label.Width >= measured.Width, "native preferred width clips the label: " + label.Width + " < " + measured.Width);
                                Require(sized.ClientRectangle.Contains(mark) && sized.ClientRectangle.Contains(label) && !mark.IntersectsWith(label), "checkbox label overlaps its mark or bounds");
                            }
                            form.Controls.Remove(sized);
                        }
                    });
                    check("resize updates native scrollbar geometry without losing content", delegate
                    {
                        panel.Height = 200; panel.Width = 190; Application.DoEvents(); Scroll(panel, 7, true);
                        Require(-panel.AutoScrollPosition.Y >= content.Height - panel.ClientSize.Height - 1, "panel bottom became unreachable"); DarkTrack(panel.Handle, true);
                        panel.Height = 350; Application.DoEvents(); Scroll(panel, 6, true); Require(panel.AutoScrollPosition.Y == 0, "panel top became unreachable after resize");
                    });
                    check("dropdown scrolling keeps owner-drawn items and dark native chrome", delegate
                    {
                        combo.DroppedDown = true; Application.DoEvents();
                        try { var info = new ComboInfo { Size = Marshal.SizeOf(typeof(ComboInfo)) }; Require(GetComboBoxInfo(combo.Handle, ref info), "dropdown window missing"); DarkTrack(info.List, true); }
                        finally { combo.DroppedDown = false; }
                        Key(combo, Keys.End); Require(combo.SelectedIndex == 79, "dropdown keyboard selection failed");
                    });
                    check("dynamic controls receive the Linux fallback", delegate
                    {
                        using (var extra = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Bounds = new Rectangle(506, 390, 252, 80), BackColor = Palette.Raised, ForeColor = Palette.Text, Text = String.Join("\r\n", Enumerable.Range(0, 20).Select(i => "Dynamic " + i)) })
                        { form.Controls.Add(extra); extra.Refresh(); Application.DoEvents(); DarkTrack(extra.Handle, true); form.Controls.Remove(extra); }
                    });
                    check("handle recreation, non-client hover and focus changes keep the fallback", delegate
                    {
                        typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(list, null);
                        Application.DoEvents(); Require(list.Items.Count == 80, "handle recreation lost rows");
                        SendMessage(list.Handle, 0xA0, new IntPtr(7), IntPtr.Zero); Application.DoEvents(); DarkTrack(list.Handle, true);
                        SendMessage(list.Handle, 0x2A2, IntPtr.Zero, IntPtr.Zero); checkbox.Focus(); Application.DoEvents();
                        text.Focus(); Application.DoEvents(); Require(text.Focused, "keyboard focus lost"); DarkTrack(text.Handle, true); DarkTrack(list.Handle, true);
                    });
                    if (!actualLinux)
                        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "linux-controls.png")); }
                    form.Close(); Application.DoEvents();
                }
            }
            finally { LinuxPlatform.TestLinux = previous; }
            report.Add((report.Count - failures) + " passed, " + failures + " failed"); File.WriteAllLines(Path.Combine(root, "linux-controls.txt"), report);
            Console.WriteLine(report[report.Count - 1]); return failures == 0 ? 0 : 1;
        }
    }
}
