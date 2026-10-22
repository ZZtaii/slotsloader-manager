# Linux and Steam Deck implementation plan

The first Linux test release retains the Windows Forms application under Wine, with a graphical setup and a small native launcher. Its target is normal desktop use on Linux and Steam Deck Desktop Mode. Actual SF6 rendering and Steam Deck hardware behavior remain separate acceptance checks.

## Reviewed decisions

- Base the work on Manager 1.0.12, commit `da14348681486627f3760a516970277feedd8746`.
- Use Bottles Flatpak as the graphical setup route, with a dedicated custom environment requesting .NET Framework 4.8. Verify its public CLI and the installed dependency with a probe rather than treating recipe creation as success. An existing system Wine installation is an advanced launch and test option, not a second dependency installer.
- Keep the application and its ordinary `Data` directory in stable per-user storage outside the Wine environment. Repairing the runtime must not remove settings, backups, mods or recovery records.
- A session-lived Python helper exposes authenticated loopback operations for fresh host process checks, game discovery, path validation, folder/URL opening, Steam launch and native trash. It accepts fixed operations, never shell command text. The launcher owns an exclusive native lock and the helper lifetime.
- Preserve Windows behavior through a small C# platform adapter. Direct Wine launches without a valid helper refuse startup before game writes or automatic recovery. Unavailable or uncertain status is never treated as a closed game.
- Resolve explicitly selected root aliases on Linux. Preserve rejection of links inside managed file trees. Missing game storage must not be recreated at an unmounted card path.
- Keep existing Windows 7-Zip integration and bundle its verified files. Do not replace the archive backend in this pass.
- Route Linux updates through the complete package; do not let a Windows-only Manager update remove the adapter. Preserve the separately controlled loader workflow.
- Use session-only Assistant keys until a native keyring path is validated. Do not read, migrate or package existing keys.

Independent plan review moved real runtime and game feasibility ahead of polishing, identified the cross-prefix game guard and update contract as release requirements, and ruled out a new native UI or private Wine distribution for this iteration.

## Beta2 visual repair decisions

The functional beta1 exposed two actual rendering failures: missing Segoe UI and
Consolas silently substituted Tahoma, and Wine's built-in GDI+ produced hard
edges even when the application requested antialiasing. Startup alone therefore
cannot be the visual acceptance gate.

- Bundle static Inter and Liberation Mono, with their original OFL licenses.
  Register them privately with both GDI and GDI+ and use their actual private
  FontFamily objects. Verify resolved families, symbol coverage and smoothing.
  Windows keeps its existing system fonts.
- Request `gdiplus` through Bottles' public dependency recipe. Bottles 67.4 can
  leave the native file in a known nested extraction folder. Accept only the
  pinned AMD64 DLL from that folder or the normal system32 location. Copy it
  atomically beside Manager, never over a prefix system DLL. Never redistribute
  the Microsoft DLL in the package.
- Configure the override through the dedicated bottle's public CLI, then preload
  the verified absolute app-local path before any drawing API. An inherited
  environment variable or app-local file alone did not select the correct module
  in the actual runtime. Verify loaded path/hash and blended antialiased pixels.
- Repaint unsupported native dark chrome while preserving native controls,
  scrolling, hit testing, focus, keyboard input and accessibility. Validate
  painted arrow/thumb positions against native geometry and exercise input.
- Treat recipe 2 migration as candidate creation followed by full verification
  and atomic selection. Keep the old runtime selected on failure, retain Data,
  guard private-drive storage, and resolve launch roots for the chosen prefix.
  Ordinary retries reuse a created candidate; explicit Repair creates a fresh
  one. Check disconnected storage before any lengthy provisioning work.
- Compare actual Linux framebuffers with matched Windows layout references,
  including populated pages, dark dialogs and a true 1280 × 800 display. The
  final package must contain the exact visually tested binaries and fonts.

The requested Astra xhigh review checked the renderer pinning, app-local loading,
two-phase migration, stable-path guards and licensing plan. Actual runtime probes
and screenshot review then resolved the loading and native scrollbar details.

## Implementation sequence

