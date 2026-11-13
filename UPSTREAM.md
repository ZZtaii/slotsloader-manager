# The Slots Loader in this repository

Since 14 Nov 2026 the Slots Loader here is the one of the SF6 Mod Suite line, copied from
[Wael3rd/SF6-ModSuite](https://github.com/Wael3rd/SF6-ModSuite). It replaces the Slotsloader fork (last release
`loader-v1.10.9`, still in this repository's history at that tag).

## Copied from

Wael3rd/SF6-ModSuite `main` at `38a51b8` (after v1.36.0). The paths below are byte for byte that commit:

- the Slots Loader: `costumes/`, `plugin/`, `common/`, `proxy/`, `stages/`, `updater/`, `tools/`, `packaging/`,
  `build.bat`, `TESTING.md`, `THIRD_PARTY.md`;
- what the plugin's build needs from elsewhere in the suite: `core/include/` (the Native Core's API header),
  `colormod/include/` (the Color Mod's entries header) and `colormod/third_party/` (nlohmann/json);
- his docs: `docs/FLUFFY.md`, `docs/MANAGER_CONTRACT.md`, `docs/REFRAMEWORK_COMPATIBILITY.md`,
  `docs/RUNTIME_OPTIONS.md`, `docs/SLOTS_LOADER_1.13_CHANGES.md`.

It includes the Manager-mode work merged there from this side (pull requests #3, #4 and #5): af2ff15, 5ddf0a6,
10ff2e0, d14a8b4, 7a97ac6, bc54835, f0f1722.

## Not copied

- `core/src/`, `colormod/src/`: the Native Core and the Color Mod, which hold LL's REFramework helpers
  (`ll_common.hpp`), used and redistributed by Wael with LL's permission. They are installed from Wael's own zips.
- `report_mod/` (Report a bug): it is built with much of the Recording Manager's code (`crsm/src`: Steam ticket,
  community HTTP). The Manager's loader package leaves Report a bug out, and Manager mode keeps it off.
- `crsm/`, `profile_mod/`, `custom_combos/`: other modules of the suite, not part of the Slots Loader.
- His `README.md`, `CHANGELOG.md`, `COMPATIBILITY.md`, `LICENSE`, `.gitignore`: this repository keeps its own (the
  Manager's Assistant reads `README.md` and `COMPATIBILITY.md` here; `LICENSE` here is MIT, Wael and ZZtaii).

## Building

`build.bat loader`, `build.bat plugin`, `build.bat sorter` (his script, unchanged). The full `build.bat` without a
target, and `build.bat report`, also need sources that are not here (the Native Core, the Color Mod, the Recording
Manager).

## Syncing

Copy the same paths from a newer commit of Wael3rd/SF6-ModSuite, check each path's tree hash against that commit,
and name the commit in the message ("Slots Loader: sync to Wael3rd/SF6-ModSuite <commit>").
