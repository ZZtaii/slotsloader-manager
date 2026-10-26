# SlotsLoader Manager for Linux and Steam Deck

## Install or update

1. On Steam Deck, switch to **Desktop Mode**.
2. Extract the whole ZIP into a normal folder. Open **Install SlotsLoader Manager.desktop** and allow it to run if your file manager asks.
3. Follow the graphical setup to select your SF6 folder and mod library. If Bottles is missing, install **Bottles** through Discover or your software centre and finish its welcome screen, then reopen this installer.
4. Launch **SlotsLoader Manager** from your application menu afterward. You can remove the extracted download once installation finishes.

Normal setup needs no terminal commands. Linux must provide Python 3.10 or newer and KDE's `kdialog` or GNOME's `zenity`. First setup needs internet and several minutes: Bottles installs its runner, .NET Framework 4.8 and a graphics dependency whose download is about 947 MB. Fonts are included. The graphics runtime is verified before Manager opens.

Updates preserve your settings, mod libraries, recovery records and compatibility runtime. The app lives in `~/.local/share/slotsloader-manager/app` (or your configured XDG data directory), separately from SF6's Proton prefix. Close Manager before manually installing a package over it.

## Automatic updates

Starting with this build, the Linux launcher checks GitHub once a day and offers newer **complete Linux packages**. Click Yes to download, verify and install an update before Manager opens. Nothing installs without your approval. Offline checks fail quietly so you can still use the app.

For an immediate check, use **Settings → Updates → Check Linux app updates**, or right-click the application-menu entry and choose **Check for Linux app updates**. Manager closes safely and reopens after the check. The daily-check checkbox in Settings controls automatic checks. The Linux beta follows its own beta-release channel; Windows packages cannot overwrite it.

Coming from 1.0.12 beta2, install this ZIP once using the graphical installer. Later Linux releases can be offered automatically. An interrupted program-file update is rolled back on the next launch. Do not use a Windows portable ZIP to update this installation.

## Playing SF6

Manage mods in Desktop Mode, close Manager, and launch SF6 through Steam. Keep your existing Proton version and Steam launch options. The **Steam game setup** action in the application menu shows the REFramework launch-option guidance. If you already have launch options, preserve and merge them. Install the required loader tool mods through Manager as usual.

Your game and mod folders can be on internal storage or a mounted microSD card. Reconnect missing drives before opening Manager. Use **Choose game and mod folders** from the application menu when adding a library on another drive. Access is granted to selected folders for this session, without global Flatpak permission changes.

**Repair compatibility runtime** prepares a new dedicated runtime while keeping your Data and the old runtime. Storage inside a private Wine C: drive must first be moved to stable Linux folders. **Save diagnostic report** creates a small report without keys, private paths, mod names or contents.

Assistant keys are session-only on Linux. Windows encrypted keys are not imported. Desktop Mode with a mouse or trackpad is the supported workflow; controller-only use and running Manager in Gaming Mode are not established.

## Feedback and source

Report your Linux/SteamOS version, Bottles version, storage type and exact failing action. Please check a familiar outfit through install, game launch, uninstall and restoration. Do not send API keys or private mod archives.

Source and releases: https://github.com/ZZtaii/slotsloader-manager

The package contains the GUI and required runtime helpers. Development checks and the CLI are kept in source, not in this download. See `LICENSE`, `THIRD_PARTY_NOTICES.md`, the font licenses and the bundled 7-Zip license for component terms.
