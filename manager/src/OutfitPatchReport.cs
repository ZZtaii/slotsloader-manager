using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // Checks the selected winners or the installed copies. The fixers only receive private buffers.
    internal sealed class OutfitPatchReport
    {
        internal sealed class Patch
        {
            internal readonly string Kind, Path, Description;
            internal readonly int Count;
            internal Patch(string kind, string path, string description, int count)
            {
                Kind = kind; Path = path; Description = description; Count = count;
            }
        }

        internal readonly List<Patch> Patches = new List<Patch>();
        internal readonly List<string> Warnings = new List<string>(), Errors = new List<string>();
        internal readonly List<string> Unchanged = new List<string>(), Other = new List<string>(), Applied = new List<string>();
        internal string Integrity;

        private static bool Candidate(string path)
        {
            string p = path.ToLowerInvariant();
            return p.Contains(".tex.") || p.Contains(".user.") || p.EndsWith(".scn.20");
        }

        private GameReference Reference(string game, IEnumerable<string> paths)
        {
            var paks = GamePaks.Load(game);
            var reference = GameReference.Load(game, paks.Base, paks.Dlc);
            if (reference.CurrentCrcs.Count == 0 && paths.Any(p => p.IndexOf(".user.", StringComparison.OrdinalIgnoreCase) >= 0))
                Warnings.Add("Colour signature checks are unavailable. The current game reference could not be read.");
            return reference;
        }

        internal static OutfitPatchReport Preview(CompositionPlan plan, string game, string sevenZip)
        {
            var report = new OutfitPatchReport();
            var files = new List<PlannedFile>();
            foreach (var file in plan.Files.Values)
            {
                string path = file.Asset.DestinationPath;
                if (!Candidate(path)) report.Other.Add(path);
                else if (file.Asset.Size > 256L * 1024 * 1024) report.Warnings.Add(path + " · Exceeds the 256 MB compatibility check limit.");
                else files.Add(file);
            }
            if (files.Count == 0) return report;
            string root = Path.GetTempPath(), stage = Safety.NewFolder(root, "slm-health-");
            try
            {
                var reference = report.Reference(game, files.Select(f => f.Asset.DestinationPath));
                var staged = files.Select((f, i) => new PlannedFile { Component = f.Component, Asset = new NativeAsset {
                    EntryPath = f.Asset.EntryPath, Size = f.Asset.Size, DestinationPath = i.ToString() } }).ToList();
                using (var reader = new SourceReader(plan.Package.SourcePath, sevenZip, plan.Package.PreviewEntries, plan.Package.PreviewSourceStamp))
                    reader.CopyFiles(staged, stage, stage);
                for (int i = 0; i < files.Count; i++)
                    report.Inspect(files[i].Asset.DestinationPath, Path.Combine(stage, staged[i].Asset.DestinationPath), reference);
            }
            catch (InvalidDataException ex) { report.Errors.Add("Compatibility check incomplete. " + ex.Message); }
            catch (IOException ex) { report.Errors.Add("Compatibility check incomplete. " + ex.Message); }
            catch (UnauthorizedAccessException ex) { report.Errors.Add("Compatibility check incomplete. " + ex.Message); }
            finally { Safety.DeleteGenerated(stage, root); }
            return report;
        }

        internal static OutfitPatchReport Installed(InstalledOutfit outfit, InstallManifest manifest, string game)
        {
            var report = new OutfitPatchReport();
            var paths = manifest.Sha256.Keys.ToList();
            report.Other.AddRange(paths.Where(p => !Candidate(p)));
            var candidates = paths.Where(Candidate).ToList();
            if (candidates.Count == 0) return report;
            try
            {
                var reference = report.Reference(game, candidates);
                foreach (string path in candidates)
                {
                    string file = Safety.Under(outfit.Folder, path);
                    if (File.Exists(file)) report.Inspect(path, file, reference);
                }
            }
            catch (InvalidDataException ex) { report.Errors.Add("Compatibility check incomplete. " + ex.Message); }
            catch (IOException ex) { report.Errors.Add("Compatibility check incomplete. " + ex.Message); }
            catch (UnauthorizedAccessException ex) { report.Errors.Add("Compatibility check incomplete. " + ex.Message); }
            return report;
        }

        private static string PatchReason(FixResult fix)
        {
            switch (fix.Kind)
            {
                case "crc": return "Uses " + ReplacePlanner.N(fix.Count, "outdated colour signature") + ".";
                case "layout": return "Uses the 2023 colour-file layout.";
                case "mip": return "Has an incorrect row pitch in " + ReplacePlanner.N(fix.Count, "texture level") + ".";
                case "swatch": return "Uses outdated select-screen swatches. The rebuild preserves " + ReplacePlanner.N(fix.Count, "colour") + ".";
                case "swap-scene": return "Uses an older character-swap scene. The patch preserves its body references.";
                case "swap-user": return "Needs the current game resource layout. The patch preserves the swap's target costume.";
                default: return fix.Message;
            }
        }

        private void Inspect(string path, string file, GameReference reference)
        {
            OperationContext.ThrowIfCancellationRequested();
            try
            {
                Safety.NoReparseChain(file);
                string suffix = ModFixes.UpgradeTexSuffix(path, reference.TexVersion);
                if (suffix != null) Patches.Add(new Patch("suffix", path, "Texture filename → " + Path.GetFileName(suffix), 1));
                List<FixResult> fixes;
                if (!(path.IndexOf(".tex.", StringComparison.OrdinalIgnoreCase) >= 0 && ModFixes.TextureFileOk(file, out fixes)))
                {
                    if (new FileInfo(file).Length > 256L * 1024 * 1024) { Warnings.Add(path + " · Exceeds the 256 MB compatibility check limit."); return; }
                    byte[] bytes = File.ReadAllBytes(file);
                    string magic = path.IndexOf(".tex.", StringComparison.OrdinalIgnoreCase) >= 0 ? "TEX" :
                        path.EndsWith(".scn.20", StringComparison.OrdinalIgnoreCase) ? "SCN" : "USR";
                    if (bytes.Length < 4 || bytes[0] != magic[0] || bytes[1] != magic[1] || bytes[2] != magic[2] || bytes[3] != 0)
                    {
                        Warnings.Add(path + " · The file header is unrecognised. Automatic repair is unavailable.");
                        return;
                    }
                    fixes = ModFixes.UpgradeModFile(ref bytes, reference, path);
                }
                foreach (var fix in fixes)
                {
                    if (fix.Changed) Patches.Add(new Patch(fix.Kind, path, PatchReason(fix), fix.Count));
                    if (fix.Warning) Warnings.Add(path + " · " + fix.Message);
                }
                if (suffix == null && fixes.Count == 0) Unchanged.Add(path);
            }
            catch (IOException ex) { Errors.Add(path + " · " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Errors.Add(path + " · " + ex.Message); }
        }
    }
}
