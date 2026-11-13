using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>
    /// Scenario's REST API (https://api.cloud.scenario.com/v1), called directly.
    ///
    /// Money rules (cross-engine contract, section 3): a paid call is sent once and
    /// never retried; a transport failure on a paid call is "ambiguous" (Scenario may
    /// have started the job); a price check that comes back with a job id is an alarm.
    /// Only reads and price checks are retried. A refusal by the API's authorizer (401,
    /// or 403 with the bare reason "Forbidden": an expired OAuth token) happens before
    /// the request reaches Scenario, so it is resent once after an OAuth refresh.
    /// </summary>
    public sealed class ApiClient
    {
        public const string DefaultBaseUrl = "https://api.cloud.scenario.com/v1";
        /// <summary>Developers: the API of a development instance, next to SCENARIO_OAUTH_ISSUER and SCENARIO_OAUTH_CLIENT_ID.</summary>
        public const string BaseUrlVariable = "SCENARIO_API_URL";
        public const string Version = "0.2.0"; // x-release-please-version
        /// <summary>Prompt Spark's mode for one prompt rewritten for one model.</summary>
        public const string PromptSparkMode = "contextual-v2";
        const int ReadAttempts = 3;
        const double MaxRetryAfter = 30;
        const double DefaultTimeout = 60;
        /// <summary>S3 multipart: every part but the last is at least 5 MiB.</summary>
        public const long UploadPartSize = 6_000_000;
        const double UploadPollSeconds = 2;
        const double UploadWaitSeconds = 90;

        /// <summary>How a call is treated: retried, refused when spending is off, ambiguous on failure.</summary>
        public enum Kind
        {
            /// <summary>Free and idempotent: retried.</summary>
            Read,
            /// <summary>A free dry run: retried, and alarmed if it returns a job.</summary>
            Price,
            /// <summary>Spends or creates something: sent once, refused when spending is off.</summary>
            Paid,
            /// <summary>Cancels a job: sent once, allowed when spending is off (it only lowers spend).</summary>
            Cancel,
        }

        /// <summary>The production API, or an HTTPS URL in SCENARIO_API_URL.</summary>
        public static Result<string> BaseUrlFrom(Func<string, string> environment)
        {
            var url = (environment(BaseUrlVariable) ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) return Result<string>.Success(DefaultBaseUrl);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Result<string>.Fail(ErrorKind.Local, $"{BaseUrlVariable} must be an HTTPS URL; using {DefaultBaseUrl}.");
            return Result<string>.Success(url);
        }

        public string BaseUrl = DefaultBaseUrl;
        public ITransport Transport;
        public IAuthProvider Auth = new NoAuth();
        public string TeamId = "";
        public string ProjectId = "";
        /// <summary>SCENARIO_NO_SPEND=1 refuses every paid call before any request is made.</summary>
        public bool SpendingDisabled = Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") == "1";
        public string UserAgent = DefaultUserAgent();

        static readonly System.Random Jitter = new System.Random();

        public ApiClient(ITransport transport, IAuthProvider auth = null)
        {
            Transport = transport;
            if (auth != null) Auth = auth;
        }

        public bool HasCredentials => Auth != null && Auth.HasCredentials;

        /// <summary>True when OAuth is used and no project is picked yet.</summary>
        public bool NeedsWorkspace => Auth != null && Auth.NeedsScope && (string.IsNullOrEmpty(TeamId) || string.IsNullOrEmpty(ProjectId));

        public void SetAuth(IAuthProvider auth) => Auth = auth ?? new NoAuth();

        public void SetScope(string teamId, string projectId)
        {
            TeamId = teamId ?? "";
            ProjectId = projectId ?? "";
        }

        static string DefaultUserAgent()
        {
            string unity;
            try { unity = UnityEngine.Application.unityVersion; }
            catch (Exception) { unity = "unknown"; }
            return $"ScenarioUnity/{Version} (Unity {unity}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription})";
        }

        /// <summary>The job id carried by a reply, or "" (a price check must carry none).</summary>
        public static string CreatedJobId(JToken value)
        {
            if (!(value is JObject obj)) return "";
            foreach (var key in new[] { "jobId", "job_id" })
            {
                var direct = Json.Str(obj, key);
                if (direct.Length > 0) return direct;
            }
            if (obj["job"] is JObject inner)
            {
                var id = Json.Str(inner, "jobId");
                if (id.Length == 0) id = Json.Str(inner, "id");
                if (id.Length > 0) return id;
            }
            return "";
        }

        /// <summary>
        /// A job with the fields the ledger reads at the top: assetIds, modelId, workflowId,
        /// cuCost and error, which the API keeps under metadata and billing.
        /// </summary>
        public static JObject Flatten(JObject job)
        {
            var row = (JObject)job.DeepClone();
            var metadata = job["metadata"] as JObject;
            if (row["assetIds"] == null && metadata?["assetIds"] is JArray assets) row["assetIds"] = assets.DeepClone();
            if (row["modelId"] == null && metadata?["input"] is JObject input && Json.Str(input, "modelId").Length > 0)
                row["modelId"] = Json.Str(input, "modelId");
            if (row["workflowId"] == null && metadata != null && Json.Str(metadata, "workflowId").Length > 0)
                row["workflowId"] = Json.Str(metadata, "workflowId");
            if (row["cuCost"] == null && job["billing"] is JObject billing && billing["cuCost"] != null)
                row["cuCost"] = billing["cuCost"].DeepClone();
            if (row["error"] == null && metadata?["error"] != null)
            {
                var error = metadata["error"];
                row["error"] = error.Type == JTokenType.String ? (string)error
                    : error is JObject detail ? Json.Str(detail, "message", detail.ToString(Formatting.None)) : error.ToString(Formatting.None);
            }
            return row;
        }

        // --- Calls ------------------------------------------------------------

        public sealed class Price
        {
            public double Cu;
            public double Discount;
        }

        /// <summary>Free price check for a model run.</summary>
        public async Task<Result<Price>> Estimate(string modelId, JObject parameters, CancellationToken cancel = default)
            => PriceFrom(await Send(Kind.Price, "POST", "/generate/custom/" + Esc(modelId), DryRun(), parameters ?? new JObject(), DefaultTimeout, cancel));

        /// <summary>Free price check for a workflow run.</summary>
        public async Task<Result<Price>> EstimateWorkflow(string workflowId, JObject inputs, CancellationToken cancel = default)
            => PriceFrom(await Send(Kind.Price, "PUT", "/workflows/" + Esc(workflowId) + "/run", DryRun(), inputs ?? new JObject(), DefaultTimeout, cancel));

        static Dictionary<string, string> DryRun() => new Dictionary<string, string> { ["dryRun"] = "true" };

        static Result<Price> PriceFrom(Result<JObject> result)
        {
            if (!result.Ok) return result.Cast<Price>();
            var value = result.Value;
            if (value["creativeUnitsCost"] == null) return Result<Price>.Fail(ErrorKind.Protocol, "Scenario returned no price for this request.");
            return Result<Price>.Success(new Price
            {
                Cu = Json.Num(value, "creativeUnitsCost"),
                Discount = Json.Num(value, "creativeUnitsDiscount"),
            });
        }

        public sealed class Submitted
        {
            public string JobId;
            public string Status;
            public JObject Raw;
        }

        /// <summary>Paid. Sent exactly once.</summary>
        public async Task<Result<Submitted>> Submit(string modelId, JObject parameters, CancellationToken cancel = default)
            => SubmittedFrom(await Send(Kind.Paid, "POST", "/generate/custom/" + Esc(modelId), null, parameters ?? new JObject(), 120, cancel));

        /// <summary>Paid. Sent exactly once.</summary>
        public async Task<Result<Submitted>> SubmitWorkflow(string workflowId, JObject inputs, CancellationToken cancel = default)
            => SubmittedFrom(await Send(Kind.Paid, "PUT", "/workflows/" + Esc(workflowId) + "/run", null, inputs ?? new JObject(), 120, cancel));

        static Result<Submitted> SubmittedFrom(Result<JObject> result)
        {
            if (!result.Ok) return result.Cast<Submitted>();
            var jobId = CreatedJobId(result.Value);
            if (jobId.Length == 0)
                return Result<Submitted>.Fail(ErrorKind.Protocol,
                    "Scenario accepted the request but returned no job id. Check recent jobs before generating again.",
                    e => e.Ambiguous = true);
            var status = result.Value["job"] is JObject job ? Json.Str(job, "status") : "";
            return Result<Submitted>.Success(new Submitted { JobId = jobId, Status = status.Length > 0 ? status : "queued", Raw = result.Value });
        }

        /// <summary>One job, flattened (see <see cref="Flatten"/>).</summary>
        public async Task<Result<JObject>> GetJob(string jobId, CancellationToken cancel = default)
        {
            var job = Unwrap<JObject>(await Send(Kind.Read, "GET", "/jobs/" + Esc(jobId), null, null, DefaultTimeout, cancel), "job");
            return job.Ok ? Result<JObject>.Success(Flatten(job.Value)) : job;
        }

        /// <summary>The project's latest jobs, newest first, flattened.</summary>
        public async Task<Result<JArray>> ListJobs(int pageSize = 20, CancellationToken cancel = default)
        {
            var query = new Dictionary<string, string> { ["pageSize"] = Math.Max(1, Math.Min(100, pageSize)).ToString() };
            var jobs = Unwrap<JArray>(await Send(Kind.Read, "GET", "/jobs", query, null, DefaultTimeout, cancel), "jobs");
            return jobs.Ok ? Result<JArray>.Success(new JArray(jobs.Value.OfType<JObject>().Select(Flatten))) : jobs;
        }

        public Task<Result<JObject>> CancelJob(string jobId, CancellationToken cancel = default)
            => Send(Kind.Cancel, "POST", "/jobs/" + Esc(jobId) + "/action", null, new JObject { ["action"] = "cancel" }, DefaultTimeout, cancel);

        public async Task<Result<JObject>> GetAsset(string assetId, CancellationToken cancel = default)
            => Unwrap<JObject>(await Send(Kind.Read, "GET", "/assets/" + Esc(assetId), null, null, DefaultTimeout, cancel), "asset");

        /// <summary>A signed CDN URL. format converts 3D (glb, fbx, obj) and images; "" keeps the original.</summary>
        public async Task<Result<string>> DownloadUrl(string assetId, string format = "", CancellationToken cancel = default)
        {
            var body = new JObject();
            // The API compares formats case-sensitively.
            if (!string.IsNullOrWhiteSpace(format)) body["targetFormat"] = format.Trim().ToLowerInvariant();
            var result = await Send(Kind.Read, "POST", "/assets/" + Esc(assetId) + "/download", null, body, 120, cancel);
            if (!result.Ok) return result.Cast<string>();
            var url = Json.Str(result.Value, "url");
            return url.Length > 0 ? Result<string>.Success(url) : Result<string>.Fail(ErrorKind.Protocol, "Scenario's reply had no download URL.");
        }

        /// <summary>A model's inputs: { model_id, name, capabilities, inputs }.</summary>
        public async Task<Result<JObject>> GetSchema(string modelId, CancellationToken cancel = default)
        {
            var model = await GetModel(modelId, cancel);
            if (!model.Ok) return model;
            var value = model.Value["model"] as JObject ?? new JObject();
            return Result<JObject>.Success(new JObject
            {
                ["model_id"] = Json.Str(value, "id", modelId),
                ["name"] = Json.Str(value, "name"),
                ["capabilities"] = value["capabilities"]?.DeepClone() ?? new JArray(),
                ["inputs"] = value["inputs"]?.DeepClone() ?? new JArray(),
            });
        }

        public Task<Result<JObject>> GetModel(string modelId, CancellationToken cancel = default)
            => Send(Kind.Read, "GET", "/models/" + Esc(modelId), null, null, DefaultTimeout, cancel);

        /// <summary>
        /// Scenario's recommended public models for a capability, as { ranked: [{ rank,
        /// model_id, name, explanation }] }. The API ranks by capability, not by prompt.
        /// </summary>
        public async Task<Result<JObject>> Recommend(string capability, int limit = 5, CancellationToken cancel = default)
        {
            var query = new Dictionary<string, string> { ["privacy"] = "public", ["limit"] = Math.Max(1, Math.Min(30, limit)).ToString() };
            if (!string.IsNullOrEmpty(capability)) query["capabilities"] = capability;
            var result = await Send(Kind.Read, "GET", "/recommendations/models", query, null, DefaultTimeout, cancel);
            if (!result.Ok) return result;
            var ranked = new JArray();
            foreach (var model in (result.Value["models"] as JArray ?? new JArray()).OfType<JObject>())
                ranked.Add(new JObject
                {
                    ["rank"] = ranked.Count + 1,
                    ["model_id"] = Json.Str(model, "id"),
                    ["name"] = Json.Str(model, "name"),
                    ["explanation"] = Json.Str(model, "shortDescription"),
                });
            return Result<JObject>.Success(new JObject { ["capability"] = capability ?? "", ["ranked"] = ranked });
        }

        /// <summary>One page of models: filters privacy and status, as the API names them.</summary>
        public Task<Result<JObject>> ListModels(JObject filters, int limit, string pageToken, CancellationToken cancel = default)
        {
            var query = new Dictionary<string, string> { ["pageSize"] = limit.ToString() };
            foreach (var key in new[] { "privacy", "status" })
                if (filters != null && Json.Str(filters, key).Length > 0) query[key] = Json.Str(filters, key);
            if (!string.IsNullOrEmpty(pageToken)) query["paginationToken"] = pageToken;
            return Send(Kind.Read, "GET", "/models", query, null, 90, cancel);
        }

        /// <summary>
        /// Searches this project's assets: { hits, estimatedTotalHits }. The API pages through
        /// every project of the team and then drops other projects' hits, so the filter names
        /// the project: without it a page can come back empty while hits exist.
        /// </summary>
        public Task<Result<JObject>> SearchAssets(string query, string filter, int limit, int offset, CancellationToken cancel = default)
        {
            var scoped = string.IsNullOrEmpty(ProjectId) ? "" : $"ownerId = \"{ProjectId}\"";
            var combined = string.IsNullOrWhiteSpace(filter) ? scoped : scoped.Length == 0 ? filter : $"({filter}) AND {scoped}";
            var body = new JObject { ["public"] = false, ["limit"] = limit, ["offset"] = offset };
            if (!string.IsNullOrWhiteSpace(query)) body["query"] = query.Trim();
            // Keyword search ranks by relevance; an empty query lists newest first.
            else body["sortBy"] = new JArray("createdAt:desc");
            if (combined.Length > 0) body["filter"] = combined;
            return Send(Kind.Read, "POST", "/search/assets", null, body, DefaultTimeout, cancel);
        }

        /// <summary>One page of the project's assets of these types, newest first: { assets, nextPaginationToken }.</summary>
        public Task<Result<JObject>> ListAssets(IEnumerable<string> types, int limit, string pageToken, CancellationToken cancel = default)
        {
            var query = new Dictionary<string, string> { ["pageSize"] = limit.ToString() };
            var list = types?.Where(t => !string.IsNullOrEmpty(t)).ToList();
            if (list != null && list.Count > 0) query["types"] = string.Join(",", list);
            if (!string.IsNullOrEmpty(pageToken)) query["paginationToken"] = pageToken;
            return Send(Kind.Read, "GET", "/assets", query, null, DefaultTimeout, cancel);
        }

        /// <summary>Paid (Prompt Spark charges a small amount). dryRun prices it. Replies { prompts, creativeUnitsCost }.</summary>
        public Task<Result<JObject>> PromptSpark(string modelId, string prompt, IList<string> images = null, int results = 1, bool dryRun = false, CancellationToken cancel = default)
        {
            var body = new JObject { ["mode"] = PromptSparkMode, ["modelId"] = modelId, ["numResults"] = Math.Max(1, Math.Min(5, results)) };
            if (!string.IsNullOrWhiteSpace(prompt)) body["prompt"] = prompt;
            if (images != null && images.Count > 0) body["images"] = new JArray(images);
            return Send(dryRun ? Kind.Price : Kind.Paid, "POST", "/generate/prompt", dryRun ? DryRun() : null, body, 120, cancel);
        }

        public Task<Result<JObject>> ListWorkflows(string privacy, int limit = 50, CancellationToken cancel = default)
        {
            var query = new Dictionary<string, string> { ["pageSize"] = limit.ToString() };
            if (!string.IsNullOrEmpty(privacy)) query["privacy"] = privacy;
            return Send(Kind.Read, "GET", "/workflows", query, null, DefaultTimeout, cancel);
        }

        public Task<Result<JObject>> GetWorkflow(string workflowId, CancellationToken cancel = default)
            => Send(Kind.Read, "GET", "/workflows/" + Esc(workflowId), null, null, DefaultTimeout, cancel);

        /// <summary>Every team of the account with its projects. Never scoped: a stale project id would be refused.</summary>
        public Task<Result<JObject>> ListTeams(CancellationToken cancel = default)
            => Send(Kind.Read, "GET", "/teams", null, null, DefaultTimeout, cancel, scoped: false);

        /// <summary>
        /// Starts a multipart upload (no CU): { upload_id, part_size, parts: [{ part_number,
        /// upload_url }] }. Each part is PUT as raw bytes, then <see cref="UploadComplete"/>.
        /// </summary>
        public async Task<Result<JObject>> UploadStart(string fileName, string contentType, string kind, long fileSize, CancellationToken cancel = default)
        {
            var parts = Math.Max(1, (int)Math.Ceiling(fileSize / (double)UploadPartSize));
            var body = new JObject
            {
                ["fileName"] = UploadFileName(fileName),
                ["contentType"] = contentType,
                ["kind"] = kind,
                ["fileSize"] = fileSize,
                ["parts"] = parts,
            };
            var result = Unwrap<JObject>(await Send(Kind.Paid, "POST", "/uploads", null, body, DefaultTimeout, cancel), "upload");
            if (!result.Ok) return result;
            var upload = result.Value;
            var id = Json.Str(upload, "id");
            if (id.Length == 0) return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario did not start the upload.");
            var list = new JArray();
            foreach (var part in (upload["parts"] as JArray ?? new JArray()).OfType<JObject>())
                list.Add(new JObject { ["part_number"] = (int)Json.Num(part, "number", list.Count + 1), ["upload_url"] = Json.Str(part, "url") });
            return Result<JObject>.Success(new JObject { ["upload_id"] = id, ["part_size"] = UploadPartSize, ["parts"] = list });
        }

        /// <summary>Completes an upload and waits until Scenario imported it: { asset_id }.</summary>
        public async Task<Result<JObject>> UploadComplete(string uploadId, CancellationToken cancel = default)
        {
            var path = "/uploads/" + Esc(uploadId);
            var done = await Send(Kind.Paid, "POST", path + "/action", null, new JObject { ["action"] = "complete" }, 180, cancel);
            // A complete that raced an earlier one, or landed before the upload was readable, is fine: the poll decides.
            if (!done.Ok && !(done.Error.Status == 400 && (done.Error.Detail.Contains("Unable to complete upload") || done.Error.Detail.Contains("Unable to load"))))
                return done;
            for (double waited = 0; waited <= UploadWaitSeconds; waited += UploadPollSeconds)
            {
                var poll = Unwrap<JObject>(await Send(Kind.Read, "GET", path, null, null, DefaultTimeout, cancel), "upload");
                if (!poll.Ok) return poll;
                var assetId = Json.Str(poll.Value, "entityId");
                if (assetId.Length > 0) return Result<JObject>.Success(new JObject { ["asset_id"] = assetId });
                var status = Json.Str(poll.Value, "status");
                if (status == "failed")
                    return Result<JObject>.Fail(ErrorKind.Invalid, "Scenario could not import the upload: " + Json.Str(poll.Value, "errorMessage", "no details"));
                if (status == "imported") return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario imported the upload but named no asset.");
                await Transport.Delay(UploadPollSeconds, cancel);
            }
            return Result<JObject>.Fail(ErrorKind.Timeout, "Scenario is still processing the upload. Try again in a moment.");
        }

        /// <summary>Lowercase kebab case, extension kept: presigned upload URLs break on spaces and brackets.</summary>
        public static string UploadFileName(string fileName)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "") ?? "";
            var extension = (System.IO.Path.GetExtension(fileName ?? "") ?? "").ToLowerInvariant();
            var stem = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return (stem.Length > 0 ? stem : "file") + Regex.Replace(extension, "[^a-z0-9.]", "");
        }

        // --- Request core -------------------------------------------------------

        /// <summary>
        /// One API call. scoped: an OAuth call carries the team and project as query
        /// parameters (an API key is scoped by the key).
        /// </summary>
        public async Task<Result<JObject>> Send(Kind kind, string method, string path, IDictionary<string, string> query, JObject body,
            double timeout = DefaultTimeout, CancellationToken cancel = default, bool scoped = true)
        {
            if (!HasCredentials) return Result<JObject>.Fail(ErrorKind.Auth, "Sign in to Scenario first.", e => e.NotSent = true);
            if (kind == Kind.Paid && SpendingDisabled)
                return Result<JObject>.Fail(ErrorKind.Local, "Spending is turned off (SCENARIO_NO_SPEND=1 or the Scenario setting), so nothing was sent.", e => e.NotSent = true);
            var parameters = query != null ? new Dictionary<string, string>(query) : new Dictionary<string, string>();
            if (scoped && Auth.NeedsScope)
            {
                if (string.IsNullOrEmpty(TeamId) || string.IsNullOrEmpty(ProjectId))
                    return Result<JObject>.Fail(ErrorKind.Local, "Pick a Scenario team and project first.", e => e.NotSent = true);
                parameters["teamId"] = TeamId;
                parameters["projectId"] = ProjectId;
            }
            var url = BaseUrl + path + QueryString(parameters);
            // Every attempt goes out under the account this call was built for.
            var auth = Auth;
            var payload = body != null ? Encoding.UTF8.GetBytes(body.ToString(Formatting.None)) : null;
            var attempts = kind == Kind.Read || kind == Kind.Price ? ReadAttempts : 1;
            Result<JObject> result = null;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                result = await SendOnce(kind, auth, method, url, payload, timeout, cancel);
                if (kind == Kind.Price)
                {
                    // A job in a price check's reply, even an error reply, means a paid run may have started.
                    var jobId = CreatedJobId(result.Ok ? result.Value : Json.Parse(result.Error.Detail ?? ""));
                    if (jobId.Length > 0)
                        return Result<JObject>.Fail(ErrorKind.EstimateJob,
                            $"Scenario returned a job for a price check, so it may have started a paid run. Automatic price checks are off for this session. Check job {jobId}.",
                            e => e.JobId = jobId);
                }
                if (result.Ok) return result;
                var error = result.Error;
                if (cancel.IsCancellationRequested) return result;
                if (attempt + 1 < attempts && Errors.RetryableKinds.Contains(error.Kind))
                {
                    var delay = RetryDelay(attempt, error.RetryAfter);
                    if (delay >= 0)
                    {
                        await Transport.Delay(delay, cancel);
                        continue;
                    }
                }
                return result;
            }
            return result;
        }

        /// <summary>One request, resent once after an OAuth refresh when the authorizer refused it.</summary>
        async Task<Result<JObject>> SendOnce(Kind kind, IAuthProvider auth, string method, string url, byte[] payload, double timeout, CancellationToken cancel)
        {
            var reply = await Post(auth, method, url, payload, timeout, cancel);
            if (reply.Answered && IsAuthorizerRefusal(reply) && ReferenceEquals(Auth, auth) && await auth.RefreshAsync())
            {
                // The refused attempt never reached Scenario: the resend is the one that counts, so it
                // goes only for the same account and, if paid, only while spending is still on.
                if (!ReferenceEquals(Auth, auth))
                    return Result<JObject>.Fail(ErrorKind.Auth, "The Scenario account changed during the request, so it was not sent.", e => e.NotSent = true);
                if (kind == Kind.Paid && SpendingDisabled)
                    return Result<JObject>.Fail(ErrorKind.Local, "Spending is turned off (SCENARIO_NO_SPEND=1 or the Scenario setting), so nothing was sent.", e => e.NotSent = true);
                reply = await Post(auth, method, url, payload, timeout, cancel);
            }
            return Read(kind, reply);
        }

        async Task<HttpReply> Post(IAuthProvider auth, string method, string url, byte[] payload, double timeout, CancellationToken cancel)
        {
            var request = new HttpRequestSpec
            {
                Method = method,
                Url = url,
                Body = payload,
                ContentType = payload != null ? "application/json" : null,
                TimeoutSeconds = Math.Max(1, Math.Min(240, timeout)),
                // The credential goes to the API only, never to a host it redirects to.
                FollowRedirects = false,
            };
            request.Headers["Authorization"] = await auth.AuthorizationHeaderAsync();
            request.Headers["Accept"] = "application/json";
            request.Headers["User-Agent"] = UserAgent;
            return await Transport.SendAsync(request, cancel);
        }

        /// <summary>
        /// The API's authorizer refused the credential before the request reached Scenario:
        /// a 401, or a 403 whose body is only the reason "Forbidden" (expired or unknown
        /// token). Refusals by Scenario itself carry another reason or an error name.
        /// </summary>
        static bool IsAuthorizerRefusal(HttpReply reply)
        {
            if (reply.Status == 401) return true;
            if (reply.Status != 403) return false;
            return Json.Parse(reply.Body ?? "") is JObject body && Json.Str(body, "reason") == "Forbidden" && body["name"] == null;
        }

        static Result<JObject> Read(Kind kind, HttpReply reply)
        {
            var paid = kind == Kind.Paid || kind == Kind.Cancel;
            if (!reply.Answered)
            {
                var errorKind = reply.Error == "timeout" ? ErrorKind.Timeout : reply.Error == "canceled" ? ErrorKind.Canceled : ErrorKind.Transport;
                return Result<JObject>.Fail(errorKind, errorKind == ErrorKind.Canceled ? "Canceled." : Errors.MessageFor(errorKind), e =>
                {
                    e.Detail = reply.ErrorDetail ?? "";
                    // Canceling a paid call after it left does not unsend it.
                    if (paid) Ambiguous(e);
                });
            }
            var body = reply.Body ?? "";
            var status = reply.Status;
            if (status < 200 || status >= 300)
            {
                var parsed = Json.Parse(body) as JObject;
                var reason = parsed != null ? Json.Str(parsed, "reason", Json.Str(parsed, "message")) : "";
                var name = parsed != null ? Json.Str(parsed, "name") : "";
                var detailReason = (parsed?["details"] as JObject) != null ? Json.Str((JObject)parsed["details"], "reason") : "";
                var errorKind = IsAuthorizerRefusal(reply) ? ErrorKind.Auth
                    : name == "PlanLimitReachedError" ? ErrorKind.Quota
                    : name == "RateLimitError" ? ErrorKind.RateLimit
                    : detailReason == "task-id-required" ? ErrorKind.Invalid
                    : Errors.Classify(reason.Length > 0 ? reason : body, status);
                var message = detailReason == "task-id-required"
                    ? "This Scenario project bills each run to a task, which the plugin does not send yet. Use another project."
                    : Errors.MessageFor(errorKind, IsAuthorizerRefusal(reply) ? "" : reason.Length > 0 ? reason : Trimmed(body));
                var retryAfter = RetryAfterSeconds(reply.Header("retry-after"));
                return Result<JObject>.Fail(errorKind, message, e =>
                {
                    e.Status = status;
                    e.RetryAfter = retryAfter;
                    e.Detail = body;
                    // A 5xx or a redirect on a paid call: Scenario may have started it.
                    if (paid && (status >= 500 || status < 200 || (status >= 300 && status < 400))) Ambiguous(e);
                });
            }
            var value = Json.Parse(body) as JObject;
            if (value == null || (kind == Kind.Paid && !value.HasValues))
                return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario returned a response the plugin could not read.", e =>
                {
                    e.Status = status;
                    e.Detail = Trimmed(body);
                    if (paid) Ambiguous(e);
                });
            return Result<JObject>.Success(value);
        }

        static void Ambiguous(ScenarioError error)
        {
            error.Ambiguous = true;
            error.Message = "The request status is unknown: Scenario may have accepted it. Check recent jobs before sending it again.";
        }

        static string QueryString(IDictionary<string, string> parameters)
        {
            if (parameters == null || parameters.Count == 0) return "";
            return "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value ?? "")));
        }

        static string Esc(string segment) => Uri.EscapeDataString(segment ?? "");

        static string Trimmed(string body) => body.Length > 300 ? body.Substring(0, 300) : body;

        static double RetryDelay(int attempt, double retryAfter)
        {
            if (retryAfter >= 0) return retryAfter > MaxRetryAfter ? -1 : retryAfter;
            var baseDelay = Math.Min(8.0, 0.5 * Math.Pow(2, attempt));
            double r;
            lock (Jitter) r = Jitter.NextDouble();
            return baseDelay * (0.5 + 0.5 * r);
        }

        static double RetryAfterSeconds(string raw)
        {
            return double.TryParse((raw ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : -1;
        }

        static Result<T> Unwrap<T>(Result<JObject> result, string key) where T : JToken
        {
            if (!result.Ok) return result.Cast<T>();
            if (result.Value[key] is T value) return Result<T>.Success(value);
            return Result<T>.Fail(ErrorKind.Protocol, $"Scenario's reply had no '{key}'.");
        }
    }
}
