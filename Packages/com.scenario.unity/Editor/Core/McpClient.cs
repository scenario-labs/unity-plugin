using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>
    /// Scenario MCP client: JSON-RPC over HTTP with SSE replies.
    ///
    /// Money rules (cross-engine contract, section 3): a paid call is sent once and
    /// never retried; a transport failure on a paid call is "ambiguous" (Scenario may
    /// have started the job); an estimate that comes back with a job id is an alarm.
    /// Only the read tools below are retried. A 401 is the one exception: the request
    /// was refused before dispatch, so it is resent once after an OAuth refresh.
    /// </summary>
    public sealed class McpClient
    {
        public const string DefaultEndpoint = "https://mcp.scenario.com/mcp?toolsets=full";
        public const string ProtocolVersion = "2025-03-26";
        public const string Version = "0.2.0"; // x-release-please-version
        const int ReadAttempts = 3;
        const double MaxRetryAfter = 30;
        const double DefaultTimeout = 60;

        /// <summary>Idempotent reads: the only calls ever retried.</summary>
        public static readonly HashSet<string> ReadTools = new HashSet<string>
        {
            "model_schema_get", "models_list", "model_get", "job_get", "jobs_list", "asset_get", "assets_get_bulk", "asset_download",
            "recommend", "search", "usage", "teams_list", "projects_list", "project_get", "assets_list", "collections_list",
            "collection_get", "workflows_list", "workflow_get", "diagnostics_run", "api_key_create",
        };

        /// <summary>Calls that can spend Creative Units or create something.</summary>
        public static readonly HashSet<string> SubmitTools = new HashSet<string>
        {
            "model_run", "prompt_spark", "upload_asset", "upload_asset_complete", "job_cancel", "workflow_run",
            "asset_caption", "asset_describe", "asset_detect", "asset_analyze",
        };

        /// <summary>Tools that never take team or project scope.</summary>
        static readonly HashSet<string> Unscoped = new HashSet<string> { "teams_list", "projects_list" };

        public string Endpoint = DefaultEndpoint;
        public ITransport Transport;
        public IAuthProvider Auth = new NoAuth();
        public string TeamId = "";
        public string ProjectId = "";
        /// <summary>SCENARIO_NO_SPEND=1 refuses every paid call before any request is made.</summary>
        public bool SpendingDisabled = Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") == "1";
        public string UserAgent = DefaultUserAgent();

        string _sessionId = "";
        Task<Result<bool>> _initializing;
        readonly object _initLock = new object();
        int _nextId = 1;
        static readonly System.Random Jitter = new System.Random();

        public McpClient(ITransport transport, IAuthProvider auth = null)
        {
            Transport = transport;
            if (auth != null) Auth = auth;
        }

        public bool HasCredentials => Auth != null && Auth.HasCredentials;

        /// <summary>True when OAuth is used and no project is picked yet.</summary>
        public bool NeedsWorkspace => Auth != null && Auth.NeedsScope && (string.IsNullOrEmpty(TeamId) || string.IsNullOrEmpty(ProjectId));

        public void SetAuth(IAuthProvider auth)
        {
            Auth = auth ?? new NoAuth();
            ResetSession();
        }

        public void SetScope(string teamId, string projectId)
        {
            TeamId = teamId ?? "";
            ProjectId = projectId ?? "";
        }

        public void ResetSession()
        {
            lock (_initLock)
            {
                _sessionId = "";
                _initializing = null;
            }
        }

        static string DefaultUserAgent()
        {
            string unity;
            try { unity = UnityEngine.Application.unityVersion; }
            catch (Exception) { unity = "unknown"; }
            return $"ScenarioUnity/{Version} (Unity {unity}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription})";
        }

        public static bool IsSubmission(string tool, JObject arguments)
        {
            if (!SubmitTools.Contains(tool)) return false;
            if (tool == "job_cancel" || tool == "upload_asset" || tool == "upload_asset_complete") return true;
            return !Json.Truthy(arguments?["dry_run"]);
        }

        public static bool IsRetryable(string tool, JObject arguments)
        {
            if ((tool == "model_run" || tool == "workflow_run" || tool == "prompt_spark") && Json.Truthy(arguments?["dry_run"]))
                return true; // a free estimate is a read
            return ReadTools.Contains(tool);
        }

        /// <summary>The job id carried by a reply, or "" (an estimate must carry none).</summary>
        public static string CreatedJobId(JToken value)
        {
            if (!(value is JObject obj)) return "";
            foreach (var key in new[] { "job_id", "jobId" })
            {
                var direct = Json.Str(obj, key);
                if (direct.Length > 0) return direct;
            }
            foreach (var key in new[] { "job", "inference" })
            {
                if (obj[key] is JObject inner)
                {
                    var id = Json.Str(inner, "jobId");
                    if (id.Length == 0) id = Json.Str(inner, "id");
                    if (id.Length > 0) return id;
                }
            }
            if (obj["jobs"] is JArray jobs && jobs.Count > 0 && jobs[0] is JObject first)
            {
                var id = Json.Str(first, "jobId");
                if (id.Length == 0) id = Json.Str(first, "id");
                if (id.Length > 0) return id;
            }
            return "";
        }

        // --- High-level calls ---------------------------------------------------

        public sealed class Price
        {
            public double Cu;
            public double Discount;
        }

        /// <summary>Free price check for a model run.</summary>
        public async Task<Result<Price>> Estimate(string modelId, JObject parameters, CancellationToken cancel = default)
        {
            var args = new JObject { ["model_id"] = modelId, ["parameters"] = parameters ?? new JObject(), ["dry_run"] = true };
            return PriceFrom(await CallTool("model_run", args, DefaultTimeout, cancel));
        }

        /// <summary>Free price check for a workflow run.</summary>
        public async Task<Result<Price>> EstimateWorkflow(string workflowId, JObject inputs, CancellationToken cancel = default)
        {
            var args = new JObject { ["workflow_id"] = workflowId, ["inputs"] = inputs ?? new JObject(), ["dry_run"] = true };
            return PriceFrom(await CallTool("workflow_run", args, DefaultTimeout, cancel));
        }

        static Result<Price> PriceFrom(Result<JObject> result)
        {
            if (!result.Ok) return result.Cast<Price>();
            var value = result.Value;
            var cost = value["creativeUnitsCost"] ?? value["cuCost"] ?? value["estimatedCost"] ?? (value["cost"] as JObject)?["creativeUnitsCost"];
            if (cost == null) return Result<Price>.Fail(ErrorKind.Protocol, "Scenario returned no price for this request.");
            return Result<Price>.Success(new Price
            {
                Cu = Json.ToDouble(cost),
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
        {
            var args = new JObject { ["model_id"] = modelId, ["parameters"] = parameters ?? new JObject(), ["wait"] = false };
            return SubmittedFrom(await CallTool("model_run", args, 120, cancel));
        }

        /// <summary>Paid. Sent exactly once.</summary>
        public async Task<Result<Submitted>> SubmitWorkflow(string workflowId, JObject inputs, CancellationToken cancel = default)
        {
            var args = new JObject { ["workflow_id"] = workflowId, ["inputs"] = inputs ?? new JObject() };
            return SubmittedFrom(await CallTool("workflow_run", args, 120, cancel));
        }

        static Result<Submitted> SubmittedFrom(Result<JObject> result)
        {
            if (!result.Ok) return result.Cast<Submitted>();
            var jobId = CreatedJobId(result.Value);
            if (jobId.Length == 0)
                return Result<Submitted>.Fail(ErrorKind.Protocol,
                    "Scenario accepted the request but returned no job id. Check recent jobs before generating again.",
                    e => e.Ambiguous = true);
            var status = Json.Str(result.Value, "status");
            if (status.Length == 0 && result.Value["job"] is JObject job) status = Json.Str(job, "status");
            return Result<Submitted>.Success(new Submitted { JobId = jobId, Status = status.Length > 0 ? status : "queued", Raw = result.Value });
        }

        public async Task<Result<JObject>> GetJob(string jobId, CancellationToken cancel = default)
            => Unwrap<JObject>(await CallTool("job_get", new JObject { ["job_id"] = jobId }, DefaultTimeout, cancel), "job");

        public async Task<Result<JArray>> ListJobs(int pageSize = 20, CancellationToken cancel = default)
            => Unwrap<JArray>(await CallTool("jobs_list", new JObject { ["limit"] = Math.Max(1, Math.Min(100, pageSize)) }, DefaultTimeout, cancel), "jobs");

        public Task<Result<JObject>> CancelJob(string jobId, CancellationToken cancel = default)
            => CallTool("job_cancel", new JObject { ["job_id"] = jobId }, DefaultTimeout, cancel);

        public async Task<Result<JObject>> GetAsset(string assetId, CancellationToken cancel = default)
            => Unwrap<JObject>(await CallTool("asset_get", new JObject { ["asset_id"] = assetId }, DefaultTimeout, cancel), "asset");

        /// <summary>A signed CDN URL. format converts 3D (glb, fbx, obj) and images; "" keeps the original.</summary>
        public async Task<Result<string>> DownloadUrl(string assetId, string format = "", CancellationToken cancel = default)
        {
            var args = new JObject { ["asset_id"] = assetId };
            if (!string.IsNullOrEmpty(format)) args["format"] = format;
            var result = await CallTool("asset_download", args, 120, cancel);
            if (!result.Ok) return result.Cast<string>();
            var url = Json.Str(result.Value, "url");
            if (url.Length == 0) url = Json.Str(result.Value, "downloadUrl");
            return url.Length > 0 ? Result<string>.Success(url) : Result<string>.Fail(ErrorKind.Protocol, "Scenario's reply had no download URL.");
        }

        public Task<Result<JObject>> GetSchema(string modelId, CancellationToken cancel = default)
            => CallTool("model_schema_get", new JObject { ["model_id"] = modelId }, DefaultTimeout, cancel);

        public Task<Result<JObject>> GetModel(string modelId, CancellationToken cancel = default)
            => CallTool("model_get", new JObject { ["model_id"] = modelId }, DefaultTimeout, cancel);

        public Task<Result<JObject>> Recommend(string capability, string prompt, int limit = 5, string priority = "quality", CancellationToken cancel = default)
        {
            var args = new JObject { ["capability"] = capability, ["prompt"] = prompt ?? "", ["limit"] = Math.Max(1, Math.Min(10, limit)) };
            if (!string.IsNullOrEmpty(priority)) args["priority"] = priority;
            return CallTool("recommend", args, 90, cancel);
        }

        public Task<Result<JObject>> ListModels(JObject filters, int limit, string pageToken, CancellationToken cancel = default)
        {
            var args = new JObject { ["limit"] = limit };
            if (filters != null) args["filters"] = filters;
            if (!string.IsNullOrEmpty(pageToken)) args["page_token"] = pageToken;
            return CallTool("models_list", args, 90, cancel);
        }

        public Task<Result<JObject>> Search(JObject arguments, CancellationToken cancel = default)
            => CallTool("search", arguments, DefaultTimeout, cancel);

        public Task<Result<JObject>> ListAssets(JObject filters, int limit, string pageToken, bool includeUrls, CancellationToken cancel = default)
        {
            var args = new JObject { ["limit"] = limit, ["include_urls"] = includeUrls };
            if (filters != null && filters.HasValues) args["filters"] = filters;
            if (!string.IsNullOrEmpty(pageToken)) args["page_token"] = pageToken;
            return CallTool("assets_list", args, DefaultTimeout, cancel);
        }

        /// <summary>Paid (Prompt Spark charges a small amount). dryRun prices it.</summary>
        public Task<Result<JObject>> PromptSpark(string modelId, string prompt, IList<string> images = null, int results = 1, bool dryRun = false, CancellationToken cancel = default)
        {
            var args = new JObject { ["model_id"] = modelId, ["num_results"] = Math.Max(1, Math.Min(5, results)) };
            if (!string.IsNullOrWhiteSpace(prompt)) args["prompt"] = prompt;
            if (images != null && images.Count > 0) args["images"] = new JArray(images);
            if (dryRun) args["dry_run"] = true;
            return CallTool("prompt_spark", args, 120, cancel);
        }

        public Task<Result<JObject>> ListWorkflows(string privacy, int limit = 50, CancellationToken cancel = default)
        {
            var args = new JObject { ["limit"] = limit };
            if (!string.IsNullOrEmpty(privacy)) args["privacy"] = privacy;
            return CallTool("workflows_list", args, DefaultTimeout, cancel);
        }

        public Task<Result<JObject>> GetWorkflow(string workflowId, CancellationToken cancel = default)
            => CallTool("workflow_get", new JObject { ["workflow_id"] = workflowId }, DefaultTimeout, cancel);

        public Task<Result<JObject>> ListTeams(CancellationToken cancel = default) => CallTool("teams_list", new JObject(), DefaultTimeout, cancel);

        public Task<Result<JObject>> ListProjects(string teamId, CancellationToken cancel = default)
            => CallTool("projects_list", new JObject { ["team_id"] = teamId, ["limit"] = 100 }, DefaultTimeout, cancel);

        public Task<Result<JObject>> GetProject(CancellationToken cancel = default)
            => CallTool("project_get", new JObject(), DefaultTimeout, cancel);

        /// <summary>Creates an asset record, costs no CU: starts a multipart upload.</summary>
        public Task<Result<JObject>> UploadStart(string fileName, string contentType, string kind, long fileSize, CancellationToken cancel = default)
            => CallTool("upload_asset", new JObject
            {
                ["file_name"] = fileName, ["content_type"] = contentType, ["kind"] = kind, ["file_size"] = fileSize,
            }, DefaultTimeout, cancel);

        public Task<Result<JObject>> UploadComplete(string uploadId, CancellationToken cancel = default)
            => CallTool("upload_asset_complete", new JObject { ["upload_id"] = uploadId }, 180, cancel);

        // --- Tool call core -----------------------------------------------------

        public async Task<Result<JObject>> CallTool(string tool, JObject arguments = null, double timeout = DefaultTimeout, CancellationToken cancel = default)
        {
            arguments = arguments ?? new JObject();
            if (!HasCredentials) return Result<JObject>.Fail(ErrorKind.Auth, "Sign in to Scenario first.", e => e.NotSent = true);
            var submit = IsSubmission(tool, arguments);
            // Cancelling can only lower spend, so the switch lets it through.
            if (submit && SpendingDisabled && tool != "job_cancel")
                return Result<JObject>.Fail(ErrorKind.Local, "Spending is turned off (SCENARIO_NO_SPEND=1 or the Scenario setting), so nothing was sent.", e => e.NotSent = true);
            if (Auth.NeedsScope && !Unscoped.Contains(tool) && arguments["team_id"] == null)
            {
                if (string.IsNullOrEmpty(TeamId) || string.IsNullOrEmpty(ProjectId))
                {
                    if (tool != "recommend")
                        return Result<JObject>.Fail(ErrorKind.Local, "Pick a Scenario team and project first.", e => e.NotSent = true);
                }
                else
                {
                    arguments["team_id"] = TeamId;
                    arguments["project_id"] = ProjectId;
                }
            }
            var attempts = IsRetryable(tool, arguments) ? ReadAttempts : 1;
            Result<JObject> result = null;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                result = await CallOnce(tool, arguments, timeout, submit, cancel);
                if (result.Ok)
                {
                    if ((tool == "model_run" || tool == "workflow_run") && Json.Truthy(arguments["dry_run"]))
                    {
                        var jobId = CreatedJobId(result.Value);
                        if (jobId.Length > 0)
                            return Result<JObject>.Fail(ErrorKind.EstimateJob,
                                $"Scenario returned a job for a price check, so it may have started a paid run. Automatic price checks are off for this session. Check job {jobId}.",
                                e => e.JobId = jobId);
                    }
                    return result;
                }
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

        async Task<Result<JObject>> CallOnce(string tool, JObject arguments, double timeout, bool submit, CancellationToken cancel)
        {
            var rpc = await Rpc("tools/call", new JObject { ["name"] = tool, ["arguments"] = arguments }, timeout, cancel);
            if (!rpc.Ok)
            {
                if (submit && Errors.AmbiguousKinds.Contains(rpc.Error.Kind) && !rpc.Error.NotSent)
                {
                    rpc.Error.Ambiguous = true;
                    rpc.Error.Message = "The request status is unknown: Scenario may have accepted it. Check recent jobs before sending it again.";
                }
                return rpc;
            }
            var result = rpc.Value;
            var structured = result["structuredContent"];
            var content = result["content"] as JArray;
            if (Json.Truthy(result["isError"]))
            {
                var detail = FirstText(content);
                if (structured is JObject s && s.HasValues) detail = s.ToString(Formatting.None);
                var kind = Errors.Classify(detail);
                return Result<JObject>.Fail(kind, Errors.MessageFor(kind, detail), e => e.Detail = detail);
            }
            if (structured is JObject structuredObj)
            {
                if (submit && !structuredObj.HasValues)
                    return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario returned an empty reply.", e => e.Ambiguous = true);
                return Result<JObject>.Success(structuredObj);
            }
            if (content != null)
            {
                foreach (var item in content)
                {
                    if (item is JObject block && Json.Str(block, "type") == "text")
                    {
                        if (Json.Parse(Json.Str(block, "text")) is JObject data)
                        {
                            if (submit && !data.HasValues) break;
                            return Result<JObject>.Success(data);
                        }
                    }
                }
            }
            return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario returned no usable result.", e => e.Ambiguous = submit);
        }

        /// <summary>
        /// One JSON-RPC request. Initializes the MCP session first, once more if the
        /// server reports the session expired (the request was not dispatched then),
        /// and refreshes an OAuth token once on a 401.
        /// </summary>
        async Task<Result<JObject>> Rpc(string method, JObject parameters, double timeout, CancellationToken cancel)
        {
            var init = await EnsureInitialized(timeout, cancel);
            if (!init.Ok)
            {
                init.Error.NotSent = true;
                return init.Cast<JObject>();
            }
            var requestId = TakeId();
            var reply = await Post(method, parameters, requestId, timeout, true, cancel);
            if (!reply.Ok && reply.Error.SessionExpired)
            {
                ResetSession();
                var again = await EnsureInitialized(timeout, cancel);
                if (!again.Ok)
                {
                    again.Error.NotSent = true;
                    return again.Cast<JObject>();
                }
                reply = await Post(method, parameters, requestId, timeout, true, cancel);
            }
            if (!reply.Ok && reply.Error.Kind == ErrorKind.Auth && reply.Error.Status == 401 && await Auth.RefreshAsync())
            {
                ResetSession();
                var again = await EnsureInitialized(timeout, cancel);
                if (!again.Ok)
                {
                    again.Error.NotSent = true;
                    return again.Cast<JObject>();
                }
                reply = await Post(method, parameters, requestId, timeout, true, cancel);
            }
            if (!reply.Ok) return reply;
            var envelope = reply.Value;
            if (envelope["error"] != null)
            {
                var rpcError = envelope["error"];
                var detail = rpcError.Type == JTokenType.String ? (string)rpcError : rpcError.ToString(Formatting.None);
                var kind = Errors.Classify(detail);
                return Result<JObject>.Fail(kind, Errors.MessageFor(kind, detail), e => e.Detail = detail);
            }
            if (!(envelope["result"] is JObject resultObj))
                return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario returned an unexpected reply structure.");
            return Result<JObject>.Success(resultObj);
        }

        Task<Result<bool>> EnsureInitialized(double timeout, CancellationToken cancel)
        {
            lock (_initLock)
            {
                if (_initializing == null || (_initializing.IsCompleted && !_initializing.Result.Ok))
                    _initializing = Initialize(timeout, CancellationToken.None); // shared by every waiting call
                return _initializing;
            }
        }

        async Task<Result<bool>> Initialize(double timeout, CancellationToken cancel)
        {
            var parameters = new JObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "scenario-unity-plugin", ["version"] = Version },
            };
            var reply = await Post("initialize", parameters, TakeId(), timeout, false, cancel);
            if (!reply.Ok && reply.Error.Kind == ErrorKind.Auth && reply.Error.Status == 401 && await Auth.RefreshAsync())
                reply = await Post("initialize", parameters, TakeId(), timeout, false, cancel);
            if (!reply.Ok) return reply.Cast<bool>();
            if (reply.Value["error"] != null)
            {
                var detail = reply.Value["error"].ToString(Formatting.None);
                var kind = Errors.Classify(detail);
                return Result<bool>.Fail(kind, Errors.MessageFor(kind, detail));
            }
            var notified = await Post("notifications/initialized", new JObject(), null, timeout, false, cancel);
            if (!notified.Ok) return notified.Cast<bool>();
            return Result<bool>.Success(true);
        }

        async Task<Result<JObject>> Post(string method, JObject parameters, object requestId, double timeout, bool allowSessionSignal, CancellationToken cancel)
        {
            var payload = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (requestId != null) payload["id"] = JToken.FromObject(requestId);
            if (parameters != null && parameters.HasValues) payload["params"] = parameters;
            var auth = await Auth.AuthorizationHeaderAsync();
            var request = new HttpRequestSpec
            {
                Method = "POST",
                Url = Endpoint,
                Body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)),
                ContentType = "application/json",
                TimeoutSeconds = Math.Max(1, Math.Min(240, timeout)),
            };
            request.Headers["Authorization"] = auth;
            request.Headers["Accept"] = "application/json, text/event-stream";
            request.Headers["MCP-Protocol-Version"] = ProtocolVersion;
            request.Headers["User-Agent"] = UserAgent;
            string sessionUsed;
            lock (_initLock) sessionUsed = _sessionId;
            if (sessionUsed.Length > 0) request.Headers["Mcp-Session-Id"] = sessionUsed;
            var response = await Transport.SendAsync(request, cancel);
            if (response.Error == "timeout") return Result<JObject>.Fail(ErrorKind.Timeout, Errors.MessageFor(ErrorKind.Timeout));
            if (response.Error == "canceled") return Result<JObject>.Fail(ErrorKind.Canceled, "Canceled.");
            if (!response.Answered) return Result<JObject>.Fail(ErrorKind.Transport, Errors.MessageFor(ErrorKind.Transport), e => e.Detail = response.ErrorDetail);
            var newSession = response.Header("mcp-session-id");
            if (newSession.Length > 0)
                lock (_initLock) _sessionId = newSession;
            var status = response.Status;
            var body = response.Body ?? "";
            if (status < 200 || status >= 300)
            {
                if (allowSessionSignal && (status == 400 || status == 404))
                {
                    // "session" covers "Mcp-Session-Id is required" and "No valid session ID";
                    // a 404 on a request that carried a session id means it expired.
                    if (body.ToLowerInvariant().Contains("session") || (status == 404 && sessionUsed.Length > 0))
                        return Result<JObject>.Fail(ErrorKind.Protocol, "Session expired.", e => { e.SessionExpired = true; e.Status = status; });
                }
                var kind = Errors.Classify(body, status);
                var retryAfter = RetryAfterSeconds(response.Header("retry-after"));
                return Result<JObject>.Fail(kind, Errors.MessageFor(kind, Trimmed(body)), e =>
                {
                    e.Status = status;
                    e.RetryAfter = retryAfter;
                    e.Detail = body;
                });
            }
            return Sse.Parse(body, response.Header("content-type"), requestId);
        }

        static string Trimmed(string body)
        {
            var parsed = Json.Parse(body);
            if (parsed is JObject obj)
            {
                var message = Json.Str(obj, "error_description");
                if (message.Length == 0) message = Json.Str(obj, "message");
                if (message.Length == 0 && obj["error"] is JObject err) message = Json.Str(err, "message");
                if (message.Length == 0) message = Json.Str(obj, "error");
                if (message.Length > 0) return message;
            }
            return body.Length > 300 ? body.Substring(0, 300) : body;
        }

        int TakeId() => Interlocked.Increment(ref _nextId);

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

        static string FirstText(JArray content)
        {
            if (content == null) return "";
            foreach (var item in content)
                if (item is JObject block && Json.Str(block, "type") == "text") return Json.Str(block, "text");
            return "";
        }

        static Result<T> Unwrap<T>(Result<JObject> result, string key) where T : JToken
        {
            if (!result.Ok) return result.Cast<T>();
            if (result.Value[key] is T value) return Result<T>.Success(value);
            return Result<T>.Fail(ErrorKind.Protocol, $"Scenario's reply had no '{key}'.");
        }
    }
}
