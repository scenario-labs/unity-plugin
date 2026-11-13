using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// Which team and project a Scenario account works in: several projects ask, a single one is chosen
    /// without asking, a saved one is kept while the account still has it and asked again once it is gone.
    /// User settings are restored after each test.
    /// </summary>
    public class WorkspaceTests
    {
        FakeMcp _server;
        string _settings;
        string _folder;
        ScenarioWindow _window;
        readonly List<(string message, string level)> _notices = new List<(string, string)>();

        [SetUp]
        public void SetUp()
        {
            _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);
            _server = new FakeMcp
            {
                // The resumed session refreshes its access token before the first call.
                Other = request => request.Url == OAuthApp.Production.TokenEndpoint
                    ? new HttpReply { Status = 200, Body = "{\"access_token\":\"at\",\"refresh_token\":\"rt_next\",\"expires_in\":3600}" }
                    : new HttpReply { Status = 404 },
            };
            _server.Tools["models_list"] = (args, i) => FakeMcp.Result(new JObject { ["models"] = new JArray() });
            _folder = TempDir.Make("workspace");
            _notices.Clear();
            // Each test is a new editor session: the check at start has not run yet, and the saved
            // session has no access token, so its first call refreshes.
            SessionState.EraseBool(ScenarioService.TeamsCheckedKey);
            _accessToken = SessionState.GetString(AccessTokenKey, "");
            _accessExpiry = SessionState.GetFloat(AccessExpiryKey, 0);
            SessionState.EraseString(AccessTokenKey);
            SessionState.EraseFloat(AccessExpiryKey);
        }

        const string AccessTokenKey = "Scenario.AccessToken";
        const string AccessExpiryKey = "Scenario.AccessExpiry";
        string _accessToken;
        float _accessExpiry;

        [TearDown]
        public void TearDown()
        {
            SessionState.EraseBool(ScenarioService.TeamsCheckedKey);
            SessionState.SetString(AccessTokenKey, _accessToken);
            SessionState.SetFloat(AccessExpiryKey, _accessExpiry);
            if (_window != null) _window.Close();
            ScenarioService.ResetForTests();
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
        }

        /// <summary>teams_list for an account: each team with its project ids.</summary>
        void Account(params (string team, string[] projects)[] teams)
        {
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Result(new JObject
            {
                ["teams"] = new JArray(teams.Select(t => new JObject
                {
                    ["id"] = t.team,
                    ["name"] = "Team " + t.team,
                    ["plan"] = "pro",
                    ["projects"] = new JArray(t.projects.Select(p => new JObject { ["id"] = p, ["name"] = "Project " + p })),
                })),
            });
        }

        /// <summary>Editor start with a saved browser or device sign-in and, when given, a saved project.</summary>
        ScenarioService Resume(string team = "", string project = "")
        {
            var user = ScenarioUserSettings.instance;
            user.TeamId = team;
            user.ProjectId = project;
            user.TeamName = team.Length > 0 ? "Team " + team : "";
            user.ProjectName = project.Length > 0 ? "Project " + project : "";
            var store = new CredentialStore(Path.Combine(_folder, "credentials.json"), new MemorySecretStore());
            store.SaveRefreshToken("rt_saved", SavedInfo());
            var s = ScenarioService.UseForTests(_server, _folder, store);
            s.Notified += (message, level) => _notices.Add((message, level));
            Assert.IsTrue(s.IsSignedIn, "the saved session resumes");
            return s;
        }

        static JObject SavedInfo() => new JObject { ["issuer"] = OAuthApp.ProductionIssuer, ["client_id"] = OAuthApp.ProductionClientId, ["email"] = "me@x.com" };

        static readonly (string, string[])[] TwoTeams = { ("t1", new[] { "p1", "p2" }), ("t2", new[] { "p3" }) };

        [Test]
        public void SeveralProjects_AskWhichOne()
        {
            Account(TwoTeams);
            var s = Resume();
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.NeedsWorkspace, "nothing is chosen for the person");
            Assert.AreEqual(2, s.Teams.Count);
            Assert.AreEqual(3, s.Teams.Sum(t => t.Projects.Count));
            Assert.IsFalse(_notices.Any(n => n.level == "warning"));
        }

        [Test]
        public void ASingleProject_IsChosenWithoutAsking()
        {
            Account(("t1", new[] { "p1" }));
            var s = Resume();
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.Ready);
            Assert.AreEqual("p1", s.Client.ProjectId);
            Assert.AreEqual("Project p1", ScenarioUserSettings.instance.ProjectName);
        }

        [Test]
        public void ASavedProjectTheAccountStillHas_IsKept()
        {
            Account(TwoTeams);
            var s = Resume("t2", "p3");
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.Ready);
            Assert.AreEqual("t2", s.Client.TeamId);
            Assert.AreEqual("p3", s.Client.ProjectId);
            Assert.IsFalse(_notices.Any(n => n.level == "warning"));
        }

        [Test]
        public void ASavedProjectThatIsGone_IsAskedAgain()
        {
            Account(TwoTeams);
            var s = Resume("t1", "p_deleted");
            Assert.IsFalse(s.NeedsWorkspace, "before the check, the saved project looks chosen");
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.NeedsWorkspace, "the picker comes back");
            Assert.AreEqual("", s.Client.ProjectId, "no call goes to the old project");
            Assert.AreEqual("", ScenarioUserSettings.instance.ProjectId);
            var notice = _notices.Single(n => n.level == "warning").message;
            StringAssert.Contains("Project p_deleted", notice);
            StringAssert.Contains("Pick a project", notice);
        }

        [Test]
        public void ASavedTeamThatIsGone_IsAskedAgain()
        {
            Account(TwoTeams);
            var s = Resume("t_left", "p_left");
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.NeedsWorkspace);
            Assert.AreEqual("", s.Client.TeamId);
        }

        [Test]
        public void ASavedProjectThatIsGone_WithASingleProjectLeft_TakesIt_AndSaysSo()
        {
            Account(("t1", new[] { "p1" }));
            var s = Resume("t9", "p_gone");
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.Ready);
            Assert.AreEqual("p1", s.Client.ProjectId);
            var notice = _notices.Single(n => n.level == "warning").message;
            StringAssert.Contains("Project p_gone", notice);
            StringAssert.Contains("Working in Project p1", notice, "generations go elsewhere now: the person is told");
        }

        [Test]
        public void ASavedTeamListedWithoutItsProjects_KeepsTheSavedProject()
        {
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Result(new JObject
            {
                ["teams"] = new JArray(new JObject { ["id"] = "t1", ["name"] = "Team t1" }, new JObject
                {
                    ["id"] = "t2", ["name"] = "Team t2", ["projects"] = new JArray(new JObject { ["id"] = "p3", ["name"] = "Project p3" }),
                }),
            });
            var s = Resume("t1", "p1");
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.AreEqual("p1", s.Client.ProjectId, "nothing says the project is gone");
            Assert.IsFalse(_notices.Any(n => n.level == "warning"));
        }

        [Test]
        public void NoTeamOrNoProject_LeavesNothingChosen()
        {
            Account();
            var s = Resume();
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.NeedsWorkspace);
            Assert.AreEqual(0, s.Teams.Count);

            Account(("t1", new string[0]));
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.IsTrue(s.NeedsWorkspace);
            Assert.AreEqual(1, s.Teams.Count);
        }

        [Test]
        public void AFailedRead_KeepsTheSavedProject()
        {
            // Offline at editor start: the choice stays until Scenario answers.
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Status(503);
            var s = Resume("t1", "p1");
            Run.Sync(s.LoadTeams(autoPick: true));
            Assert.AreEqual("p1", s.Client.ProjectId);
            Assert.AreEqual("p1", ScenarioUserSettings.instance.ProjectId);
        }

        [Test]
        public void SignOut_LeavesNoProjectForTheNextAccount()
        {
            Account(TwoTeams);
            var s = Resume("t1", "p1");
            Run.Sync(s.LoadTeams(autoPick: true));
            s.SignOut();
            var user = ScenarioUserSettings.instance;
            Assert.AreEqual("", user.TeamId + user.ProjectId + user.TeamName + user.ProjectName);
            Assert.AreEqual("", s.Client.ProjectId);
            Assert.AreEqual(0, s.Teams.Count);
        }

        [UnityTest]
        public IEnumerator EditorStart_ChecksTheSavedProject_OncePerSession()
        {
            Account(TwoTeams);
            var s = Resume("t1", "p_deleted");
            var until = EditorApplication.timeSinceStartup + 10;
            while (!s.NeedsWorkspace && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.AreEqual(1, _server.Count("teams_list"), "one free read at start");
            Assert.IsTrue(s.NeedsWorkspace, "the picker comes back without any action");
            Assert.AreEqual(2, s.Teams.Count, "the project menus are filled");

            // A script reload builds the service again in the same editor session: no second read.
            var again = Resume("t1", "p1");
            for (var i = 0; i < 10; i++) yield return null;
            Assert.AreEqual(1, _server.Count("teams_list"), "not after every script reload");
            Assert.AreEqual("p1", again.Client.ProjectId);
        }

        [UnityTest]
        public IEnumerator EditorStart_Offline_KeepsTheChoice_Quietly()
        {
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Status(503);
            var s = Resume("t1", "p1");
            var until = EditorApplication.timeSinceStartup + 10;
            while (_server.Count("teams_list") == 0 && EditorApplication.timeSinceStartup < until) yield return null;
            for (var i = 0; i < 5; i++) yield return null;
            Assert.AreEqual("p1", s.Client.ProjectId);
            Assert.IsFalse(_notices.Any(n => n.level == "error"), "a background check does not show a failure");
        }

        [UnityTest]
        public IEnumerator TwoReadsAtOnce_ShareOneRequest()
        {
            Account(TwoTeams);
            // No check at start in this test: only the two reads below.
            SessionState.SetBool(ScenarioService.TeamsCheckedKey, true);
            // Answers later, like the network: the second caller arrives while the first read is in flight.
            var s = ScenarioService.UseForTests(new WorkerThreadTransport(_server), _folder, StoreOf(_folder));
            var before = _server.Count("teams_list");
            var first = s.LoadTeams(autoPick: true);
            var second = s.LoadTeams(autoPick: true);
            Assert.AreSame(first, second);
            var until = EditorApplication.timeSinceStartup + 10;
            while (!first.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.AreEqual(before + 1, _server.Count("teams_list"));
        }

        [UnityTest]
        public IEnumerator ARefreshLandingAfterSignOut_DoesNotBringTheSessionBack()
        {
            Account(TwoTeams);
            var s = Resume("t1", "p1");
            // The read refreshes the access token; the rotated token is saved on a later editor update.
            Run.Sync(s.LoadTeams(autoPick: true));
            s.SignOut();
            for (var i = 0; i < 10; i++) yield return null;
            Assert.AreEqual("", s.Credentials.Load().Method, "Sign out stays signed out");
        }

        [UnityTest]
        public IEnumerator ARotatedRefreshToken_IsSavedWithItsProviderAndApplication()
        {
            Account(TwoTeams);
            var s = Resume("t1", "p1");
            // The read refreshes the access token; the rotated token is saved on a later editor update.
            Run.Sync(s.LoadTeams(autoPick: true));
            var until = EditorApplication.timeSinceStartup + 10;
            while (s.Credentials.Load().Secret != "rt_next" && EditorApplication.timeSinceStartup < until) yield return null;
            var saved = s.Credentials.Load();
            Assert.AreEqual("rt_next", saved.Secret);
            Assert.AreEqual(OAuthApp.ProductionIssuer, Json.Str(saved.Info, "issuer"));
            Assert.AreEqual(OAuthApp.ProductionClientId, Json.Str(saved.Info, "client_id"));
            Assert.AreEqual("me@x.com", Json.Str(saved.Info, "email"));
        }

        static CredentialStore StoreOf(string folder)
        {
            var store = new CredentialStore(Path.Combine(folder, "credentials.json"), new MemorySecretStore());
            store.SaveRefreshToken("rt_saved", SavedInfo());
            return store;
        }

        [UnityTest]
        public IEnumerator ThePicker_ListsEveryTeam_AndTakesTheProjectClicked()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("The window tests need a graphics device (run without -nographics).");
            Account(("t1", new[] { "p1", "p2" }), ("t2", new string[0]));
            var s = Resume();
            Run.Sync(s.LoadTeams(autoPick: true));
            _window = ScenarioWindow.Open();
            var until = EditorApplication.timeSinceStartup + 10;
            while (_window.rootVisualElement.Q("project-p2") == null && EditorApplication.timeSinceStartup < until) yield return null;
            var labels = _window.rootVisualElement.Query<Label>().ToList().Select(l => l.text).ToList();
            Assert.Contains("TEAM T1", labels);
            Assert.Contains("TEAM T2", labels);
            Assert.IsTrue(labels.Any(t => t.StartsWith("No project in this team yet")), "a team without projects says so");
            var row = _window.rootVisualElement.Q("project-p2");
            Assert.IsNotNull(row);
            Assert.IsNotNull(_window.rootVisualElement.Q("project-p1"));
            var mouseUp = new Event { type = EventType.MouseUp, mousePosition = row.worldBound.center, button = 0, clickCount = 1 };
            using (var click = ClickEvent.GetPooled(mouseUp))
            {
                click.target = row;
                row.SendEvent(click);
            }
            Assert.IsTrue(s.Ready);
            Assert.AreEqual("t1", s.Client.TeamId);
            Assert.AreEqual("p2", s.Client.ProjectId);
        }

        [UnityTest]
        public IEnumerator ThePicker_SaysWhenTheAccountHasNoTeam()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("The window tests need a graphics device (run without -nographics).");
            Account();
            var s = Resume();
            Run.Sync(s.LoadTeams(autoPick: true));
            _window = ScenarioWindow.Open();
            var until = EditorApplication.timeSinceStartup + 10;
            bool Says() => _window.rootVisualElement.Query<Label>().ToList().Any(l => l.text.Contains("No team found"));
            while (!Says() && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.IsTrue(Says());
        }
    }
}
