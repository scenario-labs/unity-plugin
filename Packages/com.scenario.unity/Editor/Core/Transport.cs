using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Scenario.Editor.Core
{
    public sealed class HttpReply
    {
        public int Status;
        /// <summary>Lowercase header names.</summary>
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public string Body = "";
        public byte[] Bytes;
        /// <summary>"" when the server answered, else "timeout" or "transport".</summary>
        public string Error = "";
        public string ErrorDetail = "";

        public bool Answered => string.IsNullOrEmpty(Error);
        public bool IsSuccess => Answered && Status >= 200 && Status < 300;
        public string Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : "";
    }

    public sealed class HttpRequestSpec
    {
        public string Method = "POST";
        public string Url;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public byte[] Body;
        public string ContentType;
        public double TimeoutSeconds = 60;
        public bool WantBytes;
    }

    /// <summary>
    /// Everything the core sends over the network goes through this interface,
    /// so tests run the full client against recorded replies with no network.
    /// </summary>
    public interface ITransport
    {
        Task<HttpReply> SendAsync(HttpRequestSpec request, CancellationToken cancel = default);
        /// <summary>Streams a GET into destPath (written as destPath.part, then renamed).</summary>
        Task<HttpReply> DownloadAsync(string url, string destPath, double timeoutSeconds, IProgress<float> progress = null, CancellationToken cancel = default);
        Task Delay(double seconds, CancellationToken cancel = default);
    }

    /// <summary>The real transport: one shared HttpClient, no cookies, no proxies beyond the system's.</summary>
    public sealed class HttpTransport : ITransport
    {
        static readonly HttpClient Client = CreateClient();

        static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public async Task<HttpReply> SendAsync(HttpRequestSpec request, CancellationToken cancel = default)
        {
            var reply = new HttpReply();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds))))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel))
            using (var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
            {
                if (request.Body != null)
                {
                    message.Content = new ByteArrayContent(request.Body);
                    if (!string.IsNullOrEmpty(request.ContentType))
                        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
                }
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (message.Content != null) message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
                        continue;
                    }
                    if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                try
                {
                    using (var response = await Client.SendAsync(message, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false))
                    {
                        reply.Status = (int)response.StatusCode;
                        CopyHeaders(response, reply.Headers);
                        if (request.WantBytes) reply.Bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        else reply.Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    reply.Error = "timeout";
                }
                catch (OperationCanceledException)
                {
                    reply.Error = "canceled";
                }
                catch (Exception e)
                {
                    reply.Error = "transport";
                    reply.ErrorDetail = e.GetBaseException().Message;
                }
            }
            return reply;
        }

        public async Task<HttpReply> DownloadAsync(string url, string destPath, double timeoutSeconds, IProgress<float> progress = null, CancellationToken cancel = default)
        {
            var reply = new HttpReply();
            var part = destPath + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? ".");
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds))))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel))
                using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false))
                {
                    reply.Status = (int)response.StatusCode;
                    CopyHeaders(response, reply.Headers);
                    if (!response.IsSuccessStatusCode) return reply;
                    var total = response.Content.Headers.ContentLength ?? -1;
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(part))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, linked.Token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, linked.Token).ConfigureAwait(false);
                            done += read;
                            if (total > 0) progress?.Report((float)done / total);
                        }
                    }
                }
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(part, destPath);
            }
            catch (OperationCanceledException)
            {
                reply.Error = cancel.IsCancellationRequested ? "canceled" : "timeout";
            }
            catch (Exception e)
            {
                reply.Error = "transport";
                reply.ErrorDetail = e.GetBaseException().Message;
            }
            finally
            {
                if (File.Exists(part)) TryDelete(part);
            }
            return reply;
        }

        public Task Delay(double seconds, CancellationToken cancel = default) => Task.Delay(TimeSpan.FromSeconds(seconds), cancel);

        static void CopyHeaders(HttpResponseMessage response, Dictionary<string, string> into)
        {
            foreach (var header in response.Headers) into[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
            foreach (var header in response.Content.Headers) into[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    public static class HttpRequests
    {
        public static HttpRequestSpec JsonPost(string url, string json, double timeout) => new HttpRequestSpec
        {
            Method = "POST", Url = url, Body = Encoding.UTF8.GetBytes(json), ContentType = "application/json", TimeoutSeconds = timeout,
        };

        public static HttpRequestSpec FormPost(string url, IDictionary<string, string> fields, double timeout)
        {
            var parts = new List<string>();
            foreach (var field in fields) parts.Add(Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value ?? ""));
            return new HttpRequestSpec
            {
                Method = "POST", Url = url, Body = Encoding.UTF8.GetBytes(string.Join("&", parts)),
                ContentType = "application/x-www-form-urlencoded", TimeoutSeconds = timeout,
            };
        }
    }
}
