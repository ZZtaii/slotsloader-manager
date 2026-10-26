using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SlotsInstaller
{
    // The Windows application runs in its own Wine prefix. Its native launcher owns the
    // session lock and this loopback helper sees processes in SF6's separate Proton prefix.
    // No fallback may interpret an unavailable native helper as a closed game.
    internal static class LinuxPlatform
    {
        internal const string Unavailable = "Linux integration is unavailable. Close this window and start Slotsloader Manager using its Linux launcher. Further changes are blocked until the connection is restored.";
        internal const string UpdateMessage = "Use Check Linux app updates in Settings or the application menu. Updates keep your Data and compatibility runtime together.";
        internal static bool? TestLinux;
        internal static Func<string, string, Dictionary<string, object>, Dictionary<string, object>> TestRequest;
        private static readonly bool wine = DetectWine();
        private static System.Threading.Timer heartbeat;
        private static int sessionStarted, heartbeatBusy;
        [DllImport("ntdll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr wine_get_version();
        private static bool DetectWine()
        {
            try { return wine_get_version() != IntPtr.Zero; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
        internal static bool IsWine { get { return wine; } }
        internal static bool IsLinux { get { return TestLinux ?? (wine || String.Equals(Environment.GetEnvironmentVariable("SLOTSLOADER_PLATFORM"), "linux", StringComparison.Ordinal)); } }

        internal static void Initialize()
        {
            if (!IsLinux) return;
            Request("POST", "/session", new Dictionary<string, object> { { "event", "started" } });
            Interlocked.Exchange(ref sessionStarted, 1);
            heartbeat = new System.Threading.Timer(delegate(object ignored)
            {
                if (Interlocked.Exchange(ref heartbeatBusy, 1) != 0) return;
                try { Request("POST", "/session", new Dictionary<string, object> { { "event", "heartbeat" } }); }
                catch (IOException) { /* Mutation gates independently require a live helper. */ }
                finally { Interlocked.Exchange(ref heartbeatBusy, 0); }
            }, null, 10000, 10000);
            AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(Environment.ExitCode); };
        }
        internal static void Shutdown(int exitCode)
        {
            if (Interlocked.Exchange(ref sessionStarted, 0) == 0) return;
            System.Threading.Timer timer = heartbeat; heartbeat = null; if (timer != null) timer.Dispose();
            try { Request("POST", "/session", new Dictionary<string, object> { { "event", "exited" }, { "exit_code", exitCode } }); }
            catch (IOException) { }
        }

        // Installer smoke check, deliberately before bridge or app Data initialization.
        internal static int RuntimeProbe(string reportPath)
        {
            var report = new Dictionary<string, object> { { "protocol", 1 }, { "ok", false }, { "framework_release", 0 }, { "mono", Type.GetType("Mono.Runtime") != null }, { "winforms", false } };
            try
            {
                LinuxRendering.Initialize();
                int release = 0;
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = hive.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    { object value = key == null ? null : key.GetValue("Release"); if (value is int) release = Math.Max(release, (int)value); }
                report["framework_release"] = release;
                if ((bool)report["mono"]) throw new IOException("Wine Mono is present. The Manager needs Microsoft .NET Framework 4.8.");
                if (release < 528040) throw new IOException("Microsoft .NET Framework 4.8 is not installed in the Manager environment.");
                report["font_family"] = UiFonts.Verify();
                report["font_monospace"] = UiFonts.Monospace.Name;
                report["font_smoothing"] = UiFonts.SmoothingType;
                report["font_smoothing_enabled"] = UiFonts.SmoothingEnabled;
                LinuxRendering.Verify(report);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new Form())
                using (var text = new TextBox())
                using (var tree = new TreeView())
                using (var list = new ListView())
                {
                    form.Controls.Add(text); form.Controls.Add(tree); form.Controls.Add(list);
                    if (form.Handle == IntPtr.Zero || text.Handle == IntPtr.Zero || tree.Handle == IntPtr.Zero || list.Handle == IntPtr.Zero) throw new IOException("WinForms could not create its native controls.");
                    text.Text = "Slotsloader Manager"; tree.Nodes.Add("Runtime check"); list.Items.Add("Runtime check");
                    using (System.Drawing.Graphics graphics = form.CreateGraphics()) { if (graphics.DpiX <= 0) throw new IOException("Display initialization failed."); }
                }
                report["winforms"] = true; report["ok"] = true;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; report["error"] = ex.Message; }
            string path = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serializer().Serialize(report), new UTF8Encoding(false));
            return (bool)report["ok"] ? 0 : 1;
        }

        internal sealed class ProcessStatus { internal bool GameRunning, FluffyRunning; }
        internal static ProcessStatus Status()
        {
            Dictionary<string, object> result = Request("GET", "/status", null);
            object game, fluffy;
            if (!result.TryGetValue("game_running", out game) || !(game is bool) || !result.TryGetValue("fluffy_running", out fluffy) || !(fluffy is bool))
                throw new IOException(Unavailable);
            return new ProcessStatus { GameRunning = (bool)game, FluffyRunning = (bool)fluffy };
        }
        internal static void RequireClosed(string action)
        {
            if (!IsLinux) return;
            ProcessStatus status = Status();
            if (status.GameRunning) throw new IOException("Close SF6 to " + action + ".");
            if (status.FluffyRunning) throw new IOException("Close the external mod manager to " + action + ".");
        }
        internal static Dictionary<string, object> Discover() { return Request("GET", "/discover", null); }
        internal static string StringValue(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) ? value as string : null; }
        internal static List<string> Strings(Dictionary<string, object> data, string key)
        {
            var result = new List<string>(); object value;
            if (data.TryGetValue(key, out value) && value is IEnumerable && !(value is string))
                foreach (object item in (IEnumerable)value) { string text = item as string; if (!String.IsNullOrWhiteSpace(text)) result.Add(text); }
            return result;
        }
        internal static string CanonicalRoot(string path, string kind)
        {
            if (!IsLinux || String.IsNullOrWhiteSpace(path)) return path;
            var result = Request("POST", "/validate", new Dictionary<string, object> { { "path", path }, { "kind", kind } });
            string canonical = StringValue(result, "path");
            if (String.IsNullOrWhiteSpace(canonical) || !Path.IsPathRooted(canonical)) throw new IOException("Linux returned an invalid folder path; the folder was not changed.");
            return canonical;
        }
        internal static string WinePath(string path)
        { return path != null && path.StartsWith("/", StringComparison.Ordinal) ? "Z:" + path.Replace('/', '\\') : path; }
        internal static void ValidateContainedPath(string path, string root)
        {
            Request("POST", "/contained", new Dictionary<string, object> { { "path", Path.GetFullPath(path) }, { "root", Path.GetFullPath(root) } });
        }
        internal static void OpenPath(string path, bool reveal)
        { Request("POST", "/open", new Dictionary<string, object> { { "path", Path.GetFullPath(path) }, { "reveal", reveal } }); }
        internal static void OpenUrl(string url)
        { Request("POST", "/open", new Dictionary<string, object> { { "url", url } }); }
        internal static void Launch() { Request("POST", "/launch", new Dictionary<string, object>()); }
        internal static void RequestUpdate() { Request("POST", "/update", new Dictionary<string, object>()); }
        internal static void Trash(string path)
        { Request("POST", "/trash", new Dictionary<string, object> { { "path", Path.GetFullPath(path) } }); }

        internal static Dictionary<string, object> Request(string method, string route, Dictionary<string, object> body)
        {
            Dictionary<string, object> result;
            if (TestRequest != null) result = TestRequest(method, route, body);
            else
            {
                Uri endpoint;
                string url = Environment.GetEnvironmentVariable("SLOTSLOADER_BRIDGE_URL"), token = Environment.GetEnvironmentVariable("SLOTSLOADER_BRIDGE_TOKEN");
                if (!Uri.TryCreate(url, UriKind.Absolute, out endpoint) || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.Port < 1 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0 || String.IsNullOrEmpty(token) || token.Length < 32 || token.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new IOException(Unavailable);
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(new Uri(endpoint, route));
                    request.Method = method; request.Proxy = null; request.AllowAutoRedirect = false;
                    // This local server reads short JSON bodies immediately. Avoid .NET's
                    // 350 ms Expect: 100-continue pause on every per-file safety check.
                    request.ServicePoint.Expect100Continue = false;
                    int timeout = route == "/trash" ? 35000 : 3000;
                    request.Timeout = timeout; request.ReadWriteTimeout = timeout; request.KeepAlive = false;
                    request.Headers["X-Slotsloader-Token"] = token;
                    if (method == "POST")
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(Json.Serializer().Serialize(body ?? new Dictionary<string, object>()));
                        request.ContentType = "application/json"; request.ContentLength = bytes.Length;
                        using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                    }
                    HttpWebResponse reply;
                    try { reply = (HttpWebResponse)request.GetResponse(); }
                    catch (WebException failure)
                    {
                        reply = failure.Response as HttpWebResponse;
                        if (reply == null || reply.StatusCode != HttpStatusCode.Conflict) { if (reply != null) reply.Dispose(); throw; }
                    }
                    using (var response = reply)
                    using (Stream input = response.GetResponseStream())
                    using (var output = new MemoryStream())
                    {
                        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Conflict) throw new IOException(Unavailable);
                        byte[] buffer = new byte[4096]; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        { if (output.Length + read > 256 * 1024) throw new IOException(Unavailable); output.Write(buffer, 0, read); }
                        result = Json.Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(output.ToArray()));
                        object accepted;
                        if (response.StatusCode == HttpStatusCode.Conflict && (result == null || !result.TryGetValue("ok", out accepted) || !(accepted is bool) || (bool)accepted)) throw new IOException(Unavailable);
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is WebException || ex is IOException || ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException)) throw;
                    // Never include request headers, response bodies or environment values in logs.
                    throw new IOException(Unavailable);
                }
            }
            object protocol, ok;
            if (result == null || !result.TryGetValue("protocol", out protocol) || !(protocol is int) || (int)protocol != 1 || !result.TryGetValue("ok", out ok) || !(ok is bool)) throw new IOException(Unavailable);
            if (!(bool)ok)
            {
                string error = StringValue(result, "error");
                throw new IOException(String.IsNullOrWhiteSpace(error) ? Unavailable : "Linux integration: " + error);
            }
            return result;
        }
    }

    internal sealed class LinuxRecycleBin : IRecycleBin
    {
        // The host confirms its trash operation. A failure never asks to delete permanently:
        // retain the item and let the user retry or manage it in their native file manager.
        public string Unavailable(string path, bool folder, long bytes) { LinuxPlatform.Status(); return null; }
        public void Recycle(string path, bool folder) { LinuxPlatform.Trash(path); }
        public void DeletePermanently(string path, bool folder) { throw new IOException("Use your Linux file manager for permanent deletion."); }
    }
}
