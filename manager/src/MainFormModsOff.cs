using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (user request): Settings → Game files & replacements → "Turn outfit mods off" (ReplaceEngineModsOff.cs).
    // No question: the click is the consent and the same button, the banner's "Turn mods back on" or Undo puts
    // everything back. While it is off, a banner says so on every refresh.
    internal sealed partial class MainForm
    {
        private FlatButton modsOffButton, bannerModsOn;
        internal const string ModsOffLabel = "Turn outfit mods off", ModsOnLabel = "Turn mods back on";
        internal bool ModsAreOff { get { return gameStatus != null && gameStatus.ModsOff != null; } }

        private FlatButton BuildModsOffButton()
        {
            modsOffButton = Button(ModsOffLabel, false, 150, delegate { ToggleModsOff(); });
            modsOffButton.Name = "modsOffButton";
            modsOffButton.AccessibleDescription = "Switches every replacement and extra outfit off to play vanilla. Tool mods stay on. The same button turns them back on.";
            return modsOffButton;
        }

        // Called with every banner update (each refresh of the installed list).
        private void UpdateModsOffControls()
        {
            bool off = ModsAreOff, idle = !busy && !gameRunning && !FluffyBlocksPak;
            if (bannerModsOn != null) { bannerModsOn.Visible = off; bannerModsOn.Enabled = idle; }
            if (modsOffButton == null) return;
            modsOffButton.Text = off ? ModsOnLabel : ModsOffLabel;
            modsOffButton.Enabled = !busy;
        }

        internal void ToggleModsOff()
        {
            if (busy) return;
            bool turnOff = !ModsAreOff; string game = config.GameRoot;
            ChangeGameRow(turnOff ? "Turning outfit mods off" : "Turning outfit mods back on",
                delegate(ReplaceEngine e) { string r = e.SetModsOff(turnOff); if (!turnOff) UnownedSlotsFlag.Ensure(game); return r; },
                turnOff ? "Outfit mods are off." : "Outfit mods are back on.",
                delegate(ReplaceEngine e) { string r = e.SetModsOff(!turnOff); if (turnOff) UnownedSlotsFlag.Ensure(game); return r; });
        }
    }
}
