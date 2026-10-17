# Slotsloader Manager assets

- `slotsloader.ico`: Windows ICO with 16, 24, 32, 48, 64, 128 and 256 pixel 32-bit entries (alpha preserved). `build.ps1` uses it as the executable icon and embeds the same bytes as the `SlotsInstaller.AppIcon` resource for form and dialog icons.
- `loader-pin.json`: the Slotsloader release the app installs (tag, version, asset, size, SHA-256). `build.ps1` embeds it; `tools\Package-Release.ps1` rewrites it with the newest published `loader-v*` release.
