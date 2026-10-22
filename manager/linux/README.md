# SlotsLoader Manager for Linux and Steam Deck

This is an experimental desktop release. It uses the Windows Manager interface
with a dedicated Bottles runtime and a small Linux helper. Real-game and Steam
Deck acceptance is recorded separately in **VALIDATION.md**.

The bundled SlotsLoader 1.10.4 proxy and plugin passed an isolated Wine 9
bootstrap check with default DLL loading. This verifies DLL loading and
bootstrap only: real SF6 gameplay and Steam Deck hardware remain tester
checks. The Steam setup guide recommends the documented REFramework `dinput8`
override; an additional AGS override has not been established as necessary.

## Install

1. On Steam Deck, switch to **Desktop Mode**. Extract the complete `.tar.gz`
   download into a normal folder. Do not run files from inside the archive viewer.
2. Open **Install SlotsLoader Manager.desktop**. Your file manager may ask you to
   trust or allow this downloaded launcher. Python 3.10 or newer and either KDE's `kdialog`
   or GNOME's `zenity` must be available. Use the normal KDE Desktop Mode on
   Steam Deck; setup reports any missing prerequisite.
3. Confirm the detected SF6 folder, or select it. Choose a mod library. Both
   internal storage and mounted microSD game folders are supported by discovery.
4. If prompted, install **Bottles** from Discover or your software centre and
   finish its welcome screen. Reopen the installer. It creates a dedicated
   runtime and downloads .NET Framework 4.8 and Microsoft's graphics runtime.
   The graphics dependency alone downloads about 947 MB; allow several minutes
   for first setup. Microsoft installer windows may appear.
5. After the runtime check, setup offers the Steam game setup guide and Manager
   opens. Later, use **SlotsLoader Manager** in
   your application menu. The extracted download may then be removed.

No terminal commands are part of the normal setup. An internet connection is
needed for initial runtime installation. Manager and its ordinary `Data` folder
live in `~/.local/share/slotsloader-manager/app` (or your configured XDG data
directory). The runtime is separate from SF6's Proton prefix.

The package includes Inter and Liberation Mono fonts, so the interface does not
depend on fonts installed on your desktop. Setup checks the actual loaded
graphics renderer, smooth edges and font rendering before opening Manager.

## Use and update

Run Manager in Desktop Mode. Install mods using the usual interface, close
Manager before returning to Gaming Mode, and launch SF6 through Steam.
Keep using Steam's existing game installation and launch options. This installer
does not change your SF6 Proton version or Steam launch options. The test handoff
must confirm the game-side loader configuration before its first real mod test.

To update, extract the next complete Linux package and open its installer.
Manager must be closed. The update preserves `app/Data`, mod libraries and
recovery records. An interrupted program-file update is rolled back on the next
launch. Do not install a Windows-only portable update over this package.

Updating from Linux beta1 also prepares the improved graphics runtime. Setup
keeps the previous runtime selected until the new one passes its checks, and
preserves your app Data. The old runtime remains available in Bottles afterward.

The application's menu entry has additional actions (right-click it on KDE):

- **Steam game setup:** shows the REFramework launch-options requirement with a
  copyable line. Preserve any options already present in Steam. Consult the
  supplied validation notes for the exact loader combination tested.
- **Choose game and mod folders:** change a library or give access to a new drive.
  Bottles gets these selected folders only for this launch; setup does not change
  global Flatpak filesystem overrides. Close Manager and run this action when
  adding a library outside the existing granted folders.
- Existing custom replacement stores and explicitly configured external-manager
  folders receive their own scoped access on launch. A recognised Fluffy
  `Games/SF6/Mods` library also exposes its immediate `SF6` folder so presets,
  backups and atomic `installed.ini` updates work. Higher parents are not added.
- **Repair compatibility runtime:** creates and checks a fresh dedicated runtime.
  It leaves the old runtime, app Data and mods intact. You can remove unused
  `SlotsLoader-Manager-…` bottles in Bottles after confirming the repaired runtime.
  If existing storage uses the runtime's private `C:` drive, normal launches
  retain that mapping. Repair pauses until storage and recovery are moved to
  stable Linux folders through `Z:`; it never silently points them at a new bottle.
- **Save diagnostic report:** produces a small JSON file with package/runtime
  status. It excludes API keys, private paths, mod names and mod contents.

If an external drive is missing, reconnect it before launching. Setup never
creates a replacement game folder at an unmounted card path. The Linux helper
checks SF6 and other mod-manager processes on the host before game writes. Keep
Manager running through its Linux shortcut; opening the EXE directly bypasses
this session and game writes are blocked.

Assistant API keys are session-only on this beta. Existing Windows key storage
is not imported. Full controller-only navigation and launching Manager from
Gaming Mode are outside the initial supported workflow.

## Tester checklist

- Complete a fresh setup, close and reopen Manager, then reopen after a reboot.
- Verify SF6 discovery for your actual storage, especially microSD or Flatpak Steam.
- Check text, dialogs and scrolling at 1280 × 800. Test selecting a mod archive
  and an extracted mod folder. Include ZIP, RAR and 7Z if you use all three.
- Before any real game changes, confirm the game-side loader prerequisites with
  the supplied validation notes. Preserve your existing Steam launch options.
- With SF6 closed, install one known extra outfit and one known replacement.
  Check them in-game, uninstall, and verify the originals return.
- Try starting a write while SF6 is running: Manager must refuse it.
- Return to Gaming Mode and verify the installed mod loads there too.
- Report your SteamOS/Linux version, Bottles version, storage type and the
  exact operation that failed. Include the diagnostic report; do not send keys.

## Advanced testing

An already provisioned system Wine prefix can be used with
`python3 launcher.py --wine /path/to/wine --prefix /path/to/dedicated-prefix`.
This option does not install Wine or dependencies and must never point at SF6's
prefix. It requires Microsoft .NET Framework 4.8 and the exact x64 GDI+ dependency
identified by the included Bottles recipe. The launcher verifies and loads an
application-local copy; it does not replace prefix system DLLs. Use graphical
Bottles setup unless you already maintain a suitable dedicated prefix.
The graphical path uses the public Bottles CLI and the included `bottle.yml`
recipe. In a source checkout, run native checks with
`python3 -m unittest discover -s manager/linux -p 'test_*.py'` from the repository
root, and adapter checks on Windows with `manager/test.ps1 -Only linux`.

Runtime prerequisites and CLI documentation:
[Bottles CLI](https://docs.usebottles.com/advanced/cli),
[Bottles downloads](https://usebottles.com/download/),
[Bottles filesystem access](https://docs.usebottles.com/flatpak/expose-directories).
