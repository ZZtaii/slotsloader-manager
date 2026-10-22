using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // 2.4.2: regression checks for the 2.0.1 code-review follow-ups (run by --replace-check, ReplaceChecks.Run).
    internal static class ReviewFollowupChecks
    {
        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }

        // 1. replace-state.json read while a change commits it (File.Replace).
        internal static void SharedStateReads(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Cammy Colors");
            // A reader holding the file the 2.4.2 way (ReadWrite | Delete) never blocks a commit.
            using (new FileStream(f.Engine.StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                f.Engine.SetEnabled(a.Id, false);
            Require(!f.Engine.LoadState().Find(a.Id).Enabled, "the commit did not happen while a shared reader held the state");
            // The 2.0.1-2.4.1 read (File.ReadAllText shares Read only) held across the commit makes the change fail and
            // roll back: what a background health check could do to a user's change (bounded by the writer's retries).
            var before = ReplaceChecks.Snapshot(f.Game); string stateBefore = File.ReadAllText(f.Engine.StatePath);
            using (new FileStream(f.Engine.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                ReplaceChecks.Refused(delegate { f.Engine.SetEnabled(a.Id, true); }, "being used");
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after the refused commit");
            Require(File.ReadAllText(f.Engine.StatePath) == stateBefore && !f.Engine.JournalPending, "the refused commit changed the state or left a journal");
            // Concurrent: readers loop over every state reader a background task uses while commits run.
            int reads = 0, errors = 0; string first = null; bool stop = false;
            int retriedBefore = ReplaceEngine.SharedReadRetried;
            var threads = Enumerable.Range(0, 4).Select(i => new Thread(delegate()
            {
                var reader = new ReplaceEngine(f.Game, f.Data, f.Engine.Settings);
                while (!Volatile.Read(ref stop))
                {
                    try
                    {
                        reader.LoadState();
                        if (i % 2 == 0) ReplaceEngine.KnownStoreRoots(f.Data, f.Engine.Settings); else reader.ListRemoved();
                        Interlocked.Increment(ref reads);
                    }
                    catch (Exception ex) { Interlocked.Increment(ref errors); Interlocked.CompareExchange(ref first, ex.GetType().Name + ": " + ex.Message, null); }
                }
            }) { IsBackground = true }).ToList();
            foreach (Thread t in threads) t.Start();
            int commits = 0;
            try
            {
                for (int i = 0; i < 40; i++) { f.Engine.SetEnabled(a.Id, i % 2 == 0); commits++; }
                string folder; f.Engine.Uninstall(a.Id, true, out folder); commits++;
                ReplaceMod back; f.Engine.RestoreRemoved(folder, out back); commits++;
            }
            finally { Volatile.Write(ref stop, true); foreach (Thread t in threads) t.Join(10000); }
            Console.WriteLine("NOTE shared reads: " + reads + " reads by 4 threads during " + commits + " commits, " + errors + " errors, " + (ReplaceEngine.SharedReadRetried - retriedBefore) + " read retries");
            Require(errors == 0, "a state read failed during commits: " + first);
            Require(reads >= 40, "too few concurrent reads to be meaningful: " + reads);
        }

        // 2. The move into .removed fails after the commit (a file of the store folder is open in another program).
        internal static void TrashFailure(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Cammy Colors");
            ReplaceMod b = ReplaceChecks.Install(f, "modB", "Cammy Alt");
            string storeB = Path.Combine(f.Data, "store", b.Id), pending = f.Engine.TrashPendingPath;
            string held = Directory.GetFiles(storeB, "*", SearchOption.AllDirectories).First();
            string message, folder;
            Action<TrashMove> failMove = delegate { throw new IOException("Access denied: fixture removed-copy move"); };
            // Unix permits renaming an open directory. Inject the same I/O failure at the
            // move boundary there, so recovery is still tested rather than skipped.
            if (LinuxPlatform.IsLinux) ReplaceEngine.BeforeRemovedMove = failMove;
            try
            {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                message = f.Engine.Uninstall(b.Id, true, out folder);
                Require(message.Contains(ReplaceEngine.TrashFailedStart) && message.Contains("open in another program") && message.Contains("next start"), "the failed move was not reported: '" + message + "'");
                Require(f.Engine.LoadState().Find(b.Id) == null && Directory.Exists(storeB) && File.Exists(pending), "state / store / pending record after the failed move");
                Require(!f.Engine.ListRemoved().Any(r => r.Record.Mod.Id == b.Id), "an incomplete copy is listed");
                // Retried while the file is still open: still pending, and said so.
                string again = new ReplaceEngine(f.Game, f.Data, f.Engine.Settings).RetryTrash();
                Require(again != null && again.StartsWith(ReplaceEngine.TrashFailedStart) && File.Exists(pending), "retry while locked: " + again);
            }
            }
            finally { ReplaceEngine.BeforeRemovedMove = null; }
            // The next start (a new engine, as the window's LoadGameRows or the CLI): moved, listed, restorable.
            var next = new ReplaceEngine(f.Game, f.Data, f.Engine.Settings); next.FluffyProvider = f.Engine.FluffyProvider;
            string done = next.RetryTrash();
            Require(done == "Kept 1 removed replacement for Recovery." && !File.Exists(pending) && !Directory.Exists(storeB), "retry at the next start: " + done);
            Require(next.ListRemoved().Count(r => r.Record.Mod.Id == b.Id && String.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)) == 1, "the copy is not listed after the retry");
            ReplaceMod back; next.RestoreRemoved(folder, out back);
            Require(next.LoadState().Mods.Select(m => m.Id).SequenceEqual(new[] { b.Id, a.Id }), "restore after the retry: " + String.Join(",", next.LoadState().Mods.Select(m => m.Name)));
            // A pending move is also finished by the next change (before every change, any process).
            if (LinuxPlatform.IsLinux) ReplaceEngine.BeforeRemovedMove = failMove;
            try
            {
                using (new FileStream(Directory.GetFiles(storeB, "*", SearchOption.AllDirectories).First(), FileMode.Open, FileAccess.Read, FileShare.Read))
                    message = next.Uninstall(b.Id, true, out folder);
            }
            finally { ReplaceEngine.BeforeRemovedMove = null; }
            Require(message.Contains(ReplaceEngine.TrashFailedStart) && File.Exists(pending), "second failure: " + message);
            next.SetEnabled(a.Id, false);
            Require(!File.Exists(pending) && next.ListRemoved().Any(r => String.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)), "the next change did not finish the pending move");
            // The folder of an installed replacement is never moved by a stale pending record.
            string stale = Path.Combine(f.Data, "store", a.Id);
            var record = new ReplaceRemovedRecord { GameRoot = f.Game, RemovedUtc = DateTime.UtcNow.ToString("o"), Index = 0, Mod = next.LoadState().Find(a.Id) };
            DurableFile.WriteJson(pending, new TrashPending { Moves = new List<TrashMove> { new TrashMove { From = stale, To = Path.Combine(f.Data, "store", ReplaceEngine.RemovedFolderName, a.Id + "-stale"), RecordJson = Json.Serializer().Serialize(record) } } });
            Require(next.RetryTrash() == null && Directory.Exists(stale) && !File.Exists(pending), "a stale pending record moved an installed replacement's store folder");
        }

        // 3a. Pruning rules (no disk).
        internal static void PruneRules(string root)
        {
            DateTime now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            Func<string, string, string, double, long, RemovedReplacement> copy = delegate(string folder, string source, string id, double ageDays, long bytes)
            {
                return new RemovedReplacement { Folder = Path.Combine(root, "store", ReplaceEngine.RemovedFolderName, folder), Bytes = bytes, Record = new ReplaceRemovedRecord { RemovedUtc = now.AddDays(-ageDays).ToString("o"), Mod = new ReplaceMod { Id = id, Name = folder, SourcePath = source } } };
            };
            const long MB = 1024L * 1024, GB = 1024L * MB;
            var copies = new List<RemovedReplacement>
            {
                copy("x-40d", @"C:\Mods\X.zip", "x1", 40, MB), copy("x-35d", @"c:\mods\x.ZIP", "x2", 35, MB), copy("x-2d", @"C:\Mods\X.zip", "x3", 2, MB),   // same mod (path case)
                copy("y-100d", @"C:\Mods\Y.zip", "y1", 100, MB),                                                                                      // the only copy: newest
                copy("z-60d", @"C:\Mods\Z.zip", "z1", 60, MB), copy("z-10d", @"C:\Mods\Z.zip", "z2", 10, MB),                                         // z-60d: an Undo needs it
                copy("n-90d", "", "n1", 90, MB), copy("n-80d", "", "n1", 80, MB), copy("m-70d", "", "m1", 70, MB),                                  // no source: by record id
            };
            List<string> deleted = ReplaceEngine.PruneCandidates(copies, new[] { copies.First(c => c.Record.Mod.Name == "z-60d").Folder }, now).Select(c => c.Record.Mod.Name).OrderBy(n => n).ToList();
            Require(String.Join(",", deleted) == "n-90d,x-35d,x-40d", "age rule: " + String.Join(",", deleted));
            // Size: above 5 GB the oldest eligible copies go first, never one younger than a day or a mod's newest.
            var big = new List<RemovedReplacement>
            {
                copy("w-20d", @"C:\Mods\W.zip", "w1", 20, 3 * GB), copy("w-15d", @"C:\Mods\W.zip", "w2", 15, 3 * GB), copy("w-5h", @"C:\Mods\W.zip", "w3", 5 / 24.0, 3 * GB),
                copy("w-1h", @"C:\Mods\W.zip", "w4", 1 / 24.0, GB), copy("v-25d", @"C:\Mods\V.zip", "v1", 25, 2 * GB),
            };
            deleted = ReplaceEngine.PruneCandidates(big, null, now).Select(c => c.Record.Mod.Name).OrderBy(n => n).ToList();
            Require(String.Join(",", deleted) == "w-15d,w-20d", "size rule: " + String.Join(",", deleted));
            big.RemoveAll(c => c.Record.Mod.Name == "w-15d");   // 9 GB: w-20d alone brings it to 6 GB; the rest is protected
            deleted = ReplaceEngine.PruneCandidates(big, null, now).Select(c => c.Record.Mod.Name).ToList();
            Require(String.Join(",", deleted) == "w-20d", "size rule, protected rest: " + String.Join(",", deleted));
            Require(ReplaceEngine.PruneCandidates(new List<RemovedReplacement>(), null, now).Count == 0, "empty");
            // A record without a removal time falls back to the time in its folder name.
            var named = copy("q1-20250101000000-abcdef", @"C:\Mods\Q.zip", "q1", 0, MB); named.Record.RemovedUtc = "";
            Require(ReplaceEngine.RemovedTime(named) == new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), "folder-name time: " + ReplaceEngine.RemovedTime(named).ToString("o"));
        }

        // 3b. Pruning on disk: only the rule's copies are deleted; an Undo's copy and each mod's newest stay.
        internal static void PruneOnDisk(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            var folders = new List<string>();
            for (int i = 0; i < 3; i++) { ReplaceMod m = ReplaceChecks.Install(f, "modA", "Cammy Colors"); string folder; f.Engine.Uninstall(m.Id, true, out folder); folders.Add(folder); }
            ReplaceMod bmod = ReplaceChecks.Install(f, "modB", "Cammy Alt"); string bfolder; f.Engine.Uninstall(bmod.Id, true, out bfolder);
            Action<string, double> age = delegate(string folder, double days)
            {
                string file = Path.Combine(folder, "record.json");
                ReplaceRemovedRecord r = Json.Read<ReplaceRemovedRecord>(file); r.RemovedUtc = DateTime.UtcNow.AddDays(-days).ToString("o"); DurableFile.WriteJson(file, r);
            };
            age(folders[0], 40); age(folders[1], 35); age(bfolder, 100);
            ReplaceEngine.PruneResult result = f.Engine.PruneRemoved(new[] { folders[1] });
            Require(result.Deleted == 1 && !Directory.Exists(folders[0]) && Directory.Exists(folders[1]) && Directory.Exists(folders[2]) && Directory.Exists(bfolder), "first prune: " + result.Deleted + " " + String.Join(", ", result.Folders));
            string log = File.ReadAllText(Path.Combine(f.Data, "log.txt"));
            Require(log.Contains("Recovery: deleted 1 removed replacement copy") && log.Contains("each mod's newest copy kept"), "log line missing");
            result = f.Engine.PruneRemoved(null);
            Require(result.Deleted == 1 && !Directory.Exists(folders[1]) && f.Engine.ListRemoved().Count == 2, "second prune (no Undo): " + result.Deleted);
            // A change running elsewhere (the game lock held): nothing is pruned now.
            age(folders[2], 50); ReplaceMod again = ReplaceChecks.Install(f, "modA", "Cammy Colors"); string newer; f.Engine.Uninstall(again.Id, true, out newer);
            var thread = new Thread(delegate() { using (f.Engine.Lock()) Thread.Sleep(1500); });
            thread.Start(); Thread.Sleep(200);
            Require(f.Engine.PruneRemoved(null).Deleted == 0 && Directory.Exists(folders[2]), "pruned while another task held the game lock");
            thread.Join();
            Require(f.Engine.PruneRemoved(null).Deleted == 1 && !Directory.Exists(folders[2]) && Directory.Exists(newer), "prune after the lock");
        }

        // 3c. Earlier store roots stay listed (Recovery, Undo, Last used).
        internal static void OldStoreRoots(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string store1 = Path.Combine(f.Data, "store"), store2 = Path.Combine(f.Data, "store2"), store3 = Path.Combine(root, "store3");
            Directory.CreateDirectory(store2); Directory.CreateDirectory(store3);
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Cammy Colors"); string fa; f.Engine.Uninstall(a.Id, true, out fa);
            // Settings moves the store while nothing is installed (MainForm.SaveReplacePreferences allows only that).
            Func<string, ReplaceEngine> engineFor = delegate(string store) { var e = new ReplaceEngine(f.Game, f.Data, new ReplaceSettings { Method = ReplaceMethods.Pak, StoreLocation = store }); e.FluffyProvider = f.Engine.FluffyProvider; return e; };
            f.Engine = engineFor(store2);
            Require(f.Engine.ListRemoved().Any(r => String.Equals(r.Folder, fa, StringComparison.OrdinalIgnoreCase)), "the old store's copy is not listed right after the move");
            ReplaceMod b = ReplaceChecks.Install(f, "modB", "Cammy Alt");
            ReplaceState s = f.Engine.LoadState();
            Require(String.Equals(s.StoreRoot, store2, StringComparison.OrdinalIgnoreCase) && s.PreviousStoreRoots.Count == 1 && String.Equals(s.PreviousStoreRoots[0], store1, StringComparison.OrdinalIgnoreCase), "PreviousStoreRoots: " + String.Join(", ", s.PreviousStoreRoots));
            string fb; f.Engine.Uninstall(b.Id, true, out fb);
            Require(f.Engine.ListRemoved().Count == 2 && fb.StartsWith(store2, StringComparison.OrdinalIgnoreCase), "both stores' copies listed");
            List<string> known = ReplaceEngine.KnownStoreRoots(f.Data, f.Engine.Settings);
            Require(known.Contains(store1, StringComparer.OrdinalIgnoreCase) && known.Contains(store2, StringComparer.OrdinalIgnoreCase), "KnownStoreRoots: " + String.Join(", ", known));
            Require(LibraryUsage.ReadRemoved(null, known).Any(u => String.Equals(u.SourcePath, f.Mod("modA"), StringComparison.OrdinalIgnoreCase)), "Last used does not see the old store's copy");
            // Restoring a copy from the old store puts it into the current store.
            ReplaceMod restored; f.Engine.RestoreRemoved(fa, out restored);
            Require(Directory.Exists(Path.Combine(store2, a.Id)) && !Directory.Exists(fa) && f.Engine.LoadState().Find(a.Id) != null, "restore from the old store");
            f.Engine.Uninstall(a.Id, true, out fa);
            // A third move keeps both earlier roots.
            f.Engine = engineFor(store3);
            ReplaceMod c = ReplaceChecks.Install(f, "modC", "Costume with helper");
            s = f.Engine.LoadState();
            Require(s.PreviousStoreRoots.Count == 2 && s.PreviousStoreRoots.Contains(store1, StringComparer.OrdinalIgnoreCase) && s.PreviousStoreRoots.Contains(store2, StringComparer.OrdinalIgnoreCase), "third root: " + String.Join(", ", s.PreviousStoreRoots));
            Require(f.Engine.ListRemoved().Count == 2, "copies after the third move: " + f.Engine.ListRemoved().Count);
            f.Engine.Uninstall(c.Id);
            Require(f.Engine.ListRemoved().Count == 3, "three copies across three stores");
        }

        // 4. The check cache fills per file: a cancelled 7-Zip batch keeps what it extracted; folder sources are not recopied.
        internal static void IncrementalCache(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            // Folder source: the second check copies nothing; an edited file (same size, new time) is copied alone.
            ModPackage package = PackageReader.Load(f.Mod("modA"), "");
            CompositionPlan composition = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
            using (var cache = new PrepareCache())
            {
                ReplacePlan first = ReplacePlanner.Check(composition, "", f.Engine, cache, null);
                int copied = cache.Extracted;
                ReplacePlan second = ReplacePlanner.Check(composition, "", f.Engine, cache, null);
                Require(copied == first.Files.Count && cache.Extracted == copied && cache.Hits == second.Files.Count, "folder recopied: extracted " + cache.Extracted + " of " + first.Files.Count + ", hits " + cache.Hits);
                ReplacePlan plain = ReplacePlanner.Check(composition, "", f.Engine);
                Func<ReplacePlan, string> sig = delegate(ReplacePlan p) { return String.Join("|", p.Files.Select(x => x.Path + ":" + x.Sha256 + ":" + x.Size)) + "#" + p.Fixed; };
                Require(sig(second) == sig(plain), "cached folder result differs from a plain check");
                string edited = Path.Combine(f.Mod("modA"), "natives", "stm", "product", "model", "esf", "esf009", "002", "01", "custom_a.tex.241101895");
                File.WriteAllBytes(edited, Encoding.UTF8.GetBytes("custom texture B")); File.SetLastWriteTimeUtc(edited, DateTime.UtcNow.AddMinutes(2));
                ReplacePlan third = ReplacePlanner.Check(composition, "", f.Engine, cache, null);
                Require(cache.Extracted == copied + 1 && third.Files.Any(x => x.Sha256 == ReplaceEngine.Sha(Encoding.UTF8.GetBytes("custom texture B"))), "edited folder file: extracted " + cache.Extracted);
            }
            // RAR/7Z through a fake 7-Zip: the first batch stops after two files (one more half-written); the re-check
            // asks 7-Zip only for the rest. Kept files need their listed CRC; a complete file whose CRC differs and one
            // without a listed CRC are extracted again.
            string archive = Path.Combine(root, "big.rar"); File.WriteAllBytes(archive, new byte[] { 1, 2, 3 });
            var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 6; i++) { var b = new byte[4096 + i]; new Random(i).NextBytes(b); data["natives/stm/fixture/file" + i + ".bin"] = b; }
            var entries = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in data) { var crc = new ZipCrc32(); crc.Update(kv.Value, kv.Value.Length); entries[kv.Key] = new SourceEntry { Path = kv.Key, Original = kv.Key.Replace('/', '\\'), Size = kv.Value.Length, ListedCrc = crc.Value }; }
            entries["natives/stm/fixture/file2.bin"].ListedCrc = null;                 // no CRC in the listing
            entries["natives/stm/fixture/file3.bin"].ListedCrc ^= 0x5A5A5A5Au;        // a CRC that does not match
            var component = new ModComponent { Id = "main", Name = "Main" };
            var chosen = data.Keys.ToDictionary(k => k, k => new PlannedFile { Component = component, Asset = new NativeAsset { EntryPath = k, DestinationPath = k, Size = data[k].Length } }, StringComparer.OrdinalIgnoreCase);
            var requests = new List<List<string>>();
            int completeFirst = 4;   // file0..file3 written fully, file4 half, then cancelled
            var cancel = new CancellationTokenSource();
            SourceReader.SevenZipRun = delegate(string exe, string args)
            {
                string raw = Between(args, "\"-o", "\""), list = Between(args, "\"-i@", "\"");
                List<string> wanted = File.ReadAllLines(list).Select(l => l.Replace('\\', '/')).ToList();
                requests.Add(wanted);
                int n = 0;
                foreach (string entry in wanted)
                {
                    string target = Path.Combine(raw, entry.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(target));
                    byte[] bytes = data[entry];
                    if (requests.Count == 1 && n == completeFirst) { File.WriteAllBytes(target, bytes.Take(bytes.Length / 2).ToArray()); cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
                    File.WriteAllBytes(target, bytes); n++;
                }
                return "";
            };
            try
            {
                GameReference reference = GameReference.Tables(f.Game);
                using (var cache = new PrepareCache())
                {
                    bool cancelled = false;
                    using (OperationContext.Begin(cancel.Token, null))
                    using (var reader = new SourceReader(archive, archive, entries, SourceReader.Stamp(archive)))
                    {
                        try { cache.Stage(chosen, reader, reference, "k"); }
                        catch (OperationCanceledException) { cancelled = true; }
                    }
                    Require(cancelled && requests.Count == 1 && requests[0].Count == 6, "the first batch was not cancelled as staged");
                    Require(cache.Extracted == 2, "kept after the cancel: " + cache.Extracted + " (expected file0 and file1; file2 has no CRC, file3 a wrong one, file4 is half)");
                    Dictionary<string, PreparedFile> result;
                    using (OperationContext.Begin(CancellationToken.None, null))
                    using (var reader = new SourceReader(archive, archive, entries, SourceReader.Stamp(archive)))
                        result = cache.Stage(chosen, reader, reference, "k");
                    List<string> rest = requests[1].OrderBy(x => x).ToList();
                    Require(requests.Count == 2 && String.Join(",", rest.Select(Path.GetFileName)) == "file2.bin,file3.bin,file4.bin,file5.bin", "the re-check asked 7-Zip for: " + String.Join(",", rest.Select(Path.GetFileName)));
                    Require(cache.Hits == 2 && cache.Extracted == 6 && result.Count == 6 && result.All(r => r.Value.Sha256 == ReplaceEngine.Sha(data[r.Key])), "re-check result: hits " + cache.Hits + ", extracted " + cache.Extracted);
                    // Install never keeps partial files (CopyFiles without keepPartial).
                }
            }
            finally { SourceReader.SevenZipRun = null; }
        }
        private static string Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal) + start.Length; int b = text.IndexOf(end, a, StringComparison.Ordinal);
            return text.Substring(a, b - a);
        }

        // 6. Undo / Recovery put a mod back below replacements installed after it was removed.
        internal static void RestorePosition(string root)
        {
            // Pure rule.
            DateTime t = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
            Func<string, int, ReplaceMod> mod = delegate(string id, int minutes) { return new ReplaceMod { Id = id, InstalledUtc = t.AddMinutes(minutes).ToString("o") }; };
            Func<int, string, ReplaceRemovedRecord> rec = delegate(int index, string removed) { return new ReplaceRemovedRecord { Index = index, RemovedUtc = removed }; };
            string at = t.ToString("o");
            Require(ReplaceEngine.RestoreIndex(new List<ReplaceMod> { mod("A", -60) }, rec(0, at)) == 0, "no newer: old index 0");
            Require(ReplaceEngine.RestoreIndex(new List<ReplaceMod> { mod("N", 5), mod("A", -60) }, rec(0, at)) == 1, "a newer install stays above");
            Require(ReplaceEngine.RestoreIndex(new List<ReplaceMod> { mod("N", 5), mod("A", -60) }, rec(1, at)) == 2, "index 1 below A");
            Require(ReplaceEngine.RestoreIndex(new List<ReplaceMod> { mod("A", -60), mod("N", 5), mod("C", -50) }, rec(1, at)) == 2, "below a newer mod moved down");
            Require(ReplaceEngine.RestoreIndex(new List<ReplaceMod> { mod("A", -60), mod("C", -50), mod("N", 5) }, rec(0, at)) == 3, "a newer mod at the bottom keeps it below");
            Require(ReplaceEngine.RestoreIndex(new List<ReplaceMod> { mod("N", 5), mod("A", -60) }, rec(0, "")) == 0, "a record without a time keeps its old index");
            Require(ReplaceEngine.RestoreIndex(new List<ReplaceMod>(), rec(4, at)) == 0, "clamped");
            // Engine: B shares the mesh with the newer W; W must keep it after B's Undo.
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Cammy Colors");
            ReplaceMod b = ReplaceChecks.Install(f, "modB", "Cammy Alt");                 // [B, A]
            string folder; f.Engine.Uninstall(b.Id, true, out folder);                    // [A]
            Thread.Sleep(30);
            ReplaceMod w = ReplaceChecks.Install(f, "modWeird", "Weird folder");          // [W, A], W replaces the mesh too
            ReplaceMod restored; f.Engine.RestoreRemoved(folder, out restored);
            ReplaceState s = f.Engine.LoadState();
            Require(s.Mods.Select(m => m.Id).SequenceEqual(new[] { w.Id, b.Id, a.Id }), "order after Undo: " + String.Join(", ", s.Mods.Select(m => m.Name)));
            Require(s.Deployment(ReplaceChecks.V5).Owner == w.Id && s.Deployment(ReplaceChecks.V1).Owner == b.Id, "owners after Undo: mesh " + s.Deployment(ReplaceChecks.V5).Owner + ", colours " + s.Deployment(ReplaceChecks.V1).Owner);
            // Index 1: [A, B] → uninstall B → [W2... ] newer on top → B goes back under A.
            f.Engine.Uninstall(w.Id); f.Engine.Move(b.Id, 1);                             // [A, B]
            f.Engine.Uninstall(b.Id, true, out folder);                                   // [A]
            Thread.Sleep(30);
            w = ReplaceChecks.Install(f, "modWeird", "Weird folder");                     // [W, A]
            f.Engine.RestoreRemoved(folder, out restored);
            Require(f.Engine.LoadState().Mods.Select(m => m.Id).SequenceEqual(new[] { w.Id, a.Id, b.Id }), "index-1 Undo: " + String.Join(", ", f.Engine.LoadState().Mods.Select(m => m.Name)));
        }
    }
}
