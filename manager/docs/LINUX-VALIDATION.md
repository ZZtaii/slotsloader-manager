# Linux / Steam Deck test build validation

Current build: **SlotsLoader Manager 1.0.13-linux-beta3**, 27 October 2026.
It merges official Manager 1.0.13 (`8961d587dd05e219aceb201749473cb704fc67d6`)
and retains the polished Bottles interface. The user confirmed beta2 worked;
their hardware and individual gameplay coverage were not enumerated.

Beta3 adds daily, consent-based complete Linux package updates and a Settings
button. Release discovery uses a separate Linux tag/asset channel; downloads
require GitHub's exact asset size and SHA-256 digest. Extraction rejects unsafe
paths, links, duplicate names and private Data. Installation retains the existing
transactional rollback and keeps Data and the compatibility runtime.

The ZIP contains the GUI, required helpers, dependencies, fonts and licenses.
Development reports and the CLI stay outside it. Beta2 users must install beta3
once manually before automatic checks can offer subsequent Linux releases.

The validation records below preserve beta2's rendering/runtime baseline;
they are not a claim that its earlier binaries are the beta3 release.

## Beta3 checks

- Final GUI SHA-256: `dc4f0f934a7639375e2bbccb07003bbfe2797e4edabb58931e9a650e8e1b6ee1`.
- Final ZIP SHA-256: `d540b56438a6a4bdd9701edf0b2b2b89362714c53fc69effb888fcde6b57468c`.
- Native Python suite: 73 cases; 71 passed with system Python and both actual
  Gio shortcut roundtrips passed with Bottles' existing Python interpreter.
  Includes updater channel selection, consent/preferences, offline behavior,
  hash/size checks, hostile ZIP entries, full-package preservation and rollback.
- Actual Bottles filter suite passed, including 1.0.13 selection behavior,
  sort controls at the minimum column width, and filesystem/cache persistence.
  Long library names may deliberately use ellipsis while retaining their full
  accessible path; the fit test does not claim every label fits in full.
- Nine final-binary framebuffer captures cover Library, scrolling, dropdown,
  Installed, Settings, Updates, Assistant, confirmation and resize. The 1280 x 800
  window captures use a larger isolated test display; beta2's separate true
  1280 x 800 dropdown-boundary proof remains the unchanged control baseline.
- The real Settings update button reached the authenticated host bridge and
  closed the idle GUI cleanly. The strict rendering probe passed with private
  fonts, pinned native GDI+, smoothing and 273 antialiased pixels.
- Exact final ZIP installation, reinstallation and normal relaunch passed in
  the actual Bottles runtime. Each preserved fixture settings, recovery sentinel,
  selected runtime, app-local renderer and both prefix system GDI+ DLLs.
- Windows adapter (22), controls (11), fields, UI polish, filters and updater
  suites passed. The final subsequent edits only correct Linux test expectations.
- Requested independent review at medium reasoning found no blocking issues in
  updates, locking/restart, archive handling, rollback, preservation or packaging.

## Exact build and environment

- GUI SHA-256: `c9e7c92f6c22295f668afe0d6381a4a85bacd5256ab602779712efb44f5a0175`.
- CLI SHA-256: `7eff95ad1e99453f63ef9f1d0ee2869e9bee2ab636e53ff9ca7fdfa466726e4c`.
- Manager baseline: `da14348681486627f3760a516970277feedd8746`, with Linux
  integration and beta2 rendering changes on `codex/linux-steamdeck`.
- Actual graphical runtime: Bottles **67.4** Flatpak, **soda-11.0-10**,
  Microsoft **.NET Framework 4.8** (release `528049`), x64, no Wine Mono.
- Fonts: unmodified **Inter 4.1** and **Liberation Mono 2.1.5**, registered
  privately for Manager. Windows retains Segoe UI and Consolas.
- Graphics: pinned native x64 GDI+ downloaded by Bottles, SHA-256
  `980ea189929d95eb36e35980fff0c81f7b78de9422771fde8f4ac7a779f5bd89`.
  Setup verifies size, hash and architecture before copying it beside the EXE.
  It is not redistributed in this archive. Prefix system DLLs are not replaced.
  Startup verifies the actual loaded module and antialiasing.
- Bundled SlotsLoader **1.10.4** archive SHA-256:
  `7529732062081c5cd886474aefb2a85ab6cb31b113af65aae80cff3f9d6d60de`.
- Bundled archive backend: the pinned **7-Zip 26.00 x64** distribution.

Testing used dedicated, resource-limited containers inside the existing WSL2
installation. All games, libraries, artwork, recovery records and credentials
were generated fixtures. No installed app Data, real game, personal library or
actual API key was used. Other WSL processes and containers were left alone.

## Beta2 validation

