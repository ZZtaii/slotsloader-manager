using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18: Install all variants of a ZIP mod shown with its addons (ArchiveCombine.cs, ArchiveInstalls.AddonsFor) and
    // the addon marks on the option rows (MainFormAddons.cs). Same synthetic zips as the 1.0.16 addon checks.
    internal static partial class ReplaceUiChecks
    {
        private static HashSet<string> Set(IEnumerable<string> items) { return new HashSet<string>(items, StringComparer.Ordinal); }

        // The combined ZIP: the main ZIP's bytes unchanged up to its directory, Barefoot inside Outfit when read back, the
        // addon's menu left out; a clash is refused and leaves no file; a RAR/7Z main, a gone addon and a mod that is no
        // addon are named and left out; an option at an addon's top level gets a folder named after the addon.
        private static void AddonCombine(string folder)
        {
            string main, addon; WriteAddonZips(folder, out main, out addon);
            var notes = new List<string>();
            List<ArchiveCombine.Addon> parts = ArchiveInstalls.AddonsFor(main, new[] { addon }, "", notes);
            Require(notes.Count == 0 && parts.Count == 1, "addons: " + parts.Count + " / " + String.Join(" / ", notes));
            Require(Set(parts[0].Files.Select(p => p.Value)).SetEquals(new[] { "04 Barefoot/modinfo.ini", "04 Barefoot/preview.png", "04 Barefoot/" + ReplaceChecks.V5 })
                && parts[0].Files.All(p => p.Key == p.Value), "addon files: " + String.Join(", ", parts[0].Files.Select(p => p.Key + " -> " + p.Value)));
            string combined = Path.Combine(folder, "combined.zip"); if (File.Exists(combined)) File.Delete(combined);
            ArchiveCombine.Write(main, parts, combined, "");
            byte[] mainBytes = File.ReadAllBytes(main), outBytes = File.ReadAllBytes(combined);
            int centralOffset = BitConverter.ToInt32(mainBytes, mainBytes.Length - 22 + 16);
            Require(outBytes.Length > mainBytes.Length && outBytes.Take(centralOffset).SequenceEqual(mainBytes.Take(centralOffset)), "the main archive's entries were not kept byte for byte");
            using (ZipArchive original = ZipFile.OpenRead(main))
            using (ZipArchive z = ZipFile.OpenRead(combined))
            {
                Require(z.Entries.Count == original.Entries.Count + 3 && z.Entries.Count(e => e.FullName == "00 Menu/modinfo.ini") == 1, "entries: " + z.Entries.Count + " vs " + original.Entries.Count);
                using (var reader = new StreamReader(z.GetEntry("04 Barefoot/" + ReplaceChecks.V5).Open())) Require(reader.ReadToEnd() == "barefoot mesh", "Barefoot's mesh in the copy");
                using (var reader = new StreamReader(z.GetEntry("00 Menu/modinfo.ini").Open())) Require(reader.ReadToEnd().Contains("Fixture main mod"), "the main's menu");
            }
            ModPackage back = PackageReader.Load(combined, "");
            ModComponent outfit = back.Components.Single(c => c.Name == "Outfit"), bare = back.Components.Single(c => c.Name == "Barefoot"), full = back.Components.Single(c => c.Name == "Full");
            Require(bare.ParentId == outfit.Id && back.IsChoiceBranch(outfit, bare) && back.IsChoiceBranch(outfit, full) && !back.Warnings.Any(w => w.StartsWith("Cannot uniquely resolve")), "read back: Barefoot under " + bare.ParentId + " / " + String.Join(" / ", back.Warnings));
            // A clash: the addon has a file the main mod has.
            string clash = Path.Combine(folder, AddonTestName + " Clash.zip");
            WriteZip(clash, new Dictionary<string, byte[]> {
                { "00 Menu/modinfo.ini", Ini("name=" + AddonTestName + "\nDummyMod=true\n") },
                { "03 Full/modinfo.ini", Ini("name=Full again\nAddonFor=Outfit\n") },
                { "03 Full/" + ReplaceChecks.V5, Encoding.ASCII.GetBytes("other full mesh") } });
            notes.Clear();
            List<ArchiveCombine.Addon> clashing = ArchiveInstalls.AddonsFor(main, new[] { clash }, "", notes);
            Require(clashing.Count == 1 && notes.Count == 0, "the clashing addon was not taken: " + String.Join(" / ", notes));
            string refused = Path.Combine(folder, "refused.zip"); if (File.Exists(refused)) File.Delete(refused);
            string message = null;
            try { ArchiveCombine.Write(main, clashing, refused, ""); } catch (IOException ex) { message = ex.Message; }
            Require(message != null && message.Contains("both have 03 Full/") && !File.Exists(refused), "clash: " + (message ?? "not refused") + " / file " + File.Exists(refused));
            // Left out with a reason.
            notes.Clear();
            Require(ArchiveInstalls.AddonsFor(Path.Combine(folder, "Main.7z"), new[] { addon }, "", notes).Count == 0 && notes.Single().Contains("only be combined with a ZIP mod"), "7Z main: " + String.Join(" / ", notes));
            notes.Clear();
            Require(ArchiveInstalls.AddonsFor(main, new[] { Path.Combine(folder, "Gone.zip") }, "", notes).Count == 0 && notes.Single().Contains("not in your libraries"), "gone addon: " + String.Join(" / ", notes));
            string other = Path.Combine(folder, "Other mod.zip");
            WriteZip(other, new Dictionary<string, byte[]> {
                { "00 Menu/modinfo.ini", Ini("name=Other mod\nDummyMod=true\n") },
                { "04 Extra/modinfo.ini", Ini("name=Extra\nAddonFor=Outfit\n") },
                { "04 Extra/" + ReplaceChecks.V5, Encoding.ASCII.GetBytes("extra mesh") } });
            notes.Clear();
            Require(ArchiveInstalls.AddonsFor(main, new[] { other }, "", notes).Count == 0 && notes.Single().Contains("no longer an add-on"), "not an addon: " + String.Join(" / ", notes));
            // An option at the addon's top level goes into a folder named after the addon; a loose README stays out.
            string flat = Path.Combine(folder, AddonTestName + " Flat.zip");
            WriteZip(flat, new Dictionary<string, byte[]> {
                { "modinfo.ini", Ini("name=Flat Barefoot\nAddonFor=Outfit\n") },
                { ReplaceChecks.V5, Encoding.ASCII.GetBytes("flat mesh") } });
            ModPackage flatPackage = PackageReader.Load(flat, "");
            List<KeyValuePair<string, string>> flatFiles = LibraryAddons.CombinedEntries(PackageReader.Load(main, ""), flatPackage, new[] { "modinfo.ini", ReplaceChecks.V5 });
            string wrap = AddonTestName + " Flat/";
            Require(Set(flatFiles.Select(p => p.Value)).SetEquals(new[] { wrap + "modinfo.ini", wrap + ReplaceChecks.V5 }), "top-level option: " + String.Join(", ", flatFiles.Select(p => p.Value)));
            List<KeyValuePair<string, string>> withReadme = LibraryAddons.CombinedEntries(PackageReader.Load(main, ""), PackageReader.Load(addon, ""), new[] { "README.txt", "00 Menu/modinfo.ini", "04 Barefoot/modinfo.ini" });
            Require(withReadme.Count == 1 && withReadme[0].Value == "04 Barefoot/modinfo.ini", "loose or menu files went in: " + String.Join(", ", withReadme.Select(p => p.Value)));
        }

        // In the window (Extra outfit): the main mod shown with its addon offers Install all variants naming the addon; the
        // copy has Barefoot, is linked to the main mod with the addon recorded (the addon's library row counts as
        // installed); a preset saves the addon and copies the outfit instead once the addon is gone; Barefoot's row is an
        // addon row, its description names the file, and the options' menu opens the addon or the main mod in Explorer.
        private static void AddonAllVariants(MainForm form, string folder)
        {
            string main, addon; WriteAddonZips(folder, out main, out addon);
            List<LibraryEntry> library = Get<List<LibraryEntry>>(form, "library");
            var mainEntry = new LibraryEntry { SourcePath = main, Name = Path.GetFileNameWithoutExtension(main), DisplayName = AddonTestName, Kind = "ZIP", OptionNames = new List<string> { AddonTestName, "Main files", "Outfit", "Full", "Shorts", "Hair styles", "Short", "Long", "Misc", "Portrait" } };
            var addonEntry = new LibraryEntry { SourcePath = addon, Name = Path.GetFileNameWithoutExtension(addon), DisplayName = AddonTestName, Kind = "ZIP", OptionNames = new List<string> { AddonTestName, "Barefoot" } };
            Func<string, OptionNode> node = name => Get<Dictionary<string, OptionNode>>(form, "componentNodes").Values.First(n => ((ModComponent)n.Tag).Name == name);
            string root = Get<AppConfig>(form, "config").CostumeRoot;
            string dest = ArchiveInstalls.Destination(root, "Cammy", main);
            Func<List<InstalledOutfit>> copies = () => Installer.ListInstalled(root).Where(o => o.WholeArchive && ReplacePlanner.SameSource(o.SourcePath, main)).ToList();
            string moved = addon + ".away";
            library.Add(mainEntry); library.Add(addonEntry);
            try
            {
                if (File.Exists(dest)) File.Delete(dest);
                Call(form, "SetInstallMode", InstallModes.Slot, true);
                ShowLibrary(form); Call(form, "LoadPackage", mainEntry); WaitBusyOnly(form); WaitReview(form);
                ModPackage merged = Get<ModPackage>(form, "package");
                Require(merged.MergedFrom != null && merged.AddonParts.Count == 1, "the main mod did not take in its addon");
                Get<ComboBox>(form, "targetCharacter").SelectedItem = "Cammy"; WaitReview(form);
                Panel row = Get<Panel>(form, "allVariantsRow"); FlatButton all = Get<FlatButton>(form, "allVariantsButton");
                Require(row.Visible && all.Visible && all.Enabled && all.Text == "Install all variants" && (all.AccessibleDescription ?? "").Contains(Path.GetFileName(addon)), "button: " + row.Visible + "/" + all.Enabled + "/" + all.Text + " / " + all.AccessibleDescription);
                all.PerformClick(); WaitIdle(form);
                string status = Get<Label>(form, "statusText").Text;
                Require(File.Exists(dest) && !Directory.EnumerateFiles(Path.GetDirectoryName(dest), ".*").Any(), "no copy, or a partial file was left: " + status);
                using (ZipArchive z = ZipFile.OpenRead(dest)) Require(z.GetEntry("04 Barefoot/" + ReplaceChecks.V5) != null && z.GetEntry("03 Full/" + ReplaceChecks.V5) != null, "the copy lacks Barefoot or Full");
                Require(status.Contains("every variant of " + AddonTestName + " with " + AddonTestName + " Addon for Cammy"), "status: " + status);
                List<InstalledOutfit> linked = copies();
                Require(linked.Count == 1 && linked[0].AddonSources != null && linked[0].AddonSources.Count == 1 && ReplacePlanner.SameSource(linked[0].AddonSources[0], addon), "record: " + linked.Count + " / " + (linked.Count == 1 && linked[0].AddonSources != null ? String.Join(",", linked[0].AddonSources) : "no addons"));
                Require(Library.InstallState(new LibraryEntry { SourcePath = addon, OptionIds = new List<string> { "x" } }, Installer.ListInstalled(root)) == LibraryInstallState.Full, "the addon's library row is not installed");
                // A preset keeps the addon; with the addon gone it copies the outfit and says why.
                MethodInfo pointerOf = typeof(OutfitSetStore).GetMethod("OutfitPointer", BindingFlags.NonPublic | BindingFlags.Static);
                object[] args = { linked[0], null };
                var pointer = (PresetOutfitPointer)pointerOf.Invoke(null, args);
                Require(pointer != null && pointer.Mode == OutfitSetStore.ModeAllVariants && pointer.AddonSources != null && pointer.AddonSources.Count == 1 && ReplacePlanner.SameSource(pointer.AddonSources[0], addon), "preset pointer: " + (pointer == null ? "copied, " + args[1] : String.Join(",", pointer.AddonSources ?? new List<string>())));
                File.Move(addon, moved);
                args = new object[] { copies()[0], null };
                Require(pointerOf.Invoke(null, args) == null && Convert.ToString(args[1]).StartsWith("add-on not found at "), "a gone addon: " + args[1]);
                File.Move(moved, addon);
                // Reinstall: in place, still one linked copy.
                Require(all.Text == "Reinstall all variants", "after install: " + all.Text);
                all.PerformClick(); WaitIdle(form);
                Require(copies().Count == 1 && File.Exists(dest), "reinstall: " + copies().Count);
                // The addon mark, the description line and the options' menu.
                ModComponent bare = (ModComponent)node("Barefoot").Tag, full = (ModComponent)node("Full").Tag;
                Require(LibraryAddons.IsAddonId(merged, bare.Id) && !LibraryAddons.IsAddonId(merged, full.Id), "addon ids");
                Call(form, "ShowComponent", bare);
                Require(Get<Label>(form, "packageDescription").Text.Contains("Add-on from " + Path.GetFileName(addon)), "description: " + Get<Label>(form, "packageDescription").Text);
                Call(form, "ShowComponent", full);
                Require(!Get<Label>(form, "packageDescription").Text.Contains("Add-on from"), "a main option is described as an add-on");
                ContextMenuStrip menu = Get<OptionTree>(form, "componentTree").ContextMenuStrip;
                Require(menu != null && menu.Items.Count == 2, "no options menu");
                var addonItem = (ToolStripMenuItem)menu.Items[0]; var mainItem = (ToolStripMenuItem)menu.Items[1];
                string revealed = null; MainForm.RevealHook = p => revealed = p;
                Call(form, "OptionMenuTargets", node("Barefoot"), addonItem, mainItem);
                Require(addonItem.Available && addonItem.Text.Contains(Path.GetFileName(addon)) && mainItem.Text == "Open main mod in Explorer", "menu on Barefoot: " + addonItem.Available + " / " + addonItem.Text + " / " + mainItem.Text);
                addonItem.PerformClick();
                Require(revealed != null && ReplacePlanner.SameSource(revealed, addon), "Open add-on in Explorer selected " + revealed);
                Call(form, "OptionMenuTargets", node("Full"), addonItem, mainItem);
                revealed = null; mainItem.PerformClick();
                Require(!addonItem.Available && revealed != null && ReplacePlanner.SameSource(revealed, main), "menu on Full: " + addonItem.Available + " / " + revealed);
                // A picture of the options with Barefoot selected, for a look at the tag (addon-variants\options.png).
                OptionTree tree = Get<OptionTree>(form, "componentTree");
                tree.SelectedNode = node("Barefoot"); Application.DoEvents();
                using (var picture = new System.Drawing.Bitmap(Math.Max(1, tree.Width), Math.Max(1, tree.Height)))
                {
                    tree.DrawToBitmap(picture, new System.Drawing.Rectangle(0, 0, picture.Width, picture.Height));
                    picture.Save(Path.Combine(folder, "options.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            finally
            {
                MainForm.RevealHook = null;
                if (File.Exists(moved) && !File.Exists(addon)) File.Move(moved, addon);
                library.Remove(mainEntry); library.Remove(addonEntry);
                try { if (File.Exists(dest)) File.Delete(dest); } catch (Exception ex) { Program.Log(ex); }
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                ShowLibrary(form);
            }
        }
    }
}
