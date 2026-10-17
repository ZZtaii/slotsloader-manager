using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // A failed request or download: the reason in plain words, and the HTTP status when the server answered.
    internal sealed class DownloadFailure : IOException
    {
        internal readonly string Reason;
        internal readonly int Status;
        internal DownloadFailure(string reason, int status) : base(reason) { Reason = reason; Status = status; }
    }

    // 2.4.0: the one HTTP path of the app (the pinned SlotsLoader download since 2.3.1, the update check and update
    // downloads since 2.4.0). HTTPS only (plain http only to a loopback address when a test allows it), redirects
    // followed by hand and only to HTTPS, system proxy, time-outs, cancellable through OperationContext. A download
    // goes to <name>.part, stops as soon as it is longer than expected, and is renamed only after its size and
    // SHA-256 match; any failure deletes the .part file.
    internal static class SafeHttp
    {
        internal static bool AllowLoopbackHttp;   // tests only
        internal static int TimeoutMs = 30000;
        internal const int MaxRedirects = 5;

        static SafeHttp()
        {
            SecurityProtocolType tls13;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (Enum.TryParse("Tls13", out tls13)) { try { ServicePointManager.SecurityProtocol |= tls13; } catch (NotSupportedException) { } }
        }

        internal static string AppVersion()
        {
            var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(SafeHttp).Assembly, typeof(AssemblyInformationalVersionAttribute));
            return info == null ? "2" : info.InformationalVersion;
        }

        // A small text response (the releases API, SHA256SUMS.txt), at most maxBytes.
        internal static string GetText(string url, long maxBytes, string accept, int timeoutMs)
        {
            CancellationToken token = OperationContext.Token;
            using (HttpWebResponse response = Open(new Uri(url), token, accept, timeoutMs, maxBytes, "expected"))
            using (Stream body = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[16384]; int read;
                try
                {
                    while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        memory.Write(buffer, 0, read);
                        if (memory.Length > maxBytes) throw new DownloadFailure("the response from " + response.ResponseUri.Host + " is over " + maxBytes + " bytes", 0);
                    }
                }
                catch (WebException ex) { token.ThrowIfCancellationRequested(); throw new DownloadFailure(Reason(ex, response.ResponseUri, timeoutMs), 0); }
                catch (IOException ex) { if (ex is DownloadFailure) throw; token.ThrowIfCancellationRequested(); throw new DownloadFailure("the response was cut off (" + ex.Message.TrimEnd('.') + ")", 0); }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }

        // Downloads url to dir\fileName (verified). `word` names the expectation in messages ("pinned", "published").
        internal static string Download(string url, string dir, string fileName, long size, string sha256, string stage, string word)
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, fileName), part = target + ".part";
            CancellationToken token = OperationContext.Token;
            try
            {
                string hash; long received;
                using (HttpWebResponse response = Open(new Uri(url), token, null, TimeoutMs, size, word))
                {
                    try { hash = Receive(response.GetResponseStream(), part, size, stage, token, word, out received); }
                    catch (Exception ex)
                    {
                        if (ex is DownloadFailure) throw;
                        token.ThrowIfCancellationRequested();
                        if (ex is WebException) throw new DownloadFailure(Reason((WebException)ex, response.ResponseUri, TimeoutMs), 0);
                        if (ex is IOException) throw new DownloadFailure("the download was cut off (" + ex.Message.TrimEnd('.') + ")", 0);
                        throw;
                    }
                }
                if (received != size) throw new DownloadFailure("the download is " + received + " bytes, not the " + word + " " + size, 0);
                if (hash != sha256.ToUpperInvariant()) throw new DownloadFailure("the downloaded file's SHA-256 (" + hash.Substring(0, 12) + "…) does not match the " + word + " build (" + sha256.Substring(0, Math.Min(12, sha256.Length)).ToUpperInvariant() + "…); it was deleted", 0);
                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
                return Path.GetFullPath(target);
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        // GET with redirects followed by hand (each target must be HTTPS); a 200 response, or DownloadFailure.
        private static HttpWebResponse Open(Uri uri, CancellationToken token, string accept, int timeoutMs, long limit, string word)
        {
            uri = Allowed(uri, false);
            for (int hop = 0; ; hop++)
            {
                token.ThrowIfCancellationRequested();
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "GET"; request.AllowAutoRedirect = false; request.Timeout = timeoutMs; request.ReadWriteTimeout = timeoutMs;
                request.AutomaticDecompression = DecompressionMethods.None; request.KeepAlive = false;
                request.UserAgent = "Slotsloader-Manager/" + AppVersion();
                if (accept != null) request.Accept = accept;
                if (uri.IsLoopback) request.Proxy = null;
                else { IWebProxy proxy = WebRequest.GetSystemWebProxy(); proxy.Credentials = CredentialCache.DefaultCredentials; request.Proxy = proxy; }
                HttpWebResponse response;
                using (token.Register(request.Abort))
                {
                    try { response = (HttpWebResponse)request.GetResponse(); }
                    catch (WebException ex)
                    {
                        token.ThrowIfCancellationRequested();
                        var http = ex.Response as HttpWebResponse;
                        throw new DownloadFailure(Reason(ex, uri, timeoutMs), ex.Status == WebExceptionStatus.ProtocolError && http != null ? (int)http.StatusCode : 0);
                    }
                }
                int code = (int)response.StatusCode;
                if (code >= 300 && code < 400)
                {
                    string location = response.Headers["Location"]; response.Close();
                    if (hop >= MaxRedirects) throw new DownloadFailure("too many redirects from " + uri.Host, 0);
                    if (String.IsNullOrWhiteSpace(location)) throw new DownloadFailure(uri.Host + " sent a redirect without a target", 0);
                    uri = Allowed(new Uri(uri, location), true);
                    continue;
                }
                if (code != 200) { string text = "HTTP " + code + " " + response.StatusDescription + " from " + uri.Host; response.Close(); throw new DownloadFailure(text, code); }
                if (response.ContentLength > limit) { string text = "the file on " + uri.Host + " is " + response.ContentLength + " bytes, larger than the " + word + " " + limit; response.Close(); throw new DownloadFailure(text, 0); }
                return response;
            }
        }

        private static string Receive(Stream body, string part, long size, string stage, CancellationToken token, string word, out long received)
        {
            received = 0;
            using (body)
            using (var sha = FastHash.Sha256())
            using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; int read;
                while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    received += read;
                    if (received > size) throw new DownloadFailure("the download grew past the " + word + " " + size + " bytes and was stopped", 0);
                    output.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    OperationContext.Report(stage, received, size);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                token.ThrowIfCancellationRequested();
                return Hex(sha.Hash);
            }
        }

        private static Uri Allowed(Uri uri, bool redirect)
        {
            if (uri.Scheme == Uri.UriSchemeHttps) return uri;
            if (AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return uri;
            throw new DownloadFailure(redirect ? "the server redirected to a non-HTTPS address (" + uri.Scheme + "://" + uri.Host + ")" : "the address is not HTTPS (" + uri.Scheme + "://" + uri.Host + ")", 0);
        }

        internal static string Reason(WebException ex, Uri uri, int timeoutMs)
        {
            string host = uri.Host;
            var http = ex.Response as HttpWebResponse;
            if (ex.Status == WebExceptionStatus.ProtocolError && http != null)
            {
                int code = (int)http.StatusCode; http.Close();
                return "HTTP " + code + " " + http.StatusDescription + " from " + host + (code == 404 ? " (the release isn't public or no longer exists)" : "");
            }
            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure: return host + " could not be found; check your internet connection";
                case WebExceptionStatus.ProxyNameResolutionFailure: return "the proxy server could not be found";
                case WebExceptionStatus.ConnectFailure: return "could not connect to " + host;
                case WebExceptionStatus.Timeout: return "no answer from " + host + " within " + (timeoutMs / 1000) + " s";
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.TrustFailure: return "the secure connection to " + host + " failed";
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ReceiveFailure: return host + " closed the connection";
                default: return host + ": " + ex.Status + " (" + ex.Message.TrimEnd('.') + ")";
            }
        }

        internal static string HashFile(string path)
        {
            using (var sha = FastHash.Sha256())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                return Hex(sha.ComputeHash(stream));
        }
        internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
    }
}
