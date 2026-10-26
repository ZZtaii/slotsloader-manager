#!/usr/bin/env python3
"""SlotsLoader Manager's Linux installer and session launcher (Python 3.10+).

Only the standard library and the desktop's kdialog or zenity are required.
Bottles is invoked through its public CLI; no global Flatpak overrides are used.
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import time
import uuid

TITLE = "SlotsLoader Manager"
BOTTLES = "com.usebottles.bottles"
EXE = "Slotsloader Manager.exe"
STATE_VERSION = 1
RUNTIME_RECIPE = 2
RENDERER_SHA256 = "980ea189929d95eb36e35980fff0c81f7b78de9422771fde8f4ac7a779f5bd89"
RENDERER_SIZE = 2165248
RENDERER_SOURCES = (
    "drive_c/windows/system32/amd64_microsoft.windows.gdiplus_6595b64144ccf1df_1.1.7601.17514_none_2b24536c71ed437a/gdiplus.dll",
    "drive_c/windows/system32/gdiplus.dll",
)
RENDERER_BACKUPS = 3
RENDERER_BACKUP_LIMIT = 16 * 1024 * 1024
START_TIMEOUT = 120
HEARTBEAT_TIMEOUT = 45
GAME_LAUNCH_OPTIONS = 'WINEDLLOVERRIDES="dinput8=n,b" %command%'


class SetupError(Exception):
    pass


class Relaunch(Exception):
    def __init__(self, root, arguments):
        self.root, self.arguments = root, arguments


def data_home():
    return Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share"))).expanduser()


def install_root():
    # Resolve desktop/home aliases before Wine sees the application path. The
    # application's own folder and descendants still reject symbolic links.
    return data_home().resolve() / "slotsloader-manager"


def session_lock_path():
    runtime = os.environ.get("XDG_RUNTIME_DIR")
    if runtime and Path(runtime).is_dir() and Path(runtime).stat().st_uid == os.getuid():
        return Path(runtime) / "slotsloader-manager.lock"
    return Path(tempfile.gettempdir()) / ("slotsloader-manager-" + str(os.getuid()) + ".lock")


def native_path(value, prefix=None):
    value = os.fspath(value)
    if re.match(r"^[A-Za-z]:[\\/]", value):
        if value[0].lower() != "z" and prefix is None:
            raise SetupError("This folder uses a Wine drive whose runtime is unknown. Choose the Linux folder through the Z: drive or run folder setup again.")
        from host import native_path as host_path
        return host_path(value, prefix).resolve()
    path = Path(value).expanduser()
    if not path.is_absolute():
        raise SetupError("Choose an absolute Linux folder or its Z: drive path. Relative paths are not supported.")
    return path.resolve()


def wine_path(path):
    return "Z:" + str(Path(path).resolve()).replace("/", "\\")


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with open(temp, "x", encoding="utf-8") as f:
            os.chmod(temp, 0o600)
            json.dump(value, f, indent=2)
            f.write("\n")
            f.flush()
            os.fsync(f.fileno())
        os.replace(temp, path)
    finally:
        temp.unlink(missing_ok=True)


def read_json(path, default=None):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except FileNotFoundError:
        return default
    except (ValueError, UnicodeError) as exc:
        raise SetupError("A setup file is damaged. Run the installer again to repair it.") from exc


def safe_member(name):
    p = PurePosixPath(name)
    if (not name or "\\" in name or p.is_absolute() or any(x in ("..", ".", "") for x in name.split("/"))
            or any(ord(c) < 32 for c in name) or ":" in name):
        raise SetupError("The package contains an unsafe filename.")
    if any(x.casefold() == "data" for x in p.parts):
        raise SetupError("A Linux package must never contain user Data.")
    return p


def checked_target(root, name):
    member = safe_member(name)
    target = Path(root).joinpath(*member.parts)
    current = Path(root)
    if current.is_symlink():
        raise SetupError("The installation folder is a symbolic link. Choose a normal local folder.")
    for part in member.parts:
        current /= part
        if current.is_symlink():
            raise SetupError("An application file is a symbolic link; setup has left it untouched.")
    return target


def verify_package(source):
    source = Path(source)
    manifest = read_json(source / "package.json")
    if not isinstance(manifest, dict) or manifest.get("format") != 1 or not isinstance(manifest.get("files"), dict):
        raise SetupError("This is not a complete Linux package. Extract the entire downloaded archive first.")
    required = {"app/" + EXE, "app/" + EXE + ".config", "linux/launcher.py", "linux/host.py", "linux/bottle.yml"}
    if not required.issubset(manifest["files"]):
        raise SetupError("The Linux package is missing required application files.")
    for name, digest in manifest["files"].items():
        if PurePosixPath(name).name.casefold() == "gdiplus.dll":
            raise SetupError("A Linux package must not distribute the compatibility runtime's graphics DLL.")
        if not (name.startswith(("app/", "linux/")) or name in ("install.sh", "Install SlotsLoader Manager.desktop", "README.md", "VALIDATION.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "SLOTSLOADER-LICENSE.txt", "SLOTSLOADER-THIRD-PARTY.md", "THIRD-PARTY-NOTICES.txt")):
            raise SetupError("The package contains an unexpected file; setup has not changed your installation.")
        path = checked_target(source, name)
        if not re.fullmatch(r"[0-9a-fA-F]{64}", str(digest)) or not path.is_file():
            raise SetupError("The package is incomplete. Download and extract it again.")
        if hashlib.sha256(path.read_bytes()).hexdigest().lower() != digest.lower():
            raise SetupError("A package file failed its checksum. Download and extract it again.")
    return manifest


def read_regular(path, limit):
    """Bounded reads of an explicitly checked runtime file; never follow a leaf link."""
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
    with os.fdopen(os.open(path, flags), "rb") as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size > limit:
            raise SetupError("A runtime file is not a regular file of the expected size; it has been kept untouched.")
        content = stream.read(limit + 1)
    if len(content) > limit:
        raise SetupError("A runtime file grew unexpectedly; it has been kept untouched.")
    return content


def renderer_valid(content):
    if len(content) != RENDERER_SIZE or hashlib.sha256(content).hexdigest() != RENDERER_SHA256:
        return False
    if len(content) < 64 or content[:2] != b"MZ":
        return False
    pe = struct.unpack_from("<I", content, 0x3c)[0]
    return (pe <= len(content) - 26 and content[pe:pe + 4] == b"PE\0\0"
            and struct.unpack_from("<H", content, pe + 4)[0] == 0x8664
            and struct.unpack_from("<H", content, pe + 24)[0] == 0x20b)


def atomic_bytes(path, content):
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with open(temporary, "xb") as stream:
            os.chmod(temporary, 0o600)
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        if path.is_symlink():
            raise SetupError("A runtime destination became a symbolic link; it has been kept untouched.")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def prepare_renderer(root, prefix):
    """Use only the pinned native x64 DLL installed by Bottles in this prefix."""
    root, prefix = Path(root), Path(prefix)
    if prefix.is_symlink() or not prefix.is_dir():
        raise SetupError("The selected compatibility runtime is unavailable or is a symbolic link.")
    content = None
    for name in RENDERER_SOURCES:
        source = checked_target(prefix, name)
        if source.exists():
            try:
                candidate = read_regular(source, RENDERER_SIZE)
            except SetupError:
                continue
            if renderer_valid(candidate):
                content = candidate
                break
    if content is None:
        raise SetupError("The selected runtime does not contain the verified native graphics renderer. Use Repair compatibility runtime in the Linux setup. Advanced Wine users must install Bottles' gdiplus dependency into their dedicated prefix first; the app does not download DLLs directly.")
    target = checked_target(root, "app/gdiplus.dll")
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        previous = read_regular(target, RENDERER_BACKUP_LIMIT)
        if renderer_valid(previous):
            return target
        backup_dir = checked_target(root, "runtime-backups")
        backup_dir.mkdir(mode=0o700, exist_ok=True)
        name = "gdiplus-" + hashlib.sha256(previous).hexdigest() + ".dll"
        backup = checked_target(backup_dir, name)
        if backup.exists():
            if read_regular(backup, RENDERER_BACKUP_LIMIT) != previous:
                raise SetupError("The saved graphics runtime backup differs from the original. Keep runtime-backups for recovery before trying again.")
        else:
            if len(list(backup_dir.iterdir())) >= RENDERER_BACKUPS:
                raise SetupError("The graphics runtime backup folder is full. Keep its files somewhere safe, then empty runtime-backups and try again. The current DLL has been kept.")
            atomic_bytes(backup, previous)
    atomic_bytes(target, content)
    return target


def recover_update(root):
    """Roll back only inventoried program files. Data is never moved or replaced."""
    root = Path(root)
    journal = root / "update-journal.json"
    pending = read_json(journal)
    if not pending:
        return
    transaction = pending.get("transaction", "")
    if not re.fullmatch(r"update-[0-9a-f]{32}", transaction):
        raise SetupError("The update recovery record is invalid; keep this folder for support.")
    backup = checked_target(root, transaction)
    for item in reversed(pending.get("files", [])):
        name, existed = item["name"], item["existed"]
        target = checked_target(root, name)
        saved = checked_target(backup, name)
        if saved.is_file():
            target.parent.mkdir(parents=True, exist_ok=True)
            os.replace(saved, target)
        elif not existed and target.is_file():
            target.unlink()
    journal.unlink()
    if backup.is_dir():
        shutil.rmtree(backup)


def install_package(source, root):
    source, root = Path(source).resolve(), Path(root)
    manifest = verify_package(source)
    root.mkdir(parents=True, exist_ok=True)
    recover_update(root)
    if source == root.resolve():
        return manifest
    names = sorted(manifest["files"])
    names += ["package.json"]
    transaction = "update-" + uuid.uuid4().hex
    backup = checked_target(root, transaction)
    backup.mkdir()
    entries = []
    # Backup before replacing anything; the durable journal makes interruptions recoverable.
    for name in names:
        target = checked_target(root, name)
        if target.exists() and not target.is_file():
            raise SetupError("An application file conflicts with an existing folder. Nothing was replaced.")
        entries.append({"name": name, "existed": target.exists()})
        if target.is_file():
            saved = checked_target(backup, name)
            saved.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(target, saved)
    atomic_json(root / "update-journal.json", {"transaction": transaction, "files": entries})
    try:
        for name in names:
            target = checked_target(root, name)
            target.parent.mkdir(parents=True, exist_ok=True)
            temporary = target.with_name(target.name + ".incoming")
            if temporary.is_symlink():
                raise SetupError("Setup found an unsafe temporary file.")
            shutil.copyfile(checked_target(source, name), temporary)
            os.chmod(temporary, 0o755 if name.endswith((".sh", ".desktop")) else 0o644)
            os.replace(temporary, target)
        (root / "update-journal.json").unlink()
        shutil.rmtree(backup)
    except Exception:
        recover_update(root)
        raise
    return manifest


def desktop_quote(value):
    # Desktop Exec is not shell syntax. Escape its reserved double-quote characters.
    value = str(value).replace("%", "%%")
    for char in ("\\", '"', "`", "$"):
        value = value.replace(char, "\\" + char)
    # Desktop-entry string unescaping happens before Exec argument unescaping.
    return '"' + value.replace("\\", "\\\\") + '"'


def install_shortcut(root):
    launcher = Path(root) / "linux/launcher.py"
    command = "/usr/bin/env python3 " + desktop_quote(launcher)
    icon = Path(root) / "linux/slotsloader.ico"
    text = ("[Desktop Entry]\nType=Application\nVersion=1.0\nName=SlotsLoader Manager\n"
            "Comment=Manage Street Fighter 6 mods\nTerminal=false\nCategories=Game;Utility;\n"
            "StartupNotify=true\nExec=" + command + "\nIcon=" + str(icon) + "\n"
            "Actions=Update;GameSetup;Setup;Repair;Diagnostics;\n\n[Desktop Action Update]\nName=Check for Linux app updates\nExec="
            + command + " --update\n\n[Desktop Action GameSetup]\nName=Steam game setup\nExec="
            + command + " --game-setup\n\n[Desktop Action Setup]\nName=Choose game and mod folders\nExec="
            + command + " --configure\n\n[Desktop Action Repair]\nName=Repair compatibility runtime\nExec="
            + command + " --repair\n\n[Desktop Action Diagnostics]\nName=Save diagnostic report\nExec="
            + command + " --diagnostics\n")
    applications = data_home() / "applications"
    applications.mkdir(parents=True, exist_ok=True)
    destination = applications / "slotsloader-manager.desktop"
    destination.write_text(text, encoding="utf-8")
    os.chmod(destination, 0o755)
    return destination


class Dialog:
    def __init__(self, headless=False):
        self.tool = None if headless else (shutil.which("kdialog") or shutil.which("zenity"))
        self.headless = headless
        if not self.tool and not headless:
            raise SetupError("A graphical desktop with kdialog or zenity is required. On Steam Deck, use Desktop Mode.")

    @property
    def kde(self):
        return self.tool and Path(self.tool).name == "kdialog"

    def call(self, kde, gtk):
        return subprocess.run([self.tool, "--title", TITLE] + (kde if self.kde else gtk),
                              text=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)

    def info(self, text, error=False):
        if self.headless:
            print(text, file=sys.stderr if error else sys.stdout)
            return
        self.call(["--error" if error else "--msgbox", text],
                  ["--error" if error else "--info", "--no-markup", "--width=520", "--text", text])

    def confirm(self, text):
        if self.headless:
            return False
        return self.call(["--yesno", text], ["--question", "--no-markup", "--width=520", "--text", text]).returncode == 0

    def folder(self, text, initial):
        if self.headless:
            return None
        if self.kde:
            result = self.call(["--getexistingdirectory", str(initial), "--title", text], [])
        else:
            result = self.call([], ["--file-selection", "--directory", "--title", text, "--filename", str(initial) + "/"])
        return native_path(result.stdout.strip()) if result.returncode == 0 and result.stdout.strip() else None

    def copy_text(self, message, value):
        if self.headless:
            print(message + "\n" + value)
            return
        self.call(["--inputbox", message, value],
                  ["--entry", "--width=620", "--text", message, "--entry-text", value])

    @contextlib.contextmanager
    def busy(self, text):
        process, dbus = None, None
        try:
            if not self.headless and shutil.which("zenity"):
                process = subprocess.Popen(["zenity", "--progress", "--pulsate", "--auto-close", "--no-cancel",
                                            "--title", TITLE, "--text", text, "--width=520"],
                                           stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            elif self.kde:
                qdbus = shutil.which("qdbus6") or shutil.which("qdbus")
                # A kdialog progress window outlives its command. Only create it
                # when its matching DBus client is available to close it later.
                if qdbus:
                    result = self.call(["--progressbar", text, "0"], [])
                    parts = result.stdout.strip().split()
                    if len(parts) == 2:
                        dbus = [qdbus] + parts
            yield
        finally:
            if process:
                try:
                    process.stdin.write(b"100\n")
                    process.stdin.close()
                    process.wait(timeout=3)
                except (OSError, subprocess.TimeoutExpired):
                    process.terminate()  # Only our own progress dialog.
            if dbus:
                subprocess.run(dbus + ["close"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def flatpak_base(root, roots=(), environment=None):
    command = ["flatpak", "run", "--share=network", "--filesystem=" + str(Path(root).resolve())]
    for path in sorted({str(native_path(x)) for x in roots if x}):
        if ":" in path or "\n" in path:
            raise SetupError("A selected folder contains characters Flatpak cannot grant safely. Choose another folder.")
        command.append("--filesystem=" + path)
    for key, value in (environment or {}).items():
        command.append("--env=" + key + "=" + value)
    return command + ["--command=bottles-cli", BOTTLES]


def run_checked(command, dialog, description, timeout=1800):
    with dialog.busy(description):
        try:
            result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            raise SetupError("This step took too long. Close any installer windows from this setup, then try Repair.") from exc
    if result.returncode != 0:
        # Do not put arbitrary runtime output (which may contain paths or secrets) in dialogs or diagnostics.
        raise SetupError(description + " did not finish. Open Bottles to complete its first-run setup, then choose Repair.")
    return result.stdout


def parse_cli_json(output):
    """Bottles versions may print status lines before their public JSON result."""
    decoder = json.JSONDecoder()
    for match in re.finditer(r"[\[{]", output):
        try:
            value, end = decoder.raw_decode(output[match.start():])
            if isinstance(value, (dict, list)):
                return value
        except ValueError:
            continue
    raise SetupError("Bottles did not return its runtime details. Finish its welcome setup, then choose Repair.")


def bottle_prefix(root, bottle, dialog):
    result = parse_cli_json(run_checked(flatpak_base(root) + ["-j", "list", "bottles"], dialog,
                                       "Locating the dedicated compatibility runtime", timeout=120))
    item = result.get(bottle) if isinstance(result, dict) else next((x for x in result if x.get("Name") == bottle), None)
    if not isinstance(item, dict):
        raise SetupError("Bottles did not finish creating the dedicated runtime. Choose Repair compatibility runtime.")
    path = item.get("Path", "")
    if not path:
        raise SetupError("Bottles returned an incomplete runtime configuration. Choose Repair compatibility runtime.")
    if not Path(path).is_absolute():
        base = run_checked(flatpak_base(root) + ["info", "bottles-path"], dialog,
                           "Locating compatibility runtime storage", timeout=120).strip()
        if not Path(base).is_absolute() or "\n" in base:
            raise SetupError("Bottles could not report its runtime storage location.")
        path = str(Path(base) / path)
    resolved = Path(path).resolve()
    if not (resolved / "drive_c").is_dir():
        raise SetupError("The dedicated runtime is unavailable. Reconnect its drive or choose Repair compatibility runtime.")
    return str(resolved)


def ensure_bottles(dialog):
    present = shutil.which("flatpak") and subprocess.run(["flatpak", "info", BOTTLES],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
    if present:
        return
    if dialog.confirm("SlotsLoader Manager uses Bottles to run on Linux. Install Bottles from your software centre, open it once to finish setup, then run SlotsLoader Manager again.\n\nOpen the official Bottles installation page?"):
        subprocess.Popen(["xdg-open", "https://flathub.org/apps/com.usebottles.bottles"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    raise SetupError("Install Bottles and finish its welcome screen, then reopen SlotsLoader Manager. Your files have been kept.")


def game_setup(dialog):
    dialog.info("SF6 runs through Steam's own Proton environment.\n\n"
                "1. In Steam, open Street Fighter 6 → Properties → General.\n"
                "2. For REFramework, add its native DLL override to Launch Options. The next window lets you copy it.\n"
                "3. Install the required tool mod through Manager, then launch SF6 from Steam.\n\n"
                "If Launch Options already contains anything, keep it. Merge the DLL override into your existing command instead of replacing it. This setup does not change Steam for you.\n\n"
                "The bundled SlotsLoader 1.10.4 proxy and plugin passed a Wine bootstrap check. SF6 gameplay and Steam Deck hardware still need tester confirmation; see VALIDATION.md.")
    dialog.copy_text("REFramework launch options. Copy this only into an empty Launch Options field; preserve and merge any existing options.", GAME_LAUNCH_OPTIONS)


def configure(root, state, dialog):
    from host import discover_installations
    discovered = discover_installations()
    game = state.get("game_root") or discovered.get("game_root")
    if game and Path(game).is_dir():
        if not dialog.confirm("Use this Street Fighter 6 folder?\n\n" + str(game)):
            game = None
    if not game:
        game = dialog.folder("Choose the Street Fighter 6 game folder", Path.home())
        if not game:
            raise SetupError("Folder setup was cancelled. Your previous settings have been kept.")
    if game:
        game = native_path(game)
        if not game.is_dir() or not (game / "StreetFighter6.exe").is_file():
            raise SetupError("That folder does not contain StreetFighter6.exe. Choose the game's installation folder.")
    default_library = Path(state.get("library_root") or root / "mods")
    if "library_root" not in state:
        default_library.mkdir(parents=True, exist_ok=True)
    elif not default_library.is_dir():
        default_library = root / "mods"
        default_library.mkdir(parents=True, exist_ok=True)
    library = default_library
    if not dialog.confirm("Use this folder for your mod library?\n\n" + str(default_library) + "\n\nChoose No to select an existing library."):
        library = dialog.folder("Choose your mod library folder", Path.home())
        if not library:
            raise SetupError("Folder setup was cancelled. Run SlotsLoader Manager again when ready.")
    if not Path(library).is_dir():
        raise SetupError("That mod library is unavailable. Mount its drive and choose an existing folder.")
    previous_library = state.get("library_root")
    updated = dict(state, game_root=str(game), library_root=str(native_path(library)), configured=True)
    settings_path = root / "app/Data/settings.json"
    if settings_path.is_symlink() or settings_path.parent.is_symlink():
        raise SetupError("Manager settings use a symbolic link. Setup has kept them unchanged; use an ordinary app Data folder.")
    settings = read_json(settings_path, {})
    if not isinstance(settings, dict):
        raise SetupError("Manager settings are unreadable; your existing settings have been kept.")
    libraries = settings.get("Libraries", [])
    if not isinstance(libraries, list) or not all(isinstance(x, str) for x in libraries):
        raise SetupError("Manager library settings are unreadable; your existing settings have been kept.")
    replaced = wine_path(previous_library) if previous_library else None
    settings["GameRoot"] = wine_path(game)
    settings["Libraries"] = list(dict.fromkeys([wine_path(library)] + [p for p in libraries if p != replaced]))
    atomic_json(settings_path, settings)
    state.clear()
    state.update(updated)
    atomic_json(root / "linux-state.json", state)


def configured_roots(root, state, prefix=None):
    """App settings remain authoritative when paths change inside Manager."""
    from host import native_path as host_path
    settings = read_json(root / "app/Data/settings.json", {})
    if not isinstance(settings, dict):
        raise SetupError("Manager settings are unreadable. Your existing files have been kept.")
    game = settings.get("GameRoot") or state.get("game_root")
    libraries = settings.get("Libraries") or ([state["library_root"]] if state.get("library_root") else [])
    if not isinstance(libraries, list):
        raise SetupError("Manager library settings are unreadable.")
    game = str(host_path(game, prefix).resolve()) if game else None
    libraries = list(dict.fromkeys(str(host_path(p, prefix).resolve()) for p in libraries if p))
    return game, libraries


def additional_roots(root, libraries, prefix=None):
    """Explicit replacement storage and the smallest complete Fluffy game scope.

    Fluffy's installed.ini is replaced atomically in its SF6 directory, so a
    file-only grant would break unlink/rename. Its Backup, Mods and Presets are
    siblings inside that same directory; no higher parent needs to be exposed.
    """
    replacement = read_json(root / "app/Data/replace-settings.json", {})
    recorded = read_json(root / "app/Data/replace-state.json", {})
    if not isinstance(replacement, dict) or not isinstance(recorded, dict):
        raise SetupError("Replacement storage settings are unreadable. Keep app/Data intact for recovery.")
    extra = []
    def add(value, required=False, label="replacement store"):
        if not value:
            return
        if not isinstance(value, str):
            raise SetupError("A configured " + label + " path is invalid.")
        path = native_path(value, prefix)
        if path.is_dir():
            extra.append(str(path))
        elif required:
            raise SetupError("The configured " + label + " is unavailable. Reconnect its drive or choose its existing location in Manager. No replacement folder has been created.")
    add(replacement.get("StoreLocation"), required=True)
    add(recorded.get("StoreRoot"), required=bool(recorded.get("Mods")))
    previous = recorded.get("PreviousStoreRoots") or []
    explicit = replacement.get("FluffyRoots") or []
    if not isinstance(previous, list) or not isinstance(explicit, list):
        raise SetupError("Replacement storage or external-manager folder settings are invalid.")
    for path in previous:
        add(path)
    for value in explicit:
        if not isinstance(value, str) or not value:
            raise SetupError("An external-manager folder setting is invalid.")
        path = native_path(value, prefix)
        candidate = path.parent if path.name.casefold() == "mods" else path
        add(str(candidate), required=True, label="external-manager folder")
    for value in libraries:
        path = native_path(value, prefix)
        # Match FluffyInstalls.DetectRoots; a generic folder called Mods alone
        # must never grant its parent (which could be the user's whole home).
        candidate = path.parent if path.name.casefold() == "mods" else path
        markers = ("installed.ini", "ModinfoCache.bin", "ArchiveList.bin", "Backup")
        if candidate != path and not (candidate.name.casefold() == "sf6" and candidate.parent.name.casefold() == "games"):
            continue
        if candidate.is_dir() and any((candidate / name).exists() for name in markers):
            extra.append(str(candidate))
    return list(dict.fromkeys(extra))


def checked_launch_roots(root, state, prefix=None):
    game, libraries = configured_roots(root, state, prefix)
    roots = list(dict.fromkeys(([game] if game else []) + libraries + additional_roots(root, libraries, prefix)))
    for path in roots:
        if not Path(path).is_dir():
            raise SetupError("A selected game or mod folder is unavailable. Mount its drive or microSD card, or use Choose game and mod folders from the application menu.")
    return game, libraries, roots


def require_stable_paths_for_repair(root, state=None):
    """A new bottle changes C: and other private drives, never silently rebind them."""
    fields = (("settings.json", ("GameRoot", "Libraries")),
              ("replace-settings.json", ("StoreLocation", "FluffyRoots")),
              ("replace-state.json", ("StoreRoot", "PreviousStoreRoots")))
    paths = []
    for filename, names in fields:
        settings = read_json(root / "app/Data" / filename, {})
        if not isinstance(settings, dict):
            raise SetupError("Manager storage settings are unreadable; keep app/Data intact for recovery.")
        for name in names:
            value = settings.get(name)
            paths.extend(value if isinstance(value, list) else [value])
    fallback = state if state is not None else read_json(root / "linux-state.json", {})
    if not isinstance(fallback, dict):
        raise SetupError("Linux setup settings are unreadable; keep linux-state.json and app/Data intact.")
    paths.extend(fallback.get(name) for name in ("game_root", "library_root", "GameRoot", "LibraryRoot"))
    for path in paths:
        if isinstance(path, str) and re.match(r"^[A-Ya-y]:[\\/]", path):
            raise SetupError("A saved folder or replacement recovery store uses a private Wine drive such as C:. Before repairing the runtime, move active storage to a Linux folder through Z: in Manager Settings and finish recovery from any old private-drive stores. Your current runtime and data have been kept.")


def hold_until_manager_stopped(dialog, status_reader=None):
    """Never release the native lock while a disconnected app might still write."""
    if status_reader is None:
        from host import process_status
        status_reader = process_status
    shown = False
    quiet_until = time.monotonic() + 3
    while True:
        try:
            status = status_reader()
            if status.get("manager_running") is False:
                return
            text = "Close the remaining SlotsLoader Manager window before continuing. This launcher is waiting to keep your installation safe."
        except Exception:
            text = "Linux cannot confirm that Manager has closed. Close its remaining window. This launcher will keep waiting and recheck automatically."
        if not shown and time.monotonic() >= quiet_until:
            dialog.info(text, error=True)
            shown = True
        time.sleep(1)


def session_environment(bridge):
    return {"SLOTSLOADER_PLATFORM": "linux", "SLOTSLOADER_BRIDGE_URL": bridge.url,
            "SLOTSLOADER_BRIDGE_TOKEN": bridge.token, "WINEDLLOVERRIDES": "gdiplus=n,b"}


def make_session_script(root, bridge, app_args):
    """A private, short-lived cmd wrapper survives Bottles' environment filtering.

    No user path or arbitrary command is interpolated into cmd syntax. All app args
    here are fixed switches and generated ASCII filenames used by runtime probes.
    """
    if any(char in str(root) for char in '"\r\n'):
        raise SetupError("The application data folder contains a quote or line break that Wine cannot safely use. Choose an ordinary Linux application data folder.")
    if not re.fullmatch(r"http://127\.0\.0\.1:[0-9]+", bridge.url) or not re.fullmatch(r"[A-Za-z0-9_-]+", bridge.token):
        raise SetupError("The local session could not be initialized.")
    if any(not re.fullmatch(r"[A-Za-z0-9_.-]+", a) for a in app_args):
        raise SetupError("An unsupported runtime argument was rejected.")
    folder = root / "session"
    folder.mkdir(mode=0o700, exist_ok=True)
    if folder.is_symlink():
        raise SetupError("The session folder must not be a symbolic link.")
    os.chmod(folder, 0o700)
    path = folder / ("launch-" + uuid.uuid4().hex + ".bat")
    text = "@echo off\r\nsetlocal DisableDelayedExpansion\r\n"
    for key, value in session_environment(bridge).items():
        text += 'set "' + key + "=" + value + '"\r\n'
    text += 'cd /d "%~dp0..\\app"\r\n"' + EXE + '" ' + " ".join(app_args) + "\r\nexit /b %errorlevel%\r\n"
    with open(path, "x", encoding="ascii", newline="") as f:
        os.chmod(path, 0o600)
        f.write(text)
    return path


def start_app(root, state, bridge, wine=None, prefix=None, app_args=()):
    roots = state.get("granted_roots") or [state.get("game_root"), state.get("library_root")]
    env = os.environ.copy()
    for key in session_environment(bridge):
        env.pop(key, None)
    script = None
    if wine:
        if not prefix or not Path(prefix).is_dir():
            raise SetupError("Advanced Wine launch needs an existing, dedicated --prefix with .NET Framework 4.8.")
        env["WINEPREFIX"] = str(Path(prefix).resolve())
        env.update(session_environment(bridge))
        command = [wine, str(root / "app" / EXE)] + list(app_args)
    else:
        script = make_session_script(root, bridge, app_args)
        command = flatpak_base(root, roots) + ["run", "-b", state["bottle"], "-e", str(script)]
    process = subprocess.Popen(command, cwd=str(root / "app"), env=env,
                               stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return process, script


def wait_session(process, bridge, script=None, start_timeout=START_TIMEOUT, heartbeat_timeout=HEARTBEAT_TIMEOUT, dialog=None):
    deadline = time.monotonic() + start_timeout
    try:
        with dialog.busy("Opening SlotsLoader Manager") if dialog else contextlib.nullcontext():
            while not bridge.session_started and not bridge.session_exited:
                if time.monotonic() >= deadline:
                    raise SetupError("Manager could not start its Linux session. Use Repair compatibility runtime from the application menu. Check that Bottles has finished its first-run setup.")
                if process.poll() not in (None, 0):
                    raise SetupError("The compatibility runtime could not start Manager. Choose Repair compatibility runtime.")
                time.sleep(0.2)
        if script:
            script.unlink(missing_ok=True)
        while not bridge.session_exited:
            if time.monotonic() - bridge.session_last_seen > heartbeat_timeout:
                raise SetupError("Manager stopped responding to its Linux session. Close its window, then reopen it before making any changes.")
            time.sleep(0.5)
        return bridge.session_exit_code or 0
    finally:
        if script:
            script.unlink(missing_ok=True)


def provision(root, state, dialog, repair=False):
    ensure_bottles(dialog)
    if state.get("bottle") and state.get("runtime_recipe") == RUNTIME_RECIPE and not repair:
        return dict(state)
    require_stable_paths_for_repair(root, state)
    # The selected runtime remains authoritative until the new one passes every probe.
    pending = state.get("pending_runtime")
    if repair or not (isinstance(pending, dict) and pending.get("runtime_recipe") == RUNTIME_RECIPE
            and pending.get("created") is True
            and re.fullmatch(r"SlotsLoader-Manager-[0-9a-f]{12}", str(pending.get("bottle", "")))):
        pending = {"bottle": "SlotsLoader-Manager-" + uuid.uuid4().hex[:12], "runtime_recipe": RUNTIME_RECIPE, "created": False}
        state["pending_runtime"] = pending
        atomic_json(root / "linux-state.json", state)
        run_checked(flatpak_base(root) + ["new", "--bottle-name", pending["bottle"], "--environment", "custom",
                    "--custom-environment", str(root / "linux/bottle.yml"), "--arch", "win64"],
                    dialog, "Preparing .NET Framework and the graphics runtime. This can take several minutes")
        pending["created"] = True
        atomic_json(root / "linux-state.json", state)
    pending["wine_prefix"] = bottle_prefix(root, pending["bottle"], dialog)
    atomic_json(root / "linux-state.json", state)
    selected = dict(state, bottle=pending["bottle"], wine_prefix=pending["wine_prefix"],
                    runtime_recipe=RUNTIME_RECIPE, runtime_ready=False)
    selected.pop("pending_runtime", None)
    return selected


def configure_renderer_override(root, state, dialog):
    # Bottles filters its parent environment. Persist only this dedicated bottle's
    # override through its public CLI; setting it inside cmd.exe is too late for Wine.
    run_checked(flatpak_base(root) + ["edit", "-b", state["bottle"], "--env-var", "WINEDLLOVERRIDES=gdiplus=n,b"],
                dialog, "Selecting the dedicated graphics renderer", timeout=120)


def promote_runtime(root, state, selected):
    if not selected.get("runtime_ready"):
        raise SetupError("The new runtime has not passed its checks. The previous selection has been kept.")
    updated = dict(selected)
    updated.pop("pending_runtime", None)
    previous = state.get("bottle")
    if previous and previous != updated.get("bottle"):
        updated["previous_bottles"] = list(dict.fromkeys(list(state.get("previous_bottles", [])) + [previous]))
    atomic_json(root / "linux-state.json", updated)
    state.clear()
    state.update(updated)


def runtime_probe(root, state, bridge, dialog, wine=None, prefix=None):
    state["runtime_ready"] = False
    report_name = "linux-runtime-probe-" + uuid.uuid4().hex + ".json"
    report = root / "app" / report_name
    try:
        with dialog.busy("Checking .NET Framework and the desktop interface"):
            process, script = start_app(root, state, bridge, wine, prefix, ["--linux-runtime-probe", report_name])
            deadline = time.monotonic() + START_TIMEOUT
            result = None
            while result is None:
                if report.is_file():
                    try:
                        result = json.loads(report.read_text(encoding="utf-8"))
                        break
                    except (ValueError, OSError):
                        pass  # File.WriteAllText may still be writing the short report.
                if time.monotonic() >= deadline or process.poll() not in (None, 0):
                    raise SetupError("The .NET runtime check did not complete. Finish Bottles setup, then choose Repair compatibility runtime.")
                time.sleep(0.2)
            # The probe writes its result after all checks and exits; it does not start an app session.
            code = 0
        if not isinstance(result, dict) or code or result.get("protocol") != 1 or result.get("ok") is not True:
            error = result.get("error") if isinstance(result, dict) else None
            if isinstance(error, str) and error.strip():
                error = " ".join(error.split())[:600]
                if "font" in error.casefold() and "reinstall" not in error.casefold():
                    error += " Reinstall the Linux application package to restore its Fonts folder."
                raise SetupError("The runtime check did not pass: " + error)
            raise SetupError("The runtime check did not pass. Open Bottles, finish any pending setup, then choose Repair compatibility runtime.")
        if (result.get("renderer_sha256") != RENDERER_SHA256 or result.get("renderer_antialias") is not True
                or type(result.get("renderer_blended_pixels")) is not int or result["renderer_blended_pixels"] <= 0
                or result.get("font_monospace") != "Liberation Mono" or result.get("font_family") != "Inter"
                or result.get("font_smoothing_enabled") is not True or result.get("font_smoothing") != 2
                or result.get("mono") is not False or result.get("winforms") is not True
                or not isinstance(result.get("framework_release"), int) or result["framework_release"] < 528040):
            raise SetupError("The runtime check did not confirm the required fonts and graphics renderer. Reinstall the current Linux application package, then use Repair compatibility runtime if needed.")
        state["runtime_ready"] = True
        state["probe"] = {k: v for k, v in result.items() if k in ("ok", "framework_release", "mono", "winforms", "font_family", "font_smoothing", "font_smoothing_enabled", "renderer_sha256", "renderer_antialias", "renderer_blended_pixels", "font_monospace")}
        return state["probe"]
    finally:
        report.unlink(missing_ok=True)
        if "script" in locals() and script:
            script.unlink(missing_ok=True)


def diagnostics(root, state):
    # Deliberate allowlist: no settings, game paths, mod names, process list, logs or API keys.
    manifest = read_json(root / "package.json", {})
    result = {"format": 1, "package_version": manifest.get("version"), "platform": platform.system(),
              "machine": platform.machine(), "python": platform.python_version(),
              "runtime_ready": bool(state.get("runtime_ready")), "configured": bool(state.get("configured")),
              "game_folder_present": bool(state.get("game_root") and Path(state["game_root"]).is_dir()),
              "library_folder_present": bool(state.get("library_root") and Path(state["library_root"]).is_dir()),
              "flatpak_available": bool(shutil.which("flatpak")), "dialog_available": bool(shutil.which("kdialog") or shutil.which("zenity"))}
    destination = root / "linux-diagnostics.json"
    atomic_json(destination, result)
    return destination


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, help="Install an extracted, checksummed Linux package")
    parser.add_argument("--configure", action="store_true")
    parser.add_argument("--repair", action="store_true")
    parser.add_argument("--update", action="store_true", help="Check for a complete Linux package update")
    parser.add_argument("--skip-update", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--diagnostics", action="store_true")
    parser.add_argument("--game-setup", action="store_true")
    parser.add_argument("--wine", help="Advanced: existing Wine executable")
    parser.add_argument("--prefix", type=Path, help="Advanced: existing dedicated .NET 4.8 Wine prefix")
    parser.add_argument("--headless", action="store_true", help="Advanced: no dialogs; requires completed setup")
    args = parser.parse_args(argv)
    dialog = None
    try:
        dialog = Dialog(args.headless)
        if sys.version_info < (3, 10):
            raise SetupError("SlotsLoader Manager needs Python 3.10 or newer. Update Python through your software centre.")
        if sys.platform != "linux":
            raise SetupError("This launcher is for Linux. Use the Windows package on Windows.")
        if platform.machine().lower() not in ("x86_64", "amd64"):
            raise SetupError("This test build needs an x86-64 Linux PC or Steam Deck.")
        if args.game_setup:
            game_setup(dialog)
            return 0
        from host import HostBridge, SessionLock, process_status
        root = install_root()
        root.mkdir(parents=True, exist_ok=True)
        with SessionLock(session_lock_path()):
            if process_status().get("manager_running") is not False:
                raise SetupError("SlotsLoader Manager is already running. Close its existing window before launching or updating it.")
            recover_update(root)
            if args.install:
                install_package(args.install, root)
                install_shortcut(root)
            if not (root / "app" / EXE).is_file():
                raise SetupError("Extract the Linux package and open Install SlotsLoader Manager.desktop first.")
            state = read_json(root / "linux-state.json", {"format": STATE_VERSION})
            if args.diagnostics:
                destination = diagnostics(root, state)
                dialog.info("A diagnostic report has been saved without keys, personal paths or mod contents.\n\n" + str(destination))
                if not args.headless:
                    subprocess.Popen(["xdg-open", str(destination)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                return 0
            resume = (["--wine", args.wine, "--prefix", str(args.prefix)] if args.wine and args.prefix else [])
            if args.update or not (args.skip_update or args.install or args.headless or args.configure or args.repair or args.wine):
                from updater import check_and_install
                if check_and_install(root, dialog, sys.modules[__name__], manual=args.update):
                    # Unwind the native session lock before executing the newly installed launcher.
                    raise Relaunch(root, ["--skip-update"] + resume)
            if args.configure or not state.get("configured"):
                if args.headless:
                    raise SetupError("Complete graphical folder setup before using --headless.")
                configure(root, state, dialog)
            if not args.wine and (args.repair or not state.get("bottle") or state.get("runtime_recipe") != RUNTIME_RECIPE):
                require_stable_paths_for_repair(root, state)
            # A disconnected game, library or recovery disk should fail before downloads.
            checked_launch_roots(root, state, args.prefix if args.wine else state.get("wine_prefix"))
            selected = dict(state) if args.wine else provision(root, state, dialog, args.repair)
            selected_prefix = args.prefix if args.wine else selected.get("wine_prefix")
            if not selected_prefix:
                raise SetupError("The selected runtime has no prefix. Advanced Wine needs an existing dedicated --prefix; otherwise use Repair compatibility runtime.")
            selected_prefix = Path(selected_prefix)
            # Resolve settings only after choosing the runtime whose private drives they use.
            game, libraries, roots = checked_launch_roots(root, state, selected_prefix)
            if not args.wine:
                configure_renderer_override(root, selected, dialog)
            prepare_renderer(root, selected_prefix)
            # Probe the selected runtime each time, including after an upgrade or prefix switch.
            probe_state = dict(selected, granted_roots=roots)
            with HostBridge(root / "app", allowed_roots=roots, wine_prefix=selected_prefix,
                            preferred_game=game, preferred_library=libraries[0] if libraries else None) as bridge:
                runtime_probe(root, probe_state, bridge, dialog, args.wine, selected_prefix)
            selected.update(runtime_ready=probe_state["runtime_ready"], probe=probe_state["probe"])
            if args.wine:
                atomic_json(root / "linux-state.json", selected)
                state = selected
            else:
                promote_runtime(root, state, selected)
            if not args.headless and not state.get("game_setup_shown"):
                if dialog.confirm("Manager's compatibility runtime is ready. View the Steam game setup steps before opening Manager?"):
                    game_setup(dialog)
                state["game_setup_shown"] = True
                atomic_json(root / "linux-state.json", state)
            launch_state = dict(state, game_root=game, library_root=libraries[0] if libraries else None, granted_roots=roots)
            with HostBridge(root / "app", allowed_roots=roots, wine_prefix=selected_prefix,
                            preferred_game=game, preferred_library=libraries[0] if libraries else None) as bridge:
                process, script = start_app(root, launch_state, bridge, args.wine, selected_prefix)
                try:
                    code = wait_session(process, bridge, script, dialog=dialog)
                finally:
                    hold_until_manager_stopped(dialog)
                requested_update = bridge.update_requested
            if requested_update and code == 0:
                raise Relaunch(root, ["--update"] + resume)
            return code
    except Relaunch as restart:
        os.execv(sys.executable, [sys.executable, str(restart.root / "linux/launcher.py")] + restart.arguments)
        return 1
    except Exception as exc:
        message = str(exc)
        if dialog:
            dialog.info(message, error=True)
        else:
            print(message, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
