using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 13 Nov 2026 (the user, about the in-game switches on the former Updates tab: "repurpose this section into slots
    // loader ... and we could also possibly add an image preview section for each option. To help users learn what is
    // what. For now, you could use placeholder images"): Settings > Slotsloader. What a Slotsloader of the suite line may
    // add to the game's menus and the outfit other players see (LoaderFlags.cs) stand here in one column; beside them a
    // picture of the option the pointer or the keyboard is on, with its name and what it does. More loader settings go
    // here later. The update settings stand above them (MainFormUpdates.AddUpdateSettings): there is no Updates tab.
    //
    // Pictures: assets\loader-previews\<id>.png (LoaderMenu ids, and "others-see"), embedded by build.ps1 as
    // SlotsInstaller.LoaderPreview.<id>.png. An option without a picture gets a drawn placeholder, so a picture can be
    // added later by dropping its file in that folder.
    internal sealed partial class MainForm
    {
        private FlatButton settingsLoaderTab;
        private Panel settingsLoaderPanel;
        private LoaderPreviewPanel settingsLoaderPreview;

        private Panel BuildLoaderSettings()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(0, 8, 0, 0), Name = "settingsLoaderPanel" };
            // Upper section: the update settings (the user, 13 Nov 2026: no Updates tab of their own; above the Slotsloader
            // options, which then do not start right under the tab). Lower section: the options and their picture.
            Action<Control> addRow = delegate(Control c) { c.Dock = DockStyle.Top; panel.Controls.Add(c); c.BringToFront(); };
            AddUpdateSettings(addRow);
            addRow(Rhythm.Gap(Rhythm.Group));
            var lower = new Panel { Dock = DockStyle.Fill, Name = "settingsLoaderLower" };
            var column = new Panel { Dock = DockStyle.Left, Width = 520, Name = "settingsLoaderColumn" };
            Action<Control> add = delegate(Control c) { c.Dock = DockStyle.Top; column.Controls.Add(c); c.BringToFront(); };
            // Off unless ticked (the user: no added menus in the game unless asked for). The update check inside the game
            // is no option: the user did not want it even as a choice, the Manager always keeps it off.
            Label inGame = Caption("ADDED TO THE GAME'S MENUS"); inGame.Name = "settingsGameCaption"; add(inGame);
            // One box per addition (the user: "split in different options so i can pick and choose"), in two columns: with
            // the update settings above them, one column would not fit the smallest window.
            int menuRows = (LoaderMenu.All.Length + 1) / 2;
            var grid = new TableLayoutPanel { Height = 30 * menuRows, ColumnCount = 2, RowCount = menuRows, Margin = Padding.Empty, Padding = Padding.Empty, Name = "settingsGameMenusGrid" };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int r = 0; r < menuRows; r++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / menuRows));
            settingsGameMenus = new CheckBox[LoaderMenu.All.Length];
            for (int i = 0; i < settingsGameMenus.Length; i++)
            {
                LoaderMenu menu = LoaderMenu.All[i];
                var box = new CheckBox { Text = menu.Label, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0), Name = "settingsGameMenu_" + menu.Id, AccessibleName = "Show in the game: " + menu.Label, AccessibleDescription = menu.About };
                box.CheckedChanged += delegate { SaveLoaderFlags(menu); };
                box.MouseEnter += delegate { ShowLoaderPreview(menu.Id); };
                box.Enter += delegate { ShowLoaderPreview(menu.Id); };
                settingsGameMenus[i] = box; grid.Controls.Add(box, i % 2, i / 2);
            }
            add(grid);
            add(Rhythm.Gap(Rhythm.Group));
            // The loader's own game setting "Outfit Others See", set here so its row need not be added to the game's menus.
            // The change is written to the game folder once (LoaderFlags.WriteOthersSee).
            Label othersCaption = Caption("OTHER PLAYERS SEE YOUR EXTRA OUTFITS AS"); othersCaption.Name = "settingsOthersSeeCaption"; add(othersCaption);
            var others = new TableLayoutPanel { Height = 30, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty, Name = "settingsOthersSeeRow" };
            others.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); others.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            others.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            string[] seen = { LoaderFlags.OthersSeeOutfit1, LoaderFlags.OthersSeeDriveTech };
            settingsOthersSee = new RadioButton[seen.Length];
            for (int i = 0; i < seen.Length; i++)
            {
                string value = seen[i];
                var radio = new RadioButton { Text = value, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0), Name = "settingsOthersSee_" + i, AccessibleName = "Other players see your extra outfits as " + value, AccessibleDescription = LoaderMenu.OthersSeeAbout };
                radio.CheckedChanged += delegate { if (radio.Checked) SaveOthersSee(value); };
                radio.MouseEnter += delegate { ShowLoaderPreview(LoaderMenu.OthersSeeId); };
                radio.Enter += delegate { ShowLoaderPreview(LoaderMenu.OthersSeeId); };
                settingsOthersSee[i] = radio; others.Controls.Add(radio, i, 0);
            }
            add(others); add(Rhythm.Gap(Rhythm.Related));
            settingsGameNote = new NoteLabel { Height = 26, ForeColor = Palette.Muted, Font = Palette.Small, Name = "settingsGameNote" };
            add(settingsGameNote);
            settingsLoaderPreview = new LoaderPreviewPanel { Dock = DockStyle.Fill, Name = "settingsLoaderPreview" };
            lower.Controls.Add(column); lower.Controls.Add(settingsLoaderPreview); settingsLoaderPreview.BringToFront();
            panel.Controls.Add(lower); lower.BringToFront();
            ShowLoaderPreview(LoaderMenu.All[0].Id);
            return panel;
        }

        // The picture and words of one option: a LoaderMenu id, or LoaderMenu.OthersSeeId.
        private void ShowLoaderPreview(string id)
        {
            if (settingsLoaderPreview == null) return;
            LoaderMenu menu = LoaderMenu.ById(id);
            if (menu != null) settingsLoaderPreview.Show(menu.Id, menu.Label, menu.About);
            else if (id == LoaderMenu.OthersSeeId) settingsLoaderPreview.Show(id, LoaderMenu.OthersSeeLabel, LoaderMenu.OthersSeeAbout);
        }

        private void ShowLoaderTab()
        {
            foreach (Control c in settingsGeneralParts) c.Visible = false;
            if (settingsSubtitle != null) settingsSubtitle.Visible = false;
            settingsReplacePanel.Visible = false;
            HideAboutTab();
            settingsLoaderPanel.Visible = true; settingsLoaderPanel.BringToFront();
            settingsGeneralTab.SetActive(false); settingsReplaceTab.SetActive(false); settingsLoaderTab.SetActive(true);
            LoadUpdateFields();
            LoadLoaderFlagFields();
        }
        private void HideLoaderTab() { if (settingsLoaderPanel != null) { settingsLoaderPanel.Visible = false; settingsLoaderTab.SetActive(false); } }
    }

    // One option's picture (16:9, at most 480 wide, smaller in a narrow window), its name and what it does. No picture
    // file yet: a drawn placeholder. Lighter surfaces only (Raised on the page's Surface).
    internal sealed class LoaderPreviewPanel : Control
    {
        private readonly Dictionary<string, Image> pictures = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        internal string OptionId { get; private set; }
        internal string Title { get; private set; }
        internal string About { get; private set; }
        internal bool HasPicture { get { return OptionId != null && Picture(OptionId) != null; } }

        internal LoaderPreviewPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Palette.Surface; OptionId = Title = About = "";
        }

        internal void Show(string id, string title, string about)
        {
            if (OptionId == id && Title == title && About == about) return;
            OptionId = id; Title = title ?? ""; About = about ?? "";
            AccessibleName = Title; AccessibleDescription = About;
            Invalidate();
        }

        internal static string ResourceName(string id) { return "SlotsInstaller.LoaderPreview." + id + ".png"; }
        private Image Picture(string id)
        {
            Image image;
            if (pictures.TryGetValue(id, out image)) return image;
            try
            {
                using (Stream stream = typeof(LoaderPreviewPanel).Assembly.GetManifestResourceStream(ResourceName(id)))
                    if (stream != null) using (Image decoded = Image.FromStream(stream)) image = new Bitmap(decoded);
            }
            catch (ArgumentException) { image = null; }   // not a picture: the placeholder
            pictures[id] = image;
            return image;
        }

        // Where the picture stands: after a gap from the options, as wide as the panel allows up to 480, and no taller
        // than leaves room under it for the name and three lines of words (a short window gets a smaller picture).
        internal Rectangle PictureBounds
        {
            get
            {
                int left = DpiMetrics.Px(this, 28), top = DpiMetrics.Px(this, 4);
                int byWidth = ClientSize.Width - left - DpiMetrics.Px(this, 12), byHeight = (ClientSize.Height - top - DpiMetrics.Px(this, 100)) * 16 / 9;
                int width = Math.Max(DpiMetrics.Px(this, 120), Math.Min(DpiMetrics.Px(this, 480), Math.Min(byWidth, byHeight)));
                return new Rectangle(left, top, width, width * 9 / 16);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (String.IsNullOrEmpty(OptionId)) return;
            Rectangle box = PictureBounds;
            using (var back = new SolidBrush(Palette.Raised)) g.FillRectangle(back, box);
            Image picture = Picture(OptionId);
            if (picture != null)
            {
                // Whole picture, its own proportions, centred.
                float scale = Math.Min((float)box.Width / picture.Width, (float)box.Height / picture.Height);
                int w = Math.Max(1, (int)(picture.Width * scale)), h = Math.Max(1, (int)(picture.Height * scale));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(picture, new Rectangle(box.Left + (box.Width - w) / 2, box.Top + (box.Height - h) / 2, w, h));
            }
            else DrawPlaceholder(g, box);
            using (var edge = new Pen(Palette.Border)) g.DrawRectangle(edge, box.Left, box.Top, box.Width - 1, box.Height - 1);
            int y = box.Bottom + DpiMetrics.Px(this, 14);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            Size title = TextRenderer.MeasureText(g, Title, Palette.Strong, new Size(box.Width, Int32.MaxValue), flags);
            TextRenderer.DrawText(g, Title, Palette.Strong, new Rectangle(box.Left, y, box.Width, title.Height), Palette.Text, flags);
            y += title.Height + DpiMetrics.Px(this, 6);
            TextRenderer.DrawText(g, About, Palette.Small, new Rectangle(box.Left, y, box.Width, Math.Max(0, ClientSize.Height - y)), Palette.Muted, flags);
        }

        // A framed "picture" sign and two lines: there is no picture of this option yet.
        private void DrawPlaceholder(Graphics g, Rectangle box)
        {
            int s = DpiMetrics.Px(this, 44);
            var sign = new Rectangle(box.Left + (box.Width - s) / 2, box.Top + box.Height / 2 - s, s, s * 3 / 4);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Palette.Muted, Math.Max(1f, DpiMetrics.Px(this, 2))) { LineJoin = LineJoin.Round })
            {
                g.DrawRectangle(pen, sign);
                g.DrawLines(pen, new[] { new Point(sign.Left + s / 8, sign.Bottom - s / 8), new Point(sign.Left + s * 3 / 8, sign.Top + s * 3 / 8), new Point(sign.Left + s * 5 / 8, sign.Bottom - s / 4), new Point(sign.Left + s * 3 / 4, sign.Top + s / 2 - s / 16), new Point(sign.Right - s / 8, sign.Bottom - s / 8) });
                g.DrawEllipse(pen, sign.Right - s * 5 / 16, sign.Top + s / 8, s / 8, s / 8);
            }
            g.SmoothingMode = SmoothingMode.Default;
            const TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            int y = sign.Bottom + DpiMetrics.Px(this, 12), line = DpiMetrics.Px(this, 20);
            TextRenderer.DrawText(g, Title, Palette.Small, new Rectangle(box.Left + 8, y, box.Width - 16, line), Palette.Text, centre);
            TextRenderer.DrawText(g, "Picture to come", Palette.Small, new Rectangle(box.Left + 8, y + line, box.Width - 16, line), Palette.Muted, centre);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (Image image in pictures.Values) if (image != null) image.Dispose(); pictures.Clear(); }
            base.Dispose(disposing);
        }
    }
}
