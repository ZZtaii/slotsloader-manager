using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    internal enum RecoveryKind { Backup, Trash }
    internal sealed class RecoveryEntry
    {
        internal string Folder, Name, Character, Id, SourcePath, ManifestText;
        internal RecoveryKind Kind;
        internal DateTime InstalledUtc;
        internal long Bytes;
        internal int FileCount;
        internal RemovedReplacement Replacement;   // a replacement removed with Uninstall (game files)
        internal FluffyRemoved Fluffy;             // 2.2.0: a Fluffy mod uninstalled from the Manager
        internal ReleasedFile Released;            // 2.2.0: a genuine game file behind a file kept from another tool
        internal ForeignCopy Foreign;              // 2.2.0: another tool's file replaced by Restore original / Reapply
        internal string TypeLabel { get { return Replacement != null ? "Replacement" : Fluffy != null ? "External manager mod" : Released != null ? "Original file" : Foreign != null ? "Kept file" : Kind.ToString(); } }
    }
    internal sealed class RecoveryListing
    {
        internal readonly List<RecoveryEntry> Entries = new List<RecoveryEntry>();
        internal readonly List<string> Warnings = new List<string>();
    }
    internal static class RecoveryStore
    {
        private const string ManifestName = ".slots-installer.json";
        internal static RecoveryListing List(string costumeRoot)
        {
            string root=ValidateRoot(costumeRoot);
            var result=new RecoveryListing();
            foreach (RecoveryKind kind in new[]{RecoveryKind.Backup,RecoveryKind.Trash})
            {
                string container=Container(root,kind);
                if (!Directory.Exists(container)) continue;
                Safety.NoReparseChain(container);
                foreach (string folder in Directory.EnumerateDirectories(container))
                {
                    OperationContext.ThrowIfCancellationRequested();
                    try { result.Entries.Add(ReadEntry(root,folder,kind)); }
                    catch (IOException ex) { result.Warnings.Add(Path.GetFileName(folder)+": "+ex.Message); }
                    catch (UnauthorizedAccessException ex) { result.Warnings.Add(Path.GetFileName(folder)+": "+ex.Message); }
                }
            }
            result.Entries.Sort((a,b)=>b.InstalledUtc.CompareTo(a.InstalledUtc));
            return result;
        }
        internal static string Restore(string costumeRoot,RecoveryEntry entry)
        {
            using (Installer.AcquireMutation(costumeRoot))
            {
                string root=ValidateRoot(costumeRoot);
                RecoveryEntry current=ValidateSnapshot(root,entry);
                string character=Safety.Under(root,current.Character), destination=Safety.Under(character,current.Name);
                Safety.NoReparseChain(destination);
                if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("An outfit with this name already exists; Restore never overwrites outfits.");
                OperationContext.ThrowIfCancellationRequested();
                using (OperationContext.NonCancellable())
                {
                    Directory.CreateDirectory(character);
                    Safety.NoReparseChain(destination);
                    // Never replace another folder. If updating the loader freshness
                    // marker fails, put the retained copy back before reporting failure.
                    Directory.Move(current.Folder,destination);
                    try { Directory.SetLastWriteTimeUtc(destination,DateTime.UtcNow); }
                    catch
                    {
                        if (!Directory.Exists(current.Folder) && Directory.Exists(destination)) Directory.Move(destination,current.Folder);
                        throw;
                    }
                }
                return destination;
            }
        }
        internal static void Delete(string costumeRoot,RecoveryEntry entry)
        {
            using (Installer.AcquireMutation(costumeRoot))
            {
                string root=ValidateRoot(costumeRoot);
                RecoveryEntry current=ValidateSnapshot(root,entry);
                if (!String.IsNullOrWhiteSpace(current.SourcePath))
                {
                    string source=Path.GetFullPath(current.SourcePath);
                    if (source.Equals(current.Folder,StringComparison.OrdinalIgnoreCase) || source.StartsWith(current.Folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The mod's recorded source is inside this copy, so it can't be deleted here.");
                }
                OperationContext.ThrowIfCancellationRequested();
                using (OperationContext.NonCancellable()) Safety.DeleteGenerated(current.Folder,Container(root,current.Kind));
            }
        }
        private static RecoveryEntry ValidateSnapshot(string root,RecoveryEntry entry)
        {
            if (entry==null) throw new IOException("Select a backup or removed outfit.");
            RecoveryEntry current=ReadEntry(root,entry.Folder,entry.Kind);
            if (current.Id!=entry.Id || current.ManifestText!=entry.ManifestText || current.Bytes!=entry.Bytes || current.FileCount!=entry.FileCount)
                throw new IOException("This copy changed. Refresh the list.");
            return current;
        }
        private static RecoveryEntry ReadEntry(string root,string folder,RecoveryKind kind)
        {
            string container=Container(root,kind), full=Path.GetFullPath(folder);
            string expected=Safety.Under(container,Path.GetFileName(full));
            if (!full.Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new IOException("The folder is outside the selected recovery area.");
            string leaf=Path.GetFileName(full);
            string pattern=kind==RecoveryKind.Backup ? @"^[0-9a-f]{32}$" : @"^[0-9a-f]{32}-[0-9]{14}-[0-9a-f]{6}$";
            if (!Regex.IsMatch(leaf,pattern)) throw new IOException("Unrecognized folder; left untouched.");
            Safety.NoReparseChain(full);
            string manifestPath=Path.Combine(full,ManifestName);
            Safety.NoReparseChain(manifestPath);
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length>1024*1024) throw new IOException("No valid Manager record; left untouched.");
            string text=File.ReadAllText(manifestPath,Encoding.UTF8);
            InstallManifest manifest;
            try { manifest=Json.Serializer().Deserialize<InstallManifest>(text); }
            catch (Exception ex) { if (!(ex is ArgumentException) && !(ex is InvalidOperationException)) throw; throw new IOException("Unreadable Manager record; left untouched.",ex); }
            if (manifest==null || manifest.Format!="sf6-slotsloader-installer-v1" || !Regex.IsMatch(manifest.Id ?? "",@"^[0-9a-f]{32}$") || manifest.SelectedIds==null || manifest.Sha256==null || manifest.Sha256.Count==0)
                throw new IOException("Invalid Manager record; left untouched.");
            Safety.Segment(manifest.Name); Safety.Segment(manifest.Character);
            if (manifest.Name.StartsWith(".") || manifest.Name.Equals("natives",StringComparison.OrdinalIgnoreCase) || !Characters.Names.Contains(manifest.Character,StringComparer.OrdinalIgnoreCase))
                throw new IOException("Invalid outfit destination in Manager record; left untouched.");
            if (kind==RecoveryKind.Trash && !leaf.StartsWith(manifest.Id+"-",StringComparison.Ordinal)) throw new IOException("Removed outfit identity does not match its folder; left untouched.");
            foreach (var hash in manifest.Sha256)
            {
                string path=Safety.ArchivePath(hash.Key);
                if (!path.StartsWith("natives/",StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(hash.Value ?? "",@"^[0-9a-fA-F]{64}$"))
                    throw new IOException("Invalid native file record; left untouched.");
            }
            DateTime installed;
            if (!DateTime.TryParse(manifest.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out installed))
                throw new IOException("Invalid outfit date in Manager record; left untouched.");
            var result=new RecoveryEntry {Folder=full,Name=manifest.Name,Character=manifest.Character,Id=manifest.Id,SourcePath=manifest.SourcePath,Kind=kind,ManifestText=text,InstalledUtc=installed.ToUniversalTime()};
            MeasureTree(full,result);
            return result;
        }
        private static void MeasureTree(string folder,RecoveryEntry entry)
        {
            var pending=new Stack<string>(); pending.Push(folder);
            while (pending.Count>0)
            {
                string directory=pending.Pop(); Safety.NoReparseChain(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    OperationContext.ThrowIfCancellationRequested(); Safety.NoReparse(path);
                    if (Directory.Exists(path)) pending.Push(path);
                    else { entry.Bytes=checked(entry.Bytes+new FileInfo(path).Length); entry.FileCount++; }
                }
            }
        }
        private static string ValidateRoot(string costumeRoot)
        {
            string root=Path.GetFullPath(costumeRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.GetFileName(root).Equals("costume_mods",StringComparison.OrdinalIgnoreCase)) throw new IOException("The destination must be the Slotsloader costume_mods folder.");
            Safety.NoReparseChain(root); return root;
        }
        private static string Container(string root,RecoveryKind kind)
        {
            if (kind!=RecoveryKind.Backup && kind!=RecoveryKind.Trash) throw new IOException("Unknown recovery area.");
            return Path.Combine(Path.GetDirectoryName(root),kind==RecoveryKind.Backup ? ".slotsloader-installer-backups" : ".slotsloader-installer-trash");
        }
    }
}
