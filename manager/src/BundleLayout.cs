using System;
using System.Collections.Generic;
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
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("SL-BUNDLE-1!");
        internal const string GroupPrefix = ":bundle:";
        internal static readonly string[] GroupRules = { "Original", "Choose any", "Choose one", "Choose up to one" };
        internal static readonly string[] FileRules = { "Original", "Required", "Optional" };
        internal static bool IsZip(string path) { return String.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase); }

        internal static List<BundleOption> Options(ModPackage p)
        {
            return p.Components.Select(c => new BundleOption { Id=c.Id, Parent=c.ParentId, Name=c.Name, Rule=c.BundleRule ?? "Original" }).ToList();
        }

        private static int LayoutStart(byte[] comment)
        {
            int marker = comment.Length - Marker.Length;
            if (marker < 4 || !Marker.SequenceEqual(comment.Skip(marker))) return -1;
            int size = BitConverter.ToInt32(comment, marker - 4);
            if (size <= 0 || size > marker - 4) throw new InvalidDataException("Invalid bundle layout length.");
            return marker - 4 - size;
        }

        internal static void Read(ModPackage package, byte[] comment)
        {
            int start = LayoutStart(comment);
            if (start < 0) return;
            string json = Encoding.UTF8.GetString(comment, start, comment.Length - Marker.Length - 4 - start);
            var options = new JavaScriptSerializer().Deserialize<List<BundleOption>>(json);
            package.AuthorOptions = Options(package);
            package.AuthorSlotDefaults = Composer.DefaultSelection(package,false);
            package.AuthorReplaceDefaults = Composer.DefaultSelection(package,true);
            var components = Materialize(package, options);
            package.Components = components;
            package.HasBundleLayout = true;
            package.PrepareChoices();
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
                try { SaveLocked(path,stamp,options); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void SaveLocked(string path, string stamp, List<BundleOption> options)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path), ".bundle-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (SourceReader.Stamp(path) != stamp) throw new SourceChangedException();
                    if (options != null) ValidateDraft(path, options);
                    byte[] old = ZipIntegrity.EndComment(input);
                    int start = LayoutStart(old);
                    byte[] comment = old.Take(start < 0 ? old.Length : start).ToArray();
                    if (options != null)
                    {
                        var json = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(options));
                        comment = comment.Concat(json).Concat(BitConverter.GetBytes(json.Length)).Concat(Marker).ToArray();
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
                    File.Replace(temp, path, null);
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
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
}
