using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.5.9: icons at the right end of the status bar (the user's request, with a short hover each), left to right:
    // Discord (opens the invite), bug (hover only: where to report), coffee (opens the Patreon post).
    internal sealed partial class MainForm
    {
        internal const string DiscordUrl = "https://discord.gg/9Vr2SJ3";
        internal const string PatreonUrl = "https://www.patreon.com/ZZtai/posts/buy-me-coffee-167011360";
        internal const string DiscordHint = "Join the Discord", BugHint = "Found a bug? Report it on Discord or Nexus", CoffeeHint = "Love it? Support me";
        private ToolTip supportTip;
        private readonly List<IconLink> supportIcons = new List<IconLink>();

        private Panel BuildSupportLinks()
        {
            var host = new Panel { Dock = DockStyle.Right, BackColor = Palette.Sidebar, Name = "supportLinks", Margin = Padding.Empty };
            if (supportTip == null) { supportTip = new ToolTip { InitialDelay = 250, ShowAlways = true }; Disposed += delegate { supportTip.Dispose(); }; }
            AddSupportIcon(ButtonGlyph.Chat, "discordLink", DiscordHint, DiscordUrl);
            AddSupportIcon(ButtonGlyph.Bug, "bugHint", BugHint, null);
            AddSupportIcon(ButtonGlyph.Coffee, "coffeeLink", CoffeeHint, PatreonUrl);
            for (int i = supportIcons.Count - 1; i >= 0; i--) host.Controls.Add(supportIcons[i]);   // Dock=Left: the last added is leftmost
            Action size = delegate
            {
                foreach (IconLink icon in supportIcons) icon.Width = Px(30);
                host.Width = supportIcons.Count * Px(30);
            };
            dpiLayouts.Add(size); size();
            return host;
        }
        // ---- Settings → About (2.5.9; gear → About opens it)
        internal const string RepoUrl = "https://github.com/ZZtaii/slotsloader-manager", ColorSyncUrl = "https://colorsync-sf6.pages.dev/", NexusProfileUrl = "https://www.nexusmods.com/profile/ZZtaii",
            AuthorUrl = "https://www.patreon.com/cw/ZZtai", AuthorGitHubUrl = "https://github.com/ZZtaii", Wael3rdUrl = "https://github.com/Wael3rd/",
            BrewedVfxUrl = "https://linktr.ee/brewedvfx";
        private FlatButton settingsAboutTab;
        private Panel settingsAboutPanel;
        private Panel BuildAboutSettings()
        {
            // Scrolls only when the window is too short for all three rows of cards.
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Visible = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(2, 10, 0, 8), Name = "settingsAboutPanel" };
            panel.Controls.Add(new Label { UseMnemonic = false, Text = "Slotsloader Manager " + UpdateCheck.ShortVersion(SafeHttp.AppVersion()), AutoSize = true, Font = new Font("Segoe UI", 16F, FontStyle.Bold), ForeColor = Palette.Text, Margin = new Padding(0, 0, 0, 6), Name = "aboutTitle" });
            panel.Controls.Add(new Label { UseMnemonic = false, AutoSize = false, Width = AboutWidth, Height = 56, Font = new Font("Segoe UI", 12F), ForeColor = Palette.Text, Margin = new Padding(0, 2, 0, 4), Name = "aboutIntro",
                Text = "A mod manager with lots of goodies, optimized for Street Fighter 6. Install mods as replacements or extra slots. Fast searches, safer installs and more." });
            AddAboutSection(panel, "Community & support",
                Card("Discord", "Discuss modding and everything in between.", DiscordUrl, ButtonGlyph.Chat, DiscordBlue),
                Card("Buy me a coffee", "Love it? I appreciate your support.", PatreonUrl, ButtonGlyph.Coffee, Palette.Text),
                Card("Source code", "Report issues and contribute.", RepoUrl, ButtonGlyph.GitHub, Palette.Text));
            AddAboutSection(panel, "More by me",
                Card("Patreon", "Polls, WIPs and more.", AuthorUrl, ButtonGlyph.Patreon, PatreonCoral),
                Card("ColorSync", "Color costumes right in your browser.", ColorSyncUrl, ButtonGlyph.ColorSync, Palette.Text),
                Card("Nexus Mods", "All my published mods on Nexus.", NexusProfileUrl, ButtonGlyph.Nexus, NexusOrange));
            AddAboutSection(panel, "Credits",
                Card("ZZtaii", "Creator of Slotsloader Manager.", AuthorGitHubUrl, ButtonGlyph.GitHub, Palette.Text),
                Card("Wael3rd", "Creator of the Slotsloader backend.", Wael3rdUrl, ButtonGlyph.GitHub, Palette.Text),
                Card("BrewedVFX", "Testing and recommendations.", BrewedVfxUrl, LogoResource("SlotsInstaller.BrewedVfx.png")));   // 1.0.11
            // Three cards per row at up to 330 px each; narrower windows shrink them (never a sideways scroll).
            Action fit = delegate
            {
                int gap = Px(18), scrollBar = panel.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
                int available = panel.ClientSize.Width - panel.Padding.Horizontal - scrollBar - Px(4);
                int cardWidth = Math.Max(Px(200), Math.Min(Px(AboutCardWidth), (available - gap * 2) / 3)), rowWidth = cardWidth * 3 + gap * 2;
                foreach (Control c in panel.Controls)
                {
                    if (c is FlowLayoutPanel) { c.Width = rowWidth; foreach (Control card in c.Controls) card.Width = cardWidth; }
                    else if (c.Name != "aboutTitle") c.Width = rowWidth;
                }
            };
            panel.Resize += delegate { fit(); };
            dpiLayouts.Add(fit);
            return panel;
        }
        private const int AboutWidth = 1026, AboutCardWidth = 330, AboutCardHeight = 88;
        // Brand colours of the card icons (GitHub and the Ko-fi-style mug use the text colour; ColorSync and Nexus draw their own).
        private static readonly Color DiscordBlue = Color.FromArgb(0x58, 0x65, 0xF2), PatreonCoral = Color.FromArgb(0xFF, 0x42, 0x4D), NexusOrange = Color.FromArgb(0xDA, 0x8E, 0x35);
        private static void AddAboutSection(FlowLayoutPanel panel, string caption, params AboutCard[] cards)
        {
            panel.Controls.Add(new Label { UseMnemonic = false, Text = caption, AutoSize = false, Width = AboutWidth, Height = 24, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(0, 14, 0, 8) });
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = false, Width = AboutWidth, Height = AboutCardHeight, Margin = Padding.Empty, Padding = Padding.Empty };
            foreach (AboutCard card in cards) row.Controls.Add(card);
            panel.Controls.Add(row);
        }
        private AboutCard Card(string title, string body, string url, ButtonGlyph icon, Color iconColor)
        {
            var card = new AboutCard { Title = title, Body = body, Url = url, Icon = icon, IconColor = iconColor, Width = AboutCardWidth, Height = AboutCardHeight, Margin = new Padding(0, 0, 18, 0), AccessibleName = title + ": " + body };
            card.Click += delegate { OpenReleasePage(url); };
            return card;
        }
        // 1.0.11: a card with a logo image in the tile (no logo resource: a text-only card).
        private AboutCard Card(string title, string body, string url, Image logo)
        {
            AboutCard card = Card(title, body, url, ButtonGlyph.None, Palette.Text);
            card.Picture = logo;
            if (logo != null) card.Disposed += delegate { logo.Dispose(); };
            return card;
        }
        private static Image LogoResource(string name)
        {
            using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) return null;   // source-test harnesses may build without the resource
                using (var decoded = Image.FromStream(stream)) return new Bitmap(decoded);
            }
        }
        internal void ShowAboutTab()
        {
            foreach (Control c in settingsGeneralParts) c.Visible = false;
            if (settingsSubtitle != null) settingsSubtitle.Visible = false;
            settingsReplacePanel.Visible = false; settingsUpdatesPanel.Visible = false;
            settingsAboutPanel.Visible = true; settingsAboutPanel.BringToFront();
            settingsGeneralTab.SetActive(false); settingsReplaceTab.SetActive(false); settingsUpdatesTab.SetActive(false); settingsAboutTab.SetActive(true);
        }
        private void HideAboutTab() { if (settingsAboutPanel != null) { settingsAboutPanel.Visible = false; settingsAboutTab.SetActive(false); } }

        // 1.0.1: the Installed page's icon-only History and Presets buttons name themselves on hover (the user's request).
        private void IconHint(Control control, string hint)
        {
            if (supportTip == null) { supportTip = new ToolTip { InitialDelay = 250, ShowAlways = true }; Disposed += delegate { supportTip.Dispose(); }; }
            supportTip.SetToolTip(control, hint);
        }
        // url null: hover only (no hand cursor, nothing on click).
        private void AddSupportIcon(ButtonGlyph glyph, string name, string hint, string url)
        {
            var icon = new IconLink { Glyph = glyph, Dock = DockStyle.Left, BackColor = Palette.Sidebar, Name = name, AccessibleName = hint, Tag = url };
            if (url != null) icon.Click += delegate { OpenReleasePage(url); };
            else { icon.Cursor = Cursors.Default; icon.AccessibleRole = AccessibleRole.StaticText; }
            supportTip.SetToolTip(icon, hint);
            supportIcons.Add(icon);
        }
    }
}
