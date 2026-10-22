using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SlotsInstaller
{
    internal sealed class AssistantModel
    {
        internal string Id, DisplayName, Note;
        internal bool ServerFallback;
        // USD per million tokens.
        internal decimal Input, Output, CacheRead, CacheWrite;
        // Optional long-prompt tier, applied per request when its total input exceeds the threshold.
        internal long LongPromptThreshold;
        internal decimal LongInput, LongOutput;
    }

    internal static class AssistantModels
    {
        // Opus/Fable and the xhigh/max efforts are deliberately not offered (2.0.2). Saved
        // settings naming them load as the defaults below.
        // 2.4.0: Claude Haiku 5.5 is the default (listed first). A saved assistant-settings.json always holds an
        // explicit model id (Save writes it), so an existing choice is kept; only a missing file or an unknown id
        // resolves to the default. The effort default is independent of the model and is always sent explicitly.
        internal const string DefaultModel = "claude-haiku-5-5";
        internal const string DefaultEffort = "high";
        internal static readonly string[] Efforts = { "low", "medium", "high" };
        internal static readonly string[] EffortLabels = { "Low", "Medium", "High" };

        // ServerFallback: server-side refusal fallback (fallbacks "default" + beta header). Sonnet 5.5 only;
        // Haiku 5.5 has none (a fallback list 400s there), so its refusals are handled in the client.
        internal static readonly List<AssistantModel> All = new List<AssistantModel>
        {
            new AssistantModel { Id="claude-haiku-5-5", DisplayName="Claude Haiku 5.5", Note="Default · fastest and cheapest", ServerFallback=false, Input=0.10m, Output=0.50m, CacheRead=0.01m, CacheWrite=0.125m, LongPromptThreshold=100000, LongInput=0.50m, LongOutput=2.50m },
            new AssistantModel { Id="claude-sonnet-5-5", DisplayName="Claude Sonnet 5.5", Note="More capable · higher cost", ServerFallback=true, Input=2.00m, Output=10.00m, CacheRead=0.20m, CacheWrite=2.50m }
        };

        internal static AssistantModel Find(string id) { return All.FirstOrDefault(m => String.Equals(m.Id, id, StringComparison.Ordinal)); }
        internal static bool IsEffort(string effort) { return Efforts.Contains(effort ?? "", StringComparer.Ordinal); }
        internal static string EffortLabel(string effort) { int i = Array.IndexOf(Efforts, effort); return i < 0 ? effort : EffortLabels[i]; }
    }

    // Assistant preferences live in their own file so settings.json keeps its format.
    internal sealed class AssistantSettings
    {
        public string Model = AssistantModels.DefaultModel;
        public string Effort = AssistantModels.DefaultEffort;
        public bool AutoApply = false;
        // 2.4.1: Settings → General → "Show Assistant tab" ("show_tab"). Null until ResolveShowTab decides it once.
        public bool? ShowTab;

        internal const string FileName = "assistant-settings.json";
        // Files an earlier run leaves in Data: any of them means an update, which keeps the Assistant tab visible.
        internal static readonly string[] EarlierRunFiles = { "settings.json", "view-settings.json", FileName, AssistantKeyStore.FileName };

        // 2.4.1: whether the header shows the Assistant tab. The first run of 2.4.1 decides it once and stores it:
        // hidden on a new install (none of EarlierRunFiles in Data), visible on an update so nobody's tab disappears.
        internal static bool ResolveShowTab(string dataDir, AssistantSettings settings)
        {
            if (settings.ShowTab.HasValue) return settings.ShowTab.Value;
            bool update = EarlierRunFiles.Any(name => File.Exists(Path.Combine(dataDir, name)));
            settings.ShowTab = update;
            try { SaveShowTab(dataDir, update); }
            catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException)) throw; Program.Log(ex); }
            return update;
        }

        // Writes only "show_tab" into the file and keeps its other members: a model or effort the user never chose
        // stays unset, so it keeps following the default.
        internal static void SaveShowTab(string dataDir, bool show)
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, FileName);
            Safety.NoReparseChain(path);
            Dictionary<string, object> data = null;
            if (File.Exists(path))
            {
                try { data = AssistantJson.ParseObject(File.ReadAllText(path, Encoding.UTF8)); }
                catch (Exception ex) { if (!(ex is FormatException || ex is ArgumentException || ex is InvalidOperationException)) throw; }
            }
            if (data == null) data = new Dictionary<string, object>();
            data["show_tab"] = show;
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, AssistantJson.Write(data), new UTF8Encoding(false));
            DurableFile.Publish(tmp, path);
        }

        internal static AssistantSettings Load(string dataDir)
        {
            string path = Path.Combine(dataDir, FileName);
            var result = new AssistantSettings();
            if (!File.Exists(path)) return result;
            try
            {
                Safety.NoReparseChain(path);
                var data = AssistantJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                string model = AssistantJson.Str(data, "model"), effort = AssistantJson.Str(data, "effort");
                if (AssistantModels.Find(model) != null) result.Model = model;
                if (AssistantModels.IsEffort(effort)) result.Effort = effort;
                object v;
                if (data.TryGetValue("auto_apply", out v) && v is bool) result.AutoApply = (bool)v;
                if (data.TryGetValue("show_tab", out v) && v is bool) result.ShowTab = (bool)v;
            }
            catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException || ex is InvalidOperationException)) throw; }
            return result;
        }

        internal void Save(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, FileName);
            Safety.NoReparseChain(path);
            var members = new JsonObj().Add("model", Model).Add("effort", Effort).Add("auto_apply", AutoApply);
            if (ShowTab.HasValue) members.Add("show_tab", ShowTab.Value);   // older versions ignore it
            string text = AssistantJson.Write(members);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            DurableFile.Publish(tmp, path);
        }
    }

    // The API key is stored only DPAPI-protected for the current Windows user.
    // Callers see a masked status; the value never enters logs, reports or exceptions.
    internal sealed class AssistantKeyStore
    {
        internal const string FileName = "claude-key.dat";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SF6 SlotsLoader Installer/assistant-key/v1");
        private readonly string dataDir;
        private readonly Func<string> environment;

        internal AssistantKeyStore(string dataDir) : this(dataDir, delegate { return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"); }) { }
        internal AssistantKeyStore(string dataDir, Func<string> environmentKey) { this.dataDir = dataDir; environment = environmentKey ?? delegate { return null; }; }

        private string KeyPath { get { return Path.Combine(dataDir, FileName); } }

        internal void Save(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length < 8 || key.Any(c => c < 33 || c > 126)) throw new ArgumentException("Enter a complete API key without spaces.");
            Directory.CreateDirectory(dataDir); Safety.NoReparseChain(KeyPath);
            byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
            string tmp = KeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            DurableFile.Publish(tmp, KeyPath);
        }

        internal bool HasSavedKey { get { return File.Exists(KeyPath); } }

        internal void Remove()
        {
            Safety.NoReparseChain(KeyPath);
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
        }

        // Saved key first, then the environment variable. Returns null when neither exists.
        internal string Load(out string source)
        {
            source = "none";
            if (File.Exists(KeyPath))
            {
                try
                {
                    Safety.NoReparseChain(KeyPath);
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
                    string saved = Encoding.UTF8.GetString(plain).Trim();
                    if (saved.Length > 0) { source = "saved"; return saved; }
                }
                catch (CryptographicException) { source = "unreadable"; }
            }
            string env = (environment() ?? "").Trim();
            if (env.Length > 0) { source = "environment"; return env; }
            return null;
        }

        internal string Status()
        {
            string source; string key = Load(out source);
            if (key == null) return source == "unreadable" ? "This Windows user cannot read the saved key · Remove it and save it again" : "No key";
            string tail = key.Length >= 4 ? key.Substring(key.Length - 4) : "";
            return source == "saved" ? "Saved · …" + tail : "Using ANTHROPIC_API_KEY from environment · …" + tail;
        }
    }

    internal sealed class ModelUsage
    {
        internal string Model;
        internal long Requests, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens;
        internal decimal Cost;
    }

    // Session usage, per model. Dollar values are estimates from the published table.
    internal sealed class AssistantUsageMeter
    {
        private readonly object sync = new object();
        private readonly Dictionary<string,ModelUsage> byModel = new Dictionary<string,ModelUsage>(StringComparer.Ordinal);

        internal static decimal Estimate(string model, long input, long output, long cacheRead, long cacheWrite)
        {
            AssistantModel m = AssistantModels.Find(model);
            if (m == null) return 0m;
            decimal inRate = m.Input, outRate = m.Output;
            if (m.LongPromptThreshold > 0 && input + cacheRead + cacheWrite > m.LongPromptThreshold) { inRate = m.LongInput; outRate = m.LongOutput; }
            return (input * inRate + output * outRate + cacheRead * m.CacheRead + cacheWrite * m.CacheWrite) / 1000000m;
        }

        internal decimal Add(string model, long input, long output, long cacheRead, long cacheWrite)
        {
            decimal cost = Estimate(model, input, output, cacheRead, cacheWrite);
            lock (sync)
            {
                ModelUsage u;
                if (!byModel.TryGetValue(model ?? "", out u)) byModel[model ?? ""] = u = new ModelUsage { Model = model ?? "" };
                u.Requests++; u.InputTokens += input; u.OutputTokens += output; u.CacheReadTokens += cacheRead; u.CacheWriteTokens += cacheWrite; u.Cost += cost;
            }
            return cost;
        }

        internal List<ModelUsage> Snapshot()
        {
            lock (sync) return byModel.Values.Select(u => new ModelUsage { Model=u.Model, Requests=u.Requests, InputTokens=u.InputTokens, OutputTokens=u.OutputTokens, CacheReadTokens=u.CacheReadTokens, CacheWriteTokens=u.CacheWriteTokens, Cost=u.Cost }).ToList();
        }
        internal decimal TotalCost { get { lock (sync) return byModel.Values.Sum(u => u.Cost); } }

        internal string Summary()
        {
            var rows = Snapshot();
            if (rows.Count == 0) return "No usage yet";
            long input = rows.Sum(r => r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens), output = rows.Sum(r => r.OutputTokens);
            return FormatTokens(input) + " in · " + FormatTokens(output) + " out · ≈ $" + rows.Sum(r => r.Cost).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " (estimate)";
        }
        private static string FormatTokens(long n) { return n >= 1000000 ? (n / 1000000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M" : n >= 1000 ? (n / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k" : n.ToString(); }
    }
}
