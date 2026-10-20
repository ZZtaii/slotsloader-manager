using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    internal enum OutfitSetKind { Preset, Removed }
    // 2.4.1: what a preset holds. Older presets (no Scope) hold extra outfits.
    internal enum PresetScope { ExtraOutfits, Replacements, Both }
    internal static class PresetScopes
    {
        internal const string ExtraCode="extra_outfits", ReplacementsCode="replacements", BothCode="both";
        internal static readonly string[] Labels={"Extra outfits","Replacements","Both"};
        internal static string Code(PresetScope scope) { return scope==PresetScope.Replacements ? ReplacementsCode : scope==PresetScope.Both ? BothCode : ExtraCode; }
        // Unknown or empty = null (callers pick their default).
        internal static PresetScope? Parse(string code)
        {
            switch((code ?? "").Trim().ToLowerInvariant()) { case ExtraCode: return PresetScope.ExtraOutfits; case ReplacementsCode: return PresetScope.Replacements; case BothCode: return PresetScope.Both; default: return null; }
        }
        internal static bool Outfits(PresetScope scope) { return scope!=PresetScope.Replacements; }
        internal static bool Replacements(PresetScope scope) { return scope!=PresetScope.ExtraOutfits; }
        internal static PresetScope Of(bool outfits,bool replacements) { return outfits && replacements ? PresetScope.Both : replacements ? PresetScope.Replacements : PresetScope.ExtraOutfits; }
        // The part of `wanted` a preset can give.
        internal static PresetScope Limit(PresetScope wanted,PresetScope held) { return Of(Outfits(wanted) && Outfits(held),Replacements(wanted) && Replacements(held)); }

        // The window's last save choice (Data\presets-settings.json); Both when unset or unreadable.
        internal static string PrefsPath { get { return System.IO.Path.Combine(AppConfig.DataDir,"presets-settings.json"); } }
        internal static PresetScope LastSaveScope()
        {
            try { if(!File.Exists(PrefsPath)) return PresetScope.Both; var p=Json.Read<PresetPrefs>(PrefsPath); PresetScope? s=p==null ? null : Parse(p.SaveScope); return s ?? PresetScope.Both; }
            catch(Exception ex) { if(ex is OutOfMemoryException) throw; Program.Log(ex); return PresetScope.Both; }
        }
        internal static void RememberSaveScope(PresetScope scope)
        {
            try { System.IO.Directory.CreateDirectory(AppConfig.DataDir); Json.Write(PrefsPath,new PresetPrefs {SaveScope=Code(scope)}); }
            catch(Exception ex) { if(ex is OutOfMemoryException) throw; Program.Log(ex); }
        }
    }
    public sealed class PresetPrefs { public string Format="sf6-presets-settings-v1", SaveScope=PresetScopes.BothCode; }

    internal sealed class OutfitSetInfo
    {
        internal string Id, Name, Folder, ManifestText;
        internal OutfitSetKind Kind;
        internal DateTime CreatedUtc;
        internal long Bytes;
        internal int OutfitCount;
        internal List<string> Outfits=new List<string>();
        // 2.4.1: replacements a preset holds, highest priority first, and the method they were saved with.
        internal PresetScope Scope;
        internal int ReplacementCount;
        internal List<string> Replacements=new List<string>();
        internal string ReplacementMethod="";
        // 1.0.8: a list preset (v3): restores from the library; Copied = the entries it still keeps a copy of (no source).
        internal bool Pointers;
        internal int Copied;
    }
    internal sealed class OutfitSetListing
    {
        internal readonly List<OutfitSetInfo> Entries=new List<OutfitSetInfo>();
        internal readonly List<string> Warnings=new List<string>();
    }
    internal sealed class OutfitSetRestoreResult
    {
        internal int Restored, Skipped;                       // extra outfits
        internal int ReplacementsRestored, ReplacementsSkipped; // 2.4.1: installed / identical skipped
        internal List<string> ReplacementConflicts=new List<string>();   // same name, other contents: skipped
        internal string ReplaceMessage="";
        internal List<string> Notes=new List<string>();   // 1.0.8: v3 items not restored (and why) or restored with a change
    }
    // 2.4.1: what a restore would change, for the confirmation (no hashing: an existing extra outfit is skipped
    // only when identical, which the restore checks).
    internal sealed class OutfitSetPreview
    {
        internal PresetScope Holds;
        internal List<string> AddOutfits=new List<string>(), ExistingOutfits=new List<string>();
        internal List<string> AddReplacements=new List<string>(), SameReplacements=new List<string>(), ConflictReplacements=new List<string>();
        internal bool InstallsNatives;
        internal string ReplacementError;   // the replacement state could not be read
    }

    // This record inventories the complete outfit entry, not just native files.
    // Archives remain opaque files and managed ownership records remain bytes.
    // 2.4.1: a preset holding replacements is format v2 (older Managers leave it untouched); one holding only extra
    // outfits stays v1 and readable by them.
    internal sealed class OutfitSetRecord
    {
        public string Format="sf6-slotsloader-outfit-set-v1", Id, Name, Kind, CreatedUtc;
        public string Scope;   // 2.4.1: extra_outfits | replacements | both (absent = extra_outfits)
        public List<OutfitSetItem> Entries=new List<OutfitSetItem>();
        public OutfitSetReplacements Replacements;   // 2.4.1, v2 only (v3: the copied ones)
        public PresetPointers Pointers;   // 1.0.8, v3 only (OutfitSetPointers.cs); Entries/Replacements hold only what had no library source
    }
    public sealed class OutfitSetReplacements
    {
        public string Method="";   // loose | pak when saved (information; a restore uses the current method)
        public List<OutfitSetReplacement> Mods=new List<OutfitSetReplacement>();   // priority order, 0 = highest
    }
    // replacements\<Storage>\<file path>: the store copy of every file of Mod.
    public sealed class OutfitSetReplacement { public string Storage; public ReplaceMod Mod; }
    internal sealed class OutfitSetItem
    {
        public string RelativePath, ManagedId, Storage;
        public bool Directory, Enabled, Managed;
        public List<string> Directories=new List<string>();
        public List<OutfitSetFile> Files=new List<OutfitSetFile>();
    }
    internal sealed class OutfitSetFile { public string Path, Sha256; public long Length; }

    internal static partial class OutfitSetStore
    {
        private const string RecordName="set.json";
        private sealed class SourceItem { internal InstalledOutfit Outfit; internal OutfitSetItem Item; }
        private sealed class Move { internal string From, To; internal bool Directory; }

        internal static OutfitSetListing List(string costumeRoot)
        {
            string root=Root(costumeRoot); var result=new OutfitSetListing();
            foreach (OutfitSetKind kind in new[]{OutfitSetKind.Preset,OutfitSetKind.Removed})
            {
                string container=Container(root,kind); Safety.NoReparseChain(container);
                if (!System.IO.Directory.Exists(container)) continue;
                foreach (string folder in System.IO.Directory.EnumerateDirectories(container))
                {
                    OperationContext.ThrowIfCancellationRequested();
                    try { string text; OutfitSetRecord record=Read(root,folder,kind,false,out text); result.Entries.Add(Info(folder,record,text)); }
                    catch (IOException ex) { result.Warnings.Add(System.IO.Path.GetFileName(folder)+": "+ex.Message); }
                    catch (UnauthorizedAccessException ex) { result.Warnings.Add(System.IO.Path.GetFileName(folder)+": "+ex.Message); }
                }
            }
            result.Entries.Sort((a,b)=>b.CreatedUtc.CompareTo(a.CreatedUtc)); return result;
        }

        // Tests: an old-format preset (a full copy of every enabled extra outfit, v1), which this version still restores.
        internal static OutfitSetInfo CreatePreset(string costumeRoot,string name) { return CreateCopyPreset(costumeRoot,name,PresetScope.ExtraOutfits,null); }
        // 2.4.1: Extra outfits = enabled outfits in costume_mods; Replacements = enabled replacements and tool mods
        // (never SlotsLoader) with their order, options and method; Both = each kind that has something enabled.
        // `engine` is needed unless the scope is ExtraOutfits. 1.0.8: a list of library sources (v3, OutfitSetPointers.cs);
        // only what has no library source is copied.
        internal static OutfitSetInfo CreatePreset(string costumeRoot,string name,PresetScope scope,ReplaceEngine engine) { return CreatePreset(costumeRoot,name,scope,engine,true); }
        // Tests: the 2.4.1 format (v1/v2, everything copied).
        internal static OutfitSetInfo CreateCopyPreset(string costumeRoot,string name,PresetScope scope,ReplaceEngine engine) { return CreatePreset(costumeRoot,name,scope,engine,false); }
        private static OutfitSetInfo CreatePreset(string costumeRoot,string name,PresetScope scope,ReplaceEngine engine,bool pointers)
        {
            if (PresetScopes.Replacements(scope) && engine==null) throw new IOException("Replacements can't be saved without the game folder.");
            using (Installer.AcquireMutation(costumeRoot))
            {
                string root=Root(costumeRoot); name=SetName(name);
                if (List(root).Entries.Any(e=>e.Kind==OutfitSetKind.Preset && String.Equals(e.Name,name,StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("A preset with that name already exists. Choose another name.");
                List<InstalledOutfit> enabled=PresetScopes.Outfits(scope) ? Installer.ListInstalled(root).Where(o=>o.Enabled).ToList() : new List<InstalledOutfit>();
                if (!PresetScopes.Replacements(scope))
                {
                    if (enabled.Count==0) throw new IOException("No enabled outfits to save.");
                    return pointers ? SavePointerPreset(root,name,enabled,null,null,"") : SavePreset(root,name,enabled,null,null,"",null);
                }
                return engine.WithPresetSnapshot(delegate(ReplaceState state,List<ReplaceMod> mods,string method)
                {
                    if (scope==PresetScope.Replacements && mods.Count==0) throw new IOException("No enabled replacements to save.");
                    if (enabled.Count==0 && mods.Count==0) throw new IOException("Nothing enabled to save.");
                    Func<ReplaceMod,ModFile,string> storeFile=delegate(ReplaceMod m,ModFile f) { return engine.StoreFile(state,m,f); };
                    return pointers ? SavePointerPreset(root,name,enabled,mods,storeFile,method) : SavePreset(root,name,enabled,mods,storeFile,method,null);
                });
            }
        }
        // `pointers` (1.0.8): a v3 list preset whose enabled/mods are only the entries without a library source.
        private static OutfitSetInfo SavePreset(string root,string name,List<InstalledOutfit> enabled,List<ReplaceMod> mods,Func<ReplaceMod,ModFile,string> storeFile,string method,PresetPointers pointers)
        {
                if (enabled.Count==0 && (mods==null || mods.Count==0) && pointers==null) throw new IOException("No enabled outfits to save.");
                List<SourceItem> sources=enabled.Count==0 ? new List<SourceItem>() : Prepare(root,enabled);
                OutfitSetRecord record=Record(name,OutfitSetKind.Preset,sources);
                var replacements=new List<OutfitSetReplacement>();
                if (mods!=null && mods.Count>0)
                {
                    for (int i=0;i<mods.Count;i++) replacements.Add(new OutfitSetReplacement {Storage=i.ToString("D6",CultureInfo.InvariantCulture),Mod=SavedCopy(mods[i])});
                    record.Format=FormatV2; record.Replacements=new OutfitSetReplacements {Method=method ?? "",Mods=replacements};
                }
                if (pointers!=null)
                {
                    record.Format=FormatV3; record.Pointers=pointers;
                    if (replacements.Count==0 && pointers.Replacements.Count>0) record.Replacements=new OutfitSetReplacements {Method=method ?? "",Mods=replacements};
                }
                int outfitTotal=record.Entries.Count+(pointers==null ? 0 : pointers.Outfits.Count), replacementTotal=replacements.Count+(pointers==null ? 0 : pointers.Replacements.Count);
                record.Scope=PresetScopes.Code(PresetScopes.Of(outfitTotal>0,replacementTotal>0));
                string stage=NewStage(root), final=Safety.Under(Container(root,OutfitSetKind.Preset),record.Id);
                bool published=false;
                try
                {
                    string payload=System.IO.Path.Combine(stage,"payload"); System.IO.Directory.CreateDirectory(payload);
                    for (int i=0;i<sources.Count;i++)
                    {
                        OperationContext.Report("Saving outfit "+(i+1)+" of "+sources.Count,i,sources.Count);
                        Copy(sources[i].Outfit.Folder,Safety.Under(payload,sources[i].Item.Storage),sources[i].Item);
                    }
                    if (replacements.Count>0)
                    {
                        string saved=System.IO.Path.Combine(stage,ReplacementsFolder); System.IO.Directory.CreateDirectory(saved);
                        for (int i=0;i<replacements.Count;i++)
                        {
                            OperationContext.Report("Saving replacement "+(i+1)+" of "+replacements.Count,i,replacements.Count);
                            CopyReplacement(mods[i],storeFile,Safety.Under(saved,replacements[i].Storage),ReplacementItem(replacements[i]));
                        }
                    }
                    RecheckSources(sources);
                    Json.Write(System.IO.Path.Combine(stage,RecordName),record);
                    string manifestText=File.ReadAllText(System.IO.Path.Combine(stage,RecordName));
                    OperationContext.ThrowIfCancellationRequested();
                    using (OperationContext.NonCancellable())
                    {
                        Safety.NoReparseChain(final); System.IO.Directory.CreateDirectory(Container(root,OutfitSetKind.Preset));
                        Installer.MoveDirectory(stage,final); published=true;
                    }
                    return Info(final,record,manifestText);
                }
                finally { if (!published) Cleanup(stage,Staging(root)); }
        }
        private const string FormatV1="sf6-slotsloader-outfit-set-v1", FormatV2="sf6-slotsloader-outfit-set-v2", ReplacementsFolder="replacements";
        // The record keeps what a restore needs, nothing from the test-install era.
        private static ReplaceMod SavedCopy(ReplaceMod m)
        {
            return new ReplaceMod {Id=m.Id,Name=m.Name,SourcePath=m.SourcePath ?? "",Kind=m.Kind ?? "",InstalledUtc=m.InstalledUtc ?? "",Description=m.Description ?? "",SelectedIds=(m.SelectedIds ?? new List<string>()).ToList(),Enabled=m.Enabled,Test=false,
                Files=m.Files.Select(f=>new ModFile {Path=f.Path,Sha256=(f.Sha256 ?? "").ToLowerInvariant(),Size=f.Size,Fixes=f.Fixes ?? ""}).ToList(),Notes=(m.Notes ?? new List<string>()).ToList(),Affects=m.Affects ?? ""};
        }
        // A saved replacement as a directory inventory (its files and their parent folders).
        private static OutfitSetItem ReplacementItem(OutfitSetReplacement r)
        {
            var item=new OutfitSetItem {Directory=true,Storage=r.Storage,RelativePath=r.Mod.Name};
            var dirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModFile f in r.Mod.Files)
            {
                item.Files.Add(new OutfitSetFile {Path=f.Path,Sha256=f.Sha256,Length=f.Size});
                string[] parts=f.Path.Split('/');
                for (int i=1;i<parts.Length;i++) { string d=String.Join("/",parts,0,i); if (dirs.Add(d)) item.Directories.Add(d); }
            }
            return item;
        }
        // Store file by file into the stage, hashed while copying against the state's record of each file.
        private static void CopyReplacement(ReplaceMod mod,Func<ReplaceMod,ModFile,string> storeFile,string destination,OutfitSetItem item)
        {
            Safety.NoReparseChain(destination);
            if (Exists(destination)) throw new IOException("A prepared replacement destination already exists.");
            System.IO.Directory.CreateDirectory(destination);
            foreach (string directory in item.Directories.OrderBy(d=>d.Length)) System.IO.Directory.CreateDirectory(Safety.Under(destination,directory));
            foreach (ModFile f in mod.Files)
            {
                string from=storeFile(mod,f), to=Safety.Under(destination,f.Path);
                Safety.NoReparseChain(from); Safety.NoReparseChain(to);
                using (var input=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.Read))
                using (var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    if (input.Length!=f.Size || !String.Equals(Hash(input,output),f.Sha256,StringComparison.OrdinalIgnoreCase)) throw new IOException("The stored copy of '"+mod.Name+"' changed ("+f.Path+"). Reinstall it, then save.");
            }
            if (!Matches(Inventory(destination,true,false),item,false)) throw new IOException("The saved replacement failed verification.");
        }
        private static string ReplacementPayload(string folder,OutfitSetReplacement r) { return Safety.Under(System.IO.Path.Combine(folder,ReplacementsFolder),r.Storage); }

        // 1.0.1 (the user: "uninstall should mean uninstall"): the outfits leave costume_mods with no saved copy. One
        // whose source the library still has (Managed / Install all variants) is deleted: reinstalling it is the way
        // back. One with nothing to reinstall from (a manual drop, or its source is gone) goes to the Recycle Bin; when
        // the Recycle Bin can't take it, it stays installed and Result.Kept says why.
        internal sealed class UninstallResult { internal int Deleted, Recycled; internal readonly List<string> Kept=new List<string>(); }
        internal static bool HasSource(InstalledOutfit o)
        {
            if (o==null || (!o.Managed && !o.WholeArchive) || String.IsNullOrWhiteSpace(o.SourcePath)) return false;
            try
            {
                string source=System.IO.Path.GetFullPath(o.SourcePath).TrimEnd('\\'), folder=System.IO.Path.GetFullPath(o.Folder).TrimEnd('\\');
                if (String.Equals(source,folder,StringComparison.OrdinalIgnoreCase) || source.StartsWith(folder+"\\",StringComparison.OrdinalIgnoreCase)) return false;   // the source is this install
                return File.Exists(source) || System.IO.Directory.Exists(source);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        internal static UninstallResult UninstallOutfits(string costumeRoot,IEnumerable<InstalledOutfit> selected,IRecycleBin bin)
        {
            using (Installer.AcquireMutation(costumeRoot))
            {
                string root=Root(costumeRoot); var result=new UninstallResult();
                List<SourceItem> sources=Prepare(root,selected,false);
                foreach (SourceItem source in sources)
                    if (Installer.OutfitSnapshotStamp(source.Outfit.Folder)!=source.Outfit.SnapshotStamp) throw new IOException("An outfit changed during this operation: "+source.Item.RelativePath+". Nothing was changed.");
                OperationContext.ThrowIfCancellationRequested();
                using (OperationContext.NonCancellable())
                {
                    int done=0;
                    foreach (SourceItem source in sources)
                    {
                        string path=source.Outfit.Folder; bool directory=source.Item.Directory;
                        Safety.NoReparseChain(path);
                        if (HasSource(source.Outfit))
                        {
                            if (directory) Safety.DeleteGenerated(path,System.IO.Path.GetDirectoryName(path)); else File.Delete(path);
                            result.Deleted++;
                        }
                        else
                        {
                            long bytes=source.Item.Files.Sum(x=>x.Length);
                            string why=bin.Unavailable(path,directory,bytes);
                            if (why!=null) { result.Kept.Add(source.Outfit.Name+" (the Recycle Bin can't take it: "+why+")"); continue; }
                            bin.Recycle(path,directory);
                            if (File.Exists(path) || System.IO.Directory.Exists(path)) { result.Kept.Add(source.Outfit.Name+" (Windows left it in place)"); continue; }
                            result.Recycled++;
                        }
                        OutfitHistory.Changed(HistoryKinds.Uninstall,source.Outfit,null);   // 2.4.1 history
                        OperationContext.Report("Uninstalling outfit "+(++done)+" of "+sources.Count,done,sources.Count);
                    }
                }
                return result;
            }
        }

        internal static OutfitSetInfo RemoveOutfits(string costumeRoot,IEnumerable<InstalledOutfit> selected,string name)
        {
            using (Installer.AcquireMutation(costumeRoot))
            {
                string root=Root(costumeRoot); name=SetName(name);
                List<SourceItem> sources=Prepare(root,selected);
                OutfitSetRecord record=Record(name,OutfitSetKind.Removed,sources);
                string stage=NewStage(root), payload=System.IO.Path.Combine(stage,"payload");
                string final=Safety.Under(Container(root,OutfitSetKind.Removed),record.Id);
                var moved=new List<Move>(); bool published=false, preserve=false;
                try
                {
                    System.IO.Directory.CreateDirectory(payload); Json.Write(System.IO.Path.Combine(stage,RecordName),record);
                    string manifestText=File.ReadAllText(System.IO.Path.Combine(stage,RecordName));
                    RecheckSources(sources); OperationContext.ThrowIfCancellationRequested();
                    using (OperationContext.NonCancellable())
                    {
                        try
                        {
                            foreach (SourceItem source in sources)
                            {
                                string destination=Safety.Under(payload,source.Item.Storage);
                                var move=new Move {From=source.Outfit.Folder,To=destination,Directory=source.Item.Directory};
                                MoveEntry(move.From,move.To,move.Directory); moved.Add(move);
                                OperationContext.Report("Removing outfit "+moved.Count+" of "+sources.Count,moved.Count,sources.Count);
                            }
                            Safety.NoReparseChain(final); System.IO.Directory.CreateDirectory(Container(root,OutfitSetKind.Removed));
                            Installer.MoveDirectory(stage,final); published=true;
                            foreach (SourceItem source in sources) OutfitHistory.Changed(HistoryKinds.Uninstall,source.Outfit,null);   // 2.4.1 history
                        }
                        catch (Exception ex)
                        {
                            string errors=Rollback(moved);
                            if (errors.Length>0) { preserve=true; throw new IOException("Removal failed and could not fully roll back. The files are safe at "+stage+". "+errors,ex); }
                            throw;
                        }
                    }
                    return Info(final,record,manifestText);
                }
                finally { if (!published && !preserve) Cleanup(stage,Staging(root)); }
            }
        }

        internal static OutfitSetRestoreResult Restore(string costumeRoot,OutfitSetInfo selected) { return Restore(costumeRoot,selected,PresetScope.ExtraOutfits,null); }
        // 2.4.1: restores the chosen part of what the preset holds. Every extra-outfit conflict is found before
        // anything changes; replacements go first, through the replace engine (SF6 closed, Fluffy PAK rule,
        // journal), missing ones in saved order, identical and same-named ones skipped. The loose file loader
        // question is the caller's (OutfitSetsForm asks, the Assistant refuses), before this runs.
        internal static OutfitSetRestoreResult Restore(string costumeRoot,OutfitSetInfo selected,PresetScope scope,ReplaceEngine engine) { return Restore(costumeRoot,selected,scope,engine,null); }
        // 1.0.8 v3: copied replacements, then the listed ones (installed from the library), then copied extra outfits,
        // then listed ones; item by item, problems go to result.Notes. `sevenZip` = the settings' 7-Zip when null.
        internal static OutfitSetRestoreResult Restore(string costumeRoot,OutfitSetInfo selected,PresetScope scope,ReplaceEngine engine,string sevenZip)
        {
            using (Installer.AcquireMutation(costumeRoot))
            {
                string root=Root(costumeRoot); OutfitSetRecord record=Snapshot(root,selected);
                var result=new OutfitSetRestoreResult(); var missing=new List<OutfitSetItem>(); var taken=new List<OutfitSetItem>();
                bool v3=record.Pointers!=null;
                bool outfits=PresetScopes.Outfits(scope), replacements=PresetScopes.Replacements(scope) && record.Replacements!=null && record.Replacements.Mods.Count>0;
                bool listedReplacements=v3 && PresetScopes.Replacements(scope) && record.Pointers.Replacements.Count>0;
                if ((replacements || listedReplacements) && engine==null) throw new IOException("Replacements can't be restored without the game folder.");
                if (outfits)
                    foreach (OutfitSetItem item in record.Entries)
                    {
                        string destination=Destination(root,item); Safety.NoReparseChain(destination);
                        if (!Exists(destination)) missing.Add(item);
                        else if (Matches(Inventory(destination,item.Directory,true),item,true)) result.Skipped++;
                        else if (v3) { result.Notes.Add(item.RelativePath+": a different outfit is installed there; left as is."); taken.Add(item); }
                        else throw new IOException("A different outfit already exists at "+item.RelativePath+". Nothing was restored.");
                    }
                if (replacements)
                {
                    List<OutfitSetReplacement> saved=record.Replacements.Mods;
                    PresetReplacementPlan done;
                    result.ReplaceMessage=engine.RestorePresetReplacements(saved.Select(r=>r.Mod).ToList(),delegate(int i,string storeDir)
                    {
                        OutfitSetItem item=ReplacementItem(saved[i]);
                        Copy(ReplacementPayload(selected.Folder,saved[i]),storeDir,item);
                    },out done) ?? "";
                    result.ReplacementsRestored=done.Missing.Count; result.ReplacementsSkipped=done.Identical.Count;
                    result.ReplacementConflicts=done.Conflicts.Select(i=>saved[i].Mod.Name).ToList();
                }
                if (listedReplacements) RestorePointers(root,record,false,true,engine,sevenZip,result);
                if (missing.Count>0)
                {
                    List<OutfitSetItem> considered=record.Entries.Where(e=>!taken.Contains(e)).ToList();
                    if (v3)
                    {
                        try { RestoreOutfits(root,selected,record,considered,missing,result); }
                        catch (IOException ex) { result.Notes.Add("Copied extra outfits: not restored. "+ex.Message); }
                    }
                    else if (result.ReplacementsRestored==0) RestoreOutfits(root,selected,record,considered,missing,result);
                    else
                    {
                        try { RestoreOutfits(root,selected,record,considered,missing,result); }
                        catch (OperationCanceledException ex) { throw new IOException(ReplacePlanner.N(result.ReplacementsRestored,"replacement")+" restored; extra outfits cancelled and unchanged.",ex); }
                        catch (IOException ex) { throw new IOException(ReplacePlanner.N(result.ReplacementsRestored,"replacement")+" restored; extra outfits failed: "+ex.Message,ex); }
                    }
                }
                if (v3 && outfits) RestorePointers(root,record,true,false,null,sevenZip,result);
                return result;
            }
        }
        // `considered`: the saved entries checked again before publishing (v3 leaves out those whose place is taken).
        private static void RestoreOutfits(string root,OutfitSetInfo selected,OutfitSetRecord record,List<OutfitSetItem> considered,List<OutfitSetItem> missing,OutfitSetRestoreResult result)
        {
            {
                string stage=NewStage(root); var moved=new List<Move>(); bool preserve=false;
                var parentsCreated=new List<string>();
                try
                {
                    for (int i=0;i<missing.Count;i++)
                    {
                        OutfitSetItem item=missing[i]; OperationContext.Report("Preparing restore "+(i+1)+" of "+missing.Count,i,missing.Count);
                        Copy(Payload(selected.Folder,item),Safety.Under(stage,item.Storage),item);
                    }
                    // A changed identical destination is also a conflict; check the
                    // entire set again before publishing any of the missing entries.
                    foreach (OutfitSetItem item in considered)
                    {
                        string destination=Destination(root,item); Safety.NoReparseChain(destination);
                        bool wasMissing=missing.Contains(item);
                        if (wasMissing ? Exists(destination) : !Exists(destination) || !Matches(Inventory(destination,item.Directory,true),item,true))
                            throw new IOException("The destination changed while preparing the restore: "+item.RelativePath+". Refresh and try again.");
                    }
                    OperationContext.ThrowIfCancellationRequested();
                    using (OperationContext.NonCancellable())
                    {
                        try
                        {
                            foreach (OutfitSetItem item in missing)
                            {
                                string destination=Destination(root,item), parent=System.IO.Path.GetDirectoryName(destination);
                                Safety.NoReparseChain(destination);
                                if (!System.IO.Directory.Exists(parent)) { System.IO.Directory.CreateDirectory(parent); parentsCreated.Add(parent); }
                                var move=new Move {From=Safety.Under(stage,item.Storage),To=destination,Directory=item.Directory};
                                MoveEntry(move.From,move.To,move.Directory); moved.Add(move);
                                // Loader reports predating this restoration are stale.
                                if (item.Directory) System.IO.Directory.SetLastWriteTimeUtc(destination,DateTime.UtcNow); else File.SetLastWriteTimeUtc(destination,DateTime.UtcNow);
                                OperationContext.Report("Restoring outfit "+moved.Count+" of "+missing.Count,moved.Count,missing.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            string errors=Rollback(moved);
                            if (errors.Length>0) { preserve=true; throw new IOException("Restore failed and could not fully roll back. Prepared files are at "+stage+"; the saved copy is unchanged. "+errors,ex); }
                            throw;
                        }
                    }
                    result.Restored=moved.Count;
                    // 2.4.1 history: a removed set (Undo, Recovery) comes back as recover; a preset's outfits as installs.
                    bool removedSet=record.Kind==OutfitSetKind.Removed.ToString();
                    foreach (OutfitSetItem item in missing) OutfitHistory.Restored(removedSet ? HistoryKinds.Recover : HistoryKinds.Install,Destination(root,item),item.RelativePath,item.Managed,removedSet ? "" : "from preset “"+record.Name+"”");
                }
                finally
                {
                    if (!preserve) Cleanup(stage,Staging(root));
                    foreach (string parent in parentsCreated) try { Safety.NoReparseChain(parent); if (System.IO.Directory.Exists(parent) && !System.IO.Directory.EnumerateFileSystemEntries(parent).Any()) System.IO.Directory.Delete(parent); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        internal static void Delete(string costumeRoot,OutfitSetInfo selected)
        {
            using (Installer.AcquireMutation(costumeRoot))
            {
                string root=Root(costumeRoot); Snapshot(root,selected); OperationContext.ThrowIfCancellationRequested();
                using (OperationContext.NonCancellable()) Safety.DeleteGenerated(selected.Folder,Container(root,selected.Kind));
            }
        }

        private static List<SourceItem> Prepare(string root,IEnumerable<InstalledOutfit> selection,bool hash=true)
        {
            List<InstalledOutfit> selected=selection==null ? new List<InstalledOutfit>() : selection.ToList();
            if (selected.Count==0) throw new IOException("Select at least one installed outfit.");
            var fresh=Installer.ListInstalled(root).ToDictionary(o=>System.IO.Path.GetFullPath(o.Folder),StringComparer.OrdinalIgnoreCase);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result=new List<SourceItem>();
            foreach (InstalledOutfit item in selected)
            {
                OperationContext.ThrowIfCancellationRequested();
                if (item==null || String.IsNullOrWhiteSpace(item.Folder)) throw new IOException("The selected outfit is invalid. Refresh the Installed list.");
                string path=System.IO.Path.GetFullPath(item.Folder); InstalledOutfit now;
                if (!seen.Add(path)) throw new IOException("The same outfit was selected more than once.");
                if (!fresh.TryGetValue(path,out now) || now.Enabled!=item.Enabled || now.Managed!=item.Managed || now.Id!=item.Id || now.Character!=item.Character || now.Name!=item.Name || String.IsNullOrEmpty(item.SnapshotStamp) || now.SnapshotStamp!=item.SnapshotStamp)
                    throw new IOException(item.Name+" changed. Refresh the Installed list.");
                Safety.Segment(now.Character); Safety.Segment(now.Name);
                if (!now.Enabled && (!now.Managed || !GuidName(now.Id))) throw new IOException("A disabled outfit can be uninstalled here only if the Manager installed it.");
                string relative=now.Character+"/"+(now.Enabled ? System.IO.Path.GetFileName(path) : now.Name);
                bool directory=System.IO.Directory.Exists(path);
                var inventory=Inventory(path,directory,hash);
                inventory.RelativePath=relative; inventory.Directory=directory; inventory.Enabled=now.Enabled;
                inventory.Managed=now.Managed; inventory.ManagedId=now.Managed ? now.Id : ""; inventory.Storage=result.Count.ToString("D6",CultureInfo.InvariantCulture);
                if (!String.Equals(Destination(root,inventory),path,StringComparison.OrdinalIgnoreCase)) throw new IOException("The selected outfit is outside its expected location.");
                ValidateOwnership(path,inventory);
                result.Add(new SourceItem {Outfit=now,Item=inventory});
                OperationContext.Report("Reviewing installed outfits",result.Count,selected.Count);
            }
            return result;
        }
        private static void RecheckSources(List<SourceItem> sources)
        {
            foreach (SourceItem source in sources)
            {
                OperationContext.ThrowIfCancellationRequested();
                if (Installer.OutfitSnapshotStamp(source.Outfit.Folder)!=source.Outfit.SnapshotStamp || !Matches(Inventory(source.Outfit.Folder,source.Item.Directory,true),source.Item,true))
                    throw new IOException("An outfit changed during this operation: "+source.Item.RelativePath+". Nothing was changed.");
            }
        }
        private static OutfitSetRecord Record(string name,OutfitSetKind kind,List<SourceItem> sources)
        {
            return new OutfitSetRecord {Id=Guid.NewGuid().ToString("N"),Name=name,Kind=kind.ToString(),CreatedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),Entries=sources.Select(s=>s.Item).ToList()};
        }
        private static OutfitSetRecord Snapshot(string root,OutfitSetInfo selected)
        {
            if (selected==null) throw new IOException("Select a preset or removed selection.");
            string text; OutfitSetRecord record=Read(root,selected.Folder,selected.Kind,true,out text);
            if (record.Id!=selected.Id || text!=selected.ManifestText) throw new IOException("This saved copy changed. Refresh the list.");
            return record;
        }
        private static OutfitSetRecord Read(string root,string folder,OutfitSetKind kind,bool hash,out string text)
        {
            string full=System.IO.Path.GetFullPath(folder), leaf=System.IO.Path.GetFileName(full);
            if (!GuidName(leaf) || !String.Equals(full,Safety.Under(Container(root,kind),leaf),StringComparison.OrdinalIgnoreCase)) throw new IOException("Unrecognized saved-set folder; left untouched.");
            Safety.NoReparseChain(full); string recordPath=System.IO.Path.Combine(full,RecordName); Safety.NoReparseChain(recordPath);
            if (!File.Exists(recordPath) || new FileInfo(recordPath).Length>64L*1024*1024) throw new IOException("No valid saved-set record; left untouched.");
            text=File.ReadAllText(recordPath); OutfitSetRecord record;
            try { record=Json.Serializer().Deserialize<OutfitSetRecord>(text); }
            catch (Exception ex) { if (!(ex is ArgumentException) && !(ex is InvalidOperationException)) throw; throw new IOException("Unreadable saved-set record; left untouched.",ex); }
            DateTime created;
            bool v2=record!=null && record.Format==FormatV2, v3=record!=null && record.Format==FormatV3;
            if (record==null || (record.Format!=FormatV1 && !v2 && !v3) || record.Id!=leaf || record.Kind!=kind.ToString() || record.Entries==null || (record.Entries.Count==0 && !v2 && !v3) || !DateTime.TryParse(record.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out created)) throw new IOException("Invalid saved-set record; left untouched.");
            // v1 holds extra outfits only; v2 is a preset with at least one replacement; v3 (1.0.8) a preset of pointers
            // plus copies of what had no library source (each part may be empty, not all). A stated scope must match.
            if (v3)
            {
                if (kind!=OutfitSetKind.Preset || record.Pointers==null || (record.Replacements!=null && record.Replacements.Mods==null)) throw new IOException("Invalid saved-set record; left untouched.");
                ValidatePointers(root,record,new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            else if (record.Pointers!=null || (v2 ? kind!=OutfitSetKind.Preset || record.Replacements==null || record.Replacements.Mods==null || record.Replacements.Mods.Count==0 : record.Replacements!=null)) throw new IOException("Invalid saved-set record; left untouched.");
            bool copiedReplacements=record.Replacements!=null && record.Replacements.Mods.Count>0;
            int outfitTotal=record.Entries.Count+(v3 ? record.Pointers.Outfits.Count : 0), replacementTotal=(copiedReplacements ? record.Replacements.Mods.Count : 0)+(v3 ? record.Pointers.Replacements.Count : 0);
            if (outfitTotal+replacementTotal==0) throw new IOException("Invalid saved-set record; left untouched.");
            if (!String.IsNullOrEmpty(record.Scope) && record.Scope!=PresetScopes.Code(PresetScopes.Of(outfitTotal>0,replacementTotal>0))) throw new IOException("Invalid saved-set record; left untouched.");
            var savedReplacements=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (copiedReplacements)
                foreach (OutfitSetReplacement r in record.Replacements.Mods)
                {
                    if (r==null || r.Mod==null || !Regex.IsMatch(r.Storage ?? "",@"^[0-9]{6}$") || !savedReplacements.Add(r.Storage) || String.IsNullOrWhiteSpace(r.Mod.Name) || r.Mod.Name.Length>300 || r.Mod.Files==null || r.Mod.Files.Count==0) throw new IOException("Invalid saved replacement; left untouched.");
                    if (r.Mod.SelectedIds==null) r.Mod.SelectedIds=new List<string>();
                    if (r.Mod.Notes==null) r.Mod.Notes=new List<string>();
                    var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ModFile f in r.Mod.Files)
                    {
                        if (f==null || f.Size<0 || !Regex.IsMatch(f.Sha256 ?? "",@"^[0-9a-fA-F]{64}$") || String.IsNullOrEmpty(f.Path) || CanonicalRelative(f.Path)!=f.Path || !files.Add(f.Path)) throw new IOException("Invalid saved replacement file inventory.");
                        try { ReplaceEngine.ValidateDestination(f.Path); } catch (IOException ex) { throw new IOException("A saved replacement names a protected game file; left untouched.",ex); }
                    }
                    if (LoaderCostumePak.IsLoaderMod(r.Mod)) throw new IOException("A preset can't hold Slotsloader; left untouched.");
                    if (!Matches(Inventory(ReplacementPayload(full,r),true,hash),ReplacementItem(r),hash)) throw new IOException("Saved replacement files changed or are incomplete: "+r.Mod.Name);
                }
            SetName(record.Name); var destinations=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var storage=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (v3) foreach (PresetOutfitPointer p in record.Pointers.Outfits) destinations.Add(Safety.Under(root,p.Character+"/"+p.Folder));   // a copy may not share a pointer's place
            foreach (OutfitSetItem item in record.Entries)
            {
                if (item==null || item.Files==null || item.Directories==null || !Regex.IsMatch(item.Storage ?? "",@"^[0-9]{6}$") || !storage.Add(item.Storage) || !destinations.Add(Destination(root,item))) throw new IOException("Invalid or repeated saved outfit entry.");
                if (kind==OutfitSetKind.Preset && !item.Enabled) throw new IOException("A preset contains an invalid disabled entry.");
                var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string directory in item.Directories) if (!item.Directory || CanonicalRelative(directory)!=directory || !paths.Add(directory)) throw new IOException("Invalid saved directory inventory.");
                foreach (OutfitSetFile file in item.Files)
                {
                    if (file==null || file.Length<0 || !Regex.IsMatch(file.Sha256 ?? "",@"^[0-9a-fA-F]{64}$") || (item.Directory ? CanonicalRelative(file.Path)!=file.Path : file.Path!="") || !paths.Add(file.Path)) throw new IOException("Invalid saved file inventory.");
                }
                if (!item.Directory && (item.Files.Count!=1 || item.Directories.Count!=0 || item.Managed)) throw new IOException("Invalid saved archive entry.");
                ValidateOwnership(Payload(full,item),item);
                if (!Matches(Inventory(Payload(full,item),item.Directory,hash),item,hash)) throw new IOException("Saved outfit files changed or are incomplete: "+item.RelativePath);
            }
            string payload=System.IO.Path.Combine(full,"payload"); Safety.NoReparseChain(payload);
            string replacementsDir=System.IO.Path.Combine(full,ReplacementsFolder); Safety.NoReparseChain(replacementsDir);
            if (!System.IO.Directory.Exists(payload) || System.IO.Directory.EnumerateFileSystemEntries(full).Any(p=>!String.Equals(System.IO.Path.GetFileName(p),RecordName,StringComparison.Ordinal) && !String.Equals(System.IO.Path.GetFileName(p),"payload",StringComparison.Ordinal) && !(copiedReplacements && String.Equals(System.IO.Path.GetFileName(p),ReplacementsFolder,StringComparison.Ordinal))) || System.IO.Directory.EnumerateFileSystemEntries(payload).Any(p=>!storage.Contains(System.IO.Path.GetFileName(p)))) throw new IOException("Unrecognized extra files in saved set; left untouched.");
            if (copiedReplacements && (!System.IO.Directory.Exists(replacementsDir) || System.IO.Directory.EnumerateFileSystemEntries(replacementsDir).Any(p=>!savedReplacements.Contains(System.IO.Path.GetFileName(p))))) throw new IOException("Unrecognized extra files in saved set; left untouched.");
            return record;
        }
        private static OutfitSetInfo Info(string folder,OutfitSetRecord record,string text)
        {
            List<OutfitSetReplacement> mods=record.Replacements==null || record.Replacements.Mods==null ? new List<OutfitSetReplacement>() : record.Replacements.Mods;
            // 1.0.8 v3: pointers first (in their saved order), then the copied entries, marked "(copy)"; Bytes = the copies
            // plus the record.
            PresetPointers p=record.Pointers;
            var outfits=(p==null ? new List<string>() : p.Outfits.Select(o=>o.Character+"/"+o.Folder).ToList()).Concat(record.Entries.Select(e=>e.RelativePath+(e.Enabled ? "" : " (disabled)")+(p==null ? "" : " (copy)"))).ToList();
            var replacements=(p==null ? new List<string>() : p.Replacements.Select(r=>r.Name).ToList()).Concat(mods.Select(r=>r.Mod.Name+(p==null ? "" : " (copy)"))).ToList();
            return new OutfitSetInfo {Folder=folder,Id=record.Id,Name=record.Name,Kind=(OutfitSetKind)Enum.Parse(typeof(OutfitSetKind),record.Kind),CreatedUtc=DateTime.Parse(record.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(),
                Bytes=record.Entries.Sum(e=>e.Files.Sum(f=>f.Length))+mods.Sum(r=>r.Mod.Files.Sum(f=>f.Size))+(p==null ? 0 : RecordBytes(text)),OutfitCount=outfits.Count,Outfits=outfits,ManifestText=text,
                Scope=PresetScopes.Of(outfits.Count>0,replacements.Count>0),ReplacementCount=replacements.Count,Replacements=replacements,ReplacementMethod=record.Replacements==null ? "" : record.Replacements.Method ?? "",
                Pointers=p!=null,Copied=p==null ? 0 : record.Entries.Count+mods.Count};
        }

        // 2.4.1: the confirmation's list of what a restore of `scope` would change. Reads the record without
        // hashing payloads (the restore verifies every byte).
        internal static OutfitSetPreview Preview(string costumeRoot,OutfitSetInfo selected,PresetScope scope,ReplaceEngine engine)
        {
            string root=Root(costumeRoot); string text;
            if (selected==null) throw new IOException("Select a preset or removed selection.");
            OutfitSetRecord record=Read(root,selected.Folder,selected.Kind,false,out text);
            if (record.Id!=selected.Id || text!=selected.ManifestText) throw new IOException("This saved copy changed. Refresh the list.");
            bool copied=record.Replacements!=null && record.Replacements.Mods.Count>0;
            var preview=new OutfitSetPreview {Holds=PresetScopes.Of(record.Entries.Count>0 || (record.Pointers!=null && record.Pointers.Outfits.Count>0),copied || (record.Pointers!=null && record.Pointers.Replacements.Count>0))};
            if (PresetScopes.Outfits(scope))
                foreach (OutfitSetItem item in record.Entries) (Exists(Destination(root,item)) ? preview.ExistingOutfits : preview.AddOutfits).Add(item.RelativePath+(item.Enabled ? "" : " (disabled)"));
            if (record.Pointers!=null) PreviewPointers(record,scope,engine,root,preview);   // 1.0.8 v3
            if (PresetScopes.Replacements(scope) && copied)
            {
                List<ReplaceMod> saved=record.Replacements.Mods.Select(r=>r.Mod).ToList();
                try
                {
                    if (engine==null) throw new IOException("No game folder.");
                    PresetReplacementPlan plan=engine.PlanPresetReplacements(saved);
                    preview.AddReplacements.AddRange(plan.Missing.Select(i=>saved[i].Name));
                    preview.SameReplacements.AddRange(plan.Identical.Select(i=>saved[i].Name));
                    preview.ConflictReplacements.AddRange(plan.Conflicts.Select(i=>saved[i].Name));
                    preview.InstallsNatives|=plan.InstallsNatives;
                }
                catch (IOException ex) { preview.ReplacementError=ex.Message; preview.AddReplacements.AddRange(saved.Select(m=>m.Name)); }
            }
            return preview;
        }
        private static string Destination(string root,OutfitSetItem item)
        {
            if (item==null || String.IsNullOrEmpty(item.RelativePath)) throw new IOException("Invalid saved outfit path.");
            string relative=CanonicalRelative(item.RelativePath); string[] parts=relative.Split('/');
            if (relative!=item.RelativePath || parts.Length!=2 || parts[0].StartsWith(".") || parts[1].StartsWith(".")) throw new IOException("Invalid saved outfit destination.");
            if (item.Managed && (!item.Directory || !GuidName(item.ManagedId))) throw new IOException("Invalid managed outfit identity.");
            if (!item.Directory)
            {
                string extension=System.IO.Path.GetExtension(parts[1]).ToLowerInvariant();
                if (extension!=".zip" && extension!=".rar" && extension!=".7z" && extension!=".pak") throw new IOException("Unsupported manual outfit file.");
            }
            if (item.Enabled) return Safety.Under(root,relative);
            if (!item.Managed) throw new IOException("Unrecognized disabled outfit entry.");
            return Safety.Under(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root),".slotsloader-installer-disabled"),item.ManagedId);
        }
        private static string CanonicalRelative(string path)
        {
            if (String.IsNullOrEmpty(path)) throw new IOException("An inventory path is empty.");
            return Safety.ArchivePath(path);
        }
        private static void ValidateOwnership(string path,OutfitSetItem item)
        {
            if (!item.Managed) return;
            string manifestPath=System.IO.Path.Combine(path,".slots-installer.json"); Safety.NoReparseChain(manifestPath);
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length>1024*1024) throw new IOException("Managed outfit ownership record is missing or unreadable.");
            InstallManifest manifest;
            try { manifest=Json.Read<InstallManifest>(manifestPath); }
            catch (Exception ex) { if (!(ex is ArgumentException) && !(ex is InvalidOperationException)) throw; throw new IOException("Managed outfit ownership record is unreadable.",ex); }
            string[] destination=item.RelativePath.Split('/');
            if (manifest==null || manifest.Format!="sf6-slotsloader-installer-v1" || manifest.Id!=item.ManagedId || manifest.SelectedIds==null || manifest.Sha256==null || manifest.Sha256.Count==0 || !String.Equals(manifest.Character,destination[0],StringComparison.OrdinalIgnoreCase) || !String.Equals(manifest.Name,destination[1],StringComparison.OrdinalIgnoreCase))
                throw new IOException("Managed outfit ownership does not match its saved destination: "+item.RelativePath);
        }
        private static OutfitSetItem Inventory(string path,bool directory,bool hash)
        {
            Safety.NoReparseChain(path);
            if (System.IO.Directory.Exists(path)!=directory || (!directory && !File.Exists(path))) throw new IOException("An outfit is missing or changed type: "+path);
            var result=new OutfitSetItem {Directory=directory};
            if (!directory) { result.Files.Add(FileInventory(path,"",hash)); return result; }
            var pending=new Stack<string>(); pending.Push(path);
            while (pending.Count>0)
            {
                string current=pending.Pop(); Safety.NoReparseChain(current);
                foreach (string child in System.IO.Directory.EnumerateFileSystemEntries(current))
                {
                    OperationContext.ThrowIfCancellationRequested(); Safety.NoReparse(child);
                    string relative=CanonicalRelative(child.Substring(path.Length+1).Replace('\\','/'));
                    if (System.IO.Directory.Exists(child)) { result.Directories.Add(relative); pending.Push(child); }
                    else result.Files.Add(FileInventory(child,relative,hash));
                }
            }
            return result;
        }
        private static OutfitSetFile FileInventory(string path,string relative,bool hash)
        {
            OperationContext.ThrowIfCancellationRequested(); Safety.NoReparseChain(path);
            if (!hash) return new OutfitSetFile {Path=relative,Length=new FileInfo(path).Length};
            using (var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                return new OutfitSetFile {Path=relative,Length=input.Length,Sha256=Hash(input,null)};
            }
        }
        private static string Hash(Stream input,Stream output)
        {
            using (var sha=FastHash.Sha256())
            {
                byte[] buffer=new byte[1024*1024]; int count;
                while ((count=input.Read(buffer,0,buffer.Length))>0)
                {
                    OperationContext.ThrowIfCancellationRequested(); if (output!=null) output.Write(buffer,0,count); sha.TransformBlock(buffer,0,count,null,0);
                    OperationContext.Report(output==null ? "Verifying outfit files" : "Copying outfit files",input.Position,input.Length);
                }
                sha.TransformFinalBlock(new byte[0],0,0); return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
            }
        }
        private static bool Matches(OutfitSetItem actual,OutfitSetItem expected,bool hash)
        {
            if (actual.Directory!=expected.Directory || actual.Files.Count!=expected.Files.Count || !new HashSet<string>(actual.Directories,StringComparer.OrdinalIgnoreCase).SetEquals(expected.Directories)) return false;
            var files=actual.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
            foreach (OutfitSetFile file in expected.Files)
            {
                OutfitSetFile found; if (!files.TryGetValue(file.Path,out found) || found.Length!=file.Length || (hash && !String.Equals(found.Sha256,file.Sha256,StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }
        private static void Copy(string source,string destination,OutfitSetItem item)
        {
            Safety.NoReparseChain(source); Safety.NoReparseChain(destination);
            if (Exists(destination)) throw new IOException("A prepared outfit destination already exists.");
            if (item.Directory)
            {
                System.IO.Directory.CreateDirectory(destination);
                foreach (string directory in item.Directories.OrderBy(d=>d.Length)) System.IO.Directory.CreateDirectory(Safety.Under(destination,directory));
            }
            foreach (OutfitSetFile file in item.Files)
            {
                string from=item.Directory ? Safety.Under(source,file.Path) : source, to=item.Directory ? Safety.Under(destination,file.Path) : destination;
                Safety.NoReparseChain(from); Safety.NoReparseChain(to);
                using (var input=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.Read))
                using (var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    if (input.Length!=file.Length || !String.Equals(Hash(input,output),file.Sha256,StringComparison.OrdinalIgnoreCase)) throw new IOException("An outfit file changed while copying: "+from);
                File.SetLastWriteTimeUtc(to,File.GetLastWriteTimeUtc(from));
            }
            // The stream hash covered every byte written above. Verify shape and
            // lengths without rereading all staged archive bytes a second time.
            if (!Matches(Inventory(destination,item.Directory,false),item,false)) throw new IOException("The prepared outfit failed verification.");
        }
        private static void MoveEntry(string from,string to,bool directory)
        {
            Safety.NoReparseChain(from); Safety.NoReparseChain(to);
            if (Exists(to)) throw new IOException("Destination already exists; no outfit was overwritten: "+to);
            if (directory) { Installer.MoveDirectory(from,to); return; }
            // The same short scanner locks affect opaque archive files as the
            // directory moves handled by the installer lifecycle helper.
            for (int attempt=0;;attempt++)
            {
                try { File.Move(from,to); return; }
                catch (IOException ex) { int code=ex.HResult & 0xFFFF; if (attempt>=4 || (code!=5 && code!=32 && code!=33)) throw; }
                catch (UnauthorizedAccessException) { if (attempt>=4) throw; }
                System.Threading.Thread.Sleep(100*(1<<attempt));
                Safety.NoReparseChain(from); Safety.NoReparseChain(to);
            }
        }
        private static string Rollback(List<Move> moves)
        {
            var errors=new List<string>();
            for (int i=moves.Count-1;i>=0;i--)
                try { MoveEntry(moves[i].To,moves[i].From,moves[i].Directory); }
                catch (Exception ex) { if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw; errors.Add(ex.Message); }
            return String.Join("; ",errors);
        }
        private static void Cleanup(string path,string container)
        {
            try { Safety.DeleteGenerated(path,container); }
            catch (IOException ex) { Program.Log(ex); }
            catch (UnauthorizedAccessException ex) { Program.Log(ex); }
        }
        private static string Root(string costumeRoot)
        {
            string root=System.IO.Path.GetFullPath(costumeRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!System.IO.Path.GetFileName(root).Equals("costume_mods",StringComparison.OrdinalIgnoreCase)) throw new IOException("The destination must be the Slotsloader costume_mods folder.");
            Safety.NoReparseChain(root); return root;
        }
        private static string Container(string root,OutfitSetKind kind)
        {
            if (kind!=OutfitSetKind.Preset && kind!=OutfitSetKind.Removed) throw new IOException("Unknown saved-set area.");
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root),kind==OutfitSetKind.Preset ? ".slotsloader-installer-presets" : ".slotsloader-installer-removed-sets");
        }
        private static string Staging(string root) { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root),".slotsloader-installer-set-staging"); }
        private static string NewStage(string root)
        {
            string container=Staging(root); Safety.NoReparseChain(container); System.IO.Directory.CreateDirectory(container);
            string stage=Safety.Under(container,Guid.NewGuid().ToString("N")); Safety.NoReparseChain(stage);
            if (Exists(stage)) throw new IOException("The generated staging path already exists.");
            System.IO.Directory.CreateDirectory(stage); return stage;
        }
        private static string Payload(string folder,OutfitSetItem item) { return Safety.Under(System.IO.Path.Combine(folder,"payload"),item.Storage); }
        private static bool Exists(string path) { return File.Exists(path) || System.IO.Directory.Exists(path); }
        private static bool GuidName(string value) { return Regex.IsMatch(value ?? "",@"^[0-9a-f]{32}$"); }
        // Default name for a selection removed from the Installed page. Set names
        // follow folder-name rules, so the time uses a dot rather than a colon.
        internal static string RemovedSetName(DateTime when) { return "Removed " + when.ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture); }

        private static string SetName(string name)
        {
            name=(name ?? "").Trim(); Safety.Segment(name); if (name.Length>100) throw new IOException("Use a name of 100 characters or fewer."); return name;
        }
    }
}
