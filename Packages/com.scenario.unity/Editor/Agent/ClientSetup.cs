using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Agent
{
    /// <summary>Setup snippets per MCP client, and a one-click setup where the client has a CLI.</summary>
    public static class ClientSetup
    {
        public const string ServerName = "scenario-unity";

        public sealed class Client
        {
            public string Id;
            public string Name;
            public string Icon;
            public string Where;
            public bool CanConfigure;
        }

        public static readonly List<Client> Clients = new List<Client>
        {
            new Client { Id = "claude", Name = "Claude Code", Icon = "agent", Where = "CLI, this project", CanConfigure = true },
            new Client { Id = "codex", Name = "Codex", Icon = "agent", Where = "~/.codex/config.toml" },
            new Client { Id = "cursor", Name = "Cursor", Icon = "agent", Where = ".cursor/mcp.json" },
            new Client { Id = "vscode", Name = "VS Code", Icon = "agent", Where = ".vscode/mcp.json" },
            new Client { Id = "other", Name = "Other MCP client", Icon = "plug", Where = "Streamable HTTP" },
        };

        public static string Snippet(string id) => Snippet(id, AgentServer.Url, AgentServer.Token);

        /// <summary>The text a user pastes into a client's config (public for tests).</summary>
        public static string Snippet(string id, string url, string token)
        {
            var bearer = "Bearer " + token;
            switch (id)
            {
                case "claude":
                    return $"claude mcp add --transport http {ServerName} {url} --header \"Authorization: {bearer}\"";
                case "codex":
                    return $"[mcp_servers.{ServerName}]\nurl = \"{url}\"\n\n[mcp_servers.{ServerName}.http_headers]\nAuthorization = \"{bearer}\"\n";
                case "cursor":
                    return new JObject
                    {
                        ["mcpServers"] = new JObject { [ServerName] = new JObject { ["url"] = url, ["headers"] = new JObject { ["Authorization"] = bearer } } },
                    }.ToString(Formatting.Indented);
                case "vscode":
                    return new JObject
                    {
                        ["servers"] = new JObject { [ServerName] = new JObject { ["type"] = "http", ["url"] = url, ["headers"] = new JObject { ["Authorization"] = bearer } } },
                    }.ToString(Formatting.Indented);
                default:
                    return new JObject
                    {
                        ["name"] = ServerName,
                        ["transport"] = "streamable-http",
                        ["url"] = url,
                        ["headers"] = new JObject { ["Authorization"] = bearer },
                    }.ToString(Formatting.Indented);
            }
        }

        /// <summary>Runs the client's own CLI to add the server for this Unity project (Claude Code, local scope).</summary>
        public static (bool ok, string output) Configure(string id)
        {
            if (id != "claude") return (false, "Copy the setup into this client instead.");
            var claude = ClaudeCode.Find();
            if (claude == null) return (false, "the claude command was not found. Install Claude Code, set its path in Settings, or copy the setup.");
            var project = Path.GetFullPath(".");
            // An older entry with a stale token would shadow the new one.
            Run(claude, $"mcp remove {ServerName} -s local", project);
            return Run(claude, $"mcp add --transport http -s local {ServerName} {AgentServer.Url} --header \"Authorization: Bearer {AgentServer.Token}\"", project);
        }

        static (bool ok, string output) Run(string file, string arguments, string workingDirectory)
        {
            try
            {
                var info = new ProcessStartInfo(file, arguments)
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                // An npm install of claude is a node script: give it the folders a terminal would have.
                info.EnvironmentVariables["PATH"] = ClaudeCode.ChildPath(file);
                using (var process = Process.Start(info))
                {
                    if (process == null) return (false, "could not start " + file);
                    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(30000))
                    {
                        process.Kill();
                        return (false, "timed out");
                    }
                    return (process.ExitCode == 0, output.Trim());
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is IOException)
            {
                return (false, e.Message);
            }
        }
    }
}
