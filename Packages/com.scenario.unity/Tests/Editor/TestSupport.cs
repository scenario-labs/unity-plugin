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
    /// A scripted Scenario MCP server. Every reply completes synchronously, so
    /// tests can block on tasks without a main-thread pump.
    /// </summary>
    public sealed class FakeMcp : ITransport
    {
        public readonly List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();
        public readonly List<string> ToolCalls = new List<string>();
        public readonly List<JObject> ToolArguments = new List<JObject>();
        public readonly List<double> Delays = new List<double>();
        /// <summary>tool name -> reply builder (arguments, call index) -> reply.</summary>
        public readonly Dictionary<string, Func<JObject, int, HttpReply>> Tools = new Dictionary<string, Func<JObject, int, HttpReply>>();
        public Func<HttpRequestSpec, HttpReply> Other;
        public int Initializations;
        public string SessionId = "sess_1";
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        public Task<HttpReply> SendAsync(HttpRequestSpec request, CancellationToken cancel = default)
        {
            Requests.Add(request);
            if (Other != null && !request.Url.StartsWith("https://mcp.scenario.com/mcp", StringComparison.Ordinal))
                return Task.FromResult(Other(request));
            var payload = Json.ParseObject(Encoding.UTF8.GetString(request.Body));
            var method = (string)payload["method"];
            var id = payload["id"];
            if (method == "initialize")
            {
                Initializations++;
                var reply = Sse(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JObject { ["protocolVersion"] = McpClient.ProtocolVersion } });
                reply.Headers["mcp-session-id"] = SessionId;
                return Task.FromResult(reply);
            }
            if (method == "notifications/initialized") return Task.FromResult(new HttpReply { Status = 202 });
            var name = (string)payload["params"]["name"];
            var args = (JObject)payload["params"]["arguments"];
            ToolCalls.Add(name);
            ToolArguments.Add(args);
            _counts.TryGetValue(name, out var count);
            _counts[name] = count + 1;
            if (!Tools.TryGetValue(name, out var handler)) return Task.FromResult(ToolError(id, $"unknown tool {name}"));
            var built = handler(args, count);
            // Builders return a result body without the id: put the request's id in.
            if (built.Status == 200 && built.Body.Contains("\"__ID__\""))
                built.Body = built.Body.Replace("\"__ID__\"", id.ToString(Formatting.None));
            return Task.FromResult(built);
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

        public int Count(string tool) => ToolCalls.Count(t => t == tool);

        // --- Reply builders --------------------------------------------------

        public static HttpReply Sse(JObject envelope)
        {
            var reply = new HttpReply { Status = 200, Body = "event: message\ndata: " + envelope.ToString(Formatting.None) + "\n\n" };
            reply.Headers["content-type"] = "text/event-stream";
            return reply;
        }

        /// <summary>A successful tool result carrying structuredContent.</summary>
        public static HttpReply Result(JObject structured)
        {
            var envelope = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "__ID__",
                ["result"] = new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = structured.ToString(Formatting.None) }),
                    ["structuredContent"] = structured,
                },
            };
            return Sse(envelope);
        }

        /// <summary>A tool error (isError: true) with this text.</summary>
        public static HttpReply ToolError(JToken id, string text)
        {
            return Sse(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? "__ID__",
                ["result"] = new JObject
                {
                    ["isError"] = true,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
                },
            });
        }

        public static HttpReply Error(string text) => ToolError(null, text);

        public static HttpReply Status(int status, string body = "", string retryAfter = null)
        {
            var reply = new HttpReply { Status = status, Body = body };
            if (retryAfter != null) reply.Headers["retry-after"] = retryAfter;
            return reply;
        }

        public static HttpReply Fail(string error) => new HttpReply { Error = error, ErrorDetail = error };

        /// <summary>A recorded fixture: its JSON-RPC id is replaced by the request's.</summary>
        public static HttpReply Fixture(string name)
        {
            var text = Fixtures.Read(name);
            var data = text.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal)).Substring(5).Trim();
            var envelope = Json.ParseObject(data);
            envelope["id"] = "__ID__";
            return Sse(envelope);
        }
    }

    public static class Fixtures
    {
        public const string Folder = "Packages/com.scenario.unity/Tests/Editor/Fixtures";

        public static string Read(string name) => File.ReadAllText(Path.Combine(Folder, name));

        /// <summary>The structuredContent of a recorded tool reply.</summary>
        public static JObject Structured(string name)
        {
            var data = Read(name).Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal)).Substring(5).Trim();
            // Json.Parse keeps ISO dates as strings, like the client does.
            return (JObject)Json.ParseObject(data)["result"]["structuredContent"];
        }
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
