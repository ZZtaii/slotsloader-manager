using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SlotsInstaller
{
    // 2.0.3: every confirmation and error box. A drop-in for MessageBox.Show(owner, text, caption, buttons,
    // icon[, defaultButton]) that follows the Windows app theme (Settings > Personalisation > Colours >
    // "Choose your default app mode"): dark when AppsUseLightTheme = 0, light otherwise, re-read at each
    // show (light if unreadable). Buttons, order, default, Enter/Escape/close-box, Alt mnemonics, the icon
    // kind, the system sound and Ctrl+C copy behave like the Windows message box.
    // 2.1.0: Ask() adds custom button labels and an optional check box (the loose-loader question).
    internal static class ThemedMessageBox
    {
        // Tests force a theme here instead of writing the registry; null = follow Windows.
        internal static bool? ForcedDark;
        // Tests answer dialogs here instead of showing them (the form is built but not shown); null = show.
        internal static Func<ThemedMessageForm, DialogResult> TestResponder;

        internal static bool SystemUsesDarkApps()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        internal static bool Dark { get { return ForcedDark ?? SystemUsesDarkApps(); } }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            using (var dialog = new ThemedMessageForm(text, caption, buttons, icon, defaultButton, Dark, owner, 0))
                return Run(dialog, owner);
        }

        // Custom buttons (left to right) and an optional check box. Returns the clicked button's index, or
        // -1 for Escape / the close box. `isChecked` is the check box state when the dialog closed.
        internal static int Ask(IWin32Window owner, string text, string caption, MessageBoxIcon icon, string[] buttons, int defaultIndex, string checkBox, out bool isChecked)
        {
            using (var dialog = new ThemedMessageForm(text, caption, buttons, defaultIndex, checkBox, icon, Dark, owner, 0))
            {
                DialogResult result = Run(dialog, owner);
                isChecked = dialog.OptionBox != null && dialog.OptionBox.Checked;
                Button clicked = dialog.DialogButtons.FirstOrDefault(b => b.DialogResult == result && result != DialogResult.Cancel);
                return clicked == null ? -1 : dialog.DialogButtons.IndexOf(clicked);
            }
        }

        // Checks (--dialog-check): called once the dialog is shown, on the UI thread.
        internal static Action<ThemedMessageForm> ShownHook;

        private static DialogResult Run(ThemedMessageForm dialog, IWin32Window owner)
        {
            Func<ThemedMessageForm, DialogResult> responder = TestResponder;
            if (responder != null) return responder(dialog);
            return owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        }
    }

    // 2.2.0: what a shown dialog actually rendered with (first paint of its message), logged to Data\log.txt
    // once per show so a report of a wrongly sized dialog can be diagnosed from the log.
    internal sealed class DialogPaintMetrics
    {
        internal int DeviceDpi, LayoutDpi, LabelDeviceDpi, HfontHeight, FontHeight, MeasuredLineHeight, ClientWidth, ClientHeight, LabelHeight, PanelHeight;
        internal float FontSize, FontPoints, GraphicsDpi;
        internal GraphicsUnit Unit;
        internal bool Scrolls, ButtonsVisible, FontDisposed;
        internal string Caption = "";
        internal string Line
        {
            get
            {
                return "Dialog '" + Caption + "': DeviceDpi " + DeviceDpi + ", LayoutDpi " + LayoutDpi + ", label DeviceDpi " + LabelDeviceDpi + ", Graphics DPI " + GraphicsDpi.ToString("0") +
                    ", font " + FontSize.ToString("0.##") + " " + Unit + " (" + FontPoints.ToString("0.##") + " pt, Height " + FontHeight + " px), HFONT height " + HfontHeight + " px, line " + MeasuredLineHeight + " px" +
                    ", client " + ClientWidth + "x" + ClientHeight + ", message " + LabelHeight + "/" + PanelHeight + " px" + (Scrolls ? " (scrolls)" : "") + (ButtonsVisible ? "" : ", buttons clipped") + (FontDisposed ? ", font unreadable by GDI+" : "");
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private sealed class LogFont
        {
            public int Height, Width, Escapement, Orientation, Weight;
            public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
        }
        [DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetObject(IntPtr handle, int size, [Out] LogFont font);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        // The font height (pixels) of the HFONT the window really holds (WM_GETFONT), 0 when unknown.
        internal static int ReadHfontHeight(Control control)
        {
            try
            {
                if (control == null || !control.IsHandleCreated) return 0;
                IntPtr font = SendMessage(control.Handle, 0x31, IntPtr.Zero, IntPtr.Zero);
                if (font == IntPtr.Zero) return 0;
                var lf = new LogFont();
                if (GetObject(font, Marshal.SizeOf(typeof(LogFont)), lf) == 0) return 0;
                return Math.Abs(lf.Height);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return 0; }
        }

        internal static DialogPaintMetrics Capture(Control label, Graphics graphics)
        {
            var m = new DialogPaintMetrics();
            Font f = label.Font;
            m.LabelDeviceDpi = label.DeviceDpi; m.GraphicsDpi = graphics.DpiY;
            m.FontSize = f.Size; m.Unit = f.Unit;
            try { m.FontPoints = f.SizeInPoints; m.FontHeight = f.Height; }
            catch (ArgumentException) { m.FontDisposed = true; }   // GDI+ "Parameter is not valid" (seen with 2.1.0's pixel font)
            m.HfontHeight = ReadHfontHeight(label);
            try { m.MeasuredLineHeight = TextRenderer.MeasureText(graphics, "Ag", f, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding).Height; }
            catch (ArgumentException) { m.FontDisposed = true; }
            m.LabelHeight = label.Height;
            return m;
        }
    }

    internal sealed class ThemedMessageForm : Form
    {
        [DllImport("comctl32.dll", CharSet = CharSet.Unicode)] private static extern int LoadIconWithScaleDown(IntPtr instance, IntPtr name, int cx, int cy, out IntPtr icon);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

        // Results given to custom buttons, left to right (Cancel is kept for Escape / the close box).
        private static readonly DialogResult[] CustomResults = { DialogResult.Yes, DialogResult.No, DialogResult.Retry, DialogResult.Ignore, DialogResult.Abort, DialogResult.OK };

        internal readonly string MessageText, Caption;
        internal readonly MessageBoxIcon IconKind;
        internal readonly bool IsDark;
        internal readonly List<Button> DialogButtons = new List<Button>();
        internal readonly Label MessageLabel;
        internal readonly Panel MessagePanel, ButtonStrip;
        internal readonly PictureBox IconBox;
        internal readonly CheckBox OptionBox;            // null unless a check box was asked for
        internal int LayoutDpi { get; private set; }
        internal int LayoutCount { get; private set; }   // tests: layouts applied (construction, load, DPI changes)
        private readonly DialogResult escapeResult;
        private readonly bool forcedDpi;
        private readonly Control ownerControl;
        private Bitmap iconImage;

        internal ThemedMessageForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, bool dark, IWin32Window owner, int dpi)
            : this(text, caption, Results(buttons).Select(r => new KeyValuePair<string, DialogResult>(ButtonText(r), r)).ToList(),
                   defaultButton == MessageBoxDefaultButton.Button3 ? 2 : defaultButton == MessageBoxDefaultButton.Button2 ? 1 : 0, CancelResult(buttons), null, icon, dark, owner, dpi) { }

        internal ThemedMessageForm(string text, string caption, string[] buttons, int defaultIndex, string checkBox, MessageBoxIcon icon, bool dark, IWin32Window owner, int dpi)
            : this(text, caption, buttons.Select((b, i) => new KeyValuePair<string, DialogResult>(b, CustomResults[i])).ToList(), defaultIndex, DialogResult.Cancel, checkBox, icon, dark, owner, dpi) { }

        private ThemedMessageForm(string text, string caption, List<KeyValuePair<string, DialogResult>> buttons, int defaultIndex, DialogResult escape, string checkBox, MessageBoxIcon icon, bool dark, IWin32Window owner, int dpi)
        {
            MessageText = (text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine); Caption = caption ?? ""; IconKind = icon; IsDark = dark; escapeResult = escape;
            ownerControl = owner as Control ?? (owner == null ? null : Control.FromHandle(owner.Handle));
            SuspendLayout();
            // 2.2.0: built like the app's other dialogs (report, Recovery, presets): 96-DPI logical units,
            // AutoScaleMode.Dpi and the Palette font in POINTS, which every control inherits. The text is drawn by
            // the window's own DC at the monitor DPI, like the main window. (2.0.3-2.1.0 used a pixel font: Windows
            // Forms turns it into points through the process's screen DC (96 DPI when Windows started at 100 %)
            // and draws those points at the monitor DPI, so a 288-DPI monitor showed the text 3x too large in a box
            // measured for the normal size.) Once the handle exists on its monitor the layout is computed again in
            // device pixels from that DPI so the wrapping is exact; DPI changes re-wrap the same way.
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Font = Palette.Normal;
            Text = Caption; AccessibleName = Caption; AccessibleRole = AccessibleRole.Dialog; AccessibleDescription = MessageText;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowIcon = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; KeyPreview = true;
            BackColor = dark ? Palette.Surface : SystemColors.Window; ForeColor = dark ? Palette.Text : SystemColors.WindowText;
            // Like the Windows message box: Yes/No and Abort/Retry/Ignore have no close box and ignore Escape.
            ControlBox = escape != DialogResult.None;
            if (dark) WindowChrome.Apply(this);

            MessagePanel = new Panel { BackColor = BackColor, AutoScroll = true, Margin = Padding.Empty };
            MessageLabel = new Label { UseMnemonic = false, AutoSize = false, Text = MessageText, ForeColor = ForeColor, BackColor = BackColor, AccessibleName = MessageText, AccessibleRole = AccessibleRole.StaticText, TabStop = false };
            MessagePanel.Controls.Add(MessageLabel);
            if (dark) SlotsInstaller.DarkTheme.Apply(MessagePanel);   // dark scroll bar when a long message scrolls
            IconBox = new PictureBox { SizeMode = PictureBoxSizeMode.Normal, BackColor = BackColor, Visible = icon != MessageBoxIcon.None, AccessibleName = IconName(icon), AccessibleRole = AccessibleRole.Graphic, TabStop = false };
            ButtonStrip = new Panel { BackColor = dark ? Palette.Background : SystemColors.Control, Margin = Padding.Empty };
            foreach (var spec in buttons)
            {
                var button = new Button { Text = spec.Key, DialogResult = spec.Value, UseMnemonic = true, AccessibleName = spec.Key.Replace("&", ""), UseVisualStyleBackColor = !dark };
                if (dark)
                {
                    button.FlatStyle = FlatStyle.Flat; button.BackColor = Palette.Raised; button.ForeColor = Palette.Text;
                    button.FlatAppearance.BorderColor = Palette.Border; button.FlatAppearance.MouseOverBackColor = Palette.Selected; button.FlatAppearance.MouseDownBackColor = Palette.Border;
                }
                else button.FlatStyle = FlatStyle.System;
                DialogButtons.Add(button); ButtonStrip.Controls.Add(button);
            }
            if (!String.IsNullOrEmpty(checkBox))
            {
                OptionBox = new CheckBox { Text = checkBox, UseMnemonic = true, AutoSize = false, BackColor = ButtonStrip.BackColor, ForeColor = dark ? Palette.Text : SystemColors.ControlText, AccessibleName = checkBox.Replace("&", ""), TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft, FlatStyle = dark ? FlatStyle.Flat : FlatStyle.System };
                if (dark) { OptionBox.FlatAppearance.BorderColor = Palette.Border; OptionBox.FlatAppearance.CheckedBackColor = Palette.Raised; }
                ButtonStrip.Controls.Add(OptionBox);
            }
            int index = defaultIndex < 0 || defaultIndex >= DialogButtons.Count ? 0 : defaultIndex;
            AcceptButton = DialogButtons[index];
            if (dark) DialogButtons[index].FlatAppearance.BorderColor = Palette.Accent;
            Button cancelButton = DialogButtons.FirstOrDefault(b => b.DialogResult == escape);
            if (cancelButton != null) CancelButton = cancelButton;
            Controls.Add(MessagePanel); Controls.Add(IconBox); Controls.Add(ButtonStrip);
            ActiveControl = DialogButtons[index];
            for (int i = 0; i < DialogButtons.Count; i++) DialogButtons[i].TabIndex = i;
            if (OptionBox != null) OptionBox.TabIndex = DialogButtons.Count;
            // Native (light theme) buttons and the check box get an HFONT for their monitor DPI, like the main
            // window's native fields (NativeDpiFont); owner-drawn dark controls paint with the inherited font.
            if (!dark) { foreach (Button b in DialogButtons) NativeDpiFont.Attach(b); if (OptionBox != null) NativeDpiFont.Attach(OptionBox); }
            MessageLabel.Paint += RecordFirstPaint;
            forcedDpi = dpi > 0;
            ApplyLayout(96);                         // logical (96-DPI) units, scaled by the framework
            ResumeLayout(true); PerformAutoScale();
            // Device pixels for the DPI the form has now (tests: the forced DPI); laid out again on its monitor in OnLoad.
            ApplyLayout(forcedDpi ? dpi : DeviceDpi);
        }

        internal static DialogResult[] Results(MessageBoxButtons buttons)
        {
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel: return new[] { DialogResult.OK, DialogResult.Cancel };
                case MessageBoxButtons.AbortRetryIgnore: return new[] { DialogResult.Abort, DialogResult.Retry, DialogResult.Ignore };
                case MessageBoxButtons.YesNoCancel: return new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel };
                case MessageBoxButtons.YesNo: return new[] { DialogResult.Yes, DialogResult.No };
                case MessageBoxButtons.RetryCancel: return new[] { DialogResult.Retry, DialogResult.Cancel };
                default: return new[] { DialogResult.OK };
            }
        }
        // Escape and the close box: Cancel when there is one, OK for an OK-only box, otherwise nothing (Windows rules).
        internal static DialogResult CancelResult(MessageBoxButtons buttons)
        {
            if (buttons == MessageBoxButtons.OK) return DialogResult.OK;
            return Results(buttons).Contains(DialogResult.Cancel) ? DialogResult.Cancel : DialogResult.None;
        }
        internal static string ButtonText(DialogResult result)
        {
            switch (result)
            {
                case DialogResult.Yes: return "&Yes";
                case DialogResult.No: return "&No";
                case DialogResult.Abort: return "&Abort";
                case DialogResult.Retry: return "&Retry";
                case DialogResult.Ignore: return "&Ignore";
                case DialogResult.Cancel: return "Cancel";
                default: return "OK";
            }
        }
        private static string IconName(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Question: return "Question";
                case MessageBoxIcon.Warning: return "Warning";
                case MessageBoxIcon.Error: return "Error";
                case MessageBoxIcon.Information: return "Information";
                default: return "";
            }
        }
        private static int SystemDpi() { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return (int)Math.Round(g.DpiX); }
        private static int StockIconId(MessageBoxIcon icon)
        {
            // IDI_HAND, IDI_QUESTION, IDI_EXCLAMATION, IDI_ASTERISK (Error=Hand=Stop, Warning=Exclamation, Information=Asterisk).
            switch (icon) { case MessageBoxIcon.Error: return 32513; case MessageBoxIcon.Question: return 32514; case MessageBoxIcon.Warning: return 32515; case MessageBoxIcon.Information: return 32516; default: return 0; }
        }
        private static Bitmap IconBitmap(MessageBoxIcon icon, int size)
        {
            int id = StockIconId(icon); if (id == 0) return null;
            try
            {
                IntPtr handle;
                if (LoadIconWithScaleDown(IntPtr.Zero, (IntPtr)id, size, size, out handle) == 0 && handle != IntPtr.Zero)
                {
                    try { using (Icon loaded = Icon.FromHandle(handle)) return loaded.ToBitmap(); }
                    finally { DestroyIcon(handle); }
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Icon stock = icon == MessageBoxIcon.Error ? SystemIcons.Error : icon == MessageBoxIcon.Question ? SystemIcons.Question : icon == MessageBoxIcon.Warning ? SystemIcons.Warning : SystemIcons.Information;
            var bitmap = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bitmap)) g.DrawIcon(stock, new Rectangle(0, 0, size, size));
            return bitmap;
        }

        private int S(int logical) { return (int)Math.Round(logical * LayoutDpi / 96.0); }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        // The DPI GDI (TextRenderer) measures and draws point fonts at in this process. GDI+ (Graphics.DpiY)
        // can report 96 while GDI uses the monitor/system DPI (2.2.0 finding on a 288-DPI monitor).
        internal static int GdiDpi()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            try { int dpi = dc == IntPtr.Zero ? 0 : GetDeviceCaps(dc, 90); return dpi > 0 ? dpi : 96; }   // LOGPIXELSY
            finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
        }

        // Text measured with the font the controls really draw with (the inherited point font), at GDI's DPI,
        // converted to layout pixels for LayoutDpi (exact when they are the same, as for a shown dialog).
        private int measureDpi = 96;
        internal Size Measure(string text, int width, TextFormatFlags flags)
        {
            double k = LayoutDpi / (double)measureDpi;
            Size proposed = width == Int32.MaxValue ? new Size(Int32.MaxValue, Int32.MaxValue) : new Size(Math.Max(1, (int)Math.Floor(width / k)), Int32.MaxValue);
            Size m = TextRenderer.MeasureText(String.IsNullOrEmpty(text) ? " " : text, MessageLabel.Font, proposed, flags);
            return k == 1 ? m : new Size((int)Math.Ceiling(m.Width * k), (int)Math.Ceiling(m.Height * k));
        }

        // Sizes everything for `dpi` (96 = logical units before the handle exists; the device DPI afterwards):
        // text wraps at a readable width, the box fits its content and the working area of the owner's screen
        // (long text scrolls), buttons keep the Windows minimum size. The controls keep the inherited point font.
        internal void ApplyLayout(int dpi)
        {
            LayoutDpi = Math.Max(96, dpi); LayoutCount++;
            measureDpi = GdiDpi();
            Rectangle work = WorkingArea();
            if (!IsHandleCreated && !forcedDpi && ownerControl != null && ownerControl.IsHandleCreated && ownerControl.DeviceDpi > 96)
            {
                // Logical pre-layout: the owner's working area in 96-DPI units (the final layout uses device pixels).
                int screenDpi = ownerControl.DeviceDpi;
                work = new Rectangle(work.X * 96 / screenDpi, work.Y * 96 / screenDpi, work.Width * 96 / screenDpi, work.Height * 96 / screenDpi);
            }
            int margin = S(24), gap = S(14), iconSize = IconKind == MessageBoxIcon.None ? 0 : S(32);
            if (iconImage != null) { IconBox.Image = null; iconImage.Dispose(); iconImage = null; }
            if (iconSize > 0) { iconImage = IconBitmap(IconKind, iconSize); IconBox.Image = iconImage; }
            int buttonHeight = Math.Max(S(26), Measure("Ag", Int32.MaxValue, TextFormatFlags.Default).Height + S(10));
            var widths = DialogButtons.Select(b => Math.Max(S(88), Measure(b.Text.Replace("&", ""), Int32.MaxValue, TextFormatFlags.Default).Width + S(24))).ToList();
            int buttonsWidth = widths.Sum() + S(8) * Math.Max(0, widths.Count - 1);
            int optionWidth = OptionBox == null ? 0 : Measure(OptionBox.Text.Replace("&", ""), Int32.MaxValue, TextFormatFlags.Default).Width + S(16) + S(16);
            int stripHeight = buttonHeight + S(12) * 2;
            int chrome = Width - ClientSize.Width;   // frame, kept on screen too
            int maxText = Math.Max(S(200), Math.Min(S(520), work.Width - chrome - margin * 2 - (iconSize > 0 ? iconSize + gap : 0) - S(16)));
            const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
            Size text = Measure(MessageText, maxText, flags);
            int textWidth = Math.Min(maxText, Math.Max(text.Width, S(120)));
            int contentWidth = margin * 2 + (iconSize > 0 ? iconSize + gap : 0) + textWidth;
            int clientWidth = Math.Max(contentWidth, buttonsWidth + S(12) * 2 + S(24) + (optionWidth > 0 ? optionWidth + S(12) : 0));
            textWidth = clientWidth - margin * 2 - (iconSize > 0 ? iconSize + gap : 0);
            text = Measure(MessageText, textWidth, flags);
            int captionHeight = Height - ClientSize.Height;
            int maxMessage = Math.Max(S(60), work.Height - captionHeight - stripHeight - margin * 2 - S(16));
            int messageHeight = Math.Min(text.Height, maxMessage);
            bool scrolls = text.Height > messageHeight;
            int bodyHeight = Math.Max(messageHeight, iconSize);
            ClientSize = new Size(clientWidth, margin * 2 + bodyHeight + stripHeight);
            IconBox.Bounds = new Rectangle(margin, margin, iconSize, iconSize);
            int textLeft = margin + (iconSize > 0 ? iconSize + gap : 0);
            int scrollbar = scrolls ? SystemInformation.GetVerticalScrollBarWidthForDpi(LayoutDpi) : 0;
            MessagePanel.Bounds = new Rectangle(textLeft, margin + Math.Max(0, (bodyHeight - messageHeight) / 2), textWidth, messageHeight);
            if (scrolls) text = Measure(MessageText, textWidth - scrollbar, flags);
            MessageLabel.Bounds = new Rectangle(0, 0, textWidth - scrollbar, text.Height);
            ButtonStrip.Bounds = new Rectangle(0, ClientSize.Height - stripHeight, ClientSize.Width, stripHeight);
            int x = ClientSize.Width - S(12) - buttonsWidth;
            for (int i = 0; i < DialogButtons.Count; i++) { DialogButtons[i].Bounds = new Rectangle(x, S(12), widths[i], buttonHeight); x += widths[i] + S(8); }
            if (OptionBox != null) OptionBox.Bounds = new Rectangle(S(16), S(12), Math.Min(optionWidth, ClientSize.Width - S(12) - buttonsWidth - S(28)), buttonHeight);
            Location = CenteredLocation(work);
        }

        private Rectangle OwnerBounds()
        {
            Form ownerForm = ownerControl == null ? null : ownerControl.FindForm() ?? ownerControl as Form;
            if (ownerForm != null && ownerForm.Visible && ownerForm.WindowState != FormWindowState.Minimized) return ownerForm.Bounds;
            return Rectangle.Empty;
        }
        private Rectangle WorkingArea()
        {
            Rectangle owner = OwnerBounds();
            return owner.IsEmpty ? Screen.FromPoint(Cursor.Position).WorkingArea : Screen.FromRectangle(owner).WorkingArea;
        }
        // Centred on the owner (or the screen), never off the working area.
        internal Point CenteredLocation(Rectangle work)
        {
            Rectangle owner = OwnerBounds(); if (owner.IsEmpty) owner = work;
            int x = owner.Left + (owner.Width - Width) / 2, y = owner.Top + (owner.Height - Height) / 2;
            x = Math.Max(work.Left, Math.Min(x, work.Right - Width)); y = Math.Max(work.Top, Math.Min(y, work.Bottom - Height));
            return new Point(x, y);
        }

        // The Windows message box's Ctrl+C text.
        internal string CopyText
        {
            get
            {
                const string rule = "---------------------------";
                var sb = new StringBuilder();
                sb.Append(rule).Append("\r\n").Append(Caption).Append("\r\n").Append(rule).Append("\r\n").Append(MessageText.Replace(Environment.NewLine, "\r\n")).Append("\r\n").Append(rule).Append("\r\n");
                foreach (Button b in DialogButtons) sb.Append(b.Text.Replace("&", "")).Append("   ");
                sb.Append("\r\n").Append(rule).Append("\r\n");
                return sb.ToString();
            }
        }

        // Escape without a Cancel button (custom buttons): the escape answer, e.g. Cancel for Ask().
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape && CancelButton == null && escapeResult != DialogResult.None) { DialogResult = escapeResult; return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert))
            {
                try { Clipboard.SetText(CopyText); } catch (ExternalException) { }
                e.Handled = true; e.SuppressKeyPress = true; return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // The handle now exists on its monitor: lay out again in device pixels for that DPI (exact
            // wrapping; the point font is untouched). Tests that force a DPI keep it.
            // Every control draws with the app's point font: anything that replaced it while the handle was
            // created goes back to the inherited Palette.Normal.
            if (!Font.Equals(Palette.Normal)) Font = Palette.Normal;
            foreach (Control c in new Control[] { MessagePanel, MessageLabel, IconBox, ButtonStrip, OptionBox }.Concat(DialogButtons))
                if (c != null && !c.Font.Equals(Font)) c.ResetFont();
            if (!forcedDpi || DeviceDpi != LayoutDpi) ApplyLayout(DeviceDpi);
        }
        // First paint of the message: the metrics really used on screen (one log line per show).
        internal DialogPaintMetrics PaintMetrics { get; private set; }
        private void RecordFirstPaint(object sender, PaintEventArgs e)
        {
            if (PaintMetrics != null) return;
            try
            {
                DialogPaintMetrics m = DialogPaintMetrics.Capture(MessageLabel, e.Graphics);
                m.Caption = Caption; m.DeviceDpi = DeviceDpi; m.LayoutDpi = LayoutDpi;
                m.ClientWidth = ClientSize.Width; m.ClientHeight = ClientSize.Height; m.PanelHeight = MessagePanel.ClientSize.Height;
                m.Scrolls = MessagePanel.VerticalScroll.Visible || MessageLabel.Height > MessagePanel.ClientSize.Height;
                m.ButtonsVisible = DialogButtons.All(b => ButtonStrip.ClientRectangle.Contains(b.Bounds) && ClientRectangle.Contains(new Rectangle(ButtonStrip.Left + b.Left, ButtonStrip.Top + b.Top, b.Width, b.Height)));
                PaintMetrics = m;
                Program.LogLine(m.Line);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Action<ThemedMessageForm> hook = ThemedMessageBox.ShownHook;
            if (hook != null) hook(this);
            if (Program.Headless) return;
            if (IconKind == MessageBoxIcon.Error) SystemSounds.Hand.Play();
            else if (IconKind == MessageBoxIcon.Warning) SystemSounds.Exclamation.Play();
            else if (IconKind == MessageBoxIcon.Information) SystemSounds.Asterisk.Play();
            else if (IconKind == MessageBoxIcon.Question) SystemSounds.Question.Play();
            else SystemSounds.Beep.Play();
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // The framework scaled the bounds; wrap again for the new DPI once it has finished.
            int dpi = e.DeviceDpiNew;
            BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) ApplyLayout(dpi); });
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The close box/Alt+F4 answer like Escape: OK for an OK box, refused when there is no Cancel.
            if (e.CloseReason == CloseReason.UserClosing && DialogResult == DialogResult.Cancel && !DialogButtons.Any(b => b.DialogResult == DialogResult.Cancel))
            {
                if (escapeResult == DialogResult.None) { e.Cancel = true; DialogResult = DialogResult.None; } else DialogResult = escapeResult;
            }
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (iconImage != null) iconImage.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
