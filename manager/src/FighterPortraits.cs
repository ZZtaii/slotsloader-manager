using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // 2.3.0: read-only view over the game's KPKA v4 PAKs (base, DLC, then base patches; a later PAK wins,
    // as in the game) for resolving a handful of known paths. Files are opened FileAccess.Read with
    // FileShare.ReadWrite|Delete, so nothing here ever blocks or changes a PAK.
    internal sealed class GamePak
    {
        internal string Path, Name;
        internal long Length;
        internal DateTime LastWriteUtc;
        internal uint Count, Fingerprint;
        // "name|length|mtime ticks|header fingerprint": cheap identity, no hashing of the 90 GB base PAK.
        internal string Identity { get { return Name + "|" + Length.ToString(CultureInfo.InvariantCulture) + "|" + LastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Fingerprint.ToString("X8"); } }
    }

    internal struct GamePakHit
    {
        internal GamePak Pak;
        internal long Offset, CompressedSize, DecompressedSize, Attributes;
    }

    internal sealed class GamePakReader
    {
        internal readonly List<GamePak> Paks = new List<GamePak>();   // lowest priority first
        internal readonly List<string> Skipped = new List<string>();
        internal string Root;

        // Load order: re_chunk_000.pak, dlc\*.pak, re_chunk_000.pak.patch_NNN.pak ascending (Fluffy's PAK
        // mods live in the patches and win over everything, as in the game).
        internal static GamePakReader Open(string gameRoot)
        {
            var g = new GamePakReader { Root = gameRoot };
            if (String.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) return g;
            var files = new List<string>();
            string basePak = System.IO.Path.Combine(gameRoot, "re_chunk_000.pak");
            if (File.Exists(basePak)) files.Add(basePak);
            string dlc = System.IO.Path.Combine(gameRoot, "dlc");
            if (Directory.Exists(dlc)) files.AddRange(Directory.GetFiles(dlc, "*.pak").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            files.AddRange(Directory.GetFiles(gameRoot, "re_chunk_000.pak.patch_*.pak").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            foreach (string f in files)
            {
                try
                {
                    var info = new FileInfo(f);
                    var p = new GamePak { Path = f, Name = System.IO.Path.GetFileName(f), Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
                    byte[] header;
                    using (var fs = OpenRead(f)) header = PakIndex.ReadExactly(fs, PakIndex.HeaderSize);
                    if (header[0] != 'K' || header[1] != 'P' || header[2] != 'K' || header[3] != 'A' || header[4] != 4 || BitConverter.ToUInt16(header, 6) != 0) { g.Skipped.Add(p.Name + " (unsupported header)"); continue; }
                    p.Count = BitConverter.ToUInt32(header, 8); p.Fingerprint = BitConverter.ToUInt32(header, 12);
                    if ((long)p.Count * PakIndex.EntrySize > p.Length - PakIndex.HeaderSize) { g.Skipped.Add(p.Name + " (entry table exceeds the file)"); continue; }
                    g.Paks.Add(p);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; g.Skipped.Add(System.IO.Path.GetFileName(f) + " (" + ex.Message + ")"); }
            }
            return g;
        }

        // Combined identity of every PAK (the portrait cache key). Changes with any game update or PAK-method install.
        internal string Identity { get { return String.Join(";", Paks.Select(p => p.Identity)); } }

        internal static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }

        // One streaming pass over each PAK's entry table looking for the wanted hashes; nothing else is kept.
        // A later PAK overrides an earlier one. Zeroed rows (Fluffy-invalidated) never match.
        internal Dictionary<ulong, GamePakHit> Resolve(ICollection<ulong> wanted, CancellationToken token)
        {
            var want = new HashSet<ulong>(wanted);
            var found = new Dictionary<ulong, GamePakHit>();
            var chunk = new byte[PakIndex.EntrySize * 8192];
            foreach (GamePak p in Paks)
            {
                token.ThrowIfCancellationRequested();
                using (var fs = OpenRead(p.Path))
                {
                    fs.Seek(PakIndex.HeaderSize, SeekOrigin.Begin);
                    long remaining = (long)p.Count * PakIndex.EntrySize;
                    while (remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(chunk.Length, remaining);
                        int got = 0; while (got < n) { int r = fs.Read(chunk, got, n - got); if (r <= 0) throw new EndOfStreamException("The PAK entry table ended early: " + p.Name); got += r; }
                        remaining -= n;
                        for (int o = 0; o < n; o += PakIndex.EntrySize)
                        {
                            ulong h = BitConverter.ToUInt64(chunk, o);
                            if (h == 0 || !want.Contains(h)) continue;
                            found[h] = new GamePakHit
                            {
                                Pak = p, Offset = BitConverter.ToInt64(chunk, o + 8), CompressedSize = BitConverter.ToInt64(chunk, o + 16),
                                DecompressedSize = BitConverter.ToInt64(chunk, o + 24), Attributes = BitConverter.ToInt64(chunk, o + 32)
                            };
                        }
                    }
                }
            }
            return found;
        }

        internal static byte[] Read(GamePakHit hit)
        {
            if (hit.CompressedSize < 0 || hit.CompressedSize > 256L << 20 || hit.DecompressedSize < 0 || hit.DecompressedSize > 256L << 20) throw new IOException("PAK entry is too large for a portrait.");
            if (hit.Offset < PakIndex.HeaderSize || hit.Offset > hit.Pak.Length - hit.CompressedSize) throw new IOException("PAK entry lies outside " + hit.Pak.Name + ".");
            byte[] raw;
            using (var fs = OpenRead(hit.Pak.Path)) { fs.Seek(hit.Offset, SeekOrigin.Begin); raw = PakIndex.ReadExactly(fs, (int)hit.CompressedSize); }
            return PakIndex.Decompress(raw, hit.Attributes, hit.DecompressedSize);
        }

        // True when every PAK still has the identity it had when opened (no game update or PAK write since).
        internal bool StillCurrent() { return Open(Root).Identity == Identity; }
    }

    internal sealed class PortraitInfo
    {
        internal string Id, Name, Status = "missing", Candidate = "", Path = "", Pak = "", Size = "", Format = "", Note = "";
        internal long Milliseconds;
        internal bool Ok { get { return Status == "ok"; } }
    }

    internal sealed class PortraitReport
    {
        internal string Cache = "none";            // hit, miss, stale, none (no game folder), deferred
        internal string Identity = "", Error = "";
        internal int PakCount;
        internal long OpenMs, ResolveMs, TotalMs;
        internal bool Discarded;                  // the game changed while reading: nothing cached
        internal readonly List<PortraitInfo> Fighters = new List<PortraitInfo>();
        internal readonly Dictionary<string, string> StageNames = new Dictionary<string, string>(StringComparer.Ordinal);
        internal string StageSource = "";
    }

    // Fighter portraits for the Filters panel: the character-select face of each fighter, read from the
    // user's own game PAKs (never shipped), cropped to a 5:7 tile and cached as PNG in Data\portraits\v1,
    // keyed by the identity of every PAK. Also the stage names of the game's stage-name message file.
    internal static class FighterPortraits
    {
        internal const string CacheVersion = "v1", ManifestFormat = "slotsloader-portraits-v1";
        internal const int TileWidth = 300, TileHeight = 420;   // 5:7
        internal const string Root = "natives/stm/product/gui/data/character_image/";
        internal const string StageMessagePath = "natives/stm/product/message/cmn/001/wordlist/stagenamemessage.msg.";
        // Candidate kinds in order; {id} = three-digit fighter code. The first hit wins.
        internal static readonly string[][] Templates = {
            new[] { "face", "{id}/caracterselect/tex_esf{id}_face_l_l_iam" },            // 512x512 character-select face (001-029 spelling)
            new[] { "face", "{id}/characterselect/tex_esf{id}_face_l_l_iam" },           // the same, folder spelled correctly (030+)
            new[] { "cfn", "{id}/pub/tex_{id}_cfn_iam" },                                // key art, closer crop
            new[] { "1k", "{id}/pub/tex_{id}_1k_iam" },                                  // key art with ink splash
            new[] { "card", "{id}/pub/tex_{id}_card_iam" },
            new[] { "new_challenger", "{id}/new_challenger/tex_nextchallenger_esf{id}_00_00_iam" },
            new[] { "battlehud", "{id}/battlehud/tex_battlehud_{id}_1p_iam" },
        };
        internal const int DiscoverUpTo = 60;     // fighter codes probed for fighters newer than Characters.ByCode

        internal static string DefaultCacheDirectory { get { return System.IO.Path.Combine(AppConfig.DataDir, "portraits", CacheVersion); } }

        // Suffixes to try: the game's current one (SlotsLoader's TSV or GameTables), then the 2023 one.
        internal static List<string> TexVersions(string gameRoot)
        {
            var list = new List<string>();
            try { list.Add(GameReference.Tables(gameRoot).TexVersion); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            if (list.Count == 0) list.Add(GameTables.Suffixes["tex"]);
            if (!list.Contains("143230113")) list.Add("143230113");
            return list.Distinct().ToList();
        }
        internal static string MsgVersion(string gameRoot)
        {
            try { string v; if (GameReference.Tables(gameRoot).Suffixes.TryGetValue("msg", out v) && !String.IsNullOrWhiteSpace(v)) return v.Trim(); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            return GameTables.Suffixes["msg"];
        }

        // Ordered candidate paths of one fighter: (kind, path). 030 and later try "characterselect" first,
        // older fighters the original "caracterselect" spelling first.
        internal static List<KeyValuePair<string, string>> Candidates(string id, IList<string> versions)
        {
            int number; Int32.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out number);
            var templates = Templates.ToList();
            if (number >= 30) { var t = templates[0]; templates[0] = templates[1]; templates[1] = t; }
            var result = new List<KeyValuePair<string, string>>();
            foreach (string[] t in templates)
                foreach (string v in versions)
                    result.Add(new KeyValuePair<string, string>(t[0], Root + t[1].Replace("{id}", id) + ".tex." + v));
            return result;
        }

        internal static string DisplayName(string id)
        {
            string name; return Characters.ByCode.TryGetValue(id, out name) ? name : "esf" + id;
        }

        // ---- Cache manifest ----------------------------------------------------------------------------
        internal sealed class Manifest
        {
            internal string Identity;
            internal readonly Dictionary<string, PortraitInfo> Rows = new Dictionary<string, PortraitInfo>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Stages = new Dictionary<string, string>(StringComparer.Ordinal);
            internal string StageSource = "";
        }

        internal static Manifest ReadManifest(string cacheDir)
        {
            string file = System.IO.Path.Combine(cacheDir, "portraits.tsv");
            try
            {
                if (!File.Exists(file)) return null;
                var m = new Manifest(); bool format = false;
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f[0] == "#format") { format = f.Length > 1 && f[1] == ManifestFormat; continue; }
                    if (f[0] == "#identity") { m.Identity = f.Length > 1 ? f[1] : ""; continue; }
                    if (f[0] == "#stage-source") { m.StageSource = f.Length > 1 ? f[1] : ""; continue; }
                    if (f[0] == "#stage" && f.Length >= 3) { m.Stages[f[1]] = f[2]; continue; }
                    if (line.StartsWith("#") || f.Length < 2 || f[0].Length != 3 || !f[0].All(Char.IsDigit)) continue;
                    var info = new PortraitInfo { Id = f[0], Name = DisplayName(f[0]), Status = f[1] };
                    if (f.Length > 2) info.Candidate = f[2]; if (f.Length > 3) info.Path = f[3]; if (f.Length > 4) info.Pak = f[4];
                    if (f.Length > 5) info.Size = f[5]; if (f.Length > 6) info.Format = f[6]; if (f.Length > 7) info.Note = f[7];
                    m.Rows[info.Id] = info;
                }
                return format && m.Identity != null ? m : null;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }

        internal static void WriteManifest(string cacheDir, string identity, IEnumerable<PortraitInfo> rows, IDictionary<string, string> stages, string stageSource)
        {
            var lines = new List<string> { "#format\t" + ManifestFormat, "#identity\t" + identity, "#stage-source\t" + Clean(stageSource), "#id\tstatus\tcandidate\tpath\tpak\tsize\tformat\tnote" };
            foreach (PortraitInfo r in rows.OrderBy(r => r.Id, StringComparer.Ordinal))
                lines.Add(String.Join("\t", r.Id, r.Status, Clean(r.Candidate), Clean(r.Path), Clean(r.Pak), Clean(r.Size), Clean(r.Format), Clean(r.Note)));
            foreach (var kv in stages.OrderBy(k => k.Key, StringComparer.Ordinal)) lines.Add("#stage\t" + kv.Key + "\t" + Clean(kv.Value));
            WriteAtomic(System.IO.Path.Combine(cacheDir, "portraits.tsv"), Encoding.UTF8.GetBytes(String.Join("\r\n", lines) + "\r\n"));
        }
        private static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        // Temp file + rename, so a reader never sees a half-written PNG or manifest.
        internal static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                DurableFile.Publish(tmp, path);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        // hit: same identity and every requested fighter has a row (and its PNG when ok); stale: another identity.
        internal static string CacheState(Manifest m, string identity, IEnumerable<string> ids, string cacheDir)
        {
            if (m == null) return "miss";
            if (m.Identity != identity) return "stale";
            foreach (string id in ids)
            {
                PortraitInfo row;
                if (!m.Rows.TryGetValue(id, out row)) return "miss";
                if (row.Ok && !File.Exists(System.IO.Path.Combine(cacheDir, id + ".png"))) return "miss";
            }
            return "hit";
        }

        // ---- Loading (background) ----------------------------------------------------------------------
        // Calls `tile` once per fighter (with null for a name tile) as soon as its portrait is known. With a
        // valid cache only the PNGs are read. `changeInProgress` (optional) defers the read while this app is
        // changing the game's PAKs; the PAK identity is checked again afterwards and a read that overlapped a
        // change is not cached.
        internal static PortraitReport Load(string gameRoot, IList<string> knownIds, string cacheDir, CancellationToken token, Action<PortraitInfo, Bitmap> tile, Func<bool> changeInProgress)
        {
            var report = new PortraitReport();
            var clock = Stopwatch.StartNew();
            var ids = knownIds.Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList();
            GamePakReader paks = GamePakReader.Open(gameRoot);
            report.OpenMs = clock.ElapsedMilliseconds; report.PakCount = paks.Paks.Count; report.Identity = paks.Identity;
            if (paks.Paks.Count == 0)
            {
                report.Cache = "none";
                foreach (string id in ids) { var info = new PortraitInfo { Id = id, Name = DisplayName(id), Note = "no game folder" }; report.Fighters.Add(info); if (tile != null) tile(info, null); }
                report.TotalMs = clock.ElapsedMilliseconds; return report;
            }
            Directory.CreateDirectory(cacheDir);
            Manifest manifest = ReadManifest(cacheDir);
            report.Cache = CacheState(manifest, report.Identity, ids, cacheDir);
            if (report.Cache == "hit")
            {
                foreach (string id in ids.Concat(manifest.Rows.Keys.Where(k => !ids.Contains(k) && manifest.Rows[k].Ok)).ToList())
                {
                    token.ThrowIfCancellationRequested();
                    PortraitInfo info = manifest.Rows[id]; info.Name = DisplayName(id);
                    Bitmap bitmap = info.Ok ? LoadPng(System.IO.Path.Combine(cacheDir, id + ".png")) : null;
                    if (info.Ok && bitmap == null) info.Note = "cached PNG unreadable";
                    report.Fighters.Add(info);
                    if (tile != null) tile(info, bitmap); else if (bitmap != null) bitmap.Dispose();
                }
                foreach (var kv in manifest.Stages) report.StageNames[kv.Key] = kv.Value;
                report.StageSource = manifest.StageSource;
                report.TotalMs = clock.ElapsedMilliseconds; return report;
            }
            // Wait (up to a minute) while this app holds the game lock for a change.
            for (int i = 0; changeInProgress != null && changeInProgress(); i++)
            {
                if (i >= 240) { report.Cache = "deferred"; report.Error = "A game-file change is in progress."; report.TotalMs = clock.ElapsedMilliseconds; return report; }
                token.WaitHandle.WaitOne(250); token.ThrowIfCancellationRequested();
            }
            // Fighters newer than this build: probe the next codes for a face or key art.
            var probe = new List<string>(ids);
            for (int n = 1; n <= DiscoverUpTo; n++) { string id = n.ToString("000", CultureInfo.InvariantCulture); if (!probe.Contains(id)) probe.Add(id); }
            List<string> versions = TexVersions(gameRoot);
            var candidates = probe.ToDictionary(id => id, id => Candidates(id, versions), StringComparer.Ordinal);
            var wanted = new HashSet<ulong>(candidates.Values.SelectMany(list => list.Select(c => PakHash.PathHash(c.Value))));
            string msgPath = StageMessagePath + MsgVersion(gameRoot);
            ulong msgHash = PakHash.PathHash(msgPath); wanted.Add(msgHash);
            var resolveClock = Stopwatch.StartNew();
            Dictionary<ulong, GamePakHit> hits = paks.Resolve(wanted, token);
            report.ResolveMs = resolveClock.ElapsedMilliseconds;
            var rows = new List<PortraitInfo>();
            var pending = new List<KeyValuePair<PortraitInfo, byte[]>>();
            foreach (string id in probe)
            {
                token.ThrowIfCancellationRequested();
                bool known = ids.Contains(id);
                var info = new PortraitInfo { Id = id, Name = DisplayName(id) };
                var each = Stopwatch.StartNew();
                Bitmap bitmap = null;
                foreach (var c in candidates[id])
                {
                    GamePakHit hit;
                    if (!hits.TryGetValue(PakHash.PathHash(c.Value), out hit)) continue;
                    info.Candidate = c.Key; info.Path = c.Value; info.Pak = hit.Pak.Name;
                    try
                    {
                        ReTex tex = ReTex.Parse(GamePakReader.Read(hit));
                        info.Format = tex.FormatName;
                        if (!ReTex.Supported(tex.Format)) throw new NotSupportedException(tex.FormatName + " is not supported.");
                        int level = tex.PickLevel(1024, 1024);
                        int w = tex.LevelWidth(level), h = tex.LevelHeight(level);
                        info.Size = w + "x" + h;
                        byte[] pixels = tex.DecodeLevel(level);
                        bitmap = CropTile(pixels, w, h, c.Key == "face" || c.Key == "battlehud");
                        info.Status = "ok"; info.Note = "";
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException || ex is OperationCanceledException) throw;
                        info.Status = "error"; info.Note = ex.Message;   // try the next candidate
                    }
                }
                info.Milliseconds = each.ElapsedMilliseconds;
                if (!known && !info.Ok) continue;                     // probed code without a fighter
                rows.Add(info); report.Fighters.Add(info);
                if (bitmap != null)
                {
                    byte[] png;
                    using (var ms = new MemoryStream()) { bitmap.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
                    pending.Add(new KeyValuePair<PortraitInfo, byte[]>(info, png));
                }
                if (tile != null) tile(info, bitmap); else if (bitmap != null) bitmap.Dispose();
            }
            GamePakHit msgHit;
            if (hits.TryGetValue(msgHash, out msgHit))
            {
                try
                {
                    foreach (var kv in GameMessages.StageNames(GamePakReader.Read(msgHit))) report.StageNames[kv.Key] = kv.Value;
                    report.StageSource = msgPath.Substring(msgPath.LastIndexOf('/') + 1) + " in " + msgHit.Pak.Name;
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; report.StageSource = "unreadable: " + ex.Message; }
            }
            else report.StageSource = "not found";
            token.ThrowIfCancellationRequested();
            // Cache only what was read from an unchanged game.
            if ((changeInProgress != null && changeInProgress()) || !paks.StillCurrent()) { report.Discarded = true; report.TotalMs = clock.ElapsedMilliseconds; return report; }
            foreach (var p in pending) WriteAtomic(System.IO.Path.Combine(cacheDir, p.Key.Id + ".png"), p.Value);
            WriteManifest(cacheDir, report.Identity, rows, report.StageNames, report.StageSource);
            report.TotalMs = clock.ElapsedMilliseconds;
            return report;
        }

        internal static Bitmap LoadPng(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                using (var image = Image.FromStream(ms))
                {
                    var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(copy)) { g.Clear(Color.Transparent); g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height)); }
                    return copy;
                }
            }
            // GDI+ reports an invalid image as OutOfMemoryException or ArgumentException: a name tile then.
            catch (ArgumentException) { return null; }
            catch (OutOfMemoryException) { return null; }
            catch (System.Runtime.InteropServices.ExternalException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // A 5:7 tile around the figure: the horizontal centre is the alpha-weighted centroid of the opaque
        // pixels. Faces use the full height; key art an upper-body band (10%..90% of the height).
        internal static Bitmap CropTile(byte[] bgra, int w, int h, bool fullHeight)
        {
            double cx = AlphaCentroidX(bgra, w, h, fullHeight ? 0 : (int)(h * 0.15), fullHeight ? h : (int)(h * 0.60));
            double ch = fullHeight ? h : h * 0.80, cw = ch * TileWidth / TileHeight;
            if (cw > w) { cw = w; ch = cw * TileHeight / TileWidth; }
            double x0 = Math.Max(0, Math.Min(w - cw, cx - cw / 2)), y0 = fullHeight ? 0 : Math.Max(0, Math.Min(h - ch, h * 0.10));
            var tile = new Bitmap(TileWidth, TileHeight, PixelFormat.Format32bppArgb);
            using (Bitmap source = ReTex.ToBitmap(bgra, w, h))
            using (var g = Graphics.FromImage(tile))
            using (var attributes = new ImageAttributes())
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality;
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(source, new Rectangle(0, 0, TileWidth, TileHeight), (float)x0, (float)y0, (float)cw, (float)ch, GraphicsUnit.Pixel, attributes);
            }
            return tile;
        }

        // Alpha-weighted mean x of pixels with alpha >= 128 between rows y0 and y1; the centre without any.
        internal static double AlphaCentroidX(byte[] bgra, int w, int h, int y0, int y1)
        {
            double sum = 0, weight = 0;
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y += 2)
                for (int x = 0; x < w; x += 2)
                {
                    int a = bgra[(y * w + x) * 4 + 3];
                    if (a < 128) continue;
                    sum += x * (double)a; weight += a;
                }
            return weight > 0 ? sum / weight : w / 2.0;
        }
    }

    // RE Engine message files (GMSG, the 2023+ layout of .msg.21/.msg.22): entry names and their text per
    // language. Used only for the stage names (English, language 1) of the game's stage-name list.
    internal static class GameMessages
    {
        private static readonly byte[] Key = { 0xCF, 0xCE, 0xFB, 0xF8, 0xEC, 0x0A, 0x33, 0x66, 0x93, 0xA9, 0x1D, 0x93, 0x50, 0x39, 0x5F, 0x09 };
        internal const int English = 1;

        // name -> text in `language` for every entry. Throws InvalidDataException for another layout.
        internal static List<KeyValuePair<string, string>> Entries(byte[] file, int language)
        {
            if (file == null || file.Length < 0x48 || file[4] != 'G' || file[5] != 'M' || file[6] != 'S' || file[7] != 'G') throw new InvalidDataException("Not a GMSG message file.");
            int count = BitConverter.ToInt32(file, 0x10), languages = BitConverter.ToInt32(file, 0x18);
            long dataOffset = BitConverter.ToInt64(file, 0x20);
            if (count < 0 || count > 100000 || languages <= language || languages > 64 || dataOffset < 0x48 || dataOffset > file.Length || 0x48 + 8L * count > file.Length) throw new InvalidDataException("Unsupported message file layout.");
            byte[] b = (byte[])file.Clone();
            byte previous = 0;
            for (long i = dataOffset; i < b.Length; i++) { byte current = b[i]; b[i] = (byte)(current ^ previous ^ Key[(i - dataOffset) & 0xF]); previous = current; }
            var result = new List<KeyValuePair<string, string>>();
            for (int k = 0; k < count; k++)
            {
                long entry = BitConverter.ToInt64(file, 0x48 + 8 * k);
                if (entry < 0x48 || entry + 40 + 8L * languages > file.Length) throw new InvalidDataException("Message entry " + k + " lies outside the file.");
                string name = Text(b, BitConverter.ToInt64(file, (int)entry + 24), dataOffset);
                string text = Text(b, BitConverter.ToInt64(file, (int)entry + 40 + 8 * language), dataOffset);
                result.Add(new KeyValuePair<string, string>(name, text));
            }
            return result;
        }

        private static string Text(byte[] b, long offset, long dataOffset)
        {
            if (offset < dataOffset || offset >= b.Length) throw new InvalidDataException("Message text lies outside the file.");
            long end = offset;
            while (end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0)) end += 2;
            if (end - offset > 4096) throw new InvalidDataException("Message text is too long.");
            return Encoding.Unicode.GetString(b, (int)offset, (int)(end - offset));
        }

        // "StageNameMessage41000" = stage 41000 = ess0410_00 -> "0410_00" -> "Pao Pao Cafe 6".
        internal static Dictionary<string, string> StageNames(byte[] file)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in Entries(file, English))
            {
                const string prefix = "StageNameMessage";
                int id;
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || !Int32.TryParse(kv.Key.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 0 || id > 999999) continue;
                string text = (kv.Value ?? "").Trim();
                if (text.Length == 0) continue;
                names[ModCategories.StageKey(id / 100, id % 100)] = text;
            }
            return names;
        }
    }
}
