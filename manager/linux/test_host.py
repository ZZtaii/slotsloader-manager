"""Native integration regressions. All writes stay in TemporaryDirectory fixtures."""
import http.client
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from host import HostBridge, HostError, SessionLock, discover_installations, native_path, process_status, wine_path


class NativeFixtures(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="slotsloader-host-test-")
        self.root = Path(self.temp.name).resolve()
        self.app = self.root / "Manager"
        self.app.mkdir()
        self.library = self.root / "Mods with spaces"
        self.library.mkdir()
        self.game = self.root / "Street Fighter 6"
        self.game.mkdir()
        (self.game / "StreetFighter6.exe").write_bytes(b"fixture")
        self.proc = self.root / "proc"
        self.proc.mkdir()
        self.process(1, "python3", ["python3", "fixture.py"])
        self.bridge = HostBridge(self.app, [self.library], proc_root=self.proc, home=self.root / "home")

    def tearDown(self):
        self.temp.cleanup()

    def process(self, pid, name, args):
        folder = self.proc / str(pid)
        folder.mkdir(exist_ok=True)
        (folder / "comm").write_text(name)
        (folder / "cmdline").write_bytes(b"\0".join(arg.encode() for arg in args))
        # The bridge has no reason to inspect process environments.
        (folder / "environ").mkdir(exist_ok=True)

    def test_path_mapping_and_invalid_paths(self):
        self.assertEqual(self.library, native_path(wine_path(self.library)))
        prefix = self.root / "prefix"
        (prefix / "drive_c").mkdir(parents=True)
        (prefix / "dosdevices").mkdir()
        (prefix / "dosdevices/c:").symlink_to("../drive_c")
        self.assertEqual(prefix / "drive_c/fixture/test", native_path(r"C:\fixture\test", prefix))
        for path in (r"..\danger", r"\\server\share", r"Z:\safe\..\other", "/safe/../other", "\x00", "relative", r"Q:\unknown"):
            with self.subTest(path=path), self.assertRaises(HostError):
                native_path(path)

    def test_cross_prefix_processes_and_steam_wrapper(self):
        self.assertFalse(process_status(self.proc)["game_running"])
        self.process(2, "StreetFighter6.e", ["/another/prefix/StreetFighter6.exe"])
        self.assertTrue(process_status(self.proc)["game_running"])
        self.process(2, "wine", ["wine", r"C:\SF6\StreetFighter6.exe"])
        self.assertTrue(process_status(self.proc)["game_running"])
        self.process(2, "reaper", ["reaper", "SteamLaunch", "AppId=1364780", "--"])
        self.assertTrue(process_status(self.proc)["game_running"])
        self.process(3, "Modmanager.exe", ["/other/prefix/Modmanager.exe"])
        self.assertTrue(process_status(self.proc)["fluffy_running"])

    def test_unknown_process_status_fails_closed(self):
        with self.assertRaises(HostError):
            process_status(self.root / "missing-proc")
        empty = self.root / "empty-proc"
        empty.mkdir()
        with self.assertRaises(HostError):
            process_status(empty)
        with patch.object(Path, "read_bytes", side_effect=PermissionError("fixture")), self.assertRaises(HostError):
            process_status(self.proc)

    def test_process_disappearance_is_ignored(self):
        (self.proc / "99").mkdir()
        self.assertFalse(process_status(self.proc)["game_running"])

    def test_orphan_manager_detected_without_reading_environment(self):
        self.assertFalse(process_status(self.proc)["manager_running"])
        self.process(2, "Slotsloader Man", [r"Z:\app\Slotsloader Manager.exe"])
        self.assertTrue(process_status(self.proc)["manager_running"])
        self.process(2, "wine", [r"C:\app\Slotsloader CLI.exe", "replace-install"])
        self.assertTrue(process_status(self.proc)["manager_running"])

    def test_selected_root_alias_canonicalized_but_contained_links_rejected(self):
        alias = self.root / "alias"
        alias.symlink_to(self.game)
        self.assertEqual(self.game, self.bridge.validate(str(alias), "game"))
        child = self.game / "natives"
        child.symlink_to(self.library)
        for path in (child / "new.file", self.game / "NATIVES/new.file"):
            with self.subTest(path=str(path)), self.assertRaises(HostError):
                self.bridge.contained(wine_path(path.parent.parent) + "\\" + path.parent.name + "\\new.file")

    def test_nonexistent_tail_and_actual_mount_paths_allowed(self):
        value = str(self.library / "new/subfolder/file.mesh")
        self.assertEqual(Path(value), self.bridge.contained(value, str(self.library)))
        self.assertFalse(Path(value).exists())
        with self.assertRaises(HostError):
            self.bridge.contained(str(self.app), str(self.library))

    def test_case_fallback_and_ambiguous_names(self):
        (self.library / "Natives").mkdir()
        self.assertEqual(self.library / "Natives/new", self.bridge.contained(str(self.library / "natives/new")))
        (self.library / "NATIVES").mkdir()
        for name in ("natives", "Natives", "NATIVES"):
            with self.subTest(name=name), self.assertRaises(HostError):
                self.bridge.contained(str(self.library / name / "new"))

    def test_replaced_game_on_same_filesystem_refused(self):
        self.bridge.validate(str(self.game), "game")
        self.game.rename(self.game.with_name("original-game"))
        self.game.mkdir()
        (self.game / "StreetFighter6.exe").write_bytes(b"different fixture")
        with self.assertRaises(HostError):
            self.bridge.validate(str(self.game), "game")
        with self.assertRaises(HostError):
            self.bridge.contained(str(self.game / "natives/new.file"))

    def test_disconnected_game_does_not_recreate_paths(self):
        self.bridge.validate(str(self.game), "game")
        (self.game / "StreetFighter6.exe").unlink()
        with self.assertRaises(HostError):
            self.bridge.contained(str(self.game / "natives/new.file"))
        self.assertFalse((self.game / "natives").exists())

    def test_missing_root_and_executable_symlink_rejected(self):
        with self.assertRaises(HostError):
            self.bridge.validate(str(self.root / "missing"), "game")
        (self.game / "StreetFighter6.exe").unlink()
        (self.game / "StreetFighter6.exe").symlink_to(self.library / "dummy.exe")
        with self.assertRaises(HostError):
            self.bridge.validate(str(self.game), "game")

    def test_session_lock_conflicts_and_releases(self):
        lock = self.root / "locks/manager.lock"
        with SessionLock(lock):
            with self.assertRaises(HostError):
                with SessionLock(lock):
                    self.fail("A second native Manager session acquired the lock")
        with SessionLock(lock):
            pass
        link = self.root / "bad-lock"
        link.symlink_to(lock)
        with self.assertRaises(OSError):
            with SessionLock(link):
                self.fail("Lock symlink accepted")

    def test_preferred_setup_roots_are_discovered(self):
        bridge = HostBridge(self.app, preferred_game=self.game, preferred_library=self.library, home=self.root / "empty")
        found = bridge.dispatch("GET", "/discover", {})
        self.assertEqual(wine_path(self.game), found["game_root"])
        self.assertEqual([wine_path(self.library)], found["libraries"])

    def test_desktop_operations_are_fixed_and_urls_restricted(self):
        with patch.object(self.bridge, "_desktop_open") as opener:
            self.bridge.dispatch("POST", "/launch", {})
            opener.assert_called_once_with("steam://rungameid/1364780")
            for value in ("file:///etc/passwd", "javascript:alert(1)", "https://name:password@example.com", "https://example.com\nno"):
                with self.subTest(value=value), self.assertRaises(HostError):
                    self.bridge.dispatch("POST", "/open", {"url": value})
            self.bridge.dispatch("POST", "/open", {"url": "https://example.com/docs"})
            opener.assert_called_with("https://example.com/docs")
            self.process(2, "StreetFighter6.e", ["StreetFighter6.exe"])
            opener.reset_mock()
            self.bridge.dispatch("POST", "/launch", {})
            opener.assert_not_called()

    def test_trash_failure_keeps_source_and_never_falls_back(self):
        source = self.library / "mod.zip"
        source.write_bytes(b"preserve")
        with patch("host.shutil.which", return_value="/usr/bin/gio"), patch("host.subprocess.run", return_value=subprocess.CompletedProcess([], 1)) as run:
            with self.assertRaises(HostError):
                self.bridge.dispatch("POST", "/trash", {"path": str(source)})
            self.assertEqual(b"preserve", source.read_bytes())
            self.assertEqual(["/usr/bin/gio", "trash", "--", str(source)], run.call_args.args[0])
        with patch("host.shutil.which", return_value=None), self.assertRaises(HostError):
            self.bridge.dispatch("POST", "/trash", {"path": str(source)})
        self.assertTrue(source.exists())

    def test_trash_success_and_protected_roots(self):
        source = self.library / "mod.zip"
        source.write_bytes(b"fixture")
        def trash(*args, **kwargs):
            source.unlink()  # Simulated native tool, confined to this fixture.
            return subprocess.CompletedProcess([], 0)
        with patch("host.shutil.which", return_value="/usr/bin/gio"), patch("host.subprocess.run", side_effect=trash):
            self.bridge.dispatch("POST", "/trash", {"path": str(source)})
        for path in (self.app, self.library, self.root):
            with self.subTest(path=str(path)), self.assertRaises(HostError):
                self.bridge.dispatch("POST", "/trash", {"path": str(path)})
        source.symlink_to(self.game / "StreetFighter6.exe")
        with self.assertRaises(HostError):
            self.bridge.dispatch("POST", "/trash", {"path": str(source)})

    def test_authenticated_http_lifecycle_and_error_contract(self):
        with self.bridge as bridge:
            def request(route, body=None, token=None, extra=None):
                conn = http.client.HTTPConnection("127.0.0.1", int(bridge.url.rsplit(":", 1)[1]), timeout=3)
                headers = {"X-Slotsloader-Token": bridge.token if token is None else token}
                headers.update(extra or {})
                payload = None if body is None else json.dumps(body)
                conn.request("GET" if body is None else "POST", route, body=payload, headers=headers)
                response = conn.getresponse()
                result = response.status, json.loads(response.read())
                conn.close()
                return result
            status, result = request("/status")
            self.assertEqual(200, status)
            self.assertTrue(result["ok"])
            self.assertEqual(1, result["protocol"])
            self.assertEqual(403, request("/status", token="wrong")[0])
            self.assertEqual(403, request("/status", token="é")[0])
            self.assertEqual(400, request("/status", extra={"Origin": "http://example.com"})[0])
            self.assertEqual(409, request("/shell", {"command": "anything"})[0])
            self.assertEqual(409, request("/session", {"event": "heartbeat"})[0])
            self.assertEqual(200, request("/session", {"event": "started"})[0])
            self.assertTrue(bridge.session_started)
            self.assertEqual(409, request("/session", {"event": "started"})[0])
            self.assertEqual(200, request("/session", {"event": "heartbeat"})[0])
            self.assertEqual(200, request("/session", {"event": "exited", "exit_code": 7})[0])
            self.assertEqual(7, bridge.session_exit_code)
            self.assertEqual(409, request("/session", {"event": "heartbeat"})[0])

    def test_app_trash_requires_exact_removed_copy_inventory(self):
        removed = self.app / "Data/replace-store/.removed/abc123-20261022-001"
        (removed / "files").mkdir(parents=True)
        record = removed / "record.json"
        record.write_text(json.dumps({"Format": "sf6-replace-removed-v1", "Mod": {"Id": "abc123"}}))
        self.assertTrue(self.bridge._disposable_app_copy(removed))
        self.assertFalse(self.bridge._disposable_app_copy(removed.parent))
        record.write_text(json.dumps({"Format": "sf6-replace-removed-v1", "Mod": {"Id": "other"}}))
        self.assertFalse(self.bridge._disposable_app_copy(removed))
        record.unlink()
        record.symlink_to(self.game / "StreetFighter6.exe")
        self.assertFalse(self.bridge._disposable_app_copy(removed))
        leftover = self.app / "Data/leftover-paks/removed-001"
        leftover.mkdir(parents=True)
        pak = leftover / "re_chunk_000.pak.patch_001.pak"
        pak.write_bytes(b"fixture-pak")
        (leftover / "record.json").write_text(json.dumps({"Format": "sf6-leftover-pak-v1", "Name": pak.name, "Length": pak.stat().st_size}))
        self.assertTrue(self.bridge._disposable_app_copy(pak))
        self.assertFalse(self.bridge._disposable_app_copy(leftover))
        pak.write_bytes(b"changed")
        self.assertFalse(self.bridge._disposable_app_copy(pak))

    def test_inventoried_removed_copy_trash_preserves_other_data(self):
        removed = self.app / "Data/replace-store/.removed/abc123-20261022-001"
        (removed / "files").mkdir(parents=True)
        (removed / "record.json").write_text(json.dumps({"Format": "sf6-replace-removed-v1", "Mod": {"Id": "abc123"}}))
        settings = self.app / "Data/settings.json"
        settings.write_text("fixture settings must remain")
        destination = self.library / "simulated-trash"
        def trash(*args, **kwargs):
            self.assertEqual(str(removed), args[0][-1])
            removed.rename(destination)
            return subprocess.CompletedProcess([], 0)
        with patch("host.shutil.which", return_value="/usr/bin/gio"), patch("host.subprocess.run", side_effect=trash):
            self.bridge.dispatch("POST", "/trash", {"path": str(removed)})
        self.assertFalse(removed.exists())
        self.assertTrue((destination / "record.json").is_file())
        self.assertEqual("fixture settings must remain", settings.read_text())
        with self.assertRaises(HostError):
            self.bridge.dispatch("POST", "/trash", {"path": str(settings)})


