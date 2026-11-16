# The Slots Loader in this repository

Since 14 Nov 2026 the Slots Loader here is the one of the SF6 Mod Suite line, copied from
[Wael3rd/SF6-ModSuite](https://github.com/Wael3rd/SF6-ModSuite). It replaces the Slotsloader fork (last release
`loader-v1.10.9`, still in this repository's history at that tag).

## Copied from

Synced to: `633a934d566f755c5b7da4e642ae92c2ef635da1` (release `v1.38.0`).

The paths in [.github/upstream-paths.txt](.github/upstream-paths.txt) are byte for byte that commit:

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
  community HTTP). It ships inside the official Slots Loader zip, and Manager mode keeps it off.
- `crsm/`, `profile_mod/`, `custom_combos/`: other modules of the suite, not part of the Slots Loader.
- His `README.md`, `CHANGELOG.md`, `COMPATIBILITY.md`, `LICENSE`, `.gitignore`: this repository keeps its own (the
  Manager's Assistant reads `README.md` and `COMPATIBILITY.md` here; `LICENSE` here is MIT, Wael and ZZtaii).

## What the Manager installs

The Manager installs the official zips from the suite's releases, unchanged. [dependencies.json](dependencies.json)
names the approved version of each, its release, file name, size and SHA-256. The Manager reads that file from this
repository's `main` branch, downloads the file from the suite's release page and refuses it unless size and SHA-256
match. A new version reaches users only once this file names it.

## Building

`build.bat loader`, `build.bat plugin`, `build.bat sorter` (his script, unchanged). The full `build.bat` without a
target, and `build.bat report`, also need sources that are not here (the Native Core, the Color Mod, the Recording
Manager).

## Syncing

The workflow [Sync with the SF6 Mod Suite](.github/workflows/sync-suite.yml) runs every day and on demand
(Actions, **Run workflow**, optionally with a release tag such as `v1.37.0`; an older tag goes back to it). When the
suite has a new release it opens a pull request that copies the paths above from that release's commit, checks each
path's tree hash, updates `dependencies.json` and the "Synced to" line above. Merging the pull request after testing
is the approval. Each pull request is a full copy of one release, so skipping releases is fine: a newer pull request
closes the older open ones. When a file name already approved shows another SHA-256 upstream (a zip re-uploaded under
the same name), the pull request is titled "Re-uploaded" and says to compare the files before merging; until then
Managers refuse the new file. An older release or package is only taken when asked for by tag on **Run workflow**.

The repository needs, once: Settings, Actions, General, Workflow permissions, "Allow GitHub Actions to create and
approve pull requests".

By hand, from the repository root: `python .github/scripts/sync_suite.py [--release vX.Y.Z] [--allow-older]`, then
review `git diff --cached` and commit.
