using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    public enum SignInState
    {
        SignedOut,
        SigningIn,
        SignedIn,
    }

    public sealed class ProjectInfo
    {
        public string Id = "";
        public string Name = "";
    }

    public sealed class TeamInfo
    {
        public string Id = "";
        public string Name = "";
        public string Plan = "";
        public List<ProjectInfo> Projects = new List<ProjectInfo>();
    }

    /// <summary>One generation request from the window, the agent or the C# API.</summary>
    public sealed class GenerateRequest
    {
        public string OutputType = OutputTypes.Image;
        public string ModelId = "";
        public string ModelName = "";
        /// <summary>Set to run a workflow instead of a model (its inputs go in Payload).</summary>
        public string WorkflowId = "";
        public JObject Payload = new JObject();
        public string Prompt = "";
        /// <summary>Place the result in the scene when it is imported.</summary>
        public bool Place = true;
        /// <summary>Apply to this object instead of creating a new one.</summary>
        public GameObject Target;
        public string Origin = "window";
        public string Folder = "";
    }

    /// <summary>
    /// The plugin's single brain: sign-in, workspace, catalog, price checks,
    /// generation, job tracking, import and placement. Main thread only.
    /// Every driver (window, agent, menus, C# API) goes through it, so the money
    /// contract holds everywhere.
    /// </summary>
    public sealed class ScenarioService
    {
        const string AccessTokenKey = "Scenario.AccessToken";
        const string AccessExpiryKey = "Scenario.AccessExpiry";
        const string LedgerPath = "Library/Scenario/jobs.json";
        const string CatalogPath = "Library/Scenario/catalog.json";

        static ScenarioService _instance;
        public static ScenarioService Instance => _instance ?? (_instance = new ScenarioService());
        public static bool Exists => _instance != null;

        public readonly McpClient Client;
        public readonly CredentialStore Credentials;
        public readonly JobLedger Ledger;
        public readonly Catalog Catalog;
        public readonly QuoteBook Quotes = new QuoteBook();
        readonly OAuthClient _oauth;

        public SignInState State { get; private set; }
        public AuthMethod Method => Client.Auth.Method;
        public string AccountLabel => Client.Auth.Label;
        public string SignInError { get; private set; } = "";
        public List<TeamInfo> Teams { get; private set; } = new List<TeamInfo>();
        public bool TeamsLoading { get; private set; }
        public bool CatalogLoading { get; private set; }
        public string CatalogError { get; private set; } = "";
        /// <summary>A price check returned a job: automatic price checks stop for this session.</summary>
        public bool EstimateAlarm { get; private set; }

        /// <summary>Anything the UI shows changed.</summary>
        public event Action Changed;
        /// <summary>A job reached a final state (imported, failed, blocked or canceled).</summary>
        public event Action<JobRow> JobFinished;
        /// <summary>(message, level): info, success, warning, error.</summary>
        public event Action<string, string> Notified;

        CancellationTokenSource _signIn;
        readonly Dictionary<string, JObject> _schemas = new Dictionary<string, JObject>();
        readonly Dictionary<string, JObject> _models = new Dictionary<string, JObject>();
        readonly Dictionary<string, double> _nextPoll = new Dictionary<string, double>();
        readonly Dictionary<string, double> _pollInterval = new Dictionary<string, double>();
        readonly HashSet<string> _inFlight = new HashSet<string>();
        readonly HashSet<string> _importing = new HashSet<string>();
        double _nextReconcile;

        ScenarioService() : this(new HttpTransport(), CredentialStore.ForUser(), LedgerPath, CatalogPath, readEnvironment: true)
        {
        }

        ScenarioService(ITransport transport, CredentialStore credentials, string ledgerPath, string catalogPath, bool readEnvironment)
        {
            Client = new McpClient(transport);
            Credentials = credentials;
            Ledger = new JobLedger(ledgerPath);
            Ledger.Load();
            Ledger.Changed += () => Changed?.Invoke();
            Catalog = new Catalog(catalogPath);
            Catalog.Load();
            _oauth = new OAuthClient(Client.Transport);
            Client.SpendingDisabled = Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") == "1" || ScenarioUserSettings.instance.SpendingDisabled;
            Restore(readEnvironment);
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () => EditorApplication.update -= Tick;
            ResumeImports();
        }

        /// <summary>
        /// Tests: replaces the shared service with one on this transport, with its ledger,
        /// catalog and credentials in a temp folder and no environment credentials.
        /// </summary>
        internal static ScenarioService UseForTests(ITransport transport, string folder)
        {
            ResetForTests();
            Uploader.CachePath = System.IO.Path.Combine(folder, "uploads.json");
            var credentials = new CredentialStore(System.IO.Path.Combine(folder, "credentials.json"), new MemorySecretStore());
            _instance = new ScenarioService(transport, credentials, System.IO.Path.Combine(folder, "jobs.json"), System.IO.Path.Combine(folder, "catalog.json"), readEnvironment: false);
            return _instance;
        }

        /// <summary>Tests: drops the shared service; the next use builds the real one.</summary>
        internal static void ResetForTests()
        {
            Uploader.ResetCachePath();
            if (_instance == null) return;
            EditorApplication.update -= _instance.Tick;
            _instance = null;
        }

        void Emit() => Changed?.Invoke();

        public void Notify(string message, string level = "info")
        {
            Notified?.Invoke(message, level);
            if (Notified == null || Application.isBatchMode)
            {
                if (level == "error") Debug.LogWarning("Scenario: " + message);
                else Debug.Log("Scenario: " + message);
            }
        }

        // --- Sign-in ---------------------------------------------------------

        void Restore(bool readEnvironment)
        {
            var env = readEnvironment ? CredentialStore.FromEnvironment() : null;
            if (env != null)
            {
                Client.SetAuth(new ApiKeyAuth(env.ApiKey, env.Secret, fromEnvironment: true));
                State = SignInState.SignedIn;
            }
            else
            {
                var saved = Credentials.Load();
                if (saved.Method == "apikey")
                {
                    Client.SetAuth(new ApiKeyAuth(saved.ApiKey, saved.Secret));
                    State = SignInState.SignedIn;
                }
                else if (saved.Method == "oauth")
                {
                    var tokens = new OAuthTokens
                    {
                        RefreshToken = saved.Secret,
                        AccessToken = SessionState.GetString(AccessTokenKey, ""),
                        ExpiresAt = SessionState.GetFloat(AccessExpiryKey, 0),
                    };
                    UseOAuth(Json.Str(saved.Info, "client_id"), tokens, Json.Str(saved.Info, "email"));
                    State = SignInState.SignedIn;
                }
            }
            var user = ScenarioUserSettings.instance;
            Client.SetScope(user.TeamId, user.ProjectId);
            // An API key sees only its own project: read its name for the header (free).
            if (Client.Auth is ApiKeyAuth) EditorApplication.delayCall += () => _ = LoadTeams();
        }

        void UseOAuth(string clientId, OAuthTokens tokens, string email)
        {
            var auth = new OAuthAuth(_oauth, clientId, tokens, email);
            auth.TokensChanged += rotated => MainThread.Post(() => SaveTokens(clientId, rotated, email));
            Client.SetAuth(auth);
            RememberAccess(tokens);
        }

        void SaveTokens(string clientId, OAuthTokens tokens, string email)
        {
            RememberAccess(tokens);
            var saved = Credentials.Load();
            var info = saved.Info ?? new JObject();
            info["client_id"] = clientId;
            info["email"] = email;
            Credentials.SaveRefreshToken(tokens.RefreshToken, info);
        }

        static void RememberAccess(OAuthTokens tokens)
        {
            // Session state lives in memory only: the access token never touches disk.
            SessionState.SetString(AccessTokenKey, tokens.AccessToken ?? "");
            SessionState.SetFloat(AccessExpiryKey, (float)tokens.ExpiresAt);
        }

        public bool IsSignedIn => State == SignInState.SignedIn && Client.HasCredentials;
        public bool NeedsWorkspace => IsSignedIn && Client.NeedsWorkspace;
        public bool Ready => IsSignedIn && !Client.NeedsWorkspace;
        public bool FromEnvironment => Method == AuthMethod.Environment;

        /// <summary>Opens a page in the user's browser. Tests replace it.</summary>
        internal static Action<string> OpenUrl = Application.OpenURL;

        public async Task SignInWithBrowser()
        {
            CancelSignIn();
            var signIn = new CancellationTokenSource();
            _signIn = signIn;
            State = SignInState.SigningIn;
            SignInError = "";
            Emit();
            Result<OAuthSession> result;
            try
            {
                var saved = Credentials.Load();
                // The sign-in resumes on worker threads after its network calls, and Unity opens URLs only from the main thread.
                result = await OAuthSignIn.Run(_oauth, url => MainThread.Post(() => OpenUrl(url)), Json.Str(saved.Info, "client_id"), Json.Str(saved.Info, "redirect_uri"),
                    TimeSpan.FromMinutes(5), signIn.Token);
            }
            catch (Exception e)
            {
                result = Result<OAuthSession>.Fail(ErrorKind.Local, "The sign-in stopped: " + e.Message);
            }
            // Canceled, or a newer sign-in started: that one owns the panel now.
            if (_signIn != signIn) return;
            _signIn = null;
            if (!result.Ok)
            {
                State = Client.HasCredentials ? SignInState.SignedIn : SignInState.SignedOut;
                SignInError = result.Error.Kind == ErrorKind.Canceled ? "" : result.Error.Message;
                Emit();
                return;
            }
            var session = result.Value;
            Credentials.SaveRefreshToken(session.Tokens.RefreshToken, session.Info());
            UseOAuth(session.ClientId, session.Tokens, session.Email);
            State = SignInState.SignedIn;
            Quotes.Clear();
            Emit();
            Notify("Signed in to Scenario" + (session.Email.Length > 0 ? " as " + session.Email : "") + ".", "success");
            await LoadTeams(autoPick: true);
            _ = RefreshCatalog(false);
        }

        public void CancelSignIn()
        {
            _signIn?.Cancel();
            _signIn = null;
            if (State != SignInState.SigningIn) return;
            State = Client.HasCredentials ? SignInState.SignedIn : SignInState.SignedOut;
            SignInError = "";
            Emit();
        }

        /// <summary>Checks the key with a free read before saving it.</summary>
        public async Task<bool> SignInWithApiKey(string key, string secret)
        {
            SignInError = "";
            var auth = new ApiKeyAuth(key, secret);
            if (!auth.HasCredentials)
            {
                SignInError = "Enter both the API key and the secret.";
                Emit();
                return false;
            }
            State = SignInState.SigningIn;
            Emit();
            Client.SetAuth(auth);
            Client.SetScope("", "");
            var teams = await Client.ListTeams();
            if (!teams.Ok)
            {
                Client.SetAuth(new NoAuth());
                State = SignInState.SignedOut;
                SignInError = teams.Error.Kind == ErrorKind.Auth ? "Scenario did not accept this key and secret." : teams.Error.Message;
                Emit();
                return false;
            }
            var store = Credentials.SaveApiKey(key, secret);
            if (store.Length == 0) Notify("Signed in for this session only: the key could not be saved.", "warning");
            State = SignInState.SignedIn;
            ApplyTeams(teams.Value, autoPick: true);
            Quotes.Clear();
            Emit();
            Notify("Signed in with an API key.", "success");
            _ = RefreshCatalog(false);
            return true;
        }

        public void SignOut()
        {
            if (Client.Auth is OAuthAuth oauth)
            {
                var token = oauth.Tokens.RefreshToken;
                _ = _oauth.Revoke(oauth.ClientId, token);
            }
            Credentials.Clear();
            SessionState.EraseString(AccessTokenKey);
            Client.SetAuth(new NoAuth());
            Client.SetScope("", "");
            var user = ScenarioUserSettings.instance;
            user.TeamId = user.ProjectId = user.TeamName = user.ProjectName = user.Plan = "";
            user.Save();
            Teams = new List<TeamInfo>();
            Quotes.Clear();
            State = SignInState.SignedOut;
            Emit();
        }

        // --- Workspace -------------------------------------------------------

        public async Task LoadTeams(bool autoPick = false)
        {
            if (!IsSignedIn) return;
            TeamsLoading = true;
            Emit();
            var teams = await Client.ListTeams();
            TeamsLoading = false;
            if (!teams.Ok)
            {
                if (teams.Error.Kind == ErrorKind.Auth && Client.Auth is OAuthAuth) ExpireSession();
                else Notify(teams.Error.Message, "error");
                Emit();
                return;
            }
            ApplyTeams(teams.Value, autoPick);
            Emit();
        }

        void ExpireSession()
        {
            Credentials.Clear();
            Client.SetAuth(new NoAuth());
            State = SignInState.SignedOut;
            SignInError = "Your Scenario sign-in has expired. Sign in again.";
        }

        void ApplyTeams(JObject value, bool autoPick)
        {
            Teams = new List<TeamInfo>();
            foreach (var team in (value["teams"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var info = new TeamInfo { Id = Json.Str(team, "id"), Name = Json.Str(team, "name"), Plan = Json.Str(team, "plan") };
                foreach (var project in (team["projects"] as JArray ?? new JArray()).OfType<JObject>())
                    info.Projects.Add(new ProjectInfo { Id = Json.Str(project, "id"), Name = Json.Str(project, "name") });
                Teams.Add(info);
            }
            var user = ScenarioUserSettings.instance;
            var current = Teams.FirstOrDefault(t => t.Id == user.TeamId);
            if (current != null && current.Projects.Any(p => p.Id == user.ProjectId))
            {
                SelectWorkspace(current.Id, user.ProjectId, quiet: true);
                return;
            }
            var all = Teams.SelectMany(t => t.Projects.Select(p => (t, p))).ToList();
            if (Client.Auth.NeedsScope)
            {
                if (autoPick && all.Count == 1) SelectWorkspace(all[0].t.Id, all[0].p.Id, quiet: true);
            }
            else if (all.Count >= 1)
            {
                // An API key is scoped to its own project; remember it for display.
                SelectWorkspace(all[0].t.Id, all[0].p.Id, quiet: true);
            }
        }

        public TeamInfo CurrentTeam => Teams.FirstOrDefault(t => t.Id == Client.TeamId);

        /// <summary>Web page of an asset: in the job's workspace when known, else the current one.</summary>
        public string AssetLink(string assetId, JobRow row = null) =>
            row != null && row.TeamId.Length > 0 ? Links.Asset(assetId, row.TeamId, row.ProjectId) : Links.Asset(assetId, Client.TeamId, Client.ProjectId);

        public string HomeLink => Links.Home(Client.TeamId, Client.ProjectId);

        public void SelectWorkspace(string teamId, string projectId, bool quiet = false)
        {
            var team = Teams.FirstOrDefault(t => t.Id == teamId);
            var project = team?.Projects.FirstOrDefault(p => p.Id == projectId);
            var user = ScenarioUserSettings.instance;
            var changed = user.TeamId != teamId || user.ProjectId != projectId;
            user.TeamId = teamId ?? "";
            user.ProjectId = projectId ?? "";
            user.TeamName = team?.Name ?? user.TeamName;
            user.ProjectName = project?.Name ?? user.ProjectName;
            user.Plan = team?.Plan ?? user.Plan;
            user.Save();
            if (Client.Auth.NeedsScope) Client.SetScope(teamId, projectId);
            else Client.SetScope(teamId, projectId); // informational for API keys: the key decides
            if (changed)
            {
                Quotes.Clear();
                _ = RefreshCatalog(true);
                if (!quiet) Notify($"Working in {user.ProjectName}.", "info");
            }
            Emit();
        }

        public string ScopeKey => $"{Client.TeamId}/{Client.ProjectId}";

        // --- Catalog and models ---------------------------------------------

        public async Task RefreshCatalog(bool force)
        {
            if (!Ready || CatalogLoading) return;
            if (!force && !Catalog.IsStale(Json.Now()) && Catalog.Scope == ScopeKey) return;
            CatalogLoading = true;
            CatalogError = "";
            Emit();
            var result = await Catalog.Refresh(Client, ScopeKey, Json.Now());
            CatalogLoading = false;
            if (!result.Ok) CatalogError = result.Error.Message;
            Emit();
        }

        public ModelInfo Model(string modelId) => Catalog.Find(modelId);

        public async Task<Result<JObject>> Schema(string modelId)
        {
            if (_schemas.TryGetValue(modelId, out var cached)) return Result<JObject>.Success(cached);
            var result = await Client.GetSchema(modelId);
            if (result.Ok) _schemas[modelId] = result.Value;
            return result;
        }

        public async Task<Result<JObject>> ModelDetails(string modelId)
        {
            if (_models.TryGetValue(modelId, out var cached)) return Result<JObject>.Success(cached);
            var result = await Client.GetModel(modelId);
            if (result.Ok)
            {
                var model = result.Value["model"] as JObject ?? result.Value;
                _models[modelId] = model;
                return Result<JObject>.Success(model);
            }
            return result;
        }

        // --- Price and generation ------------------------------------------

        public string Fingerprint(string modelId, JObject payload) => QuoteBook.Fingerprint(modelId, payload, ScopeKey);

        public string Fingerprint(GenerateRequest request) => string.IsNullOrEmpty(request.WorkflowId)
            ? Fingerprint(request.ModelId, request.Payload)
            : QuoteBook.Fingerprint("workflow:" + request.WorkflowId, request.Payload, ScopeKey);

        /// <summary>Free price check. The quote it stores is the only way to generate.</summary>
        public Task<Result<QuoteBook.Quote>> Estimate(string modelId, JObject payload)
            => Estimate(new GenerateRequest { ModelId = modelId, Payload = payload });

        public async Task<Result<QuoteBook.Quote>> Estimate(GenerateRequest request)
        {
            if (EstimateAlarm)
                return Result<QuoteBook.Quote>.Fail(ErrorKind.EstimateJob, "Price checks are paused: an earlier check returned a job. Check recent jobs, then restart Unity to resume.");
            var fingerprint = Fingerprint(request);
            var price = string.IsNullOrEmpty(request.WorkflowId)
                ? await Client.Estimate(request.ModelId, request.Payload)
                : await Client.EstimateWorkflow(request.WorkflowId, request.Payload);
            if (!price.Ok)
            {
                if (price.Error.Kind == ErrorKind.EstimateJob)
                {
                    EstimateAlarm = true;
                    Notify(price.Error.Message, "error");
                    Emit();
                }
                return price.Cast<QuoteBook.Quote>();
            }
            var now = Json.Now();
            Quotes.Store(fingerprint, price.Value.Cu, price.Value.Discount, now);
            return Result<QuoteBook.Quote>.Success(Quotes.Usable(fingerprint, now));
        }

        public QuoteBook.Quote QuoteFor(string modelId, JObject payload) => Quotes.Usable(Fingerprint(modelId, payload), Json.Now());

        public QuoteBook.Quote QuoteFor(GenerateRequest request) => Quotes.Usable(Fingerprint(request), Json.Now());

        /// <summary>Why a request cannot be sent right now, or "" when it can (quote aside).</summary>
        public string BlockReason(GenerateRequest request)
        {
            if (!IsSignedIn) return "Sign in to Scenario first.";
            if (!Ready) return "Pick a Scenario project first.";
            if (Client.SpendingDisabled) return "Spending is turned off in Scenario settings.";
            if (Ledger.Blocks(Fingerprint(request))) return "The same request is still unresolved: it may have started on Scenario. Check it in Jobs.";
            return "";
        }

        /// <summary>
        /// Paid. Needs a fresh quote for exactly this request; writes the intent to the
        /// ledger before sending; never retries. An unclear outcome becomes "unknown".
        /// </summary>
        public async Task<Result<JobRow>> Generate(GenerateRequest request)
        {
            if (!Ready) return Result<JobRow>.Fail(ErrorKind.Auth, IsSignedIn ? "Pick a Scenario project first." : "Sign in to Scenario first.", e => e.NotSent = true);
            var fingerprint = Fingerprint(request);
            var now = Json.Now();
            if (Quotes.Usable(fingerprint, now) == null)
                return Result<JobRow>.Fail(ErrorKind.Local, "Check the price first: the request changed since the last price check.", e => e.NotSent = true);
            if (Ledger.Blocks(fingerprint))
                return Result<JobRow>.Fail(ErrorKind.Local, "The same request is still unresolved: it may have started on Scenario. Check it in Jobs first.", e => e.NotSent = true);
            if (Client.SpendingDisabled)
                return Result<JobRow>.Fail(ErrorKind.Local, "Spending is turned off in Scenario settings, so nothing was sent.", e => e.NotSent = true);
            var quote = Quotes.Consume(fingerprint, now);
            var row = Ledger.AddIntent(request.OutputType, request.ModelId, request.ModelName, request.Payload, fingerprint, quote.Cu, now, request.Prompt, request.Origin);
            row.Place = request.Place;
            row.PlaceTarget = request.Target != null ? GlobalObjectId.GetGlobalObjectIdSlow(request.Target).ToString() : "";
            row.PlaceTargetPath = Placer.PathOf(request.Target);
            row.TeamId = Client.TeamId;
            row.ProjectId = Client.ProjectId;
            row.Folder = request.Folder ?? "";
            row.WorkflowId = request.WorkflowId ?? "";
            Ledger.Save();
            Emit();
            var submitted = string.IsNullOrEmpty(request.WorkflowId)
                ? await Client.Submit(request.ModelId, request.Payload)
                : await Client.SubmitWorkflow(request.WorkflowId, request.Payload);
            if (submitted.Ok)
            {
                Ledger.MarkSubmitted(row.LocalId, submitted.Value.JobId, submitted.Value.Status, Json.Now());
                SchedulePoll(row.LocalId, 2);
                return Result<JobRow>.Success(row);
            }
            if (submitted.Error.Ambiguous)
            {
                Ledger.MarkUnknown(row.LocalId, submitted.Error.Message, Json.Now());
                _nextReconcile = Json.Now() + 8;
            }
            else
            {
                Ledger.MarkRejected(row.LocalId, submitted.Error.Message, Json.Now());
            }
            return Result<JobRow>.Fail(submitted.Error);
        }

        public async Task Cancel(JobRow row)
        {
            if (row == null || row.JobId.Length == 0 || !row.IsActive) return;
            var result = await Client.CancelJob(row.JobId);
            if (result.Ok) Ledger.MarkCancelRequested(row.LocalId, Json.Now());
            else Notify(result.Error.Message, "error");
            SchedulePoll(row.LocalId, 1);
        }

        public void Dismiss(JobRow row)
        {
            if (row == null) return;
            Ledger.Dismiss(row.LocalId, Json.Now());
        }

        /// <summary>Looks for the Scenario job behind an unknown request.</summary>
        public async Task<int> CheckUnknown()
        {
            if (!Ready || Ledger.Unresolved().Count == 0) return 0;
            var jobs = await Client.ListJobs(50);
            if (!jobs.Ok) return 0;
            var linked = Ledger.Reconcile(jobs.Value, Json.Now());
            foreach (var row in Ledger.Jobs.Where(r => r.IsActive || (r.State == JobState.Success && !r.Imported)))
                SchedulePoll(row.LocalId, 0.5);
            if (linked > 0) Notify($"Found {linked} request{(linked == 1 ? "" : "s")} on Scenario.", "info");
            return linked;
        }

        // --- Polling and import ---------------------------------------------

        void SchedulePoll(string localId, double delay)
        {
            _nextPoll[localId] = Json.Now() + delay;
            if (!_pollInterval.ContainsKey(localId)) _pollInterval[localId] = 2;
        }

        void Tick()
        {
            if (!Ready) return;
            var now = Json.Now();
            foreach (var row in Ledger.Jobs.ToList())
            {
                if (!row.IsActive || _inFlight.Contains(row.LocalId)) continue;
                if (!_nextPoll.TryGetValue(row.LocalId, out var due)) _nextPoll[row.LocalId] = due = now;
                if (now < due || _inFlight.Count >= 4) continue;
                _ = Poll(row);
            }
            if (_nextReconcile > 0 && now >= _nextReconcile)
            {
                _nextReconcile = 0;
                _ = CheckUnknown();
            }
        }

        async Task Poll(JobRow row)
        {
            _inFlight.Add(row.LocalId);
            try
            {
                var job = await Client.GetJob(row.JobId);
                var interval = _pollInterval.TryGetValue(row.LocalId, out var i) ? i : 2;
                _pollInterval[row.LocalId] = Math.Min(10, interval * 1.4);
                _nextPoll[row.LocalId] = Json.Now() + interval;
                if (!job.Ok)
                {
                    if (job.Error.Kind == ErrorKind.Auth && Client.Auth is OAuthAuth) ExpireSession();
                    return;
                }
                Ledger.ApplyJob(job.Value, Json.Now());
                if (row.State == JobState.Success) await ImportRow(row);
                else if (row.IsFinal) Finish(row);
            }
            finally
            {
                _inFlight.Remove(row.LocalId);
            }
        }

        void Finish(JobRow row)
        {
            _nextPoll.Remove(row.LocalId);
            _pollInterval.Remove(row.LocalId);
            switch (row.State)
            {
                case JobState.Failed:
                    Notify($"{row.ModelName} failed: {Shorten(row.Error)}", "error");
                    break;
                case JobState.Blocked:
                    Notify($"{row.ModelName}: blocked by content moderation.", "warning");
                    break;
                case JobState.Canceled:
                    Notify($"{row.ModelName}: canceled.", "info");
                    break;
            }
            JobFinished?.Invoke(row);
            Emit();
        }

        static string Shorten(string text) => string.IsNullOrEmpty(text) ? "no details" : text.Length > 160 ? text.Substring(0, 160) + "..." : text;

        /// <summary>Downloads, imports and places a finished job's results.</summary>
        public async Task<bool> ImportRow(JobRow row)
        {
            if (row.Imported || row.AssetIds.Count == 0 || !_importing.Add(row.LocalId)) return false;
            try
            {
                var context = new ImportContext
                {
                    OutputType = row.OutputType,
                    Prompt = row.Prompt,
                    ModelId = row.ModelId,
                    ModelName = row.ModelName,
                    JobId = row.JobId,
                    Parameters = row.Parameters,
                    Folder = row.Folder,
                    Cu = row.ChargedCu ?? row.QuotedCu,
                };
                var imported = await Importer.Import(Client, row.AssetIds, context);
                if (!imported.Ok)
                {
                    Ledger.SetError(row.LocalId, "Import failed: " + imported.Error.Message, Json.Now());
                    Notify($"{row.ModelName} finished, but importing failed: {imported.Error.Message}", "error");
                    return false;
                }
                Ledger.MarkImported(row.LocalId, imported.Value.Select(f => f.Path).ToList(), Json.Now());
                if (row.Place)
                {
                    var target = ResolveTarget(row);
                    var placed = Placer.Place(row.OutputType, imported.Value, row.Prompt, target, create: true);
                    row.Placed = placed.Objects.Select(Placer.PathOf).ToList();
                    if (placed.Message.Length > 0) Notify(placed.Message, placed.MessageKind);
                    Ledger.Save();
                }
                Finish(row);
                Notify($"{Files.ObjectName(row.Prompt, row.ModelName)} is ready.", "success");
                return true;
            }
            finally
            {
                _importing.Remove(row.LocalId);
            }
        }

        /// <summary>The object a job places on: by GlobalObjectId, else by hierarchy path (objects in an unsaved scene have no usable GlobalObjectId).</summary>
        internal static GameObject ResolveTarget(JobRow row)
        {
            if (!string.IsNullOrEmpty(row.PlaceTarget) && GlobalObjectId.TryParse(row.PlaceTarget, out var id)
                && GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is GameObject go)
                return go;
            return Placer.FindByPath(row.PlaceTargetPath);
        }

        /// <summary>After a domain reload: finished jobs that were not imported yet, from the last hour.</summary>
        void ResumeImports()
        {
            var now = Json.Now();
            foreach (var row in Ledger.Jobs.Where(r => r.State == JobState.Success && !r.Imported && r.AssetIds.Count > 0 && now - r.UpdatedAt < 3600))
                EditorApplication.delayCall += () => { if (Ready) _ = ImportRow(row); };
            if (Ledger.Unresolved().Count > 0) _nextReconcile = now + 3;
        }

        /// <summary>Imports Scenario assets that did not come from a job here (Library, agent).</summary>
        public async Task<Result<List<ImportedFile>>> ImportAssets(IList<string> assetIds, string outputType, string prompt, bool place, GameObject target)
        {
            var imported = await Importer.Import(Client, assetIds, new ImportContext { OutputType = outputType, Prompt = prompt });
            if (imported.Ok && place)
            {
                var placed = Placer.Place(outputType, imported.Value, prompt, target, create: true);
                if (placed.Message.Length > 0) Notify(placed.Message, placed.MessageKind);
            }
            return imported;
        }

        public Task<Result<string>> Upload(UnityEngine.Object asset) => Uploader.UploadAsset(Client, asset);

        public Task<Result<string>> UploadPath(string path) => Uploader.UploadFile(Client, path);

        /// <summary>The same 3D asset in another format (free), for inputs that refuse GLB.</summary>
        public Task<Result<string>> Convert(string assetId, string format) => Uploader.Convert(Client, assetId, format);

        // --- Suggestions and Prompt Spark -------------------------------------

        /// <summary>Free: Scenario's ranked models for a capability and prompt.</summary>
        public Task<Result<JObject>> Recommend(string capability, string prompt, int limit = 3) => Client.Recommend(capability, prompt, limit);

        readonly Dictionary<string, double> _sparkPrices = new Dictionary<string, double>();

        /// <summary>Free: what one Prompt Spark run costs for this model (cached for the session).</summary>
        public async Task<Result<double>> PromptSparkPrice(string modelId)
        {
            if (_sparkPrices.TryGetValue(modelId, out var known)) return Result<double>.Success(known);
            var dry = await Client.PromptSpark(modelId, "", null, 1, dryRun: true);
            if (!dry.Ok) return dry.Cast<double>();
            if (ReturnedJob(dry.Value)) return Result<double>.Fail(ErrorKind.EstimateJob, "Prompt Spark returned a job on a price check.");
            var cu = Json.Num(dry.Value, "creativeUnitsCost", -1);
            if (cu < 0) return Result<double>.Fail(ErrorKind.Protocol, "Scenario returned no price for Prompt Spark.");
            _sparkPrices[modelId] = cu;
            return Result<double>.Success(cu);
        }

        static bool ReturnedJob(JObject value) => McpClient.CreatedJobId(value).Length > 0;

        /// <summary>
        /// Paid (a few CU): a better prompt for this model. Same rules as a generation:
        /// the price is known first, the intent is saved, the call is never retried.
        /// </summary>
        public async Task<Result<string>> ImprovePrompt(string modelId, string modelName, string prompt, string origin = "window")
        {
            var reason = !Ready ? "Sign in and pick a project first." : Client.SpendingDisabled ? "Spending is turned off in Scenario settings." : "";
            if (reason.Length > 0) return Result<string>.Fail(ErrorKind.Local, reason, e => e.NotSent = true);
            var price = await PromptSparkPrice(modelId);
            if (!price.Ok) return price.Cast<string>();
            var parameters = new JObject { ["prompt"] = prompt ?? "" };
            var fingerprint = QuoteBook.Fingerprint("prompt_spark:" + modelId, parameters, ScopeKey);
            if (Ledger.Blocks(fingerprint))
                return Result<string>.Fail(ErrorKind.Local, "The same Prompt Spark request is still unresolved. Check it in Jobs.", e => e.NotSent = true);
            var row = Ledger.AddIntent("prompt", "", "Prompt Spark · " + modelName, parameters, fingerprint, price.Value, Json.Now(), prompt, origin);
            Emit();
            var result = await Client.PromptSpark(modelId, prompt, null, 1);
            if (!result.Ok)
            {
                if (result.Error.Ambiguous) Ledger.MarkUnknown(row.LocalId, result.Error.Message, Json.Now());
                else Ledger.MarkRejected(row.LocalId, result.Error.Message, Json.Now());
                Emit();
                return result.Cast<string>();
            }
            var charged = Json.Num(result.Value, "creativeUnitsCost", price.Value);
            Ledger.MarkCharged(row.LocalId, charged, Json.Now());
            Emit();
            var improved = Json.Str(result.Value, "recommended_prompt");
            if (improved.Length == 0 && result.Value["prompts"] is JArray prompts && prompts.Count > 0)
                improved = prompts[0] is JObject first ? Json.Str(first, "prompt") : prompts[0].ToString();
            return improved.Length > 0 ? Result<string>.Success(improved) : Result<string>.Fail(ErrorKind.Protocol, "Prompt Spark returned no prompt.");
        }

        // --- Spend ----------------------------------------------------------

        /// <summary>Creative Units charged (or quoted, while running) today, from this project's ledger.</summary>
        public double SpentToday()
        {
            var start = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
            return Ledger.Jobs
                .Where(r => r.CreatedAt >= start && r.State != JobState.Rejected && r.State != JobState.Canceled && (r.JobId.Length > 0 || r.ChargedCu.HasValue))
                .Sum(r => r.ChargedCu ?? r.QuotedCu);
        }

        public void SetSpendingDisabled(bool disabled)
        {
            ScenarioUserSettings.instance.SpendingDisabled = disabled;
            ScenarioUserSettings.instance.Save();
            Client.SpendingDisabled = disabled || Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") == "1";
            Emit();
        }
    }

    /// <summary>Starts the service after a domain reload when jobs are pending or the agent server is on.</summary>
    [InitializeOnLoad]
    static class ScenarioServiceBootstrap
    {
        static ScenarioServiceBootstrap()
        {
            if (Application.isBatchMode && Environment.GetEnvironmentVariable("SCENARIO_UNITY_SERVICE") != "1") return;
            EditorApplication.delayCall += () =>
            {
                var ledger = new JobLedger("Library/Scenario/jobs.json");
                ledger.Load();
                var pending = ledger.Jobs.Any(r => r.IsActive || r.IsUnresolved || (r.State == JobState.Success && !r.Imported));
                if (pending || ScenarioUserSettings.instance.AgentEnabled) _ = ScenarioService.Instance;
            };
        }
    }
}
