using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    public sealed class NativeAsset { public string EntryPath, DestinationPath; public long Size; }
    public sealed class ModComponent
    {
        public string Id, Name, Description, Author, Version, AddonFor, ParentId, PreviewEntry;
        public bool IsDummy;
        public Dictionary<string,string> Metadata = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public List<NativeAsset> Assets = new List<NativeAsset>();
        // Files outside natives that Fluffy copies to the game root (reframework\..., dlls).
        // Only Replace-original installs use them; DestinationPath is game-root relative.
        public List<NativeAsset> RootAssets = new List<NativeAsset>();
        internal bool HasFiles(bool includeRoot) { return Assets.Count > 0 || (includeRoot && RootAssets.Count > 0); }
        public bool IsChoiceGroup { get { return IsDummy && Regex.IsMatch(Description ?? "", @"(select|choose|pick)\s+(only\s+)?one", RegexOptions.IgnoreCase); } }
    }
    public sealed class ModPackage
    {
        public string SourcePath, Name, Character;
        public List<string> CharacterCodes = new List<string>();
        public List<ModComponent> Components = new List<ModComponent>();
        public List<string> Warnings = new List<string>();
        internal int RootFilesIgnored;
        internal readonly Dictionary<string,byte[]> PreloadedPreviews = new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string,SourceEntry> PreviewEntries;
        internal string PreviewSourceStamp;
        private readonly object previewLock = new object();
        private sealed class ComponentIndex
        {
            internal List<ModComponent> Source;
            internal int Count;
            internal readonly Dictionary<string,ModComponent> ById = new Dictionary<string,ModComponent>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string,List<ModComponent>> Children = new Dictionary<string,List<ModComponent>>(StringComparer.OrdinalIgnoreCase);
            internal ModComponent FirstPreview;
        }
        private ComponentIndex componentIndex;
        private Dictionary<string,HashSet<string>> choiceGroups = new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // Components are built before publication. Rebuild at composition boundaries too,
        // since the public model is also used by fixture callers that edit its fields.
        internal void RebuildIndexes()
        {
            var index = new ComponentIndex { Source=Components,Count=Components.Count };
            foreach (ModComponent component in Components)
            {
                if (!index.ById.ContainsKey(component.Id)) index.ById.Add(component.Id,component);
                if (component.ParentId != null)
                {
                    List<ModComponent> children;
                    if (!index.Children.TryGetValue(component.ParentId,out children))
                        index.Children[component.ParentId] = children = new List<ModComponent>();
                    children.Add(component);
                }
                if (index.FirstPreview == null && !String.IsNullOrEmpty(component.PreviewEntry)) index.FirstPreview = component;
            }
            componentIndex = index;
        }
        private ComponentIndex Index()
        {
            ComponentIndex index = componentIndex;
            if (index == null || !Object.ReferenceEquals(index.Source,Components) || index.Count != Components.Count)
            {
                RebuildIndexes();
                index = componentIndex;
            }
            return index;
        }
        internal void PrepareChoices()
        {
            RebuildIndexes();
            ComponentIndex index = componentIndex;
            var choices = new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModComponent group in Components.Where(c => c.IsDummy))
            {
                List<ModComponent> directChildren;
                if (!index.Children.TryGetValue(group.Id,out directChildren)) directChildren = new List<ModComponent>();
                if (group.IsChoiceGroup)
                    choices[group.Id] = new HashSet<string>(directChildren.Select(c=>c.Id),StringComparer.OrdinalIgnoreCase);
                else
                {
                    // A shared file between main files and an overlay is expected.
                    // Only peer variants participate in inferred exclusivity.
                    var children = directChildren.Where(c => !c.IsDummy && c.Assets.Count > 0 && !Composer.IsBase(c)).ToList();
                    if (children.Count > 1)
                    {
                        var common = new HashSet<string>(children[0].Assets.Select(a => a.DestinationPath),StringComparer.OrdinalIgnoreCase);
                        foreach (var child in children.Skip(1)) common.IntersectWith(child.Assets.Select(a => a.DestinationPath));
                        if (common.Count > 0) choices[group.Id] = new HashSet<string>(children.Select(c=>c.Id),StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            choiceGroups = choices;
        }
        internal bool IsChoice(ModComponent component)
        {
            return component != null && component.IsDummy && choiceGroups.ContainsKey(component.Id);
        }
        internal bool IsChoiceBranch(ModComponent group,ModComponent child)
        {
            HashSet<string> branches;
            return group != null && child != null && choiceGroups.TryGetValue(group.Id,out branches) && branches.Contains(child.Id);
        }
        public ModComponent Find(string id)
        {
            ModComponent component;
            return id != null && Index().ById.TryGetValue(id,out component) ? component : null;
        }
        public byte[] PreviewBytes(string componentId, string sevenZipPath)
        {
            lock (previewLock)
            {
            ModComponent c = Find(componentId);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (c != null && visited.Add(c.Id))
            {
                if (!String.IsNullOrEmpty(c.PreviewEntry))
                {
                    return ReadPreview(c.PreviewEntry,sevenZipPath);
                }
                c = c.ParentId == null ? null : Find(c.ParentId);
            }
            c = Index().FirstPreview;
            if (c == null) return null;
            return ReadPreview(c.PreviewEntry,sevenZipPath);
            }
        }
        private byte[] ReadPreview(string entry,string sevenZipPath)
        {
            byte[] bytes;
            if (PreloadedPreviews.TryGetValue(entry,out bytes)) return bytes;
            using (var reader = new SourceReader(SourcePath,sevenZipPath,PreviewEntries,PreviewSourceStamp)) bytes = reader.ReadBytes(entry,25*1024*1024);
            // Keep the package cache within the original preload budget; larger
            // individual images can still use the UI's separate bounded cache.
            if (bytes.Length > 16*1024*1024) return bytes;
            while (PreloadedPreviews.Count > 0 && PreloadedPreviews.Values.Sum(b => (long)b.Length) + bytes.Length > 16L*1024*1024)
                PreloadedPreviews.Remove(PreloadedPreviews.Keys.First());
            PreloadedPreviews[entry] = bytes;
            return bytes;
        }
    }
}
