using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Agent;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// The agent bridge: JSON-RPC dispatch, the money rules on agent calls, scene edits
    /// with Undo, and the HTTP checks (host, origin, token, methods) on a free local port.
    /// </summary>
    public class AgentTests
    {
        const string TestToken = "sut_test_token_0123456789";
        FakeMcp _server;
        ScenarioService _s;
        string _settings;
        string _envToken;
        bool _wasRunning;

        [SetUp]
        public void SetUp()
        {
            _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);
            _server = new FakeMcp();
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Fixture("teams.sse.txt");
            _server.Tools["models_list"] = (args, i) => FakeMcp.Result(new JObject { ["models"] = new JArray() });
            _s = ScenarioService.UseForTests(_server, TempDir.Make("agent"));
            _wasRunning = AgentServer.Running;
            _envToken = Environment.GetEnvironmentVariable("SCENARIO_UNITY_AGENT_TOKEN");
        }

        [TearDown]
        public void TearDown()
        {
            AgentServer.Stop();
            Environment.SetEnvironmentVariable("SCENARIO_UNITY_AGENT_TOKEN", _envToken);
            AgentServer.ResetTokenCache();
            ScenarioService.ResetForTests();
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
            if (_wasRunning) AgentServer.Start();
        }

        static JObject Rpc(string method, JObject parameters = null, int id = 1)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            return message;
        }

        static (JObject response, string session) Send(JObject message, string session = "")
            => Run.Sync(AgentServer.Dispatch(message, session));

        static JObject CallTool(string name, JObject arguments)
            => (JObject)Send(Rpc("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() })).response["result"];

        static string TextOf(JObject result) => (string)result["content"].Last()["text"];

        // --- JSON-RPC ------------------------------------------------------------------

        [Test]
        public void Initialize_NegotiatesTheVersion_AndOpensASession()
        {
            var (response, session) = Send(Rpc("initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["clientInfo"] = new JObject { ["name"] = "Test client" },
            }));
            var result = response["result"];
            Assert.AreEqual("2025-06-18", (string)result["protocolVersion"]);
            Assert.AreEqual("scenario-unity", (string)result["serverInfo"]["name"]);
            Assert.IsNotNull(result["capabilities"]["tools"]);
            StringAssert.StartsWith("sus_", session);
            Assert.AreEqual("Test client", AgentServer.LastClient);

            var old = Send(Rpc("initialize", new JObject { ["protocolVersion"] = "1999-01-01" })).response;
            Assert.AreEqual(AgentServer.ProtocolVersion, (string)old["result"]["protocolVersion"]);
        }

        [Test]
        public void Ping_UnknownMethods_AndNotifications()
        {
            Assert.IsNotNull(Send(Rpc("ping")).response["result"]);
            var unknown = Send(Rpc("resources/list")).response;
            Assert.AreEqual(-32601, (int)unknown["error"]["code"]);
            var notification = new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
            Assert.IsNull(Send(notification).response, "notifications get no reply");
        }

        [Test]
        public void ToolsList_DescribesEveryTool()
        {
            var tools = (JArray)Send(Rpc("tools/list")).response["result"]["tools"];
            var names = tools.Select(t => (string)t["name"]).ToList();
            foreach (var expected in new[] { "scenario_status", "models_find", "model_schema", "estimate", "generate", "jobs", "scene_info", "object_create", "capture_view" })
                CollectionAssert.Contains(names, expected);
            Assert.AreEqual(names.Count, names.Distinct().Count(), "tool names are unique");
            foreach (var tool in tools)
            {
                Assert.AreEqual("object", (string)tool["inputSchema"]["type"], (string)tool["name"]);
                Assert.IsFalse(string.IsNullOrEmpty((string)tool["description"]), (string)tool["name"]);
            }
            var sceneInfo = tools.First(t => (string)t["name"] == "scene_info");
            Assert.AreEqual(true, (bool)sceneInfo["annotations"]["readOnlyHint"]);
        }

        [Test]
        public void MotionsFind_SearchesTheLibrary_WithoutSigningIn()
        {
            MotionLibrary.Reset();
            var result = CallTool("motions_find", new JObject { ["query"] = "casual walk", ["limit"] = 5 });
            Assert.IsNull(result["isError"], TextOf(result));
            var data = JObject.Parse(TextOf(result));
            Assert.IsTrue(data["motions"].Any(m => (int)m["id"] == 30 && (string)m["name"] == "Casual Walk"), TextOf(result));
            Assert.LessOrEqual(((JArray)data["motions"]).Count, 5);
            var dances = JObject.Parse(TextOf(CallTool("motions_find", new JObject { ["category"] = "Dancing" })));
            Assert.IsTrue(dances["motions"].All(m => (string)m["category"] == "Dancing"));
            Assert.AreEqual(33, (int)dances["total"]);
        }

        [Test]
        public void UnknownTool_IsAToolError()
        {
            var result = CallTool("does_not_exist", null);
            Assert.AreEqual(true, (bool)result["isError"]);
            StringAssert.Contains("Unknown tool", TextOf(result));
        }

        // --- Money rules on agent calls ------------------------------------------------------

        [Test]
        public void Generate_WhenSignedOut_IsRefused_AndSendsNothing()
        {
            var result = CallTool("generate", new JObject { ["model_id"] = "model_a", ["parameters"] = new JObject { ["prompt"] = "x" } });
            Assert.AreEqual(true, (bool)result["isError"]);
            StringAssert.Contains("Not signed in", TextOf(result));
            Assert.AreEqual(0, _server.Requests.Count);
        }

        [Test]
        public void Generate_AboveMaxCu_IsPricedButNotSent()
        {
            Assert.IsTrue(Run.Sync(_s.SignInWithApiKey("key_test", "secret_test")), _s.SignInError);
            _server.Tools["model_schema_get"] = (args, i) => FakeMcp.Fixture("schema_flare.sse.txt");
            _server.Tools["model_run"] = (args, i) => FakeMcp.Fixture("estimate.sse.txt");
            var result = CallTool("generate", new JObject
            {
                ["model_id"] = "model_openai-gpt-image-2-5-flare",
                ["parameters"] = new JObject { ["prompt"] = "a lantern" },
                ["max_cu"] = 5,
            });
            Assert.AreEqual(true, (bool)result["isError"], TextOf(result));
            StringAssert.Contains("above max_cu", TextOf(result));
            StringAssert.Contains("Nothing was sent", TextOf(result));
            Assert.AreEqual(1, _server.Count("model_run"), "only the free price check");
            Assert.AreEqual(true, (bool)_server.ToolArguments[_server.ToolCalls.IndexOf("model_run")]["dry_run"]);
            Assert.AreEqual(0, _s.Ledger.Jobs.Count);
        }

        [Test]
        public void ModelsFind_Newest_PutsTheLatestModelsFirst()
        {
            Assert.IsTrue(Run.Sync(_s.SignInWithApiKey("key_test", "secret_test")), _s.SignInError);
            _s.Catalog.Models = new List<ModelInfo>
            {
                new ModelInfo { Id = "model_old", Name = "Old", CreatedAt = 1000, Tags = new List<string> { "sc:featured" } },
                new ModelInfo { Id = "model_new", Name = "New", CreatedAt = 3000 },
                new ModelInfo { Id = "model_mid", Name = "Mid", CreatedAt = 2000 },
            };
            _s.Catalog.FetchedAt = Json.Now();
            string[] Ids(JObject result) => JObject.Parse(TextOf(result))["models"].Select(m => (string)m["id"]).ToArray();
            Assert.AreEqual("model_old", Ids(CallTool("models_find", null))[0], "relevance puts the featured model first");
            CollectionAssert.AreEqual(new[] { "model_new", "model_mid", "model_old" }, Ids(CallTool("models_find", new JObject { ["sort"] = "newest" })));
        }

        [Test]
        public void Generate_UnknownParameter_IsRefusedBeforePricing()
        {
            Assert.IsTrue(Run.Sync(_s.SignInWithApiKey("key_test", "secret_test")), _s.SignInError);
            _server.Tools["model_schema_get"] = (args, i) => FakeMcp.Fixture("schema_flare.sse.txt");
            var result = CallTool("generate", new JObject
            {
                ["model_id"] = "model_openai-gpt-image-2-5-flare",
                ["parameters"] = new JObject { ["prompt"] = "a lantern", ["colour_mode"] = "neon" },
            });
            Assert.AreEqual(true, (bool)result["isError"]);
            StringAssert.Contains("Unknown parameters: colour_mode", TextOf(result));
            Assert.AreEqual(0, _server.Count("model_run"));
        }

        [UnityTest]
        public IEnumerator Generate_SlowJob_AnswersWithinTheCall_AndPointsToJobs()
        {
            Assert.IsTrue(Run.Sync(_s.SignInWithApiKey("key_test", "secret_test")), _s.SignInError);
            _server.Tools["model_schema_get"] = (args, i) => FakeMcp.Fixture("schema_flare.sse.txt");
            _server.Tools["model_run"] = (args, i) => Json.Truthy(args["dry_run"])
                ? FakeMcp.Fixture("estimate.sse.txt")
                : FakeMcp.Result(new JObject { ["job_id"] = "job_slow", ["status"] = "in-progress" });
            _server.Tools["job_get"] = (args, i) => FakeMcp.Result(new JObject { ["job"] = new JObject { ["jobId"] = "job_slow", ["status"] = "in-progress", ["progress"] = 0.3 } });
            var budget = AgentTools.CallSeconds;
            AgentTools.CallSeconds = 2;
            try
            {
                var started = Json.Now();
                var call = AgentServer.Dispatch(Rpc("tools/call", new JObject
                {
                    ["name"] = "generate",
                    ["arguments"] = new JObject { ["model_id"] = "model_openai-gpt-image-2-5-flare", ["parameters"] = new JObject { ["prompt"] = "a lantern" } },
                }), "");
                yield return Await(call, 30);
                Assert.Less(Json.Now() - started, 15, "answers within the call's time, not when the job ends");
                var result = (JObject)call.Result.response["result"];
                Assert.IsNull(result["isError"], TextOf(result));
                var next = (string)JObject.Parse(TextOf(result))["next"];
                StringAssert.Contains("Call jobs with local_id", next);
                StringAssert.Contains("Do not generate it again", next);
                Assert.AreEqual(1, _server.ToolArguments.Where((a, k) => _server.ToolCalls[k] == "model_run").Count(a => !Json.Truthy(a["dry_run"])), "sent once");
            }
            finally
            {
                AgentTools.CallSeconds = budget;
            }
        }

        // --- Scene tools ---------------------------------------------------------------------

        [Test]
        public void SceneEdits_Off_RefuseEditingTools_ButNotReading()
        {
            ScenarioUserSettings.instance.AgentCanEditScene = false;
            var refused = CallTool("object_create", new JObject { ["name"] = "Nope" });
            Assert.AreEqual(true, (bool)refused["isError"]);
            StringAssert.Contains("turned off", TextOf(refused));
            Assert.IsNull(GameObject.Find("Nope"));
            Assert.IsNull(CallTool("scene_info", null)["isError"]);
        }

        [Test]
        public void ObjectCreate_IsOneUndoStep_AndShowsInSceneInfo()
        {
            GameObject cube = null;
            try
            {
                ScenarioUserSettings.instance.AgentCanEditScene = true;
                var created = CallTool("object_create", new JObject { ["name"] = "Agent Cube", ["primitive"] = "cube", ["position"] = new JArray(1, 2, 3) });
                Assert.IsNull(created["isError"], TextOf(created));
                cube = GameObject.Find("Agent Cube");
                Assert.IsNotNull(cube);
                Assert.AreEqual(new Vector3(1, 2, 3), cube.transform.position);

                var info = JObject.Parse(TextOf(CallTool("scene_info", null)));
                var entry = info["objects"].First(o => (string)o["path"] == "Agent Cube");
                CollectionAssert.Contains(entry["components"].Select(c => (string)c), "MeshRenderer");

                Undo.PerformUndo();
                Assert.IsTrue(cube == null, "one Undo removes the created object");
            }
            finally
            {
                if (cube != null) UnityEngine.Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void Overview_ShowsTheScene_WhenThereIsNoCameraToCapture()
        {
            // A new empty scene, a closed Scene view or a batch editor: capture_view falls back to this.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Rendering needs a graphics device.");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var path = Capture.Overview(256);
                Assert.IsTrue(System.IO.File.Exists(path), path);
                var image = new Texture2D(2, 2);
                image.LoadImage(System.IO.File.ReadAllBytes(path));
                Assert.AreNotEqual(image.GetPixel(image.width / 2, image.height / 2), image.GetPixel(2, 2), "the cube is in the middle of the frame");
                UnityEngine.Object.DestroyImmediate(image);
                Assert.IsNull(GameObject.Find("Scenario Overview Camera"), "the temporary camera is gone");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
        }

        // --- Client setup --------------------------------------------------------------------

        [Test]
        public void ClientSnippets_CarryTheUrlAndToken()
        {
            const string url = "http://127.0.0.1:9999/mcp";
            const string token = "sut_abc";
            Assert.AreEqual("claude mcp add --transport http scenario-unity http://127.0.0.1:9999/mcp --header \"Authorization: Bearer sut_abc\"",
                ClientSetup.Snippet("claude", url, token));
            var codex = ClientSetup.Snippet("codex", url, token);
            StringAssert.Contains("[mcp_servers.scenario-unity]", codex);
            StringAssert.Contains("url = \"http://127.0.0.1:9999/mcp\"", codex);
            StringAssert.Contains("[mcp_servers.scenario-unity.http_headers]", codex);
            StringAssert.Contains("Authorization = \"Bearer sut_abc\"", codex);
            var cursor = JObject.Parse(ClientSetup.Snippet("cursor", url, token));
            Assert.AreEqual(url, (string)cursor["mcpServers"]["scenario-unity"]["url"]);
            Assert.AreEqual("Bearer sut_abc", (string)cursor["mcpServers"]["scenario-unity"]["headers"]["Authorization"]);
            var vscode = JObject.Parse(ClientSetup.Snippet("vscode", url, token));
            Assert.AreEqual("http", (string)vscode["servers"]["scenario-unity"]["type"]);
            Assert.AreEqual(url, (string)vscode["servers"]["scenario-unity"]["url"]);
            var other = JObject.Parse(ClientSetup.Snippet("other", url, token));
            Assert.AreEqual("streamable-http", (string)other["transport"]);
            Assert.AreEqual(ClientSetup.Clients.Count, ClientSetup.Clients.Select(c => c.Id).Distinct().Count());
        }

        // --- HTTP ----------------------------------------------------------------------------

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        string StartBridge()
        {
            AgentServer.Stop();
            Environment.SetEnvironmentVariable("SCENARIO_UNITY_AGENT_TOKEN", TestToken);
            AgentServer.ResetTokenCache();
            ScenarioUserSettings.instance.AgentPort = FreePort();
            AgentServer.Start();
            Assert.IsTrue(AgentServer.Running, AgentServer.Error);
            return AgentServer.Url;
        }

        sealed class Reply
        {
            public int Status;
            public string Body = "";
            public string Session = "";
            public string Allow = "";
            public string Authenticate = "";
        }

        static async Task<Reply> Http(HttpMethod method, string url, string body, string token = TestToken, string origin = null)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            using (var request = new HttpRequestMessage(method, url))
            {
                if (token != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    var reply = new Reply { Status = (int)response.StatusCode, Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false) };
                    if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) reply.Session = sessions.First();
                    if (response.Content.Headers.TryGetValues("Allow", out var allow) || response.Headers.TryGetValues("Allow", out allow)) reply.Allow = string.Join(",", allow);
                    if (response.Headers.TryGetValues("WWW-Authenticate", out var auth)) reply.Authenticate = string.Join(",", auth);
                    return reply;
                }
            }
        }

        /// <summary>Sends raw bytes so the Host header can be anything (DNS rebinding).</summary>
        static async Task<string> RawStatusLine(int port, string host)
        {
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                var stream = tcp.GetStream();
                var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}";
                var text = $"POST /mcp HTTP/1.1\r\nHost: {host}\r\nAuthorization: Bearer {TestToken}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                var bytes = Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                return Encoding.UTF8.GetString(buffer, 0, read).Split('\n')[0].Trim();
            }
        }

        /// <summary>Waits for a task while the editor keeps updating (the bridge runs calls on the main thread).</summary>
        static IEnumerator Await(Task task, double seconds = 20)
        {
            var until = EditorApplication.timeSinceStartup + seconds;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.IsTrue(task.IsCompleted, "timed out");
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        [UnityTest]
        public IEnumerator Http_RefusesWrongTokenPathMethodAndOrigin()
        {
            var url = StartBridge();
            var ping = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}";

            var noToken = Http(HttpMethod.Post, url, ping, token: null);
            yield return Await(noToken);
            Assert.AreEqual(401, noToken.Result.Status);
            StringAssert.Contains("Bearer", noToken.Result.Authenticate);

            var wrongToken = Http(HttpMethod.Post, url, ping, token: TestToken + "x");
            yield return Await(wrongToken);
            Assert.AreEqual(401, wrongToken.Result.Status);

            var wrongPath = Http(HttpMethod.Post, url.Replace("/mcp", "/other"), ping);
            yield return Await(wrongPath);
            Assert.AreEqual(404, wrongPath.Result.Status);

            var wrongPathNoToken = Http(HttpMethod.Post, url.Replace("/mcp", "/other"), ping, token: null);
            yield return Await(wrongPathNoToken);
            Assert.AreEqual(401, wrongPathNoToken.Result.Status, "the token is checked before the path");

            var get = Http(HttpMethod.Get, url, null);
            yield return Await(get);
            Assert.AreEqual(405, get.Result.Status);
            StringAssert.Contains("POST", get.Result.Allow);

            var foreignOrigin = Http(HttpMethod.Post, url, ping, origin: "https://evil.example");
            yield return Await(foreignOrigin);
            Assert.AreEqual(403, foreignOrigin.Result.Status);

            var nullOrigin = Http(HttpMethod.Post, url, ping, origin: "null");
            yield return Await(nullOrigin);
            Assert.AreEqual(403, nullOrigin.Result.Status);

            var localOrigin = Http(HttpMethod.Post, url, ping, origin: "http://localhost:5173");
            yield return Await(localOrigin);
            Assert.AreEqual(200, localOrigin.Result.Status);

            var rebinding = RawStatusLine(AgentServer.Port, "evil.example");
            yield return Await(rebinding);
            StringAssert.DoesNotContain(" 200", rebinding.Result, "a foreign Host never reaches the tools");
            StringAssert.StartsWith("HTTP/1.", rebinding.Result);

            var parseError = Http(HttpMethod.Post, url, "{not json");
            yield return Await(parseError);
            Assert.AreEqual(400, parseError.Result.Status);
            Assert.AreEqual(-32700, (int)JObject.Parse(parseError.Result.Body)["error"]["code"]);
        }

        [UnityTest]
        public IEnumerator Http_InitializeBatchesAndNotifications()
        {
            var url = StartBridge();

            var init = Http(HttpMethod.Post, url, Rpc("initialize", new JObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["clientInfo"] = new JObject { ["name"] = "HTTP test" },
            }).ToString());
            yield return Await(init);
            Assert.AreEqual(200, init.Result.Status, init.Result.Body);
            StringAssert.StartsWith("sus_", init.Result.Session);
            Assert.AreEqual("scenario-unity", (string)JObject.Parse(init.Result.Body)["result"]["serverInfo"]["name"]);

            var notification = Http(HttpMethod.Post, url, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            yield return Await(notification);
            Assert.AreEqual(202, notification.Result.Status);

            var batch = Http(HttpMethod.Post, url, new JArray(Rpc("ping", null, 7), Rpc("tools/list", null, 8)).ToString());
            yield return Await(batch);
            Assert.AreEqual(200, batch.Result.Status, batch.Result.Body);
            var replies = JArray.Parse(batch.Result.Body);
            Assert.AreEqual(2, replies.Count);
            CollectionAssert.AreEquivalent(new[] { 7, 8 }, replies.Select(r => (int)r["id"]));

            var close = Http(HttpMethod.Delete, url, null);
            yield return Await(close);
            Assert.AreEqual(200, close.Result.Status);
        }
    }
}
