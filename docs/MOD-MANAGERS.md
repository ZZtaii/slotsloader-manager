# Installing the custom build with an external mod manager

SF6 Slots Loader 1.9.4-custom — manual costume slots
==================================================

This is a custom build based on Wael's supplied 1.9.3-beta source, not an official
upstream release. Costume slots are created ONLY from mods placed inside:
  reframework/costume_mods/<Character>/

Examples:
  reframework/costume_mods/Luke/Leon.zip
  reframework/costume_mods/Luke/Leon/natives/...
Archives (.zip, .7z, .rar), supported PAKs, and unpacked mod folders retain the
existing manual workflow. Bundles/options retain upstream processing.

No mod-manager folder discovery, installed.ini scanning, enabled-archive discovery,
or automatic slots from the game's loose natives tree or installed patch PAKs.
Existing patch PAKs are still read for dependencies and load order. Consequently,
other installed replacements can still influence resolved assets or vanilla
outfits; this is a slot-discovery change, not isolation from all installed mods.
An external mod manager may still be used to INSTALL this loader ZIP.

INSTALL / UPDATE
1. Close SF6. Back up your current loader and generated loader data.
2. Disable the previous loader package before enabling this one in your mod manager.
   Alternatively copy this package into the game folder, preserving paths.
3. Keep REFramework installed (upstream requirement: 1.5.8 or newer).
4. Put desired costume archives/folders in reframework/costume_mods/<Character>.
5. Launch SF6. The changed build ID forces a rebuild. Previously auto-discovered
   slots disappear unless those mods are now explicitly in costume_mods.
   Slot numbers may change; review your selection after updating.
6. Check SF6_CostumeLoader.log and run SF6_CostumeAudit.bat if needed.
   Old mod-manager folder paths in costume_mods/settings.ini are ignored.

Stages retain the existing stage_mods workflow.
The color fallback, shared-body high-resolution textures, cloth diagnostics,
archive handling and variant identification from 1.9.3 are retained.

ROLLBACK
Close SF6, disable this package, restore the previous loader package and your
backed-up loader data. Do not remove your personal costume_mods archives.
Never overwrite an unrelated amd_ags_x64.dll proxy without arranging the
upstream-supported proxy chain and retaining the genuine AMD library.

BUILD / TEST STATUS
The modified Windows x64 proxy was cross-compiled with LLVM-MinGW 20261006.
The native REFramework plugin, genuine AMD DLL and game metadata are copied
unchanged from the supplied 1.9.3-beta package.
Compilation, PE exports/forwarders, dependency inspection, source-scope checks,
and package integrity were checked. This build has NOT been run in Windows or
SF6 here. Please test one manual costume, then an empty costume_mods folder,
with your usual externally installed mods present; only manual entries should
produce extra slots. Confirm colors, shared-body textures and stage loading.

Original project/code credit and licenses remain with their respective authors.
