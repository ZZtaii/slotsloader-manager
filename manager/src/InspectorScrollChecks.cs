using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.8 (the user): the Mod library inspector scrolls when its details don't fit (OverlayScroll.cs,
    // MainFormInspectorScroll.cs). Part of --filter-check (same hidden library window).
    internal static partial class FilterChecks
    {
        private static void CheckInspectorScroll(string root, List<string> report)
        {
            using (Window w = Open(root, null, new Size(1110, 610), SearchFixture()))
            {
                MainForm form = w.Form;
                form.ShowFilters(false, false); Settle(w.Host);
                OverlayScroller scroller = form.InspectorScroller;
                Control inspector = scroller.Content;
                Label description = Get<Label>(form, "packageDescription");
                Panel mode = Get<Panel>(form, "modePanel"), replaces = Get<Panel>(form, "replacesPanel");
                ConflictView conflicts = Get<ConflictView>(form, "conflictView");
                Func<Control, Rectangle> inView = delegate(Control c) { return scroller.RectangleToClient(c.RectangleToScreen(c.ClientRectangle)); };
                // Short details: nothing to scroll, no thumb, the wheel is left to the controls.
                description.Text = "Hover an option to preview it."; Settle(w.Host);
                Require(!scroller.CanScroll && scroller.Offset == 0 && !scroller.ThumbShown && inspector.Height == scroller.ClientSize.Height && !scroller.Wheel(-120), "short inspector scrolls: content " + inspector.Height + " / " + scroller.ClientSize.Height);
                Require(inView(mode).Bottom <= scroller.ClientSize.Height, "INSTALL AS below the column without scrolling");
                // A long description stays one line while compact (the user: INSTALL AS must stay in view)...
                string longText = String.Join(" ", Enumerable.Repeat("This option replaces the jacket with a cropped version, keeps the original boots and adds three colour variants that follow the outfit's CMD.", 9));
                description.Text = longText; Settle(w.Host);
                int needed = TextRenderer.MeasureText(longText, description.Font, new Size(description.ClientSize.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height;
                Require(!form.InspectorDetailsOpen && description.Height == DpiMetrics.Px(form, 24) && !scroller.CanScroll && inView(mode).Bottom <= scroller.ClientSize.Height, "compact long description: label " + description.Height + ", can scroll " + scroller.CanScroll);
                // ...until the user scrolls down over the inspector: it wraps in full, the column scrolls (the preview keeps its
                // minimum), the wheel step is applied and the thumb (thin, at the right edge, in the theme) shows.
                Control preview = Get<PictureBox>(form, "preview").Parent;
                Require(scroller.Wheel(-120) && form.InspectorDetailsOpen && description.Height >= needed && scroller.CanScroll && inspector.Height > scroller.ClientSize.Height && preview.Height >= DpiMetrics.Px(form, MainForm.InspectorPreviewMin), "expanded description: label " + description.Height + " / text " + needed + ", content " + inspector.Height + " / " + scroller.ClientSize.Height + ", preview " + preview.Height);
                Require(scroller.Offset == DpiMetrics.Px(form, OverlayScroller.WheelStep) && inspector.Top == -scroller.Offset && scroller.ThumbShown, "wheel: offset " + scroller.Offset);
                Rectangle thumb = scroller.ThumbBounds;
                Require(thumb.Width == DpiMetrics.Px(form, OverlayScroller.ThumbWidth) && thumb.Right == scroller.ClientSize.Width - DpiMetrics.Px(form, 2) && thumb.Height < scroller.ClientSize.Height && thumb.Top > 0, "thumb " + thumb);
                SaveShot(w.Host, scroller, Path.Combine(root, "inspector-scroll.png"));
                Require(scroller.ScrollTo(Int32.MaxValue, true) && scroller.Offset == scroller.MaxOffset && inView(mode).Bottom <= scroller.ClientSize.Height + 1 && inView(mode).Bottom >= scroller.ClientSize.Height - DpiMetrics.Px(form, 20), "scrolled to the end: INSTALL AS at " + inView(mode));
                int topBefore = thumb.Top; Require(scroller.ThumbBounds.Top > topBefore, "thumb did not follow the offset");
                DateTime until = DateTime.UtcNow.AddMilliseconds(OverlayScroller.HideDelayMs + 400);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
                Require(!scroller.ThumbShown, "thumb still shown after scrolling stopped");
                // Focus moving to a control out of view brings it into view.
                scroller.ScrollTo(scroller.MaxOffset, false);
                scroller.ScrollIntoView(Get<Label>(form, "componentTitle"));
                Require(inView(Get<Label>(form, "componentTitle")).Top >= 0, "scroll into view: title at " + inView(Get<Label>(form, "componentTitle")));
                // Another mod: compact again, at the top. REPLACES notes show their headlines and "Scroll for details".
                typeof(MainForm).GetMethod("CollapseInspectorDetails", PrivateInstance).Invoke(form, null);
                Require(!form.InspectorDetailsOpen && scroller.Offset == 0, "another mod did not make the inspector compact");
                description.Text = "Enable first"; Settle(w.Host);
                replaces.Visible = true;
                string note = "natives/stm/product/charparam/esf files (+5) already has 'Bundle Sakura as Ryu (WT) - C2 · Combined Hair + Barefoot' (enabled, installed 19 Oct 18:03, 151 files) · this install overrides 16 of its 151 files; 135 stay active";
                var slot = new ReplaceConflict { Kind = "slot", ModName = "Bundle Sakura as Ryu (WT) - C2 · Combined Hair + Barefoot", Enabled = true, NewWins = true, TheirFiles = 151, Shared = 16, InstalledUtc = new DateTime(2026, 10, 19, 17, 3, 0, DateTimeKind.Utc).ToString("o"), Areas = new List<string> { "Ryu Outfit 1 (esf001/001)" } };
                var stack = new ReplaceConflict { Kind = "stack", ModName = "Stage Lighting Overhaul", Enabled = true, Areas = new List<string> { "natives/stm/product/gui/data" } };
                string[] p1 = ReplacePlanner.ConflictParagraphs(slot), p2 = ReplacePlanner.ConflictParagraphs(stack);
                Require(p1[0].EndsWith("already has 'Bundle Sakura as Ryu (WT) - C2 · Combined Hair + Barefoot'.") && p1[1].StartsWith("Enabled · installed ") && p1[1].Contains(" · 151 files. This install overrides 16 of them; the other 135 stay active") && p2[1] == "Enabled · no shared files, so both stay installed.", "paragraphs: " + String.Join(" / ", p1.Concat(p2)));
                conflicts.SetEntries(new[] { new ConflictView.Entry { Text = p1[0], Detail = p1[1], Color = Palette.Accent, Link = "Uninstall 'Bundle Sakura as Ryu (WT) - C2'" }, new ConflictView.Entry { Text = p2[0], Detail = p2[1], Color = Palette.Text }, new ConflictView.Entry { Text = note, Color = Palette.Muted } });
                typeof(MainForm).GetMethod("SizeReplacesPanel", PrivateInstance).Invoke(form, null); Settle(w.Host);
                Require(conflicts.Compact && conflicts.HidesDetails(conflicts.ClientSize.Width) && form.ExpandHintShown && inView(mode).Bottom <= scroller.ClientSize.Height && !scroller.CanScroll, "compact notes: INSTALL AS at " + inView(mode) + ", can scroll " + scroller.CanScroll + ", hint " + form.ExpandHintShown);
                SaveShot(w.Host, replaces, Path.Combine(root, "inspector-replaces-compact.png"));
                Require(scroller.Wheel(-120) && form.InspectorDetailsOpen && !conflicts.Compact && !form.ExpandHintShown, "a wheel turn did not open the notes (or the hint stayed)");
                Settle(w.Host);
                int full = conflicts.PreferredHeightFor(conflicts.ClientSize.Width);
                Require(conflicts.Entries.All(e => e.MaxLines >= 50) && conflicts.ClientSize.Height >= full && full > 4 * TextRenderer.MeasureText("Ag", conflicts.Font).Height, "notes cut: view " + conflicts.ClientSize.Height + " / needed " + full);
                Require(scroller.MaxOffset >= 0 && inspector.Height >= scroller.ClientSize.Height, "content shorter than the column");
                scroller.ScrollIntoView(replaces); SaveShot(w.Host, replaces, Path.Combine(root, "inspector-replaces-notes.png"));
                report.Add("  1110x610 @ " + form.DeviceDpi + " DPI: column " + scroller.ClientSize.Height + " px, long description content " + needed + " px text, thumb " + thumb.Width + "x" + thumb.Height);
            }
        }
        private static void SaveShot(Control host, Control part, string file)
        {
            // The part itself (a form's DrawToBitmap includes its frame, so a crop by client coordinates was off).
            using (var bmp = new Bitmap(Math.Max(1, part.Width), Math.Max(1, part.Height))) { part.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(file, ImageFormat.Png); }
        }
    }
}
