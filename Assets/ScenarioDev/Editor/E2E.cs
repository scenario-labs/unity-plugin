using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using Scenario.Editor.Agent;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

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
    /// for the session. Device, Browser and Resume: a sign-in through the window, then a restart, a
    /// refresh and Sign out (below).
    /// Run with -batchmode -executeMethod ScenarioDev.E2E.OAuth, .Device, .Browser, .Resume or .Agent.
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
                var app = OAuthApp.Select(Environment.GetEnvironmentVariable);
                if (!app.Ok)
                {
                    File.WriteAllText(report, "FAIL sign-in: " + app.Error.Message);
                    return;
                }
                var oauth = new OAuthClient(transport, app.Value);
                var result = await OAuthSignIn.Run(oauth, url => File.WriteAllText(urlFile, url), TimeSpan.FromMinutes(10));
                if (!result.Ok)
                {
                    File.WriteAllText(report, "FAIL sign-in: " + result.Error.Message);
                    return;
                }
                var session = result.Value;
                var client = new ApiClient(transport, new OAuthAuth(oauth, session.Tokens, session.Email));
                var baseUrl = ApiClient.BaseUrlFrom(Environment.GetEnvironmentVariable);
                if (baseUrl.Ok) client.BaseUrl = baseUrl.Value;
                var teams = await client.ListTeams();
                var count = teams.Ok ? (teams.Value["teams"] as JArray)?.Count ?? 0 : -1;
                var domain = session.Email.Contains("@") ? session.Email.Substring(session.Email.IndexOf('@')) : "";
                File.WriteAllText(report, (teams.Ok ? "OK" : "FAIL") + $" sign-in: code exchanged as the registered application, user *{domain}, "
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

        /// <summary>
        /// Sign-in on another device through the Scenario window, as a person does it: the window opens
        /// signed out with no project remembered, the check clicks "Sign in on another device" and writes
        /// the page and the code to device.txt, and a person approves within 10 minutes. An account with
        /// several projects gets the project picker; the check chooses SCENARIO_E2E_PROJECT (a name or an
        /// id; on production, the project dedicated to plug-in tests), else the first project of the first
        /// team, and reads the catalog there (free). Window shots and device-report.txt go to the same
        /// folder. The session stays saved for E2E.Resume, which signs out.
        /// </summary>
        public static void Device() => EditorApplication.delayCall += () => _ = RunSignIn(true);

        /// <summary>
        /// The same through "Sign in with Scenario": the authorize URL lands in authorize-url.txt instead of
        /// opening a browser, and a person opens it and approves. Shots and browser-report.txt.
        /// </summary>
        public static void Browser() => EditorApplication.delayCall += () => _ = RunSignIn(false);

        static async Task RunSignIn(bool onDevice)
        {
            Directory.CreateDirectory(Dir);
            var name = onDevice ? "device" : "browser";
            var lines = new List<string>();
            var code = 1;
            // ScenarioService.OpenUrl is internal to the package: the dev tools reach it by reflection.
            var openUrl = typeof(ScenarioService).GetField("OpenUrl", BindingFlags.Static | BindingFlags.NonPublic);
            var previousOpenUrl = openUrl?.GetValue(null);
            try
            {
                var s = ScenarioService.Instance;
                if (s.IsSignedIn)
                {
                    lines.Add("FAIL signed in already: sign out first (E2E.Resume does)");
                    return;
                }
                // A first sign-in: no project remembered from an earlier session.
                var user = ScenarioUserSettings.instance;
                user.TeamId = user.ProjectId = user.TeamName = user.ProjectName = user.Plan = "";
                user.Save();
                var window = ScenarioWindow.Open();
                window.position = new Rect(40, 40, 460, 780);
                await Task.Delay(2000);
                Shoot(window, name + "-1-signed-out", lines);
                if (onDevice)
                {
                    Shots.Click(window.rootVisualElement.Q("signin-device"));
                    await Until(() => s.PendingDevice != null || s.State != SignInState.SigningIn, 30);
                    var device = s.PendingDevice;
                    if (device == null)
                    {
                        await Task.Delay(1000);
                        Shoot(window, name + "-2-refused", lines);
                        lines.Add("FAIL no code: " + s.SignInError);
                        return;
                    }
                    File.WriteAllText(Path.Combine(Dir, "device.txt"), device.PageToOpen + "\n" + device.UserCode + "\n");
                }
                else
                {
                    var urlFile = Path.Combine(Dir, "authorize-url.txt");
                    if (File.Exists(urlFile)) File.Delete(urlFile);
                    if (openUrl == null)
                    {
                        lines.Add("FAIL ScenarioService.OpenUrl not found");
                        return;
                    }
                    openUrl.SetValue(null, (Action<string>)(url => File.WriteAllText(urlFile, url)));
                    Shots.Click(window.rootVisualElement.Q("signin-browser"));
                    await Until(() => File.Exists(urlFile) || s.State != SignInState.SigningIn, 30);
                }
                await Task.Delay(1000);
                Shoot(window, name + "-2-waiting", lines);
                await Until(() => s.State != SignInState.SigningIn, 600);
                if (!s.IsSignedIn)
                {
                    lines.Add("FAIL sign-in: " + s.SignInError);
                    return;
                }
                await Until(() => !s.TeamsLoading && s.Teams.Count > 0, 30);
                await Task.Delay(1000);
                Shoot(window, name + "-3-signed-in", lines);
                lines.Add($"signed in as *{Domain(s.AccountLabel)}: {s.Teams.Count} team(s), {s.Teams.Sum(t => t.Projects.Count)} project(s), project picker {(s.NeedsWorkspace ? "shown" : "not needed")}");
                var wanted = (Environment.GetEnvironmentVariable("SCENARIO_E2E_PROJECT") ?? "").Trim();
                var choice = s.Teams.SelectMany(t => t.Projects.Select(p => (t, p)))
                    .FirstOrDefault(c => wanted.Length == 0 || c.p.Id == wanted || c.p.Name == wanted);
                if (choice.p == null)
                {
                    lines.Add(wanted.Length > 0 ? $"FAIL no project '{wanted}' in this account" : "FAIL no project to choose");
                    return;
                }
                s.SelectWorkspace(choice.t.Id, choice.p.Id);
                // Choosing a project starts the catalog refresh: wait for it to start and to finish.
                await Until(() => s.CatalogLoading, 5);
                var catalogRead = await Until(() => !s.CatalogLoading, 90);
                await Task.Delay(1500);
                Shoot(window, name + "-4-workspace", lines);
                lines.Add(!catalogRead ? "catalog FAIL: still loading after 90 seconds"
                    : s.CatalogError.Length > 0 ? "catalog FAIL: " + s.CatalogError
                    : $"catalog read in the chosen project: {s.Catalog.Models.Count} models");
                var saved = s.Credentials.Load();
                var app = OAuthApp.Select(Environment.GetEnvironmentVariable);
                var ownApp = app.Ok && app.Value.Issued(saved.Info);
                lines.Add($"session saved: {(saved.Method == "oauth" ? "yes" : "NO")}, in {saved.Source}, issued to {(ownApp ? "the selected application" : "ANOTHER application")}");
                code = catalogRead && s.CatalogError.Length == 0 && saved.Method == "oauth" && ownApp ? 0 : 1;
            }
            catch (Exception e)
            {
                lines.Add("FAIL " + e);
            }
            finally
            {
                if (openUrl != null) openUrl.SetValue(null, previousOpenUrl);
                lines.Insert(0, (code == 0 ? "OK" : "FAIL") + $" {name} sign-in");
                File.WriteAllLines(Path.Combine(Dir, name + "-report.txt"), lines);
                Debug.Log("E2E " + string.Join(" | ", lines));
                EditorApplication.Exit(code);
            }
        }

        /// <summary>
        /// After E2E.Device or E2E.Browser and an editor restart: the saved session resumes without a
        /// browser, the read at editor start refreshes the access token (the refresh token rotates in the
        /// credential store) and keeps the saved project, or, when SCENARIO_E2E_STALE_PROJECT=1 replaced it
        /// by one the account does not have, brings the picker back (or takes the account's only project).
        /// Then Sign out empties the store and the provider refuses the old refresh token.
        /// </summary>
        public static void Resume() => EditorApplication.delayCall += () => _ = RunResume();

        static async Task RunResume()
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>();
            var code = 1;
            try
            {
                // SCENARIO_E2E_STALE_PROJECT=1: the saved project is replaced by one the account does not
                // have, as after a deletion; the editor start must bring the picker back, or take the
                // account's only project.
                var stale = Environment.GetEnvironmentVariable("SCENARIO_E2E_STALE_PROJECT") == "1";
                var user = ScenarioUserSettings.instance;
                if (stale && user.ProjectId.Length > 0)
                {
                    user.ProjectId = "proj_e2e_gone";
                    user.Save();
                }
                var savedProject = user.ProjectId;
                var savedName = user.ProjectName;
                var s = ScenarioService.Instance;
                if (!s.IsSignedIn || !(s.Client.Auth is OAuthAuth))
                {
                    lines.Add("FAIL no saved Scenario account session to resume: " + s.SignInError);
                    return;
                }
                var before = Fingerprint(s.Credentials.Load().Secret);
                var window = ScenarioWindow.Open();
                window.position = new Rect(40, 40, 460, 780);
                // The editor start reads the teams once and checks the saved project.
                await Until(() => s.Teams.Count > 0 || !s.IsSignedIn, 30);
                await Task.Delay(1500);
                if (!s.IsSignedIn)
                {
                    lines.Add("FAIL the saved session was refused: " + s.SignInError);
                    return;
                }
                // The rotated token is saved on the main thread after the refresh.
                var rotated = await Until(() => Fingerprint(s.Credentials.Load().Secret) != before, 10);
                lines.Add($"resumed without a browser as *{Domain(s.AccountLabel)}, {s.Teams.Count} team(s) read, refresh token rotated: {(rotated ? "yes" : "NO")}");
                var projects = s.Teams.SelectMany(t => t.Projects).ToList();
                // Stale: several projects left bring the picker back; a single one is taken (WorkspaceTests).
                var onlyProject = projects.Count == 1 ? projects[0].Id : "";
                var workspaceOk = !stale ? !s.NeedsWorkspace && s.Client.ProjectId == savedProject
                    : onlyProject.Length > 0 ? !s.NeedsWorkspace && s.Client.ProjectId == onlyProject
                    : s.NeedsWorkspace;
                lines.Add(!stale
                    ? $"saved project ({(savedName.Length > 0 ? "chosen before the restart" : "none")}) {(workspaceOk ? "kept" : "NOT kept")} after the restart"
                    : onlyProject.Length > 0
                        ? $"saved project replaced by one the account does not have: the only project {(workspaceOk ? "taken" : "NOT taken")}"
                        : $"saved project replaced by one the account does not have: picker {(s.NeedsWorkspace ? "shown again" : "NOT shown")}");
                Shoot(window, stale && onlyProject.Length == 0 ? "resume-1-picker-again" : "resume-1-signed-in", lines);

                var refresh = ((OAuthAuth)s.Client.Auth).Tokens.RefreshToken;
                s.SignOut();
                var saved = s.Credentials.Load();
                var emptied = saved.Method.Length == 0 && saved.Secret.Length == 0;
                lines.Add($"signed out: credential store {(emptied ? "empty" : "NOT EMPTY")}");
                // Sign out revokes in the background.
                await Task.Delay(3000);
                var app = OAuthApp.Select(Environment.GetEnvironmentVariable);
                var reuse = await new OAuthClient(new HttpTransport(), app.Value).Refresh(refresh, Json.Now());
                // Only a refusal proves the revocation: a network or server failure does not.
                var refused = !reuse.Ok && reuse.Error.Kind == ErrorKind.Auth;
                lines.Add($"provider refuses the old refresh token: {(refused ? "yes" : "NO")}");
                Shoot(window, "resume-2-signed-out", lines);
                code = rotated && workspaceOk && emptied && refused ? 0 : 1;
            }
            catch (Exception e)
            {
                lines.Add("FAIL " + e);
            }
            finally
            {
                lines.Insert(0, (code == 0 ? "OK" : "FAIL") + " resume and sign out");
                File.WriteAllLines(Path.Combine(Dir, "resume-report.txt"), lines);
                Debug.Log("E2E " + string.Join(" | ", lines));
                EditorApplication.Exit(code);
            }
        }

        static void Shoot(EditorWindow window, string name, List<string> lines)
        {
            var error = WindowShots.Capture(window, Path.Combine(Dir, name + ".png"));
            if (error.Length > 0) lines.Add($"shot {name}: {error}");
        }

        static async Task<bool> Until(Func<bool> condition, double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > end) return false;
                await Task.Delay(200);
            }
            return true;
        }

        static string Domain(string email) => email.Contains("@") ? email.Substring(email.IndexOf('@')) : "";

        /// <summary>Compares secrets without logging them.</summary>
        static string Fingerprint(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret))).Substring(0, 12);
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
