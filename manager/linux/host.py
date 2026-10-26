#!/usr/bin/env python3
"""Small native companion for the Windows Manager running through Wine.

Only the supported launcher starts this process. No commands are accepted from
HTTP clients; every operation is fixed and requires a random session token.
"""

from __future__ import annotations

import errno
import fcntl
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import stat
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

PROTOCOL = 1
APP_ID = "1364780"
GAME_EXE = "StreetFighter6.exe"
MAX_BODY = 16384


class HostError(Exception):
    """An actionable error which can safely be shown by the application."""


def wine_path(path: str | Path) -> str:
    value = str(Path(path).resolve(strict=False))
    if not value.startswith("/"):
        raise HostError("Choose an absolute Linux folder.")
    return "Z:" + value.replace("/", "\\")


def native_path(value: str, prefix: str | Path | None = None) -> Path:
    if not isinstance(value, str) or not value or len(value) > 32768 or any(c in value for c in "\x00\r\n"):
        raise HostError("The folder path is invalid.")
    if value.startswith("/"):
        if ".." in value.split("/"):
            raise HostError("The folder path contains a parent-directory reference.")
        return Path(os.path.abspath(value))
    match = re.fullmatch(r"([A-Za-z]):[\\/](.*)", value)
    if not match:
        raise HostError("Choose a Linux folder using the Z: drive in the file browser.")
    drive, tail = match.group(1).lower(), match.group(2).replace("\\", "/")
    # A drive-rooted path must never turn into a relative or network path.
    if any(part == ".." for part in tail.split("/")):
        raise HostError("The folder path contains a parent-directory reference.")
    if drive == "z":
        return Path("/") / tail.lstrip("/")
    if prefix is None:
        raise HostError("Choose a Linux folder using the Z: drive in the file browser.")
    prefix_root = Path(prefix).resolve(strict=True)
    drive_link = prefix_root / "dosdevices" / (drive + ":")
    if drive_link.exists():
        root = drive_link.resolve(strict=True)
    elif drive == "c":
        root = prefix_root / "drive_c"
    else:
        raise HostError("This Wine drive is unavailable. Choose the folder using the Z: drive.")
    return root / tail.lstrip("/")


def _canonical_directory(path: Path) -> Path:
    try:
        result = path.resolve(strict=True)
        if not result.is_dir():
            raise HostError("Choose an existing folder.")
        return result
    except (FileNotFoundError, NotADirectoryError):
        raise HostError("This folder is unavailable. Connect its drive or choose its new location.")
    except OSError as error:
        raise HostError("The folder cannot be accessed: " + str(error))


def _vdf_pairs(text: str):
    for match in re.finditer(r'"((?:[^"\\]|\\.)*)"\s+"((?:[^"\\]|\\.)*)"', text):
        yield tuple(part.replace("\\\\", "\\").replace('\\"', '"') for part in match.groups())


def discover_installations(home: str | Path | None = None) -> dict:
    """Read Steam manifests only; never scan unrelated user directories."""
    home_path = Path(home) if home is not None else Path.home()
    candidates = [
        home_path / ".local/share/Steam",
        home_path / ".steam/steam",
        home_path / ".steam/root",
        home_path / ".var/app/com.valvesoftware.Steam/.local/share/Steam",
        home_path / ".var/app/com.valvesoftware.Steam/data/Steam",
    ]
    roots: list[Path] = []
    libraries: list[Path] = []
    def add(target: list[Path], value: Path):
        try:
            resolved = value.resolve(strict=True)
            if resolved.is_dir() and resolved not in target:
                target.append(resolved)
        except OSError:
            pass
    for candidate in candidates:
        add(roots, candidate)
    for root in roots:
        add(libraries, root)
        for relative in ("steamapps/libraryfolders.vdf", "config/libraryfolders.vdf"):
            try:
                file = root / relative
                if file.stat().st_size > 1024 * 1024:
                    continue
                for key, value in _vdf_pairs(file.read_text(encoding="utf-8", errors="replace")):
                    if (key == "path" or key.isdigit()) and value.startswith("/"):
                        add(libraries, Path(value))
            except OSError:
                continue
    game_roots: list[Path] = []
    for library in libraries:
        manifest = library / "steamapps" / ("appmanifest_" + APP_ID + ".acf")
        name = "Street Fighter 6"
        try:
            if manifest.stat().st_size <= 1024 * 1024:
                name = dict(_vdf_pairs(manifest.read_text(encoding="utf-8", errors="replace"))).get("installdir", name)
        except OSError:
            pass
        if name in ("", ".", "..") or "/" in name or "\\" in name:
            continue
        candidate = library / "steamapps/common" / name
        try:
            canonical = candidate.resolve(strict=True)
            if canonical.is_dir() and (canonical / GAME_EXE).is_file():
                add(game_roots, canonical)
        except OSError:
            continue
    mod_folders: list[Path] = []
    for candidate in [home_path / "Games/Slotsloader Mods", home_path / "Downloads/SF6 Mods"]:
        add(mod_folders, candidate)
    return {
        "game_root": str(game_roots[0]) if len(game_roots) == 1 else "",
        "game_roots": [str(path) for path in game_roots],
        "libraries": [str(path) for path in mod_folders],
        "steam_roots": [str(path) for path in roots],
        "steam_libraries": [str(path) for path in libraries],
    }


