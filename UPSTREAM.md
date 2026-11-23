# The Slots Loader in this repository

Since 14 Nov 2026 the Slots Loader here is the one of the SF6 Mod Suite line, copied from
[Wael3rd/SF6-ModSuite](https://github.com/Wael3rd/SF6-ModSuite). It replaces the Slotsloader fork (last release
`loader-v1.10.9`, still in this repository's history at that tag).

Since 21 Nov 2026 Wael3rd/SF6-ModSuite holds only the releases; the suite's code is in Wael's private repository
`Wael3rd/SF6_ModSuite_Private`, with the same release tags. The sync takes the release (zips, version, notes) from
the public repository and the source from the same tag in the private one.

## Copied from

Synced to: `837a1f0431da8671b25646a44c9e73183a8c2c78` (release `v1.50.0`).

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

[dependencies.json](dependencies.json) names the approved version of each zip, its release, file name, size and
SHA-256. The Manager reads that file from this repository's `main` branch and refuses a file unless size and SHA-256
match. A new version reaches users only once this file names it.

Since Manager 1.0.21 the Slots Loader comes as a slim copy of the official zip: the same files, byte for byte the same
content, without the ones the game doesn't need (listed in [.github/loader-slim-skip.txt](.github/loader-slim-skip.txt):
the cover picture, the game title screen's preview picture, Report a bug, `SF6_SlotsClean.exe`, the audit script). The sync workflow builds it from the official
zip (checked against its SHA-256), publishes it as a pre-release `slotsloader-v<version>` of this repository (never
Latest) and names it in `slots_loader.slim`, beside the official entry. Managers 1.0.21 and later download the slim
copy; older ones keep downloading the official zip of the same entry. A copy of the official zip in a mod library is
still used, and installing it leaves out the same files.

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

The repository needs, once:

- Settings, Actions, General, Workflow permissions, "Allow GitHub Actions to create and approve pull requests";
- the repository secret `SUITE_SOURCE_TOKEN` (Settings, Secrets and variables, Actions): a token of an account with
  read access to `Wael3rd/SF6_ModSuite_Private`. The workflow's own token cannot read another owner's private
  repository, and fine-grained tokens only reach repositories of their own owner, so it is a classic token with the
  `repo` scope. It is used only to read that repository's code and tags; everything else uses the workflow's token.
  When it expires the sync fails at the source step and says so; put a new one in the same secret.

By hand, from the repository root: `SUITE_SOURCE_TOKEN=<token> python .github/scripts/sync_suite.py [--release vX.Y.Z]
[--allow-older]`, then review `git diff --cached` and commit.
