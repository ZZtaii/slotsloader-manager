# Slotsloader Manager assets

- `slotsloader.ico`: Windows ICO with 16, 24, 32, 48, 64, 128 and 256 pixel 32-bit entries (alpha preserved). `build.ps1` uses it as the executable icon and embeds the same bytes as the `SlotsInstaller.AppIcon` resource for form and dialog icons.
- The Slotsloader the app installs is no longer an asset here: `build.ps1` embeds the repository's `dependencies.json` (its approved `slots_loader` entry) as `SlotsInstaller.Dependencies.json` (1.0.19).
