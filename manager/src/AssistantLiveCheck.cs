using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // Opt-in smoke test against the real Messages API. The key is read only from
    // this process's ANTHROPIC_API_KEY variable and is never printed or saved.
    // Everything it installs goes into a generated fixture under <root>.
    internal static class AssistantLiveCheck
    {
        internal static int Run(string fixtureRoot, string step, string model)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            string run = Path.Combine(Path.GetFullPath(fixtureRoot), "live-" + step + "-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(run);
            Library.IdentityCacheDirectory = Path.Combine(run, "identity-cache");
            string scratchData = Path.Combine(run, "data");
            var store = new AssistantKeyStore(scratchData, delegate { return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"); });
            string source; string key = store.Load(out source);
            // 2.4.0 "cache": may use the key saved in this app's Data (the user's own key; costs a few cents).
            if (key == null && step == "cache") key = new AssistantKeyStore(AppConfig.DataDir, delegate { return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"); }).Load(out source);
            if (key == null || (source != "environment" && step != "cache")) { Console.WriteLine("ANTHROPIC_API_KEY is not set for this process; nothing was sent."); return 2; }
            model = String.IsNullOrEmpty(model) ? AssistantModels.DefaultModel : model;
            if (AssistantModels.Find(model) == null) { Console.WriteLine("Unknown model " + model); return 2; }
            Console.WriteLine("Live check '" + step + "' · model " + model + " · effort low · fixture " + run);
            var log = Path.Combine(run, "assistant-log.txt");
            Func<ClaudeClient> factory = delegate
            {
                var c = new ClaudeClient(key);
                c.Diagnostics = delegate(string line) { File.AppendAllText(log, line + Environment.NewLine); Console.WriteLine("  [http] " + line); };
                return c;
            };
            int exit = 0;
            try
            {
                if (step == "connect") exit = Connect(factory, model);
                else if (step == "full") exit = Full(run, factory, model);
                else if (step == "cache") exit = Cache(run, factory, model);
                else { Console.WriteLine("Unknown step " + step + " (use connect, full or cache)."); exit = 2; }
            }
            catch (ClaudeApiException ex)
            {
                Console.WriteLine("API ERROR status=" + ex.Status + " type=" + ex.ErrorType + " request-id=" + ex.RequestId + " message=" + ex.ApiMessage);
                exit = 1;
            }
            finally
            {
                var hits = AssistantChecks.FilesContaining(run, key);
                Console.WriteLine(hits.Count == 0 ? "PASS key not present in any written file under the live run folder" : "FAIL key found in " + hits.Count + " written file(s)");
                if (hits.Count > 0) exit = 1;
                key = null;
            }
            return exit;
        }

        private static string Money(decimal d) { return "$" + d.ToString("0.0000", CultureInfo.InvariantCulture); }

        private static int Connect(Func<ClaudeClient> factory, string model)
        {
            AssistantModel m = AssistantModels.Find(model);
            var body = new JsonObj().Add("model", model).Add("max_tokens", 2048)
                .Add("thinking", new JsonObj().Add("type", "adaptive").Add("display", "summarized"))
                .Add("output_config", new JsonObj().Add("effort", "low"));
            if (m.ServerFallback) body.Add("fallbacks", "default");
            body.Add("messages", new[] { new JsonObj().Add("role", "user").Add("content", "Reply with the single word: ready") });
            using (ClaudeClient client = factory())
            {
                ClaudeResponse r = client.Send(AssistantJson.Write(body), m.ServerFallback, CancellationToken.None);
                var usage = AssistantJson.Obj(r.Data, "usage");
                string text = String.Join(" ", AssistantJson.List(r.Data, "content").OfType<Dictionary<string,object>>().Where(b => AssistantJson.Str(b, "type") == "text").Select(b => AssistantJson.Str(b, "text")));
                long input = AssistantJson.Long(usage, "input_tokens"), output = AssistantJson.Long(usage, "output_tokens"), cr = AssistantJson.Long(usage, "cache_read_input_tokens"), cw = AssistantJson.Long(usage, "cache_creation_input_tokens");
                Console.WriteLine("HTTP " + r.Status + " · model " + AssistantJson.Str(r.Data, "model") + " · stop_reason " + AssistantJson.Str(r.Data, "stop_reason") + " · text: " + text.Trim());
                Console.WriteLine("USAGE input=" + input + " output=" + output + " cache_read=" + cr + " cache_write=" + cw + " cost≈" + Money(AssistantUsageMeter.Estimate(model, input, output, cr, cw)));
                return r.Status == 200 ? 0 : 1;
            }
        }

        // 2.4.0: two tiny requests in two NEW conversations; the second must read the cached tools + system prefix
        // (instructions and the knowledge pack behind the 1 h breakpoint).
        private static int Cache(string run, Func<ClaudeClient> factory, string model)
        {
            AssistantFixture f = AssistantFixture.Create(Path.Combine(run, "fixture"));
            var settings = new AssistantSettings { Model = model, Effort = "low", AutoApply = false };
            long[] reads = new long[2], writes = new long[2];
            for (int i = 0; i < 2; i++)
            {
                var conv = new AssistantConversation(new AssistantWorkspace(f.Config, settings), factory, model, "low");
                var turn = conv.Send("Reply with the single word: ok", CancellationToken.None);
                var u = conv.Usage.Snapshot().FirstOrDefault();
                reads[i] = u == null ? 0 : u.CacheReadTokens; writes[i] = u == null ? 0 : u.CacheWriteTokens;
                Console.WriteLine("request " + (i + 1) + ": " + turn.Status + " · cache read " + reads[i] + " · cache write " + writes[i] + (u == null ? "" : " · input " + u.InputTokens + " · output " + u.OutputTokens + " · ≈ " + Money(u.Cost)));
            }
            bool ok = reads[1] > 0;
            Console.WriteLine(ok ? "PASS the second conversation read " + reads[1] + " cached tokens" : "FAIL the second conversation read nothing from the cache");
            return ok ? 0 : 1;
        }

        private static int Full(string run, Func<ClaudeClient> factory, string model)
        {
            AssistantFixture f = AssistantFixture.Create(Path.Combine(run, "fixture"));
            var settings = new AssistantSettings { Model = model, Effort = "low", AutoApply = false };
            var ws = new AssistantWorkspace(f.Config, settings);
            var conv = new AssistantConversation(ws, factory, model, "low");
            conv.Events = delegate(AssistantEvent e)
            {
                if (e.Kind == AssistantEventKind.Usage) return;
                string text = e.Text.Replace("\r", " ").Replace("\n", " ");
                if (e.Kind == AssistantEventKind.Thinking && text.Length > 240) text = text.Substring(0, 240) + "…";
                Console.WriteLine("  [" + e.Kind + "] " + text + (e.Action != null ? " {#" + e.Action.Number + " " + e.Action.StateText + "}" : ""));
            };
            decimal before = 0m;
            Func<string, AssistantTurnResult> turn = delegate(string prompt)
            {
                Console.WriteLine("USER> " + prompt);
                AssistantTurnResult t = conv.Send(prompt, CancellationToken.None);
                decimal total = conv.Usage.TotalCost;
                Console.WriteLine("TURN status=" + t.Status + " calls=" + t.ModelCalls + " stop=" + t.StopReason + " cost≈" + Money(total - before) + (t.Message.Length > 0 ? " · " + t.Message : ""));
                foreach (var u in conv.Usage.Snapshot()) Console.WriteLine("  usage so far " + u.Model + ": requests=" + u.Requests + " input=" + u.InputTokens + " output=" + u.OutputTokens + " cache_read=" + u.CacheReadTokens + " cache_write=" + u.CacheWriteTokens + " cost≈" + Money(u.Cost));
                before = total;
                return t;
            };
            AssistantTurnResult b = turn("Install barefoot outfits for Cammy and Juri from my library. I want darker colors, at most one outfit each. Name each one 'Barefoot Dark'.");
            if (b.Status != AssistantTurnStatus.Completed) return 1;
            var pending = conv.Toolbox.PendingActions();
            Console.WriteLine("QUEUED " + pending.Count + ": " + String.Join(" | ", pending.Select(a => "#" + a.Number + " " + a.Title)));
            bool untouched = !Directory.Exists(Path.Combine(f.CostumeRoot, "Cammy", "Barefoot Dark")) && !Directory.Exists(Path.Combine(f.CostumeRoot, "Juri", "Barefoot Dark"));
            Console.WriteLine((untouched ? "PASS" : "FAIL") + " nothing installed before approval");
            if (pending.Count == 0 || !untouched) return 1;
            AssistantBatchResult batch = conv.Apply(pending.Select(a => a.Number), CancellationToken.None, null);
            Console.WriteLine("APPLIED " + batch.Succeeded + " of " + batch.Requested + ": " + String.Join(" | ", batch.Actions.Select(a => "#" + a.Number + " " + a.StateText + " " + a.Result)));
            AssistantTurnResult c = turn("Thanks. Briefly confirm what is installed for Cammy and Juri now.");
            Console.WriteLine("TOTAL cost≈" + Money(conv.Usage.TotalCost) + " (" + conv.Usage.Summary() + ")");
            return c.Status == AssistantTurnStatus.Completed && batch.FailedAction == null ? 0 : 1;
        }
    }

}
