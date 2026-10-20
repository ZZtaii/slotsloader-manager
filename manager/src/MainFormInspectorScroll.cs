using System;
using System.Drawing;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.8 (the user): the Mod library inspector in an OverlayScroller (OverlayScroll.cs). It opens compact: the option
    // description on one line and each REPLACES note as its headline, cut to fit above INSTALL AS (so INSTALL AS and the
    // footer never leave the view), with "Scroll for details" when something is left out. Scrolling down over the
    // inspector shows everything (inspectorDetailsOpen); the content then grows and scrolls. Opening another mod makes it
    // compact again. The content is as tall as the column, or as tall as it needs with the preview at InspectorPreviewMin.
    internal sealed partial class MainForm
    {
        internal const int InspectorPreviewMin = 100;
        private OverlayScroller inspectorScroller;
        private int replacesWidth = -1;
        private bool inspectorDetailsOpen;
        private Label expandHint;   // "Scroll to expand" in the REPLACES heading row (MainFormReplace.BuildReplacesPanel)
        internal bool ExpandHintShown { get { return expandHint != null && OverlayScroller.OwnVisible(expandHint); } }
        internal OverlayScroller InspectorScroller { get { return inspectorScroller; } }
        internal bool InspectorDetailsOpen { get { return inspectorDetailsOpen; } }

        // Title, byline and description under the preview.
        private int InspectorTextHeight()
        {
            return componentTitle.Height + (OverlayScroller.OwnVisible(packageMeta) ? packageMeta.Height : 0) + packageDescription.Height;
        }
        // The height the inspector needs at a width: its rows at the bottom (INSTALL AS, REPLACES, Install all variants,
        // character and outfit name), the preview minimum and the text.
        private int InspectorNaturalHeight(int width)
        {
            Control inspector = inspectorScroller == null ? null : inspectorScroller.Content;
            if (inspector == null) return 0;
            int height = inspector.Padding.Vertical;
            foreach (Control row in inspector.Controls) if (row.Dock == DockStyle.Bottom && OverlayScroller.OwnVisible(row)) height += row.Height;
            return height + Px(InspectorPreviewMin) + InspectorTextHeight();
        }
        private const TextFormatFlags DescriptionFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        private int DescriptionTextHeight(int width)
        {
            return String.IsNullOrEmpty(packageDescription.Text) ? 0 : TextRenderer.MeasureText(packageDescription.Text, packageDescription.Font, new Size(width, Int32.MaxValue), DescriptionFlags).Height;
        }
        // The option description: one line with an ellipsis while compact, its full length once the details are open.
        private void FitDescription()
        {
            int width = packageDescription.ClientSize.Width;
            if (width <= 0) return;
            int height = inspectorDetailsOpen ? Math.Max(Px(24), DescriptionTextHeight(width) + Px(4)) : Px(24);
            if (packageDescription.Height != height) packageDescription.Height = height;
        }

        // Scrolling down over a compact inspector that leaves something out: show it all (OverlayScroller.Expand).
        private bool ExpandInspectorDetails()
        {
            if (inspectorDetailsOpen) return false;
            int width = packageDescription.ClientSize.Width;
            bool more = (width > 0 && DescriptionTextHeight(width) > Px(24)) || (OverlayScroller.OwnVisible(replacesPanel) && conflictView.HidesDetails(Math.Max(Px(100), replacesPanel.ClientSize.Width)));
            if (!more) return false;
            inspectorDetailsOpen = true;
            SizeReplacesPanel();   // every line of every note
            if (layoutInspectorInfo != null) layoutInspectorInfo();
            inspectorScroller.Relayout();
            return true;
        }
        // Another mod opened: compact again, at the top.
        private void CollapseInspectorDetails()
        {
            if (inspectorScroller != null) inspectorScroller.ScrollTo(0, false);
            if (!inspectorDetailsOpen) return;
            inspectorDetailsOpen = false;
            SizeReplacesPanel();
            if (layoutInspectorInfo != null) layoutInspectorInfo();
            if (inspectorScroller != null) inspectorScroller.Relayout();
        }
    }
}
