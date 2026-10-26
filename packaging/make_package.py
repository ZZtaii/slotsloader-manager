# The built files come from this repository (build.bat first: build/amd_ags_x64.dll,
# build/SF6_CostumeSlotsNative.dll), the generated data files and the genuine AMD library from a
# live installation (--game), since neither lives in this repository.
"""Assemble le zip (pour gestionnaire de mods externe) du mod de base "SF6 Slots Loader" (costumes + stages).

Contenu (chemins relatifs a la racine du jeu, modinfo.ini a la racine du zip) :
  amd_ags_x64.dll                       proxy AGS + loaders costumes et stages (build/amd_ags_x64.dll ;
                                        --costumes-only : build/costumes_only/amd_ags_x64.dll, sans stages)
  amd_ags_x64_real.dll                  la vraie bibliotheque AMD AGS du jeu (MIT), que le proxy relaie
  reframework/plugins/SF6_CostumeSlotsNative.dll   (tout le jeu : menus, couleurs, alias en ligne, stages)
  reframework/data/SF6_Costumes_Data/loader/vanilla_costume_index.tsv
  reframework/data/SF6_Costumes_Data/loader/static/*      (6 fichiers structurels + static_meta.json)
  reframework/data/SF6_StageSlots_Data/loader/stage_paths.txt   (du depot, fins de ligne LF)
  reframework/costume_mods/<32 personnages>/, reframework/stage_mods/   (vides, avec leur mode d'emploi)

Usage : python make_package.py [--version 1.0] [--game "<dossier du jeu>"] [--out "<chemin>.zip"] [--costumes-only]
La vraie AGS est prise dans l'ordre : <jeu>/amd_ags_x64_real.dll, puis reframework/_backup/amd_ags_x64_real_orig_20260922.dll.
"""
import argparse, os, sys, zipfile, hashlib

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)                     # racine du depot
DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Street Fighter 6"

def sha(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""): h.update(c)
    return h.hexdigest()[:16]

def is_real_ags(p):
    # la vraie lib AMD importe VCRUNTIME/USER32 et ne contient pas nos chaines ; le proxy contient "amd_ags_x64_real"
    b = open(p, "rb").read()
    return b"amd_ags_x64_real" not in b and b"hardread_core" not in b

CHARACTERS = [
    "Ryu", "Luke", "Kimberly", "Chun-Li", "Manon", "Zangief", "JP",
    "Dhalsim", "Cammy", "Ken", "Dee Jay", "Lily", "A.K.I", "Rashid",
    "Blanka", "Juri", "Marisa", "Guile", "Ed", "E. Honda", "Jamie",
    "Akuma", "Sagat", "M. Bison", "Terry", "Mai", "Elena", "C. Viper",
    "Alex", "Ingrid", "Yasmine", "Arjun",
]

CRLF = chr(13) + chr(10)


STAGE_MODS_TXT = CRLF.join([
    "Drop stage mods here, exactly as downloaded from Nexus: the .zip, .7z or .rar file itself,",
    "a folder with a natives tree, or a .pak. Restart the game. EXPERIMENTAL.",
    "",
    "On the stage select screen of Fighting Ground > Versus, a stage that has mods shows its name",
    "between UP / DOWN symbols: UP / DOWN cycles it through its original look and each mod made for",
    "it. The name and the preview image follow, the VS screen shows the choice, and the battle loads",
    "it. The choice is kept per stage. Each option of a mod gives one variant of the stage it changes.",
    "",
    "The choice is local, and it applies to every load of that stage: training, arcade, replays and",
    "online too (the other player sees their own version). Online play has not been tested. To get",
    "the original back, select it again on the stage select screen.",
    "",
    "To remove a mod, delete its archive or folder: the stage pak is rebuilt at the next launch.",
    "The first launch after adding a large archive takes a few seconds longer: it is unpacked once",
    "into the hidden .cache folder, which can be deleted at any time.",
    "",
    "Stage mods installed with an external mod manager still replace the stage (not variants yet). A mod",
    "that hangs the game on its own hangs it here too. When reporting a problem, send",
    "SF6_StageSlots.log from the game folder and the mod link.",
    "",
])