| Area | Result |
| --- | --- |
| Actual rendering probe | Exact final binaries passed in Bottles: Inter and Liberation Mono resolved in GDI/GDI+, required symbols present, smoothing enabled, the pinned application-local renderer loaded, and 273 blended pixels in the antialiasing check. |
| Screenshot review | Eight actual Linux framebuffer captures cover Library, scrolled Library, character dropdown, managed Installed outfits, Settings, Assistant, confirmation dialog and a larger Library window. Independent comparison against matched Windows references found no blocking client-rendering defects. |
| Display sizes | Matched client areas of 1272 × 766 and 1592 × 966 at 96 DPI. A separate real 1280 × 800 test display confirms that the character dropdown opens upward wholly within the screen. Linux captures are X11 framebuffers; Windows references are hidden WinForms renders. TEST artwork is synthetic. |
| Native controls | All 11 checks passed under Bottles and Windows: thumb/arrow paint geometry, page/line/wheel/key scrolling, pointer dragging, checkbox keyboard/accessibility/label fit, resizing, dropdowns, dynamic controls and handle recreation. |
| Windows final build | All 22 Linux-adapter checks, text-field checks at 288 DPI, and the UI-polish/welcome/Help/layout suite passed. Earlier beta2 Assistant UI, replacement UI and filter checks also passed. |
| Native launcher/helper | All 62 cases covered: 60 passed with the container's Python, and the two actual Gio desktop-entry roundtrips passed separately with Bottles' existing PyGObject interpreter. Includes package updates, runtime failure/retry/repair, strict rendering checks, mount preflight and native safety checks. |
| Runtime setup and migration | Real beta1-to-recipe2 migration created a fresh bottle through the public custom recipe, installing .NET 4.8 and GDI+, and completed in 444 seconds. The old runtime stayed selected and all fixture Data remained byte-identical until the full probe passed. Settings and the preservation sentinel also survived GUI startup/close. Both old and new prefixes retained their system DLLs. |
| Package | Manifest, executable bits, pinned dependencies, font files and licenses verified. Complete archive installation and reinstallation preserved recovery Data; updates also preserve the runtime-managed renderer. The delivery handoff records the final archive hash and final repetition of the package checks. |

The screenshot comparison verifies content layout, readable text, complete
labels, smooth icons and rounded buttons, dark scrollbars and dialogs. It does
not claim pixel-identical Windows fonts or desktop window decorations.

## Functional baseline retained

The preceding beta1 integration exercised the same install/recovery engines:

- Wine: Linux adapter **22**, generated install/recovery **46** (one
  Windows-only junction check skipped), replacement lifecycle **62**, Assistant
  **37**, all passed. Linux helper tests separately exercise real symlinks.
- Windows: generated **48**, replacement **62**, Assistant **37**, variants **6**,
  loader **11**, updates **9**, all passed.
- Full graphical installer with real Zenity folder dialogs, welcome, runtime
  check, Steam guidance, application relaunch and diagnostic dialog passed.
- Bundled 7-Zip extracted ZIP, RAR and 7Z fixtures byte-for-byte under Bottles.
- SlotsLoader 1.10.4's proxy loaded, forwarded its AGS export, wrote its bootstrap
  log and loaded its native plugin under Wine 9 with default DLL loading.

These are retained baseline results, not claims that every engine suite was
repeated for beta2. The loader check does not initialize REFramework inside SF6
or render a fight, and does not establish a need for an additional AGS override.

## First volunteer test

1. Use Steam Deck **Desktop Mode**. Extract the complete archive and open
   **Install SlotsLoader Manager.desktop**. Follow graphical setup in `README.md`;
   normal setup requires no terminal. First setup needs internet and several
   minutes. The graphics dependency downloads about 947 MB.
2. Confirm game and library folders. Close and reopen Manager through its
   application-menu entry. Check internal storage or mounted microSD as used.
3. Open a familiar ZIP, RAR or 7Z mod and an extracted folder. Check text,
   scrolling, dropdowns and dialogs at your normal display size.
4. Before game changes, launch unmodified SF6 normally through Steam. Install
   REFramework and SlotsLoader tool mods through Manager following its prompts.
   Do not enable an older SlotsLoader copy through another manager as well.
5. With SF6 closed, install one known extra outfit; verify it in-game, close SF6,
   uninstall and verify restoration. Repeat with one known replacement. Enable
   REFramework's loose file loader when using that replacement method.
6. Start a write while SF6 is running: Manager must refuse it. Return to Gaming
   Mode and verify the installed mod there. Recheck discovery after a reboot.
7. Report Linux/SteamOS and Bottles versions, storage type and the exact failing
   operation. Use **Save diagnostic report** from the application-menu entry;
   do not send API keys or private mod archives.

REFramework documents this Proton launch option:

```text
WINEDLLOVERRIDES="dinput8=n,b" %command%
```

Copy it only into an empty Steam Launch Options field. Preserve and merge any
existing options or overrides. Setup leaves Steam options and Proton selection
alone; its **Steam game setup** action presents the same instruction.

References: [REFramework Proton/Linux instructions](https://github.com/praydog/REFramework#protonlinux),
[Bottles CLI](https://docs.usebottles.com/advanced/cli),
[Bottles filesystem access](https://docs.usebottles.com/flatpak/expose-directories).

## Remaining acceptance boundaries

- Physical Steam Deck/SteamOS, KDE dialogs, Flatpak Steam, microSD behavior and
  real SF6 gameplay under Proton still need the volunteer's device.
- The first welcome card fits and is readable. Its translucent backdrop became
  fully black on the uncomposited test desktop; KDE compositing was not tested.
- Desktop Mode with mouse/trackpad is the supported Manager workflow.
  Controller-only use and running Manager itself in Gaming Mode are unverified.
- Assistant keys are session-only on Linux; Windows encrypted keys are not imported.
- Update through complete Linux packages. Windows-only Manager updates are blocked.
- Runtime migration/repair retains old bottles and Data. Existing private Wine
  drive storage must be moved to stable Linux folders before switching runtimes.
- Failed Trash removal retains the sole copy; it never becomes permanent deletion.
