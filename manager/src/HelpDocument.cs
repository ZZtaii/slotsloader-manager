using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 1.0.11 (the user): the in-app Help (gear → Help, F1) reads docs/HELP.md, embedded in both exes as
    // SlotsInstaller.Help.md. The Assistant's knowledge pack reads the same file. Format (see the file's own header):
    // "# Group", "## Topic", "> Lead" right after a topic, "### Heading", "- Item", "!! TITLE: text" (a note card),
    // other lines are paragraphs; inline `code`, **bold** and links to other topics: [[Topic]], [[Topic#Heading]] (that
    // heading of the topic), either with |shown text.
    internal enum HelpBlockKind { Heading, Paragraph, Item, Note }

    internal sealed class HelpBlock
    {
        internal HelpBlockKind Kind;
        internal string Title = "", Text = "";
    }

    internal sealed class HelpTopic
    {
        internal string Group = "", Title = "", Lead = "";
        internal readonly List<HelpBlock> Blocks = new List<HelpBlock>();
        // Everything a search looks at, lower-case and without the inline marks.
        internal string SearchText { get { return HelpDocument.Plain(Title + " " + Lead + " " + String.Join(" ", Blocks.Select(b => b.Title + " " + b.Text))).ToLowerInvariant(); } }
        internal bool Matches(string query)
        {
            string[] words = (query ?? "").ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            string text = SearchText;
            return words.All(w => text.Contains(w));
        }
    }

    // One piece of inline text: plain, **bold**, `code` or a link (Link = "Topic" or "Topic#Heading").
    internal sealed class HelpRun
    {
        internal string Text = "", Link;
        internal bool Bold, Code;
    }

    internal static class HelpDocument
    {
        internal const string ResourceName = "SlotsInstaller.Help.md";
        private static List<HelpTopic> cached;

        // The embedded topics (empty when a test harness builds without the resource).
        internal static List<HelpTopic> Topics
        {
            get
            {
                if (cached != null) return cached;
                string text = "";
                using (Stream s = typeof(HelpDocument).Assembly.GetManifestResourceStream(ResourceName))
                    if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
                return cached = Parse(text);
            }
        }

        internal static List<HelpTopic> Parse(string text)
        {
            var topics = new List<HelpTopic>();
            string body = text ?? "";
            int comment = body.IndexOf("<!--", StringComparison.Ordinal);
            while (comment >= 0)
            {
                int end = body.IndexOf("-->", comment, StringComparison.Ordinal);
                body = end < 0 ? body.Substring(0, comment) : body.Remove(comment, end + 3 - comment);
                comment = body.IndexOf("<!--", StringComparison.Ordinal);
            }
            string group = "";
            HelpTopic topic = null;
            var paragraph = new StringBuilder();
            Action flush = delegate
            {
                if (topic != null && paragraph.Length > 0) topic.Blocks.Add(new HelpBlock { Kind = HelpBlockKind.Paragraph, Text = paragraph.ToString().Trim() });
                paragraph.Clear();
            };
            foreach (string raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                string t = line.TrimStart();
                if (t.Length == 0) { flush(); continue; }
                if (t.StartsWith("### ")) { flush(); if (topic != null) topic.Blocks.Add(new HelpBlock { Kind = HelpBlockKind.Heading, Text = t.Substring(4).Trim() }); }
                else if (t.StartsWith("## ")) { flush(); topic = new HelpTopic { Group = group, Title = t.Substring(3).Trim() }; topics.Add(topic); }
                else if (t.StartsWith("# ")) { flush(); group = t.Substring(2).Trim(); topic = null; }
                else if (t.StartsWith("> ") && topic != null && topic.Blocks.Count == 0 && topic.Lead.Length == 0 && paragraph.Length == 0) topic.Lead = t.Substring(2).Trim();
                else if (t.StartsWith("- ")) { flush(); if (topic != null) topic.Blocks.Add(new HelpBlock { Kind = HelpBlockKind.Item, Text = t.Substring(2).Trim() }); }
                else if (t.StartsWith("!! "))
                {
                    flush();
                    string note = t.Substring(3).Trim(); int colon = note.IndexOf(':');
                    if (topic != null) topic.Blocks.Add(new HelpBlock { Kind = HelpBlockKind.Note, Title = colon > 0 ? note.Substring(0, colon).Trim() : "", Text = colon > 0 ? note.Substring(colon + 1).Trim() : note });
                }
                else { if (paragraph.Length > 0) paragraph.Append(' '); paragraph.Append(t); }
            }
            flush();
            return topics;
        }

        // The text as it reads on the page: the marks removed, links as their shown text.
        internal static string Plain(string text) { return String.Concat(Runs(text).Select(r => r.Text)); }

        // The topic a link names ("Topic" or "Topic#Heading", any case); `heading` is the part after # (or null).
        internal static HelpTopic Find(string target, out string heading)
        {
            string t = target ?? ""; int hash = t.IndexOf('#');
            heading = hash >= 0 ? t.Substring(hash + 1).Trim() : null;
            string title = (hash >= 0 ? t.Substring(0, hash) : t).Trim();
            return Topics.FirstOrDefault(p => String.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
        }

        // `code`, **bold** and [[links]]; an unclosed mark is plain text.
        internal static List<HelpRun> Runs(string text)
        {
            var runs = new List<HelpRun>();
            string s = text ?? "";
            int i = 0;
            var plain = new StringBuilder();
            Action flush = delegate { if (plain.Length > 0) { runs.Add(new HelpRun { Text = plain.ToString() }); plain.Clear(); } };
            while (i < s.Length)
            {
                if (s[i] == '[' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    int end = s.IndexOf("]]", i + 2, StringComparison.Ordinal);
                    if (end > i)
                    {
                        flush();
                        string inner = s.Substring(i + 2, end - i - 2); int bar = inner.IndexOf('|');
                        string target = (bar >= 0 ? inner.Substring(0, bar) : inner).Trim(), shown = bar >= 0 ? inner.Substring(bar + 1).Trim() : null;
                        int hash = target.IndexOf('#');
                        runs.Add(new HelpRun { Link = target, Text = shown ?? (hash >= 0 ? target.Substring(hash + 1).Trim() : target) });
                        i = end + 2; continue;
                    }
                }
                if (s[i] == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i) { flush(); runs.Add(new HelpRun { Text = s.Substring(i + 1, end - i - 1), Code = true }); i = end + 1; continue; }
                }
                else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i) { flush(); runs.Add(new HelpRun { Text = s.Substring(i + 2, end - i - 2), Bold = true }); i = end + 2; continue; }
                }
                plain.Append(s[i]); i++;
            }
            flush();
            return runs;
        }
    }
}