class DiscoveryFixtures(unittest.TestCase):
    def test_native_flatpak_alias_and_external_library(self):
        with tempfile.TemporaryDirectory(prefix="slotsloader-steam-test-") as directory:
            home = Path(directory).resolve()
            steam = home / ".local/share/Steam"
            (steam / "steamapps").mkdir(parents=True)
            (home / ".steam").mkdir()
            (home / ".steam/root").symlink_to(steam)
            flatpak = home / ".var/app/com.valvesoftware.Steam/.local/share/Steam"
            (flatpak / "steamapps").mkdir(parents=True)
            sd = home / "SD Card/Steam Library"
            game = sd / "steamapps/common/Street Fighter 6"
            game.mkdir(parents=True)
            (game / "StreetFighter6.exe").touch()
            (steam / "steamapps/libraryfolders.vdf").write_text('"libraryfolders" { "1" { "path" "' + str(sd) + '" } }')
            (sd / "steamapps/appmanifest_1364780.acf").write_text('"AppState" { "installdir" "Street Fighter 6" }')
            result = discover_installations(home)
            self.assertEqual(str(game), result["game_root"])
            self.assertEqual(2, len(result["steam_roots"]))
            self.assertIn(str(sd), result["steam_libraries"])
            second = flatpak / "steamapps/common/Street Fighter 6"
            second.mkdir(parents=True)
            (second / "StreetFighter6.exe").touch()
            result = discover_installations(home)
            self.assertEqual("", result["game_root"])
            self.assertEqual(2, len(result["game_roots"]))
            (sd / "steamapps/appmanifest_1364780.acf").write_text('"installdir" "../../outside"')
            self.assertNotIn(str(game), discover_installations(home)["game_roots"])


if __name__ == "__main__":
    unittest.main()
