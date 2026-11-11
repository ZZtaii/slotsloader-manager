using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.6 (the user): in Extra outfit mode, "Add to outfit" (end of the destination line) copies exactly the ticked
    // options of the open mod into an extra outfit that is already installed, instead of a new slot. AddPartsForm picks
    // the outfit. The engine is Installer.AddToOutfit (InstallerParts.cs).
    internal sealed partial class MainForm
    {
        private FlatButton addPartsButton;

        private FlatButton BuildAddPartsButton()
        {
            addPartsButton = Button("Add to outfit", false, 128, delegate { AddPartsToOutfit(); });
            addPartsButton.Font = Palette.Small; addPartsButton.Margin = Padding.Empty; addPartsButton.Name = "addPartsButton";
            addPartsButton.AccessibleName = "Add the ticked options to an installed outfit";
            addPartsButton.AccessibleDescription = "Copies the files of the ticked options into an extra outfit you already have.";
            addPartsButton.Visible = false;
            return addPartsButton;
        }

        // The ticked options' files (no main-file or choose-one rule: a lone hair option is fine).
        // 1.0.16: the open mod's own ticks only (addon options shown inside it are added by Install).
        private CompositionPlan AddSelection() { return package == null ? null : Installer.SelectionFiles(SourcePackage, LibraryAddons.MainOnly(package, selectedOrder)); }

        // Shown in Extra outfit mode for an open mod; usable once a ticked option has natives files.
        private void UpdateAddPartsButton()
        {
            if (addPartsButton == null) return;
            bool show = !IsReplaceMode && package != null;
            if (showAddParts != show) { showAddParts = show; LayoutVariantRow(); }
            CompositionPlan selection = show ? AddSelection() : null;
            addPartsButton.Enabled = show && !busy && missingRestoredOptions.Count == 0 && selection != null && selection.Errors.Count == 0 && selection.Files.Count > 0;
        }

        // 1.0.18 (user request: "If I have installed an addon via Add to outfit, is there any way to see this in the GUI
        // later?"): the Installed page says what was added to an outfit, from its install record (Parts).
        // The row: "+ Hair B", or "+ 3 added options" when the names would not fit; null when nothing was added.
        internal static string AddedPartsShort(InstalledOutfit outfit)
        {
            List<PartGraft> parts = outfit == null || outfit.Parts == null ? new List<PartGraft>() : outfit.Parts.Where(g => g != null).ToList();
            if (parts.Count == 0) return null;
            List<string> names = parts.SelectMany(g => g.Options ?? new List<string>()).Where(n => !String.IsNullOrWhiteSpace(n)).ToList();
            return names.Count >= 1 && names.Count <= 2 ? "+ " + String.Join(" + ", names) : "+ " + ReplacePlanner.N(Math.Max(names.Count, parts.Count), "added option");
        }
        // The details: one line per addition (options, mod, files, date; a note when its mod is gone), or "".
        internal static string AddedPartsDetails(InstalledOutfit outfit)
        {
            if (AddedPartsShort(outfit) == null) return "";
            var lines = new List<string> { "ADDED FROM OTHER MODS (Add to outfit, or an addon's options)" };
            foreach (PartGraft g in outfit.Parts.Where(g => g != null))
            {
                DateTime when; bool dated = DateTime.TryParse(g.AddedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out when);
                bool gone = false;
                try { gone = !String.IsNullOrEmpty(g.SourcePath) && !System.IO.File.Exists(g.SourcePath) && !System.IO.Directory.Exists(g.SourcePath); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                lines.Add("• " + Installer.GraftText(g) + " · " + ReplacePlanner.N((g.Files ?? new List<string>()).Count, "file") + (dated ? " · added " + when.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "")
                    + (gone ? " · its mod is no longer in the library, so a reinstall of this outfit leaves it out" : ""));
            }
            return String.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine;
        }

        // 1.0.18 (user requests: "hopefully we can selectively remove the added mods too", then "a new window could be
        // cool because that way you can reorder parts of what to load first/last too"): "Added parts" on the Installed
        // page, shown for one selected outfit with added parts, opens AddedPartsForm: the parts with the winner on top,
        // drag to reorder, Remove takes one off, Save rebuilds the outfit from its mod with that list
        // (Installer.SetParts). No question for the usual case: Undo puts the earlier list back.
        private FlatButton removePartButton;
        private static bool PartSourceGone(PartGraft g)
        {
            try { return String.IsNullOrEmpty(g.SourcePath) || (!System.IO.File.Exists(g.SourcePath) && !System.IO.Directory.Exists(g.SourcePath)); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return true; }
        }
        internal void ShowAddedParts()
        {
            if (busy) return;
            InstalledOutfit outfit = GetInstalledSelection();
            List<PartGraft> parts = outfit == null || !outfit.Managed || outfit.Parts == null ? new List<PartGraft>() : outfit.Parts.Where(g => g != null).ToList();
            if (parts.Count == 0) return;
            if (PartSourceGone(new PartGraft { SourcePath = outfit.SourcePath }))
            { SetPageStatus(installedPage, "Can't change the added parts: the outfit's own mod is no longer in the library, and it is needed to put the outfit together again.", Palette.Accent); return; }
            List<PartGraft> wanted;
            // 1.0.18 (the user): no instant "Save as recipe" here; Recipes → New recipe opens the builder with this outfit's parts.
            if (!AddedPartsForm.Choose(this, outfit.Name, parts, PartSourceGone, null, out wanted, ModNameOf(outfit.SourcePath), SlotOptions(outfit))) return;
            SaveAddedParts(outfit, parts, wanted);
        }
        // Other added parts that this save would lose for good (their mods are gone, so they can't be copied again).
        private bool ConfirmLostParts(string name, List<PartGraft> lost)
        {
            if (lost.Count == 0 || Program.Headless) return true;
            string question = "Save the added parts of " + name + "?\n\nThe outfit is put together again from its own mod. These added parts can't be put back because their mods are no longer in the library, so they are removed too, with no Undo:\n"
                + String.Join("\n", lost.Select(g => "• " + Installer.GraftText(g)));
            return ThemedMessageBox.Show(this, question, "Added parts", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        // 1.0.18 (the user, about a part added with Add to outfit: "I don't see any options to manage it, like toggle
        // or untoggle it", "and even remove the extra option I added"): the added parts of the selected outfit as a
        // tick list in the Installed inspector, the winner first. A tick turns a part off (its files leave the outfit,
        // the record stays in the install record's OffParts) or on again in its old place; the cross at the end of a
        // row removes it for good. Each change rebuilds the outfit from its mod (Installer.SetParts) and has an Undo.
        // The Added parts window stays for the order.
        private Panel addedPartsPanel;
        private Label addedPartsCaption;
        private VariantList addedPartList;
        private InstalledOutfit addedPartsOutfit;
        private List<KeyValuePair<PartGraft, bool>> addedPartRows = new List<KeyValuePair<PartGraft, bool>>();
        internal VariantList AddedPartListForTest { get { return addedPartList; } }
        internal const int AddedPartRowsShown = 5;
        private Panel BuildAddedPartsPanel()
        {
            addedPartsPanel = new Panel { Dock = DockStyle.Top, Height = 34 + 30, Visible = false, Padding = Padding.Empty, Name = "addedPartsPanel" };
            addedPartsCaption = Caption("ADDED PARTS"); addedPartsCaption.Dock = DockStyle.Top; addedPartsCaption.Height = 34; addedPartsCaption.Padding = new Padding(0, 10, 0, 0); addedPartsCaption.AutoEllipsis = true;
            addedPartList = new VariantList { Dock = DockStyle.Fill, Removable = true, AccessibleName = "Added parts: tick to turn one off or on, Delete to remove it" };
            addedPartList.Toggled += ToggleAddedPart; addedPartList.Removed += RemoveAddedPart;
            addedPartsPanel.Controls.Add(addedPartList); addedPartsPanel.Controls.Add(addedPartsCaption);
            return addedPartsPanel;
        }
        private void ShowAddedPartList(InstalledOutfit outfit)
        {
            if (addedPartsPanel == null) return;
            List<PartGraft> on = outfit == null || !outfit.Managed || outfit.Parts == null ? new List<PartGraft>() : outfit.Parts.Where(g => g != null).ToList();
            List<PartGraft> off = outfit == null || !outfit.Managed || outfit.OffParts == null ? new List<PartGraft>() : outfit.OffParts.Where(g => g != null).ToList();
            addedPartsOutfit = on.Count + off.Count > 0 ? outfit : null;
            addedPartsPanel.Visible = addedPartsOutfit != null;
            // The winner first (the part added last), then the ones that are off.
            addedPartRows = Enumerable.Reverse(on).Select(g => new KeyValuePair<PartGraft, bool>(g, true)).Concat(off.Select(g => new KeyValuePair<PartGraft, bool>(g, false))).ToList();
            if (addedPartsOutfit == null) { addedPartList.SetRows(new VariantList.Row[0]); return; }
            addedPartsCaption.Text = "ADDED PARTS · " + on.Count + " OF " + (on.Count + off.Count) + " ON";
            // "Hair B from Hair Bundle"; a part that is its whole mod is named once ("Manon Victorias Secret C1 - barefoot").
            Func<PartGraft, string> shownPart = delegate(PartGraft g)
            {
                string mod = String.IsNullOrEmpty(g.Mod) ? System.IO.Path.GetFileNameWithoutExtension(g.SourcePath ?? "") : g.Mod;
                bool whole = g.Options == null || g.Options.Count == 0 || (g.Options.Count == 1 && String.Equals(g.Options[0], mod, StringComparison.OrdinalIgnoreCase));
                return (whole ? mod : Installer.GraftText(g)) + (g.Parts != null && g.Parts.Count > 0 ? " · " + OutfitParts.Labels(g.Parts) + " only" : "");
            };
            addedPartList.SetRows(addedPartRows.Select(r => new VariantList.Row { Label = Installer.GraftText(r.Key), Display = shownPart(r.Key), On = r.Value,
                Note = PartSourceGone(r.Key) ? "its mod is gone" : r.Value ? ReplacePlanner.N((r.Key.Files ?? new List<string>()).Count, "file") : "off", NoteAccent = PartSourceGone(r.Key) }));
            addedPartList.Enabled = !busy;
            addedPartsPanel.Height = Px(34) + Math.Min(addedPartRows.Count, AddedPartRowsShown) * addedPartList.RowHeight;
        }
        private bool AddedPartLists(int index, out InstalledOutfit outfit, out PartGraft part, out bool isOn, out List<PartGraft> on, out List<PartGraft> off)
        {
            outfit = addedPartsOutfit; part = null; isOn = false; on = off = null;
            if (busy || outfit == null || index < 0 || index >= addedPartRows.Count) return false;
            part = addedPartRows[index].Key; isOn = addedPartRows[index].Value;
            on = (outfit.Parts ?? new List<PartGraft>()).Where(g => g != null).ToList(); off = (outfit.OffParts ?? new List<PartGraft>()).Where(g => g != null).ToList();
            return true;
        }
        internal void ToggleAddedPart(int index)
        {
            InstalledOutfit outfit; PartGraft part; bool isOn; List<PartGraft> on, off;
            if (!AddedPartLists(index, out outfit, out part, out isOn, out on, out off)) return;
            List<PartGraft> wantOn = on.ToList(), wantOff = off.ToList();
            // A part whose mod is gone could never be turned on again: off would be a removal with no way back, so it
            // is refused and the cross (which says what it does) is the way to take it off.
            if (isOn && PartSourceGone(part)) { SetPageStatus(installedPage, "Can't turn " + Installer.GraftText(part) + " off: its mod is no longer in the library, so it could not be turned on again. Use the cross to remove it.", Palette.Accent); return; }
            if (isOn) { part.Place = on.IndexOf(part); wantOn.Remove(part); wantOff.Add(part); }
            else
            {
                if (PartSourceGone(part)) { SetPageStatus(installedPage, "Can't turn " + Installer.GraftText(part) + " on again: its mod is no longer in the library.", Palette.Accent); return; }
                wantOff.Remove(part); wantOn.Insert(Math.Max(0, Math.Min(part.Place < 0 ? wantOn.Count : part.Place, wantOn.Count)), part);
            }
            SaveAddedParts(outfit, on, wantOn, off, wantOff, (isOn ? "Turned off " : "Turned on ") + Installer.GraftText(part) + " on " + outfit.Name);
        }
        internal void RemoveAddedPart(int index)
        {
            InstalledOutfit outfit; PartGraft part; bool isOn; List<PartGraft> on, off;
            if (!AddedPartLists(index, out outfit, out part, out isOn, out on, out off)) return;
            List<PartGraft> wantOn = on.ToList(), wantOff = off.ToList();
            if (isOn) wantOn.Remove(part); else wantOff.Remove(part);
            SaveAddedParts(outfit, on, wantOn, off, wantOff, "Removed " + Installer.GraftText(part) + " from " + outfit.Name);
        }

        internal void SaveAddedParts(InstalledOutfit outfit, List<PartGraft> before, List<PartGraft> wanted) { SaveAddedParts(outfit, before, wanted, null, null, null); }
        // offBefore/offWanted: the parts that are turned off, before and after (null: they stay as they are).
        // said: what the status says when it worked, else the rebuild's own sentence.
        internal void SaveAddedParts(InstalledOutfit outfit, List<PartGraft> before, List<PartGraft> wanted, List<PartGraft> offBefore, List<PartGraft> offWanted, string said)
        {
            if (busy || outfit == null) return;
            string source = outfit.SourcePath, sevenZip = config.SevenZipPath, root = config.CostumeRoot, name = outfit.Name, id = outfit.Id;
            bool sameParts = before.Count == wanted.Count && before.Zip(wanted, (a, b) => ReferenceEquals(a, b)).All(x => x);
            if (!sameParts && PartSourceGone(new PartGraft { SourcePath = source }))
            { SetPageStatus(installedPage, "Can't change the added parts: the outfit's own mod is no longer in the library, and it is needed to put the outfit together again.", Palette.Accent); return; }
            if (!sameParts && !ConfirmLostParts(name, wanted.Where(PartSourceGone).ToList())) return;
            List<string> selection = (outfit.SelectedIds ?? new List<string>()).ToList();
            Func<List<PartGraft>, InstallResult> rebuildParts = null;
            Func<List<PartGraft>, List<PartGraft>, bool, InstallResult> rebuildAll = delegate(List<PartGraft> list, List<PartGraft> offList, bool partsChanged)
            {
                InstallResult made = partsChanged ? rebuildParts(list) : new InstallResult { Message = "Saved the added parts of " + name + "." };
                if (offList != null)
                {
                    InstalledOutfit fresh = Installer.ListInstalled(root).FirstOrDefault(o => o.Managed && o.Id == id);
                    if (fresh == null) throw new System.IO.IOException(name + " is no longer installed.");
                    // A part the rebuild could not put back (its mod changed, say) is named in the result; it stays in
                    // the list as off instead of vanishing from both lists.
                    List<PartGraft> onNow = (fresh.Parts ?? new List<PartGraft>()).Where(g => g != null).ToList();
                    List<PartGraft> record = offList.ToList();
                    foreach (PartGraft wantedPart in list)
                        if (!onNow.Any(g => Installer.SamePart(g, wantedPart)) && !record.Any(g => Installer.SamePart(g, wantedPart))) record.Add(wantedPart);
                    Installer.SetOffParts(fresh, root, record);
                }
                return made;
            };
            Func<List<PartGraft>, InstallResult> rebuild = delegate(List<PartGraft> list) { return rebuildAll(list, list == wanted ? offWanted : offBefore, !sameParts); };
            rebuildParts = delegate(List<PartGraft> list)
            {
                InstalledOutfit now = Installer.ListInstalled(root).FirstOrDefault(o => o.Managed && o.Id == id);
                if (now == null) throw new System.IO.IOException(name + " is no longer installed.");
                ModPackage source2 = PackageReader.Load(source, sevenZip, false);
                List<string> dropped; List<string> ids = MapSelection(source2, selection.Select(s => new KeyValuePair<string, string>(s, "")).ToList(), out dropped);
                if (dropped.Count > 0) throw new System.IO.IOException("The outfit's saved options are missing from its mod: " + String.Join(", ", dropped) + ". Use Edit options instead.");
                if (selection.Count == 0) ids = Composer.DefaultSelection(source2, false);
                CompositionPlan composition = Composer.SavedPlan(source2, ids, false);
                if (!composition.CanInstall) throw new System.IO.IOException("Couldn't rebuild " + name + ": " + String.Join(" ", composition.Errors));
                return Installer.SetParts(now, root, composition, list, sevenZip);
            };
            Work("Saving the added parts of " + name + "…", delegate { return rebuild(wanted); }, delegate(InstallResult result)
            {
                if (!before.Any(PartSourceGone)) OfferUndo("Changed the added parts of " + name, delegate
                {
                    Work("Putting the added parts of " + name + " back…", delegate { return rebuild(before); }, delegate(InstallResult back) { RefreshInstalled("✓ Undone: the added parts of " + name + " are as before."); });
                });
                RefreshInstalled("✓ " + (said == null ? result.Message : said + (result.PartsNote ?? "") + ". Restart SF6 to load it."));
            });
        }

        internal void AddPartsToOutfit()
        {
            if (busy || package == null) return;
            CompositionPlan selection = AddSelection();
            if (selection == null || selection.Errors.Count > 0 || selection.Files.Count == 0) { SetPageStatus(libraryPage, selection == null ? "Open a mod first." : String.Join(" ", selection.Errors.DefaultIfEmpty("Tick the options to add.")), Palette.Accent); return; }
            HashSet<string> codes = Installer.SelectionCodes(selection);
            string character = codes.Count == 1 && Characters.ByCode.ContainsKey(codes.First()) ? Characters.ByCode[codes.First()] : codes.Count == 0 ? package.Character : null;
            if (String.IsNullOrEmpty(character)) { SetPageStatus(libraryPage, codes.Count > 1 ? "These options change several fighters; tick one fighter's options." : "No fighter found in these options.", Palette.Accent); return; }
            // Outfits the Manager composed (enabled or disabled); manual drops and Install all variants copies are not rewritten.
            List<InstalledOutfit> outfits = installed.Where(o => o.Managed && String.Equals(o.Character, character, StringComparison.OrdinalIgnoreCase)).OrderByDescending(o => o.Enabled).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            InstalledOutfit target;
            if (!AddPartsForm.Choose(this, package.Name, Installer.OptionText(selection), selection.Files.Count, character, Installer.SelectionFolders(selection), outfits, out target)) return;
            string root = config.CostumeRoot, sevenZip = config.SevenZipPath;
            Work("Adding " + Installer.OptionText(selection) + " to " + target.Name + "…", delegate { return Installer.AddToOutfit(target, root, selection, sevenZip); }, delegate(InstallResult result)
            {
                RefreshInstalled("✓ " + result.Message);
            });
        }
    }

    // The outfit picker: the fighter's extra outfits as rows (name, the outfit slot it's built on). An outfit built on
    // another slot than the files' is shown muted with the reason and can't be picked.
    internal sealed class AddPartsForm : Form
    {
        private readonly OutfitRows rows;
        private readonly Label summary;
        private readonly FlatButton addButton;
        internal InstalledOutfit Target { get { return rows.Selected; } }

        internal static bool Choose(IWin32Window owner, string modName, string options, int fileCount, string character, HashSet<string> fileFolders, List<InstalledOutfit> outfits, out InstalledOutfit target)
        {
            target = null;
            if (Program.Headless) return false;
            using (var form = new AddPartsForm(modName, options, fileCount, character, fileFolders, outfits))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return false;
                target = form.Target;
                return target != null;
            }
        }

        internal AddPartsForm(string modName, string options, int fileCount, string character, HashSet<string> fileFolders, List<InstalledOutfit> outfits)
        {
            const int rowHeight = 52, visibleRows = 6;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Add to an installed outfit"; StartPosition = FormStartPosition.CenterParent;
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            var title = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoEllipsis = true, Text = options };
            var source = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 22, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, Text = ReplacePlanner.N(fileCount, "file") + " from " + modName };
            var note = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 6, 0, 0), Text = "Copied into the outfit you pick; its files with the same names are replaced. Edit options and reinstalls of that outfit keep them." };
            var caption = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, Text = character.ToUpperInvariant() + " OUTFITS", TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4) };
            rows = new OutfitRows(rowHeight) { Dock = DockStyle.Top, Name = "addPartsOutfits", AccessibleName = character + " outfits", Empty = "No " + character + " outfit installed by the Manager yet. Install one first." };
            foreach (InstalledOutfit o in outfits)
            {
                List<string> built = Installer.OutfitFolders(o);
                string mismatch = Installer.FolderMismatch(fileFolders, built);
                string slot = built.Count == 0 ? "No outfit body" : String.Join(", ", built.Select(f => "Outfit " + Int32.Parse(f)));
                rows.Add(o, o.Name, slot + (o.Enabled ? "" : " · disabled") + (mismatch == null ? "" : " · these files are for " + mismatch), mismatch == null);
            }
            var listHost = new Panel { Dock = DockStyle.Top, Height = rowHeight * Math.Max(1, Math.Min(visibleRows, outfits.Count)) + 2, AutoScroll = outfits.Count > visibleRows, BackColor = Palette.Background };
            rows.Height = rowHeight * Math.Max(1, outfits.Count);
            listHost.Controls.Add(rows);
            summary = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 14, 0, 0) };
            addButton = new FlatButton("Add " + ReplacePlanner.N(fileCount, "file"), true) { Width = 150, Dock = DockStyle.Right, Font = Palette.Small, Name = "addPartsAdd" }; addButton.Click += delegate { Accept(); };
            var cancel = new FlatButton("Cancel", false) { Width = 100, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(summary); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(addButton);
            Controls.Add(footer); Controls.Add(listHost); Controls.Add(caption); Controls.Add(note); Controls.Add(source); Controls.Add(title);
            ClientSize = new Size(520, Padding.Vertical + title.Height + source.Height + note.Height + caption.Height + listHost.Height + footer.Height);
            rows.SelectionChanged += delegate { UpdateSummary(); };
            rows.Activated += delegate { Accept(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } else if (e.KeyCode == Keys.Enter && addButton.Enabled) { e.Handled = true; Accept(); } };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            rows.SelectFirstUsable();
            ActiveControl = rows;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            InstalledOutfit target = Target;
            addButton.Enabled = target != null;
            summary.Text = target == null ? (rows.Count == 0 ? "" : "Choose an outfit.") : "Into " + target.Name;
        }

        private void Accept()
        {
            if (!addButton.Enabled) return;
            DialogResult = DialogResult.OK; Close();
        }

        // Rows drawn like the library list: name, then the slot line; the chosen row has the selection colour and an
        // accent rail. Click or Up/Down choose (only rows that can take the files), double-click or Enter adds.
        internal sealed class OutfitRows : Control
        {
            private sealed class Row { internal InstalledOutfit Outfit; internal string Title, Detail; internal bool Usable; }
            private readonly List<Row> items = new List<Row>();
            private readonly int logicalRow;
            private int selected = -1, hover = -1;
            internal string Empty = "";
            internal event EventHandler SelectionChanged, Activated;
            internal OutfitRows(int rowHeight)
            {
                logicalRow = rowHeight;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true; BackColor = Palette.Background;
            }
            internal int Count { get { return items.Count; } }
            internal InstalledOutfit Selected { get { return selected >= 0 && selected < items.Count && items[selected].Usable ? items[selected].Outfit : null; } }
            internal void Add(InstalledOutfit outfit, string title, string detail, bool usable) { items.Add(new Row { Outfit = outfit, Title = title ?? "", Detail = detail ?? "", Usable = usable }); Invalidate(); }
            internal void SelectFirstUsable() { int i = items.FindIndex(r => r.Usable); if (i >= 0) Select(i); }
            private int RowHeight { get { return DpiMetrics.Px(this, logicalRow); } }
            private int IndexAt(int y) { int i = y / Math.Max(1, RowHeight); return i >= 0 && i < items.Count ? i : -1; }
            private void Select(int index)
            {
                if (index < 0 || index >= items.Count || !items[index].Usable || index == selected) return;
                selected = index; Invalidate();
                // Keep the row in view when the host scrolls.
                var host = Parent as ScrollableControl;
                if (host != null && host.AutoScroll)
                {
                    int top = index * RowHeight, view = -host.AutoScrollPosition.Y;
                    if (top < view) host.AutoScrollPosition = new Point(0, top);
                    else if (top + RowHeight > view + host.ClientSize.Height) host.AutoScrollPosition = new Point(0, top + RowHeight - host.ClientSize.Height);
                }
                var handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty);
            }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) Select(IndexAt(e.Y)); }
            protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); int i = IndexAt(e.Y); if (i >= 0 && i == selected) { var handler = Activated; if (handler != null) handler(this, EventArgs.Empty); } }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = IndexAt(e.Y); if (h != hover) { hover = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
            protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                int step = e.KeyCode == Keys.Up || e.KeyCode == Keys.W ? -1 : e.KeyCode == Keys.Down || e.KeyCode == Keys.S ? 1 : 0;
                if (step == 0) return;
                for (int i = selected + step; i >= 0 && i < items.Count; i += step) if (items[i].Usable) { Select(i); break; }
                e.Handled = true;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics; int h = RowHeight;
                using (Brush bg = new SolidBrush(Palette.Background)) g.FillRectangle(bg, ClientRectangle);
                const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                if (items.Count == 0) { TextRenderer.DrawText(g, Empty, Palette.Small, new Rectangle(DpiMetrics.Px(this, 12), 0, Width - DpiMetrics.Px(this, 24), Height), Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix); return; }
                for (int i = 0; i < items.Count; i++)
                {
                    var r = new Rectangle(0, i * h, Width, h); if (!r.IntersectsWith(e.ClipRectangle)) continue;
                    Row row = items[i]; bool on = i == selected;
                    Color fill = on ? Palette.Selected : i == hover && row.Usable ? Palette.Raised : Palette.Background;
                    using (Brush b = new SolidBrush(fill)) g.FillRectangle(b, r);
                    using (Brush rail = new SolidBrush(on ? Palette.Accent : Palette.Border)) g.FillRectangle(rail, r.Left + DpiMetrics.Px(this, 2), r.Top + DpiMetrics.Px(this, 10), DpiMetrics.Px(this, 3), h - DpiMetrics.Px(this, 20));
                    int x = r.Left + DpiMetrics.Px(this, 14), w = r.Width - DpiMetrics.Px(this, 24);
                    TextRenderer.DrawText(g, row.Title, Palette.Strong, new Rectangle(x, r.Top + DpiMetrics.Px(this, 6), w, DpiMetrics.Px(this, 22)), row.Usable ? Palette.Text : Palette.Muted, one);
                    TextRenderer.DrawText(g, row.Detail, Palette.Tiny, new Rectangle(x, r.Top + DpiMetrics.Px(this, 28), w, DpiMetrics.Px(this, 18)), Palette.Muted, one);
                    if (i < items.Count - 1) using (var pen = new Pen(Palette.Border)) g.DrawLine(pen, r.Left + DpiMetrics.Px(this, 12), r.Bottom - 1, r.Right - DpiMetrics.Px(this, 12), r.Bottom - 1);
                }
                if (Focused && ShowFocusCues && selected >= 0) using (var pen = new Pen(Palette.Text)) g.DrawRectangle(pen, 0, selected * h, Width - 1, h - 1);
            }
        }
    }
}
