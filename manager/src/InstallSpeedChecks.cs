using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    internal static class InstallSpeedChecks
    {
        private static string FilePath(int i) { return "natives/stm/benchmark/file" + i.ToString("D5") + ".bin"; }
        internal static int Run(string output, bool largeOnly = false)
        {
            output = Path.GetFullPath(output); Safety.NoReparseChain(output);
            if (Directory.Exists(output)) throw new IOException("Choose a new benchmark directory.");
            Directory.CreateDirectory(output);
            var probe = GameProcesses.Probe; bool measured = InstallMetrics.Enabled;
            GameProcesses.Probe = new FakeProcessProbe(); InstallMetrics.Enabled = true;
            try
            {
                var lines = new List<string> { "case\tsample\tuncached\toperation\tmilliseconds\tlayout_calls" };
                if (!largeOnly)
                {
                    Case(output, "small", 24, 10, 512, 4096, lines);
                    Case(output, "representative", 1027, 368, 286816, 65536, lines);
                }
                // Larger metadata control, with a smaller PAK table to isolate path/state scale.
                Case(output, "large-metadata", 10270, 3680, 4096, 1024, lines);
                File.WriteAllLines(Path.Combine(output, "samples.tsv"), lines);
                return 0;
            }
            finally { GameProcesses.Probe = probe; InstallMetrics.Enabled = measured; }
        }
        private static void Case(string output, string label, int files, int invalidated, int rows, int bytes, List<string> lines)
        {
            var f = ReplaceChecks.Create(Path.Combine(output, label), false, ReplaceMethods.Pak);
            byte[] vanilla = { 7 };
            File.Delete(Path.Combine(f.Game, "re_chunk_000.pak")); // generated baseline from Create, never a real game
            PakWriter.WriteFixture(Path.Combine(f.Game, "re_chunk_000.pak"), Enumerable.Range(0, rows).Select(i => new KeyValuePair<string, byte[]>(i < invalidated - 2 ? FilePath(i) : "natives/stm/benchmark/unused" + i, vanilla)).ToList(), 77, 0);
            PakWriter.WriteFixture(Path.Combine(f.Game, "dlc", "benchmark.pak"), Enumerable.Range(invalidated - 2, 2).Select(i => new KeyValuePair<string, byte[]>(FilePath(i), vanilla)).ToList(), 88, 0);
            byte[] data = new byte[bytes]; for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 13);
            for (int i = 0; i < files; i++) ReplaceChecks.Put(f.Mod("bulk"), FilePath(i), data);
            ReplaceChecks.Put(f.Mod("second"), FilePath(files - 1), new byte[] { 9, 8, 7 });
            var clean = ReplaceChecks.Snapshot(f.Game);
            ReplaceChecks.Install(f, "bulk", "Bulk");
            ReplaceMod second = ReplaceChecks.Install(f, "second", "Second");
            ReplaceChecks.Require(f.Engine.LoadState().Deployed.SelectMany(d => d.Zeroed).Count() == invalidated, "fixture invalidation count");
            string basePath = Path.Combine(f.Game, "re_chunk_000.pak"), dlcPath = Path.Combine(f.Game, "dlc", "benchmark.pak");
            string pak = ReplaceEngine.Sha(basePath), dlc = ReplaceEngine.Sha(dlcPath);
            // First process operation and OS cache state differ: these runs are warm,
            // alternating the old calculation and memo in the same engine/fixture.
            // Large metadata is a parity/stress control, not a timing estimate;
            // representative and small cases get a warm-up and five measured pairs.
            for (int sample = label == "large-metadata" ? 0 : -1; sample < (label == "large-metadata" ? 1 : 5); sample++)
            {
                foreach (bool uncached in sample % 2 == 0 ? new[] { true, false } : new[] { false, true })
                {
                    f.Engine.BenchmarkUncachedLayouts = uncached;
                    Action<string, Action> run = (operation, action) =>
                    {
                        InstallMetrics.Reset(); var clock = Stopwatch.StartNew(); action(); clock.Stop();
                        long calls = InstallMetrics.Calls("reconcile layout hash");
                        ReplaceChecks.Require(calls == (uncached ? invalidated : 2), "unexpected layout calls: " + operation + " " + calls);
                        ReplaceChecks.Require(ReplaceEngine.Sha(basePath) == pak && ReplaceEngine.Sha(dlcPath) == dlc, "PAK bytes changed during same-owner operations");
                        if (sample >= 0)
                        {
                            lines.Add(label + "\t" + sample + "\t" + uncached + "\t" + operation + "\t" + clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + "\t" + calls);
                            File.WriteAllLines(Path.Combine(output, "samples.tsv"), lines);
                            File.WriteAllLines(Path.Combine(output, label + "-" + sample + "-" + uncached + "-" + operation + ".tsv"), InstallMetrics.Lines());
                        }
                    };
                    run("install", () => { second = ReplaceChecks.Install(f, "second", "Second"); });
                    run("disable", () => f.Engine.SetEnabled(second.Id, false));
                    run("enable", () => f.Engine.SetEnabled(second.Id, true));
                    run("priority", () => { var order = f.Engine.LoadState(); f.Engine.Move(second.Id, order.Mods.IndexOf(order.Find(second.Id)) == 0 ? 1 : -1); });
                    var state = f.Engine.LoadState();
                    ReplaceChecks.Require(state.Mods.Count == 2 && state.Mods.All(m => m.Enabled) && state.Deployed.Count == files, "state/enable/priority parity");
                    foreach (var file in state.Deployed) ReplaceChecks.Require(ReplaceEngine.Sha(Safety.Under(f.Game, file.Path)) == file.Sha256, "deployed payload changed");
                }
            }
            f.Engine.BenchmarkUncachedLayouts = false;
            // Same size and timestamp cannot conceal drift in an unaffected file.
            string changed = Safety.Under(f.Game, FilePath(0)); DateTime time = File.GetLastWriteTimeUtc(changed);
            byte[] original = File.ReadAllBytes(changed); byte[] different = (byte[])original.Clone(); different[0] ^= 1;
            File.WriteAllBytes(changed, different); File.SetLastWriteTimeUtc(changed, time);
            foreach (bool old in new[] { false, true }) { f.Engine.BenchmarkUncachedLayouts = old; ReplaceChecks.Refused(() => f.Engine.SetEnabled(second.Id, false), null); }
            File.WriteAllBytes(changed, original); File.SetLastWriteTimeUtc(changed, time);
            f.Engine.BenchmarkUncachedLayouts = false;
            foreach (var mod in f.Engine.LoadState().Mods.ToList()) f.Engine.Uninstall(mod.Id);
            ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), "benchmark restored " + label);
            LayoutIdentity(basePath);
        }
        private static void LayoutIdentity(string path)
        {
            var snapshot = PakIndex.Open(path); string layout = snapshot.LayoutSha256();
            snapshot.Table[0] ^= 1;
            ReplaceChecks.Require(snapshot.LayoutSha256() == layout, "row hashes entered layout identity");
            snapshot.Table[8] ^= 1;
            ReplaceChecks.Require(snapshot.LayoutSha256() != layout, "layout offset ignored");
            snapshot.Table[8] ^= 1; snapshot.Header[12] ^= 1;
            ReplaceChecks.Require(snapshot.LayoutSha256() != layout && PakIndex.Open(path).LayoutSha256() == layout, "snapshot identity reused across instances");
        }
    }
}
