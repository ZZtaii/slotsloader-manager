using System;
using System.Collections.Generic;
using System.Linq;

namespace SlotsInstaller
{
    // 26 Nov 2026: the one-click setup (GameSetup.cs) from the window: Settings' "Install ..." button, the library's notice
    // under INSTALL AS (Extra outfit and Extra stage) and nothing else. The click is the consent: no question, no review.
    internal sealed partial class MainForm
    {
        internal SetupResult LastSetupResult;   // tests

        // What one click would install now (read fresh: the game folder, the Manager's records, the external manager).
        private SetupNeeds SetupNeedsNow()
        {
            try
            {
                if (!System.IO.Directory.Exists(config.GameRoot)) return new SetupNeeds();
                ReplaceEngine engine = Engine();
                FluffyInstalls fluffy = null; ReplaceState state = null;
                try { fluffy = engine.Fluffy(); } catch (Exception ex) { Program.Log(ex); }
                try { state = engine.LoadState(); } catch (Exception ex) { Program.Log(ex); }
                return GameSetup.Needs(ToolStatus.Detect(config.GameRoot, fluffy), state, fluffy, UpdateCheck.CoreInstallPin(UpdateSettings.Load()));
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return new SetupNeeds(); }
        }

        // The user: "change this to dependencies in button" (the lines above it already name what is missing).
        internal static string SetupButtonText(SetupNeeds needs)
        {
            bool core = needs.Core || needs.CoreUpdate;
            if (needs.ReFrameworkUpdate && !needs.Loader && !core) return "Update REFramework";
            if (needs.CoreUpdate && !needs.ReFramework && !needs.Loader) return "Update Native Core";
            return "Install dependencies";
        }

        private void RunGameSetup()
        {
            if (busy) { SetPageStatus(currentPage, "Another task is running. Click again when it finishes.", Palette.Accent); return; }
            if (gameRunning) { SetPageStatus(currentPage, "Close SF6 first: REFramework, Slotsloader and Native Core can't be installed while it runs.", Palette.Red); return; }
            SetupNeeds needs = SetupNeedsNow();
            string off = needs.TurnedOff.Count == 0 ? "" : GameSetup.Join(needs.TurnedOff) + (needs.TurnedOff.Count == 1 ? " is" : " are") + " turned off on the Installed page: turn " + (needs.TurnedOff.Count == 1 ? "it" : "them") + " back on there.";
            if (!needs.Any)
            {
                if (off.Length > 0) SetPageStatus(currentPage, off, Palette.Accent);
                else SetPageStatus(currentPage, "✓ REFramework, Slotsloader and Native Core are all in the game folder.", Palette.Green);
                LoadAfterSetup(); return;
            }
            ReplaceEngine engine = Engine();
            LoaderPin pin = CurrentInstallPin();
            List<string> libraries = config.Libraries.ToList(); string sevenZip = config.SevenZipPath, downloads = LoaderRelease.DownloadDir;
            Work("Installing " + needs.Text() + "…", delegate { return GameSetup.Run(engine, needs, pin, libraries, sevenZip, downloads); }, delegate(SetupResult r)
            {
                LastSetupResult = r;
                Program.LogLine("Set up: " + r.Message);
                LoadAfterSetup();
                RefreshInstalled((r.Failed == null ? "✓ " : "") + r.Message + (off.Length > 0 ? " " + off : ""));
            });
        }
        private void LoadAfterSetup()
        {
            UpdateModeUi();
            if (SettingsReplaceShown) LoadReplaceSettingsFields();
        }
    }
}
