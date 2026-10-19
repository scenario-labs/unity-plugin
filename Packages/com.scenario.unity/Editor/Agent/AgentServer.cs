using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor.Agent
{
    public sealed class AgentActivity
    {
        public double Time;
        public string Client = "";
        public string Tool = "";
        public string Summary = "";
        public bool IsError;
    }

    /// <summary>A paid call above the agent's limits, waiting for the user in the Agent view.</summary>
    public sealed class Approval
    {
        public string Id = "";
        public string Client = "";
        public string Tool = "";
        public string Summary = "";
        public string Reason = "";
        public double Cu;
        internal readonly TaskCompletionSource<bool> Decision = new TaskCompletionSource<bool>();

        public void Approve() => AgentServer.Resolve(this, true);
        public void Deny() => AgentServer.Resolve(this, false);
    }

    /// <summary>
    /// The agent bridge: a Streamable HTTP MCP server inside the editor, on
    /// 127.0.0.1 only, with a bearer token. Requests arrive on worker threads and
    /// run on the main thread. It stops before each domain reload and starts again
    /// after it; jobs live in the ledger, so a recompile loses nothing.
    /// </summary>
    [InitializeOnLoad]
    public static class AgentServer
    {
        public const string ProtocolVersion = "2025-03-26";
        static readonly string[] KnownVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
        const int MaxBodyBytes = 4 * 1024 * 1024;
        const int MaxActivity = 200;
        const string SpentKey = "Scenario.AgentSpent";
        const string ClientKey = "Scenario.AgentLastClient";

        static HttpListener _listener;
        static CancellationTokenSource _stop;
        static int _busy;
        static readonly Dictionary<string, string> Sessions = new Dictionary<string, string>();
        static readonly object SessionsLock = new object();

        public static readonly List<AgentActivity> Activity = new List<AgentActivity>();
        public static readonly List<Approval> Pending = new List<Approval>();
        public static event Action StateChanged;

        public static bool Running => _listener != null && _listener.IsListening;
        public static bool Busy => _busy > 0;
        public static string Error { get; private set; } = "";
        public static int Port => ScenarioUserSettings.instance.AgentPort;
        public static string Url => $"http://127.0.0.1:{Port}/mcp";

        public static string LastClient
        {
            get => SessionState.GetString(ClientKey, "");
            private set => SessionState.SetString(ClientKey, value ?? "");
        }

        /// <summary>Creative Units approved for agents since Unity started.</summary>
        public static double SessionSpent
        {
            get => SessionState.GetFloat(SpentKey, 0);
            private set => SessionState.SetFloat(SpentKey, (float)value);
        }

        /// <summary>SCENARIO_UNITY_AGENT_TOKEN overrides the stored token (CI and tests).</summary>
        public static string Token
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("SCENARIO_UNITY_AGENT_TOKEN");
                return string.IsNullOrEmpty(env) ? ScenarioUserSettings.instance.EnsureAgentToken() : env;
            }
        }

        static AgentServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            var forced = Environment.GetEnvironmentVariable("SCENARIO_UNITY_AGENT") == "1";
            if (Application.isBatchMode && !forced) return;
            if (forced || ScenarioUserSettings.instance.AgentEnabled) EditorApplication.delayCall += Start;
        }

        static void Changed() => MainThread.Post(() => StateChanged?.Invoke());

        public static void SetEnabled(bool enabled)
        {
            var user = ScenarioUserSettings.instance;
            user.AgentEnabled = enabled;
            user.Save();
            if (enabled) Start();
            else Stop();
        }

        public static void RegenerateToken()
        {
            ScenarioUserSettings.instance.EnsureAgentToken(regenerate: true);
            _tokenCache = Token;
            Changed();
        }

        public static void Start()
        {
            if (Running) return;
            Error = "";
            try
            {
                _tokenCache = Token;
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                listener.Start();
                _listener = listener;
                _stop = new CancellationTokenSource();
                var stop = _stop.Token;
                Task.Run(() => Loop(listener, stop));
                Application.logMessageReceivedThreaded -= AgentLogs.OnLog;
                Application.logMessageReceivedThreaded += AgentLogs.OnLog;
                _ = ScenarioService.Instance;
            }
            catch (Exception e) when (e is HttpListenerException || e is System.Net.Sockets.SocketException || e is InvalidOperationException)
            {
                _listener = null;
                Error = $"Port {Port} is not available ({e.Message}). Pick another port below.";
            }
            Changed();
        }

        public static void Stop()
        {
            try
            {
                _stop?.Cancel();
                _listener?.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }
            _listener = null;
            _stop = null;
            Application.logMessageReceivedThreaded -= AgentLogs.OnLog;
            foreach (var approval in Pending.ToList()) approval.Decision.TrySetResult(false);
            Pending.Clear();
            Changed();
        }

        static async Task Loop(HttpListener listener, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break;
                }
                _ = Task.Run(() => Serve(context));
            }
        }

        // --- HTTP ------------------------------------------------------------------------

        static async Task Serve(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                var host = request.Url.Host;
                if (host != "127.0.0.1" && host != "localhost")
                {
                    await Reply(response, 403, Message("Host not allowed."));
                    return;
                }
                var origin = request.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
                {
                    await Reply(response, 403, Message("Origin not allowed."));
                    return;
                }
                // The token comes before the path, so no caller reaches routing without it.
                if (!Authorized(request.Headers["Authorization"]))
                {
                    response.AddHeader("WWW-Authenticate", "Bearer");
                    await Reply(response, 401, Message("Missing or wrong bearer token. Copy the setup from Scenario > Agent in Unity."));
                    return;
                }
                if (request.Url.AbsolutePath.TrimEnd('/') != "/mcp")
                {
                    await Reply(response, 404, Message("Not found. The endpoint is /mcp."));
                    return;
                }
                if (request.HttpMethod == "DELETE")
                {
                    await Reply(response, 200, null);
                    return;
                }
                if (request.HttpMethod != "POST")
                {
                    response.AddHeader("Allow", "POST, DELETE");
                    await Reply(response, 405, Message("Use POST."));
                    return;
                }
                if (request.ContentLength64 > MaxBodyBytes)
                {
                    await Reply(response, 413, Message("Request too large."));
                    return;
                }
                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                if (body.Length > MaxBodyBytes)
                {
                    await Reply(response, 413, Message("Request too large."));
                    return;
                }
                JToken parsed;
                try
                {
                    parsed = JToken.Parse(body);
                }
                catch (JsonException)
                {
                    await Reply(response, 400, RpcError(null, -32700, "Parse error"));
                    return;
                }
                var session = request.Headers["Mcp-Session-Id"] ?? "";
                var messages = parsed is JArray batch ? batch.OfType<JObject>().ToList() : parsed is JObject single ? new List<JObject> { single } : new List<JObject>();
                var replies = new JArray();
                foreach (var message in messages)
                {
                    var reply = await MainThread.RunAsync(() => Dispatch(message, session));
                    if (reply.response != null) replies.Add(reply.response);
                    if (reply.session.Length > 0) response.AddHeader("Mcp-Session-Id", reply.session);
                }
                if (replies.Count == 0)
                {
                    await Reply(response, 202, null);
                    return;
                }
                await Reply(response, 200, parsed is JArray ? (JToken)replies : replies[0]);
            }
            catch (Exception e)
            {
                try
                {
                    await Reply(response, 500, Message("Bridge error: " + e.Message));
                }
                catch (Exception)
                {
                    // The client went away.
                }
            }
        }

        static bool IsLocalOrigin(string origin)
        {
            if (origin == "null") return false;
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.Host == "127.0.0.1" || uri.Host == "localhost");
        }

        static bool Authorized(string header)
        {
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
            var given = Encoding.UTF8.GetBytes(header.Substring(7).Trim());
            var expected = Encoding.UTF8.GetBytes(TokenCache);
            if (given.Length != expected.Length) return false;
            var diff = 0;
            for (var i = 0; i < given.Length; i++) diff |= given[i] ^ expected[i];
            return diff == 0;
        }

        // The token is read on the main thread (EditorPrefs) and cached for worker threads.
        static string _tokenCache = "";
        static string TokenCache
        {
            get
            {
                if (_tokenCache.Length == 0) _tokenCache = MainThread.Run(() => Token).Result;
                return _tokenCache;
            }
        }

        internal static void ResetTokenCache() => _tokenCache = "";

        static JObject Message(string text) => new JObject { ["error"] = text };

        static async Task Reply(HttpListenerResponse response, int status, JToken body)
        {
            response.StatusCode = status;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            response.Close();
        }

        static JObject RpcError(JToken id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        };

        static JObject RpcResult(JToken id, JToken result) => new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

        // --- JSON-RPC (main thread) ------------------------------------------------------------

        /// <summary>Handles one JSON-RPC message. Public for tests.</summary>
        public static async Task<(JObject response, string session)> Dispatch(JObject message, string session)
        {
            var id = message["id"];
            var method = Json.Str(message, "method");
            var parameters = message["params"] as JObject ?? new JObject();
            if (id == null || id.Type == JTokenType.Null) return (null, ""); // notifications need no reply
            switch (method)
            {
                case "initialize":
                {
                    var info = parameters["clientInfo"] as JObject;
                    var client = Json.Str(info, "name", "MCP client");
                    var requested = Json.Str(parameters, "protocolVersion");
                    var newSession = "sus_" + OAuthClient.RandomToken(12);
                    lock (SessionsLock) Sessions[newSession] = client;
                    LastClient = client;
                    Changed();
                    return (RpcResult(id, new JObject
                    {
                        ["protocolVersion"] = KnownVersions.Contains(requested) ? requested : ProtocolVersion,
                        ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JObject { ["name"] = "scenario-unity", ["title"] = "Scenario for Unity", ["version"] = McpClient.Version },
                        ["instructions"] = AgentTools.Instructions,
                    }), newSession);
                }
                case "ping":
                    return (RpcResult(id, new JObject()), "");
                case "tools/list":
                    return (RpcResult(id, new JObject { ["tools"] = AgentTools.List() }), "");
                case "tools/call":
                {
                    var name = Json.Str(parameters, "name");
                    var arguments = parameters["arguments"] as JObject ?? new JObject();
                    var client = ClientFor(session);
                    Interlocked.Increment(ref _busy);
                    Changed();
                    JObject result;
                    try
                    {
                        result = await AgentTools.Call(name, arguments, new AgentTools.Context { Client = client });
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _busy);
                    }
                    var isError = Json.Truthy(result["isError"]);
                    Log(client, name, isError ? FirstText(result) : Summarize(arguments), isError);
                    return (RpcResult(id, result), "");
                }
                default:
                    return (RpcError(id, -32601, $"Method not found: {method}"), "");
            }
        }

        static string ClientFor(string session)
        {
            lock (SessionsLock)
                if (session.Length > 0 && Sessions.TryGetValue(session, out var name)) return name;
            return LastClient.Length > 0 ? LastClient : "Agent";
        }

        static string FirstText(JObject result) => (result["content"] as JArray)?.OfType<JObject>().Select(c => Json.Str(c, "text")).FirstOrDefault(t => t.Length > 0) ?? "";

        static string Summarize(JObject arguments)
        {
            var text = arguments.ToString(Formatting.None);
            return text.Length > 140 ? text.Substring(0, 140) + "…" : text;
        }

        static void Log(string client, string tool, string summary, bool isError)
        {
            Activity.Add(new AgentActivity { Time = Json.Now(), Client = client, Tool = tool, Summary = summary ?? "", IsError = isError });
            if (Activity.Count > MaxActivity) Activity.RemoveRange(0, Activity.Count - MaxActivity);
            Changed();
        }

        // --- Spending approvals -------------------------------------------------------------

        /// <summary>
        /// True when an agent may spend this much now: within the per-call limit and the
        /// session budget, or approved by the user in the Agent view (2 minute timeout).
        /// </summary>
        public static async Task<bool> RequestSpend(string client, string tool, string summary, double cu)
        {
            var user = ScenarioUserSettings.instance;
            var overCall = cu > user.AgentAutoApproveCu;
            var overBudget = SessionSpent + cu > user.AgentSessionBudgetCu;
            if (!overCall && !overBudget) return true;
            var approval = new Approval
            {
                Id = OAuthClient.RandomToken(6),
                Client = client,
                Tool = tool,
                Summary = summary,
                Cu = cu,
                Reason = overCall ? $"above the {user.AgentAutoApproveCu} CU auto-approve limit" : $"past the {user.AgentSessionBudgetCu} CU session budget",
            };
            Pending.Add(approval);
            Changed();
            if (Application.isBatchMode) Debug.Log($"Scenario: {client} asks to spend {Math.Round(cu, 1)} CU ({summary}). Waiting for approval.");
            else if (!EditorWindow.HasOpenInstances<UI.ScenarioWindow>()) UI.ScenarioWindow.OpenView("agent");
            var finished = await Task.WhenAny(approval.Decision.Task, Task.Delay(TimeSpan.FromMinutes(2)));
            Pending.Remove(approval);
            Changed();
            return finished == approval.Decision.Task && approval.Decision.Task.Result;
        }

        internal static void Resolve(Approval approval, bool approved) => approval.Decision.TrySetResult(approved);

        public static void AddSpent(double cu)
        {
            SessionSpent += cu;
            Changed();
        }
    }

    /// <summary>Recent console messages for the console_logs tool.</summary>
    public static class AgentLogs
    {
        public sealed class Entry
        {
            public double Time;
            public string Type;
            public string Message;
            public string Where;
        }

        static readonly List<Entry> Entries = new List<Entry>();
        static readonly object Lock = new object();

        internal static void OnLog(string message, string stack, LogType type)
        {
            var where = (stack ?? "").Split('\n').FirstOrDefault(l => l.Contains("(at ")) ?? "";
            lock (Lock)
            {
                Entries.Add(new Entry { Time = Json.Now(), Type = type.ToString(), Message = message ?? "", Where = where.Trim() });
                if (Entries.Count > 400) Entries.RemoveRange(0, Entries.Count - 400);
            }
        }

        public static List<Entry> Recent(int limit, string level)
        {
            lock (Lock)
            {
                IEnumerable<Entry> list = Entries;
                if (level == "error") list = list.Where(e => e.Type == "Error" || e.Type == "Exception" || e.Type == "Assert");
                else if (level == "warning") list = list.Where(e => e.Type != "Log");
                return list.Reverse().Take(Math.Max(1, limit)).Reverse().ToList();
            }
        }
    }
}
