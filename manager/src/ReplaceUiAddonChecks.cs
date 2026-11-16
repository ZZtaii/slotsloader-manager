using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.16: addon archives shown inside their main mod (LibraryAddons.cs, MainFormAddons.cs). Synthetic zips laid out
    // like the user's "Sakura as Ryu C1" + "Sakura as Ryu C1 Addon" (Fluffy modinfo.ini per folder, the addon's Barefoot
    // naming the main mod's Outfit group), for Cammy's Outfit 2 of the fixture game.
    internal static partial class ReplaceUiChecks
    {
        private const string AddonTestName = "Cammy Addon Test";
        private static readonly byte[] AddonPreview = Encoding.ASCII.GetBytes("barefoot preview bytes");

        private static void WriteZip(string zip, Dictionary<string, byte[]> files)
        {
            string staging = zip + ".staging"; if (Directory.Exists(staging)) Directory.Delete(staging, true);
            foreach (var file in files) ReplaceChecks.Put(staging, file.Key, file.Value);
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(staging, zip);
            Directory.Delete(staging, true);
        }
        private static byte[] Ini(string text) { return Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")); }
        private const string AddonModel = "natives/stm/product/model/esf/esf009/002/01/";

        internal static void WriteAddonZips(string folder, out string main, out string addon)
        {
            Directory.CreateDirectory(folder);
            main = Path.Combine(folder, AddonTestName + ".zip"); addon = Path.Combine(folder, AddonTestName + " Addon.zip");
            WriteZip(main, new Dictionary<string, byte[]> {
                { "00 Menu/modinfo.ini", Ini("name=" + AddonTestName + "\nDummyMod=true\ndescription=Fixture main mod\n") },
                { "01 Main files/modinfo.ini", Ini("name=Main files\nAddonFor=" + AddonTestName + "\nMenuPriority=400\n") },
                { "01 Main files/" + AddonModel + "addon_main.tex.241101895", Encoding.ASCII.GetBytes("main files texture") },
                { "02 Variants/modinfo.ini", Ini("name=Outfit\nDummyMod=true\ndescription=Choose one outfit.\nAddonFor=" + AddonTestName + "\nMenuPriority=200\n") },
                { "03 Full/modinfo.ini", Ini("name=Full\nAddonFor=Outfit\nRequirement=Main files\nMenuPriority=200\n") },
                { "03 Full/" + ReplaceChecks.V5, Encoding.ASCII.GetBytes("full mesh") },
                { "03b Shorts/modinfo.ini", Ini("name=Shorts\nAddonFor=Outfit\nMenuPriority=150\n") },
                { "03b Shorts/" + ReplaceChecks.V5, Encoding.ASCII.GetBytes("shorts mesh") },
                { "03b Shorts/" + AddonModel + "shorts.tex.241101895", Encoding.ASCII.GetBytes("shorts texture") },
                { "05 Hair styles/modinfo.ini", Ini("name=Hair styles\nDummyMod=true\ndescription=Choose one hairstyle.\nAddonFor=" + AddonTestName + "\nMenuPriority=300\n") },
                { "06 Short/modinfo.ini", Ini("name=Short\nAddonFor=Hair styles\n") },
                { "06 Short/" + AddonModel + "hair_short.tex.241101895", Encoding.ASCII.GetBytes("short hair") },
                { "07 Long/modinfo.ini", Ini("name=Long\nAddonFor=Hair styles\n") },
                { "07 Long/" + AddonModel + "hair_long.tex.241101895", Encoding.ASCII.GetBytes("long hair") },
                { "13 Misc/modinfo.ini", Ini("name=Misc\nDummyMod=true\ndescription=Optional extras.\nAddonFor=" + AddonTestName + "\nMenuPriority=100\n") },
                { "09 Portrait/modinfo.ini", Ini("name=Portrait\nAddonFor=Misc\n") },
                { "09 Portrait/" + AddonModel + "portrait.tex.241101895", Encoding.ASCII.GetBytes("portrait") } });
            WriteZip(addon, new Dictionary<string, byte[]> {
                { "00 Menu/modinfo.ini", Ini("name=" + AddonTestName + "\nDummyMod=true\ndescription=Fixture main mod\n") },
                { "04 Barefoot/modinfo.ini", Ini("name=Barefoot\nAddonFor=Outfit\nRequirement=Main files\nscreenshot=preview.png\nMenuPriority=100\n") },
                { "04 Barefoot/preview.png", AddonPreview },
                { "04 Barefoot/" + ReplaceChecks.V5, Encoding.ASCII.GetBytes("barefoot mesh") } });
        }

        // The package-level rules: detection both ways, the bundle-layout guard, the fill-in pick and its leftover note.
        private static void AddonRules(string main, string addon)
        {
            ModPackage m = PackageReader.Load(main, ""), a = PackageReader.Load(addon, "");
            Require(a.Warnings.Any(w => w.StartsWith("Cannot uniquely resolve group 'Outfit'")) && a.Components.Single(c => c.Name == "Barefoot").ParentId == null, "fixture: the addon alone resolves Barefoot");
            Require(LibraryAddons.IsAddonOf(m, a) && !LibraryAddons.IsAddonOf(a, m), "detection: the addon of the main mod, never the other way round");
            ModPackage bundled = PackageReader.Load(main, ""); bundled.HasBundleLayout = true;
            ModPackage bundledAddon = PackageReader.Load(addon, ""); bundledAddon.HasBundleLayout = true;
            Require(!LibraryAddons.IsAddonOf(bundled, a) && !LibraryAddons.IsAddonOf(m, bundledAddon), "a package with a saved bundle layout was merged");
            ModPackage renamed = PackageReader.Load(addon, ""); renamed.Name = "Another mod";
            Require(!LibraryAddons.IsAddonOf(m, renamed), "an addon with another mod name was merged");
            ModPackage merged = LibraryAddons.Merge(m, new List<ModPackage> { a });
            Require(m.Components.All(c => !c.Id.StartsWith(LibraryAddons.PrefixHead)) && m.MergedFrom == null && a.Components.Single(c => c.Name == "Barefoot").ParentId == null, "merging changed the cached packages");
            ModComponent outfit = merged.Components.Single(c => c.Name == "Outfit"), full = merged.Components.Single(c => c.Name == "Full"), bare = merged.Components.Single(c => c.Name == "Barefoot");
            Require(bare.ParentId == outfit.Id && merged.IsChoiceBranch(outfit, bare) && merged.IsChoiceBranch(outfit, full) && !merged.Components.Any(c => c.Id.StartsWith(LibraryAddons.PrefixHead) && c.IsDummy), "merged tree: Barefoot " + bare.ParentId);
            string mainFiles = merged.Components.Single(c => c.Name == "Main files").Id, shortHair = merged.Components.Single(c => c.Name == "Short").Id;
            AddonSplit split = LibraryAddons.Split(merged, new[] { mainFiles, bare.Id, shortHair }, true);
            Require(String.Join("|", split.MainIds) == mainFiles + "|" + full.Id + "|" + shortHair && split.Parts.Single().Value.SequenceEqual(new[] { "04 Barefoot" }) && split.Warnings.Count == 0 && split.Notes.Single() == "Also installs Barefoot from " + Path.GetFileName(addon) + " on top.",
                "split: main " + String.Join("|", split.MainIds) + " addon " + String.Join("|", split.Parts.Single().Value) + " warnings " + String.Join(" / ", split.Warnings));
            Require(Composer.Plan(m, split.MainIds, true).CanInstall && Composer.Plan(a, split.Parts.Single().Value, true).CanInstall, "a split record is not valid alone");
            Require(Composer.Plan(merged, new[] { mainFiles, full.Id, bare.Id, shortHair }, true).Errors.Contains("Choose one option in 'Outfit'."), "two picks in Outfit were not an error");
            List<string> restored = LibraryAddons.Restore(merged, new List<string> { mainFiles, full.Id, shortHair }, new[] { new KeyValuePair<AddonPart, List<string>>(merged.AddonParts[0], new List<string> { "04 Barefoot" }) });
            Require(restored.Contains(bare.Id) && !restored.Contains(full.Id) && restored.Contains(shortHair), "restore: " + String.Join("|", restored));
            // Only Shorts to stand in: it is the fill-in and its texture Barefoot doesn't replace is named.
            ModPackage shortsOnly = PackageReader.Load(main, ""); shortsOnly.Components.RemoveAll(c => c.Name == "Full"); shortsOnly.RebuildIndexes(); shortsOnly.PrepareChoices();
            ModPackage merged2 = LibraryAddons.Merge(shortsOnly, new List<ModPackage> { a });
            AddonSplit split2 = LibraryAddons.Split(merged2, new[] { mainFiles, merged2.Components.Single(c => c.Name == "Barefoot").Id }, false);
            Require(split2.MainIds.Contains(merged2.Components.Single(c => c.Name == "Shorts").Id) && split2.Warnings.Count == 1 && split2.Warnings[0].Contains("shorts.tex.241101895") && split2.Warnings[0].Contains("Barefoot"), "leftover note: " + String.Join(" / ", split2.Warnings));
        }

        private static void AddonArchives(MainForm form, ReplaceChecks.Fixture f, string folder)
        {
            string main, addon; WriteAddonZips(folder, out main, out addon);
            AddonRules(main, addon);
            ReplaceEngine engine = FormEngine(f);
            List<LibraryEntry> library = Get<List<LibraryEntry>>(form, "library");
            // Rows as the library index has them (mod name and option names from the modinfo.ini files).
            var mainEntry = new LibraryEntry { SourcePath = main, Name = Path.GetFileNameWithoutExtension(main), DisplayName = AddonTestName, Kind = "ZIP", OptionNames = new List<string> { AddonTestName, "Main files", "Outfit", "Full", "Shorts", "Hair styles", "Short", "Long", "Misc", "Portrait" } };
            var addonEntry = new LibraryEntry { SourcePath = addon, Name = Path.GetFileNameWithoutExtension(addon), DisplayName = AddonTestName, Kind = "ZIP", OptionNames = new List<string> { AddonTestName, "Barefoot" } };
            // Another copy of the main mod (the same names): never read to look for addons.
            string copy = Path.Combine(folder, AddonTestName + " (copy).zip"); File.Copy(main, copy, true);
            var copyEntry = new LibraryEntry { SourcePath = copy, Name = Path.GetFileNameWithoutExtension(copy), DisplayName = AddonTestName, Kind = "ZIP", OptionNames = mainEntry.OptionNames.ToList() };
            Func<string, bool> isMain = p => ReplacePlanner.SameSource(p, main);
            Func<string, bool> isAddon = p => ReplacePlanner.SameSource(p, addon);
            Func<string, OptionNode> node = name => Get<Dictionary<string, OptionNode>>(form, "componentNodes").Values.First(n => ((ModComponent)n.Tag).Name == name);
            Func<string, bool> ticked = name => node(name).Checked;
            Action<LibraryEntry> open = e => { ShowLibrary(form); Call(form, "LoadPackage", e); WaitBusyOnly(form); WaitReview(form); };
            Func<string> state = () => Summary(form) + " / " + Details(form) + " / " + Get<Label>(form, "statusText").Text + " / ticks " + Selection(form);
            string root = Get<AppConfig>(form, "config").CostumeRoot;
            Action cleanup = delegate
            {
                foreach (ReplaceMod m in engine.LoadState().Mods.Where(m => isMain(m.SourcePath) || isAddon(m.SourcePath) || m.Name == "Unrelated above").ToList()) engine.UninstallForGood(m.Id, DriftChoice.Refuse, null);
                List<InstalledOutfit> mine = Installer.ListInstalled(root).Where(o => o.Managed && isMain(o.SourcePath)).ToList();
                if (mine.Count > 0) OutfitSetStore.UninstallOutfits(root, mine, null);
            };
            library.Add(mainEntry); library.Add(addonEntry); library.Add(copyEntry);
            try
            {
                cleanup();
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                // Opening the main mod reads it and its addon, not the copy of the main mod (the same option names).
                int reads = form.PackageReads;
                open(mainEntry);
                Require(form.PackageReads == reads + 2 && Get<ModPackage>(form, "package").MergedFrom != null, "reads while opening the main mod: +" + (form.PackageReads - reads));
                // The addon opened alone is listed as before: Barefoot a root option with the note.
                open(addonEntry);
                ModPackage alone = Get<ModPackage>(form, "package");
                Require(alone.MergedFrom == null && node("Barefoot").Parent == null && alone.Warnings.Any(w => w.StartsWith("Cannot uniquely resolve group 'Outfit'")), "the addon alone changed: merged " + (alone.MergedFrom != null));
                // The main mod again (both archives kept): Barefoot inside Outfit next to Full (choose one); its preview from
                // the addon archive.
                reads = form.PackageReads;
                open(mainEntry);
                Require(form.PackageReads == reads, "a kept archive was read again: +" + (form.PackageReads - reads));
                ModPackage merged = Get<ModPackage>(form, "package");
                Require(merged.MergedFrom != null && merged.AddonParts.Count == 1 && isAddon(merged.AddonParts[0].SourcePath), "the main mod did not take in its addon");
                ModComponent bare = merged.Components.Single(c => c.Name == "Barefoot"), full = merged.Components.Single(c => c.Name == "Full"), outfit = merged.Components.Single(c => c.Name == "Outfit");
                Require(node("Barefoot").Parent == node("Outfit") && merged.IsChoiceBranch(outfit, bare) && merged.IsChoiceBranch(outfit, full), "Barefoot is not a choice of Outfit in the tree");
                Require(merged.PreviewBytes(bare.Id, "").SequenceEqual(AddonPreview), "Barefoot's preview was not read from the addon");
                Require(!mainEntry.OptionIds.Any(id => id.StartsWith(LibraryAddons.PrefixHead)), "the main row lists the addon's options: " + String.Join(",", mainEntry.OptionIds));
                Require(Get<Label>(form, "packageHint").Text.StartsWith("With options from " + Path.GetFileName(addon)), "no hint about the addon: " + Get<Label>(form, "packageHint").Text);
                // 1.0.19 (the user: "relax requirements ... install the mod anyways. this helps to mix and match options"):
                // Main files unticked by hand stays out when Barefoot (Requirement=Main files) is ticked; the line under
                // the preview picture names it and its row lights up. Ticked again by hand, it is back for good.
                node("Full").Checked = true; node("Main files").Checked = false; node("Barefoot").Checked = true;
                Require(ticked("Barefoot") && !ticked("Full") && !ticked("Main files") && form.NeedNotice.Text.StartsWith("May not work without “Main files”") && form.CurrentNeeds.Any(n => n.Needed.Name == "Main files"),
                    "Main files unticked by hand did not stay out with a note: " + form.NeedNotice.Text + " / " + state());
                // 13 Nov 2026 (the user: "highlights should never happen on mod hovers, only on mod installs if there are
                // possibly some pre-requisites missing"): the ticks lit nothing up. Install lights the row of what is left
                // out, once, and installs all the same.
                string mainFilesId = ((ModComponent)node("Main files").Tag).Id;
                Func<bool> litAtInstall = delegate
                {
                    DateTime until = DateTime.UtcNow.AddSeconds(20);
                    while (!form.IsFlashing(mainFilesId) && form.Working && DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                    return form.IsFlashing(mainFilesId);
                };
                Require(!form.IsFlashing(mainFilesId), "a tick lit the row of the option left out");
                WaitReview(form);
                Require(Get<FlatButton>(form, "installButton").Enabled && form.CurrentNeeds.Any(n => n.Needed.Id == mainFilesId) && !form.IsFlashing(mainFilesId), "Install is off with Main files left out, or the review lit its row: " + state());
                Call(form, "InstallCurrent");
                Require(litAtInstall(), "Install with Main files left out did not light its row: " + state());
                WaitIdle(form); WaitReview(form);
                Require(engine.LoadState().Mods.Any(m => isMain(m.SourcePath) && !m.SelectedIds.Contains(mainFilesId)), "the install with Main files left out did not go ahead: " + state());
                cleanup(); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); WaitReview(form);
                // Barefoot unticked Full (choose one); Main files goes back in by hand.
                node("Main files").Checked = true; node("Short").Checked = true; WaitReview(form);
                Require(form.NeedNotice.Text == "", "the line stayed with Main files ticked: " + form.NeedNotice.Text);
                Require(ticked("Barefoot") && !ticked("Full") && ticked("Main files") && ticked("Short"), "ticks after Barefoot: " + state());
                ReplacePlan health = Get<ReplacePlan>(form, "replacePlan");
                Require(health != null && health.CanInstall && health.Items.Any(i => i.Text == "Also installs Barefoot from " + Path.GetFileName(addon) + " on top.") && Get<FlatButton>(form, "installButton").Enabled, "health: " + state() + (health == null ? "" : " / " + String.Join(" / ", health.Items.Select(i => i.Text))));
                // Install: two records, the addon directly above the main one; the main record is valid alone with Full.
                Call(form, "InstallCurrent"); WaitIdle(form); WaitReview(form);
                List<ReplaceMod> mods = engine.LoadState().Mods;
                ReplaceMod mainMod = mods.Single(m => isMain(m.SourcePath)), addonMod = mods.Single(m => isAddon(m.SourcePath));
                Require(mods.IndexOf(addonMod) == mods.IndexOf(mainMod) - 1 && addonMod.Name == AddonTestName + " Addon" && addonMod.SelectedIds.SequenceEqual(new[] { "04 Barefoot" }) && mainMod.SelectedIds.Contains(full.Id) && !mainMod.SelectedIds.Any(id => id.StartsWith(LibraryAddons.PrefixHead)),
                    "records: " + String.Join(" / ", mods.Select(m => m.Name + " [" + String.Join(",", m.SelectedIds) + "]")) + " / " + state());
                Require(Composer.Plan(PackageReader.Load(main, ""), mainMod.SelectedIds, true).CanInstall, "the main record is not valid alone");
                Require(engine.LoadState().Deployment(ReplaceChecks.V5).Owner == addonMod.Id, "Barefoot's mesh does not win in game");
                Require(Get<Label>(form, "statusText").Text.Contains(AddonTestName + " Addon (Barefoot)"), "status: " + state());
                // Another mod moved between them: Install puts the addon back directly above the main record.
                ModPackage other = PackageReader.Load(f.Mod("modB"), ""); ReplacePlan otherPlan;
                ReplacePlanner.PrepareAndInstall(Composer.Plan(other, Composer.DefaultSelection(other, true), true), "", engine, "Unrelated above", false, null, out otherPlan);
                engine.Move(addonMod.Id, -1);
                mods = engine.LoadState().Mods;
                Require(mods.FindIndex(m => m.Id == addonMod.Id) == 0 && mods[1].Name == "Unrelated above", "fixture order: " + String.Join(" / ", mods.Select(m => m.Name)));
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                // Reopening restores Barefoot ticked and Full unticked; the main mod's own addon is no conflict.
                open(addonEntry); open(mainEntry);
                Require(ticked("Barefoot") && !ticked("Full") && ticked("Short") && ticked("Main files"), "reopen: " + state());
                health = Get<ReplacePlan>(form, "replacePlan");
                Require(health != null && !health.ConflictList.Any(c => c.ModId == addonMod.Id), "the addon's record is reported as a conflict: " + (health == null ? state() : String.Join(" / ", health.ConflictList.Select(c => c.Text))));
                Call(form, "InstallCurrent"); WaitIdle(form); WaitReview(form);
                mods = engine.LoadState().Mods; mainMod = mods.Single(m => isMain(m.SourcePath)); addonMod = mods.Single(m => isAddon(m.SourcePath));
                Require(mods.IndexOf(addonMod) == mods.IndexOf(mainMod) - 1, "the addon is not directly above the main record after a reinstall: " + String.Join(" / ", mods.Select(m => m.Name)));
                foreach (ReplaceMod m in mods.Where(m => m.Name == "Unrelated above").ToList()) engine.UninstallForGood(m.Id, DriftChoice.Refuse, null);
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form); WaitReview(form);
                // Full instead of Barefoot: Install takes the addon's record out.
                node("Full").Checked = true; WaitReview(form);
                health = Get<ReplacePlan>(form, "replacePlan");
                Require(!ticked("Barefoot") && health != null && health.Items.Any(i => i.Text.StartsWith("Uninstalls " + AddonTestName + " Addon")), "untick note: " + state());
                Call(form, "InstallCurrent"); WaitIdle(form); WaitReview(form);
                mods = engine.LoadState().Mods;
                Require(!mods.Any(m => isAddon(m.SourcePath)) && mods.Single(m => isMain(m.SourcePath)).SelectedIds.Contains(full.Id) && engine.LoadState().Deployment(ReplaceChecks.V5).Owner == mods.Single(m => isMain(m.SourcePath)).Id, "the addon record stayed: " + String.Join(" / ", mods.Select(m => m.Name)));
                // Barefoot again, then the footer's Uninstall removes both records.
                node("Barefoot").Checked = true; WaitReview(form); Call(form, "InstallCurrent"); WaitIdle(form); WaitReview(form);
                Require(engine.LoadState().Mods.Count(m => isMain(m.SourcePath) || isAddon(m.SourcePath)) == 2, "reinstall with Barefoot");
                FlatButton uninstall = Get<FlatButton>(form, "libraryUninstallButton");
                Require(uninstall.Visible && uninstall.Enabled, "footer Uninstall");
                Call(form, "UninstallLibraryOutfit"); WaitIdle(form); WaitReview(form);
                Require(!engine.LoadState().Mods.Any(m => isMain(m.SourcePath) || isAddon(m.SourcePath)), "the footer Uninstall left a record: " + String.Join(" / ", engine.LoadState().Mods.Select(m => m.Name)));
                // Extra outfit: the main mod is the outfit, Barefoot an added part of it; unticked, the part goes.
                Call(form, "SetInstallMode", InstallModes.Slot, true);
                open(mainEntry);
                node("Barefoot").Checked = true; node("Short").Checked = true; WaitReview(form);
                Get<ComboBox>(form, "targetCharacter").SelectedItem = "Cammy"; Get<PaddedTextField>(form, "outfitName").Text = "Addon Extra"; WaitReview(form);
                Require(Get<FlatButton>(form, "installButton").Enabled && Get<CompositionPlan>(form, "plan").Warnings.Contains("Also installs Barefoot from " + Path.GetFileName(addon) + " on top."), "Extra outfit not ready: " + state());
                Call(form, "InstallCurrent"); WaitIdle(form); WaitReview(form);
                InstalledOutfit outfitNow = Installer.ListInstalled(root).Single(o => o.Name == "Addon Extra");
                string mesh = Path.Combine(outfitNow.Folder, ReplaceChecks.V5.Replace('/', '\\'));
                Require(isMain(outfitNow.SourcePath) && outfitNow.SelectedIds.Contains(full.Id) && outfitNow.Parts != null && outfitNow.Parts.Count == 1 && isAddon(outfitNow.Parts[0].SourcePath) && outfitNow.Parts[0].SelectedIds.SequenceEqual(new[] { "04 Barefoot" }) && File.ReadAllText(mesh) == "barefoot mesh",
                    "extra outfit: " + String.Join(",", outfitNow.SelectedIds) + " parts " + (outfitNow.Parts == null ? 0 : outfitNow.Parts.Count) + " mesh " + (File.Exists(mesh) ? File.ReadAllText(mesh) : "missing") + " / " + state());
                open(addonEntry); open(mainEntry);
                Require(ticked("Barefoot") && !ticked("Full") && ticked("Short"), "Extra outfit reopen: " + state());
                node("Full").Checked = true; Get<PaddedTextField>(form, "outfitName").Text = "Addon Extra"; WaitReview(form);
                Call(form, "InstallCurrent"); WaitIdle(form); WaitReview(form);
                outfitNow = Installer.ListInstalled(root).Single(o => o.Name == "Addon Extra");
                Require((outfitNow.Parts == null || outfitNow.Parts.Count == 0) && File.ReadAllText(mesh) == "full mesh" && Get<Label>(form, "statusText").Text.Contains("removed Barefoot"), "the graft stayed: parts " + (outfitNow.Parts == null ? 0 : outfitNow.Parts.Count) + " mesh " + File.ReadAllText(mesh) + " / " + state());
                // 13 Nov 2026: the same for an extra outfit. Reopened, the mod lights nothing up; Main files left out by hand
                // lights up when Install is pressed, and the outfit is installed without it.
                open(addonEntry); open(mainEntry);
                mainFilesId = ((ModComponent)node("Main files").Tag).Id;
                Require(!form.IsFlashing(mainFilesId), "opening the mod lit a row up");
                node("Main files").Checked = false; Get<PaddedTextField>(form, "outfitName").Text = "Addon Extra"; WaitReview(form);
                Require(Get<FlatButton>(form, "installButton").Enabled && form.CurrentNeeds.Any(n => n.Needed.Id == mainFilesId) && !form.IsFlashing(mainFilesId), "Extra outfit: Install is off with Main files left out, or a tick lit its row: " + state());
                Call(form, "InstallCurrent");
                Require(litAtInstall(), "Extra outfit: Install with Main files left out did not light its row: " + state());
                WaitIdle(form); WaitReview(form);
                outfitNow = Installer.ListInstalled(root).Single(o => o.Name == "Addon Extra");
                Require(!outfitNow.SelectedIds.Contains(mainFilesId) && Get<Label>(form, "statusText").Text.Contains("Installed Addon Extra"), "Extra outfit: the install with Main files left out did not go ahead: " + String.Join(",", outfitNow.SelectedIds) + " / " + state());
            }
            finally
            {
                library.Remove(mainEntry); library.Remove(addonEntry); library.Remove(copyEntry);
                try { cleanup(); } catch (Exception ex) { Program.Log(ex); }
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                ShowLibrary(form);
            }
        }

        // 1.0.16 (PR #22 review): F1 an addon record is restored ticked without a main record and when disabled, and only a
        // record shown ticked is uninstalled; F2 a changed addon archive stops the install before the main record, a late
        // failure is named in the status, the addon goes directly above the main record without a Move, and in Extra
        // outfit a part that can't be added leaves the outfit as it was; F3 addon ticks count in "the same install"
        // (both modes); L3 the main record's name shows the addon option, not the fill-in.
        private static void AddonReviewFixes(MainForm form, ReplaceChecks.Fixture f, string folder)
        {
            string main, addon; WriteAddonZips(folder, out main, out addon);
            ReplaceEngine engine = FormEngine(f);
            List<LibraryEntry> library = Get<List<LibraryEntry>>(form, "library");
            var mainEntry = new LibraryEntry { SourcePath = main, Name = Path.GetFileNameWithoutExtension(main), DisplayName = AddonTestName, Kind = "ZIP", OptionNames = new List<string> { AddonTestName, "Main files", "Outfit", "Full", "Shorts", "Hair styles", "Short", "Long", "Misc", "Portrait" } };
            var addonEntry = new LibraryEntry { SourcePath = addon, Name = Path.GetFileNameWithoutExtension(addon), DisplayName = AddonTestName, Kind = "ZIP", OptionNames = new List<string> { AddonTestName, "Barefoot" } };
            Func<string, bool> isMain = p => ReplacePlanner.SameSource(p, main), isAddon = p => ReplacePlanner.SameSource(p, addon);
            Func<string, OptionNode> node = name => Get<Dictionary<string, OptionNode>>(form, "componentNodes").Values.First(n => ((ModComponent)n.Tag).Name == name);
            Func<string, bool> ticked = name => node(name).Checked;
            Action<LibraryEntry> open = e => { ShowLibrary(form); Call(form, "LoadPackage", e); WaitBusyOnly(form); WaitReview(form); };
            Action install = () => { Call(form, "InstallCurrent"); WaitIdle(form); Application.DoEvents(); WaitIdle(form); WaitReview(form); };
            Action refresh = () => { Call(form, "RefreshInstalled", (string)null); WaitIdle(form); WaitReview(form); };
            Func<string> status = () => Get<Label>(form, "statusText").Text;
            Func<string> state = () => Summary(form) + " / " + Details(form) + " / " + status() + " / ticks " + Selection(form) + " / records " + String.Join(" / ", engine.LoadState().Mods.Select(m => m.Name + (m.Enabled ? "" : " (off)") + " [" + String.Join(",", m.SelectedIds) + "]"));
            Func<IEnumerable<string>> healthLines = () => { ReplacePlan h = Get<ReplacePlan>(form, "replacePlan"); return h == null ? new string[0] : h.Items.Select(i => i.Text).ToArray(); };
            Func<ReplaceMod> mainRecord = () => engine.LoadState().Mods.SingleOrDefault(m => isMain(m.SourcePath));
            Func<List<ReplaceMod>> addonRecords = () => engine.LoadState().Mods.Where(m => isAddon(m.SourcePath)).ToList();
            string root = Get<AppConfig>(form, "config").CostumeRoot;
            Action cleanup = delegate
            {
                foreach (ReplaceMod m in engine.LoadState().Mods.Where(m => isMain(m.SourcePath) || isAddon(m.SourcePath)).ToList()) engine.UninstallForGood(m.Id, DriftChoice.Refuse, null);
                List<InstalledOutfit> mine = Installer.ListInstalled(root).Where(o => o.Managed && isMain(o.SourcePath)).ToList();
                if (mine.Count > 0) OutfitSetStore.UninstallOutfits(root, mine, null);
            };
            library.Add(mainEntry); library.Add(addonEntry);
            try
            {
                cleanup();
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                // F1 A: the addon installed first (opened alone), no main record: the main mod opens with Barefoot ticked.
                ModPackage alone = PackageReader.Load(addon, ""); ReplacePlan alonePlan;
                ReplacePlanner.PrepareAndInstall(Composer.Plan(alone, new[] { "04 Barefoot" }, true), "", engine, "Barefoot alone", false, null, out alonePlan);
                refresh();
                open(mainEntry);
                Require(ticked("Barefoot") && !ticked("Full") && mainRecord() == null, "an addon installed first is not shown ticked: " + state());
                node("Short").Checked = true; node("Portrait").Checked = false; WaitReview(form);
                Require(!healthLines().Any(t => t.StartsWith("Uninstalls")), "the health report says the addon goes: " + String.Join(" / ", healthLines()));
                int generation = engine.LoadState().Generation;
                install();
                List<ReplaceMod> mods = engine.LoadState().Mods; ReplaceMod mainMod = mainRecord(); List<ReplaceMod> addons = addonRecords();
                Require(mainMod != null && addons.Count == 1 && addons[0].SelectedIds.SequenceEqual(new[] { "04 Barefoot" }) && mods.FindIndex(m => m.Id == addons[0].Id) == mods.FindIndex(m => m.Id == mainMod.Id) - 1, "addon first, then the main mod: " + state());
                // F2: the addon record is switched in place and moved above the new main record in its own install (no Move).
                Require(engine.LoadState().Generation == generation + 2, "changes for main + addon: " + (engine.LoadState().Generation - generation) + " (a Move is one more)");
                // L3: the main record's name shows Barefoot (the addon), not Full (the fill-in it carries).
                Require(mainMod.Name == AddonTestName + " · Short + Barefoot (addon)", "main record name: " + mainMod.Name);
                // F3 (Replace original): Full instead of Barefoot is not "already installed".
                Require(Call(form, "SameInstalledReplacement") != null, "the same ticks are not the installed replacement: " + state());
                node("Full").Checked = true; WaitReview(form);
                Require(Call(form, "SameInstalledReplacement") == null && !Details(form).Contains("Already installed"), "Full instead of Barefoot counts as already installed: " + state());
                node("Barefoot").Checked = true; WaitReview(form);
                // F1 B: the addon record disabled: shown ticked, it stays disabled when the main mod is reinstalled.
                engine.SetEnabled(addons[0].Id, false); refresh();
                open(addonEntry); open(mainEntry);
                Require(ticked("Barefoot") && !ticked("Full"), "a disabled addon record is not shown ticked: " + state());
                Require(healthLines().Any(t => t.Contains("stays disabled")) && !healthLines().Any(t => t.StartsWith("Uninstalls")), "health for a disabled addon: " + String.Join(" / ", healthLines()));
                node("Long").Checked = true; WaitReview(form);
                install();
                addons = addonRecords();
                Require(addons.Count == 1 && !addons[0].Enabled && addons[0].SelectedIds.SequenceEqual(new[] { "04 Barefoot" }) && mainRecord().SelectedIds.Contains(node("Long").Tag is ModComponent ? ((ModComponent)node("Long").Tag).Id : "") && status().Contains("kept disabled"), "the disabled addon record after a reinstall: " + state());
                // F1: a record never shown ticked is left alone (unticked addon: no health line, nothing uninstalled) ...
                Get<HashSet<string>>(form, "shownAddonRecords").Clear();
                node("Full").Checked = true; WaitReview(form);
                Require(!ticked("Barefoot") && !healthLines().Any(t => t.StartsWith("Uninstalls")), "health for a record never shown: " + String.Join(" / ", healthLines()));
                install();
                Require(addonRecords().Count == 1, "a record never shown ticked was uninstalled: " + state());
                // ... and one shown ticked and unticked goes, as the health report says.
                open(addonEntry); open(mainEntry);
                Require(ticked("Barefoot"), "reopen with the disabled record: " + state());
                node("Full").Checked = true; WaitReview(form);
                Require(healthLines().Any(t => t.StartsWith("Uninstalls " + AddonTestName + " Addon")), "no uninstall line for the shown record: " + String.Join(" / ", healthLines()));
                install();
                Require(addonRecords().Count == 0 && status().Contains("removed " + AddonTestName + " Addon"), "the shown, unticked record stayed: " + state());
                // F2: a step after the main commit that fails is named in the status (no check mark).
                MainForm.BeforeAddonCommit = delegate { throw new IOException("simulated addon failure"); };
                try { node("Barefoot").Checked = true; WaitReview(form); install(); }
                finally { MainForm.BeforeAddonCommit = null; }
                Require(mainRecord() != null && addonRecords().Count == 0 && status().StartsWith("Installed " + AddonTestName) && status().Contains("; adding Barefoot from " + Path.GetFileName(addon) + " failed: simulated addon failure"), "late failure: " + state());
                // F2: the addon archive changed after the review: refused before the main record is written.
                cleanup(); refresh();
                open(mainEntry); node("Barefoot").Checked = true; node("Short").Checked = true; WaitReview(form);
                Require(Get<FlatButton>(form, "installButton").Enabled, "not ready: " + state());
                WriteZip(addon, new Dictionary<string, byte[]> {
                    { "00 Menu/modinfo.ini", Ini("name=" + AddonTestName + "\nDummyMod=true\ndescription=Fixture main mod\n") },
                    { "04 Barefoot/modinfo.ini", Ini("name=Barefoot\nAddonFor=Outfit\nRequirement=Main files\nscreenshot=preview.png\nMenuPriority=100\n") },
                    { "04 Barefoot/preview.png", AddonPreview },
                    { "04 Barefoot/" + ReplaceChecks.V5, Encoding.ASCII.GetBytes("barefoot mesh, changed on disk") } });
                install();
                Require(mainRecord() == null && addonRecords().Count == 0, "a changed addon archive was not refused before the main record: " + state());
                // F3 (Extra outfit): Barefoot and Full are two outfits; then a part that can't be added leaves the outfit as it was.
                Call(form, "SetInstallMode", InstallModes.Slot, true);
                open(addonEntry); open(mainEntry);
                node("Barefoot").Checked = true; node("Short").Checked = true; WaitReview(form);
                Get<ComboBox>(form, "targetCharacter").SelectedItem = "Cammy"; Get<PaddedTextField>(form, "outfitName").Text = "Addon Extra 2"; WaitReview(form);
                install();
                InstalledOutfit outfit = Installer.ListInstalled(root).Single(o => o.Name == "Addon Extra 2");
                string mesh = Path.Combine(outfit.Folder, ReplaceChecks.V5.Replace('/', '\\'));
                Require(outfit.Parts != null && outfit.Parts.Count == 1 && isAddon(outfit.Parts[0].SourcePath) && File.ReadAllText(mesh) == "barefoot mesh, changed on disk", "extra outfit with Barefoot: " + state());
                Require(form.DuplicateExtraOutfit() != null && form.SuggestedOutfitName() == "Addon Extra 2", "the same ticks are not that outfit: " + form.SuggestedOutfitName());
                node("Full").Checked = true; WaitReview(form);
                Require(form.DuplicateExtraOutfit() == null && form.SuggestedOutfitName() != "Addon Extra 2", "Full instead of Barefoot counts as that outfit: " + form.SuggestedOutfitName());
                node("Barefoot").Checked = true; Get<PaddedTextField>(form, "outfitName").Text = "Addon Extra 2"; WaitReview(form);
                WriteZip(addon, new Dictionary<string, byte[]> {
                    { "00 Menu/modinfo.ini", Ini("name=" + AddonTestName + "\nDummyMod=true\ndescription=Fixture main mod\n") },
                    { "04 Barefoot/modinfo.ini", Ini("name=Barefoot\nAddonFor=Outfit\nRequirement=Main files\nscreenshot=preview.png\nMenuPriority=100\n") },
                    { "04 Barefoot/preview.png", AddonPreview },
                    { "04 Barefoot/" + ReplaceChecks.V5, Encoding.ASCII.GetBytes("barefoot mesh, changed once more") } });
                install();
                outfit = Installer.ListInstalled(root).Single(o => o.Name == "Addon Extra 2");
                Require(outfit.Parts != null && outfit.Parts.Count == 1 && isAddon(outfit.Parts[0].SourcePath) && File.ReadAllText(mesh) == "barefoot mesh, changed on disk", "the outfit lost its Barefoot part when adding it failed: parts " + (outfit.Parts == null ? 0 : outfit.Parts.Count) + " mesh " + File.ReadAllText(mesh) + " / " + state());
            }
            finally
            {
                MainForm.BeforeAddonCommit = null;
                library.Remove(mainEntry); library.Remove(addonEntry);
                try { cleanup(); } catch (Exception ex) { Program.Log(ex); }
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                ShowLibrary(form);
            }
        }

        // With tests.local.json keys addonMain and addonPart (the user's real zips, read only): the main mod takes Barefoot
        // into Outfit next to Full. Without them, nothing to check.
        private static string RealAddonArchives()
        {
            LocalTestConfig local;
            try { if (!LocalTestConfig.Available) return "skipped (no tests.local.json)"; local = LocalTestConfig.Current; } catch (Exception ex) { return "skipped (" + ex.Message + ")"; }
            string main, addon;
            if (local.Archives == null || !local.Archives.TryGetValue("addonMain", out main) || !local.Archives.TryGetValue("addonPart", out addon) || !File.Exists(main) || !File.Exists(addon)) return "skipped (no addonMain/addonPart archives)";
            ModPackage m = PackageReader.Load(main, local.SevenZip), a = PackageReader.Load(addon, local.SevenZip);
            Require(LibraryAddons.IsAddonOf(m, a) && !LibraryAddons.IsAddonOf(a, m), "real: not detected as the main mod's addon");
            ModPackage merged = LibraryAddons.Merge(m, new List<ModPackage> { a });
            ModComponent outfit = merged.Components.Single(c => c.Name == "Outfit"), full = merged.Components.Single(c => c.Name == "Full"), bare = merged.Components.Single(c => c.Name == "Barefoot");
            Require(bare.ParentId == outfit.Id && merged.IsChoiceBranch(outfit, bare) && merged.IsChoiceBranch(outfit, full), "real: Barefoot is not a choice of Outfit");
            string mainFiles = merged.Components.Single(c => c.Name == "Main files").Id;
            AddonSplit split = LibraryAddons.Split(merged, new[] { mainFiles, bare.Id }, true);
            Require(split.MainIds.Contains(full.Id) && split.Warnings.Count == 0 && split.Parts.Single().Value.Count == 1, "real: split " + String.Join("|", split.MainIds) + " / " + String.Join(" / ", split.Warnings));
            return "checked " + Path.GetFileName(main) + " + " + Path.GetFileName(addon);
        }
    }
}
