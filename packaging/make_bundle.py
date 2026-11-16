"""Build the all-in-one Fluffy package of a release from its separate packages:

  SF6_ModSuite_AllInOne_v<version>.zip   every file of every package zip found in the release folder, one modinfo.ini
                                         (carrying the Slots Loader's SlotsloaderContract), one README.txt (this
                                         package's notice, then each package's README). The Mod Sorter's zip
                                         (make_sorter_package.py) is one of them: SF6_ModSorter.exe lands in the game folder.
                                         --root-file adds a tool of the repository at its root (ROOT_TOOLS: SF6_CrashReport.bat),
                                         which lands in the game folder too; no package ships it, the in-game updater never
                                         installs or updates it (the all-in-one is no asset of the manifest).

Run it after make_suite_packages.py, once every package of the release is in the folder. Two packages shipping the
same path with different contents stop the build. A root file stops it too when it is no ROOT_TOOLS name, a name a
package ships, not tracked by git or changed since the last commit, or (a .bat) not CRLF only or with a BOM: cmd.exe
needs both. It then writes the folder's manifest.json (make_manifest.py): upload it
to the GitHub release with the zips, the in-game updater reads it.

Usage: python make_bundle.py --version <release version> [--release "<folder>"] [--root-file <repo file>]...
"""
import argparse, hashlib, os, re, subprocess, sys, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from make_manifest import write_manifest  # noqa: E402
CRLF = chr(13) + chr(10)
BUNDLE_PREFIX = "SF6_ModSuite_AllInOne_"
# tools put at the all-in-one's root (--root-file), with the README's lines about them
ROOT_TOOLS = {
    "SF6_CrashReport.bat": [
        "SF6_CrashReport.bat (in the game folder): run it right after the game crashed, before starting the game again. It",
        "gathers the crash into one zip (report.txt, REFramework's log, the end of the mods' log, and the crash dump if you agree;",
        "your Windows user name, the PC's name and any SteamID removed), then asks whether to send it to the mods' team. Sent, it",
        "goes with the sign-in that the in-game Report a bug saved on this PC (open Report a bug once first), at most 5 crash",
        "reports a day per account. With no sign-in, or when the server refuses it, the zip stays on your Desktop to be sent by",
        "hand. Nothing is sent before you answer.",
    ],
}

NOTICE = [
    "SF6 Mod Suite v%s (all-in-one)",
    "================================",
    "",
    "Every package of this release in one Fluffy Mod Manager package:",
    "%s",
    "",
    "Install EITHER this package OR the separate ones, never both: two packages shipping the same files conflict in",
    "Fluffy Mod Manager. To switch, disable or remove the one you have first.",
    "",
    "The README of each package follows.",
    "",
]


def modinfo_of(z):
    info = {}
    for line in z.read("modinfo.ini").decode("utf-8", "replace").splitlines():
        m = re.match(r"\s*([A-Za-z]+)\s*=\s*(.*)$", line)
        if m:
            info[m.group(1).lower()] = m.group(2).strip()
    return info


