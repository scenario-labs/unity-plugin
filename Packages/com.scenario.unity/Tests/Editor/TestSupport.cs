using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// A scripted Scenario REST API. Requests are matched to routes such as
    /// "GET /jobs/{id}"; a route's handler gets the query parameters, the JSON body's
    /// fields and the path's {id} as one object. Every reply completes synchronously,
    /// so tests can block on tasks without a main-thread pump.
    /// </summary>
    public sealed class FakeApi : ITransport
    {
        public static readonly string[] Patterns =
        {
            "GET /teams", "GET /models", "GET /models/{id}", "POST /generate/custom/{id}", "POST /generate/prompt",
            "GET /workflows", "GET /workflows/{id}", "PUT /workflows/{id}/run", "GET /jobs", "GET /jobs/{id}",
            "POST /jobs/{id}/action", "GET /assets", "GET /assets/{id}", "POST /assets/{id}/download", "POST /search/assets",
            "GET /recommendations/models", "POST /uploads", "GET /uploads/{id}", "POST /uploads/{id}/action",
        };

        public readonly List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();
        /// <summary>Matched routes, in call order, and their arguments.</summary>
        public readonly List<string> Calls = new List<string>();
        public readonly List<JObject> Arguments = new List<JObject>();
        public readonly List<double> Delays = new List<double>();
        /// <summary>route -> reply builder (arguments, call index) -> reply.</summary>
        public readonly Dictionary<string, Func<JObject, int, HttpReply>> Routes = new Dictionary<string, Func<JObject, int, HttpReply>>();
        /// <summary>Requests to any other host (the identity provider, uploads).</summary>
        public Func<HttpRequestSpec, HttpReply> Other;
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        public Task<HttpReply> SendAsync(HttpRequestSpec request, CancellationToken cancel = default)
        {
            Requests.Add(request);
            if (!request.Url.StartsWith(ApiClient.DefaultBaseUrl + "/", StringComparison.Ordinal))
                return Task.FromResult(Other != null ? Other(request) : Status(404, "{\"reason\":\"no such host in the test\"}"));
            var uri = new Uri(request.Url);
            var path = uri.AbsolutePath.Substring(new Uri(ApiClient.DefaultBaseUrl).AbsolutePath.Length);
            var args = request.Body != null && request.Body.Length > 0 ? Core.Json.ParseObject(Encoding.UTF8.GetString(request.Body)) : new JObject();
            foreach (var pair in uri.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(new[] { '=' }, 2);
                args[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            }
            var route = Match(request.Method, path, args);
            Calls.Add(route);
            Arguments.Add(args);
            _counts.TryGetValue(route, out var count);
            _counts[route] = count + 1;
            if (!Routes.TryGetValue(route, out var handler)) return Task.FromResult(Status(404, $"{{\"reason\":\"no route {route} in the test\"}}"));
            return Task.FromResult(handler(args, count));
        }

        /// <summary>The pattern a request matches, its {id} put in args["id"]; else "METHOD path".</summary>
        static string Match(string method, string path, JObject args)
        {
            var segments = path.Trim('/').Split('/');
            foreach (var pattern in Patterns)
            {
                var space = pattern.IndexOf(' ');
                if (pattern.Substring(0, space) != method) continue;
                var expected = pattern.Substring(space + 1).Trim('/').Split('/');
                if (expected.Length != segments.Length) continue;
                var id = "";
                var matched = true;
                for (var i = 0; i < expected.Length && matched; i++)
                {
                    if (expected[i] == "{id}") id = Uri.UnescapeDataString(segments[i]);
                    else matched = expected[i] == segments[i];
                }
                if (!matched) continue;
                if (id.Length > 0) args["id"] = id;
                return pattern;
            }
            return method + " " + path;
        }

        /// <summary>The bytes a download returns, by URL (default: the text "fake").</summary>
        public Func<string, byte[]> DownloadContent;

        public Task<HttpReply> DownloadAsync(string url, string destPath, double timeoutSeconds, IProgress<float> progress = null, CancellationToken cancel = default)
        {
            Requests.Add(new HttpRequestSpec { Method = "GET", Url = url });
            File.WriteAllBytes(destPath, DownloadContent?.Invoke(url) ?? Encoding.ASCII.GetBytes("fake"));
            return Task.FromResult(new HttpReply { Status = 200 });
        }

        public Task Delay(double seconds, CancellationToken cancel = default)
        {
            Delays.Add(seconds);
            return Task.CompletedTask;
        }

        public int Count(string route) => Calls.Count(c => c == route);

        /// <summary>The arguments of the last call to this route.</summary>
        public JObject Last(string route)
        {
            for (var i = Calls.Count - 1; i >= 0; i--)
                if (Calls[i] == route) return Arguments[i];
            return null;
        }

        // --- Reply builders --------------------------------------------------

        /// <summary>A JSON reply (200 by default; a price check answers 269).</summary>
        public static HttpReply Json(JObject body, int status = 200) => new HttpReply { Status = status, Body = body.ToString(Formatting.None) };

        /// <summary>The API's error body: { reason, name? }.</summary>
        public static HttpReply Error(int status, string reason, string name = null)
        {
            var body = new JObject { ["reason"] = reason };
            if (name != null) body["name"] = name;
            return new HttpReply { Status = status, Body = body.ToString(Formatting.None) };
        }

        public static HttpReply Status(int status, string body = "", string retryAfter = null)
        {
            var reply = new HttpReply { Status = status, Body = body };
            if (retryAfter != null) reply.Headers["retry-after"] = retryAfter;
            return reply;
        }

        public static HttpReply Fail(string error) => new HttpReply { Error = error, ErrorDetail = error };

        /// <summary>A recorded reply body from Fixtures/.</summary>
        public static HttpReply Fixture(string name, int status = 200) => new HttpReply { Status = status, Body = Fixtures.Read(name) };
    }

    public static class Fixtures
    {
        public const string Folder = "Packages/com.scenario.unity/Tests/Editor/Fixtures";

        public static string Read(string name) => File.ReadAllText(Path.Combine(Folder, name));

        /// <summary>A recorded reply body, parsed. Json.Parse keeps ISO dates as strings, like the client does.</summary>
        public static JObject Body(string name) => Json.ParseObject(Read(name));
    }

    public static class Run
    {
        /// <summary>Blocks on a task that the fake transport completes synchronously.</summary>
        public static T Sync<T>(Task<T> task)
        {
            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(20)), "task did not complete");
            return task.Result;
        }

        public static void Sync(Task task) => Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(20)), "task did not complete");
    }

    /// <summary>Answers like the real network: later, on a worker thread.</summary>
    public sealed class WorkerThreadTransport : ITransport
    {
        readonly FakeApi _inner;
        public WorkerThreadTransport(FakeApi inner) { _inner = inner; }

        public async Task<HttpReply> SendAsync(HttpRequestSpec request, CancellationToken cancel = default)
        {
            await Task.Delay(50).ConfigureAwait(false);
            return await _inner.SendAsync(request, cancel).ConfigureAwait(false);
        }

        public Task<HttpReply> DownloadAsync(string url, string destPath, double timeoutSeconds, IProgress<float> progress = null, CancellationToken cancel = default)
            => _inner.DownloadAsync(url, destPath, timeoutSeconds, progress, cancel);

        public Task Delay(double seconds, CancellationToken cancel = default) => _inner.Delay(seconds, cancel);
    }

    public static class TempDir
    {
        public static string Make(string label)
        {
            var path = Path.Combine(Path.GetTempPath(), "scenario-unity-tests", label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
