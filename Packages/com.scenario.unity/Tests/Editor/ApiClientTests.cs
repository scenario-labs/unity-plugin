using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;

namespace Scenario.Editor.Tests
{
    public class ApiClientTests
    {
        const string Generate = "POST /generate/custom/{id}";

        FakeApi _server;
        ApiClient _client;

        [SetUp]
        public void SetUp()
        {
            _server = new FakeApi();
            _client = new ApiClient(_server, new ApiKeyAuth("key_abc123", "secret")) { SpendingDisabled = false };
        }

        [Test]
        public void EstimateReadsThePriceOfADryRun()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Fixture("estimate.json", 269);
            var price = Run.Sync(_client.Estimate("model_openai-gpt-image-2-5-flare", new JObject { ["prompt"] = "chest" }));
            Assert.IsTrue(price.Ok, price.Error?.Message);
            Assert.AreEqual(11, price.Value.Cu);
            Assert.AreEqual("true", (string)_server.Arguments[0]["dryRun"], "the API checks the literal true");
            Assert.AreEqual("model_openai-gpt-image-2-5-flare", (string)_server.Arguments[0]["id"]);
            Assert.AreEqual("chest", (string)_server.Arguments[0]["prompt"], "the parameters are the body");
        }

        [Test]
        public void BasicAuthAndHeadersAreSentAndRedirectsAreNotFollowed()
        {
            _server.Routes["GET /models/{id}"] = (args, i) => FakeApi.Json(new JObject { ["model"] = new JObject { ["id"] = "m" } });
            Run.Sync(_client.GetModel("m"));
            var call = _server.Requests.Last();
            Assert.AreEqual(ApiClient.DefaultBaseUrl + "/models/m", call.Url, "an API key is scoped by the key: no workspace in the query");
            StringAssert.StartsWith("Basic ", call.Headers["Authorization"]);
            Assert.AreEqual("application/json", call.Headers["Accept"]);
            StringAssert.StartsWith("ScenarioUnity/", call.Headers["User-Agent"]);
            Assert.IsFalse(call.FollowRedirects, "the credential never follows a redirect");
        }

