"""Installer, update recovery and session-lifetime tests using disposable fixtures."""
import hashlib
import json
from pathlib import Path
import tempfile
import tarfile
import time
import types
import unittest
from unittest.mock import patch

import launcher


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base / "source with spaces"
        self.target = self.base / "installed"
        self.source.mkdir()
        self.make_package()

    def make_package(self, version="one"):
        names = ["app/" + launcher.EXE, "app/" + launcher.EXE + ".config",
                 "linux/launcher.py", "linux/host.py", "linux/bottle.yml", "README.md"]
        files = {}
        for name in names:
            path = self.source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            content = (version + name).encode()
            path.write_bytes(content)
            files[name] = hashlib.sha256(content).hexdigest()
        (self.source / "package.json").write_text(json.dumps({"format": 1, "version": version, "files": files}))

    def test_packaged_archive_rehearsal(self):
        package = launcher.os.environ.get("SLOTSLOADER_TEST_PACKAGE")
        if not package:
            self.skipTest("Set SLOTSLOADER_TEST_PACKAGE for a packaged install rehearsal")
        extracted = self.base / "extracted"
        extracted.mkdir()
        with tarfile.open(package) as archive:
            members = archive.getmembers()
            self.assertTrue(members)
            for member in members:
                self.assertFalse(member.issym() or member.islnk())
                self.assertFalse(member.name.startswith("/") or ".." in Path(member.name).parts)
                self.assertNotIn("Data", Path(member.name).parts)
                if member.name.endswith((".desktop", ".sh")):
                    self.assertTrue(member.mode & 0o111)
            archive.extractall(extracted)
        source = next(extracted.iterdir())
        manifest = launcher.verify_package(source)
        self.assertIn("app/7-Zip/7z.exe", manifest["files"])
        self.assertIn("app/7-Zip/7z.dll", manifest["files"])
        self.assertIn("Source-Available Personal Use License", (source / "LICENSE").read_text())
        self.assertIn("bcdec", (source / "THIRD_PARTY_NOTICES.md").read_text())
        self.assertIn("MIT License", (source / "SLOTSLOADER-LICENSE.txt").read_text())
        launcher.install_package(source, self.target)
        data = self.target / "app/Data"
        data.mkdir()
        (data / "recovery.json").write_bytes(b"retain recovery byte-for-byte")
        launcher.install_package(source, self.target)
        self.assertEqual((data / "recovery.json").read_bytes(), b"retain recovery byte-for-byte")
        self.assertEqual(manifest["version"], launcher.verify_package(self.target)["version"])

    def test_install_and_upgrade_preserve_data_and_untracked_files(self):
        launcher.install_package(self.source, self.target)
        data = self.target / "app/Data"
        data.mkdir()
        (data / "recovery.json").write_bytes(b"precious recovery")
        (self.target / "app/local-user-note.txt").write_text("preserve")
        self.make_package("two")
        launcher.install_package(self.source, self.target)
        self.assertTrue((self.target / "app" / launcher.EXE).read_text().startswith("two"))
        self.assertEqual((data / "recovery.json").read_bytes(), b"precious recovery")
        self.assertEqual((self.target / "app/local-user-note.txt").read_text(), "preserve")
        self.assertFalse((self.target / "update-journal.json").exists())

    def test_bad_checksum_fails_before_any_existing_file_changes(self):
        launcher.install_package(self.source, self.target)
        original = (self.target / "app" / launcher.EXE).read_bytes()
        (self.source / "app" / launcher.EXE).write_text("tampered")
        with self.assertRaises(launcher.SetupError):
            launcher.install_package(self.source, self.target)
        self.assertEqual((self.target / "app" / launcher.EXE).read_bytes(), original)

    def test_manifest_cannot_overwrite_data_or_configuration(self):
        for name in ["app/Data/key.txt", "../escape", "/absolute", "linux-state.json", "app/../escape", "app\\escape", "app/a:b"]:
            with self.subTest(name=name):
                self.make_package()
                manifest = json.loads((self.source / "package.json").read_text())
                manifest["files"][name] = "0" * 64
                (self.source / "package.json").write_text(json.dumps(manifest))
                with self.assertRaises(launcher.SetupError):
                    launcher.verify_package(self.source)

    def test_partial_update_is_rolled_back_and_data_is_untouched(self):
        launcher.install_package(self.source, self.target)
        name = "app/" + launcher.EXE
        original = (self.target / name).read_bytes()
        backup = self.target / ("update-" + "f" * 32)
        (backup / "app").mkdir(parents=True)
        (backup / name).write_bytes(original)
        (self.target / name).write_text("half-updated")
        (self.target / "app/new.dll").write_text("new")
        data = self.target / "app/Data"
        data.mkdir()
        (data / "settings.json").write_text("keep")
        launcher.atomic_json(self.target / "update-journal.json", {"transaction": backup.name, "files": [
            {"name": name, "existed": True}, {"name": "app/new.dll", "existed": False}]})
        launcher.recover_update(self.target)
        self.assertEqual((self.target / name).read_bytes(), original)
        self.assertFalse((self.target / "app/new.dll").exists())
        self.assertEqual((data / "settings.json").read_text(), "keep")

    def test_replace_failure_restores_previous_version(self):
        launcher.install_package(self.source, self.target)
        original = (self.target / "app" / launcher.EXE).read_bytes()
        self.make_package("two")
        replace = launcher.os.replace
        failed = [False]
        def fail_once(source, target):
            if str(source).endswith(".incoming") and not failed[0]:
                failed[0] = True
                raise OSError("simulated storage failure")
            return replace(source, target)
        with patch.object(launcher.os, "replace", side_effect=fail_once):
            with self.assertRaises(OSError):
                launcher.install_package(self.source, self.target)
        self.assertEqual((self.target / "app" / launcher.EXE).read_bytes(), original)
        self.assertFalse((self.target / "update-journal.json").exists())

    def test_symlink_in_target_rejected(self):
        (self.target / "app").mkdir(parents=True)
        outside = self.base / "outside"
        outside.write_text("do not replace")
        try:
            (self.target / "app" / launcher.EXE).symlink_to(outside)
        except OSError:
            self.skipTest("symlink creation unavailable")
        with self.assertRaises(launcher.SetupError):
            launcher.install_package(self.source, self.target)
        self.assertEqual(outside.read_text(), "do not replace")


