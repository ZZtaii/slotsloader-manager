using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 25 Nov 2026 (the user: "since we use wael's slotsloader now, i believe we can install stages as extra ... we should
    // probably start allowing it"): a stage mod opens with INSTALL AS "Extra stage" | "Replace original". Extra stage puts
    // the ticked options in the game's reframework\stage_mods (StageMods.cs), where the suite-line Slots Loader makes each
    // one a variant of its stage with the original kept. No character, no outfit name, no composition check: the options
    // are copied as they are. A mod of one option opens with it ticked; a mod of several opens with none (the user: "if a
    // mod has only one file then its fine to tick it by default"), or with the options it is installed with. Every ticked
    // option is a variant of its own, so choose-one groups don't untick each other here.
    internal sealed partial class MainForm
    {
        private StageInfo stageInfo;
        internal const string StageModeLabel = "Extra stage";
        internal const string StageModeDescription = "Extra stage (Slots Loader): adds each ticked option as a variant of its stage; the original stays.";
        internal const string StageSearchWords = " stage stages";

        // Extra stage is what INSTALL AS shows: a stage mod is open and Replace original is not chosen.
        internal bool StageMode { get { return stageInfo != null && package != null && installMode != InstallModes.Replace; } }
        // The checks that count files outside natives (a stage mod's .pak files) in Replace original and in Extra stage.
        internal bool WithRootFiles { get { return IsReplaceMode || StageMode; } }
        private bool StageLoaderReady { get { return StageMods.LoaderReady(config.GameRoot); } }

        // Runs on the reading thread (LoadPackage): a mod of .pak files only has its PAKs read for stage paths.
        private static StageInfo DetectStage(ModPackage p, string gameRoot, string sevenZip)
        {
            try
            {
                StageInfo quick = StageMods.Detect(p, null, gameRoot);
                if (quick != null || p == null || !IsPakPackage(p) || StageMods.Table(gameRoot) == null) return quick;
                using (var reader = new SourceReader(p.SourcePath, sevenZip)) return StageMods.Detect(p, reader, gameRoot);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); return null; }
        }

        // The stage folder's name: the mod's own name, except when a pack of several options has none of its own and
        // took one option's name (then its file or folder name, without a Nexus-style "-1234-1-0-1774283942" tail).
        internal static string StageModName(ModPackage p)
        {
            var options = StageMods.Options(p, p.Components.Select(c => c.Id));
            if (options.Count < 2 || !options.Any(c => String.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase))) return p.Name;
            string file = Directory.Exists(p.SourcePath) ? Path.GetFileName(p.SourcePath.TrimEnd('\\', '/')) : Path.GetFileNameWithoutExtension(p.SourcePath);
            file = System.Text.RegularExpressions.Regex.Replace(file ?? "", @"-\d+(-[\w.]+)*-\d{9,}$", "").Trim();
            return file.Length > 0 ? file : p.Name;
        }

        internal static List<string> StageDefaults(ModPackage p)
        {
            List<string> real = p == null ? new List<string>() : StageMods.Options(p, p.Components.Select(c => c.Id)).Select(c => c.Id).ToList();
            return real.Count == 1 ? real : new List<string>();
        }

        private List<GameModRow> LinkedStageRows(string source)
        {
            if (String.IsNullOrEmpty(source) || gameRows == null) return new List<GameModRow>();
            return gameRows.Where(r => r.Stage != null && String.Equals(r.Stage.Record.SourcePath, source, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        // The options this mod is installed with as an extra stage (those it still has), else null.
        private List<string> InstalledStageIds(ModPackage p)
        {
            GameModRow row = LinkedStageRows(p == null ? null : p.SourcePath).OrderByDescending(r => r.Stage.InstalledUtc).FirstOrDefault();
            if (row == null) return null;
            List<string> ids = row.Stage.Record.OptionIds.Where(id => { ModComponent c = p.Find(id); return c != null && !c.IsDummy && c.HasFiles(true); }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return ids.Count > 0 ? ids : null;
        }

        // 26 Nov 2026: a missing Slots Loader or Native Core is one click away (RunGameSetup); people without the Native
        // Core got stages without their pictures (the loader draws stage thumbnails and tiles through it).
        private string StageNotice(ToolStatus tools, out bool clickable)
        {
            clickable = false;
            if (!StageLoaderReady)
            {
                clickable = tools != null && tools.GameFound;
                return "Wael's Slots Loader isn't in the game folder, so Extra stage is off · " + (clickable ? "click to install it" : "use Replace original");
            }
            if (tools != null && tools.GameFound && !tools.NativeCore && LoaderRelease.CorePin != null)
            {
                clickable = true;
                return "Stage pictures need SF6 Native Core · click to install it";
            }
            return "Each ticked option becomes a variant of its stage · the original stays";
        }
        private string StagesText(int most)
        {
            if (stageInfo == null) return "";
            List<string> named = stageInfo.Keys.Where(k => k != ModCategories.OtherStage).Select(k => ModCategories.StageName(k, StageNames)).Distinct().ToList();
            if (named.Count == 0) return "Stage files";
            return String.Join(", ", named.Take(most)) + (named.Count > most ? " +" + (named.Count - most) : "");
        }

        private void UpdateStageFooter()
        {
            List<ModComponent> options = StageMods.Options(package, selectedOrder);
            GameModRow installedRow = LinkedStageRows(package.SourcePath).FirstOrDefault();
            if (!StageLoaderReady)
            {
                planSummary.Text = "Needs Wael's Slots Loader"; planSummary.ForeColor = Palette.Accent;
                planDetails.Text = "Install it, or use Replace original"; planDetails.ForeColor = Palette.Muted;
            }
            else if (options.Count == 0)
            {
                planSummary.Text = "Select options"; planSummary.ForeColor = Palette.Text;
                planDetails.Text = "Tick the options to add · each one is a variant of its stage"; planDetails.ForeColor = Palette.Muted;
            }
            else
            {
                planSummary.Text = "Ready to install · " + ReplacePlanner.N(options.Count, "variant"); planSummary.ForeColor = Palette.Green;
                planDetails.Text = installedRow != null ? "Installed with " + ReplacePlanner.N(installedRow.Stage.Record.OptionIds.Count, "variant") + " · Install updates it" : StagesText(2) + " · added to stage_mods";
                planDetails.ForeColor = installedRow != null ? Palette.Accent : Palette.Muted;
            }
            UpdateStageInstallButton();
        }

        private void UpdateStageInstallButton()
        {
            if (installButton == null || package == null) return;
            installButton.Text = "Install";
            installButton.Enabled = !busy && StageLoaderReady && StageMods.Options(package, selectedOrder).Count > 0;
            if (previewButton != null) previewButton.Enabled = false;   // nothing to check: the options are copied as they are
        }

        private void InstallStage()
        {
            if (busy || package == null || !StageMode) return;
            ModPackage current = package; List<string> ids = selectedOrder.ToList(); string game = config.GameRoot, sevenZip = config.SevenZipPath, name = StageModName(current);
            Work("Installing extra stage…", delegate { return StageMods.Install(current, ids, game, sevenZip, name); }, delegate(InstallResult result)
            {
                bool core = ToolStatus.Detect(game).NativeCore || LoaderRelease.CorePin == null;
                RefreshInstalled("✓ " + result.Message + (core ? "" : " Its pictures need SF6 Native Core: Settings, Install Native Core."));
            });
        }

        internal static string StageRowText(GameModRow row)
        {
            StageInstallRecord r = row.Stage.Record;
            return "Extra stage   ·   " + ReplacePlanner.N(r.OptionIds.Count, "variant") + "   ·   " + Utilities.FriendlyBytes(r.Bytes) + "   ·   stage_mods";
        }

        private void ShowStageRow(GameModRow row)
        {
            StageInstall s = row.Stage;
            installedTitle.Text = row.Name;
            var chips = new List<ChipStrip.Chip> { ChipStrip.Neutral("Extra stage"), ChipStrip.Neutral(ReplacePlanner.N(s.Record.OptionIds.Count, "variant")) };
            AddAuthorChip(chips, s.Record.SourcePath);
            string gone = SourceGoneNote(s.Record.SourcePath);
            if (gone != null) chips.Add(ChipStrip.Quiet(gone));
            if (!StageMods.LoaderReady(config.GameRoot)) chips.Add(ChipStrip.Warn("Slots Loader missing"));
            else if (!ToolStatus.Detect(config.GameRoot).NativeCore) chips.Add(ChipStrip.Warn("No pictures: Native Core missing"));
            installedChips.SetChips(chips);
            installedChips.Cursor = Cursors.Default;
            installedDetail.Text = s.Folder; installedPathRow.Visible = true;
            installedSectionCaption.Text = "EXTRA STAGE";
            var lines = new List<string>
            {
                "In the game's stage_mods folder. Wael's Slots Loader makes each option a variant of the stage it changes: on the stage select, UP / DOWN picks it, and the original stays one of the choices. A whole new stage built over another gets a cell of its own in Battle Settings, Training and Replays.",
                "",
                "Uninstall deletes this folder; the stage goes back to the original at the next start of SF6. Open the mod in the Mod library to change its options (Install updates this folder).",
                "",
                "VARIANTS",
            };
            lines.AddRange(s.Record.OptionNames.Select(n => "• " + n));
            if (!String.IsNullOrEmpty(s.Record.SourcePath)) { lines.Add(""); lines.Add("From " + s.Record.SourcePath); }
            installedHelp.Text = String.Join(Environment.NewLine, lines); installedHelp.Select(0, 0);
        }

        // No confirmation and no Undo, as an extra outfit (the click is the consent; the library source reinstalls it).
        private void UninstallStageRow(GameModRow row)
        {
            if (row == null || row.Stage == null || busy) return;
            string folder = row.Stage.Folder, name = row.Name, game = config.GameRoot;
            Work("Uninstalling " + name + "…", delegate { return StageMods.Uninstall(game, folder); }, delegate(string message)
            {
                RefreshInstalled("✓ Uninstalled " + name + " from stage_mods." + (String.IsNullOrEmpty(message) ? "" : " " + message) + " Restart SF6 to see the change.");
                UpdateLibraryUninstall();
            });
        }
    }
}
