using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SlotsInstaller
{
    internal sealed class BundleOption
    {
        public string Id, Parent, Name, Rule;
        public override string ToString() { return Name; }
    }

    // A versioned trailer in the ZIP comment leaves every local file and central-directory
    // entry untouched. The author's original comment is retained ahead of the layout.
    internal static class BundleLayout
    {
        // 1: JSON, its length as a binary int, marker. 2 (written since): a heading line, the JSON and its length
        // as 8 hex digits, so archivers that show the ZIP comment show readable text. Both markers are 12 bytes.
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("SL-BUNDLE-1!"), TextMarker = Encoding.ASCII.GetBytes("SL-BUNDLE-2!");
        private const string Heading = "\r\nSlotsloader Manager option layout (edit it in the Manager):\r\n";
        internal const string GroupPrefix = ":bundle:";
        internal static readonly string[] GroupRules = { "Original", "Choose any", "Choose one", "Choose up to one" };
        internal static readonly string[] FileRules = { "Original", "Required", "Optional" };
        internal static bool IsZip(string path) { return String.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase); }

        internal static List<BundleOption> Options(ModPackage p)
        {
            return p.Components.Select(c => new BundleOption { Id=c.Id, Parent=c.ParentId, Name=c.Name, Rule=c.BundleRule ?? "Original" }).ToList();
        }

        // Where the layout trailer starts in a ZIP comment, and its JSON. -1: none, or damaged (a marker
        // with an impossible length), so the comment is kept as it is.
        private static int LayoutStart(byte[] comment, out string json, out bool damaged)
        {
            json = null; damaged = false;
            int marker = comment.Length - Marker.Length, size = 0, start;
            if (marker < 4) return -1;
            if (TextMarker.SequenceEqual(comment.Skip(marker)))
            {
                damaged = marker < 8 || !Int32.TryParse(Encoding.ASCII.GetString(comment, marker - 8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size) || size <= 0 || size > marker - 8;
                if (damaged) return -1;
                start = marker - 8 - size;
                string text = Encoding.UTF8.GetString(comment, start, size);
                json = text.StartsWith(Heading, StringComparison.Ordinal) ? text.Substring(Heading.Length) : text;
                return start;
            }
            if (!Marker.SequenceEqual(comment.Skip(marker))) return -1;
            size = BitConverter.ToInt32(comment, marker - 4);
            damaged = size <= 0 || size > marker - 4;
            if (damaged) return -1;
            start = marker - 4 - size;
            json = Encoding.UTF8.GetString(comment, start, size);
            return start;
        }
        private static int LayoutStart(byte[] comment)
        {
            string json; bool damaged;
            return LayoutStart(comment, out json, out damaged);
        }

        // A layout that no longer fits the ZIP (files added by another program, a damaged comment) never makes
        // the mod unusable: the author's layout is used and Edit bundle can save a new one.
        internal static void Read(ModPackage package, byte[] comment)
        {
            string json; bool damaged;
            int start = LayoutStart(comment, out json, out damaged);
            if (start < 0) { if (damaged) Ignore(package, "Its saved option layout is damaged"); return; }
            var kept = package.Components.Select(c => new { Component = c, c.Name, c.ParentId, c.BundleRule, c.InferredChoice, c.IsDefaultChoice, c.AuthorBase, c.AuthorChoiceRequired }).ToList();
            var authorOptions = Options(package);
            var slotDefaults = Composer.DefaultSelection(package,false);
            var replaceDefaults = Composer.DefaultSelection(package,true);
            string problem;
            try
            {
                var options = new JavaScriptSerializer().Deserialize<List<BundleOption>>(json);
                package.AuthorOptions = authorOptions; package.AuthorSlotDefaults = slotDefaults; package.AuthorReplaceDefaults = replaceDefaults;
                var components = Materialize(package, options);
                package.Components = components;
                package.HasBundleLayout = true;
                package.PrepareChoices();
                return;
            }
            catch (InvalidDataException ex) { problem = ex.Message; }
            catch (ArgumentException ex) { problem = ex.Message; }
            catch (InvalidOperationException ex) { problem = ex.Message; }
            foreach (var k in kept)
            {
                k.Component.Name = k.Name; k.Component.ParentId = k.ParentId; k.Component.BundleRule = k.BundleRule; k.Component.InferredChoice = k.InferredChoice;
                k.Component.IsDefaultChoice = k.IsDefaultChoice; k.Component.AuthorBase = k.AuthorBase; k.Component.AuthorChoiceRequired = k.AuthorChoiceRequired;
            }
            package.AuthorOptions = null; package.AuthorSlotDefaults = package.AuthorReplaceDefaults = null; package.HasBundleLayout = false;
            Program.LogLine(package.SourcePath + ": saved option layout ignored (" + problem + ")");
            Ignore(package, "Its saved option layout no longer matches its options");
        }
        private static void Ignore(ModPackage package, string why)
        {
            package.LayoutError = why + ", so the author's layout is shown. Edit bundle saves a new one.";
            package.Warnings.Insert(0, package.LayoutError);
        }

        private static List<ModComponent> Materialize(ModPackage package, List<BundleOption> options)
        {
            if (options == null || options.Count == 0) throw new InvalidDataException("The bundle has no options.");
            var nodes = new Dictionary<string,BundleOption>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in options)
            {
                if (item == null || item.Id == null || nodes.ContainsKey(item.Id) || String.IsNullOrWhiteSpace(item.Name))
                    throw new InvalidDataException("Each option needs a name and a unique ID.");
                nodes.Add(item.Id, item);
            }
            if (package.Components.Any(c => !c.Id.StartsWith(GroupPrefix, StringComparison.Ordinal) && !nodes.ContainsKey(c.Id)))
                throw new InvalidDataException("The bundle no longer matches the options in this ZIP.");
            var result = new List<ModComponent>();
            foreach (var item in options)
            {
                var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ancestor = item;
                while (ancestor.Parent != null)
                {
                    if (!chain.Add(ancestor.Id) || !nodes.TryGetValue(ancestor.Parent, out ancestor) || chain.Count > 128)
                        throw new InvalidDataException("Invalid or circular nesting for " + item.Name + ".");
                }
                ModComponent c = package.Find(item.Id);
                if (c == null)
                {
                    Guid id;
                    if (!item.Id.StartsWith(GroupPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(item.Id.Substring(GroupPrefix.Length), "N", out id))
                        throw new InvalidDataException("Unknown bundle option: " + item.Name);
                    c = new ModComponent { Id=item.Id, IsDummy=true };
                }
                string[] rules = c.IsDummy ? GroupRules : FileRules;
                if (!rules.Contains(item.Rule)) throw new InvalidDataException("Invalid selection rule for " + item.Name + ".");
                result.Add(c);
            }
            for (int i=0;i<result.Count;i++)
            {
                var c=result[i]; var item=options[i];
                c.AuthorBase=Composer.IsBase(c);
                c.AuthorChoiceRequired=c.RequiresChoice;
                c.Name=item.Name.Trim(); c.ParentId=item.Parent; c.BundleRule=item.Rule;
                if (item.Rule!="Original") { c.InferredChoice=false; c.IsDefaultChoice=false; }
            }
            foreach (var group in result.Where(c => c.IsChoiceGroup))
            {
                var children = result.Where(c => String.Equals(c.ParentId, group.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (group.RequiresChoice && children.Count == 0)
                    throw new InvalidDataException("Move at least one option into '" + group.Name + "' or change its rule to Choose any.");
                if (children.Count > 1 && children.Any(c => !c.IsDummy && Composer.IsBase(c)))
                    throw new InvalidDataException("A required option in '" + group.Name + "' would block its alternatives. Move it outside the group or mark it Optional.");
            }
            return result;
        }

        internal static void ValidateDraft(string path,List<BundleOption> options)
        {
            Materialize(PackageReader.AuthorPackage(path),options);
        }

        internal static void Save(string path, string stamp, List<BundleOption> options)
        {
            if (!IsZip(path)) throw new IOException("Bundle editing is available for ZIP archives.");
            Safety.NoReparseChain(path);
            string key = ReplaceEngine.Sha(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
            using (var mutex = new System.Threading.Mutex(false, "SlotsloaderBundle-" + key))
            {
                try { if (!mutex.WaitOne(0)) throw new IOException("Another window is saving this bundle."); }
                catch (System.Threading.AbandonedMutexException) { } // the previous writer exited before publishing
                DateTime before;
                try { before = SaveLocked(path,stamp,options); }
                finally { mutex.ReleaseMutex(); }
                BundleEdits.Record(path, stamp, before, SourceReader.Stamp(path));
            }
        }

        // Returns the archive's write time before the save. A failed replace can remove the archive after the
        // new copy is complete (ERROR_UNABLE_TO_MOVE_REPLACEMENT): DurableFile puts the copy in its place.
        private static DateTime SaveLocked(string path, string stamp, List<BundleOption> options)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path), ".bundle-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool complete = false;
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (SourceReader.Stamp(path) != stamp) throw new SourceChangedException();
                    DateTime before = File.GetLastWriteTimeUtc(path);
                    if (options != null) ValidateDraft(path, options);
                    byte[] old = ZipIntegrity.EndComment(input);
                    int start = LayoutStart(old);
                    byte[] comment = old.Take(start < 0 ? old.Length : start).ToArray();
                    if (options != null)
                    {
                        var text = Encoding.UTF8.GetBytes(Heading + new JavaScriptSerializer().Serialize(options));
                        comment = comment.Concat(text).Concat(Encoding.ASCII.GetBytes(text.Length.ToString("X8", CultureInfo.InvariantCulture))).Concat(TextMarker).ToArray();
                    }
                    if (comment.Length > UInt16.MaxValue) throw new IOException("This bundle layout is too large for a ZIP comment. Shorten option names or use fewer groups.");
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        long length = input.Length - old.Length - 2;
                        input.Position = 0;
                        CopyPrefix(input, output, length);
                        WriteComment(output, comment);
                        output.Flush(true);
                    }
                    OperationContext.ThrowIfCancellationRequested();
                    if (SourceReader.Stamp(path) != stamp) throw new SourceChangedException();
                    complete = true;
                    DurableFile.Publish(temp, path);
                    return before;
                }
            }
            finally { DurableFile.Discard(temp, path, complete); }
        }

        private static void CopyPrefix(Stream input, Stream output, long length)
        {
            byte[] buffer = new byte[128 * 1024];
            while (length > 0)
            {
                OperationContext.ThrowIfCancellationRequested();
                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (n == 0) throw new EndOfStreamException();
                output.Write(buffer,0,n);
                length -= n;
            }
        }
        private static void WriteComment(Stream target, byte[] comment)
        {
            target.WriteByte((byte)comment.Length);
            target.WriteByte((byte)(comment.Length >> 8));
            target.Write(comment,0,comment.Length);
        }

        // Recovery records identify original archive contents, including the author's
        // comment. An edit to menu organization must not orphan installed replacements.
        internal static string ArchiveHash(string path, out long size, long expectedSize = -1)
        {
            using (var input = File.OpenRead(path))
            {
                size = input.Length;
                byte[] comment = IsZip(path) ? ZipIntegrity.EndComment(input) : new byte[0];
                int start = LayoutStart(comment);
                if (start >= 0) size -= comment.Length - start;
                if (expectedSize >= 0 && size != expectedSize) return null;
                input.Position=0;
                using (var sha = FastHash.Sha256())
                {
                    if (start < 0) sha.ComputeHash(input);
                    else using (var hashStream = new System.Security.Cryptography.CryptoStream(Stream.Null, sha, System.Security.Cryptography.CryptoStreamMode.Write))
                    {
                        CopyPrefix(input, hashStream, input.Length - comment.Length - 2);
                        WriteComment(hashStream, comment.Take(start).ToArray());
                        hashStream.FlushFinalBlock();
                    }
                    return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }
    }

    // Data\bundle-edits.json: archives whose last writes were only layout saves. "Source changed" ignores them while
    // the archive still has the stamp the save left, so a menu edit never asks to reinstall unchanged files.
    internal static class BundleEdits
    {
        public sealed class Edit
        {
            public string Stamp = "";            // the archive's stamp after the last layout save
            public long OriginalWriteTicks;      // its write time before the first of these saves (UTC ticks)
        }
        private static readonly object Gate = new object();
        private static string FilePath { get { return Path.Combine(AppConfig.DataDir, "bundle-edits.json"); } }
        private static string Key(string path) { return Path.GetFullPath(path).ToUpperInvariant(); }

        private static Dictionary<string, Edit> Load()
        {
            try { if (File.Exists(FilePath)) return Json.Serializer().Deserialize<Dictionary<string, Edit>>(File.ReadAllText(FilePath)) ?? new Dictionary<string, Edit>(); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return new Dictionary<string, Edit>();
        }

        internal static void Record(string path, string stampBefore, DateTime writtenBefore, string stampAfter)
        {
            lock (Gate)
                try
                {
                    var edits = Load();
                    Edit previous;
                    long original = edits.TryGetValue(Key(path), out previous) && previous.Stamp == stampBefore ? previous.OriginalWriteTicks : writtenBefore.Ticks;
                    edits[Key(path)] = new Edit { Stamp = stampAfter, OriginalWriteTicks = original };
                    foreach (string gone in edits.Keys.Where(k => !File.Exists(k)).ToList()) edits.Remove(gone);
                    Directory.CreateDirectory(AppConfig.DataDir);
                    DurableFile.WriteJson(FilePath, edits);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }

        // True when the archive was last written by layout saves only, and its contents date from before installedUtc.
        internal static bool LayoutOnlySince(string path, DateTime installedUtc)
        {
            if (!IsArchive(path)) return false;
            lock (Gate)
            {
                Edit edit;
                if (!Load().TryGetValue(Key(path), out edit)) return false;
                try { return SourceReader.Stamp(path) == edit.Stamp && new DateTime(edit.OriginalWriteTicks, DateTimeKind.Utc) <= installedUtc; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }
        private static bool IsArchive(string path) { return !String.IsNullOrEmpty(path) && BundleLayout.IsZip(path) && File.Exists(path); }
    }
}
