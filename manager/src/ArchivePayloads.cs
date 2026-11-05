using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    internal sealed partial class ReplaceEngine
    {
        internal static bool IsLibraryArchive(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return (ext == ".zip" || ext == ".rar" || ext == ".7z") && File.Exists(path);
        }

        private static bool Verified(string path, ModFile f)
        {
            return Present(path, f) && Sha(path) == f.Sha256;
        }
        // Verified without the hash (1.0.16: for a file whose hash this change already read).
        private static bool Present(string path, ModFile f)
        {
            Safety.NoReparseChain(path);
            return File.Exists(path) && new FileInfo(path).Length == f.Size;
        }

        internal bool PayloadMatches(ReplaceState s, ReplaceMod m, ModFile f)
        {
            return Verified(StoreFile(s, m, f), f) || (f.ArchiveEntry != null && Verified(GamePath(f.Path), f));
        }

        internal string ReadPayload(ReplaceState s, ReplaceMod m, ModFile f)
        {
            string stored = StoreFile(s, m, f);
            if (File.Exists(stored) || f.ArchiveEntry == null) return stored;
            string live = GamePath(f.Path);
            if (Verified(live, f)) return live;
            Hydrate(s, m, new List<ModFile> { f });
            return stored;
        }

        // Materialize only files that are about to lose their live bytes, or need repair. Publication
        // uses a new temporary file and checks the installed SHA before anything can use its contents.
        private void Hydrate(ReplaceState state, ReplaceMod mod, List<ModFile> files) { Hydrate(state, mod, files, null); }
        // observed (1.0.16, speed): the SHA-256 this change's Reconcile just read for each game path, under the same
        // lock. A live file it found intact is copied without hashing it again first (uninstalling a large archive mod
        // read every file once less); the copy is still verified, and a copy that differs gets the full check of the
        // live file, so it is extracted from the archive or refused exactly as before.
        private void Hydrate(ReplaceState state, ReplaceMod mod, List<ModFile> files, Dictionary<string,string> observed)
        {
            if (files.Count == 0) return;
            string root = StoreRoot(state);
            Directory.CreateDirectory(root);
            string scratch = Safety.NewFolder(root, ".recover-");
            try
            {
                var extract = new List<ModFile>();
                foreach (ModFile f in files)
                {
                    OperationContext.ThrowIfCancellationRequested();
                    string live = GamePath(f.Path);
                    string seen;
                    bool known = observed != null && observed.TryGetValue(f.Path, out seen) && seen != null && seen == f.Sha256 && Present(live, f);
                    if (!known && !Verified(live, f)) { extract.Add(f); continue; }
                    string temp = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
                    File.Copy(live, temp);
                    if (known && !Verified(temp, f))
                    {
                        File.Delete(temp);
                        if (!Verified(live, f)) { extract.Add(f); continue; }
                        throw new IOException("The recovered bytes differ from the installed version of " + f.Path + ".");
                    }
                    PublishPayload(state, mod, f, temp, known);
                }
                if (extract.Count == 0) return;
                string archive = ResolveArchive(mod);
                if (archive == null) throw new IOException("Restore the original archive for '" + mod.Name + "' in a configured library. Its installed files could not be recovered.");
                using (var reader = new SourceReader(archive, SourceConfig == null ? "" : SourceConfig.SevenZipPath))
                {
                    // Two destinations may refer to the same original archive member.
                    var entries = extract.GroupBy(f => f.ArchiveEntry, StringComparer.OrdinalIgnoreCase).ToList();
                    var planned = new List<PlannedFile>();
                    foreach (var group in entries)
                    {
                        SourceEntry entry;
                        if (!reader.Entries.TryGetValue(group.Key, out entry) || group.Any(f => f.Size != entry.Size))
                            throw new IOException("Archive entry changed: " + group.Key);
                        planned.Add(new PlannedFile { Asset = new NativeAsset { EntryPath = entry.Path, DestinationPath = "payload-" + planned.Count, Size = entry.Size } });
                    }
                    reader.CopyFiles(planned, scratch, scratch);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        string extracted = Safety.Under(scratch, planned[i].Asset.DestinationPath);
                        var destinations = entries[i].ToList();
                        for (int j = 0; j < destinations.Count; j++)
                        {
                            string temp = extracted;
                            if (j + 1 < destinations.Count)
                            {
                                temp = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
                                File.Copy(extracted, temp);
                            }
                            PublishPayload(state, mod, destinations[j], temp);
                        }
                    }
                }
            }
            finally { Safety.DeleteGenerated(scratch, root); }
        }

        private void PublishPayload(ReplaceState state, ReplaceMod mod, ModFile f, string temp, bool verified = false)
        {
            if (!verified && !Verified(temp, f)) throw new IOException("The recovered bytes differ from the installed version of " + f.Path + ".");
            string target = StoreFile(state, mod, f);
            Safety.NoReparseChain(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            DurableFile.Publish(temp, target);
        }

        private string ResolveArchive(ReplaceMod m)
        {
            if (IsLibraryArchive(m.SourcePath) && MatchesArchive(m.SourcePath,m)) return m.SourcePath;
            if (SourceConfig == null) return null;
            return MovedArchive(m, Library.ListEntries(SourceConfig.Libraries), false);
        }

        // Library archives that can be this record's archive: the same size, or a ZIP up to a ZIP comment's 64 KB
        // larger (an Edit bundle layout).
        private static IEnumerable<LibraryEntry> Candidates(ReplaceMod m, IEnumerable<LibraryEntry> library)
        {
            return library.Where(e => (e.Size == m.ArchiveSize || (BundleLayout.IsZip(e.SourcePath) && e.Size > m.ArchiveSize && e.Size - m.ArchiveSize <= UInt16.MaxValue)) && IsLibraryArchive(e.SourcePath));
        }

        // unique: relinking a record needs the one archive. Recovery reads any byte-identical copy (the first found),
        // so a duplicate download never makes the files unrecoverable.
        internal static string MovedArchive(ReplaceMod m, IEnumerable<LibraryEntry> library, bool unique = true)
        {
            string match = null;
            foreach (LibraryEntry entry in Candidates(m, library))
            {
                OperationContext.ThrowIfCancellationRequested();
                Safety.NoReparseChain(entry.SourcePath);
                if (!MatchesArchive(entry.SourcePath,m)) continue;
                if (!unique) return entry.SourcePath;
                if (match != null) return null;
                match = entry.SourcePath;
            }
            return match;
        }
        private static bool MatchesArchive(string path,ReplaceMod mod)
        {
            long size;
            try { return BundleLayout.ArchiveHash(path,out size,mod.ArchiveSize)==mod.ArchiveSha; }
            catch (InvalidDataException) { return false; }
            catch (IOException) { return false; }
        }

        internal string FindPointerArchive(PresetReplacementPointer pointer)
        {
            if (pointer.ArchiveSha == null) return pointer.SourcePath;
            var identity = new ReplaceMod { SourcePath = pointer.SourcePath, ArchiveSize = pointer.ArchiveSize, ArchiveSha = pointer.ArchiveSha };
            return ResolveArchive(identity);
        }

        internal void KeepArchivePayloads(IEnumerable<string> paths)
        {
            var sources = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            using (Lock())
            {
                if (JournalPending) throw new IOException("Finish the interrupted replacement change first.");
                ReplaceState state = LoadState();
                bool changed = false;
                foreach (ReplaceMod m in state.Mods.Where(m => sources.Contains(m.SourcePath) && m.ArchiveSha != null))
                {
                    var archiveFiles = m.Files.Where(f => f.ArchiveEntry != null).ToList();
                    Hydrate(state, m, archiveFiles.Where(f => !Verified(StoreFile(state, m, f), f)).ToList());
                    foreach (var f in archiveFiles) f.ArchiveEntry = null;
                    m.ArchiveSha = m.ArchiveStamp = null;
                    changed = true;
                }
                if (changed) { state.Generation++; DurableFile.WriteJson(StatePath, state); }
            }
        }

        // Records whose archive was not found, with the candidates' paths and stamps at that time: a source that
        // stays missing is not hashed again at every refresh, only after a candidate changes.
        private static readonly Dictionary<string, string> relinkMisses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string StampOrNone(string path)
        {
            try { return SourceReader.Stamp(path); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        internal void RelinkArchives()
        {
            if (SourceConfig == null || !File.Exists(StatePath)) return;
            using (Lock())
            {
                if (JournalPending) return;
                ReplaceState state = LoadState();
                List<LibraryEntry> entries = null;
                var moves = new List<KeyValuePair<string,string>>();
                bool updated = false;
                try
                {
                foreach (ReplaceMod m in state.Mods.Where(m => m.ArchiveSha != null))
                {
                    string current = IsLibraryArchive(m.SourcePath) ? StampOrNone(m.SourcePath) : null;
                    if (current != null && current == m.ArchiveStamp) continue;
                    if (entries == null) entries = Library.ListEntries(SourceConfig.Libraries);
                    string signature = m.ArchiveSha + "|" + current + "|" + String.Join("|", Candidates(m, entries).Select(e => e.SourcePath + ">" + StampOrNone(e.SourcePath)));
                    string missed;
                    lock (relinkMisses) if (relinkMisses.TryGetValue(m.Id, out missed) && missed == signature) continue;
                    // Rewritten in place (an Edit bundle save keeps the files): only the stamp is new.
                    string path = current != null && MatchesArchive(m.SourcePath, m) ? m.SourcePath : MovedArchive(m, entries);
                    lock (relinkMisses) { if (path == null) relinkMisses[m.Id] = signature; else relinkMisses.Remove(m.Id); }
                    if (path == null)
                    {
                        // 1.0.18: the archive is gone and no library copy holds it. Its files that are still intact in
                        // the game become the stored copy (as Disable does), so Enable, Reapply and rebuilds keep working.
                        // Not while identical copies exist (only the relink needs one; repairs read any of them), and
                        // only when the archive is provably gone or replaced, never because a drive or library is
                        // offline or the file can't be read right now (review P1-1).
                        if (MovedArchive(m, entries, false) == null && SourceGoneForGood(m))
                        {
                            bool retry;
                            if (RescueFromGame(state, m, out retry)) updated = true;
                            // A game file it couldn't read (SF6 running) is tried again at the next refresh.
                            if (retry) lock (relinkMisses) relinkMisses.Remove(m.Id);
                        }
                        continue;
                    }
                    if (!ReplacePlanner.SameSource(path, m.SourcePath)) moves.Add(new KeyValuePair<string,string>(m.SourcePath, path));
                    m.SourcePath = path; m.ArchiveStamp = SourceReader.Stamp(path); updated = true;
                }
                }
                catch
                {
                    // 1.0.18 (review P2-2): a cancel or error later in the loop keeps what was already rescued.
                    if (updated) { state.Generation++; DurableFile.WriteJson(StatePath, state); }
                    throw;
                }
                if (!updated) return;
                state.Generation++;
                DurableFile.WriteJson(StatePath, state);
                foreach (var move in moves.Distinct())
                    if (!ReplacePlanner.SameSource(move.Key, move.Value) && !File.Exists(move.Key))
                        OutfitSetStore.RenameSource(SourceConfig.CostumeRoot, move.Key, move.Value);
            }
        }

        // 1.0.18 (review P1-1): the recorded archive is really gone, not just out of reach. A file still at the path
        // must be read and hold other contents (a newer download under the same name); an unreadable one is not
        // proof. A missing file counts only while its folder and every configured library are there, so an unplugged
        // drive, a sleeping network share or a library folder renamed for a moment never triggers a rescue.
        private bool SourceGoneForGood(ReplaceMod m)
        {
            string path = m.SourcePath;
            if (String.IsNullOrEmpty(path) || SourceConfig == null) return false;
            try
            {
                if (SourceConfig.Libraries.Any(l => !String.IsNullOrEmpty(l) && !Directory.Exists(l))) return false;
                if (File.Exists(path))
                {
                    long size; string sha;
                    try { sha = BundleLayout.ArchiveHash(path, out size, m.ArchiveSize); }
                    catch (InvalidDataException) { return true; }   // not an archive any more
                    return sha != m.ArchiveSha;
                }
                string folder = Path.GetDirectoryName(path);
                return !String.IsNullOrEmpty(folder) && Directory.Exists(folder);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; return false; }
        }

        // 1.0.18: copies a mod's archive-backed files whose installed bytes still match the record into the store and
        // stops reading them from the archive. A file another mod covers, or one changed in the game, is left alone
        // (the missing-copy notice names the mod). The record keeps its archive identity (review P1-1, P2-3), so the
        // archive found again later is relinked, and presets still tell this version from another one.
        // retry: an installed file could not be read (SF6 running). A failed copy (disk full) is not retried until the
        // next start or a library change, so a large copy that can't fit isn't repeated at every refresh.
        // Records only what is verified in the store, so a copy that fails part-way keeps the files it finished; a
        // store copy that is already there and verifies is adopted (review P3-1).
        // Two destinations of one archive member are each checked against their own installed file.
        internal bool RescueFromGame(ReplaceState state, ReplaceMod m, out bool retry)
        {
            retry = false;
            var rescue = new List<ModFile>();
            var kept = new List<ModFile>();
            foreach (ModFile f in m.Files.Where(f => f.ArchiveEntry != null))
            {
                try
                {
                    string stored = StoreFile(state, m, f);
                    if (File.Exists(stored) && Verified(stored, f)) kept.Add(f);
                    else if (Verified(GamePath(f.Path), f)) rescue.Add(f);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; retry = true; }
            }
            if (rescue.Count > 0)
            {
                try { Hydrate(state, m, rescue); }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException || ex is OperationCanceledException) throw;
                    Program.LogLine("Keeping the installed files of '" + m.Name + "' (source gone) stopped: " + ex.Message);
                }
                foreach (ModFile f in rescue)
                {
                    try { if (Verified(StoreFile(state, m, f), f)) kept.Add(f); }
                    catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; }
                }
            }
            if (kept.Count == 0) return false;
            foreach (ModFile f in kept) f.ArchiveEntry = null;
            int still = m.Files.Count(f => f.ArchiveEntry != null);
            Program.LogLine("Source of '" + m.Name + "' is gone; kept " + ReplacePlanner.N(kept.Count, "installed file") + " as its stored copy" +
                (still == 0 ? "." : ", " + still + " still need the archive."));
            return true;
        }

        internal bool CanReclaimArchiveStore(ReplaceState state)
        {
            if (state == null || state.Mods.Count == 0) return false;
            if (state.ArchiveStoreReclaimed.HasValue) return !state.ArchiveStoreReclaimed.Value;
            var older = state.Mods.Where(m => m.Enabled && m.ArchiveSha == null && IsLibraryArchive(m.SourcePath)).ToList();
            if (older.Count == 0) return false;
            var owners = state.Deployed.ToDictionary(d => d.Path, d => d.Owner, StringComparer.OrdinalIgnoreCase);
            return older.Any(m => m.Files.Any(f => {
                string owner;
                return String.IsNullOrEmpty(f.Fixes) && owners.TryGetValue(f.Path, out owner) && owner == m.Id && File.Exists(StoreFile(state, m, f));
            }));
        }

        // At most what Reclaim space frees: the stored copies CompactArchiveStore checks (files it can't verify stay).
        internal long ReclaimableBytes(ReplaceState state)
        {
            if (state == null) return 0;
            var owners = state.Deployed.ToDictionary(d => d.Path, d => d.Owner, StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (ReplaceMod m in state.Mods.Where(m => IsLibraryArchive(m.SourcePath)))
                foreach (ModFile f in m.Files)
                {
                    string owner;
                    if (!String.IsNullOrEmpty(f.Fixes) || !owners.TryGetValue(f.Path, out owner) || owner != m.Id) continue;
                    try { if (File.Exists(StoreFile(state, m, f))) total += f.Size; }
                    catch (IOException) { }
                }
            return total;
        }

        // Older records have only deployment paths and hashes. Bind an entry only after verifying the
        // actual member contents. Commit its recovery recipe before deleting any redundant store files.
        internal string CompactArchiveStore()
        {
            long released = 0;
            var skipped = new List<string>();
            using (Lock())
            {
                if (JournalPending) throw new IOException("Finish the interrupted replacement change first.");
                ReplaceState state = LoadState();
                if (!CanReclaimArchiveStore(state)) return "No replacement store cleanup is needed.";
                if (state.ArchiveStoreReclaimed == null)
                {
                    state.ArchiveStoreReclaimed = false;
                    state.Generation++;
                    DurableFile.WriteJson(StatePath, state);
                }
                var owners = state.Deployed.ToDictionary(d => d.Path, d => d.Owner, StringComparer.OrdinalIgnoreCase);
                foreach (ReplaceMod m in state.Mods)
                {
                    OperationContext.ThrowIfCancellationRequested();
                    if (!IsLibraryArchive(m.SourcePath)) continue;
                    var files = m.Files.Where(f => String.IsNullOrEmpty(f.Fixes) && owners.ContainsKey(f.Path) && owners[f.Path] == m.Id &&
                        File.Exists(StoreFile(state, m, f))).ToList();
                    if (files.Count == 0) continue;
                    OperationContext.Report("Verifying " + m.Name, state.Mods.IndexOf(m), state.Mods.Count);
                    try
                    {
                        Safety.NoReparseChain(m.SourcePath);
                        using (var archive = File.OpenRead(m.SourcePath))
                        using (var reader = new SourceReader(m.SourcePath, SourceConfig == null ? "" : SourceConfig.SevenZipPath))
                        {
                            long archiveSize;
                            string sha = BundleLayout.ArchiveHash(m.SourcePath, out archiveSize);
                            if (m.ArchiveSha != null && m.ArchiveSha != sha) { skipped.Add(m.Name); continue; }
                            var sizes = new HashSet<long>(files.Select(f => f.Size));
                            var members = reader.Entries.Values.Where(e => sizes.Contains(e.Size)).Select(e => new PlannedFile {
                                Asset = new NativeAsset { EntryPath = e.Path, DestinationPath = e.Path, Size = e.Size } }).ToList();
                            var byHash = reader.VerifyFiles(members).GroupBy(pair => pair.Value).ToDictionary(g => g.Key, g => g.First().Key);
                            var verified = new List<ModFile>();
                            foreach (ModFile f in files)
                            {
                                string entry;
                                if (byHash.TryGetValue(f.Sha256, out entry) && Verified(GamePath(f.Path), f) && Verified(StoreFile(state, m, f), f))
                                { f.ArchiveEntry = entry; verified.Add(f); }
                            }
                            if (verified.Count == 0) continue;
                            m.ArchiveSha = sha; m.ArchiveSize = archiveSize; m.ArchiveStamp = SourceReader.Stamp(m.SourcePath);
                            state.Generation++;
                            DurableFile.WriteJson(StatePath, state);
                            foreach (ModFile f in verified) { File.Delete(StoreFile(state, m, f)); released += f.Size; }
                        }
                    }
                    catch (IOException ex) { skipped.Add(m.Name + " (" + ex.Message + ")"); }
                    catch (UnauthorizedAccessException ex) { skipped.Add(m.Name + " (" + ex.Message + ")"); }
                }
                if (skipped.Count == 0)
                {
                    OperationContext.ThrowIfCancellationRequested();
                    state.ArchiveStoreReclaimed = true;
                    state.Generation++;
                    DurableFile.WriteJson(StatePath, state);
                }
            }
            return "Freed " + Utilities.FriendlyBytes(released) + " from the replacement store." +
                (skipped.Count == 0 ? "" : " Could not compact: " + String.Join(", ", skipped) + ".");
        }

        private static bool ArchiveAvailable(ReplaceMod m)
        {
            return m.ArchiveSha != null && IsLibraryArchive(m.SourcePath) && SourceReader.Stamp(m.SourcePath) == m.ArchiveStamp;
        }

        private void PrepareArchivePayloads(ReplaceState current, ReplaceState next, ReplaceJournal journal, Dictionary<string,string> trash, bool recycleSurvivingFiles, Dictionary<string,string> observed)
        {
            var winners = next.Deployed.ToDictionary(d => d.Path, d => d.Owner, StringComparer.OrdinalIgnoreCase);
            var needed = new HashSet<string>(journal.Ops.Where(o => o.Kind == "file").SelectMany(o => new[] { o.BeforeSource, o.AfterSource }), StringComparer.OrdinalIgnoreCase);
            foreach (ReplaceMod m in next.Mods.Concat(current.Mods.Where(old => next.Find(old.Id) == null)))
            {
                if (m.ArchiveSha == null) continue;
                bool remains = next.Find(m.Id) != null;
                bool canRelease = remains && ArchiveAvailable(m);
                var hydrate = new List<ModFile>();
                foreach (ModFile f in m.Files.Where(f => f.ArchiveEntry != null))
                {
                    string owner;
                    bool winner = remains && winners.TryGetValue(f.Path, out owner) && owner == m.Id;
                    string stored = StoreFile(current, m, f);
                    bool present = File.Exists(stored);
                    bool needsCopy = needed.Contains(stored) || (remains && !winner);
                    if (!present && !needsCopy && trash != null && trash.ContainsKey(m.Id))
                        needsCopy = !recycleSurvivingFiles || Verified(GamePath(f.Path), f);
                    if (!present && needsCopy) hydrate.Add(f);
                    if (canRelease && winner && (present || needsCopy)) journal.CleanupAfterCommit.Add(stored);
                }
                Hydrate(current, m, hydrate, observed);
            }
        }
    }
}
