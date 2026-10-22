using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.11: leftover patch PAKs (ReplaceLeftoverPaks.cs) on generated game folders. Nothing here touches the real game.
    internal static class LeftoverPakChecks
    {
        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }
        private static string Pak(ReplaceChecks.Fixture f, int n) { return Path.Combine(f.Game, ReplaceEngine.PatchPakName(n)); }
        private static void WritePak(string path, string tag) { if (File.Exists(path)) File.Delete(path); PakWriter.WriteFixture(path, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("natives/stm/leftover_" + tag + ".user.2", Encoding.UTF8.GetBytes(tag)) }, 0, 0); }
        private static string Names(IEnumerable<PatchPak> paks) { return String.Join(", ", paks.Select(p => p.Name)); }

        private sealed class StubBin : IRecycleBin
        {
            internal string Folder; internal readonly List<string> Recycled = new List<string>();
            public string Unavailable(string path, bool folder, long bytes) { return null; }
            public void Recycle(string path, bool folder) { Recycled.Add(Path.GetFileName(path)); Directory.CreateDirectory(Folder); File.Move(path, Path.Combine(Folder, Recycled.Count + " " + Path.GetFileName(path))); }
            public void DeletePermanently(string path, bool folder) { File.Delete(path); }
        }

        // Detection: leftover vs the Manager's PAK mod vs PAKs either external-manager root lists vs the empty placeholder vs
        // Slotsloader's costume pak vs an unreadable one; an unreadable installed.ini means none. Removal of a middle PAK
        // leaves the placeholder, of the top one deletes it (other PAKs byte-identical, never renumbered); Undo restores; Undo
        // refused once the number holds another PAK; both refused while SF6 or the external manager runs; the copy goes to
        // the Recycle Bin when its Undo ends.
        internal static void Engine(string root, FakeProcessProbe probe)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "game"), false, ReplaceMethods.Pak);
            ReplaceMod owned = ReplaceChecks.Install(f, "badPak", "Numbered PAK");   // the Manager's PAK mod: patch_002
            Require(owned.Kind == "Pak" && File.Exists(Pak(f, 2)), "the Manager's PAK mod is not patch_002");
            string a = Path.Combine(root, "ManagerA", "Games", "SF6"), b = Path.Combine(root, "ManagerB", "Games", "SF6");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "installed.ini"), "[Pak A]\r\nModName=Pak A\r\ninstalltype=pakcopy_reengine_invalidate\r\nfile=re_chunk_000.pak.patch_003.pak\r\n");
            File.WriteAllText(Path.Combine(b, "installed.ini"), "[Pak B]\r\nModName=Pak B\r\ninstalltype=pakcopy_reengine_invalidate\r\nfile=RE_CHUNK_000.PAK.PATCH_004.PAK\r\n");
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] { a, b }); };
            WritePak(Pak(f, 3), "listed-a"); WritePak(Pak(f, 4), "listed-b");
            File.WriteAllBytes(Pak(f, 5), ReplaceEngine.FluffyDummyPak);
            LoaderCostumePak.WritePlaceholder(Pak(f, 6));   // Slotsloader's marker-only pak (a full one would raise its own orphan notice)
            WritePak(Pak(f, 7), "top");
            Require(LoaderCostumePak.Scan(f.Game).Single(p => p.Number == 6).Kind == PatchPakKind.Costume, "fixture costume pak not recognised");
            using (new FileStream(Pak(f, 8), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))   // in use: Unreadable
                Require(Names(f.Engine.LeftoverPaks()) == "re_chunk_000.pak.patch_001.pak, re_chunk_000.pak.patch_007.pak", "detected: " + Names(f.Engine.LeftoverPaks()));
            File.Delete(Pak(f, 8));
            GameFilesStatus status = f.Engine.Status();
            Require(status.LeftoverPaks.Count == 2 && status.Summary == "" && status.Details.Last() == "2 leftover PAKs not listed by any mod manager (SF6 still loads them): re_chunk_000.pak.patch_001.pak, re_chunk_000.pak.patch_007.pak", "status: '" + status.Summary + "' / " + String.Join(" | ", status.Details));
            using (new FileStream(Path.Combine(a, "installed.ini"), FileMode.Open, FileAccess.Read, FileShare.None))
                Require(f.Engine.LeftoverPaks().Count == 0, "an unreadable installed.ini must mean no leftovers");
            // No external mod manager found (its Mods folder isn't a library): its PAKs must never look like leftovers.
            Func<FluffyInstalls> withManagers = f.Engine.FluffyProvider;
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new string[0]); };
            Require(f.Engine.LeftoverPaks().Count == 0, "no external mod manager found must mean no leftovers");
            f.Engine.FluffyProvider = withManagers;
            // The disabled PAK mod (its number is the empty placeholder now) is never a leftover either; it stays disabled
            // (enabling waits for the external manager's lockout).
            f.Engine.SetEnabled(owned.Id, false);
            Require(ReplaceEngine.IsFluffyDummy(Pak(f, 2)) && Names(f.Engine.LeftoverPaks()) == "re_chunk_000.pak.patch_001.pak, re_chunk_000.pak.patch_007.pak", "with the PAK mod disabled: " + Names(f.Engine.LeftoverPaks()));
            // Its record still claims the number when something else fills it (a file of a record, enabled or not).
            WritePak(Pak(f, 2), "refilled");
            Require(Names(f.Engine.LeftoverPaks()) == "re_chunk_000.pak.patch_001.pak, re_chunk_000.pak.patch_007.pak", "a disabled record's number: " + Names(f.Engine.LeftoverPaks()));
            File.WriteAllBytes(Pak(f, 2), ReplaceEngine.FluffyDummyPak);
            // The CLI finds the two external managers through its settings (this fixture has no Mods library).
            var replaceSettings = ReplaceSettings.Load(); List<string> savedRoots = replaceSettings.FluffyRoots;
            replaceSettings.FluffyRoots = new List<string> { a, b }; replaceSettings.Save();
            string cli;
            try { cli = ReplaceChecks.Cli("replace-status", "--game", f.Game); }
            finally { replaceSettings.FluffyRoots = savedRoots; replaceSettings.Save(); }
            Require(cli.Contains("re_chunk_000.pak.patch_001.pak (leftover: no mod manager lists it, ") && cli.Contains("re_chunk_000.pak.patch_005.pak (empty placeholder)") && cli.Contains("leftover PAK") && cli.Contains("(remove: select it on the Installed page, then Remove PAK)"), "replace-status: " + cli);

            // Refused while SF6 or the external manager runs, and for a PAK that isn't a leftover; nothing changes.
            var before = ReplaceChecks.Snapshot(f.Game); string folder;
            probe.Game = true; ReplaceChecks.Refused(delegate { f.Engine.RemoveLeftoverPak("re_chunk_000.pak.patch_001.pak", out folder); }, "Close SF6 to remove a leftover PAK"); probe.Game = false;
            probe.Fluffy = true; ReplaceChecks.Refused(delegate { f.Engine.RemoveLeftoverPak("re_chunk_000.pak.patch_001.pak", out folder); }, "Close the external mod manager to remove a leftover PAK"); probe.Fluffy = false;
            ReplaceChecks.Refused(delegate { f.Engine.RemoveLeftoverPak("re_chunk_000.pak.patch_003.pak", out folder); }, "is no longer a leftover PAK");
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after the refusals");

            // A middle PAK: the empty placeholder keeps its number; nothing else changes; Undo restores it byte for byte.
            byte[] p1 = File.ReadAllBytes(Pak(f, 1)), p7 = File.ReadAllBytes(Pak(f, 7));
            var others = Enumerable.Range(2, 6).ToDictionary(n => n, n => ReplaceEngine.Sha(Pak(f, n)));
            string message = f.Engine.RemoveLeftoverPak("re_chunk_000.pak.patch_001.pak", out folder);
            Require(ReplaceEngine.IsFluffyDummy(Pak(f, 1)) && message.Contains("empty placeholder") && File.ReadAllBytes(Path.Combine(folder, "re_chunk_000.pak.patch_001.pak")).SequenceEqual(p1) && folder.StartsWith(Path.Combine(f.Data, "leftover-paks"), StringComparison.OrdinalIgnoreCase), "middle removal: " + message);
            Require(others.All(kv => ReplaceEngine.Sha(Pak(f, kv.Key)) == kv.Value) && !File.Exists(Pak(f, 8)), "another PAK changed or was renumbered");
            Require(Names(f.Engine.LeftoverPaks()) == "re_chunk_000.pak.patch_007.pak", "after removal: " + Names(f.Engine.LeftoverPaks()));
            probe.Fluffy = true; ReplaceChecks.Refused(delegate { f.Engine.RestoreLeftoverPak(folder); }, "Close the external mod manager to put the leftover PAK back"); probe.Fluffy = false;
            probe.Game = true; ReplaceChecks.Refused(delegate { f.Engine.RestoreLeftoverPak(folder); }, "Close SF6 to put the leftover PAK back"); probe.Game = false;
            Require(ReplaceEngine.IsFluffyDummy(Pak(f, 1)), "a refused Undo changed the placeholder");
            f.Engine.RestoreLeftoverPak(folder);
            Require(File.ReadAllBytes(Pak(f, 1)).SequenceEqual(p1) && !Directory.Exists(folder), "Undo of the middle removal");

            // The top PAK is deleted (no placeholder); Undo puts it back.
            message = f.Engine.RemoveLeftoverPak("re_chunk_000.pak.patch_007.pak", out folder);
            Require(!File.Exists(Pak(f, 7)) && message == "" && Enumerable.Range(1, 6).All(n => File.Exists(Pak(f, n))), "top removal: " + message);
            f.Engine.RestoreLeftoverPak(folder);
            Require(File.ReadAllBytes(Pak(f, 7)).SequenceEqual(p7) && !Directory.Exists(folder), "Undo of the top removal");

            // Another PAK took the number meanwhile: Undo refuses and leaves it; the ended Undo's copy goes to the Recycle Bin.
            f.Engine.RemoveLeftoverPak("re_chunk_000.pak.patch_001.pak", out folder);
            WritePak(Pak(f, 1), "newer");
            string newer = ReplaceEngine.Sha(Pak(f, 1));
            ReplaceChecks.Refused(delegate { f.Engine.RestoreLeftoverPak(folder); }, "is now another PAK, so the leftover PAK can't go back");
            Require(ReplaceEngine.Sha(Pak(f, 1)) == newer && Directory.Exists(folder), "the refused Undo changed the PAK or lost the copy");
            var bin = new StubBin { Folder = Path.Combine(root, "bin") };
            Require(f.Engine.DiscardLeftoverCopiesExcept(new[] { folder }, bin) == 0 && Directory.Exists(folder), "a kept copy was discarded");
            Require(f.Engine.DiscardLeftoverCopiesExcept(new string[0], bin) == 1 && !Directory.Exists(folder) && bin.Recycled.SequenceEqual(new[] { "re_chunk_000.pak.patch_001.pak" }) && File.ReadAllBytes(Directory.GetFiles(bin.Folder).Single()).SequenceEqual(p1), "the copy did not go to the Recycle Bin: " + String.Join(", ", bin.Recycled));
            // The window's Undo end goes through DeleteRemoved (the check run's stand-in Recycle Bin, never the real one).
            Require(RecycleBin.Current is CheckRecycleBin, "the checks must run with the stand-in Recycle Bin");
            f.Engine.RemoveLeftoverPak("re_chunk_000.pak.patch_007.pak", out folder);
            f.Engine.DeleteRemoved(folder);
            Require(!Directory.Exists(folder) && !File.Exists(Pak(f, 7)), "DeleteRemoved did not discard the leftover copy");
        }
    }

    // 1.0.11: the Installed page's leftover row in the window.
    internal static partial class ReplaceUiChecks
    {
        internal static void LeftoverPakRow(MainForm form, ReplaceChecks.Fixture f, FakeProcessProbe probe, Dictionary<string, string> clean)
        {
            string pak = Path.Combine(f.Game, "re_chunk_000.pak.patch_001.pak"), copies = Path.Combine(AppConfig.DataDir, "leftover-paks");
            byte[] original = File.ReadAllBytes(pak);
            int asked = 0;
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { asked++; return DialogResult.Cancel; };
            Func<int> copyCount = delegate { return Directory.Exists(copies) ? Directory.GetDirectories(copies).Length : 0; };
            Action refresh = delegate { Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); };
            try
            {
                refresh();
                GameModRow row = SelectGameRow(form, r => r.Leftover != null);
                string sub = (string)typeof(MainForm).GetMethod("LeftoverRowText", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { row });
                Require(row != null && row.Name == "Leftover PAK" && row.Leftover.Name == "re_chunk_000.pak.patch_001.pak" && sub == "not listed by any mod manager   ·   " + Utilities.FriendlyBytes(original.Length) + "   ·   re_chunk_000.pak.patch_001.pak", "leftover row: " + (row == null ? "none" : row.Name + " / " + sub));
                FlatButton remove = Get<FlatButton>(form, "removeInstalledButton");
                Require(remove.Visible && remove.Enabled && remove.Text == "Remove PAK" && Get<Label>(form, "installedTitle").Text == "Leftover PAK" && Get<TextBox>(form, "installedHelp").Text.Contains("It is the last patch PAK") && !Get<FlatButton>(form, "toggleInstalledButton").Visible, "leftover actions: " + remove.Text + " visible " + remove.Visible + " enabled " + remove.Enabled);
                // SF6 running: refused inline, nothing changes.
                probe.Game = true; Call(form, "PollGame");
                remove.PerformClick(); WaitIdle(form);
                Require(File.Exists(pak) && Get<Label>(form, "statusText").Text == "Close SF6 to remove a leftover PAK." && !form.UndoAvailable, "while SF6 runs: " + Get<Label>(form, "statusText").Text);
                probe.Game = false; Call(form, "PollGame");
                // Remove PAK: no popup, the copy kept for Undo, the row gone.
                SelectGameRow(form, r => r.Leftover != null); remove.PerformClick(); WaitIdle(form); refresh();
                Require(!File.Exists(pak) && asked == 0 && form.UndoAvailable && form.UndoLabel == "Removed leftover PAK re_chunk_000.pak.patch_001.pak" && copyCount() == 1 && !Get<List<GameModRow>>(form, "gameRows").Any(r => r.Leftover != null), "Remove PAK: exists " + File.Exists(pak) + ", undo '" + form.UndoLabel + "', copies " + copyCount());
                // Undo puts it back byte for byte.
                form.PerformUndo(); WaitIdle(form); refresh();
                Require(File.Exists(pak) && File.ReadAllBytes(pak).SequenceEqual(original) && copyCount() == 0 && Get<List<GameModRow>>(form, "gameRows").Any(r => r.Leftover != null) && asked == 0, "Undo: exists " + File.Exists(pak) + ", copies " + copyCount() + ", status " + Get<Label>(form, "statusText").Text);
                // An Undo that ends unused: the copy goes to the Recycle Bin (the check run's stand-in, never the real one).
                Require(RecycleBin.Current is CheckRecycleBin, "the checks must run with the stand-in Recycle Bin");
                SelectGameRow(form, r => r.Leftover != null); remove.PerformClick(); WaitIdle(form);
                Require(!File.Exists(pak) && copyCount() == 1, "second removal");
                Call(form, "ClearUndo", true);
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (copyCount() > 0 && DateTime.UtcNow < deadline) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                Require(copyCount() == 0 && !form.UndoAvailable, "the ended Undo kept its copy");
            }
            finally
            {
                ThemedMessageBox.TestResponder = null; probe.Game = false; Call(form, "PollGame");
                if (!File.Exists(pak)) File.WriteAllBytes(pak, original);
                refresh();
            }
            ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), "after the leftover PAK checks");
        }
    }
}
