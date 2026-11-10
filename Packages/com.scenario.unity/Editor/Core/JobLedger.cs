using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    public static class JobState
    {
        public const string Submitting = "submitting";
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Success = "success";
        public const string Failed = "failed";
        public const string Blocked = "blocked";
        public const string CancelRequested = "cancel_requested";
        public const string Canceled = "canceled";
        public const string Unknown = "unknown";
        public const string Rejected = "rejected";
        public const string Dismissed = "dismissed";

        public static readonly HashSet<string> Active = new HashSet<string> { Queued, Running, CancelRequested };
        public static readonly HashSet<string> Unresolved = new HashSet<string> { Submitting, Unknown };
        public static readonly HashSet<string> Final = new HashSet<string> { Success, Failed, Blocked, Canceled, Rejected, Dismissed };

        public static string ForStatus(string status, string errorText)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case "success":
                case "succeeded":
                case "completed":
                    return Success;
                case "failure":
                case "failed":
                case "error":
                    return Errors.IsModerated(errorText) ? Blocked : Failed;
                case "canceled":
                case "cancelled":
                    return Canceled;
                case "queued":
                case "pending":
                    return Queued;
                default:
                    return Running;
            }
        }
    }

    /// <summary>One request, from intent to imported files.</summary>
    public sealed class JobRow
    {
        public string LocalId = "";
        public string JobId = "";
        public string State = JobState.Submitting;
        /// <summary>Output type id ("image", "model3d", ...) used for import and placement.</summary>
        public string OutputType = "";
        public string ModelId = "";
        public string ModelName = "";
        public string WorkflowId = "";
        public string Prompt = "";
        public JObject Parameters = new JObject();
        public string Fingerprint = "";
        public double QuotedCu;
        public double? ChargedCu;
        public float Progress;
        public List<string> AssetIds = new List<string>();
        /// <summary>Project-relative paths of the imported files.</summary>
        public List<string> Files = new List<string>();
        public bool Imported;
        public bool Place;
        /// <summary>GlobalObjectId of the object to place on, or "".</summary>
        public string PlaceTarget = "";
        /// <summary>Hierarchy path of that object, for objects in unsaved scenes, which have no GlobalObjectId.</summary>
        public string PlaceTargetPath = "";
        public List<string> Placed = new List<string>();
        public string Folder = "";
        /// <summary>The Scenario team and project the request was sent to, for web links.</summary>
        public string TeamId = "";
        public string ProjectId = "";
        /// <summary>"window", "agent:&lt;client&gt;", "api", "menu".</summary>
        public string Origin = "window";
        public string Error = "";
        public double CreatedAt;
        public double UpdatedAt;

        [JsonIgnore] public bool IsActive => JobState.Active.Contains(State) && JobId.Length > 0;
        [JsonIgnore] public bool IsUnresolved => JobState.Unresolved.Contains(State);
        [JsonIgnore] public bool IsFinal => JobState.Final.Contains(State);
    }

    /// <summary>
    /// Durable job ledger: survives editor restarts and domain reloads.
    ///
    /// The intent is written with state "submitting" BEFORE the paid request is
    /// sent. A row still "submitting" when the ledger loads means the editor
    /// stopped mid-request: it becomes "unknown" and blocks the same request until
    /// it is matched to a Scenario job or dismissed by the user.
    /// </summary>
    public sealed class JobLedger
    {
        public const int MaxRows = 300;
        /// <summary>A server job counts as the match of an unknown row if created this close after the intent.</summary>
        public const double ReconcileWindowSeconds = 180;
        const int FormatVersion = 1;

        public readonly string Path;
        public List<JobRow> Jobs = new List<JobRow>();
        int _counter;

        public event Action Changed;

        public JobLedger(string path)
        {
            Path = path ?? "";
        }

        public void Load()
        {
            Jobs = new List<JobRow>();
            if (Path.Length == 0 || !File.Exists(Path)) return;
            JObject data;
            try
            {
                data = Json.ParseObject(File.ReadAllText(Path));
            }
            catch (IOException)
            {
                data = null;
            }
            if (data == null)
            {
                UnityEngine.Debug.LogWarning("Scenario: the job ledger could not be read and was set aside: " + Path);
                try { File.Move(Path, Path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); }
                catch (IOException) { }
                return;
            }
            if (data["jobs"] is JArray rows)
            {
                foreach (var token in rows)
                {
                    JobRow row;
                    try { row = token.ToObject<JobRow>(); }
                    catch (JsonException) { continue; }
                    if (row == null || string.IsNullOrEmpty(row.LocalId)) continue;
                    if (row.State == JobState.Submitting)
                    {
                        row.State = JobState.Unknown;
                        row.Error = "The editor closed while this request was being sent. It may have started on Scenario.";
                    }
                    Jobs.Add(row);
                }
            }
        }

        public bool Save()
        {
            Changed?.Invoke();
            if (Path.Length == 0) return true;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");
                var tmp = Path + ".tmp";
                var json = new JObject { ["version"] = FormatVersion, ["jobs"] = JArray.FromObject(Jobs) };
                File.WriteAllText(tmp, json.ToString(Formatting.Indented));
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(tmp, Path);
                return true;
            }
            catch (IOException e)
            {
                UnityEngine.Debug.LogWarning("Scenario: could not write the job ledger: " + e.Message);
                return false;
            }
        }

        public JobRow AddIntent(string outputType, string modelId, string modelName, JObject parameters, string fingerprint,
            double quotedCu, double now, string prompt = "", string origin = "window")
        {
            _counter++;
            var row = new JobRow
            {
                LocalId = $"local_{(long)(now * 1000)}_{_counter}",
                State = JobState.Submitting,
                OutputType = outputType ?? "",
                ModelId = modelId ?? "",
                ModelName = modelName ?? "",
                Prompt = string.IsNullOrEmpty(prompt) ? Json.Str(parameters, "prompt") : prompt,
                Parameters = (JObject)(parameters ?? new JObject()).DeepClone(),
                Fingerprint = fingerprint ?? "",
                QuotedCu = quotedCu,
                Origin = origin ?? "window",
                CreatedAt = now,
                UpdatedAt = now,
            };
            Jobs.Insert(0, row);
            Trim();
            Save();
            return row;
        }

        public void MarkSubmitted(string localId, string jobId, string serverStatus, double now)
        {
            var row = Find(localId);
            if (row == null) return;
            row.JobId = jobId;
            row.State = JobState.ForStatus(serverStatus, "");
            row.UpdatedAt = now;
            Save();
        }

        /// <summary>Scenario answered with a clear refusal: nothing ran, nothing was charged.</summary>
        public void MarkRejected(string localId, string message, double now) => SetState(localId, JobState.Rejected, message, now);

        public void MarkUnknown(string localId, string message, double now) => SetState(localId, JobState.Unknown, message, now);

        public void MarkCancelRequested(string localId, double now)
        {
            var row = Find(localId);
            if (row != null && JobState.Active.Contains(row.State)) SetState(localId, JobState.CancelRequested, "", now);
        }

        public void Dismiss(string localId, double now) => SetState(localId, JobState.Dismissed, "", now);

        /// <summary>A paid call that answers at once (Prompt Spark): done and charged, nothing to import.</summary>
        public void MarkCharged(string localId, double cu, double now)
        {
            var row = Find(localId);
            if (row == null) return;
            row.State = JobState.Success;
            row.ChargedCu = cu;
            row.Imported = true;
            row.UpdatedAt = now;
            Save();
        }

        public void MarkImported(string localId, List<string> files, double now)
        {
            var row = Find(localId);
            if (row == null) return;
            row.Files = files ?? new List<string>();
            row.Imported = true;
            row.UpdatedAt = now;
            Save();
        }

        public void SetError(string localId, string message, double now)
        {
            var row = Find(localId);
            if (row == null) return;
            row.Error = message ?? "";
            row.UpdatedAt = now;
            Save();
        }

        /// <summary>Applies a compact job row from job_get. Returns the ledger row or null.</summary>
        public JobRow ApplyJob(JObject job, double now)
        {
            var jobId = Json.Str(job, "jobId");
            if (jobId.Length == 0) jobId = Json.Str(job, "id");
            var row = FindByJob(jobId);
            if (row == null) return null;
            var errorText = Json.Str(job, "error");
            if (errorText.Length == 0 && job["failure"] != null) errorText = Json.Str(job, "failure");
            var state = JobState.ForStatus(Json.Str(job, "status"), errorText);
            if (row.State == JobState.CancelRequested && (state == JobState.Queued || state == JobState.Running))
                state = JobState.CancelRequested; // the status changes only when Scenario reports it
            row.State = state;
            if (job["assetIds"] is JArray assets && assets.Count > 0) row.AssetIds = assets.Select(a => a.ToString()).ToList();
            double? cu = job["cuCost"] != null ? Json.Num(job, "cuCost")
                : job["billing"] is JObject billing && billing["cuCost"] != null ? Json.Num(billing, "cuCost") : (double?)null;
            // A workflow job reports 0 itself; its steps carry the cost. Keep the quote then.
            if (cu.HasValue && (cu.Value > 0 || row.WorkflowId.Length == 0)) row.ChargedCu = cu;
            if (job["progress"] != null) row.Progress = (float)Json.Num(job, "progress");
            if (errorText.Length > 0) row.Error = errorText;
            row.UpdatedAt = now;
            Save();
            return row;
        }

        /// <summary>
        /// Links unresolved rows to jobs found on Scenario. Returns how many were linked.
        /// Only an unambiguous match (exactly one untracked job of the same model in the
        /// window after the intent) is linked; anything else stays for the user.
        /// </summary>
        public int Reconcile(JArray serverJobs, double now)
        {
            var linked = 0;
            foreach (var row in Jobs)
            {
                if (!row.IsUnresolved) continue;
                var candidates = new List<JObject>();
                foreach (var token in serverJobs)
                {
                    if (!(token is JObject job)) continue;
                    var same = row.WorkflowId.Length > 0
                        ? Json.Str(job, "workflowId") == row.WorkflowId
                        : row.ModelId.Length > 0 && Json.Str(job, "modelId") == row.ModelId;
                    if (!same) continue;
                    var jobId = Json.Str(job, "jobId");
                    if (jobId.Length == 0 || FindByJob(jobId) != null) continue;
                    var created = Json.IsoToUnix(Json.Str(job, "createdAt"));
                    if (created < 0) continue;
                    var delta = created - row.CreatedAt;
                    if (delta >= -5 && delta <= ReconcileWindowSeconds) candidates.Add(job);
                }
                if (candidates.Count == 1)
                {
                    row.JobId = Json.Str(candidates[0], "jobId");
                    row.Error = "";
                    row.State = JobState.ForStatus(Json.Str(candidates[0], "status"), Json.Str(candidates[0], "error"));
                    row.UpdatedAt = now;
                    linked++;
                }
            }
            if (linked > 0) Save();
            return linked;
        }

        public JobRow Find(string localId) => Jobs.FirstOrDefault(r => r.LocalId == localId);

        public JobRow FindByJob(string jobId) => string.IsNullOrEmpty(jobId) ? null : Jobs.FirstOrDefault(r => r.JobId == jobId);

        public List<JobRow> Active() => Jobs.Where(r => r.IsActive).ToList();

        public List<JobRow> Unresolved() => Jobs.Where(r => r.IsUnresolved).ToList();

        /// <summary>True when an unresolved row holds the same request: sending it again could pay twice.</summary>
        public bool Blocks(string fingerprint) => Jobs.Any(r => r.IsUnresolved && r.Fingerprint == fingerprint);

        void SetState(string localId, string state, string message, double now)
        {
            var row = Find(localId);
            if (row == null) return;
            row.State = state;
            row.Error = message ?? "";
            row.UpdatedAt = now;
            Save();
        }

        void Trim()
        {
            while (Jobs.Count > MaxRows)
            {
                var index = Jobs.FindLastIndex(r => r.IsFinal);
                if (index < 0) return;
                Jobs.RemoveAt(index);
            }
        }
    }
}
