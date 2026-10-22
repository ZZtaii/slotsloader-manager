using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static partial class ReplaceUiChecks
    {
        private static void RootChoiceUi(MainForm form, ReplaceChecks.Fixture f, string output)
        {
            InstallImprovementChecks.OptionsFixture(f.Mod("choice-levels"));
            Call(form, "SetInstallMode", InstallModes.Replace, true);
            Load(form, f, "choice-levels"); WaitReview(form);
            var p = Get<ModPackage>(form, "package");
            var nodes = Get<Dictionary<string, OptionNode>>(form, "componentNodes");
            var ids = p.Components.Where(c => !c.IsDummy).Select(c => c.Id).ToList();
            Require(p.Name == "Lighting levels" && Get<List<string>>(form, "selectedOrder").Count == 0, "root choices were preselected or renamed");
            Require(!Get<FlatButton>(form, "installButton").Enabled && Summary(form).Contains("Lighting levels"), "unresolved choice footer/install gate");
            Render(form, Path.Combine(output, "root-choice-empty.png"));
            nodes[ids[0]].Checked = true; WaitReview(form);
            Require(Get<FlatButton>(form, "installButton").Enabled && Get<CompositionPlan>(form, "plan").Files.Count == 29, "single option did not become installable");
            nodes[ids[4]].Checked = true; WaitReview(form);
            Require(!nodes[ids[0]].Checked && Get<List<string>>(form, "selectedOrder").SequenceEqual(new[] { ids[4] }), "radio change retained the old level");
            Render(form, Path.Combine(output, "root-choice-selected.png"));
            form.ResetOptions(); WaitReview(form);
            Require(Get<List<string>>(form, "selectedOrder").Count == 0 && !Get<FlatButton>(form, "installButton").Enabled, "reset guessed a choice");
            // A record made by the old all-roots default: its bytes are Off,
            // while its persisted layer list contains all five. Reading must not rewrite it.
            var engine = FormEngine(f); ReplacePlan prepared;
            var mod = ReplacePlanner.PrepareAndInstall(Composer.Plan(p, new[] { ids[4] }, true), "", engine, "Saved lighting", false, null, out prepared);
            try
            {
                var state = engine.LoadState(); state.Find(mod.Id).SelectedIds = ids.ToList(); DurableFile.WriteJson(engine.StatePath, state);
                Call(form, "RefreshInstalled", (object)null); WaitIdle(form);
                Load(form, f, "choice-levels"); WaitReview(form);
                Require(Get<List<string>>(form, "selectedOrder").SequenceEqual(new[] { ids[4] }), "saved all-five selection did not preserve Off");
                Require(Get<Label>(form, "statusText").Visible && Get<Label>(form, "statusText").Text.Contains("Saved selection resolves to 5. Off"), "saved-choice note not visible after review");
                Require(Call(form, "SameInstalledReplacement") != null, "already-installed footer lost its record");
                Require(engine.LoadState().Find(mod.Id).SelectedIds.SequenceEqual(ids), "reading rewrote the saved record");
                Render(form, Path.Combine(output, "root-choice-restored.png"));
            }
            finally { engine.Uninstall(mod.Id); Call(form, "RefreshInstalled", (object)null); WaitIdle(form); }
            // The actual queue skips an unchosen group and continues to the next mod.
            string simple = f.Mod("choice-simple"); ReplaceChecks.Put(simple, "natives/stm/choice-simple.bin", new byte[] { 4 });
            var queued = Get<List<MainForm.BatchItem>>(form, "batchItems");
            queued.Clear(); queued.Add(new MainForm.BatchItem { Entry = new LibraryEntry { SourcePath = p.SourcePath, Name = p.Name } });
            queued.Add(new MainForm.BatchItem { Entry = new LibraryEntry { SourcePath = simple, Name = "Simple" } });
            Get<SegmentedChoice>(form, "batchMode").SelectedIndex = 1;
            try
            {
                form.RunBatchInstall(); WaitIdle(form);
                Require(queued[0].State == MainForm.BatchStates.Skipped && queued[0].Detail.Contains("Choose an option") && queued[1].State == MainForm.BatchStates.Installed, "queue guessed a level or failed to continue");
                Require(!engine.LoadState().Mods.Any(m => ReplacePlanner.SameSource(m.SourcePath, p.SourcePath)), "queue installed unresolved choices");
            }
            finally
            {
                foreach (var added in engine.LoadState().Mods.Where(m => ReplacePlanner.SameSource(m.SourcePath, simple)).ToList()) engine.Uninstall(added.Id);
                queued.Clear(); Call(form, "CloseBatch"); Call(form, "RefreshInstalled", (object)null); WaitIdle(form);
            }
            InstalledPakSearchUi(form, output);
        }
        private static void InstalledPakSearchUi(MainForm form, string output)
        {
            var previous = Get<List<GameModRow>>(form, "gameRows");
            var search = Get<PaddedTextField>(form, "installedSearch"); string text = search.Text;
            var pak = new GameModRow { Kind = "Pak", Mod = new ReplaceMod { Id = "search-pak", Name = "NULSPACE", Files = new List<ModFile> { new ModFile { Path = "re_chunk_000.pak.patch_002.pak" } } } };
            var loose = new GameModRow { Kind = "Replacement", Mod = new ReplaceMod { Id = "search-loose", Name = "Loose lighting", Files = new List<ModFile> { new ModFile { Path = "natives/stm/lighting.bin" } } } };
            try
            {
                Set(form, "gameRows", new List<GameModRow> { pak, loose });
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Kind(form, "All");
                foreach (string query in new[] { "pak", "pak mods nulspace" })
                {
                    search.Text = query; Application.DoEvents();
                    var rows = Get<ListView>(form, "installedList").Items.Cast<ListViewItem>().ToList();
                    Require(rows.Count == 1 && rows[0].Tag == pak, "Installed PAK query: " + query);
                }
                Render(form, Path.Combine(output, "installed-pak-search.png"));
            }
            finally { Set(form, "gameRows", previous); search.Text = text; Call(form, "FillInstalled"); Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); }
        }
    }

    internal static partial class FilterChecks
    {
        private static void CheckPortraitPriorityUi(string root)
        {
            var single = new LibraryEntry { Name = "E. Honda Outfit", SourcePath = Path.Combine(root, "honda"), Detection = "Native", Character = "E. Honda", CharacterCodes = new List<string> { "020" }, CostumeKeys = new List<string> { "020/001" } };
            var multi = new LibraryEntry { Name = "All fighters lighting", SourcePath = Path.Combine(root, "lighting"), Detection = "Ambiguous", CharacterCodes = new List<string> { "020", "001" }, CostumeKeys = new List<string> { "020/001", "001/001" }, AddedUtc = DateTime.UtcNow };
            var pending = new LibraryEntry { Name = "A pending E. Honda mod", SourcePath = Path.Combine(root, "pending"), Detection = "Pending", Character = "E. Honda", FilenameCharacter = "E. Honda", Pending = true };
            using (Window w = Open(root, null, new Size(1232, 640), new List<LibraryEntry> { multi, pending, single }))
            {
                var form = w.Form; var panel = Get<FilterPanel>(form, "filterPanel");
                var list = Get<ListView>(form, "modList");
                form.ShowFilters(true, false); Toggle(panel.Grid, "020"); Settle(w.Host);
                foreach (LibrarySortKey key in Enum.GetValues(typeof(LibrarySortKey))) foreach (bool ascending in new[] { false, true })
                {
                    form.SetLibrarySort(key, ascending);
                    Require(list.Items.Count == 3 && list.Items[0].Tag == single, "portrait specificity lost to sort " + key);
                }
                form.SetLibrarySort(LibrarySortKey.Added, false);
                F("selectingMod").SetValue(form, true);
                list.Items.Cast<ListViewItem>().Single(i => i.Tag == multi).Selected = true;
                F("selectingMod").SetValue(form, false);
                pending.Pending = false; pending.Detection = "Native"; pending.CharacterCodes.Add("020");
                Apply(form); Settle(w.Host);
                Require(list.Items.Count == 3 && list.Items[0].Tag == pending && list.Items[1].Tag == single && list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag == multi, "identity completion changed inclusion/selection");
                Get<PaddedTextField>(form, "search").Text = "c1"; Apply(form);
                Require(list.Items[0].Tag == single, "text relevance outranked the portrait");
                Get<PaddedTextField>(form, "search").Text = ""; Apply(form); Settle(w.Host);
                using (var bitmap = new Bitmap(w.Host.Width, w.Host.Height))
                {
                    w.Host.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(root, "honda-specific-first.png"), ImageFormat.Png);
                }
            }
        }
    }
}
