using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.1: the × inside a search field (Mod library, Installed, History): shown while the field has text; a
    // click clears it, runs the page's refresh and puts the focus back in the field. Esc does the same from the
    // keyboard (ShortcutAction.ClearSearch). Accessible name "Clear search"; no tooltip.
    internal static class SearchClear
    {
        internal static Label Attach(PaddedTextField field, Action cleared)
        {
            var clear = new Label { UseMnemonic = false, Text = "×", Font = new Font("Segoe UI", 12F, FontStyle.Bold), ForeColor = Palette.Muted, BackColor = Palette.Raised, Width = 26, Height = 24, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, AccessibleName = "Clear search", AccessibleRole = AccessibleRole.PushButton, Visible = false };
            clear.Parent = field; if (field.TrailingSpace < 27) field.TrailingSpace = 27;   // the text stops before the ×
            clear.Click += delegate { field.Clear(); if (cleared != null) cleared(); field.Focus(); };
            clear.MouseEnter += delegate { clear.ForeColor = Palette.Text; };
            clear.MouseLeave += delegate { clear.ForeColor = Palette.Muted; };
            Action position = delegate
            {
                clear.Size = new Size(DpiMetrics.Px(field, 26), DpiMetrics.Px(field, 24));
                clear.Location = new Point(Math.Max(0, field.ClientSize.Width - clear.Width - DpiMetrics.Px(field, 2)), Math.Max(0, (field.ClientSize.Height - clear.Height) / 2));
            };
            field.Resize += delegate { position(); };
            field.DpiChangedAfterParent += delegate { position(); };
            clear.VisibleChanged += delegate { if (clear.Visible) position(); };
            field.TextChanged += delegate { clear.Visible = field.TextLength > 0; };
            clear.BringToFront();
            return clear;
        }
    }

    internal sealed partial class MainForm
    {
        private Label installedSearchClear;

        // 2.4.1: the Installed page's search uses the Mod library's matching (LibrarySearch): character names and
        // aliases ("aki" = A.K.I, "viper"), a unique start of a name ("cam"), "name, word" terms, costume terms
        // ("cammy, c1") and is:tool. Each row is matched as its library entry (when the source is still in a
        // library) and as itself (row name, character and the costume folders its files touch).
        private sealed class InstalledQuery
        {
            internal LibrarySearch.Query Query; internal HashSet<string> Codes; internal bool Empty;
            internal Dictionary<string, LibraryEntry> Library;
        }
        private InstalledQuery ParseInstalledSearch()
        {
            string text = installedSearch == null ? "" : installedSearch.Text.Trim();
            var q = new InstalledQuery { Empty = text.Length == 0 };
            if (q.Empty) return q;
            q.Query = LibrarySearch.Parse(text, SearchCharacterTerm);
            q.Codes = LibrarySearch.CodesFor(q.Query.Characters);
            q.Library = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (LibraryEntry e in library) if (!String.IsNullOrEmpty(e.SourcePath) && !q.Library.ContainsKey(e.SourcePath)) q.Library[e.SourcePath] = e;
            return q;
        }
        private static bool InstalledEntryMatches(LibraryEntry e, InstalledQuery q)
        {
            if (!LibrarySearch.MatchesText(e, q.Query, EntryMatchesCharacter, EntryMatchesName)) return false;
            if (LibrarySearch.CostumeRank(e, q.Query, q.Codes, EntryMatchesCharacter) < 0) return false;
            return MatchesModType(e, q.Query.Type) && MatchesFormat(e, q.Query.Format);
        }
        private static readonly Regex CostumeFolder = new Regex(@"/esf/esf(\d{3})/(\d{3})/", RegexOptions.IgnoreCase);
        internal static LibraryEntry InstalledRowEntry(string name, string character, string sourcePath, IEnumerable<string> files, bool tool)
        {
            var e = new LibraryEntry { Name = name ?? "", Character = character ?? "", SourcePath = sourcePath ?? "", Kind = "Normal" };
            foreach (string file in files ?? new string[0])
            {
                Match m = CostumeFolder.Match(file ?? "");
                if (!m.Success) continue;
                string key = m.Groups[1].Value + "/" + m.Groups[2].Value;
                if (!e.CostumeKeys.Contains(key)) e.CostumeKeys.Add(key);
                if (!e.CharacterCodes.Contains(m.Groups[1].Value)) e.CharacterCodes.Add(m.Groups[1].Value);
            }
            if (!String.IsNullOrEmpty(character))
                foreach (var kv in Characters.ByCode) if (String.Equals(kv.Value, character, StringComparison.OrdinalIgnoreCase) && !e.CharacterCodes.Contains(kv.Key)) e.CharacterCodes.Add(kv.Key);
            if (tool) e.ToolKinds.Add(Library.ToolScript);
            return e;
        }
        private bool InstalledRowMatches(InstalledQuery q, string name, string character, string sourcePath, IEnumerable<string> files, bool tool)
        {
            if (q.Empty) return true;
            LibraryEntry own;
            if (!String.IsNullOrEmpty(sourcePath) && q.Library.TryGetValue(sourcePath, out own) && InstalledEntryMatches(own, q)) return true;
            return InstalledEntryMatches(InstalledRowEntry(name, character, sourcePath, files, tool), q);
        }
        private bool OutfitMatchesSearch(InstalledQuery q, InstalledOutfit o)
        {
            return InstalledRowMatches(q, o.Name, o.Character, o.SourcePath, null, false);
        }
        private bool GameRowMatchesSearch(InstalledQuery q, GameModRow r)
        {
            if (r.Mod != null) return InstalledRowMatches(q, r.Name, null, r.Mod.SourcePath, r.Mod.Files.Select(f => f.Path), r.Kind == "Tool");
            if (r.Fluffy != null) return InstalledRowMatches(q, r.Name, null, null, r.Fluffy.Files, false);
            return InstalledRowMatches(q, r.Name, null, null, null, false);
        }
    }
}
