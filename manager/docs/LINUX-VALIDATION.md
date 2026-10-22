# Linux / Steam Deck test build validation

Test build: **SlotsLoader Manager 1.0.12-linux-beta1**, 23 October 2026.
The Windows interface runs in a dedicated Bottles compatibility environment.
This package is prepared for a volunteer test. It is not a claim of Steam Deck
hardware or in-game SF6 validation.

## Tested software

- Manager source baseline: `da14348681486627f3760a516970277feedd8746`, with the
  Linux platform adapter, native helper and graphical launcher in this build.
- GUI SHA-256: `239dc4820402f18e4ec7f863ffc387b0d4f1cd82682a3b2dfb8881ac67b180d8`.
- CLI SHA-256: `5f16aeb276ba048d052ec7e7e9f6f6ecfb73bb2f0e2904a1c7fe3abba3073de7`.
- Bundled SlotsLoader: **1.10.4**, the original pinned archive, SHA-256
  `7529732062081c5cd886474aefb2a85ab6cb31b113af65aae80cff3f9d6d60de`.
- Bundled archive backend: **7-Zip 26.00 x64**, verified against the source pin.
- Actual Bottles test: **67.4** Flatpak, its automatically selected
  **soda-11.0-10** runner and Microsoft **.NET Framework 4.8**.
- Independent runtime test: Wine **9.0**, native Microsoft .NET Framework 4.8,
  x64 process, CLR `4.0.30319.42000`, framework release `528049`; no Wine Mono.

The Linux environments ran in two dedicated, resource-limited containers inside
an existing Ubuntu 26.04 WSL2 installation. Tests used generated games, mods,
PAKs, recovery records and fake Assistant credentials. No installed Windows app
Data, real game, personal mod library or actual API key was used for these tests.
WSL and unrelated containers/processes were not stopped or reconfigured.

## Validation results

| Area | Result |
| --- | --- |
| Clean compatibility runtime | The shipped custom Bottles recipe downloaded Soda and installed the .NET prerequisite and .NET 4.8 successfully. The native runtime probe passed. |
| Actual Bottles application | Authenticated Manager session opened at 1280 × 800, discovered a separately granted synthetic library with spaces in its path, closed normally and reopened successfully. Generated and replacement lifecycle checks also passed in Bottles. |
| Graphical installation | Full packaged installer completed with fresh app storage and real Zenity folder dialogs, Steam guidance, runtime check, welcome screen, library, normal exit and diagnostic dialog. This test reused the dedicated bottle whose clean provisioning was tested separately. |
| Archive backend under Bottles | Bundled 7-Zip 26.00 extracted ZIP, RAR and 7Z fixture payloads byte-for-byte; the 7Z archive was also created with the bundled tool. |
| Native helper | 20 Linux tests passed: cross-prefix process checks, uncertain-status refusal, disconnected/replaced storage, canonical roots, case collisions, symlink refusal, session locking, authenticated HTTP and recoverable Trash. |
| Graphical launcher and updater | 27 tests passed: desktop shortcut execution through real Gio, scoped Flatpak grants, actual application session lifetime, configuration preservation, repair path guards, desktop-folder aliases, KDE progress cleanup, checksum rejection, transactional rollback and complete archive installation/reinstallation. Together with helper tests, all 47 native checks passed. |
| C# Linux adapter under Wine | 22/22 passed. |
| Generated install/recovery suite under Wine | 46 passed. One Windows-only junction-creation check was explicitly skipped; native helper tests separately exercise actual Linux symlinks. |
| Replacement lifecycle under Wine | 62/62 passed, including restoration and recovery fixtures. |
| Assistant under Wine | 37/37 passed using fake credentials and mock network responses. No live API request. |
| Windows regressions | Generated 48, replacement 62, Assistant 37, variants 6, loader 11 and updates 9 passed with the Linux-aware source. Final Linux-only welcome glyph changes also passed the UI-polish/welcome/layout/Help suite and all 22 adapter checks. |
| Loader DLL startup | The pinned 1.10.4 proxy loaded from the fixture folder, forwarded the AGS export, wrote its bootstrap log, and loaded the native plugin with its initialization export under Wine 9. Both default loading and an explicit AGS override passed. |

