using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    internal sealed class MockRequest
    {
        internal string Method, Path, Body;
        internal Dictionary<string,string> Headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal DateTime Received;
        internal Dictionary<string,object> Json { get { return AssistantJson.ParseObject(Body); } }
        internal List<object> Messages { get { return AssistantJson.List(Json, "messages"); } }
        internal Dictionary<string,object> LastMessage { get { return (Dictionary<string,object>)Messages.Last(); } }
        internal List<Dictionary<string,object>> LastContent { get { return AssistantJson.List(LastMessage, "content").OfType<Dictionary<string,object>>().ToList(); } }
    }

    internal sealed class MockReply
    {
        internal int Status = 200;
        internal string Body = "";
        internal Dictionary<string,string> Headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal int DelayMs;
    }

    // Minimal loopback HTTP/1.1 server for the Messages API checks. One request per
    // connection; no URL ACL is needed because it listens on a raw socket.
    internal sealed class MockAnthropicServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private volatile bool stopping;
        private int counter;
        internal readonly List<MockRequest> Requests = new List<MockRequest>();
        internal Func<MockRequest, int, MockReply> Handler;
        internal readonly string BaseUrl;

        internal MockAnthropicServer(Func<MockRequest, int, MockReply> handler)
        {
            Handler = handler;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(Loop) { IsBackground = true, Name = "mock-anthropic" };
            thread.Start();
        }

        internal List<MockRequest> Snapshot() { lock (Requests) return Requests.ToList(); }

        private void Loop()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 15000;
                    var headerBytes = new List<byte>();
                    while (true)
                    {
                        int b = stream.ReadByte(); if (b < 0) return;
                        headerBytes.Add((byte)b);
                        int n = headerBytes.Count;
                        if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10 && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
                        if (n > 65536) return;
                    }
                    string[] lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] first = lines[0].Split(' ');
                    var request = new MockRequest { Method = first[0], Path = first.Length > 1 ? first[1] : "", Received = DateTime.UtcNow };
                    foreach (string line in lines.Skip(1))
                    {
                        int colon = line.IndexOf(':'); if (colon <= 0) continue;
                        request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                    }
                    string expect;
                    if (request.Headers.TryGetValue("Expect", out expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    { byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); stream.Write(cont, 0, cont.Length); }
                    string lengthText; int length = 0;
                    if (request.Headers.TryGetValue("Content-Length", out lengthText)) length = Int32.Parse(lengthText, CultureInfo.InvariantCulture);
                    byte[] body = new byte[length]; int read = 0;
                    while (read < length) { int r = stream.Read(body, read, length - read); if (r <= 0) return; read += r; }
                    request.Body = new UTF8Encoding(false).GetString(body);
                    int index;
                    lock (Requests) { Requests.Add(request); index = counter++; }
                    MockReply reply;
                    try { reply = Handler(request, index); }
                    catch (Exception ex) { reply = new MockReply { Status = 400, Body = "{\"type\":\"error\",\"error\":{\"type\":\"mock_handler_error\",\"message\":" + AssistantJson.Write(ex.Message) + "}}" }; }
                    if (reply.DelayMs > 0) Thread.Sleep(reply.DelayMs);
                    byte[] payload = new UTF8Encoding(false).GetBytes(reply.Body ?? "");
                    var head = new StringBuilder();
                    head.Append("HTTP/1.1 ").Append(reply.Status).Append(' ').Append(Reason(reply.Status)).Append("\r\n");
                    head.Append("Content-Type: application/json\r\nContent-Length: ").Append(payload.Length).Append("\r\nConnection: close\r\n");
                    head.Append("request-id: req_mock_").Append(index.ToString("000", CultureInfo.InvariantCulture)).Append("\r\n");
                    foreach (var h in reply.Headers) head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                    head.Append("\r\n");
                    byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                    stream.Write(headBytes, 0, headBytes.Length); stream.Write(payload, 0, payload.Length); stream.Flush();
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static string Reason(int status)
        {
            switch (status) { case 200: return "OK"; case 400: return "Bad Request"; case 401: return "Unauthorized"; case 404: return "Not Found"; case 429: return "Too Many Requests"; case 500: return "Internal Server Error"; case 529: return "Overloaded"; default: return "Status"; }
        }

        public void Dispose()
        {
            stopping = true;
            try { listener.Stop(); } catch (SocketException) { }
            thread.Join(2000);
        }
    }
}
