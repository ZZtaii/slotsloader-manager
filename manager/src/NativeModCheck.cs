using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SlotsInstaller
{
    internal sealed class NativeCheckResult
    {
        public string Sandbox, Loader, LoaderSha256, LoaderBuild, ReportFile, LogFile, BasePak;
        public bool Accepted, SourcesPreserved;
        public int Kept, Rejected;
        public List<string> Outfits = new List<string>();
        public List<string> OmittedDlcPaks = new List<string>();
        public string Scope = "Actual native loader in an isolated game fixture, using the base PAK read-only; DLC PAKs omitted. No game rendering or runtime test.";
        public string SourceVerification = "SHA256 of all original loader metadata; base PAK header/index SHA256, length and modification time (not a full PAK-content hash). Sandbox input assets also retain their installed SHA256.";
    }
    internal static class NativeModCheck
    {
        internal static bool IsWithin(string path, string root)
        {
            string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        internal static NativeCheckResult Run(CompositionPlan plan, string character, string sevenZip, string loader, string gameRoot, string sandbox)
        {
            if (!File.Exists(loader)) throw new FileNotFoundException("Native costume_loader.exe was not found.", loader);
            string game = Path.GetFullPath(gameRoot), target = Path.GetFullPath(sandbox);
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("The loader sandbox must be a NEW directory.");
            if (IsWithin(target, game) || IsWithin(game, target) || IsWithin(target, plan.Package.SourcePath) || IsWithin(plan.Package.SourcePath, target)) throw new IOException("The sandbox must be separate from the installed game and source mod.");
            Safety.NoReparseChain(target);
            string basePak = Path.Combine(game, "re_chunk_000.pak");
            ValidateNativeArgument(target, "Sandbox");
            ValidateNativeArgument(basePak, "Base PAK");
            string metadata = Path.Combine(game, "reframework", "data", "SF6_Costumes_Data", "loader");
            if (!File.Exists(basePak) || !File.Exists(Path.Combine(metadata, "vanilla_costume_index.tsv")) || !Directory.Exists(Path.Combine(metadata, "static"))) throw new IOException("The source game needs its base PAK and installed Slotsloader metadata (loader/static and vanilla_costume_index.tsv).");
            Safety.NoReparseChain(basePak); Safety.NoReparseChain(metadata); Safety.NoReparseChain(loader);
            var result = new NativeCheckResult { Sandbox = target, Loader = loader, LoaderSha256 = HashFile(loader), BasePak = basePak };
            string baseBefore = BaseFingerprint(basePak);
            Dictionary<string,string> metadataBefore = HashTree(metadata);
            string dlc = Path.Combine(game, "dlc");
            if (Directory.Exists(dlc)) result.OmittedDlcPaks.AddRange(Directory.EnumerateFiles(dlc, "*.pak").Select(Path.GetFileName));
            string targetParent=Path.GetDirectoryName(target);
            Directory.CreateDirectory(targetParent);
            string reservation=Path.Combine(targetParent,".slotsloader-check-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(reservation);
            try { Directory.Move(reservation,target); }
            finally { if (Directory.Exists(reservation)) Directory.Delete(reservation); }
            Safety.NoReparseChain(target);
            string testMetadata = Path.Combine(target, "reframework", "data", "SF6_Costumes_Data", "loader");
            CopyMetadata(metadata, testMetadata);
            string costumeRoot = Path.Combine(target, "reframework", "costume_mods");
            OperationContext.Report("Preparing native loader fixture", 0, 0);
            InstallResult installed = Installer.Install(plan, costumeRoot, character, "CLI test outfit", sevenZip, false);
            string manifestPath = Path.Combine(installed.Folder, ".slots-installer.json");
            InstallManifest manifest = Json.Read<InstallManifest>(manifestPath);
            try
            {
                OperationContext.ThrowIfCancellationRequested();
                OperationContext.Report("Running native Slotsloader", 0, 0);
                string output = ProcessUtil.RunText(loader, ProcessUtil.Quote(target) + " --base-pak " + ProcessUtil.Quote(basePak));
                File.WriteAllText(Path.Combine(target, "loader-console.txt"), output);
                result.ReportFile = Path.Combine(target, "reframework", "data", "SF6_Costumes_Data", "outfits.json");
                result.LogFile = Path.Combine(target, "SF6_CostumeLoader.log");
                if (!File.Exists(result.ReportFile)) throw new IOException("Native loader produced no outfits.json. Inspect the sandbox log.");
                var data = Json.Read<Dictionary<string,object>>(result.ReportFile);
                result.LoaderBuild = Value(data, "loader");
                var outfits = Rows(data, "outfits");
                result.Kept = outfits.Count;
                result.Rejected = Rows(data, "left_out").Count;
                result.Outfits.AddRange(outfits.Select(o => Value(o, "fighter_dir") + ": " + Value(o, "name") + " (" + Value(o, "label") + ")"));
                string pak = Value(data, "pak");
                result.Accepted = result.Kept > 0 && result.Rejected == 0 && !String.IsNullOrEmpty(pak) && File.Exists(Safety.Under(target, pak));
                // The native loader may cache and emit PAKs, but must retain its input payload.
                if (!manifest.Sha256.All(f => File.Exists(Safety.Under(installed.Folder, f.Key)) && HashFile(Safety.Under(installed.Folder, f.Key)) == f.Value)) throw new IOException("The native loader changed a sandbox source asset. Inspect the retained fixture.");
                return result;
            }
            finally
            {
                result.SourcesPreserved = baseBefore == BaseFingerprint(basePak) && SameHashes(metadataBefore, HashTree(metadata));
                Json.Write(Path.Combine(target, "native-check.json"), result);
                if (!result.SourcesPreserved) throw new IOException("Source preservation check failed. Inspect the native-check.json report.");
            }
        }
        internal static string Value(Dictionary<string,object> data, string key)
        {
            object value; return data != null && data.TryGetValue(key, out value) ? Convert.ToString(value) : "";
        }
        // The current native executable converts narrow argv through CP_ACP into MAX_PATH buffers.
        // Refuse unsupported input before preparing a fixture instead of letting that wrapper truncate it.
        internal static void ValidateNativeArgument(string path, string label)
        {
            if (path.Length >= 260) throw new IOException(label + " path must be shorter than 260 characters for this native loader executable. Use a shorter test folder.");
            try
            {
                Encoding ansi = Encoding.GetEncoding(Encoding.Default.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                if (!String.Equals(ansi.GetString(ansi.GetBytes(path)), path, StringComparison.Ordinal)) throw new EncoderFallbackException();
            }
            catch (EncoderFallbackException) { throw new IOException(label + " path contains characters unsupported by this native loader's Windows code page. Choose a compatible test folder."); }
        }
        internal static List<Dictionary<string,object>> Rows(Dictionary<string,object> data, string key)
        {
            object value;
            var rows = new List<Dictionary<string,object>>();
            if (data == null || !data.TryGetValue(key, out value) || !(value is IEnumerable)) return rows;
            foreach (object item in (IEnumerable)value) { var row = item as Dictionary<string,object>; if (row != null) rows.Add(row); }
            return rows;
        }
        internal static string HashFile(string file)
        {
            using (var sha = FastHash.Sha256()) using (var input = File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        private static Dictionary<string,string> HashTree(string root)
        {
            var hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in EnumerateSafe(root)) hashes.Add(file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar), HashFile(file));
            return hashes;
        }
        private static bool SameHashes(Dictionary<string,string> a, Dictionary<string,string> b) { return a.Count == b.Count && a.All(pair => b.ContainsKey(pair.Key) && b[pair.Key] == pair.Value); }
        private static IEnumerable<string> EnumerateSafe(string root)
        {
            Safety.NoReparseChain(root);
            foreach (string path in Directory.EnumerateFileSystemEntries(root))
            {
                Safety.NoReparse(path);
                if (Directory.Exists(path)) { foreach (string child in EnumerateSafe(path)) yield return child; }
                else yield return path;
            }
        }
        private static void CopyMetadata(string source, string target)
        {
            foreach (string file in EnumerateSafe(source))
            {
                OperationContext.ThrowIfCancellationRequested();
                string destination = Safety.Under(target, file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file, destination, false);
            }
        }
        private static string BaseFingerprint(string file)
        {
            var info = new FileInfo(file);
            using (var input = File.OpenRead(file))
            using (var reader = new BinaryReader(input))
            using (var sha = FastHash.Sha256())
            {
                byte[] header = reader.ReadBytes(16);
                if (header.Length != 16 || BitConverter.ToUInt32(header, 0) != 0x414B504B || header[4] != 4) throw new IOException("The base PAK header is not the expected format.");
                long tableSize = (long)BitConverter.ToUInt32(header, 8) * 48;
                if (tableSize > info.Length - 16 || tableSize > 256L * 1024 * 1024) throw new IOException("Base PAK index is outside supported bounds.");
                sha.TransformBlock(header, 0, header.Length, header, 0);
                byte[] buffer = new byte[81920];
                while (tableSize > 0) { int n = input.Read(buffer, 0, (int)Math.Min(tableSize, buffer.Length)); if (n == 0) throw new EndOfStreamException(); sha.TransformBlock(buffer, 0, n, buffer, 0); tableSize -= n; }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return info.Length + ":" + info.LastWriteTimeUtc.Ticks + ":" + BitConverter.ToString(sha.Hash);
            }
        }
    }
}
