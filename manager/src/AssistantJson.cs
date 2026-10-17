using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace SlotsInstaller
{
    // Insertion-ordered JSON object. Request bodies and tool results are
    // written in a fixed member order so the prompt-cache prefix stays stable.
    internal sealed class JsonObj : IEnumerable<KeyValuePair<string,object>>
    {
        private readonly List<KeyValuePair<string,object>> members = new List<KeyValuePair<string,object>>();
        internal JsonObj Add(string key, object value) { members.Add(new KeyValuePair<string,object>(key, value)); return this; }
        internal int Count { get { return members.Count; } }
        public IEnumerator<KeyValuePair<string,object>> GetEnumerator() { return members.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return members.GetEnumerator(); }
    }

    // Already-serialized JSON that is written verbatim. Assistant turns are kept
    // as the exact bytes the API returned, so thinking signatures replay unchanged.
    internal sealed class JsonRaw
    {
        internal readonly string Text;
        internal JsonRaw(string text) { Text = text; }
    }

    internal static class AssistantJson
    {
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 }; }

        internal static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            JsonRaw raw = value as JsonRaw;
            if (raw != null) { sb.Append(raw.Text); return; }
            string s = value as string;
            if (s != null) { WriteString(sb, s); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is int || value is long || value is short || value is byte || value is uint || value is ulong) { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is double || value is float || value is decimal) { sb.Append(Convert.ToDecimal(value).ToString(CultureInfo.InvariantCulture)); return; }
            JsonObj obj = value as JsonObj;
            if (obj != null)
            {
                sb.Append('{'); bool first = true;
                foreach (var pair in obj) { if (!first) sb.Append(','); first = false; WriteString(sb, pair.Key); sb.Append(':'); WriteValue(sb, pair.Value); }
                sb.Append('}'); return;
            }
            IDictionary dict = value as IDictionary;
            if (dict != null)
            {
                sb.Append('{'); bool first = true;
                foreach (DictionaryEntry pair in dict) { if (!first) sb.Append(','); first = false; WriteString(sb, Convert.ToString(pair.Key, CultureInfo.InvariantCulture)); sb.Append(':'); WriteValue(sb, pair.Value); }
                sb.Append('}'); return;
            }
            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('['); bool first = true;
                foreach (object item in list) { if (!first) sb.Append(','); first = false; WriteValue(sb, item); }
                sb.Append(']'); return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        internal static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        internal static Dictionary<string,object> ParseObject(string text)
        {
            object parsed = Serializer().DeserializeObject(text);
            var result = parsed as Dictionary<string,object>;
            if (result == null) throw new FormatException("Expected a JSON object.");
            return result;
        }

        // Returns the exact source text of one top-level member of a JSON object,
        // or null when the member is absent. Only the outer object is walked.
        internal static string RawMember(string json, string name)
        {
            int i = 0;
            SkipSpace(json, ref i);
            if (i >= json.Length || json[i] != '{') throw new FormatException("Expected a JSON object.");
            i++;
            while (true)
            {
                SkipSpace(json, ref i);
                if (i < json.Length && json[i] == '}') return null;
                int keyStart = i; SkipString(json, ref i);
                string key = json.Substring(keyStart + 1, i - keyStart - 2);
                if (key.IndexOf('\\') >= 0) key = Convert.ToString(Serializer().Deserialize<string[]>("[" + json.Substring(keyStart, i - keyStart) + "]")[0]);
                SkipSpace(json, ref i);
                if (i >= json.Length || json[i] != ':') throw new FormatException("Malformed JSON object.");
                i++; SkipSpace(json, ref i);
                int valueStart = i; SkipValue(json, ref i);
                if (key == name) return json.Substring(valueStart, i - valueStart);
                SkipSpace(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                if (i < json.Length && json[i] == '}') return null;
                throw new FormatException("Malformed JSON object.");
            }
        }

        private static void SkipSpace(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++; }
        private static void SkipString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected a JSON string.");
            i++;
            while (i < s.Length)
            {
                if (s[i] == '\\') { i += 2; continue; }
                if (s[i] == '"') { i++; return; }
                i++;
            }
            throw new FormatException("Unterminated JSON string.");
        }
        private static void SkipValue(string s, ref int i)
        {
            if (i >= s.Length) throw new FormatException("Missing JSON value.");
            char c = s[i];
            if (c == '"') { SkipString(s, ref i); return; }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char d = s[i];
                    if (d == '"') { SkipString(s, ref i); continue; }
                    if (d == '{' || d == '[') depth++;
                    else if (d == '}' || d == ']') { depth--; if (depth == 0) { i++; return; } }
                    i++;
                }
                throw new FormatException("Unterminated JSON container.");
            }
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && s[i] != ' ' && s[i] != '\r' && s[i] != '\n' && s[i] != '\t') i++;
        }

        // Typed accessors for JavaScriptSerializer output.
        internal static string Str(IDictionary<string,object> d, string key)
        {
            object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }
        internal static long Long(IDictionary<string,object> d, string key)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (FormatException) { return 0; } catch (InvalidCastException) { return 0; } catch (OverflowException) { return 0; }
        }
        internal static Dictionary<string,object> Obj(IDictionary<string,object> d, string key)
        {
            object v; return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string,object> : null;
        }
        internal static List<object> List(IDictionary<string,object> d, string key)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || v == null) return new List<object>();
            var array = v as object[]; if (array != null) return new List<object>(array);
            var list = v as ArrayList; if (list != null) return new List<object>(list.ToArray());
            return new List<object>();
        }
    }
}
