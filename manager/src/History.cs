using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // 2.4.1: install/usage history. Data\history.jsonl is append-only: one JSON event per line, written by every
    // process that changes the game (window, CLI, Assistant) and never sent anywhere. Past a size cap the oldest
    // events are folded into one summary line per mod, so per-mod totals survive (HistoryStore.Compact).
    internal static class HistoryKinds
    {
        internal const string Install = "install", Update = "update", Uninstall = "uninstall", Enable = "enable", Disable = "disable",
            Reorder = "reorder", Recover = "recover", Undo = "undo", FluffyUninstall = "fluffy_uninstall",
            SessionStart = "session_start", SessionStop = "session_stop", Launch = "launch", Summary = "summary",
            Rename = "rename";   // 1.0.5: a library source renamed in the Manager (Source = new path, PrevId = old path)
        internal static bool IsPlay(string kind) { return kind == SessionStart || kind == Launch; }
    }
    internal static class HistoryModes { internal const string Extra = "extra_outfit", Replace = "replace", Tool = "tool", Fluffy = "fluffy"; }

    // One mod that was active in a game session or launch.
    internal sealed class HistoryMod
    {
        internal string Source = "", Name = "", Mode = "", Character = "", Slot = "";
    }

    internal sealed class HistoryEvent
    {
        internal DateTime Utc;
        internal string Kind = "", Mode = "", Source = "", Stamp = "", Name = "", Mod = "", Character = "", Slot = "", Id = "", PrevId = "", Detail = "", Via = "";
        internal List<string> Options, PrevOptions;
        internal List<HistoryMod> Mods;                 // session_start / launch: the active installed set
        internal int Slots;                             // launch: slots SlotsLoader built (slot-uses); summary: folded slot-uses
        internal DateTime? Start;                       // session_stop: when it began; session_start: the game process start when known
        // summary (compaction): totals of the folded events of one mod, or (no source) the folded launches
        internal int Plays, Events, Launches;
        internal DateTime? FirstInstalled, LastInstalled, LastUpdated, LastUninstalled, LastPlayed;
        internal List<string> InstalledSlots, DisabledSlots;
        internal int Length;                            // bytes of its line when read (compaction)

        internal HistoryEvent Copy() { return (HistoryEvent)MemberwiseClone(); }
    }

    internal static class HistoryJson
    {
        internal const int Version = 1;
        private static string T(DateTime t) { return t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static void S(Dictionary<string, object> d, string key, string value) { if (!String.IsNullOrEmpty(value)) d[key] = value; }
        private static void L(Dictionary<string, object> d, string key, List<string> value) { if (value != null) d[key] = value.ToArray(); }
        private static void D(Dictionary<string, object> d, string key, DateTime? value) { if (value.HasValue) d[key] = T(value.Value); }

        internal static string Write(HistoryEvent e)
        {
            var d = new Dictionary<string, object>();
            d["v"] = Version; d["t"] = T(e.Utc); d["kind"] = e.Kind ?? "";
            S(d, "mode", e.Mode); S(d, "source", e.Source); S(d, "stamp", e.Stamp); S(d, "name", e.Name); S(d, "mod", e.Mod);
            S(d, "character", e.Character); S(d, "slot", e.Slot); S(d, "id", e.Id); S(d, "prev_id", e.PrevId);
            L(d, "options", e.Options); L(d, "prev_options", e.PrevOptions);
            S(d, "detail", e.Detail); S(d, "via", e.Via);
            if (e.Mods != null)
                d["mods"] = e.Mods.Select(m =>
                {
                    var x = new Dictionary<string, object>();
                    S(x, "source", m.Source); S(x, "name", m.Name); S(x, "mode", m.Mode); S(x, "character", m.Character); S(x, "slot", m.Slot);
                    return x;
                }).ToArray();
            if (e.Slots != 0) d["slots"] = e.Slots;
            D(d, "start", e.Start);
            if (e.Kind == HistoryKinds.Summary)
            {
                d["plays"] = e.Plays; d["events"] = e.Events;
                if (e.Launches != 0) d["launches"] = e.Launches;
                D(d, "first_installed", e.FirstInstalled); D(d, "last_installed", e.LastInstalled); D(d, "last_updated", e.LastUpdated);
                D(d, "last_uninstalled", e.LastUninstalled); D(d, "last_played", e.LastPlayed);
                L(d, "installed_slots", e.InstalledSlots); L(d, "disabled_slots", e.DisabledSlots);
            }
            return Json.Serializer().Serialize(d);
        }

        private static string Str(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        private static int Int(Dictionary<string, object> d, string key) { object v; if (!d.TryGetValue(key, out v) || v == null) return 0; try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch (Exception ex) { if (ex is FormatException || ex is InvalidCastException || ex is OverflowException) return 0; throw; } }
        private static DateTime? Date(Dictionary<string, object> d, string key)
        {
            string s = Str(d, key); DateTime t;
            return s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : (DateTime?)null;
        }
        private static IEnumerable<object> Items(Dictionary<string, object> d, string key)
        {
            object v; if (!d.TryGetValue(key, out v) || v == null) return null;
            var array = v as object[]; if (array != null) return array;
            var list = v as ArrayList; return list != null ? list.Cast<object>() : null;
        }
        private static List<string> Strings(Dictionary<string, object> d, string key)
        {
            IEnumerable<object> items = Items(d, key);
            return items == null ? null : items.Where(x => x != null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
        }

        // null for a line that is not a history event (it is skipped and counted).
        internal static HistoryEvent Read(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;
            Dictionary<string, object> d;
            try { d = Json.Serializer().DeserializeObject(line) as Dictionary<string, object>; }
            catch (Exception ex) { if (ex is ArgumentException || ex is InvalidOperationException) return null; throw; }
            if (d == null) return null;
            DateTime? t = Date(d, "t"); string kind = Str(d, "kind");
            if (!t.HasValue || kind.Length == 0) return null;
            var e = new HistoryEvent
            {
                Utc = t.Value, Kind = kind, Mode = Str(d, "mode"), Source = Str(d, "source"), Stamp = Str(d, "stamp"), Name = Str(d, "name"), Mod = Str(d, "mod"),
                Character = Str(d, "character"), Slot = Str(d, "slot"), Id = Str(d, "id"), PrevId = Str(d, "prev_id"), Detail = Str(d, "detail"), Via = Str(d, "via"),
                Options = Strings(d, "options"), PrevOptions = Strings(d, "prev_options"), Slots = Int(d, "slots"), Start = Date(d, "start"),
                Plays = Int(d, "plays"), Events = Int(d, "events"), Launches = Int(d, "launches"),
                FirstInstalled = Date(d, "first_installed"), LastInstalled = Date(d, "last_installed"), LastUpdated = Date(d, "last_updated"),
                LastUninstalled = Date(d, "last_uninstalled"), LastPlayed = Date(d, "last_played"),
                InstalledSlots = Strings(d, "installed_slots"), DisabledSlots = Strings(d, "disabled_slots")
            };
            IEnumerable<object> mods = Items(d, "mods");
            if (mods != null)
                e.Mods = mods.OfType<Dictionary<string, object>>().Select(x => new HistoryMod { Source = Str(x, "source"), Name = Str(x, "name"), Mode = Str(x, "mode"), Character = Str(x, "character"), Slot = Str(x, "slot") }).ToList();
            return e;
        }
    }

    // What the history says about one mod (a library source, a manual outfit or a Fluffy mod).
    internal sealed class ModHistory
    {
        internal string Key = "", Source = "", Name = "", Mode = "", Character = "", Slot = "";
        internal DateTime? FirstInstalled, LastInstalled, LastUpdated, LastUninstalled, LastPlayed, LastEvent;
        internal int Plays, Events;
        // What the history last saw installed (slots for extra outfits, record ids for replacements) and disabled.
        internal readonly HashSet<string> Slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Installed { get { return Slots.Count > 0; } }
        internal bool Enabled { get { return Slots.Any(s => !Disabled.Contains(s)); } }
        internal DateTime? LastInstallOrSwitch { get { return History.Max(LastInstalled, LastUpdated); } }
        // THE Last used definition: the last time it was played, else the last install or switch.
        internal DateTime? LastUsed { get { return LastPlayed ?? LastInstallOrSwitch; } }
    }

    // One game launch: the probe's session start and SlotsLoader's run for the same launch are merged.
    internal sealed class HistoryPlay
    {
        internal DateTime Utc;
        internal int Slots;
        internal bool Probe, Log;
        internal readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<HistoryEvent> Events = new List<HistoryEvent>();
    }

    internal sealed class HistoryIndex
    {
        internal static readonly TimeSpan SameLaunch = TimeSpan.FromMinutes(3);
        internal readonly Dictionary<string, ModHistory> Mods = new Dictionary<string, ModHistory>(StringComparer.OrdinalIgnoreCase);
        internal List<HistoryPlay> Plays = new List<HistoryPlay>();
        internal int FoldedLaunches, FoldedSlotUses;
        internal DateTime? Since;
        private readonly List<HistoryEvent> playEvents = new List<HistoryEvent>();
        private Dictionary<string, int> foldedPlays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> foldedLast = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // 1.0.5: renamed sources (old path -> new path). A mod's history follows its file; plays that list the old path count for the new one.
        private readonly Dictionary<string, string> renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string Resolve(string key)
        {
            string next; int hops = 0;
            while (!String.IsNullOrEmpty(key) && renamed.TryGetValue(key, out next) && hops++ < 32) key = next;
            return key;
        }

        internal int Launches { get { return FoldedLaunches + Plays.Count; } }
        internal int SlotUses { get { return FoldedSlotUses + Plays.Sum(p => p.Slots); } }

        internal static string Key(string source, string mode, string slot, string id)
        {
            if (!String.IsNullOrEmpty(source)) return source;
            if (mode == HistoryModes.Fluffy) return "fluffy:" + (slot ?? "");
            if (!String.IsNullOrEmpty(slot)) return "slot:" + slot;
            return String.IsNullOrEmpty(id) ? "" : "id:" + id;
        }
        internal static string Key(HistoryEvent e) { return Key(e.Source, e.Mode, e.Slot, e.Id); }
        internal static string Key(HistoryMod m) { return Key(m.Source, m.Mode, m.Slot, ""); }
        // Extra outfits are tracked by their costume_mods folder ("Cammy/Boots"), replacements by their record id.
        private static string StateKey(HistoryEvent e) { return e.Mode == HistoryModes.Extra ? e.Slot : String.IsNullOrEmpty(e.Id) ? e.Slot : e.Id; }

        internal ModHistory Find(string key) { ModHistory m; return key != null && Mods.TryGetValue(key, out m) ? m : null; }
        private ModHistory Get(string key, string source, string name, string mode, string character, string slot)
        {
            ModHistory m;
            if (!Mods.TryGetValue(key, out m)) { m = new ModHistory { Key = key }; Mods[key] = m; }
            if (!String.IsNullOrEmpty(source)) m.Source = source;
            if (!String.IsNullOrEmpty(name)) m.Name = name;
            if (!String.IsNullOrEmpty(mode)) m.Mode = mode;
            if (!String.IsNullOrEmpty(character)) m.Character = character;
            if (!String.IsNullOrEmpty(slot)) m.Slot = slot;
            return m;
        }

        internal static HistoryIndex Build(IEnumerable<HistoryEvent> events)
        {
            var index = new HistoryIndex();
            foreach (HistoryEvent e in events) index.ApplyState(e);
            index.RebuildPlays();
            return index;
        }

        // Applies one event; a play only marks the launches for RebuildPlays (they arrive out of order: the log
        // backfill appends older launches after newer sessions).
        internal bool Apply(HistoryEvent e) { ApplyState(e); return HistoryKinds.IsPlay(e.Kind); }

        private void ApplyState(HistoryEvent e)
        {
            if (e == null) return;
            if (!Since.HasValue || e.Utc < Since.Value) Since = e.Utc;
            if (HistoryKinds.IsPlay(e.Kind)) { playEvents.Add(e); return; }
            if (e.Kind == HistoryKinds.SessionStop) return;
            if (e.Kind == HistoryKinds.Undo) return;   // its own row in the timeline; the restore it runs has its own events
            if (e.Kind == HistoryKinds.Rename) { ApplyRename(e); return; }
            string key = Resolve(Key(e));
            if (e.Kind == HistoryKinds.Summary)
            {
                if (key.Length == 0) { FoldedLaunches += e.Launches; FoldedSlotUses += e.Slots; return; }
                ModHistory s = Get(key, e.Source, e.Name, e.Mode, e.Character, e.Slot);
                s.Events += e.Events;
                s.FirstInstalled = History.Min(s.FirstInstalled, e.FirstInstalled); s.LastInstalled = History.Max(s.LastInstalled, e.LastInstalled);
                s.LastUpdated = History.Max(s.LastUpdated, e.LastUpdated); s.LastUninstalled = History.Max(s.LastUninstalled, e.LastUninstalled);
                s.LastEvent = History.Max(s.LastEvent, e.Utc);
                s.Slots.Clear(); s.Disabled.Clear();
                if (e.InstalledSlots != null) s.Slots.UnionWith(e.InstalledSlots);
                if (e.DisabledSlots != null) s.Disabled.UnionWith(e.DisabledSlots);
                int plays; foldedPlays.TryGetValue(key, out plays); foldedPlays[key] = plays + e.Plays;
                if (e.LastPlayed.HasValue) { DateTime last; if (!foldedLast.TryGetValue(key, out last) || e.LastPlayed.Value > last) foldedLast[key] = e.LastPlayed.Value; }
                return;
            }
            if (key.Length == 0) return;
            ModHistory m = Get(key, e.Source, e.Name, e.Mode, e.Character, e.Slot);
            m.Events++; m.LastEvent = History.Max(m.LastEvent, e.Utc);
            string slot = StateKey(e);
            switch (e.Kind)
            {
                case HistoryKinds.Install:
                    if (!m.FirstInstalled.HasValue || e.Utc < m.FirstInstalled.Value) m.FirstInstalled = e.Utc;
                    m.LastInstalled = History.Max(m.LastInstalled, e.Utc);
                    if (!String.IsNullOrEmpty(slot)) { m.Slots.Add(slot); m.Disabled.Remove(slot); }
                    break;
                case HistoryKinds.Update:
                    if (!m.FirstInstalled.HasValue) m.FirstInstalled = e.Utc;
                    m.LastUpdated = History.Max(m.LastUpdated, e.Utc);
                    bool wasDisabled = !String.IsNullOrEmpty(e.PrevId) && m.Disabled.Contains(e.PrevId);
                    if (!String.IsNullOrEmpty(e.PrevId) && e.PrevId != slot) { m.Slots.Remove(e.PrevId); m.Disabled.Remove(e.PrevId); }
                    if (!String.IsNullOrEmpty(slot)) { m.Slots.Add(slot); if (wasDisabled) m.Disabled.Add(slot); }
                    break;
                case HistoryKinds.Recover:
                    if (!String.IsNullOrEmpty(slot)) { m.Slots.Add(slot); m.Disabled.Remove(slot); }
                    break;
                case HistoryKinds.Uninstall:
                    m.LastUninstalled = History.Max(m.LastUninstalled, e.Utc);
                    if (!String.IsNullOrEmpty(slot)) { m.Slots.Remove(slot); m.Disabled.Remove(slot); }
                    break;
                case HistoryKinds.FluffyUninstall:
                    m.LastUninstalled = History.Max(m.LastUninstalled, e.Utc); m.Slots.Clear(); m.Disabled.Clear();
                    break;
                case HistoryKinds.Enable:
                    if (!String.IsNullOrEmpty(slot)) { m.Slots.Add(slot); m.Disabled.Remove(slot); }
                    break;
                case HistoryKinds.Disable:
                    if (!String.IsNullOrEmpty(slot)) { m.Slots.Add(slot); m.Disabled.Add(slot); }
                    break;
            }
        }

        private void ApplyRename(HistoryEvent e)
        {
            string from = e.PrevId ?? "", to = e.Source ?? "";
            if (from.Length == 0 || to.Length == 0 || String.Equals(from, to, StringComparison.Ordinal)) return;
            string current = Resolve(from);
            if (String.Equals(current, to, StringComparison.OrdinalIgnoreCase) && !String.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;   // already merged
            ModHistory old; bool had = Mods.TryGetValue(current, out old);
            if (had) Mods.Remove(current);
            // The requested name is now the source, even when it was used before (A -> B -> A).
            // Point every earlier name straight to it; the current name must never be its own alias.
            string[] aliases = renamed.Where(p => String.Equals(p.Value, current, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();
            renamed.Remove(to);
            foreach (string alias in aliases) if (!String.Equals(alias, to, StringComparison.OrdinalIgnoreCase)) renamed[alias] = to;
            if (!String.Equals(current, to, StringComparison.OrdinalIgnoreCase)) renamed[current] = to;
            ModHistory m = Get(to, to, e.Name, had ? old.Mode : e.Mode, had ? old.Character : e.Character, had ? old.Slot : e.Slot);
            m.LastEvent = History.Max(m.LastEvent, e.Utc);
            if (had)
            {
                if (String.IsNullOrEmpty(m.Name)) m.Name = old.Name;
                m.Events += old.Events;
                m.FirstInstalled = History.Min(m.FirstInstalled, old.FirstInstalled); m.LastInstalled = History.Max(m.LastInstalled, old.LastInstalled);
                m.LastUpdated = History.Max(m.LastUpdated, old.LastUpdated); m.LastUninstalled = History.Max(m.LastUninstalled, old.LastUninstalled);
                m.LastEvent = History.Max(m.LastEvent, old.LastEvent);
                m.Slots.UnionWith(old.Slots); m.Disabled.UnionWith(old.Disabled);
            }
            int plays; if (foldedPlays.TryGetValue(current, out plays)) { foldedPlays.Remove(current); int now; foldedPlays.TryGetValue(to, out now); foldedPlays[to] = now + plays; }
            DateTime last; if (foldedLast.TryGetValue(current, out last)) { foldedLast.Remove(current); DateTime now; if (!foldedLast.TryGetValue(to, out now) || last > now) foldedLast[to] = last; }
        }

        // Launches, newest last: a probe session and a SlotsLoader run less than three minutes apart (or the same
        // game process) are one launch; every mod in either counts once.
        internal static List<HistoryPlay> MergePlays(IEnumerable<HistoryEvent> events)
        {
            var plays = new List<HistoryPlay>();
            HistoryPlay current = null;
            foreach (HistoryEvent e in events.Where(x => HistoryKinds.IsPlay(x.Kind)).OrderBy(x => PlayTime(x)))
            {
                DateTime t = PlayTime(e);
                if (current == null || t - current.Utc > SameLaunch) { current = new HistoryPlay { Utc = t }; plays.Add(current); }
                current.Events.Add(e);
                if (e.Kind == HistoryKinds.Launch) { current.Log = true; current.Slots = Math.Max(current.Slots, e.Slots); } else current.Probe = true;
                if (e.Mods != null) foreach (HistoryMod m in e.Mods) { string k = Key(m); if (k.Length > 0) current.Keys.Add(k); }
            }
            return plays;
        }
        // The game process start when the probe knew it, else the event time.
        internal static DateTime PlayTime(HistoryEvent e) { return e.Kind == HistoryKinds.SessionStart && e.Start.HasValue ? e.Start.Value : e.Utc; }

        internal void RebuildPlays()
        {
            Plays = MergePlays(playEvents);
            foreach (ModHistory m in Mods.Values) { m.Plays = 0; m.LastPlayed = null; }
            foreach (var kv in foldedPlays) { ModHistory m = Find(Resolve(kv.Key)); if (m != null) m.Plays += kv.Value; }
            foreach (var kv in foldedLast) { ModHistory m = Find(Resolve(kv.Key)); if (m != null) m.LastPlayed = History.Max(m.LastPlayed, kv.Value); }
            foreach (HistoryPlay p in Plays)
                foreach (string key in new HashSet<string>(p.Keys.Select(Resolve), StringComparer.OrdinalIgnoreCase))   // a launch counts once per mod
                {
                    ModHistory m = Find(key);
                    if (m == null)
                    {
                        HistoryMod first = p.Events.Where(e => e.Mods != null).SelectMany(e => e.Mods).FirstOrDefault(x => String.Equals(Key(x), key, StringComparison.OrdinalIgnoreCase));
                        m = Get(key, first == null ? "" : first.Source, first == null ? "" : first.Name, first == null ? "" : first.Mode, first == null ? "" : first.Character, first == null ? "" : first.Slot);
                    }
                    m.Plays++; m.LastPlayed = History.Max(m.LastPlayed, p.Utc);
                }
        }

        // Fast lookups for the window (no file access): Last used and the meta line.
        internal DateTime? LastUsed(string source) { ModHistory m = Find(source); return m == null ? null : m.LastUsed; }
    }

    internal sealed class HistoryStore
    {
        internal const string FileName = "history.jsonl";
        internal static long CapBytes = 4L * 1024 * 1024;      // compaction keeps the newest half of this
        internal static string DefaultDir;                      // tests; null = the app's Data folder
        internal static Func<DateTime> Clock = delegate { return DateTime.UtcNow; };
        private static readonly Dictionary<string, HistoryStore> stores = new Dictionary<string, HistoryStore>(StringComparer.OrdinalIgnoreCase);
        internal static HistoryStore Default { get { return For(DefaultDir ?? AppConfig.DataDir); } }
        internal static HistoryStore For(string dataDir)
        {
            string full = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar);
            lock (stores) { HistoryStore s; if (!stores.TryGetValue(full, out s)) { s = new HistoryStore(full); stores[full] = s; } return s; }
        }
        private static readonly HistoryIndex Empty = new HistoryIndex();

        internal readonly string DataDir, FilePath;
        private readonly object sync = new object();
        private List<HistoryEvent> events;
        // Copy-on-write: a reader keeps a consistent index while a writer builds the next one.
        private volatile HistoryIndex index;
        private long knownLength = -1;
        private string knownHead = "";
        private DateTime lastCheck = DateTime.MinValue, lastPeek = DateTime.MinValue;
        private int refreshing;
        internal int BadLines, Compactions, Reloads;
        // Bumps whenever the index changes (the window repaints when it moved).
        private int version;
        internal int Version { get { return Thread.VolatileRead(ref version); } }

        private HistoryStore(string dataDir) { DataDir = dataDir; FilePath = Path.Combine(dataDir, FileName); }

        private Mutex OpenMutex()
        {
            string key;
            using (var sha = FastHash.Sha256()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant()))).Replace("-", "").Substring(0, 32);
            return new Mutex(false, "Local\\SF6SlotsInstaller-history-" + key);
        }
        private static bool Acquire(Mutex m) { try { return m.WaitOne(3000); } catch (AbandonedMutexException) { return true; } }
        private void Publish(HistoryIndex next) { index = next; Interlocked.Increment(ref version); }

        // Never throws: a history write must not fail the change it records.
        internal bool Append(HistoryEvent e) { return Append(new[] { e }); }
        internal bool Append(IList<HistoryEvent> batch)
        {
            if (batch == null || batch.Count == 0) return true;
            try
            {
                lock (sync)
                using (Mutex mutex = OpenMutex())
                {
                    if (!Acquire(mutex)) { Program.LogLine("History: the history file stayed busy; " + batch.Count + " event(s) not recorded."); return false; }
                    try
                    {
                        Directory.CreateDirectory(DataDir);
                        RefreshLocked(true);
                        var text = new StringBuilder();
                        foreach (HistoryEvent e in batch) { string line = HistoryJson.Write(e); e.Length = Encoding.UTF8.GetByteCount(line) + 1; text.Append(line).Append('\n'); }
                        byte[] bytes = new UTF8Encoding(false).GetBytes(text.ToString());
                        using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        {
                            stream.Write(bytes, 0, bytes.Length);
                            knownLength = stream.Position;
                        }
                        knownHead = Head(knownLength);
                        events.AddRange(batch);
                        Publish(HistoryIndex.Build(events));
                        if (knownLength > CapBytes) CompactLocked();
                        return true;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { Program.Log(ex); return false; }
        }

        // The current index, read now when the file changed (tests, the CLI, the Assistant, background work).
        internal HistoryIndex Index()
        {
            lock (sync)
            {
                try
                {
                    DateTime now = Clock();
                    if (events == null || now - lastCheck > TimeSpan.FromSeconds(2) || now < lastCheck) { lastCheck = now; RefreshLocked(false); }
                }
                catch (Exception ex) { Program.Log(ex); if (index == null) { events = new List<HistoryEvent>(); Publish(new HistoryIndex()); } }
                return index ?? Empty;
            }
        }
        // For the UI thread: never reads the file and never waits. The first call and then every 2 s queue a
        // background check; the window repaints when Version moves.
        internal HistoryIndex Peek()
        {
            HistoryIndex current = index;
            DateTime now = Clock();
            if (current == null || now - lastPeek > TimeSpan.FromSeconds(2) || now < lastPeek) { lastPeek = now; ScheduleRefresh(); }
            return current ?? Empty;
        }
        internal bool Loaded { get { return index != null; } }
        private void ScheduleRefresh()
        {
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Index(); }
                catch (Exception ex) { Program.Log(ex); }
                finally { Interlocked.Exchange(ref refreshing, 0); }
            });
        }
        internal List<HistoryEvent> Events() { lock (sync) { Index(); return events.ToList(); } }
        internal void Reload() { lock (sync) { events = null; knownLength = -1; knownHead = ""; lastCheck = DateTime.MinValue; } Index(); }

        private string Head(long count)
        {
            try
            {
                using (var s = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var b = new byte[(int)Math.Max(0, Math.Min(Math.Min(256, s.Length), count))]; int n = 0;
                    while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                    using (var sha = FastHash.Sha256()) return n + ":" + BitConverter.ToString(sha.ComputeHash(b, 0, n)).Replace("-", "");
                }
            }
            catch (FileNotFoundException) { return ""; }
            catch (DirectoryNotFoundException) { return ""; }
        }

        // Reads what is new: the tail when the file only grew, everything when it was compacted or replaced.
        private void RefreshLocked(bool force)
        {
            if (!File.Exists(FilePath))
            {
                if (events == null || knownLength != 0) { events = new List<HistoryEvent>(); knownLength = 0; knownHead = ""; Publish(new HistoryIndex()); }
                return;
            }
            long length = new FileInfo(FilePath).Length;
            if (events != null && length == knownLength && !force) return;
            bool tail = events != null && knownLength > 0 && length >= knownLength && Head(knownLength) == knownHead;
            if (tail && length == knownLength) return;
            long from = tail ? knownLength : 0;
            byte[] data;
            using (var s = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                s.Position = from; data = new byte[Math.Max(0, s.Length - from)]; int n = 0;
                while (n < data.Length) { int r = s.Read(data, n, data.Length - n); if (r <= 0) break; n += r; }
                if (n < data.Length) Array.Resize(ref data, n);
            }
            int complete = Array.LastIndexOf(data, (byte)'\n') + 1;   // a line still being written is read next time
            var fresh = new List<HistoryEvent>(); int start = 0, bad = 0;
            var utf8 = new UTF8Encoding(false);
            for (int i = 0; i < complete; i++)
            {
                if (data[i] != (byte)'\n') continue;
                string line = utf8.GetString(data, start, i - start).TrimEnd('\r');
                HistoryEvent e = HistoryJson.Read(line);
                if (e != null) { e.Length = i - start + 1; fresh.Add(e); } else if (line.Trim().Length > 0) bad++;
                start = i + 1;
            }
            if (!tail) { events = fresh; BadLines = bad; Reloads++; } else { events.AddRange(fresh); BadLines += bad; }
            knownLength = from + complete; knownHead = Head(knownLength);
            Publish(HistoryIndex.Build(events));
        }

        // Folds the oldest events into one summary line per mod (and one for the launches) and keeps the newest
        // events up to half the cap. Per-mod totals (plays, last played, install dates, installed state) are the
        // same before and after; a launch is never split across the boundary.
        internal void Compact() { lock (sync) using (Mutex mutex = OpenMutex()) { if (!Acquire(mutex)) return; try { RefreshLocked(true); CompactLocked(); } finally { mutex.ReleaseMutex(); } } }
        private void CompactLocked()
        {
            if (events == null || events.Count < 2) return;
            List<HistoryEvent> ordered = events.Select((e, i) => new { e, i }).OrderBy(x => x.e.Utc).ThenBy(x => x.i).Select(x => x.e).ToList();
            long budget = CapBytes / 2, kept = 0; int k = ordered.Count;
            while (k > 0 && kept + Math.Max(1, ordered[k - 1].Length) <= budget) { k--; kept += Math.Max(1, ordered[k].Length); }
            if (k <= 0) return;
            if (k >= ordered.Count) k = ordered.Count - 1;   // always keep the newest event
            DateTime boundary = ordered[k].Utc;
            // A launch spans its probe session and its SlotsLoader run: keep all of it on the newer side.
            foreach (HistoryPlay play in HistoryIndex.MergePlays(ordered))
            {
                DateTime first = play.Events.Min(e => e.Utc), last = play.Events.Max(e => e.Utc);
                if (first < boundary && last >= boundary) boundary = first;
            }
            List<HistoryEvent> folded = ordered.Where(e => e.Utc < boundary).ToList(), rest = ordered.Where(e => e.Utc >= boundary).ToList();
            if (folded.Count == 0) return;
            HistoryIndex old = HistoryIndex.Build(folded);
            DateTime at = folded.Max(e => e.Utc);
            var summaries = new List<HistoryEvent>();
            summaries.Add(new HistoryEvent { Utc = at, Kind = HistoryKinds.Summary, Launches = old.Launches, Slots = old.SlotUses, Detail = "folded " + folded.Count + " events" });
            foreach (ModHistory m in old.Mods.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                bool slotKey = m.Key.StartsWith("slot:") || m.Key.StartsWith("fluffy:");
                summaries.Add(new HistoryEvent
                {
                    Utc = m.LastEvent ?? at, Kind = HistoryKinds.Summary, Source = m.Source, Name = m.Name, Mode = m.Mode, Character = m.Character,
                    Slot = slotKey ? m.Key.Substring(m.Key.IndexOf(':') + 1) : m.Slot, Id = m.Key.StartsWith("id:") ? m.Key.Substring(3) : "",
                    Plays = m.Plays, Events = m.Events, FirstInstalled = m.FirstInstalled, LastInstalled = m.LastInstalled, LastUpdated = m.LastUpdated,
                    LastUninstalled = m.LastUninstalled, LastPlayed = m.LastPlayed, InstalledSlots = m.Slots.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                    DisabledSlots = m.Disabled.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
            var all = summaries.Concat(rest).ToList();
            var text = new StringBuilder();
            foreach (HistoryEvent e in all) { string line = HistoryJson.Write(e); e.Length = Encoding.UTF8.GetByteCount(line) + 1; text.Append(line).Append('\n'); }
            string tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, text.ToString(), new UTF8Encoding(false));
                DurableFile.Publish(tmp, FilePath);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            events = all;
            knownLength = new FileInfo(FilePath).Length; knownHead = Head(knownLength); Compactions++;
            Publish(HistoryIndex.Build(all));
            Program.LogLine("History compacted: " + folded.Count + " older events folded into " + summaries.Count + " summary lines.");
        }
    }

    // Helpers shared by the hooks, the window and the Assistant.
    internal static class History
    {
        internal static DateTime? Max(DateTime? a, DateTime? b) { return !a.HasValue ? b : !b.HasValue ? a : a.Value >= b.Value ? a : b; }
        internal static DateTime? Min(DateTime? a, DateTime? b) { return !a.HasValue ? b : !b.HasValue ? a : a.Value <= b.Value ? a : b; }
        internal static DateTime Now { get { return HistoryStore.Clock(); } }

        internal static string Normalize(string source)
        {
            if (String.IsNullOrWhiteSpace(source)) return "";
            try { return Path.GetFullPath(source).TrimEnd('\\', '/'); }
            catch (Exception ex) { if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return source.Trim(); throw; }
        }
        // The archive identity the library and the Assistant's package cache use: length and last write (UTC ticks).
        // Folder sources have none (their files change independently).
        internal static string Stamp(string source)
        {
            try
            {
                if (String.IsNullOrEmpty(source)) return "";
                var file = new FileInfo(source);
                if (file.Exists) return file.Length.ToString(CultureInfo.InvariantCulture) + ":" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                return Directory.Exists(source) ? "folder" : "missing";
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return ""; }
        }
        internal static string ReplaceMode(ReplaceMod m) { return m != null && (m.Kind == "Tool" || m.Kind == "Mixed") ? HistoryModes.Tool : HistoryModes.Replace; }
        internal static string CharacterOf(IEnumerable<string> paths)
        {
            var names = new List<string>();
            foreach (string p in paths ?? new string[0])
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(p ?? "", @"/esf/esf(\d{3})/", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    string name; if (Characters.ByCode.TryGetValue(m.Groups[1].Value, out name) && !names.Contains(name)) names.Add(name);
                }
            return String.Join(", ", names);
        }

        // Records into a Data folder's history; logs and swallows every failure.
        internal static void Record(string dataDir, HistoryEvent e) { Record(dataDir, new List<HistoryEvent> { e }); }
        internal static void Record(string dataDir, List<HistoryEvent> batch)
        {
            try
            {
                if (batch == null || batch.Count == 0) return;
                foreach (HistoryEvent e in batch) { if (e.Utc == DateTime.MinValue) e.Utc = Now; e.Source = Normalize(e.Source); }
                (dataDir == null ? HistoryStore.Default : HistoryStore.For(dataDir)).Append(batch);
            }
            catch (Exception ex) { Program.Log(ex); }
        }
        internal static HistoryEvent ForReplacement(string kind, ReplaceMod m)
        {
            return new HistoryEvent
            {
                Kind = kind, Mode = ReplaceMode(m), Source = m.SourcePath ?? "", Stamp = Stamp(m.SourcePath), Name = m.Name ?? "", Mod = m.Name ?? "",
                Character = CharacterOf((m.Files ?? new List<ModFile>()).Select(f => "/" + f.Path)), Slot = m.Affects ?? "", Id = m.Id ?? "",
                Options = (m.SelectedIds ?? new List<string>()).ToList()
            };
        }
        internal static HistoryEvent ForOutfit(string kind, string source, string name, string character, string id, IEnumerable<string> options)
        {
            return new HistoryEvent
            {
                Kind = kind, Mode = HistoryModes.Extra, Source = source ?? "", Stamp = String.IsNullOrEmpty(source) ? "" : Stamp(source), Name = name ?? "",
                Character = character ?? "", Slot = (character ?? "") + "/" + (name ?? ""), Id = id ?? "", Options = options == null ? null : options.ToList()
            };
        }

        // "today", "3d ago", "12 Oct", "12 Oct 2025" (local time).
        internal static string Relative(DateTime utc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - utc;
            if (age < TimeSpan.FromHours(1) && age > TimeSpan.FromMinutes(-5)) return "just now";
            if (age < TimeSpan.FromHours(24) && age > TimeSpan.Zero) return (int)age.TotalHours + "h ago";
            if (age < TimeSpan.FromDays(30) && age > TimeSpan.Zero) return Math.Max(1, (int)age.TotalDays) + "d ago";
            return Date(utc, nowUtc);
        }
        internal static string Date(DateTime utc, DateTime nowUtc)
        {
            DateTime local = utc.ToLocalTime(), today = nowUtc.ToLocalTime();
            return local.ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
        }
        // The short muted text for rows: "played 3d ago", else "installed 12 Oct" while installed; null when there is
        // nothing to say.
        internal static string Meta(ModHistory m, bool installedNow, DateTime nowUtc)
        {
            if (m == null) return null;
            if (m.LastPlayed.HasValue) return "played " + Relative(m.LastPlayed.Value, nowUtc);
            DateTime? installed = m.LastInstallOrSwitch;
            if (installedNow && installed.HasValue) return "installed " + Date(installed.Value, nowUtc);
            return null;
        }
    }
}
