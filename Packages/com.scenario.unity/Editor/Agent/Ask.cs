using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Scenario.Editor.Agent
{
    /// <summary>
    /// Ask from inside Unity: runs the user's Claude Code in print mode against this
    /// editor's agent bridge and keeps its progress as short lines for the Scene view
    /// overlay and the Agent view. Paid calls still go through the bridge's approval rules.
    /// One run at a time; the log survives script reloads, a running request does not.
    /// </summary>
    [InitializeOnLoad]
    public static class Ask
    {
        const int MaxEvents = 200;
        const string StateKey = "Scenario.Ask";

        public static readonly List<AskEvent> Events = new List<AskEvent>();
        /// <summary>Raised on the main thread when a line arrives or a run starts or ends.</summary>
        public static event Action Changed;
        public static string Request { get; private set; } = "";
        public static bool Running => _process != null;

        static Process _process;
        static int _run;
        static bool _stopping;

        static Ask()
        {
            Restore();
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
        }

        /// <summary>
        /// Starts a run (main thread). Turns the bridge on if needed. False, with the reason in
        /// the log, when the request is empty, a run is going, or claude cannot start.
        /// </summary>
        public static bool Start(string request, IEnumerable<string> selection)
        {
            request = (request ?? "").Trim();
            if (request.Length == 0) return false;
            if (Running)
            {
                Add(AskEventKind.Warning, "Claude is still working on the last request: Stop it first.");
                return false;
            }
            Events.Clear();
            Request = request;
            _stopping = false;
            var run = ++_run;

            if (!AgentServer.Running)
            {
                AgentServer.SetEnabled(true);
                if (!AgentServer.Running)
                {
                    Add(AskEventKind.Error, "The agent bridge could not start. " + AgentServer.Error);
                    return false;
                }
                Add(AskEventKind.Info, "Turned the agent bridge on so Claude can work in this editor.");
            }

            var claude = ClaudeCode.Find();
            if (claude == null)
            {
                var configured = ScenarioUserSettings.instance.ClaudeCodePath.Trim();
                Add(AskEventKind.Error, configured.Length > 0
                    ? $"No Claude Code at {configured}. Fix the path in Settings, or clear it to find claude automatically."
                    : "Claude Code was not found. " + ClaudeCode.InstallHint + " The overlay's buttons work without it.");
                return false;
            }

            string folder, config;
            try
            {
                // Same folder and file each run: the config holds the bridge token, so it stays in the user's temp folder.
                folder = Path.Combine(Path.GetTempPath(), "scenario-unity-ask");
                Directory.CreateDirectory(folder);
                config = Path.Combine(folder, "mcp-config.json");
                File.WriteAllText(config, ClaudeCode.McpConfig(AgentServer.Url, AgentServer.Token).ToString(Formatting.None));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Add(AskEventKind.Error, "Could not write the temporary MCP config: " + e.Message);
                return false;
            }

            var info = new ProcessStartInfo(claude, ClaudeCode.CommandLine(ClaudeCode.Arguments(config)))
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.EnvironmentVariables["PATH"] = ClaudeCode.ChildPath(claude);
            // Set when Unity itself was started from a Claude Code session; claude refuses to nest.
            info.EnvironmentVariables.Remove("CLAUDECODE");

            Process process;
            try { process = Process.Start(info); }
            catch (Exception e) when (e is Win32Exception || e is InvalidOperationException || e is IOException)
            {
                Add(AskEventKind.Error, $"Could not start Claude Code: {e.Message}");
                return false;
            }
            if (process == null)
            {
                Add(AskEventKind.Error, "Could not start Claude Code.");
                return false;
            }
            _process = process;
            Add(AskEventKind.Info, "Claude Code is starting.");

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderr)
                {
                    if (stderr.Length < 16000) stderr.AppendLine(e.Data);
                }
            };
            process.BeginErrorReadLine();
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(ClaudeCode.Prompt(request, selection));
                process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process ended at once; its exit is reported below.
            }
            new Thread(() => Read(process, run, stderr)) { IsBackground = true, Name = "Scenario Ask" }.Start();
            return true;
        }

        /// <summary>Stops the running request (kills claude). Calls already sent to the bridge finish.</summary>
        public static void Stop()
        {
            if (_process == null) return;
            _stopping = true;
            try
            {
                if (!_process.HasExited) _process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException || e is Win32Exception)
            {
                // Already gone.
            }
        }

        /// <summary>Clears the log of a finished run.</summary>
        public static void Clear()
        {
            if (Running) return;
            Events.Clear();
            Request = "";
            Save();
            Changed?.Invoke();
        }

        // Worker thread: one line at a time, events handed to the main thread.
        static void Read(Process process, int run, StringBuilder stderr)
        {
            var parser = new StreamJsonParser();
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    var events = parser.Feed(line);
                    if (events.Count > 0) MainThread.Post(() => Deliver(run, events));
                }
                process.WaitForExit();
            }
            catch (Exception e) when (e is IOException || e is InvalidOperationException || e is ObjectDisposedException)
            {
                // The pipe closed: the exit below says how it ended.
            }
            var code = -1;
            try { code = process.ExitCode; }
            catch (InvalidOperationException) { }
            string errors;
            lock (stderr) errors = stderr.ToString();
            var finished = parser.Finished;
            MainThread.Post(() => Exited(run, process, code, finished, errors));
        }

        static void Deliver(int run, List<AskEvent> events)
        {
            if (run != _run) return;
            foreach (var e in events) Append(e);
            Changed?.Invoke();
        }

        static void Exited(int run, Process process, int code, bool finished, string stderr)
        {
            process.Dispose();
            if (run != _run) return;
            _process = null;
            if (_stopping) Append(new AskEvent(AskEventKind.Info, "Stopped."));
            else if (!finished) Append(new AskEvent(AskEventKind.Error, ClaudeCode.ExitMessage(code, stderr)));
            _stopping = false;
            Save();
            Changed?.Invoke();
        }

        static void Add(AskEventKind kind, string text)
        {
            Append(new AskEvent(kind, text));
            Changed?.Invoke();
        }

        static void Append(AskEvent e)
        {
            Events.Add(e);
            if (Events.Count > MaxEvents) Events.RemoveRange(0, Events.Count - MaxEvents);
        }

        // --- Script reloads: the log is kept, the process is not -------------------------

        static void BeforeReload()
        {
            if (_process != null)
            {
                Stop();
                Append(new AskEvent(AskEventKind.Info, "Stopped: Unity reloaded its scripts."));
                _process = null;
            }
            Save();
        }

        static void Save()
        {
            var events = new JArray(Events.Select(e => new JObject { ["k"] = e.Kind.ToString(), ["t"] = e.Text, ["d"] = e.Detail }));
            SessionState.SetString(StateKey, new JObject { ["request"] = Request, ["events"] = events }.ToString(Formatting.None));
        }

        static void Restore()
        {
            var saved = Core.Json.ParseObject(SessionState.GetString(StateKey, ""));
            if (saved == null) return;
            Request = (string)saved["request"] ?? "";
            foreach (var e in (saved["events"] as JArray ?? new JArray()).OfType<JObject>())
                if (Enum.TryParse((string)e["k"], out AskEventKind kind))
                    Events.Add(new AskEvent(kind, (string)e["t"], (string)e["d"]));
        }
    }
}
