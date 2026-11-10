using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Agent
{
    /// <summary>
    /// The user's Claude Code, run from inside Unity: where the claude command is, the
    /// command line that points it at this editor's agent bridge, and the environment it
    /// needs. No Unity API here, so it is tested without running claude.
    /// </summary>
    public static class ClaudeCode
    {
        public const string InstallHint = "Install Claude Code (npm install -g @anthropic-ai/claude-code, or the native installer from claude.com), or set its path in Settings.";
        public const string ToolPrefix = "mcp__" + ClientSetup.ServerName + "__";

        static bool Windows => Environment.OSVersion.Platform == PlatformID.Win32NT;
        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>Where installers put claude. Editors started from the Dock or the Hub get a minimal PATH that misses them.</summary>
        public static List<string> UsualFolders(string home, bool windows)
        {
            var folders = new List<string> { Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local") };
            if (windows) folders.Add(Path.Combine(home, "AppData", "Roaming", "npm"));
            else folders.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(home, ".npm-global", "bin") });
            return folders;
        }

        static IEnumerable<string> Split(string pathVariable, bool windows) =>
            (pathVariable ?? "").Split(windows ? ';' : ':').Select(f => f.Trim()).Where(f => f.Length > 0);

        /// <summary>
        /// The claude command: the configured path when there is one (null if it does not exist),
        /// else the first match on PATH and in the usual install folders.
        /// </summary>
        public static string Find(string configured, string pathVariable, string home, bool windows, Func<string, bool> exists)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var path = configured.Trim();
                if (path == "~" || path.StartsWith("~/")) path = home + path.Substring(1);
                return exists(path) ? path : null;
            }
            var names = windows ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
            foreach (var folder in Split(pathVariable, windows).Concat(UsualFolders(home, windows)).Distinct())
                foreach (var name in names)
                {
                    var candidate = Path.Combine(folder, name);
                    if (exists(candidate)) return candidate;
                }
            return null;
        }

        /// <summary>The claude command on this machine, or null. Main thread (reads the user settings).</summary>
        public static string Find() =>
            Find(ScenarioUserSettings.instance.ClaudeCodePath, Environment.GetEnvironmentVariable("PATH"), Home, Windows, File.Exists);

        /// <summary>
        /// PATH for the claude process: its own folder first (an npm install is a node script that
        /// needs node, often installed next to it), then the editor's PATH, then the usual folders.
        /// </summary>
        public static string ChildPath(string claude, string pathVariable, string home, bool windows)
        {
            var folders = new List<string>();
            var own = Path.GetDirectoryName(claude ?? "");
            if (!string.IsNullOrEmpty(own)) folders.Add(own);
            folders.AddRange(Split(pathVariable, windows));
            folders.AddRange(UsualFolders(home, windows));
            if (!windows) folders.AddRange(new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" });
            return string.Join(windows ? ";" : ":", folders.Distinct());
        }

        public static string ChildPath(string claude) => ChildPath(claude, Environment.GetEnvironmentVariable("PATH"), Home, Windows);

        /// <summary>The MCP config file for one run: only the bridge, with its bearer token.</summary>
        public static JObject McpConfig(string url, string token) => new JObject
        {
            ["mcpServers"] = new JObject
            {
                [ClientSetup.ServerName] = new JObject
                {
                    ["type"] = "http",
                    ["url"] = url,
                    ["headers"] = new JObject { ["Authorization"] = "Bearer " + token },
                },
            },
        };

        /// <summary>
        /// Print mode on the bridge's tools only (ToolSearch loads them), allowed without a prompt,
        /// streamed as JSON lines. The request goes on standard input, so quotes and new lines in
        /// it never meet a command line parser.
        /// </summary>
        public static List<string> Arguments(string configPath) => new List<string>
        {
            "-p",
            "--mcp-config", configPath,
            "--strict-mcp-config",
            "--tools", "ToolSearch",
            "--allowedTools", "mcp__" + ClientSetup.ServerName,
            "--permission-mode", "dontAsk",
            "--output-format", "stream-json",
            "--verbose",
        };

        /// <summary>The request, then what is selected in Unity (hierarchy paths or asset paths).</summary>
        public static string Prompt(string request, IEnumerable<string> selection)
        {
            var text = (request ?? "").Trim();
            var selected = (selection ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            return selected.Count == 0 ? text : text + "\n\nSelected in Unity: " + string.Join(", ", selected);
        }

        /// <summary>One command line string from arguments (quoted when they hold spaces or quotes).</summary>
        public static string CommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

        static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>One line for a run that ended without a result.</summary>
        public static string ExitMessage(int code, string stderr)
        {
            var lines = (stderr ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var all = string.Join(" ", lines);
            if (StreamJsonParser.IsSignInError(all)) return StreamJsonParser.SignIn;
            var last = lines.LastOrDefault() ?? "";
            if (last.Length > 160) last = last.Substring(0, 160) + "…";
            return last.Length > 0 ? $"Claude Code stopped (exit {code}): {last}" : $"Claude Code stopped (exit {code}) without an answer.";
        }
    }

    public enum AskEventKind
    {
        Info,
        Tool,
        Text,
        Warning,
        Result,
        Error,
    }

    /// <summary>One line of an Ask run: a tool call, the assistant's text, the result or an error.</summary>
    public sealed class AskEvent
    {
        public AskEventKind Kind;
        public string Text = "";
        public string Detail = "";

        public AskEvent() { }

        public AskEvent(AskEventKind kind, string text, string detail = "")
        {
            Kind = kind;
            Text = text ?? "";
            Detail = detail ?? "";
        }

        public override string ToString() => Detail.Length > 0 ? $"{Kind}: {Text} ({Detail})" : $"{Kind}: {Text}";
    }

    /// <summary>
    /// Claude Code's stream-json output (one JSON message per line) as short events. Keeps
    /// the tool names by call id, so a failed tool result names its tool. Thread-agnostic.
    /// </summary>
    public sealed class StreamJsonParser
    {
        public const string SignIn = "Claude Code is not signed in: run claude in a terminal once and sign in, then ask again.";

        static readonly string[] KeyArguments =
        {
            "prompt", "request", "text", "query", "name", "path", "object", "target", "character", "source", "on",
            "model_id", "modelId", "model", "asset_id", "workflow_id", "job_id", "id", "url",
        };

        readonly Dictionary<string, string> _tools = new Dictionary<string, string>();

        public int ToolCalls { get; private set; }
        /// <summary>A result line arrived (success or error).</summary>
        public bool Finished { get; private set; }

        public static bool IsSignInError(string text)
        {
            var lower = (text ?? "").ToLowerInvariant();
            return lower.Contains("/login") || lower.Contains("not logged in") || lower.Contains("invalid api key") || lower.Contains("please log in");
        }

        /// <summary>The tool's name without the bridge prefix.</summary>
        public static string ShortName(string tool)
        {
            tool = tool ?? "";
            if (tool.StartsWith(ClaudeCode.ToolPrefix, StringComparison.Ordinal)) return tool.Substring(ClaudeCode.ToolPrefix.Length);
            if (tool.StartsWith("mcp__", StringComparison.Ordinal))
            {
                var end = tool.IndexOf("__", 5, StringComparison.Ordinal);
                if (end > 0) return tool.Substring(end + 2);
            }
            return tool;
        }

        /// <summary>The argument that says what a call is about: a prompt, a name, a path, else the first text value.</summary>
        public static string KeyArgument(JObject input)
        {
            if (input == null) return "";
            JToken value = null;
            foreach (var key in KeyArguments)
            {
                var token = input[key];
                if (token != null && token.Type != JTokenType.Null && token.Type != JTokenType.Object && token.Type != JTokenType.Array && token.ToString().Length > 0)
                {
                    value = token;
                    break;
                }
            }
            value = value ?? input.Properties().Select(p => p.Value).FirstOrDefault(v => v.Type == JTokenType.String && v.ToString().Length > 0)
                    ?? input.Properties().Select(p => p.Value).FirstOrDefault(v => v is JValue && v.Type != JTokenType.Null);
            return Line(value?.ToString() ?? "", 80);
        }

        static string Line(string text, int max)
        {
            var line = string.Join(" ", (text ?? "").Split(new[] { '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return line.Length > max ? line.Substring(0, max - 1) + "…" : line;
        }

        static string ResultText(JToken content)
        {
            if (content == null) return "";
            if (content.Type == JTokenType.String) return content.ToString();
            if (content is JArray parts)
                return string.Join(" ", parts.OfType<JObject>().Where(p => (string)p["type"] == "text").Select(p => (string)p["text"] ?? ""));
            return "";
        }

        /// <summary>The events in one output line; none for blank lines, text that is not JSON and messages without anything to show.</summary>
        public List<AskEvent> Feed(string line)
        {
            var events = new List<AskEvent>();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{') return events;
            JObject message;
            try { message = JObject.Parse(line); }
            catch (Newtonsoft.Json.JsonException) { return events; }

            switch ((string)message["type"])
            {
                case "system":
                    if ((string)message["subtype"] != "init") break;
                    var bridge = (message["mcp_servers"] as JArray)?.OfType<JObject>().FirstOrDefault(s => (string)s["name"] == ClientSetup.ServerName);
                    var status = (string)bridge?["status"] ?? "missing";
                    if (status == "connected") events.Add(new AskEvent(AskEventKind.Info, "Claude Code is connected to this editor."));
                    else events.Add(new AskEvent(AskEventKind.Warning, $"Claude Code could not reach the agent bridge ({status}). Check that it is on in Agent."));
                    break;

                case "assistant":
                    var body = message["message"] as JObject;
                    // Synthetic messages repeat what the result line says (for example, not signed in).
                    if ((string)body?["model"] == "<synthetic>") break;
                    foreach (var part in (body?["content"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var type = (string)part["type"];
                        if (type == "text")
                        {
                            var text = ((string)part["text"] ?? "").Trim();
                            if (text.Length > 0) events.Add(new AskEvent(AskEventKind.Text, text));
                        }
                        else if (type == "tool_use")
                        {
                            var name = (string)part["name"] ?? "";
                            var input = part["input"] as JObject;
                            _tools[(string)part["id"] ?? ""] = ShortName(name);
                            ToolCalls++;
                            if (name == "ToolSearch")
                                events.Add(new AskEvent(AskEventKind.Tool, "find tools", Line(((string)input?["query"] ?? "").Replace(ClaudeCode.ToolPrefix, ""), 80)));
                            else
                                events.Add(new AskEvent(AskEventKind.Tool, ShortName(name), KeyArgument(input)));
                        }
                    }
                    break;

                case "user":
                    foreach (var part in ((message["message"] as JObject)?["content"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        if ((string)part["type"] != "tool_result" || part["is_error"]?.Type != JTokenType.Boolean || !(bool)part["is_error"]) continue;
                        _tools.TryGetValue((string)part["tool_use_id"] ?? "", out var tool);
                        events.Add(new AskEvent(AskEventKind.Warning, $"{tool ?? "A tool"} failed", Line(ResultText(part["content"]), 120)));
                    }
                    break;

                case "result":
                    Finished = true;
                    var isError = message["is_error"]?.Type == JTokenType.Boolean && (bool)message["is_error"];
                    var subtype = (string)message["subtype"] ?? "";
                    var result = ((string)message["result"] ?? "").Trim();
                    if (!isError && subtype == "success")
                    {
                        var seconds = Math.Round(((double?)message["duration_ms"] ?? 0) / 1000);
                        var calls = ToolCalls == 1 ? "1 tool call" : $"{ToolCalls} tool calls";
                        events.Add(new AskEvent(AskEventKind.Result, result.Length > 0 ? result : "Done.", seconds > 0 ? $"Done in {seconds} s, {calls}" : $"Done, {calls}"));
                    }
                    else events.Add(new AskEvent(AskEventKind.Error, Failure(result, subtype)));
                    break;
            }
            return events;
        }

        static string Failure(string result, string subtype)
        {
            if (IsSignInError(result)) return SignIn;
            if (subtype == "error_max_turns") return "Claude stopped after too many steps. Ask for a smaller change.";
            if (result.Length > 0) return Line(result, 200);
            return subtype == "error_during_execution" ? "Claude Code stopped with an error." : "Claude Code did not finish.";
        }
    }
}
