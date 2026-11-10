using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;
using UnityEditor;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// The service on a scripted Scenario: sign-in, workflow pricing and runs, Prompt
    /// Spark through the ledger. Nothing leaves the machine; user settings are restored.
    /// </summary>
    public class ServiceTests
    {
        FakeMcp _server;
        ScenarioService _s;
        string _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);
            _server = new FakeMcp();
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Fixture("teams.sse.txt");
            _server.Tools["models_list"] = (args, i) => FakeMcp.Result(new JObject { ["models"] = new JArray() });
            _s = ScenarioService.UseForTests(_server, TempDir.Make("service"));
        }

        [TearDown]
        public void TearDown()
        {
            ScenarioService.ResetForTests();
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
        }

        void SignIn()
        {
            Assert.IsTrue(Run.Sync(_s.SignInWithApiKey("key_test", "secret_test")), _s.SignInError);
        }

        [Test]
        public void ApiKeySignIn_ChecksTheKey_ThenShowsItsProject()
        {
            SignIn();
            Assert.IsTrue(_s.Ready);
            Assert.AreEqual(1, _server.Count("teams_list"));
            var project = Fixtures.Structured("teams.sse.txt")["teams"][0]["projects"][0];
            Assert.AreEqual((string)project["name"], ScenarioUserSettings.instance.ProjectName);
            Assert.AreEqual("apikey", _s.Credentials.Load().Method);
        }

        [Test]
        public void RejectedKey_StaysSignedOut_AndSavesNothing()
        {
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Status(401, "{\"error\":\"invalid key\"}");
            Assert.IsFalse(Run.Sync(_s.SignInWithApiKey("key_bad", "secret_bad")));
            Assert.IsFalse(_s.IsSignedIn);
            StringAssert.Contains("did not accept", _s.SignInError);
            Assert.AreEqual("", _s.Credentials.Load().Method);
        }

        [Test]
        public void WorkflowPrice_UsesWorkflowDryRun_AndItsOwnFingerprint()
        {
            SignIn();
            _server.Tools["workflow_run"] = (args, i) => FakeMcp.Result(new JObject { ["dry_run"] = true, ["creativeUnitsCost"] = 42 });
            var inputs = new JObject { ["prompt"] = "a knight" };
            var request = new GenerateRequest { WorkflowId = "wf_1", Payload = inputs };
            var quote = Run.Sync(_s.Estimate(request));
            Assert.IsTrue(quote.Ok, quote.Ok ? "" : quote.Error.Message);
            Assert.AreEqual(42, quote.Value.Cu);
            var args = _server.ToolArguments[_server.ToolCalls.LastIndexOf("workflow_run")];
            Assert.AreEqual(true, (bool)args["dry_run"]);
            Assert.AreEqual("wf_1", (string)args["workflow_id"]);
            Assert.AreNotEqual(_s.Fingerprint(new GenerateRequest { ModelId = "wf_1", Payload = inputs }), _s.Fingerprint(request));
            Assert.AreEqual(0, _server.Count("model_run"));
        }

        [Test]
        public void WorkflowRun_IsSentOnce_WithoutDryRun_AndRecordsTheWorkflow()
        {
            SignIn();
            _server.Tools["workflow_run"] = (args, i) => args["dry_run"] != null
                ? FakeMcp.Result(new JObject { ["dry_run"] = true, ["creativeUnitsCost"] = 42 })
                : FakeMcp.Result(new JObject { ["job"] = new JObject { ["jobId"] = "job_wf", ["status"] = "queued" } });
            var request = new GenerateRequest { WorkflowId = "wf_1", Payload = new JObject { ["prompt"] = "a knight" }, Place = false };
            Assert.IsTrue(Run.Sync(_s.Estimate(request)).Ok);
            var result = Run.Sync(_s.Generate(request));
            Assert.IsTrue(result.Ok, result.Ok ? "" : result.Error.Message);
            Assert.AreEqual(2, _server.Count("workflow_run"));
            var sent = _server.ToolArguments[_server.ToolCalls.LastIndexOf("workflow_run")];
            Assert.IsNull(sent["dry_run"]);
            Assert.AreEqual("job_wf", result.Value.JobId);
            Assert.AreEqual("wf_1", result.Value.WorkflowId);
            Assert.AreEqual(42, result.Value.QuotedCu);
        }

        [Test]
        public void Generate_WithoutAFreshQuote_SendsNothing()
        {
            SignIn();
            var result = Run.Sync(_s.Generate(new GenerateRequest { ModelId = "model_a", Payload = new JObject { ["prompt"] = "x" } }));
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Error.NotSent);
            Assert.AreEqual(0, _server.Count("model_run"));
            Assert.AreEqual(0, _s.Ledger.Jobs.Count);
        }

        [Test]
        public void Generate_WithSpendingOff_SendsNothing()
        {
            SignIn();
            _server.Tools["model_run"] = (args, i) => FakeMcp.Fixture("estimate.sse.txt");
            var request = new GenerateRequest { ModelId = "model_openai-gpt-image-2-5-flare", Payload = new JObject { ["prompt"] = "x" } };
            Assert.IsTrue(Run.Sync(_s.Estimate(request)).Ok);
            _s.Client.SpendingDisabled = true;
            var result = Run.Sync(_s.Generate(request));
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Error.NotSent);
            Assert.AreEqual(1, _server.Count("model_run"), "only the free price check");
            StringAssert.Contains("Spending", _s.BlockReason(request));
        }

        [Test]
        public void ImprovePrompt_IsPricedFirst_AndLandsInTheLedgerAsCharged()
        {
            SignIn();
            _server.Tools["prompt_spark"] = (args, i) => args["dry_run"] != null
                ? FakeMcp.Result(new JObject { ["dry_run"] = true, ["creativeUnitsCost"] = 2.75 })
                : FakeMcp.Result(new JObject { ["recommended_prompt"] = "A better knight", ["creativeUnitsCost"] = 2.75 });
            var result = Run.Sync(_s.ImprovePrompt("model_a", "Model A", "a knight"));
            Assert.IsTrue(result.Ok, result.Ok ? "" : result.Error.Message);
            Assert.AreEqual("A better knight", result.Value);
            Assert.AreEqual(2, _server.Count("prompt_spark"));
            var row = _s.Ledger.Jobs.Single();
            Assert.AreEqual("prompt", row.OutputType);
            Assert.AreEqual(JobState.Success, row.State);
            Assert.AreEqual(2.75, row.ChargedCu);
            Assert.AreEqual(2.75, _s.SpentToday(), 1e-9);
        }

        [Test]
        public void ImprovePrompt_LostReply_IsUnknown_AndNotRetried()
        {
            SignIn();
            _server.Tools["prompt_spark"] = (args, i) => args["dry_run"] != null
                ? FakeMcp.Result(new JObject { ["dry_run"] = true, ["creativeUnitsCost"] = 2.75 })
                : FakeMcp.Fail("timeout");
            var result = Run.Sync(_s.ImprovePrompt("model_a", "Model A", "a knight"));
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Error.Ambiguous);
            Assert.AreEqual(2, _server.Count("prompt_spark"), "one price check, one paid call, no retry");
            Assert.IsTrue(_s.Ledger.Jobs.Single().IsUnresolved);
            var again = Run.Sync(_s.ImprovePrompt("model_a", "Model A", "a knight"));
            Assert.IsFalse(again.Ok);
            Assert.IsTrue(again.Error.NotSent, "the same unresolved request is blocked");
            Assert.AreEqual(2, _server.Count("prompt_spark"));
        }

        [Test]
        public void PriceCheckThatReturnsAJob_PausesPriceChecks()
        {
            SignIn();
            _server.Tools["model_run"] = (args, i) => FakeMcp.Result(new JObject { ["creativeUnitsCost"] = 5, ["job"] = new JObject { ["jobId"] = "job_oops" } });
            var first = Run.Sync(_s.Estimate("model_a", new JObject { ["prompt"] = "x" }));
            Assert.IsFalse(first.Ok);
            Assert.IsTrue(_s.EstimateAlarm);
            var second = Run.Sync(_s.Estimate("model_a", new JObject { ["prompt"] = "y" }));
            Assert.IsFalse(second.Ok);
            Assert.AreEqual(1, _server.Count("model_run"), "no further price check is sent");
        }
    }
}
