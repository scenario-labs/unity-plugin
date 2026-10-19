using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Scenario.Editor.Agent;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// Ask Claude: finding the claude command, the command line and MCP config that point it
    /// at the bridge, the PATH it gets, and its stream-json output read on recorded lines.
    /// claude itself never runs here.
    /// </summary>
    public class AskTests
    {
        const string Home = "/Users/tester";

        static System.Func<string, bool> Exists(params string[] files)
        {
            var set = new HashSet<string>(files);
            return set.Contains;
        }

        // --- Command line -------------------------------------------------------------------

        [Test]
        public void Arguments_PrintModeOnTheBridgeOnly_StreamedAsJson()
        {
            var arguments = ClaudeCode.Arguments("/tmp/scenario-unity-ask/mcp-config.json");
            CollectionAssert.AreEqual(new[]
            {
                "-p", "--mcp-config", "/tmp/scenario-unity-ask/mcp-config.json", "--strict-mcp-config", "--tools", "ToolSearch",
                "--allowedTools", "mcp__scenario-unity", "--permission-mode", "dontAsk", "--output-format", "stream-json", "--verbose",
            }, arguments);
            Assert.IsFalse(arguments.Any(a => a.Contains("Bearer")), "the token stays in the config file, off the command line");
        }

        [Test]
        public void CommandLine_QuotesArgumentsWithSpaces()
        {
            var line = ClaudeCode.CommandLine(new[] { "-p", "--mcp-config", "/Users/A B/tmp/mcp-config.json", "--verbose" });
            Assert.AreEqual("-p --mcp-config \"/Users/A B/tmp/mcp-config.json\" --verbose", line);
        }

        [Test]
        public void McpConfig_IsTheBridgeOverHttp_WithTheBearerToken()
        {
            var config = ClaudeCode.McpConfig("http://127.0.0.1:8791/mcp", "sut_testtoken");
            Assert.AreEqual(1, ((Newtonsoft.Json.Linq.JObject)config["mcpServers"]).Count);
            var server = config["mcpServers"]["scenario-unity"];
            Assert.AreEqual("http", (string)server["type"]);
            Assert.AreEqual("http://127.0.0.1:8791/mcp", (string)server["url"]);
            Assert.AreEqual("Bearer sut_testtoken", (string)server["headers"]["Authorization"]);
        }

        [Test]
        public void Prompt_IsTheRequest_ThenTheSelection()
        {
            Assert.AreEqual("make the bridge rustier\n\nSelected in Unity: Temple/Bridge, Assets/Scenario/Models/pine.glb",
                ClaudeCode.Prompt("  make the bridge rustier ", new[] { "Temple/Bridge", "", "Assets/Scenario/Models/pine.glb" }));
            Assert.AreEqual("add 20 pines around the temple", ClaudeCode.Prompt("add 20 pines around the temple", new string[0]));
        }

        // --- Finding claude -------------------------------------------------------------------

        [Test]
        public void Find_UsesTheConfiguredPath_OrNothing()
        {
            Assert.AreEqual("/opt/tools/claude", ClaudeCode.Find("/opt/tools/claude", "/usr/bin", Home, false, Exists("/opt/tools/claude", "/usr/bin/claude")));
            Assert.IsNull(ClaudeCode.Find("/opt/tools/claude", "/usr/bin", Home, false, Exists("/usr/bin/claude")), "a wrong path is not replaced by a guess");
            Assert.AreEqual(Home + "/bin/claude", ClaudeCode.Find("~/bin/claude", "", Home, false, Exists(Home + "/bin/claude")));
        }

        [Test]
        public void Find_SearchesPath_ThenTheUsualInstallFolders()
        {
            Assert.AreEqual("/custom/bin/claude", ClaudeCode.Find("", "/usr/bin:/custom/bin", Home, false, Exists("/custom/bin/claude", Home + "/.local/bin/claude")));
            // A GUI editor's minimal PATH misses the native installer's folder.
            Assert.AreEqual(Path.Combine(Home, ".local", "bin", "claude"), ClaudeCode.Find("", "/usr/bin:/bin", Home, false, Exists(Path.Combine(Home, ".local", "bin", "claude"))));
            Assert.AreEqual("/opt/homebrew/bin/claude", ClaudeCode.Find("", "/usr/bin", Home, false, Exists("/opt/homebrew/bin/claude")));
            Assert.AreEqual(Path.Combine(Home, ".claude", "local", "claude"), ClaudeCode.Find("", null, Home, false, Exists(Path.Combine(Home, ".claude", "local", "claude"))));
            Assert.IsNull(ClaudeCode.Find("", "/usr/bin", Home, false, Exists()));
            var npm = Path.Combine(Home, "AppData", "Roaming", "npm", "claude.cmd");
            Assert.AreEqual(npm, ClaudeCode.Find("", "", Home, true, Exists(npm)), "the npm shim on Windows");
        }

        [Test]
        public void ChildPath_PutsClaudesFolderFirst_ThenPath_ThenTheUsualFolders()
        {
            var path = ClaudeCode.ChildPath("/Users/tester/.nvm/versions/node/v22/bin/claude", "/usr/bin:/bin:/opt/homebrew/bin", Home, false);
            var folders = path.Split(':');
            Assert.AreEqual("/Users/tester/.nvm/versions/node/v22/bin", folders[0], "node installed next to an npm claude");
            Assert.AreEqual("/usr/bin", folders[1]);
            CollectionAssert.Contains(folders, "/usr/local/bin");
            CollectionAssert.Contains(folders, Path.Combine(Home, ".local", "bin"));
            Assert.AreEqual(folders.Length, folders.Distinct().Count(), "no folder twice");
        }

        // --- Stream-json ----------------------------------------------------------------------

        [Test]
        public void Parser_ReadsARecordedRun_AsShortLines()
        {
            var parser = new StreamJsonParser();
            var events = Fixtures.Read("claude-ask.stream.txt").Split('\n').SelectMany(parser.Feed).ToList();

            Assert.AreEqual(8, events.Count, string.Join("\n", events));
            Assert.AreEqual(AskEventKind.Info, events[0].Kind, "connected to the bridge");
            Assert.AreEqual(AskEventKind.Text, events[1].Kind);
            Assert.AreEqual("I'll look at the scene first.", events[1].Text);
            Assert.AreEqual(AskEventKind.Tool, events[2].Kind);
            Assert.AreEqual("find tools", events[2].Text);
            Assert.AreEqual("select:scene_info,scatter", events[2].Detail, "the bridge prefix is dropped");
            Assert.AreEqual("scene_info", events[3].Text);
            Assert.AreEqual("", events[3].Detail);
            Assert.AreEqual("scatter", events[4].Text);
            Assert.AreEqual("Assets/Scenario/Models/pine.glb", events[4].Detail, "the key argument");
            Assert.AreEqual(AskEventKind.Warning, events[5].Kind, "a failed tool call");
            Assert.AreEqual("scatter failed", events[5].Text);
            Assert.AreEqual("No object at \"Terrain\".", events[5].Detail);
            Assert.AreEqual(AskEventKind.Text, events[6].Kind);
            Assert.AreEqual(AskEventKind.Result, events[7].Kind);
            Assert.AreEqual("Placed 20 pines around the temple.", events[7].Text);
            Assert.AreEqual("Done in 12 s, 3 tool calls", events[7].Detail);
            Assert.IsTrue(parser.Finished);
            Assert.AreEqual(3, parser.ToolCalls);
        }

        [Test]
        public void Parser_ExplainsErrors_InOneLine()
        {
            var unreachable = new StreamJsonParser().Feed(
                "{\"type\":\"system\",\"subtype\":\"init\",\"mcp_servers\":[{\"name\":\"scenario-unity\",\"status\":\"failed\"}]}");
            Assert.AreEqual(AskEventKind.Warning, unreachable.Single().Kind);
            StringAssert.Contains("could not reach the agent bridge (failed)", unreachable[0].Text);

            var parser = new StreamJsonParser();
            Assert.IsEmpty(parser.Feed("{\"type\":\"assistant\",\"message\":{\"model\":\"<synthetic>\",\"content\":[{\"type\":\"text\",\"text\":\"Not logged in · Please run /login\"}]}}"),
                "the synthetic message repeats the result");
            var signIn = parser.Feed("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"duration_ms\":300,\"result\":\"Not logged in · Please run /login\"}");
            Assert.AreEqual(AskEventKind.Error, signIn.Single().Kind);
            Assert.AreEqual(StreamJsonParser.SignIn, signIn[0].Text);
            Assert.IsTrue(parser.Finished);

            var turns = new StreamJsonParser().Feed("{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":true}");
            StringAssert.Contains("too many steps", turns.Single().Text);

            Assert.IsEmpty(new StreamJsonParser().Feed(""));
            Assert.IsEmpty(new StreamJsonParser().Feed("{ not json"));
            Assert.IsEmpty(new StreamJsonParser().Feed("{\"type\":\"stream_event\",\"event\":{}}"));
        }

        [Test]
        public void ExitMessage_SaysWhatToDo()
        {
            Assert.AreEqual(StreamJsonParser.SignIn, ClaudeCode.ExitMessage(1, "Invalid API key · Please run /login\n"));
            Assert.AreEqual("Claude Code stopped (exit 2): Error: unknown option '--tools'", ClaudeCode.ExitMessage(2, "warning: old version\nError: unknown option '--tools'\n"));
            Assert.AreEqual("Claude Code stopped (exit 1) without an answer.", ClaudeCode.ExitMessage(1, ""));
        }

        [Test]
        public void KeyArgument_PrefersWhatTheCallIsAbout()
        {
            Assert.AreEqual("a rusty iron bridge", StreamJsonParser.KeyArgument(Newtonsoft.Json.Linq.JObject.Parse("{\"model_id\":\"model_x\",\"prompt\":\"a rusty iron bridge\"}")));
            Assert.AreEqual("Temple/Bridge", StreamJsonParser.KeyArgument(Newtonsoft.Json.Linq.JObject.Parse("{\"scale\":2,\"path\":\"Temple/Bridge\"}")));
            Assert.AreEqual("2", StreamJsonParser.KeyArgument(Newtonsoft.Json.Linq.JObject.Parse("{\"scale\":2}")));
            Assert.AreEqual(80, StreamJsonParser.KeyArgument(Newtonsoft.Json.Linq.JObject.Parse("{\"prompt\":\"" + new string('a', 200) + "\"}")).Length);
        }
    }
}