def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="1.10.5")
    ap.add_argument("--out", default=None)
    ap.add_argument("--game", default=DEFAULT_GAME)
    ap.add_argument("--costumes-only", action="store_true", help="the costume loader alone, without stage slots")
    ap.add_argument("--loader-data", default=None, help="loader data folder (vanilla_costume_index.tsv + static/); default: the game's installed one")
    a = ap.parse_args()
    GAME = a.game                                # racine du jeu
    REF = os.path.join(GAME, "reframework")
    out = a.out or os.path.join(os.path.expanduser("~"), "Documents", "SF6_SlotsLoader_v%s.zip" % a.version)
    stages = False  # This variant never ships stage functionality.

    proxy = os.path.join(REPO, "build", "amd_ags_x64.dll")
    real = None
    for cand in (os.path.join(GAME, "amd_ags_x64_real.dll"), os.path.join(REF, "_backup", "amd_ags_x64_real_orig_20260922.dll")):
        if os.path.exists(cand) and is_real_ags(cand): real = cand; break
    if real is None: sys.exit("vraie amd_ags_x64.dll introuvable")
    if not os.path.exists(proxy) or is_real_ags(proxy): sys.exit(proxy + " manquant ou pas le proxy (build.bat ?)")
    if stages and b"stageslots-" not in open(proxy, "rb").read():
        sys.exit(proxy + " ne contient pas les stages (build.bat ?)")
    plugin = os.path.join(REPO, "build", "SF6_CostumeSlotsNative.dll")
    loader_dir = a.loader_data or os.path.join(REF, "data", "SF6_Costumes_Data", "loader")
    files = [
        (proxy, "amd_ags_x64.dll"),
        (real, "amd_ags_x64_real.dll"),
        (plugin, "reframework/plugins/SF6_CostumeSlotsNative.dll"),
        (os.path.join(loader_dir, "vanilla_costume_index.tsv"), "reframework/data/SF6_Costumes_Data/loader/vanilla_costume_index.tsv"),
    ]
    for n in ("SF6_CostumeAudit.bat", "SF6_CostumeAudit.ps1"):
        files.append((os.path.join(REPO, "costumes", "audit", n), "reframework/costume_mods/" + n))
    for n in sorted(os.listdir(os.path.join(loader_dir, "static"))):
        if n.lower().endswith((".md",)): continue
        files.append((os.path.join(loader_dir, "static", n), "reframework/data/SF6_Costumes_Data/loader/static/" + n))
    for src, _ in files:
        if not os.path.exists(src): sys.exit("manquant : " + src)
    modinfo = (
        "name=Slotsloader Fork\n"
        "version=%s\n"
        "description=Creates EXTRA outfit slots from costume_mods. Drop costume mods as downloaded (.zip, .7z, .rar) in reframework/costume_mods/<Character>/. Costumes only. Install once; then place costume mods in costume_mods and restart the game.\n"
        "author=ZZtaii, Wael3rd\n"
        "NameAsBundle=Slotsloader Fork\n" % a.version)
    with open(os.path.join(HERE, "PACKAGE-README.txt"), encoding="utf-8") as guide:
        readme = ("Slotsloader %s for Street Fighter 6\n\n" % a.version + guide.read()).replace("\r\n", "\n").replace("\n", "\r\n")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("modinfo.ini", modinfo)
        z.writestr("README.txt", readme)
        for src, dst in files:
            z.write(src, dst)
            print("  %-70s %10d  %s" % (dst, os.path.getsize(src), sha(src)))
        if stages:
            # the stage path index as the repository stores it (LF), whatever the checkout did to it
            idx = open(os.path.join(REPO, "stages", "data", "loader", "stage_paths.txt"), "rb").read().replace(b"\r\n", b"\n")
            z.writestr("reframework/data/SF6_StageSlots_Data/loader/stage_paths.txt", idx)
            print("  %-70s %10d  %s" % ("reframework/data/SF6_StageSlots_Data/loader/stage_paths.txt", len(idx), hashlib.sha256(idx).hexdigest()[:16]))
            z.writestr("reframework/stage_mods/README.txt", STAGE_MODS_TXT)
            print("  %-70s %10s  %s" % ("reframework/stage_mods/", "-", "structure"))
        # structure vide livree telle quelle : un dossier par personnage + son mode d'emploi
        z.writestr("reframework/costume_mods/README.txt", readme)
        for name in CHARACTERS:
            zi = zipfile.ZipInfo("reframework/costume_mods/%s/" % name)
            zi.external_attr = (0o40755 << 16) | 0x10   # repertoire
            z.writestr(zi, b"")
        print("  %-70s %10s  %s" % ("reframework/costume_mods/<%d personnages>/" % len(CHARACTERS), "-", "structure"))
    print("zip :", out, os.path.getsize(out), "octets")

if __name__ == "__main__":
    main()