def root_file(path, shipped):
    """(name, bytes) of a --root-file, after its checks (docstring)."""
    name = os.path.basename(path)
    if name not in ROOT_TOOLS:
        sys.exit("--root-file %s: not a ROOT_TOOLS name (%s)" % (name, ", ".join(sorted(ROOT_TOOLS))))
    if name in shipped or name.lower() in ("modinfo.ini", "readme.txt"):
        sys.exit("--root-file %s: a package ships that name" % name)
    full = os.path.abspath(path)
    if not os.path.isfile(full):
        sys.exit("--root-file %s: no such file" % path)
    rel = os.path.relpath(full, REPO).replace(os.sep, "/")
    git = lambda *x: subprocess.run(["git", "-C", REPO] + list(x), capture_output=True).returncode
    if rel.startswith("../") or git("ls-files", "--error-unmatch", "--", rel) != 0:
        sys.exit("--root-file %s: not a file tracked by git" % rel)
    if git("diff", "--quiet", "HEAD", "--", rel) != 0:
        sys.exit("--root-file %s: changed since the last commit: commit it first" % rel)
    data = open(full, "rb").read()
    if name.lower().endswith(".bat"):
        if data.startswith(b"\xef\xbb\xbf"):
            sys.exit("--root-file %s: starts with a BOM (cmd.exe reads it as a command)" % rel)
        if data.count(b"\n") != data.count(b"\r\n"):
            sys.exit("--root-file %s: not CRLF only (cmd.exe needs CRLF)" % rel)
    return name, data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True)
    ap.add_argument("--release", default=os.path.join(REPO, "build", "release"))
    ap.add_argument("--root-file", action="append", default=[], help="a tool of the repository put at the root (ROOT_TOOLS)")
    a = ap.parse_args()

    names = sorted(n for n in os.listdir(a.release)
                   if n.lower().endswith(".zip") and not n.startswith(BUNDLE_PREFIX))
    if not names:
        sys.exit("no package zip in " + a.release)
    files, owner, dirs, packages, readmes, contracts = {}, {}, {}, [], [], {}
    for n in names:
        with zipfile.ZipFile(os.path.join(a.release, n)) as z:
            info = modinfo_of(z)
            label = "%s %s" % (info.get("name", n), info.get("version", "?"))
            packages.append(label)
            if "slotsloadercontract" in info:                # the Slots Loader's, read by the Slotsloader Manager
                contracts.setdefault(info["slotsloadercontract"], n)
            if "README.txt" in z.namelist():
                readmes.append(z.read("README.txt").decode("utf-8").replace(CRLF, "\n").rstrip("\n"))
            for zi in z.infolist():
                e = zi.filename
                if e.endswith("/"):                          # folders a package creates (costume_mods/<Character>/...)
                    dirs.setdefault(e, zi)
                    continue
                if e in ("modinfo.ini", "README.txt"):
                    continue
                data = z.read(e)
                if e in files and files[e] != data:
                    sys.exit("%s: shipped by %s and %s with different contents" % (e, owner[e], n))
                files[e] = data
                owner.setdefault(e, n)

    roots = [root_file(p, files) for p in a.root_file]
    if len(set(n for n, _ in roots)) != len(roots):
        sys.exit("--root-file: a name twice")
    if len(contracts) > 1:
        sys.exit("packages declare different SlotsloaderContract values: " +
                 ", ".join("%s (%s)" % c for c in sorted(contracts.items())))
    notice = NOTICE[:-2]                             # the root files' lines before "The README of each package follows."
    if roots:
        notice += ["At this zip's root, beside the packages (no package ships it; the in-game updater never installs or updates it):"]
        notice += [line for n, _ in roots for line in ROOT_TOOLS[n]] + [""]
    readme = "\n".join(notice + NOTICE[-2:]) % (a.version, "\n".join("  - " + p for p in packages))
    readme = (readme + "\n" + ("\n\n" + "-" * 78 + "\n\n").join(readmes) + "\n").replace("\n", CRLF)
    modinfo = CRLF.join([
        "name=SF6 Mod Suite (all-in-one)",
        "version=%s" % a.version,
        "description=Every SF6 Mod Suite package in one: %s. Install either this or the separate packages, never both."
        % ", ".join(packages),
        "author=Wael",
        "NameAsBundle=SF6 Mod Suite",
    ] + ["SlotsloaderContract=%s" % c for c in contracts] + [""])
    out = os.path.join(a.release, "%sv%s.zip" % (BUNDLE_PREFIX, a.version))
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("modinfo.ini", modinfo)
        z.writestr("README.txt", readme)
        for e in sorted(dirs):
            z.writestr(dirs[e], b"")
        for e in sorted(files):
            z.writestr(e, files[e])
        for n, data in roots:
            z.writestr(n, data)
    print("== SF6 Mod Suite %s (all-in-one): %d files, %d folders from %d packages" % (a.version, len(files), len(dirs), len(names)))
    for n, data in roots:
        print("  at its root: %s (%d bytes, sha256 %s)" % (n, len(data), hashlib.sha256(data).hexdigest()[:16]))
    for p in packages:
        print("  " + p)
    print("zip: %s %d bytes md5 %s" % (out, os.path.getsize(out), hashlib.md5(open(out, "rb").read()).hexdigest()))
    write_manifest(a.release, a.version)


if __name__ == "__main__":
    main()