The loader check does **not** initialize REFramework inside SF6 or render a
fight. It does not establish that an AGS override is required by Proton.
The final welcome adjustment uses readable plain separators and a close button
under Wine. Windows retains its original glyphs. Detailed runtime logs and
screenshots are retained with the source handoff; no personal mod assets are
included in this package.

## Steam setup for the volunteer

Use **Desktop Mode** to install and manage mods. Launch SF6 once normally through
Steam and confirm that the unmodified game works before testing this package.
The Manager runtime is separate from SF6's Proton environment.

REFramework documents this Steam launch option for Proton:

```text
WINEDLLOVERRIDES="dinput8=n,b" %command%
```

Copy it only into an empty Launch Options field. If you already have launch
options or DLL overrides, preserve and merge them. The installer deliberately
leaves Steam's existing options and Proton selection alone. Its **Steam game
setup** menu action shows the same copyable instruction.

Install the required REFramework and SlotsLoader tool mods through Manager,
following its normal prompts. Do not enable another older SlotsLoader copy
through Fluffy at the same time. REFramework's loose file loader must be enabled
when using that replacement method, as on Windows.

References: [REFramework Proton/Linux instructions](https://github.com/praydog/REFramework#protonlinux),
[Bottles CLI](https://docs.usebottles.com/advanced/cli),
[Bottles filesystem access](https://docs.usebottles.com/flatpak/expose-directories).

## First volunteer test

1. Extract the complete archive and open **Install SlotsLoader Manager.desktop**.
   Approve the file manager's trust/run prompt if shown. Follow graphical setup;
   no terminal commands are required. Read `README.md` for prerequisites.
2. Confirm the correct game and library folders. Check internal storage or your
   mounted microSD location, whichever you use. Close and reopen Manager using
   its application-menu entry.
3. Open one familiar ZIP, RAR or 7Z mod and one extracted mod folder. Check text,
   scrolling, dialogs and file selection at your normal screen size.
4. With SF6 closed and the required loader tools installed, install one known
   extra outfit. Launch SF6 through Steam, verify it, close the game, uninstall
   the outfit and check that the original state returns.
5. Repeat with one known replacement using your normal replacement method.
   Start a write while SF6 is running; Manager must refuse it.
6. Close Manager, return to Gaming Mode and verify the installed outfit there.
   Reopen Manager after a reboot and repeat discovery of external storage.
7. Report the Linux/SteamOS version, Bottles version, storage type and exact
   failing operation. Use **Save diagnostic report** from the application-menu
   entry; do not send API keys or private mod archives.

## Known boundaries

- Actual Steam Deck/SteamOS, hardware input, Flatpak Steam and microSD discovery
  have fixture coverage but still need real-device acceptance.
- The actual graphical installer test used Zenity. KDE dialog behavior has
  fixture checks and remains part of the Steam Deck volunteer test. Wine fonts
  and desktop window decorations can differ from Windows.
- Real SF6 rendering, REFramework initialization, SlotsLoader extra outfits and
  replacements under Proton remain volunteer checks.
- Desktop Mode with mouse/trackpad is the supported Manager workflow. Gaming
  Mode/controller-only navigation is not established.
- Assistant keys are session-only on Linux. No Windows encrypted key is imported.
- Use complete Linux packages for Manager updates. Windows-only Manager updates
  are blocked on this build; loader updates retain their normal workflow.
- Runtime repair keeps the old bottle and stable Data. Private Wine-drive storage
  must be moved to stable Linux folders before repair; setup explains this rather
  than rebinding it to an empty runtime.
- If the desktop Trash service cannot safely accept a removed sole copy, Manager
  retains it and reports its location. It never falls back to permanent deletion.
