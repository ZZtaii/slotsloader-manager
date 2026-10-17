# Build the custom version

## Windows
Install Visual Studio C++ Build Tools and run build.bat from this repository.
Use build.bat deps when archive decoder source changes.
Outputs include the costumes-only proxy, native plugin and diagnostic tools.
`tests/build_game_derived.bat` (after build.bat) adds build/game_derived.exe, which
runs the loader's game-derived code on given PAKs for the tests.
The coexistence candidate was built with the installed MSVC x64 toolchain.
Run the native regressions in tests/README.md after building.

## Linux cross-build used for the delivered proxy
Download the official mstorsjo/llvm-mingw 20261006 UCRT Ubuntu 22.04 x86_64 toolchain.
Set LLVM_MINGW_ROOT to its extracted root and run python3 build_cross.py.
Outputs: build-cross/amd_ags_x64.dll and build-cross/SF6_CostumeSlotsNative.dll.
build-compat provides header filename and library aliases for case-sensitive filesystems.
Recreate build-cross after compiler flag or header changes; the cache checks source mtimes only.

## Game-derived loader data (after a game update)
Since 1.9.7 the loader builds the structural files and the missing index rows from
the game's own files at each PAK build (docs/GAME-UPDATE.md), so a game update no
longer requires the steps below. They remain the fallback (the shipped copies are
used when the build from the game fails) and the reference of the tests: after an
update, `tests/game_derived_real_input.py` shows whether the shipped data still
matches the game; regenerate it with the steps below when it does not.

Never index a generated `re_chunk_000.pak.patch_*.pak`: only the base and DLC PAKs
describe vanilla files, and mods installed through a mod manager must be disabled first.

Vanilla index (vanilla_costume_index.tsv):
1. Clone REasy (github.com/seifhassine/REasy; 1.9.6 used commit b9ed75d) into a
   scratch folder, create a venv with `zstandard` and `mmh3` only, and copy the
   SF6 RSZ dump (rszsf6.json) next to the output: REasy writes a `.cache` folder
   beside the dump it reads.
2. `set REASY_PARSER=<clone>` then
   `python costumes/tools/build_vanilla_inventory.py --game <SF6> --out-dir <dir> --registry <dir>/rszsf6.json`
   (base + both DLC PAKs, read-only; writes vanilla_costume_paths.json).
3. `python costumes/tools/inventory_to_tsv.py <dir>/vanilla_costume_paths.json <out>.tsv --compare <previous>.tsv --report <dir>/compare.json`.
   The converter refuses unresolved or missing files. Review the comparison:
   costumes that did not change in the game must be byte-identical.

Static structural files: see costumes/tools/README_STATIC.md (`static_slots.py
verify`, then `build` when a fighter or a version scene is added). Then run
tests/new_costumes_data.py against the new data and the current base PAK.

## Package
packaging/make_package.py accepts --version 1.9.4-custom --game <SF6 directory> --out <zip>
and `--loader-data <dir>` (vanilla_costume_index.tsv + static/) to take the data
from a build folder instead of the installed game.
It expects binaries in build/ plus game-derived metadata and the genuine AMD library.
The coexistence candidate rebuilds both DLLs; packaging reads the genuine AMD
library and loader metadata from --game without changing that installation.
The repository alone does not contain all game-derived packaging inputs.

## Before release
Check AGS exports/forwarders and runtime imports, ZIP integrity, preserved resource hashes,
and check the changed features in game. Keep upstream and custom artifacts distinguishable.
