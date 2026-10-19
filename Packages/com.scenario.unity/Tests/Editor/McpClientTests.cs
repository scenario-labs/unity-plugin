using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;

namespace Scenario.Editor.Tests
{
    public class McpClientTests
    {
        FakeMcp _server;
        McpClient _client;

        [SetUp]
        public void SetUp()
        {
            _server = new FakeMcp();
            _client = new McpClient(_server, new ApiKeyAuth("key_abc123", "secret")) { SpendingDisabled = false };
        }

        [Test]
        public void EstimateReadsThePriceFromARecordedReply()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Fixture("estimate.sse.txt");
            var price = Run.Sync(_client.Estimate("model_openai-gpt-image-2-5-flare", new JObject { ["prompt"] = "chest" }));
            Assert.IsTrue(price.Ok, price.Error?.Message);
            Assert.AreEqual(11, price.Value.Cu);
            Assert.IsTrue((bool)_server.ToolArguments[0]["dry_run"]);
            Assert.AreEqual(1, _server.Initializations);
        }

        [Test]
        public void SessionHeaderAndBasicAuthAreSent()
        {
            _server.Tools["model_get"] = (args, i) => FakeMcp.Result(new JObject { ["id"] = "m" });
            Run.Sync(_client.GetModel("m"));
            var call = _server.Requests.Last();
            Assert.AreEqual("sess_1", call.Headers["Mcp-Session-Id"]);
            StringAssert.StartsWith("Basic ", call.Headers["Authorization"]);
            Assert.AreEqual(McpClient.ProtocolVersion, call.Headers["MCP-Protocol-Version"]);
            StringAssert.StartsWith("ScenarioUnity/", call.Headers["User-Agent"]);
        }