class LaunchTests(unittest.TestCase):
    def test_install_root_canonicalizes_desktop_data_parent(self):
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp)
            actual = parent / "real-data"
            actual.mkdir()
            alias = parent / "desktop-data"
            alias.symlink_to(actual, target_is_directory=True)
            with patch.dict(launcher.os.environ, {"XDG_DATA_HOME": str(alias)}):
                self.assertEqual(launcher.install_root(), actual / "slotsloader-manager")

    def test_kde_progress_does_not_orphan_dialog_without_dbus_client(self):
        dialog = launcher.Dialog(headless=True)
        dialog.headless, dialog.tool = False, "/usr/bin/kdialog"
        with patch.object(launcher.shutil, "which", return_value=None), patch.object(dialog, "call") as call:
            with dialog.busy("Preparing runtime"):
                pass
            call.assert_not_called()

    def test_scopes_include_custom_and_recorded_stores_and_exact_fluffy_game(self):
        try:
            import host
        except ImportError:
            self.skipTest("Linux host module unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            data = root / "app/Data"
            data.mkdir(parents=True)
            store, recorded, previous = (root / p for p in ("customstore", "recordedstore", "oldstore"))
            fluffy = root / "Fluffy/Games/SF6"
            explicit = root / "explicit-external/SF6"
            generic_mods = root / "generic/Mods"
            for path in (store, recorded, previous, fluffy / "Mods", fluffy / "Presets", explicit, generic_mods, generic_mods.parent / "Backup"):
                path.mkdir(parents=True, exist_ok=True)
            (fluffy / "installed.ini").write_text("fixture")
            (data / "replace-settings.json").write_text(json.dumps({"StoreLocation": launcher.wine_path(store), "FluffyRoots": [launcher.wine_path(explicit)]}))
            (data / "replace-state.json").write_text(json.dumps({"StoreRoot": launcher.wine_path(recorded), "PreviousStoreRoots": [launcher.wine_path(previous), "/missing/history"], "Mods": [{}]}))
            actual = launcher.additional_roots(root, [str(fluffy / "Mods"), str(generic_mods)])
            self.assertEqual(set(actual), {str(store), str(recorded), str(previous), str(fluffy), str(explicit)})
            self.assertNotIn(str(fluffy.parent), actual)
            self.assertNotIn(str(fluffy.parent.parent), actual)
            self.assertNotIn(str(generic_mods.parent), actual)
            self.assertFalse(Path("/missing/history").exists())

    def test_missing_active_custom_store_is_not_recreated(self):
        try:
            import host
        except ImportError:
            self.skipTest("Linux host module unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            data = root / "app/Data"
            data.mkdir(parents=True)
            missing = root / "unmounted/card/store"
            (data / "replace-settings.json").write_text(json.dumps({"StoreLocation": launcher.wine_path(missing)}))
            with self.assertRaises(launcher.SetupError):
                launcher.additional_roots(root, [])
            self.assertFalse(missing.exists())

    def test_wine_drive_mapping_requires_prefix_and_blocks_repair_rebinding(self):
        try:
            import host
        except ImportError:
            self.skipTest("Linux host module unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            data = root / "app/Data"
            data.mkdir(parents=True)
            prefix = root / "prefix"
            store = prefix / "drive_c/CustomStore"
            store.mkdir(parents=True)
            self.assertEqual(launcher.native_path("C:\\CustomStore", prefix), store)
            with self.assertRaises(launcher.SetupError):
                launcher.native_path("C:\\CustomStore")
            with self.assertRaises(launcher.SetupError):
                launcher.native_path("relative/path")
            (data / "replace-settings.json").write_text(json.dumps({"StoreLocation": "C:\\CustomStore"}))
            self.assertEqual(launcher.additional_roots(root, [], prefix), [str(store)])
            with self.assertRaises(launcher.SetupError):
                launcher.require_stable_paths_for_repair(root)
            self.assertEqual(json.loads((data / "replace-settings.json").read_text())["StoreLocation"], "C:\\CustomStore")
            (data / "replace-settings.json").write_text(json.dumps({"StoreLocation": launcher.wine_path(store)}))
            launcher.require_stable_paths_for_repair(root)

    def test_cancelled_game_selection_preserves_previous_settings(self):
        try:
            import host
        except ImportError:
            self.skipTest("Linux host module unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            game = root / "game"
            game.mkdir()
            (game / "StreetFighter6.exe").write_text("fixture")
            state = {"game_root": str(game), "library_root": "/oldmods", "configured": True}
            original = dict(state)
            dialog = unittest.mock.Mock()
            dialog.confirm.return_value = False
            dialog.folder.return_value = None
            with patch.object(host, "discover_installations", return_value={}):
                with self.assertRaises(launcher.SetupError):
                    launcher.configure(root, state, dialog)
            self.assertEqual(state, original)
            self.assertFalse((root / "app/Data/settings.json").exists())

    def test_explicit_setup_preserves_other_settings_and_additional_libraries(self):
        try:
            import host
        except ImportError:
            self.skipTest("Linux host module unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            game, old, library, second = (root / name for name in ("game", "old", "new", "second"))
            for path in (game, old, library, second):
                path.mkdir()
            (game / "StreetFighter6.exe").write_text("fixture")
            settings_path = root / "app/Data/settings.json"
            settings_path.parent.mkdir(parents=True)
            settings_path.write_text(json.dumps({"GameRoot": launcher.wine_path(game),
                "Libraries": [launcher.wine_path(old), launcher.wine_path(second)], "SevenZipPath": "keep", "FutureField": {"preserved": True}}))
            state = {"game_root": str(game), "library_root": str(old)}
            dialog = unittest.mock.Mock()
            dialog.confirm.side_effect = [True, False]
            dialog.folder.return_value = library
            with patch.object(host, "discover_installations", return_value={}):
                launcher.configure(root, state, dialog)
            settings = json.loads(settings_path.read_text())
            self.assertEqual(settings["Libraries"], [launcher.wine_path(library), launcher.wine_path(second)])
            self.assertEqual(settings["SevenZipPath"], "keep")
            self.assertEqual(settings["FutureField"], {"preserved": True})
            self.assertEqual(state["library_root"], str(library))

    def test_installed_desktop_exec_roundtrip_with_real_gio(self):
        try:
            import gi
            from gi.repository import Gio
        except ImportError:
            self.skipTest("Gio desktop parser unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'installed with spaces & $dollar %percent "quote"'
            (root / "linux").mkdir(parents=True)
            fixture = root / "linux/launcher.py"
            fixture.write_text("from pathlib import Path\nPath(__file__).with_suffix('.launched').write_text('done')\n")
            with patch.object(launcher, "data_home", return_value=Path(temp)):
                desktop = launcher.install_shortcut(root)
            app = Gio.DesktopAppInfo.new_from_filename(str(desktop))
            self.assertIsNotNone(app)
            self.assertTrue(app.launch([], None))
            marker = fixture.with_suffix(".launched")
            deadline = time.monotonic() + 5
            while not marker.exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertTrue(marker.exists(), app.get_commandline())

    def test_installer_desktop_exec_roundtrip_with_real_gio(self):
        try:
            import gi
            from gi.repository import Gio
        except ImportError:
            self.skipTest("Gio desktop parser unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'folder with spaces & $dollar %percent "quote"'
            root.mkdir()
            desktop = root / "Install SlotsLoader Manager.desktop"
            desktop.write_bytes((Path(launcher.__file__).parent / desktop.name).read_bytes())
            script = root / "install.sh"
            script.write_text('#!/bin/sh\nprintf done > "$0.launched"\n')
            app = Gio.DesktopAppInfo.new_from_filename(str(desktop))
            self.assertIsNotNone(app)
            self.assertTrue(app.launch([], None))
            marker = root / "install.sh.launched"
            deadline = time.monotonic() + 5
            while not marker.exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertTrue(marker.exists(), app.get_commandline())

    def test_settings_paths_override_previous_launcher_roots(self):
        try:
            import host
        except ImportError:
            self.skipTest("Linux host module unavailable")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            data = root / "app/Data"
            data.mkdir(parents=True)
            (data / "settings.json").write_text(json.dumps({"GameRoot": "Z:\\games\\new", "Libraries": ["Z:\\mods\\new", "Z:\\mods\\second"]}))
            game, libraries = launcher.configured_roots(root, {"game_root": "/old", "library_root": "/oldmods"})
            self.assertEqual(game, "/games/new")
            self.assertEqual(libraries, ["/mods/new", "/mods/second"])

    def test_stale_session_keeps_waiting_until_scan_confirms_closed(self):
        calls = iter([{"manager_running": True}, RuntimeError("unknown"), {"manager_running": False}])
        def scan():
            result = next(calls)
            if isinstance(result, Exception):
                raise result
            return result
        dialog = unittest.mock.Mock()
        with patch.object(launcher.time, "sleep") as sleep:
            launcher.hold_until_manager_stopped(dialog, scan)
        self.assertEqual(sleep.call_count, 2)

    def test_existing_bottle_is_reused_even_if_probe_needs_retry(self):
        with patch.object(launcher, "ensure_bottles"), patch.object(launcher, "run_checked") as run:
            launcher.provision(Path("/fixture"), {"bottle": "SlotsLoader-Manager-fixture", "runtime_ready": False}, None)
            run.assert_not_called()

    def test_cli_json_accepts_status_prefix_and_rejects_missing_data(self):
        result = launcher.parse_cli_json('Looking for bottles...\n[info] okay\n{"mine":{"Path":"mine"}}\n')
        self.assertEqual(result["mine"]["Path"], "mine")
        with self.assertRaises(launcher.SetupError):
            launcher.parse_cli_json("No structured result")

    def test_flatpak_grants_are_scoped_and_arguments_are_not_shell_text(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            selected = root / "game with spaces & symbols"
            command = launcher.flatpak_base(root, [selected, selected])
            self.assertEqual(command[:2], ["flatpak", "run"])
            self.assertNotIn("--filesystem=host", command)
            self.assertNotIn("--filesystem=home", command)
            self.assertEqual(sum(x == "--filesystem=" + str(selected.resolve()) for x in command), 1)
            self.assertEqual(command[-2:], ["--command=bottles-cli", launcher.BOTTLES])

    def test_session_script_has_only_fixed_commands_and_is_private(self):
        with tempfile.TemporaryDirectory() as temp:
            bridge = types.SimpleNamespace(url="http://127.0.0.1:12345", token="safe_TOKEN-123")
            script = launcher.make_session_script(Path(temp), bridge, [])
            text = script.read_text()
            self.assertIn("DisableDelayedExpansion", text)
            self.assertIn('%~dp0..\\app', text)
            self.assertNotIn(str(temp), text)
            if launcher.os.name != "nt":
                self.assertEqual(script.stat().st_mode & 0o777, 0o600)
                self.assertEqual(script.parent.stat().st_mode & 0o777, 0o700)
            with self.assertRaises(launcher.SetupError):
                launcher.make_session_script(Path(temp), bridge, ["bad & whoami"])
            with self.assertRaises(launcher.SetupError):
                launcher.make_session_script(Path(temp), types.SimpleNamespace(url="http://other:12345", token="x"), [])
            for name in ('unsafe" & command', "unsafe\ncommand"):
                with self.assertRaises(launcher.SetupError):
                    launcher.make_session_script(Path(temp) / name, bridge, [])

    def test_detached_cli_exit_does_not_end_session(self):
        bridge = types.SimpleNamespace(session_started=False, session_exited=False, session_last_seen=10, session_exit_code=0)
        process = types.SimpleNamespace(poll=lambda: 0)
        ticks = [0]
        def tick(_):
            ticks[0] += 1
            if ticks[0] == 2:
                bridge.session_started = True
            if ticks[0] == 5:
                bridge.session_exited = True
        with patch.object(launcher.time, "sleep", side_effect=tick), patch.object(launcher.time, "monotonic", return_value=10):
            self.assertEqual(launcher.wait_session(process, bridge), 0)
        self.assertEqual(ticks[0], 5)

    def test_missing_session_times_out_and_removes_secret_file(self):
        with tempfile.TemporaryDirectory() as temp:
            script = Path(temp) / "session.cmd"
            script.write_text("secret")
            bridge = types.SimpleNamespace(session_started=False, session_exited=False)
            process = types.SimpleNamespace(poll=lambda: 0)
            with patch.object(launcher.time, "monotonic", side_effect=[0, 121]):
                with self.assertRaises(launcher.SetupError):
                    launcher.wait_session(process, bridge, script)
            self.assertFalse(script.exists())

    def test_stale_heartbeat_fails_closed(self):
        bridge = types.SimpleNamespace(session_started=True, session_exited=False, session_last_seen=1)
        process = types.SimpleNamespace(poll=lambda: None)
        with patch.object(launcher.time, "monotonic", return_value=99):
            with self.assertRaises(launcher.SetupError):
                launcher.wait_session(process, bridge)

    def test_diagnostic_allowlist_excludes_keys_paths_and_logs(self):
        with tempfile.TemporaryDirectory() as temp:
            state = {"game_root": "/private/game", "library_root": "/private/mods", "token": "SECRET", "api_key": "SECRET", "log": "private"}
            path = launcher.diagnostics(Path(temp), state)
            text = path.read_text()
            self.assertNotIn("SECRET", text)
            self.assertNotIn("/private", text)
            self.assertNotIn('"log"', text)

    def test_desktop_exec_quotes_percent_and_metacharacters(self):
        value = launcher.desktop_quote('/path with " $ ` %/launcher.py')
        self.assertTrue(value.startswith('"') and value.endswith('"'))
        self.assertIn("%%", value)
        self.assertIn('\\"', value)
        self.assertIn('\\$', value)


if __name__ == "__main__":
    unittest.main()