        [Test]
        public void ARedirectOnAPaidCallIsAmbiguous()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Status(302);
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsTrue(result.Error.Ambiguous);
        }

        [Test]
        public void AnEstimateThatReturnsAJobRaisesTheAlarm()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Json(new JObject { ["creativeUnitsCost"] = 5, ["job"] = new JObject { ["jobId"] = "job_oops" } });
            var price = Run.Sync(_client.Estimate("m", new JObject()));
            Assert.IsFalse(price.Ok);
            Assert.AreEqual(ErrorKind.EstimateJob, price.Error.Kind);
            Assert.AreEqual("job_oops", price.Error.JobId);
        }

        [Test]
        public void APriceCheckErrorThatCarriesAJobRaisesTheAlarm()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Status(502, "{\"job\":{\"jobId\":\"job_oops\"}}");
            var price = Run.Sync(_client.Estimate("m", new JObject()));
            Assert.AreEqual(ErrorKind.EstimateJob, price.Error.Kind);
            Assert.AreEqual("job_oops", price.Error.JobId);
            Assert.AreEqual(1, _server.Count(Generate), "never retried");
        }

        [Test]
        public void ACanceledPaidCallIsAmbiguous()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Fail("canceled");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual(ErrorKind.Canceled, result.Error.Kind);
            Assert.IsTrue(result.Error.Ambiguous, "canceling after it left does not prove it did not run");
            _server.Routes["GET /jobs/{id}"] = (args, i) => FakeApi.Fail("canceled");
            Assert.IsFalse(Run.Sync(_client.GetJob("job_1")).Error.Ambiguous, "a read is never ambiguous");
        }

        [Test]
        public void APaidCallIsNeverRetried()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Status(503, "busy");
            var result = Run.Sync(_client.Submit("m", new JObject { ["prompt"] = "x" }));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, _server.Count(Generate));
            Assert.IsTrue(result.Error.Ambiguous, "a 5xx on a paid call may have run");
        }

        [Test]
        public void ATransportErrorOnAPaidCallIsAmbiguous()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Fail("timeout");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsTrue(result.Error.Ambiguous);
            Assert.AreEqual(1, _server.Count(Generate));
        }

        [Test]
        public void AClearRefusalOnAPaidCallIsNotAmbiguous()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Error(400, "Insufficient creative units");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual(ErrorKind.Quota, result.Error.Kind);
            Assert.IsFalse(result.Error.Ambiguous);
        }

        [Test]
        public void APlanLimitIsAQuotaAndIsNotRetried()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Error(429, "Upgrade to run this model", "PlanLimitReachedError");
            var price = Run.Sync(_client.Estimate("m", new JObject()));
            Assert.AreEqual(ErrorKind.Quota, price.Error.Kind);
            Assert.AreEqual(1, _server.Count(Generate));
        }

        [Test]
        public void ATaskIdRequirementIsExplained()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Status(400, "{\"reason\":\"Task id required\",\"details\":{\"reason\":\"task-id-required\"}}");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual(ErrorKind.Invalid, result.Error.Kind);
            StringAssert.Contains("task", result.Error.Message);
            Assert.IsFalse(result.Error.Ambiguous);
        }

        [Test]
        public void ModerationIsBlocked()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Error(400, "Your prompt was flagged by our content moderation");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual(ErrorKind.Moderated, result.Error.Kind);
        }

        [Test]
        public void ReadsAreRetriedWithBackoffAndRetryAfter()
        {
            _server.Routes["GET /jobs/{id}"] = (args, i) => i == 0 ? FakeApi.Status(429, "slow down", "2") : i == 1 ? FakeApi.Status(502) : FakeApi.Fixture("job.json");
            var job = Run.Sync(_client.GetJob("job_test00000000000000000001"));
            Assert.IsTrue(job.Ok, job.Error?.Message);
            Assert.AreEqual(3, _server.Count("GET /jobs/{id}"));
            Assert.AreEqual(2, _server.Delays[0]);
            Assert.AreEqual("success", (string)job.Value["status"]);
        }

        [Test]
        public void ARetryAfterLongerThanTheCapStops()
        {
            _server.Routes["GET /jobs/{id}"] = (args, i) => FakeApi.Status(429, "", "120");
            var job = Run.Sync(_client.GetJob("job_x"));
            Assert.AreEqual(ErrorKind.RateLimit, job.Error.Kind);
            Assert.AreEqual(1, _server.Count("GET /jobs/{id}"));
        }

        [Test]
        public void AJobIsFlattenedForTheLedger()
        {
            _server.Routes["GET /jobs/{id}"] = (args, i) => FakeApi.Fixture("job.json");
            var job = Run.Sync(_client.GetJob("job_test00000000000000000001")).Value;
            Assert.AreEqual("model_openai-gpt-image-2-5-sunburst", (string)job["modelId"], "from metadata.input");
            Assert.AreEqual(1, ((JArray)job["assetIds"]).Count, "from metadata");
            Assert.AreEqual(14, (int)job["cuCost"], "from billing");
            var failed = ApiClient.Flatten(new JObject { ["jobId"] = "j", ["metadata"] = new JObject { ["error"] = new JObject { ["message"] = "GPU lost" }, ["workflowId"] = "workflow_1" } });
            Assert.AreEqual("GPU lost", (string)failed["error"]);
            Assert.AreEqual("workflow_1", (string)failed["workflowId"]);
        }

        [Test]
        public void SpendingSwitchRefusesBeforeSending()
        {
            _client.SpendingDisabled = true;
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Error.NotSent);
            Assert.AreEqual(0, _server.Requests.Count);
            Assert.IsTrue(Run.Sync(_client.UploadStart("a.png", "image/png", "image", 10)).Error.NotSent, "uploads are refused too");
            // A price check and a cancel stay allowed: one is free, the other only lowers spend.
            _server.Routes[Generate] = (args, i) => FakeApi.Fixture("estimate.json", 269);
            Assert.IsTrue(Run.Sync(_client.Estimate("m", new JObject())).Ok);
            _server.Routes["POST /jobs/{id}/action"] = (args, i) => FakeApi.Json(new JObject { ["job"] = new JObject { ["jobId"] = "job_1" } });
            Assert.IsTrue(Run.Sync(_client.CancelJob("job_1")).Ok);
            Assert.AreEqual("cancel", (string)_server.Last("POST /jobs/{id}/action")["action"]);
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
        public void OAuthCallsCarryTheWorkspaceInTheQuery()
        {
            var auth = new StubAuth();
            _client.SetAuth(auth);
            var refused = Run.Sync(_client.GetModel("m"));
            Assert.AreEqual(ErrorKind.Local, refused.Error.Kind);
            Assert.IsTrue(refused.Error.NotSent);
            _client.SetScope("team_1", "proj_1");
            _server.Routes["GET /models/{id}"] = (args, i) => FakeApi.Json(new JObject { ["model"] = new JObject { ["id"] = "m" } });
            _server.Routes["GET /teams"] = (args, i) => FakeApi.Fixture("teams.json");
            Assert.IsTrue(Run.Sync(_client.GetModel("m")).Ok);
            Assert.AreEqual("team_1", (string)_server.Arguments.Last()["teamId"]);
            Assert.AreEqual("proj_1", (string)_server.Arguments.Last()["projectId"]);
            StringAssert.StartsWith("Bearer ", _server.Requests.Last().Headers["Authorization"]);
            Run.Sync(_client.ListTeams());
            Assert.IsNull(_server.Arguments.Last()["teamId"], "the teams list is unscoped: a stale project would be refused");
            Assert.IsNull(_server.Arguments.Last()["projectId"]);
        }

        [Test]
        public void AnAuthorizerRefusalRefreshesOnceAndResends()
        {
            var auth = new StubAuth { RefreshWorks = true };
            _client.SetAuth(auth);
            _client.SetScope("t", "p");
            // An expired OAuth token: 403 with the bare reason, before the request reaches Scenario.
            _server.Routes[Generate] = (args, i) => i == 0 ? FakeApi.Error(403, "Forbidden") : FakeApi.Json(new JObject { ["job"] = new JObject { ["jobId"] = "job_1" } });
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsTrue(result.Ok, result.Error?.Message);
            Assert.AreEqual(1, auth.Refreshes);
            Assert.AreEqual(2, _server.Count(Generate), "resent once: the first never reached Scenario");
            Assert.AreEqual("Bearer t1", _server.Requests.Last().Headers["Authorization"]);
        }

        [Test]
        public void AnAuthorizerRefusalWithoutARefreshIsAnAuthError()
        {
            var auth = new StubAuth { RefreshWorks = false };
            _client.SetAuth(auth);
            _client.SetScope("t", "p");
            _server.Routes["GET /teams"] = (args, i) => FakeApi.Error(403, "Forbidden");
            var result = Run.Sync(_client.ListTeams());
            Assert.AreEqual(ErrorKind.Auth, result.Error.Kind, "the service then asks to sign in again");
            Assert.AreEqual(1, _server.Count("GET /teams"));
        }

        [Test]
        public void AScenarioRefusalIsNotMistakenForAnExpiredToken()
        {
            var auth = new StubAuth { RefreshWorks = true };
            _client.SetAuth(auth);
            _client.SetScope("t", "p");
            _server.Routes[Generate] = (args, i) => FakeApi.Error(403, "This model is not available on your plan", "ModelAccessRestrictedError");
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual(ErrorKind.Forbidden, result.Error.Kind);
            Assert.AreEqual(0, auth.Refreshes);
            Assert.AreEqual(1, _server.Count(Generate));
        }

        [Test]
        public void SubmitReadsTheJobId()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Json(new JObject { ["job"] = new JObject { ["jobId"] = "job_1", ["status"] = "queued" } });
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.AreEqual("job_1", result.Value.JobId);
            Assert.AreEqual("queued", result.Value.Status);
            Assert.IsNull(_server.Arguments[0]["dryRun"]);
        }

        [Test]
        public void ASubmitWithoutAJobIdIsAmbiguous()
        {
            _server.Routes[Generate] = (args, i) => FakeApi.Json(new JObject { ["status"] = "ok" });
            var result = Run.Sync(_client.Submit("m", new JObject()));
            Assert.IsTrue(result.Error.Ambiguous);
        }

        [Test]
        public void WorkflowsArePricedAndRunWithPut()
        {
            _server.Routes["PUT /workflows/{id}/run"] = (args, i) => args["dryRun"] != null
                ? FakeApi.Json(new JObject { ["creativeUnitsCost"] = 42 }, 269)
                : FakeApi.Json(new JObject { ["job"] = new JObject { ["jobId"] = "job_wf" } });
            Assert.AreEqual(42, Run.Sync(_client.EstimateWorkflow("workflow_1", new JObject { ["image1"] = "asset_1" })).Value.Cu);
            Assert.AreEqual("job_wf", Run.Sync(_client.SubmitWorkflow("workflow_1", new JObject())).Value.JobId);
            Assert.AreEqual("asset_1", (string)_server.Arguments[0]["image1"]);
        }

        [Test]
        public void ListJobsAsksForAPageSizeAndFlattens()
        {
            _server.Routes["GET /jobs"] = (args, i) => FakeApi.Fixture("jobs.json");
            var jobs = Run.Sync(_client.ListJobs(5));
            Assert.IsTrue(jobs.Ok);
            Assert.Greater(jobs.Value.Count, 0);
            Assert.AreEqual("5", (string)_server.Arguments[0]["pageSize"]);
            Assert.IsNotNull(jobs.Value[0]["modelId"]);
        }

        [Test]
        public void DownloadAsksForTheFormatInLowerCase()
        {
            _server.Routes["POST /assets/{id}/download"] = (args, i) => FakeApi.Json(new JObject { ["url"] = "https://cdn.cloud.scenario.com/x.glb" });
            Assert.AreEqual("https://cdn.cloud.scenario.com/x.glb", Run.Sync(_client.DownloadUrl("asset_1", "GLB")).Value);
            Assert.AreEqual("glb", (string)_server.Arguments[0]["targetFormat"]);
            Run.Sync(_client.DownloadUrl("asset_1"));
            Assert.IsNull(_server.Arguments[1]["targetFormat"], "no format keeps the original");
        }

        [Test]
        public void SchemaIsTheModelsInputs()
        {
            _server.Routes["GET /models/{id}"] = (args, i) => FakeApi.Fixture("schema_flare.json");
            var schema = Run.Sync(_client.GetSchema("model_openai-gpt-image-2-5-flare")).Value;
            Assert.AreEqual("model_openai-gpt-image-2-5-flare", (string)schema["model_id"]);
            Assert.Greater(((JArray)schema["inputs"]).Count, 0);
        }

        [Test]
        public void AssetSearchIsLimitedToTheProject()
        {
            _client.SetScope("team_1", "proj_1");
            _server.Routes["POST /search/assets"] = (args, i) => FakeApi.Json(new JObject { ["hits"] = new JArray(), ["estimatedTotalHits"] = 0 });
            Run.Sync(_client.SearchAssets("", "kind IN [\"image\"]", 20, 40));
            var body = _server.Arguments[0];
            Assert.AreEqual("(kind IN [\"image\"]) AND ownerId = \"proj_1\"", (string)body["filter"]);
            Assert.AreEqual(false, (bool)body["public"], "sent explicitly: the API picks the index from it");
            Assert.AreEqual(40, (int)body["offset"]);
            Assert.AreEqual("createdAt:desc", (string)body["sortBy"][0], "an empty query lists newest first");
            Run.Sync(_client.SearchAssets("chest", "", 20, 0));
            Assert.AreEqual("ownerId = \"proj_1\"", (string)_server.Arguments[1]["filter"]);
            Assert.IsNull(_server.Arguments[1]["sortBy"], "a query ranks by relevance");
        }

        [Test]
        public void RecommendationsBecomeARankedList()
        {
            _server.Routes["GET /recommendations/models"] = (args, i) => FakeApi.Fixture("recommendations_3d.json");
            var result = Run.Sync(_client.Recommend("txt23d", 3)).Value;
            var ranked = (JArray)result["ranked"];
            Assert.AreEqual(3, ranked.Count);
            Assert.AreEqual(1, (int)ranked[0]["rank"]);
            StringAssert.StartsWith("model_", (string)ranked[0]["model_id"]);
            Assert.AreEqual("txt23d", (string)_server.Arguments[0]["capabilities"]);
            Assert.AreEqual("public", (string)_server.Arguments[0]["privacy"]);
        }

        [Test]
        public void PromptSparkIsPricedThenSentOnce()
        {
            _server.Routes["POST /generate/prompt"] = (args, i) => args["dryRun"] != null
                ? FakeApi.Json(new JObject { ["creativeUnitsCost"] = 2 }, 269)
                : FakeApi.Status(504);
            Assert.IsTrue(Run.Sync(_client.PromptSpark("m", "", dryRun: true)).Ok);
            var paid = Run.Sync(_client.PromptSpark("m", "a knight"));
            Assert.IsTrue(paid.Error.Ambiguous);
            Assert.AreEqual(2, _server.Count("POST /generate/prompt"));
            Assert.AreEqual(ApiClient.PromptSparkMode, (string)_server.Arguments[1]["mode"]);
            Assert.AreEqual("m", (string)_server.Arguments[1]["modelId"]);
        }

        [Test]
        public void AnUploadStartsCompletesAndWaitsForTheAsset()
        {
            _server.Routes["POST /uploads"] = (args, i) => FakeApi.Json(new JObject
            {
                ["upload"] = new JObject
                {
                    ["id"] = "upload_1",
                    ["parts"] = new JArray(new JObject { ["number"] = 1, ["url"] = "https://bucket.s3.amazonaws.com/1" }, new JObject { ["number"] = 2, ["url"] = "https://bucket.s3.amazonaws.com/2" }),
                },
            });
            // The first complete races an earlier one: the poll decides.
            _server.Routes["POST /uploads/{id}/action"] = (args, i) => FakeApi.Error(400, "Unable to complete upload with status: complete");
            _server.Routes["GET /uploads/{id}"] = (args, i) => FakeApi.Json(new JObject
            {
                ["upload"] = i == 0 ? new JObject { ["status"] = "validating" } : new JObject { ["status"] = "imported", ["entityId"] = "asset_up" },
            });
            var start = Run.Sync(_client.UploadStart("My Shot (1).PNG", "image/png", "image", 7_000_000)).Value;
            var sent = _server.Last("POST /uploads");
            Assert.AreEqual("my-shot-1.png", (string)sent["fileName"]);
            Assert.AreEqual(2, (int)sent["parts"], "every part but the last is 6 MB");
            Assert.AreEqual("upload_1", (string)start["upload_id"]);
            Assert.AreEqual(ApiClient.UploadPartSize, (long)start["part_size"]);
            Assert.AreEqual("https://bucket.s3.amazonaws.com/2", (string)start["parts"][1]["upload_url"]);
            var done = Run.Sync(_client.UploadComplete("upload_1"));
            Assert.IsTrue(done.Ok, done.Error?.Message);
            Assert.AreEqual("asset_up", (string)done.Value["asset_id"]);
            Assert.AreEqual("complete", (string)_server.Last("POST /uploads/{id}/action")["action"]);
            Assert.AreEqual(2, _server.Count("GET /uploads/{id}"));
        }

        [Test]
        public void AFailedUploadSaysWhy()
        {
            _server.Routes["POST /uploads/{id}/action"] = (args, i) => FakeApi.Json(new JObject { ["upload"] = new JObject { ["status"] = "complete" } });
            _server.Routes["GET /uploads/{id}"] = (args, i) => FakeApi.Json(new JObject { ["upload"] = new JObject { ["status"] = "failed", ["errorMessage"] = "unsupported file" } });
            var done = Run.Sync(_client.UploadComplete("upload_1"));
            StringAssert.Contains("unsupported file", done.Error.Message);
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

        [Test]
        public void BaseUrlIsTheApiOrAnHttpsOverride()
        {
            Assert.AreEqual(ApiClient.DefaultBaseUrl, ApiClient.BaseUrlFrom(_ => null).Value);
            Assert.AreEqual(ApiClient.DefaultBaseUrl, ApiClient.BaseUrlFrom(_ => "  ").Value);
            var dev = ApiClient.BaseUrlFrom(name => name == ApiClient.BaseUrlVariable ? " https://api.example.test/v1/ " : null);
            Assert.AreEqual("https://api.example.test/v1", dev.Value);
            var plain = ApiClient.BaseUrlFrom(name => name == ApiClient.BaseUrlVariable ? "http://api.example.test/v1" : null);
            Assert.IsFalse(plain.Ok, "a bearer token never goes over plain HTTP");
            StringAssert.Contains(ApiClient.BaseUrlVariable, plain.Error.Message);
        }

        [Test]
        public void UploadFileNamesAreSafeForPresignedUrls()
        {
            Assert.AreEqual("hero-sword.glb", ApiClient.UploadFileName("Hero Sword.GLB"));
            Assert.AreEqual("file.png", ApiClient.UploadFileName("(!).png"));
        }
    }
}
