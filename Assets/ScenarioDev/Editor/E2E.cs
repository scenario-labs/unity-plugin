using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using Scenario.Editor.Agent;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): live end-to-end checks driven from outside the editor.
    /// OAuth: runs the browser sign-in with the authorize URL written to Library/ScenarioDevLogs/e2e
    /// instead of opening a browser; a person opens it and signs in (Scenario's sign-in page has a
    /// human check, so a headless browser cannot). Nothing is saved: the tokens make one free read and
    /// are dropped. Agent: turns the agent bridge on and keeps the editor running until a stop file
    /// appears, so an agent CLI can drive it (tools/e2e_agent.py, tools/world_rehearsal.py). For long
    /// builds: SCENARIO_E2E_MINUTES (default 15), SCENARIO_E2E_AUTO_APPROVE and SCENARIO_E2E_BUDGET (CU,
    /// for this run only; the user's settings come back at the end), SCENARIO_E2E_PORT (the bridge port, when
    /// another editor already serves the default one) and SCENARIO_E2E_SCENE (a new scene
    /// with a camera and a light, saved at that path, for the agent to build in). Unity's audio is muted
    /// for the session.
    /// Run with -batchmode -executeMethod ScenarioDev.E2E.OAuth or .Agent.
    /// </summary>
    public static class E2E
    {
        static string Dir => Path.GetFullPath("Library/ScenarioDevLogs/e2e");

        public static void OAuth() => EditorApplication.delayCall += () => _ = RunOAuth();

        static async Task RunOAuth()
        {
            Directory.CreateDirectory(Dir);
            var urlFile = Path.Combine(Dir, "authorize-url.txt");
            var report = Path.Combine(Dir, "oauth.txt");
            if (File.Exists(urlFile)) File.Move(urlFile, urlFile + "." + DateTime.Now.ToString("HHmmss"));
            var code = 1;
            try
            {
                var transport = new HttpTransport();
                var oauth = new OAuthClient(transport);
                var result = await OAuthSignIn.Run(oauth, url => File.WriteAllText(urlFile, url), "", "", TimeSpan.FromMinutes(10));
                if (!result.Ok)
                {
                    File.WriteAllText(report, "FAIL sign-in: " + result.Error.Message);
                    return;
                }
                var session = result.Value;
                var client = new McpClient(transport, new OAuthAuth(oauth, session.ClientId, session.Tokens, session.Email));
                var teams = await client.ListTeams();
                var count = teams.Ok ? (teams.Value["teams"] as JArray)?.Count ?? 0 : -1;
                var domain = session.Email.Contains("@") ? session.Email.Substring(session.Email.IndexOf('@')) : "";
                File.WriteAllText(report, (teams.Ok ? "OK" : "FAIL") + $" sign-in: client registered, code exchanged, user *{domain}, "
                                          + (teams.Ok ? $"{count} team(s) listed with the token" : "teams_list failed: " + teams.Error.Message)
                                          + $", access token expires in {Math.Round(session.Tokens.ExpiresAt - Json.Now())} s, refresh token {(session.Tokens.RefreshToken.Length > 0 ? "received" : "missing")}. Nothing saved.");
                code = teams.Ok ? 0 : 1;
            }
            catch (Exception e)
            {
                File.WriteAllText(report, "FAIL " + e);
            }
            finally
            {
                Debug.Log("E2E " + (File.Exists(report) ? File.ReadAllText(report) : "no report"));
                EditorApplication.Exit(code);
            }
        }

        static double _deadline;
        static string _settings;
        static bool _muted;

        static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

        public static void Agent()
        {
            Directory.CreateDirectory(Dir);
            var stop = Path.Combine(Dir, "agent-stop");
            if (File.Exists(stop)) File.Move(stop, stop + "." + DateTime.Now.ToString("HHmmss"));
            _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);
            // Silent session: scenes the agent builds play their audio in Play mode, and nobody is listening.
            _muted = EditorUtility.audioMasterMute;
            EditorUtility.audioMasterMute = true;
            var settings = ScenarioUserSettings.instance;
            settings.AgentCanEditScene = true;
            settings.AgentAutoApproveCu = EnvInt("SCENARIO_E2E_AUTO_APPROVE", settings.AgentAutoApproveCu);
            settings.AgentSessionBudgetCu = EnvInt("SCENARIO_E2E_BUDGET", settings.AgentSessionBudgetCu);
            settings.AgentPort = EnvInt("SCENARIO_E2E_PORT", settings.AgentPort);
            Debug.Log($"E2E settings: project '{settings.ProjectName}' ({settings.ProjectId}), port {settings.AgentPort}, auto-approve {settings.AgentAutoApproveCu} CU, budget {settings.AgentSessionBudgetCu} CU");
            var scene = Environment.GetEnvironmentVariable("SCENARIO_E2E_SCENE") ?? "";
            if (scene.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scene) ?? "Assets");
                var created = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects, UnityEditor.SceneManagement.NewSceneMode.Single);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(created, scene);
            }
            AgentServer.SetEnabled(true);
            File.WriteAllText(Path.Combine(Dir, "agent-endpoint.json"), new JObject
            {
                ["url"] = AgentServer.Url,
                ["authorization"] = "Bearer " + AgentServer.Token,
                ["running"] = AgentServer.Running,
                ["error"] = AgentServer.Error,
            }.ToString());
            Debug.Log($"E2E agent bridge running={AgentServer.Running} url={AgentServer.Url}");
            _deadline = EditorApplication.timeSinceStartup + 60 * EnvInt("SCENARIO_E2E_MINUTES", 15);
            EditorApplication.update += Wait;
        }

        static void Wait()
        {
            if (!File.Exists(Path.Combine(Dir, "agent-stop")) && EditorApplication.timeSinceStartup < _deadline) return;
            EditorApplication.update -= Wait;
            File.WriteAllLines(Path.Combine(Dir, "agent-activity.txt"),
                AgentServer.Activity.Select(a => $"{a.Time} {a.Client} {a.Tool} {(a.IsError ? "ERROR " : "")}{a.Summary}"));
            AgentServer.SetEnabled(false);
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
            ScenarioUserSettings.instance.Save();
            EditorUtility.audioMasterMute = _muted;
            Debug.Log("E2E agent done");
            EditorApplication.Exit(0);
        }
    }
}