def process_status(proc_root: str | Path = "/proc", uid: int | None = None) -> dict:
    """Fresh process inspection across Wine prefixes, without reading environ."""
    proc = Path(proc_root)
    user = os.getuid() if uid is None else uid
    result = {"game_running": False, "fluffy_running": False, "manager_running": False}
    try:
        entries = list(proc.iterdir())
    except OSError:
        raise HostError("Linux process status is unavailable. Restart the Manager from its Linux launcher.")
    inspected = 0
    for directory in entries:
        if not directory.name.isdigit():
            continue
        try:
            if directory.stat().st_uid != user:
                continue
            name = (directory / "comm").read_text(encoding="utf-8", errors="replace").strip().casefold()
            raw = (directory / "cmdline").read_bytes()
            args = [part.decode("utf-8", errors="replace") for part in raw.split(b"\0") if part]
            inspected += 1
        except (FileNotFoundError, ProcessLookupError):
            continue  # A process ended between enumeration and reading.
        except OSError as error:
            if error.errno in (errno.ENOENT, errno.ESRCH):
                continue
            raise HostError("Linux could not confirm which programs are running. Close SF6 and restart the Manager from its Linux launcher.")
        executable_names = [arg.replace("\\", "/").rsplit("/", 1)[-1].casefold() for arg in args]
        if name.startswith("streetfighter6") or any(arg in ("streetfighter6", "streetfighter6.exe") for arg in executable_names):
            result["game_running"] = True
        # Cover Steam's launch wrapper before the executable itself appears.
        if any(arg.casefold() == "appid=" + APP_ID for arg in args):
            result["game_running"] = True
        if name in ("modmanager", "modmanager.exe", "modmanagerv2", "modmanagerv2.exe", "fluffymodmanager", "fluffymodmanage") or any(
            arg in ("modmanager.exe", "modmanagerv2.exe", "fluffymodmanager.exe") for arg in executable_names
        ):
            result["fluffy_running"] = True
        if name in ("slotsloader man", "slotsloader cli", "slotsloader manager.exe", "slotsloader cli.exe") or any(
            arg in ("slotsloader manager.exe", "slotsloader cli.exe") for arg in executable_names
        ):
            result["manager_running"] = True
    if inspected == 0:
        raise HostError("Linux process status is unavailable. No game files were changed.")
    result["checked_unix_ms"] = int(time.time() * 1000)
    return result


class SessionLock:
    """The supported Linux launcher has one active session per user."""
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.fd: int | None = None

    def __enter__(self):
        self.path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        flags = os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0)
        try:
            self.fd = os.open(self.path, flags, 0o600)
            info = os.fstat(self.fd)
            if info.st_uid != os.getuid() or not stat.S_ISREG(info.st_mode):
                raise HostError("The Manager session lock is not owned by this user.")
            fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            os.ftruncate(self.fd, 0)
            os.write(self.fd, str(os.getpid()).encode("ascii"))
            return self
        except BlockingIOError:
            self.__exit__(None, None, None)
            raise HostError("Slotsloader Manager is already running. Close its other window first.")
        except Exception:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *_):
        if self.fd is not None:
            os.close(self.fd)
            self.fd = None


