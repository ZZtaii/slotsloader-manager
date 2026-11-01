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
            Safety.NoReparseChain(path);
            return File.Exists(path) && new FileInfo(path).Length == f.Size && Sha(path) == f.Sha256;
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
        private void Hydrate(ReplaceState state, ReplaceMod mod, List<ModFile> files)
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
                    if (!Verified(live, f)) { extract.Add(f); continue; }
                    string temp = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
                    File.Copy(live, temp);
                    PublishPayload(state, mod, f, temp);
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

        private void PublishPayload(ReplaceState state, ReplaceMod mod, ModFile f, string temp)
        {
            if (!Verified(temp, f)) throw new IOException("The recovered bytes differ from the installed version of " + f.Path + ".");
            string target = StoreFile(state, mod, f);
            Safety.NoReparseChain(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            DurableFile.Publish(temp, target);
        }

        private string ResolveArchive(ReplaceMod m)
        {
            if (IsLibraryArchive(m.SourcePath) && MatchesArchive(m.SourcePath,m)) return m.SourcePath;
            if (SourceConfig == null) return null;
            return MovedArchive(m, Library.ListEntries(SourceConfig.Libraries));
        }

        internal static string MovedArchive(ReplaceMod m, IEnumerable<LibraryEntry> library)
        {
            string match = null;
            foreach (LibraryEntry entry in library.Where(e => (e.Size == m.ArchiveSize || (BundleLayout.IsZip(e.SourcePath) && e.Size > m.ArchiveSize && e.Size - m.ArchiveSize <= UInt16.MaxValue)) && IsLibraryArchive(e.SourcePath)))
            {
                OperationContext.ThrowIfCancellationRequested();
                Safety.NoReparseChain(entry.SourcePath);
                if (!MatchesArchive(entry.SourcePath,m)) continue;
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

        internal void RelinkArchives()
        {
            if (SourceConfig == null || !File.Exists(StatePath)) return;
            using (Lock())
            {
                if (JournalPending) return;
                ReplaceState state = LoadState();
                List<LibraryEntry> entries = null;
                var moves = new List<KeyValuePair<string,string>>();
                foreach (ReplaceMod m in state.Mods.Where(m => m.ArchiveSha != null))
                {
                    if (IsLibraryArchive(m.SourcePath) && SourceReader.Stamp(m.SourcePath) == m.ArchiveStamp) continue;
                    if (entries == null) entries = Library.ListEntries(SourceConfig.Libraries);
                    string path = MovedArchive(m, entries);
                    if (path == null) continue;
                    moves.Add(new KeyValuePair<string,string>(m.SourcePath, path));
                    m.SourcePath = path; m.ArchiveStamp = SourceReader.Stamp(path);
                }
                if (moves.Count == 0) return;
                state.Generation++;
                DurableFile.WriteJson(StatePath, state);
                foreach (var move in moves.Distinct())
                    if (!ReplacePlanner.SameSource(move.Key, move.Value) && !File.Exists(move.Key))
                        OutfitSetStore.RenameSource(SourceConfig.CostumeRoot, move.Key, move.Value);
            }
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

        private void PrepareArchivePayloads(ReplaceState current, ReplaceState next, ReplaceJournal journal, Dictionary<string,string> trash, bool recycleSurvivingFiles)
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
                Hydrate(current, m, hydrate);
            }
        }
    }
}
