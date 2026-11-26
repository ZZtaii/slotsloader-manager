using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 26 Nov 2026 (the user: "we don't allow replacement installs when other mod manager is open. But we probably need to
    // make this known more prominent"). Until now the rule showed as a red footer line and a small hint beside INSTALL AS,
    // which people missed. Now a banner across the top of the page says it, only where it stops something:
    // - Mod library, a mod open in Replace original: "Replace original is off" while the other mod manager has mods
    //   installed (ReplaceEngine.ExternalLockReason), or "close it" while it runs and this install would edit the PAK.
    // - Installed page: while it runs and replacements use the PAK method (their changes wait: FluffyBlocksPak).
    // Someone who keeps the other manager for replacements and installs only extra outfits here never sees it.
    internal sealed partial class MainForm
    {
        private NoticeBanner libraryManagerBanner, installedManagerBanner;
        private string modeExternalLock;      // UpdateModeUi: the lockout reason for the open mod (null: none)
        private string modeMethodNow = "";    // UpdateModeUi: the method the open mod would install with (Replace original)
        private int modeExternalMods;         // UpdateModeUi: the other manager's installed mods, as the lockout counted them

        internal static string ManagerLockText(int mods) { return "Replace original is off while your other mod manager has " + ReplacePlanner.N(mods, "mod") + " installed. Extra outfits still install here."; }
        internal const string ManagerOpenInstallText = "Close your other mod manager to install this replacement: both apps edit the same game PAK.";
        internal const string ManagerOpenChangeText = "Your other mod manager is open, so replacements can't change here until you close it: both apps edit the same game PAK.";

        private NoticeBanner BuildManagerBanner(string name, bool migrate)
        {
            var banner = new NoticeBanner { Visible = false, Name = name };
            FlatButton why = banner.Add(Button("Why?", false, 64, delegate { ShowManagerHelp(banner.Message.Text); })); why.Name = name + "Why"; why.AccessibleName = "Why replacements wait for the other mod manager";
            if (migrate) { FlatButton move = banner.Add(Button("Migrate mods here", false, 150, ShowMigration)); move.Name = name + "Migrate"; move.AccessibleName = "Migrate the other mod manager's installed mods here"; }
            return banner;
        }
        private Panel BuildLibraryManagerBanner() { return libraryManagerBanner = BuildManagerBanner("libraryManagerBanner", true); }
        private Panel BuildInstalledManagerBanner() { return installedManagerBanner = BuildManagerBanner("installedManagerBanner", false); }

        // Why?: Help's "Using it with your mod manager", scrolled to the part the banner is about.
        internal const string ManagerHelpTopic = "Using it with your mod manager", ManagerOpenHeading = "While your mod manager is open", ManagerLockHeading = "While your mod manager has mods installed";
        internal string LastManagerHelp;   // tests: "<topic>#<heading>" the last Why? opened
        private void ShowManagerHelp(string bannerText)
        {
            string link = ManagerHelpTopic + "#" + (bannerText == ManagerOpenInstallText || bannerText == ManagerOpenChangeText ? ManagerOpenHeading : ManagerLockHeading);
            LastManagerHelp = link;
            ShowHelp();
            string heading; HelpTopic topic = HelpDocument.Find(link, out heading);
            if (topic == null) return;
            helpBack.Clear(); OpenHelpTopic(topic, heading);
        }

        // What the Mod library banner says now (null: hidden).
        internal string LibraryManagerNotice()
        {
            if (package == null || !IsReplaceMode || StageMode) return null;
            if (ReplaceEngine.IsExternalLock(modeExternalLock)) return ManagerLockText(Math.Max(1, modeExternalMods));
            bool natives = PackagePaths(package).Any(p => (p ?? "").Replace('\\', '/').TrimStart('/').StartsWith("natives/", StringComparison.OrdinalIgnoreCase));
            if (fluffyRunning && natives && (FluffyBlocksPlan || modeMethodNow == ReplaceMethods.Pak)) return ManagerOpenInstallText;
            return null;
        }
        // What the Installed page banner says now (null: hidden).
        internal string InstalledManagerNotice()
        {
            if (!fluffyRunning || gameRows == null) return null;
            return FluffyBlocks(gameRows.Where(r => r.Mod != null).Select(r => r.Mod)) ? ManagerOpenChangeText : null;
        }

        internal void UpdateManagerBanners()
        {
            Set(libraryManagerBanner, LibraryManagerNotice());
            Set(installedManagerBanner, InstalledManagerNotice());
        }
        private static void Set(NoticeBanner banner, string text)
        {
            if (banner == null) return;
            bool show = !String.IsNullOrEmpty(text);
            if (show && banner.Message.Text != text) banner.Message.Text = text;
            FlatButton migrate = banner.Actions.Controls.OfType<FlatButton>().FirstOrDefault(b => b.Name.EndsWith("Migrate", StringComparison.Ordinal));
            if (migrate != null) migrate.Visible = show && text != ManagerOpenInstallText;
            if (banner.Visible != show) banner.Visible = show;
        }
    }
}
