using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // A scripted Assistant conversation against a local mock of the Messages API
    // and generated fixtures. Used by the UI checks and the screenshot renders;
    // no real key, network service, library or game folder is involved.
    internal sealed class AssistantDemo : IDisposable
    {
        internal const string DemoKey = "sk-ant-demo-not-a-real-key-0000DEMO";
        internal const string Prompt = "Install barefoot outfits for Cammy and Juri, darker colors, max 1 each";
        internal readonly AssistantFixture Fixture;
        internal readonly MockAnthropicServer Server;
        internal int FirstReplyDelayMs;
        private int counter;

        private AssistantDemo(string root)
        {
            Fixture = AssistantFixture.Create(root);
            Server = new MockAnthropicServer(Reply);
        }

        internal static AssistantDemo Attach(AssistantPage page, string root)
        {
            Library.IdentityCacheDirectory = Path.Combine(root + "-cache");
            var demo = new AssistantDemo(root);
            string baseUrl = demo.Server.BaseUrl;
            page.KeyOverride = delegate { return DemoKey; };
            page.ClientFactory = delegate(string key) { return new ClaudeClient(key, baseUrl) { BackoffBaseSeconds = 0.02 }; };
            page.Rebind(demo.Fixture.Config, null);
            return demo;
        }

        public void Dispose() { Server.Dispose(); }

        private static string Q(string s) { var sb = new StringBuilder(); AssistantJson.WriteString(sb, s); return sb.ToString(); }
        private static string Text(string t) { return "{\"type\":\"text\",\"text\":" + Q(t) + "}"; }
        private static string Think(string t) { return "{\"type\":\"thinking\",\"thinking\":" + Q(t) + ",\"signature\":\"demo-signature\"}"; }
        private static string Use(string id, string name, JsonObj input) { return "{\"type\":\"tool_use\",\"id\":" + Q(id) + ",\"name\":" + Q(name) + ",\"input\":" + AssistantJson.Write(input) + "}"; }
        private string Message(string stop, int input, int output, int cacheRead, int cacheWrite, params string[] blocks)
        {
            int n = Interlocked.Increment(ref counter);
            return "{\"id\":\"msg_demo_" + n + "\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5-5\",\"content\":[" + String.Join(",", blocks) + "],\"stop_reason\":" + Q(stop) + ",\"stop_details\":null,\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":" + cacheWrite + ",\"cache_read_input_tokens\":" + cacheRead + "}}";
        }
        private static JsonObj Install(string mod, string[] options, string character, string name) { return new JsonObj().Add("mod_id", mod).Add("option_ids", options).Add("character", character).Add("outfit_name", name).Add("replace_installed_id", ""); }

        private MockReply Reply(MockRequest request, int index)
        {
            string cammy = Fixture.CammyPackId, juri = Fixture.JuriPackId;
            string body;
            switch (index)
            {
                case 0:
                    body = Message("tool_use", 2900, 210, 0, 4200,
                        Think("The user wants one darker barefoot outfit each for Cammy and Juri. I'll search the library for barefoot mods for both characters first."),
                        Text("I'll look for barefoot outfits for Cammy and Juri."),
                        Use("toolu_demo_1", "search_mods", new JsonObj().Add("query", "barefoot").Add("character", "Cammy").Add("kind", "any").Add("install_state", "any").Add("limit", 0).Add("offset", 0).Add("costume", "").Add("sort", "").Add("order", "")),
                        Use("toolu_demo_2", "search_mods", new JsonObj().Add("query", "barefoot").Add("character", "Juri").Add("kind", "any").Add("install_state", "any").Add("limit", 0).Add("offset", 0).Add("costume", "").Add("sort", "").Add("order", "")));
                    return new MockReply { Body = body, DelayMs = FirstReplyDelayMs };
                case 1:
                    body = Message("tool_use", 640, 120, 4200, 900, Use("toolu_demo_3", "get_mod_options", new JsonObj().Add("mod_id", cammy)), Use("toolu_demo_4", "get_mod_options", new JsonObj().Add("mod_id", juri)));
                    break;
                case 2:
                    body = Message("tool_use", 1100, 160, 5100, 1300,
                        Think("Both packs have a required Color group. Black and Midnight are the darkest names. I'll check for a saved preset first."),
                        Use("toolu_demo_5", "list_presets", new JsonObj()));
                    break;
                case 3:
                    body = Message("tool_use", 900, 190, 6400, 600,
                        Use("toolu_demo_6", "check_outfit", new JsonObj().Add("mod_id", cammy).Add("option_ids", new[] { "Main Files", "Color/Black" }).Add("character", "Cammy")),
                        Use("toolu_demo_7", "check_outfit", new JsonObj().Add("mod_id", juri).Add("option_ids", new[] { "Main Files", "Color/Midnight" }).Add("character", "Juri")),
                        Use("toolu_demo_8", "list_installed", new JsonObj().Add("character", "")));
                    break;
                case 4:
                    body = Message("tool_use", 700, 230, 7000, 800,
                        Use("toolu_demo_9", "install_outfit", Install(cammy, new[] { "Main Files", "Color/Black" }, "Cammy", "Barefoot Black")),
                        Use("toolu_demo_10", "install_outfit", Install(juri, new[] { "Main Files", "Color/Midnight" }, "Juri", "Barefoot Midnight")));
                    break;
                default:
                    body = Message("end_turn", 520, 260, 7800, 450, Text("I queued two outfits for your approval:\n• #1 Cammy · \"Barefoot Black\" from Cammy Barefoot Pack (Main Files + Black)\n• #2 Juri · \"Barefoot Midnight\" from Juri Barefoot Variants (Main Files + Midnight)\n\nBoth passed the install check and the names are free. Nothing is installed until you apply them in the Plan; restart SF6 afterwards to see them."));
                    break;
            }
            return new MockReply { Body = body };
        }
    }
}
