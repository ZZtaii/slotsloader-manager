using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.19 (the user, 13 Nov 2026: "i also want to relax requirements. when a mod has some files tagged as requirement
    // or guessed etc. then we should show a popup (less annoying though) around the image that it may not work without x
    // mod and highlight the required mod temporarily but install the mod anyways. this helps to mix and match options").
    // What the ticks leave out (Composer.MissingNeeds) is said in one line under the preview picture, the option left out
    // lights up in the list for a moment, and Install stays on. Nothing here is a dialog and nothing needs a click.
    //
    // 13 Nov 2026 (the user: "highlights should never happen on mod hovers, only on mod installs if there are possibly
    // some pre-requisites missing"): the rows light up only when Install is pressed with something left out (the install
    // goes ahead all the same: FlashNeedsAtInstall) and on a click on the line under the picture (JumpToNeed). Never when
    // a mod is opened, selected or pointed at, and never when a tick changes. The line under the picture is as it was.
    internal sealed partial class MainForm
    {
        private List<MissingNeed> needs = new List<MissingNeed>();
        // Options the user unticked by hand in the open mod: ticking something else does not tick them again.
        private readonly HashSet<string> leftOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> flashIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer flashTimer;
        private int flashStart;
        private Label needNotice;
        // A required group left empty blocks Install only while nothing with files is ticked.
        private bool choicesBlock;
        // 13 Nov 2026 (the user, of the row that lights up for the main files left out: "can we make it a bit lighter?
        // ... and probably make it disappear sooner"): it was the full tint for 1.8 s and 0.8 s of fading; now a little
        // over half the tint (NeedFlashStrength) for 0.8 s and 0.5 s of fading.
        internal const int NeedFlashHold = 800, NeedFlashFade = 500;
        internal const double NeedFlashStrength = 0.55;
        // 13 Nov 2026 (the user: "change the highlight from orang'ish to more lightish grey that kind of blends in"): the
        // tint was the brown (64, 44, 27) with an accent (orange) edge. Now a grey of the palette's own cast, a little
        // lighter than a row the pointer is on: a Surface row (30, 33, 39) shows as (49, 53, 60), a Raised one
        // (39, 42, 49) as (53, 57, 64). The edge at the left is a grey too and fades with the row. Timings unchanged.
        internal static readonly Color NeedTint = Color.FromArgb(66, 70, 78), NeedEdge = Color.FromArgb(120, 126, 138);

        internal Label NeedNotice { get { return needNotice; } }
        internal IList<MissingNeed> CurrentNeeds { get { return needs.AsReadOnly(); } }
        internal bool IsFlashing(string id) { return flashIds.Contains(id); }
        internal bool ChoicesBlock { get { return choicesBlock; } }

        // The line under the preview picture. It takes its height from the picture, not from the text below it.
        private Control BuildNeedNotice()
        {
            needNotice = new Label { UseMnemonic = false, Dock = DockStyle.Bottom, Height = 26, Visible = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), BackColor = Palette.Raised, ForeColor = Palette.Accent, Font = Palette.Small, Cursor = Cursors.Hand, AccessibleName = "What this selection leaves out", AccessibleRole = AccessibleRole.Link };
            needNotice.Click += delegate { JumpToNeed(); };   // no hover text (2.4.0 rule): the full notes are in the report's Notes
            flashTimer = new System.Windows.Forms.Timer { Interval = NeedFlashHold };
            flashTimer.Tick += delegate
            {
                if (unchecked(Environment.TickCount - flashStart) >= NeedFlashHold + NeedFlashFade) { flashTimer.Stop(); flashIds.Clear(); componentTree.Invalidate(); return; }
                flashTimer.Interval = 40;
                foreach (string id in flashIds) { OptionNode node; if (componentNodes.TryGetValue(id, out node)) componentTree.InvalidateNode(node); }
            };
            FormClosed += delegate { flashTimer.Dispose(); };
            return needNotice;
        }

        // How much of the light is left: all of it while it holds, then less until it is gone.
        private double FlashLeft()
        {
            int age = unchecked(Environment.TickCount - flashStart);
            return age <= NeedFlashHold ? 1 : Math.Max(0, 1 - (age - NeedFlashHold) / (double)NeedFlashFade);
        }
        internal static Color Blend(Color own, Color to, double share) { return Color.FromArgb((int)(own.R + (to.R - own.R) * share), (int)(own.G + (to.G - own.G) * share), (int)(own.B + (to.B - own.B) * share)); }
        // The row colour of an option that is lighting up: the grey tint, fading back to the row's own colour.
        private Color FlashColor(Color own) { return Blend(own, NeedTint, NeedFlashStrength * FlashLeft()); }
        // Its edge at the left (MainForm.DrawComponentNode): the grey edge, fading the same way.
        private Color FlashEdgeColor(Color own) { return Blend(own, NeedEdge, FlashLeft()); }

        private List<MissingNeed> LiveNeeds()
        {
            List<MissingNeed> result = Composer.MissingNeeds(package, selectedOrder, IsReplaceMode);
            // An addon shown inside the mod: its Requirement= names options of the main mod (LibraryAddons.Requirements).
            if (package.MergedFrom != null)
                foreach (string id in selectedOrder.Where(id => LibraryAddons.IsAddonId(package, id)).ToList())
                {
                    ModComponent c = package.Find(id); if (c == null) continue;
                    foreach (ModComponent need in LibraryAddons.Requirements(package, c))
                        if (!selectedOrder.Contains(need.Id, StringComparer.OrdinalIgnoreCase) && !result.Any(n => String.Equals(n.Needed.Id, need.Id, StringComparison.OrdinalIgnoreCase)))
                            result.Add(new MissingNeed { Needed = need, By = c, Text = "'" + c.Name + "' may not work without '" + need.Name + "'." });
                }
            return result;
        }

        // "May not work without “Main files”" (and one more name, or a count).
        internal string NeedsText()
        {
            List<string> names = needs.Select(n => n.Needed.IsDummy ? "an option from “" + n.Needed.Name + "”" : "“" + n.Needed.Name + "”").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) return "";
            return "May not work without " + names[0] + (names.Count == 1 ? "" : names.Count == 2 ? " and " + names[1] : " and " + (names.Count - 1) + " more");
        }

        // After the ticks changed (UpdateRequiredChoices). fill: a mod was just opened, so what its ticks leave out was
        // left out before (a saved selection) and is not ticked again behind the user's back.
        // No row lights up here (13 Nov 2026): not for an opened mod and not for a changed tick. Only the line is kept
        // up to date, and rows still alight go dark once nothing is left out.
        private void UpdateNeeds(bool fill)
        {
            needs = package == null ? new List<MissingNeed>() : LiveNeeds();
            if (fill) foreach (MissingNeed n in needs) if (!n.Needed.IsDummy) leftOut.Add(n.Needed.Id);
            if (needNotice != null)
            {
                string text = NeedsText();
                needNotice.Text = text; needNotice.Visible = text.Length > 0;
                needNotice.AccessibleDescription = text.Length == 0 ? "" : String.Join(" ", needs.Select(n => n.Text).Distinct()) + " Install works either way. Click to see it in the list.";
            }
            if (needs.Count == 0 && flashIds.Count > 0) { flashTimer.Stop(); flashIds.Clear(); componentTree.Invalidate(); }
        }

        // Install was pressed and its work is about to start (MainForm.InstallSlot, MainFormReplace.InstallReplacement)
        // with something left out: those rows light up once, as a last word. The install is not held back. The first of
        // them is scrolled into view when the list is scrolled away from it (no group is opened); the list is on show
        // while the install runs, so the light is seen beside the status line saying "Installing".
        private void FlashNeedsAtInstall()
        {
            if (needs.Count == 0 || package == null) return;
            OptionNode first;
            if (componentNodes.TryGetValue(needs[0].Needed.Id, out first))
            {
                while (first != null && componentTree.RowIndex(first) < 0) first = first.Parent;
                if (first != null) componentTree.EnsureVisible(first);
            }
            Flash(needs.Select(n => n.Needed.Id).ToList());
        }

        // Lights the rows up for a moment. No group is opened: an option inside a closed group lights its nearest row
        // that is on show.
        private void Flash(IEnumerable<string> ids)
        {
            flashIds.Clear();
            foreach (string id in ids)
            {
                OptionNode node; if (!componentNodes.TryGetValue(id, out node)) continue;
                while (node != null && componentTree.RowIndex(node) < 0) node = node.Parent;
                ModComponent shown = node == null ? null : node.Tag as ModComponent;
                if (shown != null) flashIds.Add(shown.Id);
            }
            if (flashIds.Count == 0) return;
            flashStart = Environment.TickCount;
            flashTimer.Stop(); flashTimer.Interval = NeedFlashHold; flashTimer.Start();
            componentTree.Invalidate();
        }

        // A click on the line: the first option left out is scrolled into view and everything left out lights up again.
        internal void JumpToNeed()
        {
            MissingNeed first = needs.FirstOrDefault(); OptionNode node;
            if (first == null || package == null || !componentNodes.TryGetValue(first.Needed.Id, out node)) return;
            ShowPage(libraryPage); ShowDetailTab(false);
            node.EnsureVisible(); if (first.Needed.IsDummy) node.Expand();
            Flash(needs.Select(n => n.Needed.Id).ToList());
            componentTree.Focus();
        }

        // Screenshot mode ("needs"): untick the first ticked main-files or Required option, as the user would.
        private void ShowNeedsForScreenshot()
        {
            if (package == null) return;
            ModComponent main = selectedOrder.Select(id => package.Find(id)).FirstOrDefault(c => c != null && Composer.IsBase(c))
                ?? selectedOrder.Select(id => package.Find(id)).FirstOrDefault(c => c != null && selectedOrder.Any(o => { ModComponent x = package.Find(o); return x != null && String.Equals(x.ParentId, c.Id, StringComparison.OrdinalIgnoreCase); }));
            OptionNode node;
            if (main == null || !componentNodes.TryGetValue(main.Id, out node)) return;
            List<string> kept = selectedOrder.Where(id => !String.Equals(id, main.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            node.Checked = false;
            foreach (string id in kept) { OptionNode again; if (componentNodes.TryGetValue(id, out again) && !again.Checked) again.Checked = true; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (ReviewPending && clock.ElapsedMilliseconds < 15000) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
            Flash(needs.Select(n => n.Needed.Id).ToList());
        }
    }
}