class HostBridge:
    def __init__(self, app_root: str | Path, allowed_roots=None, wine_prefix=None,
                 proc_root="/proc", home=None, preferred_game=None, preferred_library=None):
        self.app_root = _canonical_directory(Path(app_root))
        self.wine_prefix = Path(wine_prefix) if wine_prefix else None
        self.allowed_roots = {self.app_root}
        for root in allowed_roots or []:
            self.allowed_roots.add(_canonical_directory(Path(root)))
        self.proc_root = Path(proc_root)
        self.home = Path(home) if home is not None else Path.home()
        self.token = secrets.token_urlsafe(32)
        self.url = ""
        self.session_started = False
        self.session_last_seen = 0.0
        self.session_exited = False
        self.session_exit_code = None
        self.update_requested = False
        self._state_lock = threading.Lock()
        self._storage: dict[Path, tuple[int, int]] = {}
        self._case_cache = {}
        self._server = None
        self._thread = None
        self.preferred_game = self.validate(str(preferred_game), "game") if preferred_game else None
        self.preferred_library = self.validate(str(preferred_library), "library") if preferred_library else None

    def __enter__(self):
        bridge = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass  # URLs, filenames and tokens are not diagnostic logs.

            def _respond(self, status, data):
                payload = json.dumps(dict(protocol=PROTOCOL, **data), ensure_ascii=True).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Length", str(len(payload)))
                self.send_header("Connection", "close")
                self.end_headers()
                try:
                    self.wfile.write(payload)
                except (BrokenPipeError, ConnectionResetError):
                    pass

            def _request(self, method):
                token = self.headers.get("X-Slotsloader-Token", "")
                if not token.isascii() or not hmac.compare_digest(token, bridge.token):
                    self._respond(403, {"ok": False, "error": "This request is not part of the Manager session."})
                    return
                if self.headers.get("Origin") or self.headers.get("Transfer-Encoding"):
                    self._respond(400, {"ok": False, "error": "Unsupported request."})
                    return
                try:
                    body = {}
                    if method == "POST":
                        length = int(self.headers.get("Content-Length", "0"))
                        if not 0 < length <= MAX_BODY:
                            raise HostError("The request is too large or empty.")
                        self.connection.settimeout(3)
                        body = json.loads(self.rfile.read(length).decode("utf-8"))
                        if not isinstance(body, dict):
                            raise HostError("The request must be an object.")
                    value = bridge.dispatch(method, self.path, body)
                    self._respond(200, dict(ok=True, **value))
                except HostError as error:
                    self._respond(409, {"ok": False, "error": str(error)})
                except (ValueError, UnicodeError, TimeoutError):
                    self._respond(400, {"ok": False, "error": "The request could not be read."})
                except Exception:
                    self._respond(500, {"ok": False, "error": "Linux integration is unavailable. Restart the Manager from its Linux launcher."})

            def do_GET(self):
                self._request("GET")

            def do_POST(self):
                self._request("POST")

        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self._server.daemon_threads = True
        self.url = "http://127.0.0.1:" + str(self._server.server_address[1])
        self._thread = threading.Thread(target=self._server.serve_forever, name="slotsloader-host", daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *_):
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()
            self._server = None
        if self._thread is not None:
            self._thread.join(timeout=3)
            self._thread = None

    def _path(self, value):
        return native_path(value, self.wine_prefix)

    def validate(self, value, kind="path"):
        if kind not in ("game", "library", "path"):
            raise HostError("Unknown folder type.")
        original = self._path(value)
        if kind in ("game", "library"):
            canonical = _canonical_directory(original)
            if kind == "game":
                if not (canonical / GAME_EXE).is_file() or (canonical / GAME_EXE).is_symlink():
                    raise HostError("SF6 is unavailable in this folder. Connect its drive or choose the folder containing StreetFighter6.exe.")
                info = canonical.stat()
                identity = (info.st_dev, info.st_ino)
                with self._state_lock:
                    prior = self._storage.get(canonical)
                    if prior is not None and prior != identity:
                        raise HostError("The game's storage changed. Restart the Manager and confirm its location.")
                    self._storage[canonical] = identity
            with self._state_lock:
                self.allowed_roots.add(canonical)
            return canonical
        try:
            canonical = original.resolve(strict=True)
        except OSError:
            raise HostError("This file or folder is unavailable. Connect its drive or choose its new location.")
        # Existing managed operations reject reparse points; the native boundary
        # also refuses a link hidden by Wine when acting on an individual path.
        if original != canonical:
            raise HostError("This path contains a symbolic link. Choose the actual file or folder.")
        return canonical

    def _require_allowed(self, path: Path):
        with self._state_lock:
            roots = list(self.allowed_roots)
        if not any(path == root or root in path.parents for root in roots):
            raise HostError("Select this folder in Manager settings before changing its files.")

    def contained(self, value, root=None):
        """Inspect real links without confusing Wine's bind-mount attributes.

        Wine's case-insensitive fallback must be mirrored: a differently cased
        link is still a link. A nonexistent suffix is permitted for new files.
        """
        path = self._path(value)
        boundary = self._path(root) if root else Path("/")
        if path != boundary and boundary not in path.parents:
            raise HostError("The managed path is outside its folder.")
        with self._state_lock:
            storage = list(self._storage.items())
        for game, identity in storage:
            if path == game or game in path.parents:
                try:
                    info = game.stat()
                    if (info.st_dev, info.st_ino) != identity or not (game / GAME_EXE).is_file():
                        raise HostError("The game's storage changed. Reconnect its drive and restart the Manager.")
                except OSError:
                    raise HostError("The game's storage is unavailable. Reconnect its drive before changing mods.")
        current = Path("/")
        missing = False
        for part in path.parts[1:]:
            if missing:
                current /= part
                continue
            candidate = current / part
            # C# and Wine treat names without case distinction. Refuse ambiguous
            # siblings even when this spelling happens to exist exactly. Cache
            # directory name indexes only while identity/mtime/ctime stay equal;
            # each actual path component still gets a fresh lstat below.
            try:
                directory_info = current.stat()
                stamp = (directory_info.st_dev, directory_info.st_ino, directory_info.st_mtime_ns, directory_info.st_ctime_ns)
                with self._state_lock:
                    cached = self._case_cache.get(current)
                if cached is None or cached[0] != stamp:
                    names = {}
                    for child in current.iterdir():
                        names.setdefault(child.name.casefold(), []).append(child.name)
                    cached = (stamp, names)
                    with self._state_lock:
                        if len(self._case_cache) > 4096:
                            self._case_cache.clear()
                        self._case_cache[current] = cached
                matches = cached[1].get(part.casefold(), [])
                if len(matches) > 1:
                    raise HostError("This folder contains names that differ only by letter case. Rename them before using it with the Manager.")
            except OSError:
                raise HostError("A managed folder cannot be inspected. Check that its drive is connected.")
            try:
                info = candidate.lstat()
            except FileNotFoundError:
                if not matches:
                    missing = True
                    current = candidate
                    continue
                candidate = current / matches[0]
                try:
                    info = candidate.lstat()
                except OSError:
                    raise HostError("A managed path changed during inspection. Refresh and try again.")
            except OSError:
                raise HostError("A managed folder cannot be inspected. Check permissions and its drive.")
            if stat.S_ISLNK(info.st_mode):
                raise HostError("A managed path contains a symbolic link. Select the actual root folder, and keep links out of its managed files.")
            current = candidate
        # The caller may have used a differently cased Wine path; check the
        # registered root again after resolving those component spellings.
        for game, identity in storage:
            if current == game or game in current.parents:
                try:
                    info = game.stat()
                    if (info.st_dev, info.st_ino) != identity or not (game / GAME_EXE).is_file():
                        raise HostError("The game's storage changed. Reconnect its drive and restart the Manager.")
                except OSError:
                    raise HostError("The game's storage is unavailable. Reconnect its drive before changing mods.")
        return current

    def _desktop_open(self, target):
        executable = shutil.which("xdg-open")
        if executable is None:
            raise HostError("Desktop opening is unavailable. Install xdg-utils using your software manager.")
        try:
            # No shell, no inherited bridge credentials, no user-provided argv.
            env = {key: value for key, value in os.environ.items() if not key.startswith("SLOTSLOADER_BRIDGE_")}
            subprocess.Popen([executable, target], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, env=env, start_new_session=True)
        except OSError:
            raise HostError("The desktop could not open this item. Check your default application.")

    def _disposable_app_copy(self, path):
        """Allow only inventoried removed copies, never general application Data."""
        try:
            parts = path.relative_to(self.app_root).parts
            replacement = (len(parts) == 4 and parts[:3] == ("Data", "replace-store", ".removed"))
            leftover = (len(parts) == 4 and parts[:2] == ("Data", "leftover-paks"))
            if not replacement and not leftover:
                return False
            folder = path if replacement else path.parent
            record_path = self.validate(str(folder / "record.json"), "path")
            if record_path.stat().st_size > 4 * 1024 * 1024:
                return False
            record = json.loads(record_path.read_text(encoding="utf-8-sig"))
            if not isinstance(record, dict):
                return False
            if replacement:
                mod = record.get("Mod")
                if record.get("Format") != "sf6-replace-removed-v1" or not isinstance(mod, dict):
                    return False
                mod_id = mod.get("Id")
                if not isinstance(mod_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", mod_id) or not path.name.startswith(mod_id + "-"):
                    return False
                files = self.validate(str(folder / "files"), "path")
                return files.is_dir() and path.is_dir()
            name = record.get("Name")
            return (record.get("Format") == "sf6-leftover-pak-v1" and isinstance(name, str)
                    and re.fullmatch(r"re_chunk_000\.pak\.patch_[0-9]+\.pak", name) is not None
                    and path.name == name and path.is_file()
                    and isinstance(record.get("Length"), int) and not isinstance(record.get("Length"), bool)
                    and record["Length"] == path.stat().st_size)
        except (ValueError, OSError, HostError):
            return False

    def dispatch(self, method, route, body):
        if method == "GET" and route == "/status":
            return process_status(self.proc_root)
        if method == "GET" and route == "/discover":
            found = discover_installations(self.home)
            if self.preferred_game:
                game = str(self.validate(str(self.preferred_game), "game"))
                found["game_root"] = game
                found["game_roots"] = [game] + [path for path in found["game_roots"] if path != game]
            if self.preferred_library:
                library = str(self.validate(str(self.preferred_library), "library"))
                found["libraries"] = [library] + [path for path in found["libraries"] if path != library]
            for root in found["game_roots"] + found["libraries"]:
                self.validate(root, "game" if root in found["game_roots"] else "library")
            return {key: ([wine_path(path) for path in value] if isinstance(value, list)
                          else wine_path(value) if value else "") for key, value in found.items()}
        if method == "POST" and route == "/session":
            event = body.get("event")
            if event not in ("started", "heartbeat", "exited"):
                raise HostError("Unknown session event.")
            with self._state_lock:
                if event == "started":
                    if self.session_started:
                        raise HostError("A Manager process has already joined this session.")
                    self.session_started = True
                    self.session_last_seen = time.monotonic()
                elif event == "heartbeat":
                    if not self.session_started or self.session_exited:
                        raise HostError("The Manager session is not active.")
                    self.session_last_seen = time.monotonic()
                else:
                    code = body.get("exit_code", 0)
                    if not isinstance(code, int) or isinstance(code, bool):
                        raise HostError("Invalid exit status.")
                    self.session_exited = True
                    self.session_exit_code = code
                    self.session_last_seen = time.monotonic()
            return {}
        if method == "POST" and route == "/update":
            with self._state_lock:
                if not self.session_started or self.session_exited:
                    raise HostError("The Manager session is not active.")
                self.update_requested = True
            return {}
        if method == "POST" and route == "/validate":
            path = self.validate(body.get("path"), body.get("kind", "path"))
            return {"path": wine_path(path)}
        if method == "POST" and route == "/contained":
            self.contained(body.get("path"), body.get("root"))
            return {}
        if method == "POST" and route == "/launch":
            current = process_status(self.proc_root)
            if not current["game_running"]:
                self._desktop_open("steam://rungameid/" + APP_ID)
            return {}
        if method == "POST" and route == "/open":
            if "url" in body:
                url = body["url"]
                if not isinstance(url, str) or len(url) > 8192 or any(c in url for c in "\x00\r\n"):
                    raise HostError("The web address is invalid.")
                parsed = urlsplit(url)
                if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password:
                    raise HostError("Only ordinary HTTP and HTTPS links can be opened.")
                self._desktop_open(url)
            else:
                original = self._path(body.get("path"))
                try:
                    path = original.resolve(strict=True)
                except OSError:
                    raise HostError("This file or folder is unavailable.")
                if body.get("reveal") or (path.is_file() and path.suffix.casefold() not in (".txt", ".md", ".html", ".htm", ".pdf")):
                    path = path.parent
                self._desktop_open(str(path))
            return {}
        if method == "POST" and route == "/trash":
            path = self.validate(body.get("path"), "path")
            self._require_allowed(path)
            if (path == self.app_root or self.app_root in path.parents or path in self.app_root.parents) and not self._disposable_app_copy(path):
                raise HostError("The Manager's application and recovery files cannot be removed this way.")
            with self._state_lock:
                if path in self.allowed_roots:
                    raise HostError("A selected library or game root cannot be removed this way.")
            tool = shutil.which("gio")
            if tool is None:
                raise HostError("Desktop Trash is unavailable. The source was kept; use your file manager to remove it.")
            try:
                run = subprocess.run([tool, "trash", "--", str(path)], stdin=subprocess.DEVNULL,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=30,
                                     env={key: val for key, val in os.environ.items() if not key.startswith("SLOTSLOADER_BRIDGE_")})
            except (OSError, subprocess.TimeoutExpired):
                raise HostError("Desktop Trash did not confirm removal. Check the source and Trash before trying again.")
            if not os.path.lexists(path) and run.returncode != 0:
                raise HostError("Desktop Trash reported a problem after moving the source. Check Trash before trying again.")
            if run.returncode != 0 or os.path.lexists(path):
                raise HostError("Desktop Trash could not accept this item. The source was kept; use your file manager to remove it.")
            return {}
        raise HostError("Unsupported Linux integration operation.")
