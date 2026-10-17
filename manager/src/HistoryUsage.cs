using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 2.4.1 "last played", part (b): SlotsLoader writes one "=== SF6 Costume Slot Loader ===" run per game launch to
    // SF6_CostumeLoader.log in the game folder. The Manager reads it READ-ONLY and incrementally (byte offset + the
    // hash of the bytes before it, kept in Data\history-loader-log.json) and records one "launch" event per run with
    // the extra outfits SlotsLoader built, plus the replacements and tool mods that were installed and enabled then.
    public sealed class LoaderLogState
    {
        public string Format = "sf6-history-loader-log-v1";
        public string Log = "";                 // the log it describes (a different game folder restarts)
        public long Offset;                     // bytes read (the end of the last complete run)
        public string Head = "";                // "<n>:<sha256 of the first n bytes>" (n <= 4096): a rewritten log restarts
        public string LastRunUtc = "";          // the newest run recorded; older runs are never recorded twice
        public int LastSlots;                   // the last run that built slots: an "Up to date" run reuses them
        public List<string> LastOutfits = new List<string>();
        public int Launches, SlotUses, Restarts;
    }

    internal sealed class LoaderLogRun
    {
        internal DateTime Local, Utc;
        internal bool UpToDate, Built;
        internal int SlotsBuilt, SlotsLeftOut;
        internal int Slots { get { return Math.Max(0, SlotsBuilt - SlotsLeftOut); } }
        internal readonly List<string> Outfits = new List<string>();   // "Character/Folder or archive", in log order
    }

    internal sealed class LoaderLogResult
    {
        internal int Runs, Recorded, SlotUses, Skipped, Mapped, Unmapped;
        internal bool Restarted, Missing;
        internal long Offset;
        internal List<HistoryEvent> Events = new List<HistoryEvent>();
        internal string Summary { get { return Missing ? "no Slotsloader log" : Runs + " runs read, " + Recorded + " launches recorded, " + SlotUses + " slot-uses" + (Restarted ? " (log rewritten: read again from the start)" : ""); } }
    }

    internal static class LoaderLogBackfill
    {
        internal const string LogName = "SF6_CostumeLoader.log", StateName = "history-loader-log.json";
        private static readonly Regex Stamp = new Regex(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] ?(.*)$", RegexOptions.Compiled);
        private static readonly Regex Entry = new Regex(@"^  (folder|archive): (.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex Option = new Regex(@"^  option: (.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex Name = new Regex(@"^    name: (.+?)(  \(add-on for .+\))?\s*$", RegexOptions.Compiled);
        private static readonly Regex Output = new Regex(@"^    -> esf(\d{3}) costume \d+", RegexOptions.Compiled);
        private static readonly Regex Variant = new Regex(@"^  variant: (.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex Ignored = new Regex(@"^  add-on (.+?): no costume it can complete was found, ignored", RegexOptions.Compiled);
        private static readonly Regex SlotBuilt = new Regex(@"^    slot esf(\d{3})/\d{3} \(v\d+", RegexOptions.Compiled);
        private static readonly Regex LeftOut = new Regex(@"^  WARN: slot esf(\d{3})/v\d+ .*left out", RegexOptions.Compiled);
        internal static readonly TimeSpan Settled = TimeSpan.FromMinutes(5);   // a last run without its end line counts after this

        private sealed class RunParse
        {
            internal LoaderLogRun Run = new LoaderLogRun();
            internal string Current;                               // entry of the current option/folder
            internal List<string> CurrentVariant;
            internal readonly List<string> Entries = new List<string>();
            internal readonly Dictionary<string, string> OptionEntry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, HashSet<string>> NameEntries = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, HashSet<string>> Outputs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, int> Built = new Dictionary<string, int>(), Left = new Dictionary<string, int>();
            internal bool Ended;
        }

        private static string EntryOf(string path)
        {
            string[] parts = (path ?? "").Replace('\\', '/').Split('/');
            return parts.Length >= 2 ? parts[0] + "/" + parts[1] : path ?? "";
        }
        private static void AddOutput(RunParse p, string entry, string code)
        {
            if (String.IsNullOrEmpty(entry)) return;
            HashSet<string> codes; if (!p.Outputs.TryGetValue(entry, out codes)) { codes = new HashSet<string>(); p.Outputs[entry] = codes; }
            codes.Add(code);
        }
        // A variant names its base option by path ("Char/Mod.zip/01 Main - Variant") or by name ("Base + Add-on").
        private static List<string> VariantEntries(RunParse p, string label)
        {
            var found = new List<string>();
            string path = p.Entries.Where(e => label.Equals(e, StringComparison.OrdinalIgnoreCase) || label.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase) || label.StartsWith(e + " - ", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length).FirstOrDefault();
            if (path != null) found.Add(path);
            foreach (string part in label.Split(new[] { " + " }, StringSplitOptions.None))
            {
                HashSet<string> entries;
                if (p.NameEntries.TryGetValue(part.Trim(), out entries)) found.AddRange(entries);
                else
                {
                    string name = p.NameEntries.Keys.Where(n => part.StartsWith(n + " - ", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n.Length).FirstOrDefault();
                    if (name != null) found.AddRange(p.NameEntries[name]);
                }
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void Line(RunParse p, string content)
        {
            Match m;
            if ((m = Entry.Match(content)).Success) { string e = EntryOf(m.Groups[2].Value); p.Current = e; p.CurrentVariant = null; if (!p.Entries.Contains(e, StringComparer.OrdinalIgnoreCase)) p.Entries.Add(e); return; }
            if ((m = Option.Match(content)).Success) { string e = EntryOf(m.Groups[1].Value); p.Current = e; p.CurrentVariant = null; p.OptionEntry[m.Groups[1].Value] = e; if (!p.Entries.Contains(e, StringComparer.OrdinalIgnoreCase)) p.Entries.Add(e); return; }
            if ((m = Variant.Match(content)).Success) { p.CurrentVariant = VariantEntries(p, m.Groups[1].Value); p.Current = null; return; }
            if ((m = Name.Match(content)).Success)
            {
                if (p.Current == null) return;
                string n = m.Groups[1].Value.Trim(); HashSet<string> set;
                if (!p.NameEntries.TryGetValue(n, out set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); p.NameEntries[n] = set; }
                set.Add(p.Current); return;
            }
            if ((m = Output.Match(content)).Success)
            {
                string code = m.Groups[1].Value;
                if (p.CurrentVariant != null) foreach (string e in p.CurrentVariant) AddOutput(p, e, code);
                else AddOutput(p, p.Current, code);
                return;
            }
            if ((m = SlotBuilt.Match(content)).Success) { int n; p.Built.TryGetValue(m.Groups[1].Value, out n); p.Built[m.Groups[1].Value] = n + 1; p.Run.Built = true; return; }
            if ((m = LeftOut.Match(content)).Success) { int n; p.Left.TryGetValue(m.Groups[1].Value, out n); p.Left[m.Groups[1].Value] = n + 1; return; }
            if (Ignored.IsMatch(content)) return;
            if (content.StartsWith("Up to date", StringComparison.Ordinal)) { p.Run.UpToDate = true; p.Ended = true; return; }
            if (content.StartsWith("Done in ", StringComparison.Ordinal)) { p.Ended = true; return; }
            if (content.StartsWith("Assigning slots", StringComparison.Ordinal) || content.StartsWith("Assembling output", StringComparison.Ordinal)) { p.Current = null; p.CurrentVariant = null; }
        }

        private static LoaderLogRun Finish(RunParse p)
        {
            LoaderLogRun run = p.Run;
            run.SlotsBuilt = p.Built.Values.Sum(); run.SlotsLeftOut = p.Left.Values.Sum();
            var kept = new HashSet<string>(p.Built.Where(kv => { int l; p.Left.TryGetValue(kv.Key, out l); return kv.Value > l; }).Select(kv => kv.Key));
            foreach (string entry in p.Entries)
            {
                HashSet<string> codes;
                if (!p.Outputs.TryGetValue(entry, out codes) || !codes.Any(kept.Contains)) continue;
                string character = entry.Split('/')[0];
                if (!Characters.Names.Contains(character)) continue;   // e.g. a stray natives folder at the top of costume_mods
                run.Outfits.Add(entry);
            }
            return run;
        }

        // Complete runs in data[0..length). `consumed` is the end of the last complete run (the next read starts
        // there). The last run counts as complete when it has its end line, or once the log is `Settled` old.
        internal static List<LoaderLogRun> Parse(byte[] data, int length, bool settled, out int consumed)
        {
            var runs = new List<LoaderLogRun>(); consumed = 0;
            var utf8 = new UTF8Encoding(false);
            int lineStart = 0, runStart = -1; RunParse current = null; DateTime last = DateTime.MinValue;
            int end = Math.Min(length, data.Length);
            int complete = end; while (complete > 0 && data[complete - 1] != (byte)'\n') complete--;
            for (int i = 0; i <= complete; i++)
            {
                if (i < complete && data[i] != (byte)'\n') continue;
                if (i == complete) break;
                string line = utf8.GetString(data, lineStart, i - lineStart).TrimEnd('\r');
                int thisStart = lineStart; lineStart = i + 1;
                Match m = Stamp.Match(line); string content = line;
                if (m.Success)
                {
                    DateTime t;
                    if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t)) last = t;
                    content = m.Groups[2].Value;
                    if (content.StartsWith("=== SF6 Costume Slot Loader ===", StringComparison.Ordinal))
                    {
                        if (current != null) { runs.Add(Finish(current)); consumed = thisStart; }
                        current = new RunParse(); current.Run.Local = last; current.Run.Utc = last.ToUniversalTime(); runStart = thisStart;
                        continue;
                    }
                }
                if (current == null) { consumed = lineStart; continue; }   // text before the first run
                Line(current, content);
                if (current.Ended) { runs.Add(Finish(current)); current = null; consumed = lineStart; }
            }
            if (current != null && settled) { runs.Add(Finish(current)); consumed = complete; }
            else if (current == null && complete > consumed) consumed = complete;
            return runs;
        }

        private static string HeadOf(FileStream s, long count)
        {
            long n = Math.Min(Math.Min(4096, s.Length), count);
            var b = new byte[n]; s.Position = 0; int read = 0;
            while (read < n) { int r = s.Read(b, read, (int)n - read); if (r <= 0) break; read += r; }
            using (var sha = FastHash.Sha256()) return read + ":" + BitConverter.ToString(sha.ComputeHash(b, 0, read)).Replace("-", "");
        }

        internal static LoaderLogState LoadState(string dataDir)
        {
            try { string f = Path.Combine(dataDir, StateName); if (File.Exists(f)) { var s = Json.Read<LoaderLogState>(f); if (s != null && s.Format == "sf6-history-loader-log-v1") { if (s.LastOutfits == null) s.LastOutfits = new List<string>(); return s; } } }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return new LoaderLogState();
        }

        // Reads what SlotsLoader appended since the last call and records the new launches. Read-only on the game
        // folder; `installed` and `replacements` are the current extra outfits and Manager records (the fallback when
        // the history does not cover a run's time).
        internal static LoaderLogResult Run(string gameRoot, string dataDir, List<InstalledOutfit> installed, List<ReplaceMod> replacements, DateTime nowUtc)
        {
            var result = new LoaderLogResult();
            string log = Path.Combine(gameRoot ?? "", LogName);
            if (String.IsNullOrEmpty(gameRoot) || !File.Exists(log)) { result.Missing = true; return result; }
            LoaderLogState state = LoadState(dataDir);
            string logKey = History.Normalize(log);
            byte[] data; long start;
            using (var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bool same = String.Equals(state.Log, logKey, StringComparison.OrdinalIgnoreCase) && state.Offset > 0 && s.Length >= state.Offset;
                if (same) { int n; string[] head = state.Head.Split(':'); same = head.Length == 2 && Int32.TryParse(head[0], out n) && HeadOf(s, n) == state.Head; }
                if (!same && state.Offset > 0) { result.Restarted = true; state.Restarts++; }
                start = same ? state.Offset : 0;
                long length = s.Length - start;
                if (length > 256L * 1024 * 1024) { start = s.Length - 256L * 1024 * 1024; length = s.Length - start; }   // a runaway log: the newest part only
                data = new byte[length]; s.Position = start; int read = 0;
                while (read < length) { int r = s.Read(data, read, (int)(length - read)); if (r <= 0) break; read += r; }
                if (read < length) Array.Resize(ref data, read);
                bool settled = nowUtc - File.GetLastWriteTimeUtc(log) > Settled;
                int consumed;
                List<LoaderLogRun> runs = Parse(data, data.Length, settled, out consumed);
                state.Log = logKey; state.Offset = start + consumed; state.Head = HeadOf(s, state.Offset);
                result.Runs = runs.Count; result.Offset = state.Offset;
                DateTime lastRun; bool haveLast = DateTime.TryParse(state.LastRunUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastRun);
                HistoryIndex index = HistoryStore.For(dataDir).Index();
                List<HistoryEvent> timeline = HistoryStore.For(dataDir).Events();
                foreach (LoaderLogRun run in runs)
                {
                    if (!run.UpToDate && run.Built) { state.LastSlots = run.Slots; state.LastOutfits = run.Outfits.ToList(); }
                    List<string> outfits = run.UpToDate ? state.LastOutfits.ToList() : run.Outfits;
                    int slots = run.UpToDate ? state.LastSlots : run.Slots;
                    if (haveLast && run.Utc <= lastRun.ToUniversalTime()) { result.Skipped++; continue; }
                    var mods = new List<HistoryMod>();
                    foreach (string entry in outfits) { HistoryMod m = MapOutfit(entry, run.Utc, timeline, installed); if (m.Source.Length > 0) result.Mapped++; else result.Unmapped++; mods.Add(m); }
                    mods.AddRange(ActiveReplacements(run.Utc, timeline, replacements));
                    result.Events.Add(new HistoryEvent { Utc = run.Utc, Kind = HistoryKinds.Launch, Via = "loader_log", Slots = slots, Mods = mods, Detail = run.UpToDate ? "up to date" : run.Built ? "built" : "no slots" });
                    result.Recorded++; result.SlotUses += slots;
                    state.LastRunUtc = run.Utc.ToString("o", CultureInfo.InvariantCulture); lastRun = run.Utc; haveLast = true;
                }
            }
            if (result.Events.Count > 0) HistoryStore.For(dataDir).Append(result.Events);
            state.Launches += result.Recorded; state.SlotUses += result.SlotUses;
            Directory.CreateDirectory(dataDir);
            Json.Write(Path.Combine(dataDir, StateName), state);
            return result;
        }

        // The extra outfit in costume_mods/<entry> at `utc`: the latest install/update/recover/enable the history
        // has for that folder at or before then (still installed), else the outfit there now if it was installed by
        // then; a manual outfit (or one that predates the history) is kept by its folder name.
        internal static HistoryMod MapOutfit(string entry, DateTime utc, List<HistoryEvent> timeline, List<InstalledOutfit> installed)
        {
            string[] parts = entry.Split('/');
            var mod = new HistoryMod { Mode = HistoryModes.Extra, Character = parts[0], Slot = entry, Name = parts.Length > 1 ? Path.GetFileNameWithoutExtension(parts[1]) : entry };
            HistoryEvent last = null;
            foreach (HistoryEvent e in timeline)
            {
                if (e.Mode != HistoryModes.Extra || !String.Equals(e.Slot, entry, StringComparison.OrdinalIgnoreCase) || e.Utc > utc.AddMinutes(1)) continue;
                if (last == null || e.Utc >= last.Utc) last = e;
            }
            if (last != null && (last.Kind == HistoryKinds.Install || last.Kind == HistoryKinds.Update || last.Kind == HistoryKinds.Recover || last.Kind == HistoryKinds.Enable) && last.Source.Length > 0)
            { mod.Source = last.Source; mod.Name = last.Name; return mod; }
            if (last != null) return mod;   // uninstalled or disabled by then
            InstalledOutfit now = (installed ?? new List<InstalledOutfit>()).FirstOrDefault(o => o.Enabled && String.Equals(o.Character + "/" + Path.GetFileName(o.Folder), entry, StringComparison.OrdinalIgnoreCase));
            if (now != null && now.Managed && !String.IsNullOrEmpty(now.SourcePath) && (now.InstalledUtc == DateTime.MinValue || now.InstalledUtc <= utc.AddMinutes(1)))
            { mod.Source = History.Normalize(now.SourcePath); mod.Name = now.Name; }
            return mod;
        }

        // Replacements and tool mods count as used when installed and enabled at that time: the history's last
        // state change for the record before `utc`, else (before the history) the current record when it was
        // installed by then.
        internal static List<HistoryMod> ActiveReplacements(DateTime utc, List<HistoryEvent> timeline, List<ReplaceMod> current)
        {
            var result = new List<HistoryMod>();
            var states = new Dictionary<string, HistoryEvent>(StringComparer.OrdinalIgnoreCase);
            foreach (HistoryEvent e in timeline)
            {
                if ((e.Mode != HistoryModes.Replace && e.Mode != HistoryModes.Tool) || String.IsNullOrEmpty(e.Id) || e.Utc > utc) continue;
                if (e.Kind == HistoryKinds.Update && !String.IsNullOrEmpty(e.PrevId)) states.Remove(e.PrevId);
                if (e.Kind == HistoryKinds.Install || e.Kind == HistoryKinds.Update || e.Kind == HistoryKinds.Recover || e.Kind == HistoryKinds.Enable || e.Kind == HistoryKinds.Disable || e.Kind == HistoryKinds.Uninstall) states[e.Id] = e;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HistoryEvent e in states.Values)
            {
                seen.Add(e.Id);
                if (e.Kind == HistoryKinds.Disable || e.Kind == HistoryKinds.Uninstall || e.Source.Length == 0) continue;
                result.Add(new HistoryMod { Source = e.Source, Name = e.Name, Mode = e.Mode, Character = e.Character, Slot = e.Slot });
            }
            bool covered = timeline.Any(e => (e.Mode == HistoryModes.Replace || e.Mode == HistoryModes.Tool) && e.Utc <= utc);
            foreach (ReplaceMod m in current ?? new List<ReplaceMod>())
            {
                if (seen.Contains(m.Id ?? "") || !m.Enabled || String.IsNullOrEmpty(m.SourcePath)) continue;
                if (timeline.Any(e => String.Equals(e.Id, m.Id, StringComparison.OrdinalIgnoreCase))) continue;   // the history knows when it arrived
                DateTime installedAt = LibraryUsage.ParseUtc(m.InstalledUtc);
                if (installedAt == DateTime.MinValue || installedAt > utc) continue;
                if (covered && installedAt > utc) continue;
                HistoryEvent shape = History.ForReplacement(HistoryKinds.Install, m);
                result.Add(new HistoryMod { Source = History.Normalize(m.SourcePath), Name = shape.Name, Mode = shape.Mode, Character = shape.Character, Slot = shape.Slot });
            }
            return result;
        }
    }

    // 2.4.1 "last played", part (a): while the Manager is open, the SF6 process probe's transitions are game
    // sessions. A start records the active installed set; a stop records how long it ran.
    internal sealed class HistorySessions
    {
        private readonly string dataDir;
        private bool running;
        private DateTime startUtc;
        private DateTime? gameStart;
        internal bool InSession { get { return running; } }
        internal HistorySessions(string dataDir) { this.dataDir = dataDir; }

        // `active` is called only on a start (enabled extra outfits, enabled replacements and tool mods). Returns
        // true when a session started or stopped (the caller reads the loader log again after a stop).
        internal bool Poll(bool gameRunning, Func<List<HistoryMod>> active, Func<DateTime?> processStart, DateTime nowUtc)
        {
            if (gameRunning == running) return false;
            running = gameRunning;
            if (gameRunning)
            {
                startUtc = nowUtc; gameStart = null;
                try { if (processStart != null) gameStart = processStart(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                List<HistoryMod> mods;
                try { mods = active == null ? new List<HistoryMod>() : active() ?? new List<HistoryMod>(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); mods = new List<HistoryMod>(); }
                History.Record(dataDir, new HistoryEvent { Utc = nowUtc, Kind = HistoryKinds.SessionStart, Via = "probe", Mods = mods, Start = gameStart, Detail = ReplacePlanner.N(mods.Count, "active mod") });
            }
            else History.Record(dataDir, new HistoryEvent { Utc = nowUtc, Kind = HistoryKinds.SessionStop, Via = "probe", Start = gameStart ?? startUtc, Detail = Duration(nowUtc - (gameStart ?? startUtc)) });
            return true;
        }
        // The Manager closes while SF6 runs: the session's end is not known.
        internal void Close(DateTime nowUtc)
        {
            if (!running) return;
            running = false;
            History.Record(dataDir, new HistoryEvent { Utc = nowUtc, Kind = HistoryKinds.SessionStop, Via = "manager_closed", Start = gameStart ?? startUtc, Detail = "Manager closed while SF6 ran" });
        }
        internal static string Duration(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalMinutes < 1 ? "under a minute" : t.TotalHours < 1 ? (int)t.TotalMinutes + " min" : (int)t.TotalHours + " h " + t.Minutes + " min";
        }
        // The real StreetFighter6 process's start time (only with the real probe; tests have none).
        internal static DateTime? GameProcessStart()
        {
            if (!(GameProcesses.Probe is SystemProcessProbe)) return null;
            DateTime? start = null;
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(SystemProcessProbe.GameNames[0]))
            {
                try { DateTime t = p.StartTime.ToUniversalTime(); if (!start.HasValue || t < start.Value) start = t; }
                catch (Exception ex) { if (!(ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)) throw; }
                finally { p.Dispose(); }
            }
            return start;
        }
    }
}
