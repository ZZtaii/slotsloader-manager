using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // Development-only aggregate timings. Disabled unless explicitly requested; no
    // file names or payloads are recorded. Fixtures can collect the same counters.
    internal static class InstallMetrics
    {
        internal sealed class Sample { internal long Calls, Ticks, Bytes; }
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Sample> Samples = new Dictionary<string, Sample>();
        internal static bool Enabled;
        static InstallMetrics()
        {
            string output = Environment.GetEnvironmentVariable("SLOTSLOADER_PROFILE_FILE");
            Enabled = !String.IsNullOrWhiteSpace(output);
            if (Enabled) AppDomain.CurrentDomain.ProcessExit += delegate
            {
                try { File.WriteAllLines(output, Lines()); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
            };
        }
        internal static void Reset() { lock (Gate) Samples.Clear(); }
        internal static string[] Lines()
        {
            lock (Gate) return new[] { "phase\tcalls\tmilliseconds\tbytes" }.Concat(Samples.OrderBy(k => k.Key).Select(k => k.Key + "\t" + k.Value.Calls + "\t" + (k.Value.Ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\t" + k.Value.Bytes)).ToArray();
        }
        internal static long Calls(string name) { lock (Gate) { Sample s; return Samples.TryGetValue(name, out s) ? s.Calls : 0; } }
        internal static IDisposable Measure(string name, long bytes = 0) { return Enabled ? new Timing(name, bytes) : null; }
        private sealed class Timing : IDisposable
        {
            private readonly string name; private readonly long bytes, start = Stopwatch.GetTimestamp();
            internal Timing(string phase, long count) { name = phase; bytes = count; }
            public void Dispose()
            {
                long elapsed = Stopwatch.GetTimestamp() - start;
                lock (Gate) { Sample s; if (!Samples.TryGetValue(name, out s)) Samples[name] = s = new Sample(); s.Calls++; s.Ticks += elapsed; s.Bytes += bytes; }
            }
        }
    }
}
