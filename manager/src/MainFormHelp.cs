using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.11 (the user): in-app Help, gear → Help or F1 (no header tab of its own), also linked from the welcome.
    // A page like History, built on first use: search and the topic list at the left, the article at the right
    // (HelpControls.cs), both columns scrolling with the overlay thumb. Topics come from docs/HELP.md (HelpDocument).
    // Opening Help again resumes the topic and scroll position, also after a restart (view-settings HelpTopic / HelpOffset).
    // A [[link]] opens its topic (or scrolls to its heading); Back returns to where the link was.
    internal sealed partial class MainForm
    {
        private Panel helpPage;
        private PaddedTextField helpSearch;
        private HelpTopicList helpTopics;
        private HelpArticleView helpArticle;
        private OverlayScroller helpTopicScroll, helpArticleScroll;
        private readonly Stack<KeyValuePair<HelpTopic, int>> helpBack = new Stack<KeyValuePair<HelpTopic, int>>();   // topic and scroll offset

        internal void ShowHelp()
        {
            Panel page = EnsureHelpPage();
            ShowPage(page);
            navSettings.SetActive(true);
            if (helpArticle.Topic == null)
            {
                string heading;
                HelpTopic saved = viewPreferences == null ? null : HelpDocument.Find(viewPreferences.HelpTopic, out heading);
                OpenHelpTopic(saved ?? HelpDocument.Topics.FirstOrDefault(), null);
                if (saved != null) helpArticleScroll.ScrollTo(Px(Math.Max(0, viewPreferences.HelpOffset)), false);
            }
            helpTopicScroll.Relayout(); helpArticleScroll.Relayout();
        }

        private Panel EnsureHelpPage()
        {
            if (helpPage != null) return helpPage;
            Panel page = BuildHelpPage();
            page.Visible = false;
            if (dpiLayoutReady && DeviceDpi != 96) page.Scale(new SizeF(DeviceDpi / 96F, DeviceDpi / 96F));
            DarkTheme.Apply(page);
            pagesPanel.Controls.Add(page);
            helpPage = page;
            return page;
        }

        private Panel BuildHelpPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, Name = "helpPage" };
            var rail = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Palette.Surface, Padding = new Padding(12, 12, 6, 12) };
            helpSearch = TextField(); helpSearch.Dock = DockStyle.Top; helpSearch.Height = 32; helpSearch.PlaceholderText = "Search help"; helpSearch.AccessibleName = "Search help"; helpSearch.Name = "helpSearch";
            helpSearch.TextChanged += delegate { FilterHelp(); };
            helpTopics = new HelpTopicList { Name = "helpTopics" };
            helpTopics.SetTopics(HelpDocument.Topics);
            helpTopics.Chosen = ShowHelpTopic;
            helpTopicScroll = new OverlayScroller { Dock = DockStyle.Fill, Smooth = true };
            helpTopicScroll.SetContent(helpTopics, delegate(int w) { return helpTopics.NaturalHeight(w); });
            rail.Controls.Add(helpTopicScroll); rail.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10 }); rail.Controls.Add(helpSearch);
            var reader = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface };
            helpArticle = new HelpArticleView { Name = "helpArticle" };
            helpArticle.Navigate = FollowHelpLink; helpArticle.Page = ShowHelpTopic; helpArticle.Back = HelpBack;
            helpArticleScroll = new OverlayScroller { Dock = DockStyle.Fill, Smooth = true };   // 1.0.11: the wheel glides
            helpArticleScroll.SetContent(helpArticle, delegate(int w) { return helpArticle.NaturalHeight(w); });
            reader.Controls.Add(helpArticleScroll);
            page.Controls.Add(reader); page.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 14 }); page.Controls.Add(rail);
            return page;
        }

        // The list's choice or Previous / Next: a fresh start (nothing to go back to).
        internal void ShowHelpTopic(HelpTopic topic) { helpBack.Clear(); OpenHelpTopic(topic, null); }

        // A [[link]]: Back returns to this topic at this scroll position.
        internal void FollowHelpLink(HelpTopic topic, string heading)
        {
            if (topic == null) return;
            if (helpArticle.Topic != null) helpBack.Push(new KeyValuePair<HelpTopic, int>(helpArticle.Topic, helpArticleScroll.Offset));
            OpenHelpTopic(topic, heading);
        }

        internal void HelpBack()
        {
            if (helpBack.Count == 0) return;
            KeyValuePair<HelpTopic, int> to = helpBack.Pop();
            OpenHelpTopic(to.Key, null);
            helpArticleScroll.ScrollTo(to.Value, false);
        }

        // Shows a topic, chosen in the list, from the top or from `heading`; Previous / Next follow the full topic order.
        private void OpenHelpTopic(HelpTopic topic, string heading)
        {
            if (topic == null || helpArticle == null) return;
            if (!helpTopics.Topics.Contains(topic)) helpSearch.Text = "";   // a link out of the search's matches lists every topic again
            List<HelpTopic> all = HelpDocument.Topics;
            int i = all.IndexOf(topic);
            helpTopics.Selected = topic;
            helpArticle.Show(topic, i > 0 ? all[i - 1] : null, i >= 0 && i + 1 < all.Count ? all[i + 1] : null, helpBack.Count > 0 ? helpBack.Peek().Key : null);
            helpArticleScroll.Relayout();
            int? top = helpArticle.HeadingTop(heading);
            helpArticleScroll.ScrollTo(top.HasValue ? Math.Max(0, top.Value - Px(14)) : 0, false);
            SetPageStatus(helpPage, topic.Title);
            QueueViewSave();
        }

        // Every word of the search in the topic's title or text; the article follows the first match when it no longer matches.
        private void FilterHelp()
        {
            string query = helpSearch.Text.Trim();
            List<HelpTopic> matches = HelpDocument.Topics.Where(t => query.Length == 0 || t.Matches(query)).ToList();
            helpTopics.SetTopics(matches);
            helpTopicScroll.Relayout(); helpTopicScroll.ScrollTo(0, false);
            if (matches.Count > 0 && !matches.Contains(helpArticle.Topic)) ShowHelpTopic(matches[0]);
        }

        // For the view settings: the topic and scroll position Help resumes at (logical pixels).
        private string HelpResumeTopic { get { return helpArticle != null && helpArticle.Topic != null ? helpArticle.Topic.Title : viewPreferences != null ? viewPreferences.HelpTopic : ""; } }
        private int HelpResumeOffset { get { return helpArticle != null && helpArticle.Topic != null ? (int)Math.Round(helpArticleScroll.Offset * 96.0 / DeviceDpi) : viewPreferences != null ? viewPreferences.HelpOffset : 0; } }
    }
}