1. Build and hash the unchanged baseline. In a dedicated resource-limited Linux container, establish Wine, .NET 4.8 and a graphical display, then run startup and fixture checks.
2. Implement and independently test the native helper and its fixed protocol. Add Linux discovery, canonical path handling, fresh process checks, missing-storage refusal, locking and recoverable native trash.
3. Add the C# adapter and wire it below GUI and CLI operations, including recovery. Test missing-helper behavior, fake credentials, updater routing and unchanged Windows behavior.
4. Build the graphical launcher and repeatable Bottles recipe. Include install, launch, repair and diagnostics, with actionable errors and no terminal commands in the normal user flow.
5. Run meaningful Windows regressions and Linux fixture lifecycle checks. Inspect the interface at 1280 by 800 and fix demonstrated layout or runtime issues.
6. Package the exact tested build, launchers, recipe, verified dependencies, notices and illustrated or concise testing instructions. Verify archive inventory, checksums and executable permissions. Rehearse installation into fresh per-user storage.
7. Deliver the package with an exact validation report and a short volunteer checklist: install, discover game, install loader, load one known extra outfit and replacement, uninstall, return to original state, repeat after Gaming Mode restart, check internal and microSD storage.

## Helper protocol version 1

The launcher supplies `SLOTSLOADER_PLATFORM=linux`, `SLOTSLOADER_BRIDGE_URL` (loopback HTTP only) and `SLOTSLOADER_BRIDGE_TOKEN` (a fresh random session secret). Every request requires `X-Slotsloader-Token`. Responses contain `protocol: 1` and `ok`; failures include a useful `error`. The token is never written into diagnostics.

| Endpoint | Contract |
| --- | --- |
| `GET /status` | Fresh host scan; `game_running` and `fluffy_running`. Inspection failures are errors, never a stopped state. |
| `GET /discover` | Canonical Windows-mapped `game_root`, `libraries` and `steam_roots` for native or Flatpak Steam. |
| `POST /validate` | `{path, kind}` with kind `game`, `library` or `path`; return canonical Wine path. Game roots must exist and contain the game executable. |
| `POST /contained` | `{path, root}`; inspect native path components, permit actual mount boundaries, reject symbolic links and case collisions. Check registered game directory identity even for a new target. |
| `POST /session` | `started`, `heartbeat`, or `exited` with optional exit code. Own the helper for the actual application lifetime, including a detached Bottles CLI. |
| `POST /open` | `{path, reveal}` or `{url}`. Native folder/file opening, or HTTP(S) URL only. |
| `POST /launch` | Launch the fixed SF6 Steam app ID 1364780. |
| `POST /trash` | Native recoverable deletion. General app/Data access is refused; exact inventoried removed-mod and leftover-PAK copies may be trashed. Failures retain the source or explicitly report an uncertain Trash result. |

## Acceptance gates

| Gate | Evidence |
| --- | --- |
| Runtime | Repeated clean setup and usable interface, dialogs and ZIP/RAR/7Z operations. |
| Writes | Fresh host game guard, preserved recovery records, byte-exact fixture restoration and interruption/missing-storage cases. |
| Setup | Fresh installation and relaunch without terminal commands or undocumented manual edits. |
| Package | Stable Data, coherent versions, no secrets or personal/game assets, verified runtime/dependency checks and archive contents. |
| Real game | A volunteer tests the exact loader/app/runtime combination on Linux or Deck; simulated tests do not substitute for this. |

## Local validation isolation

Use dedicated Docker containers and named runtime volumes within the existing WSL installation. The Wine proof is capped at two CPUs and 3 GiB RAM; the Bottles proof at 1.5 CPUs and 3 GiB. The latter uses a narrow container-only seccomp allowance for Flatpak namespaces and its own D-Bus service. Do not shut down WSL, upgrade the distro, restart shared services, prune Docker resources or stop unrelated processes. Only test-generated files and explicitly named task resources may be cleaned up. The installed Windows application, its Data, supplied libraries and real game are preserved.

## References

- [Bottles CLI](https://docs.usebottles.com/advanced/cli)
- [Bottles .NET 4.8 recipe](https://github.com/bottlesdevs/dependencies/blob/main/Essentials/dotnet48.yml)
- [Bottles folder access](https://docs.usebottles.com/flatpak/expose-directories)
- [REFramework Proton instructions](https://github.com/praydog/REFramework#protonlinux)

The exact SlotsLoader proxy behavior must be tested; REFramework's DLL override alone does not establish that the complete loader works under Proton.
