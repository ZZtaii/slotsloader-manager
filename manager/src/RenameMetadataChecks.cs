using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // Generated fixtures only; called by the library Delete/Rename checks. No installed game or app Data is used.
    internal static class RenameMetadataChecks
    {
        internal static void Run(string parent)
        {
            string root = Path.Combine(parent, "rename-metadata-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            string oldCache = Library.IdentityCacheDirectory, oldData = ArchiveInstalls.DataDirOverride, oldHistory = HistoryStore.DefaultDir;
            try
            {
                ArchiveInstalls.DataDirOverride = Path.Combine(root, "Data"); HistoryStore.DefaultDir = Path.Combine(root, "history");
                ArchiveCache(Path.Combine(root, "archive"));
                InvalidCache(Path.Combine(root, "schema"), "schema");
                InvalidCache(Path.Combine(root, "version"), "version");
                InvalidCache(Path.Combine(root, "source"), "source");
                InvalidCache(Path.Combine(root, "tool"), "tool");
                ChangedAfterCapture(Path.Combine(root, "changed-after-capture"));
                FolderCache(Path.Combine(root, "folder"));
                ManifestScopeAndRollback(Path.Combine(root, "manifests"));
                FilenameIdentity();
            }
            finally { Library.IdentityCacheDirectory = oldCache; ArchiveInstalls.DataDirOverride = oldData; HistoryStore.DefaultDir = oldHistory; }
        }

        private static void ArchiveCache(string root)
        {
            string library, source, tool; Setup(root, out library, out source, out tool);
            Library.Scan(new[] { library }, tool);
            LibraryIdentityRecord before = Cache().Records.Single();
            SourceRename.Outcome result = Rename(source, "Cammy Renamed", library, "", tool);
            Require(result.Done, "archive rename: " + result.Error);
            LibraryIdentityRecord after = Cache().Records.Single();
            Require(after.SourcePath == result.To && after.Stamp == before.Stamp && after.Authors.SequenceEqual(before.Authors)
                && after.CharacterCodes.SequenceEqual(before.CharacterCodes) && after.OptionIds.SequenceEqual(before.OptionIds) && after.CodesVersion == before.CodesVersion, "valid content metadata was not moved");
            int misses = Library.CacheMisses;
            LibraryEntry entry = Library.Scan(new[] { library }, tool).Single();
            Require(Library.CacheMisses == misses && entry.Name == "Cammy Renamed" && entry.FilenameCharacter == "Cammy" && entry.Authors.SequenceEqual(new[] { "Fixture author" }), "renamed archive was relisted or kept old filename metadata");
            result = Rename(result.To, "cammy renamed", library, "", tool);
            Require(result.Done && Cache().Records.Single().SourcePath == result.To, "case-only cache migration: " + result.Error);
            misses = Library.CacheMisses; Library.Scan(new[] { library }, tool);
            Require(Library.CacheMisses == misses, "case-only rename lost the cache");
        }

        private static void InvalidCache(string root, string kind)
        {
            string library, source, tool; Setup(root, out library, out source, out tool);
            Library.Scan(new[] { library }, tool);
            if (kind == "schema" || kind == "version")
            {
                LibraryIdentityCache cache = Cache();
                // 1.0.6: missing names (authors, display name, option names) are read in a later pass and keep the record;
                // a missing identity field still makes it invalid.
                if (kind == "schema") cache.Records.Single().MiscAreas = null; else cache.Records.Single().CodesVersion = 0;
                Json.Write(CachePath(), cache); Library.IdentityCacheDirectory = Library.IdentityCacheDirectory;
            }
            else if (kind == "source") File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(5));
            else File.AppendAllText(tool, "new tool version");
            SourceRename.Outcome result = Rename(source, "Renamed", library, "", tool);
            Require(result.Done && !Cache().Records.Any(r => String.Equals(r.SourcePath, result.To, StringComparison.OrdinalIgnoreCase)), kind + " reused an invalid cache record: " + result.Error);
            int misses = Library.CacheMisses; Library.Scan(new[] { library }, tool);
            Require(Library.CacheMisses == misses + 1, kind + " did not re-inspect the archive");
        }

        private static void ChangedAfterCapture(string root)
        {
            string library, source, tool; Setup(root, out library, out source, out tool);
            Library.Scan(new[] { library }, tool);
            LibraryIdentityRecord hint = Library.CaptureRenameIdentity(source, tool);
            Require(hint != null, "could not capture a valid record");
            string target = Path.Combine(library, "Changed.zip"); File.Move(source, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(target).AddSeconds(7));
            Library.CompleteSourceRename(source, target, hint, tool);
            Require(Cache().Records.Count == 0, "a source changed after capture retained its cache");
            Library.Scan(new[] { library }, tool);
            hint = Library.CaptureRenameIdentity(target, tool);
            string next = Path.Combine(library, "Changed Again.zip"); File.Move(target, next); File.AppendAllText(tool, "changed after capture");
            Library.CompleteSourceRename(target, next, hint, tool);
            Require(Cache().Records.Count == 0, "a tool changed after capture retained the archive cache");
        }

        private static void FolderCache(string root)
        {
            string library = Path.Combine(root, "library"), source = Path.Combine(library, "Folder mod");
            Directory.CreateDirectory(source); Library.IdentityCacheDirectory = Path.Combine(root, "cache");
            File.WriteAllText(Path.Combine(source, "readme.txt"), "folder fixture");
            Library.Scan(new[] { library }, "");
            SourceRename.Outcome result = Rename(source, "Renamed folder", library, "", "");
            Require(result.Done && Cache().Records.Count == 0, "folder rename unexpectedly cached content: " + result.Error);
            int misses = Library.CacheMisses; Library.Scan(new[] { library }, "");
            Require(Library.CacheMisses == misses + 1, "folder scan skipped its content inspection");
        }

        private static void ManifestScopeAndRollback(string root)
        {
            string library, source, tool; Setup(root, out library, out source, out tool);
            string costume = Path.Combine(root, "game", "reframework", "costume_mods"); Directory.CreateDirectory(costume);
            string enabled = Manifest(Path.Combine(costume, "Juri", "Enabled"), "Enabled", "Juri", source);
            string disabled = Manifest(Path.Combine(Path.GetDirectoryName(costume), ".slotsloader-installer-disabled", "disabled"), "Disabled", "Juri", source);
            string hidden = Manifest(Path.Combine(costume, "Juri", ".hidden"), "Hidden", "Juri", source);
            string hiddenAttribute = Manifest(Path.Combine(costume, "Juri", "Hidden attribute"), "Hidden attribute", "Juri", source);
            File.SetAttributes(Path.GetDirectoryName(hiddenAttribute), FileAttributes.Directory | FileAttributes.Hidden);
            string hiddenCharacter = Manifest(Path.Combine(costume, ".Hidden character", "Hidden"), "Hidden", "Juri", source);
            string mismatch = Manifest(Path.Combine(costume, "Luke", "Wrong character"), "Wrong character", "Juri", source);
            string other = Manifest(Path.Combine(costume, "Juri", "Other"), "Other", "Juri", Path.Combine(library, "Other.zip"));
            Put(Path.Combine(costume, "Juri", "Corrupt", ".slots-installer.json"), "{bad json");
            string[] expected = Installer.ListInstalled(costume).Where(o => o.Managed).Select(o => o.Id).OrderBy(x => x).ToArray();
            Require(Installer.ManagedManifests(costume).Select(s => s.Manifest.Id).OrderBy(x => x).SequenceEqual(expected), "lean discovery disagrees with installed managed visibility");

            Library.Scan(new[] { library }, tool); byte[] cacheBefore = File.ReadAllBytes(CachePath());
            File.SetAttributes(enabled, FileAttributes.ReadOnly);
            SourceRename.Outcome failed;
            try { failed = Rename(source, "Failed", library, costume, tool); }
            finally { File.SetAttributes(enabled, FileAttributes.Normal); }
            Require(!failed.Done && File.Exists(source) && File.ReadAllBytes(CachePath()).SequenceEqual(cacheBefore), "failed rename moved or invalidated cached metadata");
            int misses = Library.CacheMisses; Library.Scan(new[] { library }, tool);
            Require(Library.CacheMisses == misses, "rollback lost the old source cache");

            // A full Installed scan would touch this unrelated archive record's stale write time. Rename's first
            // pass must not run archive matching or loader decoration merely to find managed manifests.
            string archive = Path.Combine(costume, "Juri", "Manual.zip"); File.WriteAllText(archive, "manual archive fixture");
            string recordsFile = Path.Combine(ArchiveInstalls.DataDirOverride, ArchiveInstalls.FileName); Directory.CreateDirectory(Path.GetDirectoryName(recordsFile));
            Json.Write(recordsFile, new ArchiveInstallFile { Records = new List<ArchiveInstallRecord> { new ArchiveInstallRecord { Id = "manual", Path = archive, SourcePath = other, Character = "Juri", Size = new FileInfo(archive).Length, WriteTicks = 0, Sha256 = ReplaceEngine.Sha(archive) } } });
            byte[] recordsBefore = File.ReadAllBytes(recordsFile);
            SourceRename.Outcome renamed = Rename(source, "Renamed", library, costume, tool);
            Require(renamed.Done && renamed.Outfits == 2, "managed rename: " + renamed.Error + " / " + renamed.Outfits);
            Require(Json.Read<InstallManifest>(enabled).SourcePath == renamed.To && Json.Read<InstallManifest>(disabled).SourcePath == renamed.To, "enabled/disabled manifests did not follow");
            foreach (string file in new[] { hidden, hiddenAttribute, hiddenCharacter, mismatch }) Require(Json.Read<InstallManifest>(file).SourcePath == source, "an unlisted manifest changed: " + file);
            Require(Json.Read<InstallManifest>(other).SourcePath != renamed.To && File.ReadAllBytes(recordsFile).SequenceEqual(recordsBefore), "rename touched an unrelated manifest or decorated archives");
            bool refused = false;
            try { Installer.ManagedManifests(root).ToList(); } catch (IOException) { refused = true; }
            Require(refused, "lean discovery accepted a non-costume root");
        }

        private static void FilenameIdentity()
        {
            var entry = new LibraryEntry { SourcePath = @"C:\fixture\Cammy.zip", Kind = "ZIP", FilenameCharacter = "Juri", Character = "Juri", Detection = "Filename" };
            Library.RefreshFilenameIdentity(entry);
            Require(entry.FilenameCharacter == "Cammy" && entry.Character == "Cammy" && entry.Detection == "Filename", "live filename fallback stayed on the old fighter");
            entry.MiscAreas.Add("UI"); Library.RefreshFilenameIdentity(entry);
            Require(entry.Character == "" && entry.Detection == "Unknown", "Misc acquired a filename-only fighter");
            entry.Detection = "Error"; entry.DetectionError = "unreadable"; Library.RefreshFilenameIdentity(entry);
            Require(entry.Detection == "Error" && entry.FilenameCharacter == "" && entry.DetectionError == "unreadable", "rename cleared an unreadable-source error");
        }

        private static void Setup(string root, out string library, out string source, out string tool)
        {
            library = Path.Combine(root, "library"); source = Path.Combine(library, "Juri Original.zip"); tool = Path.Combine(root, "tool.exe");
            Directory.CreateDirectory(library); File.WriteAllText(tool, "fixture tool fingerprint"); Library.IdentityCacheDirectory = Path.Combine(root, "cache");
            using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                using (var text = new StreamWriter(zip.CreateEntry("Main/modinfo.ini").Open())) text.Write("name=Fixture\nauthor=Fixture author\n");
                using (var text = new StreamWriter(zip.CreateEntry("Main/natives/stm/product/model/esf/esf032/001/fixture.mesh").Open())) text.Write("fixture mesh");
            }
        }
        private static SourceRename.Outcome Rename(string source, string name, string library, string costume, string tool)
        {
            return SourceRename.Run(source, name, new[] { library }, new KeyValuePair<string, string>[0], costume, null, tool);
        }
        private static string Manifest(string folder, string name, string character, string source)
        {
            string path = Path.Combine(folder, ".slots-installer.json"); Directory.CreateDirectory(folder);
            var manifest = new InstallManifest { Id = Guid.NewGuid().ToString("N"), Name = name, Character = character, SourcePath = source, SelectedIds = new List<string> { "Main" }, Sha256 = new Dictionary<string, string> { { "fixture.mesh", "fixture" } } };
            File.WriteAllText(path, Json.Serializer().Serialize(manifest), new UTF8Encoding(true));
            return path;
        }
        private static string CachePath() { return Path.Combine(Library.IdentityCacheDirectory, Library.CacheFilename); }
        private static LibraryIdentityCache Cache() { return File.Exists(CachePath()) ? Json.Read<LibraryIdentityCache>(CachePath()) : new LibraryIdentityCache(); }
        private static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
