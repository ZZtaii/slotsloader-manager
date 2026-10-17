using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlotsInstaller
{
    internal sealed class ClaudeResponse
    {
        internal int Status, Attempts;
        internal string RequestId, RawBody, RawContent;
        internal Dictionary<string,object> Data;
    }

    internal sealed class ClaudeApiException : Exception
    {
        internal readonly int Status;
        internal readonly string ErrorType, ApiMessage, RequestId;
        internal readonly int Attempts;
        internal ClaudeApiException(string message, int status, string errorType, string apiMessage, string requestId, int attempts, Exception inner)
            : base(message, inner) { Status = status; ErrorType = errorType ?? ""; ApiMessage = apiMessage ?? ""; RequestId = requestId ?? ""; Attempts = attempts; }
    }

    // Messages API over HTTPS. The official SDK needs a newer .NET than this
    // portable C# 5 / .NET Framework build, so requests are formed directly.
    internal sealed class ClaudeClient : IDisposable
    {
        internal const string DefaultBaseUrl = "https://api.anthropic.com";
        internal const string ApiVersion = "2023-06-01";
        internal const string FallbackBeta = "server-side-fallback-2026-07-01";

        private readonly string apiKey, endpoint;
        private readonly HttpClient http;
        private static readonly Random Jitter = new Random();

        internal int MaxAttempts = 3;
        internal double BackoffBaseSeconds = 1.0;
        internal TimeSpan RetryAfterCap = TimeSpan.FromSeconds(90);
        // Receives one redacted line per HTTP attempt (status, request-id, timing). Never bodies or the key.
        internal Action<string> Diagnostics;
        // Test hook: observes every wait before a retry.
        internal Action<TimeSpan> RetryWaitObserved;

        static ClaudeClient()
        {
            SecurityProtocolType tls13;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (Enum.TryParse("Tls13", out tls13)) { try { ServicePointManager.SecurityProtocol |= tls13; } catch (NotSupportedException) { } }
        }

        internal ClaudeClient(string apiKey) : this(apiKey, DefaultBaseUrl) { }
        internal ClaudeClient(string apiKey, string baseUrl)
        {
            if (String.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Anthropic API key. Add one in Settings.");
            this.apiKey = apiKey.Trim();
            Uri baseUri = new Uri(String.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/') + "/");
            endpoint = new Uri(baseUri, "v1/messages").ToString();
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            if (baseUri.IsLoopback) handler.UseProxy = false;
            http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.ExpectContinue = false;
        }

        public void Dispose() { http.Dispose(); }

        internal string Redact(string text)
        {
            if (String.IsNullOrEmpty(text)) return text ?? "";
            return text.Replace(apiKey, "[redacted]");
        }

        // Header listing for diagnostics; the key is never shown.
        internal static string DescribeHeaders(bool serverFallback)
        {
            return "POST /v1/messages; x-api-key: [redacted]; anthropic-version: " + ApiVersion + "; content-type: application/json" + (serverFallback ? "; anthropic-beta: " + FallbackBeta : "");
        }

        internal ClaudeResponse Send(string body, bool serverFallback, CancellationToken token)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                int status = 0; string requestId = "", text = null; TimeSpan? retryAfter = null; Exception network = null;
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                    {
                        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                        if (serverFallback) request.Headers.TryAddWithoutValidation("anthropic-beta", FallbackBeta);
                        request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        using (HttpResponseMessage response = Wait(http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token), token))
                        {
                            status = (int)response.StatusCode;
                            IEnumerable<string> values;
                            if (response.Headers.TryGetValues("request-id", out values)) requestId = values.FirstOrDefault() ?? "";
                            retryAfter = ParseRetryAfter(response);
                            text = Wait(response.Content.ReadAsStringAsync(), token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) { Log("cancelled", 0, "", attempt, clock); throw; }
                    network = new TimeoutException("Request timed out.");
                }
                catch (HttpRequestException ex) { network = ex; }
                catch (WebException ex) { network = ex; }
                catch (System.IO.IOException ex) { network = ex; }

                if (network != null)
                {
                    Log("network error", 0, "", attempt, clock);
                    if (attempt >= MaxAttempts) throw new ClaudeApiException("Could not reach the Anthropic API after " + attempt + " attempts: " + Redact(Innermost(network).Message), 0, "network_error", Redact(Innermost(network).Message), "", attempt, null);
                    Pause(Backoff(attempt), token); continue;
                }
                Log("HTTP " + status, status, requestId, attempt, clock);
                if (status >= 200 && status < 300)
                {
                    Dictionary<string,object> data;
                    string rawContent;
                    try { data = AssistantJson.ParseObject(text); rawContent = AssistantJson.RawMember(text, "content"); }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException) throw;
                        throw new ClaudeApiException("The Anthropic API sent an unreadable response." + RequestSuffix(requestId), status, "parse_error", "", requestId, attempt, null);
                    }
                    return new ClaudeResponse { Status = status, Attempts = attempt, RequestId = requestId, RawBody = text, RawContent = rawContent ?? "[]", Data = data };
                }
                string errorType, apiMessage; ReadError(text, out errorType, out apiMessage);
                bool retryable = status == 429 || status >= 500;
                if (retryable && attempt < MaxAttempts)
                {
                    TimeSpan wait = status == 429 && retryAfter.HasValue ? retryAfter.Value : Backoff(attempt);
                    if (wait > RetryAfterCap) wait = RetryAfterCap;
                    Pause(wait, token); continue;
                }
                throw new ClaudeApiException(Friendly(status, errorType, apiMessage, attempt) + RequestSuffix(requestId), status, errorType, apiMessage, requestId, attempt, null);
            }
        }

        private static T Wait<T>(Task<T> task, CancellationToken token)
        {
            try { task.Wait(token); return task.Result; }
            catch (AggregateException ex)
            {
                Exception inner = ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
                if (inner is TaskCanceledException && token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (inner is OperationCanceledException) throw (OperationCanceledException)inner;
                if (inner is HttpRequestException) throw (HttpRequestException)inner;
                if (inner is System.IO.IOException) throw (System.IO.IOException)inner;
                if (inner is WebException) throw (WebException)inner;
                throw new HttpRequestException(inner.Message, inner);
            }
        }

        private static Exception Innermost(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex; }

        private TimeSpan Backoff(int attempt)
        {
            double jitter; lock (Jitter) jitter = Jitter.NextDouble();
            return TimeSpan.FromSeconds(BackoffBaseSeconds * Math.Pow(2, attempt - 1) * (0.75 + 0.5 * jitter));
        }

        private void Pause(TimeSpan wait, CancellationToken token)
        {
            if (RetryWaitObserved != null) RetryWaitObserved(wait);
            if (wait > TimeSpan.Zero && token.WaitHandle.WaitOne(wait)) token.ThrowIfCancellationRequested();
        }

        private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            IEnumerable<string> values;
            if (!response.Headers.TryGetValues("retry-after", out values)) return null;
            string value = (values.FirstOrDefault() ?? "").Trim();
            double seconds;
            if (Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0) return TimeSpan.FromSeconds(seconds);
            DateTimeOffset when;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when)) { TimeSpan d = when - DateTimeOffset.UtcNow; return d > TimeSpan.Zero ? d : TimeSpan.Zero; }
            return null;
        }

        private void ReadError(string text, out string type, out string message)
        {
            type = ""; message = "";
            try
            {
                var data = AssistantJson.ParseObject(text ?? "");
                var error = AssistantJson.Obj(data, "error");
                type = AssistantJson.Str(error, "type") ?? ""; message = AssistantJson.Str(error, "message") ?? "";
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            type = Redact(type); message = Redact(message);
            if (message.Length > 500) message = message.Substring(0, 500) + "…";
        }

        private static string Friendly(int status, string type, string message, int attempts)
        {
            string detail = message.Length > 0 ? ": " + message : ".";
            string kind = type.Length > 0 ? " " + type : "";
            switch (status)
            {
                case 400: return "The Anthropic API rejected the request (400" + kind + ")" + detail;
                case 401: return "API key rejected (401" + kind + "). Replace it in Settings.";
                case 403: return "This API key is not permitted to do this (403" + kind + ")" + detail;
                case 404: return "Model not available to this API key (404" + kind + ")" + detail + " Choose another model.";
                case 413: return "Conversation too large to send (413" + kind + "). Start a new chat.";
                case 429: return "Rate limited by the Anthropic API (429" + kind + ") after " + attempts + " attempts" + detail + " Try again shortly.";
            }
            if (status == 529) return "Anthropic API overloaded (529) after " + attempts + " attempts. Try again in a few minutes.";
            if (status >= 500) return "Anthropic API server error (" + status + kind + ") after " + attempts + " attempts" + detail;
            return "The Anthropic API returned HTTP " + status + kind + detail;
        }

        private static string RequestSuffix(string requestId) { return String.IsNullOrEmpty(requestId) ? "" : " (request-id " + requestId + ")"; }

        private void Log(string what, int status, string requestId, int attempt, Stopwatch clock)
        {
            Action<string> sink = Diagnostics; if (sink == null) return;
            try { sink(Redact(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " assistant " + what + " attempt=" + attempt + " ms=" + clock.ElapsedMilliseconds + (String.IsNullOrEmpty(requestId) ? "" : " request-id=" + requestId))); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
        }
    }
}