        [Test]
        public void AnEstimateThatReturnsAJobRaisesTheAlarm()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Result(new JObject { ["creativeUnitsCost"] = 5, ["job"] = new JObject { ["jobId"] = "job_oops" } });
            var price = Run.Sync(_client.Estimate("m", new JObject()));
            Assert.IsFalse(price.Ok);
            Assert.AreEqual(ErrorKind.EstimateJob, price.Error.Kind);
            Assert.AreEqual("job_oops", price.Error.JobId);
        }

        [Test]
        public void APaidCallIsNeverRetried()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Status(503, "busy");
            var result = Run.Sync(_client.Submit("m", new JObject { ["prompt"] = "x" }));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, _server.Count("model_run"));
            Assert.IsTrue(result.Error.Ambiguous, "a 5xx on a paid call may have run");
        }

        [Test]
        public void ATransportErrorOnAPaidCallIsAmbiguous()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Fail("timeout");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsTrue(result.Error.Ambiguous);
            Assert.AreEqual(1, _server.Count("model_run"));
        }

        [Test]
        public void AClearRefusalOnAPaidCallIsNotAmbiguous()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Error("Insufficient creative units");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual(ErrorKind.Quota, result.Error.Kind);
            Assert.IsFalse(result.Error.Ambiguous);
        }

        [Test]
        public void ModerationIsBlocked()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Error("Your prompt was flagged by our content moderation");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual(ErrorKind.Moderated, result.Error.Kind);
        }

        [Test]
        public void ReadsAreRetriedWithBackoffAndRetryAfter()
        {
            _server.Tools["job_get"] = (args, i) => i == 0 ? FakeMcp.Status(429, "slow down", "2") : i == 1 ? FakeMcp.Status(502) : FakeMcp.Fixture("job.sse.txt");
            var job = Run.Sync(_client.GetJob("job_test00000000000000000001"));
            Assert.IsTrue(job.Ok, job.Error?.Message);
            Assert.AreEqual(3, _server.Count("job_get"));
            Assert.AreEqual(2, _server.Delays[0]);
            Assert.AreEqual("success", (string)job.Value["status"]);
        }

        [Test]
        public void ARetryAfterLongerThanTheCapStops()
        {
            _server.Tools["job_get"] = (args, i) => FakeMcp.Status(429, "", "120");
            var job = Run.Sync(_client.GetJob("job_x"));
            Assert.AreEqual(ErrorKind.RateLimit, job.Error.Kind);
            Assert.AreEqual(1, _server.Count("job_get"));
        }

        [Test]
        public void SpendingSwitchRefusesBeforeSending()
        {
            _client.SpendingDisabled = true;
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Error.NotSent);
            Assert.AreEqual(0, _server.Requests.Count);
            // A price check stays allowed: it is free.
            _server.Tools["model_run"] = (args, i) => FakeMcp.Fixture("estimate.sse.txt");
            Assert.IsTrue(Run.Sync(_client.Estimate("m", new JObject())).Ok);
        }

        [Test]
        public void NoCredentialsMeansNothingIsSent()
        {
            _client.SetAuth(new NoAuth());
            var result = Run.Sync(_client.GetModel("m"));
            Assert.AreEqual(ErrorKind.Auth, result.Error.Kind);
            Assert.AreEqual(0, _server.Requests.Count);
        }

        [Test]
        public void OAuthCallsCarryTheWorkspace()
        {
            var auth = new StubAuth();
            _client.SetAuth(auth);
            var refused = Run.Sync(_client.GetModel("m"));
            Assert.AreEqual(ErrorKind.Local, refused.Error.Kind);
            Assert.IsTrue(refused.Error.NotSent);
            _client.SetScope("team_1", "proj_1");
            _server.Tools["model_get"] = (args, i) => FakeMcp.Result(new JObject { ["id"] = "m" });
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Fixture("teams.sse.txt");
            Assert.IsTrue(Run.Sync(_client.GetModel("m")).Ok);
            Assert.AreEqual("team_1", (string)_server.ToolArguments.Last()["team_id"]);
            Assert.AreEqual("proj_1", (string)_server.ToolArguments.Last()["project_id"]);
            Run.Sync(_client.ListTeams());
            Assert.IsNull(_server.ToolArguments.Last()["team_id"], "teams_list is unscoped");
        }

        [Test]
        public void A401RefreshesOnceAndResends()
        {
            var auth = new StubAuth { RefreshWorks = true };
            _client.SetAuth(auth);
            _client.SetScope("t", "p");
            _server.Tools["model_get"] = (args, i) => i == 0 ? FakeMcp.Status(401, "{\"error\":\"invalid_token\"}") : FakeMcp.Result(new JObject { ["id"] = "m" });
            var result = Run.Sync(_client.GetModel("m"));
            Assert.IsTrue(result.Ok, result.Error?.Message);
            Assert.AreEqual(1, auth.Refreshes);
            Assert.AreEqual(2, _server.Count("model_get"));
            Assert.AreEqual(2, _server.Initializations, "the session restarts with the new token");
        }

        [Test]
        public void AnExpiredSessionReinitializesAndResends()
        {
            _server.Tools["model_get"] = (args, i) => i == 0 ? FakeMcp.Status(404, "Session not found") : FakeMcp.Result(new JObject { ["id"] = "m" });
            Run.Sync(_client.GetModel("warm")); // first call: 404 with a session id
            Assert.AreEqual(2, _server.Initializations);
            Assert.AreEqual(2, _server.Count("model_get"));
        }

        [Test]
        public void SubmitReadsTheJobId()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Result(new JObject { ["job"] = new JObject { ["jobId"] = "job_1", ["status"] = "queued" } });
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual("job_1", result.Value.JobId);
            Assert.IsFalse((bool)_server.ToolArguments[0]["wait"]);
            Assert.IsNull(_server.ToolArguments[0]["dry_run"]);
        }

        [Test]
        public void ASubmitWithoutAJobIdIsAmbiguous()
        {
            _server.Tools["model_run"] = (args, i) => FakeMcp.Result(new JObject { ["status"] = "ok" });
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsTrue(result.Error.Ambiguous);
        }

        [Test]
        public void SubmissionClassification()
        {
            Assert.IsTrue(McpClient.IsSubmission("model_run", new JObject()));
            Assert.IsFalse(McpClient.IsSubmission("model_run", new JObject { ["dry_run"] = true }));
            Assert.IsTrue(McpClient.IsSubmission("upload_asset", new JObject { ["dry_run"] = true }));
            Assert.IsFalse(McpClient.IsSubmission("model_get", new JObject()));
            Assert.IsTrue(McpClient.IsRetryable("model_run", new JObject { ["dry_run"] = "true" }));
            Assert.IsFalse(McpClient.IsRetryable("model_run", new JObject()));
        }

        [Test]
        public void ListJobsReadsBillingCost()
        {
            _server.Tools["jobs_list"] = (args, i) => FakeMcp.Fixture("jobs.sse.txt");
            var jobs = Run.Sync(_client.ListJobs(5));
            Assert.IsTrue(jobs.Ok);
            Assert.Greater(jobs.Value.Count, 0);
            Assert.AreEqual(5, (int)_server.ToolArguments[0]["limit"]);
        }

        sealed class StubAuth : IAuthProvider
        {
            public bool RefreshWorks;
            public int Refreshes;
            public AuthMethod Method => AuthMethod.OAuth;
            public bool HasCredentials => true;
            public bool NeedsScope => true;
            public string Label => "test";
            public Task<string> AuthorizationHeaderAsync() => Task.FromResult("Bearer t" + Refreshes);
            public Task<bool> RefreshAsync()
            {
                Refreshes++;
                return Task.FromResult(RefreshWorks);
            }
        }
    }
}
