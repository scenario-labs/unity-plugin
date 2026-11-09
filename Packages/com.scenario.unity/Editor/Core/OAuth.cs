using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    public sealed class OAuthTokens
    {
        public string AccessToken = "";
        public string RefreshToken = "";
        /// <summary>Unix seconds.</summary>
        public double ExpiresAt;

        public bool IsFresh(double now) => AccessToken.Length > 0 && now < ExpiresAt - 60;
    }

    /// <summary>
    /// Scenario's identity provider and the registered OAuth application Unity signs in
    /// as: a public client (no secret ships in the plugin) whose one redirect URI,
    /// http://127.0.0.1/callback, the provider matches without the port. Endpoints
    /// checked against /.well-known/oauth-authorization-server on 2026-11-09.
    /// </summary>
    public sealed class OAuthApp
    {
        public const string ProductionIssuer = "https://clerk.scenario.com";
        /// <summary>"Scenario for Unity" on the production identity provider. A public client id, not a secret.</summary>
        public const string ProductionClientId = "7KFTIhEklYR5rU4Q";
        public const string IssuerVariable = "SCENARIO_OAUTH_ISSUER";
        public const string ClientIdVariable = "SCENARIO_OAUTH_CLIENT_ID";

        public static readonly OAuthApp Production = new OAuthApp(ProductionIssuer, ProductionClientId);

        public readonly string Issuer;
        public readonly string ClientId;

        public OAuthApp(string issuer, string clientId)
        {
            Issuer = (issuer ?? "").Trim().TrimEnd('/');
            ClientId = (clientId ?? "").Trim();
        }

        public string AuthorizationEndpoint => Issuer + "/oauth/authorize";
        public string TokenEndpoint => Issuer + "/oauth/token";
        public string RevocationEndpoint => Issuer + "/oauth/token/revoke";
        public string UserInfoEndpoint => Issuer + "/oauth/userinfo";
        public string DeviceAuthorizationEndpoint => Issuer + "/oauth/device_authorization";

        /// <summary>
        /// Production, or the development provider and application named by both
        /// SCENARIO_OAUTH_ISSUER (HTTPS) and SCENARIO_OAUTH_CLIENT_ID.
        /// </summary>
        public static Result<OAuthApp> Select(Func<string, string> environment)
        {
            var issuer = (environment(IssuerVariable) ?? "").Trim();
            var clientId = (environment(ClientIdVariable) ?? "").Trim();
            if (issuer.Length == 0 && clientId.Length == 0) return Result<OAuthApp>.Success(Production);
            if (issuer.Length == 0 || clientId.Length == 0 || !issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Result<OAuthApp>.Fail(ErrorKind.Local, $"Set {IssuerVariable} (HTTPS) and {ClientIdVariable} together, or neither.");
            return Result<OAuthApp>.Success(new OAuthApp(issuer, clientId));
        }
    }

    /// <summary>A code to enter on another device (RFC 8628), while Unity polls for the approval.</summary>
    public sealed class DeviceCode
    {
        public string Code = "";
        public string UserCode = "";
        public string VerificationUri = "";
        /// <summary>The page with the code already filled in, when the provider sends one.</summary>
        public string VerificationUriComplete = "";
        /// <summary>Seconds between polls.</summary>
        public double Interval = 5;
        /// <summary>Unix seconds.</summary>
        public double ExpiresAt;

        public string PageToOpen => VerificationUriComplete.Length > 0 ? VerificationUriComplete : VerificationUri;
    }

    /// <summary>
    /// "Sign in with Scenario": OAuth 2.1 authorization code with PKCE against Scenario's
    /// identity provider, as Unity's registered application, with a loopback redirect; or the
    /// device authorization grant when the browser cannot reach 127.0.0.1.
    /// </summary>
    public sealed class OAuthClient
    {
        /// <summary>The account chip shows the email and name, and every session needs a refresh token.</summary>
        public string Scope = "email offline_access profile";
        public readonly OAuthApp App;

        readonly ITransport _transport;

        public OAuthClient(ITransport transport, OAuthApp app)
        {
            _transport = transport;
            App = app;
        }

        // --- PKCE -----------------------------------------------------------

        public static string RandomToken(int bytes = 32)
        {
            var buffer = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buffer);
            return Base64Url(buffer);
        }

        public static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create()) return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // --- Endpoints ------------------------------------------------------

        public string AuthorizeUrl(string redirectUri, string state, string challenge)
        {
            var query = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = App.ClientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = Scope,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            };
            var parts = new List<string>();
            foreach (var pair in query) parts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
            return App.AuthorizationEndpoint + "?" + string.Join("&", parts);
        }

        public Task<Result<OAuthTokens>> Exchange(string code, string redirectUri, string verifier, double now, CancellationToken cancel = default)
            => Token(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = App.ClientId,
                ["code_verifier"] = verifier,
            }, now, "", cancel);

        public Task<Result<OAuthTokens>> Refresh(string refreshToken, double now, CancellationToken cancel = default)
            => Token(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = App.ClientId,
            }, now, refreshToken, cancel);

        async Task<Result<OAuthTokens>> Token(Dictionary<string, string> form, double now, string previousRefresh, CancellationToken cancel)
        {
            var reply = await _transport.SendAsync(HttpRequests.FormPost(App.TokenEndpoint, form, 30), cancel);
            return Tokens(reply, Json.ParseObject(reply.Body), now, previousRefresh);
        }

        public const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

        /// <summary>Asks the provider for a code the person enters on another device.</summary>
        public async Task<Result<DeviceCode>> StartDevice(double now, CancellationToken cancel = default)
        {
            var form = new Dictionary<string, string> { ["client_id"] = App.ClientId, ["scope"] = Scope };
            var reply = await _transport.SendAsync(HttpRequests.FormPost(App.DeviceAuthorizationEndpoint, form, 30), cancel);
            var data = Json.ParseObject(reply.Body);
            var device = new DeviceCode
            {
                Code = Json.Str(data, "device_code"),
                UserCode = Json.Str(data, "user_code"),
                VerificationUri = Json.Str(data, "verification_uri"),
                VerificationUriComplete = Json.Str(data, "verification_uri_complete"),
                Interval = Math.Max(1, Json.Num(data, "interval", 5)),
                ExpiresAt = now + Json.Num(data, "expires_in", 600),
            };
            if (!reply.IsSuccess || device.Code.Length == 0 || device.UserCode.Length == 0)
            {
                // The device grant is switched on per application: a 4xx means this one may not use it.
                var refused = reply.Answered && reply.Status >= 400 && reply.Status < 500 && reply.Status != 429;
                return Result<DeviceCode>.Fail(KindFor(reply), refused
                    ? "Sign-in on another device is not available for Unity yet. Sign in with the browser, or use an API key."
                    : "Sign-in on another device could not start: " + Describe(reply, data));
            }
            if (!IsHttps(device.VerificationUri))
                return Result<DeviceCode>.Fail(ErrorKind.Invalid, "Sign-in on another device sent a page that is not HTTPS.");
            if (!IsHttps(device.VerificationUriComplete)) device.VerificationUriComplete = "";
            return Result<DeviceCode>.Success(device);
        }

        /// <summary>
        /// Polls until the person approves on the other device, declines, or the code expires.
        /// slow_down adds 5 seconds between polls (RFC 8628, 3.5); a network or server failure
        /// waits and tries again, as the code's expiry still ends the wait.
        /// </summary>
        public async Task<Result<OAuthTokens>> PollDevice(DeviceCode device, Func<double> clock, CancellationToken cancel = default)
        {
            var interval = Math.Max(1, device.Interval);
            while (clock() < device.ExpiresAt)
            {
                try { await _transport.Delay(interval, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                if (cancel.IsCancellationRequested) return Result<OAuthTokens>.Fail(ErrorKind.Canceled, "Sign-in canceled.");
                var form = new Dictionary<string, string> { ["grant_type"] = DeviceGrant, ["device_code"] = device.Code, ["client_id"] = App.ClientId };
                var reply = await _transport.SendAsync(HttpRequests.FormPost(App.TokenEndpoint, form, 30), cancel).ConfigureAwait(false);
                if (cancel.IsCancellationRequested) return Result<OAuthTokens>.Fail(ErrorKind.Canceled, "Sign-in canceled.");
                if (!reply.Answered || reply.Status >= 500) continue;
                var data = Json.ParseObject(reply.Body);
                if (reply.IsSuccess) return Tokens(reply, data, clock(), "");
                switch (Json.Str(data, "error"))
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "access_denied":
                        return Result<OAuthTokens>.Fail(ErrorKind.Auth, "Sign-in was declined in the browser.");
                    case "expired_token":
                        return Result<OAuthTokens>.Fail(ErrorKind.Timeout, "The sign-in code expired. Start again.");
                    default:
                        return Result<OAuthTokens>.Fail(KindFor(reply), "Sign-in on another device failed: " + Describe(reply, data));
                }
            }
            return Result<OAuthTokens>.Fail(ErrorKind.Timeout, "The sign-in code expired. Start again.");
        }

        static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        static Result<OAuthTokens> Tokens(HttpReply reply, JObject data, double now, string previousRefresh)
        {
            var access = Json.Str(data, "access_token");
            if (!reply.IsSuccess || access.Length == 0)
            {
                var error = Json.Str(data, "error");
                var kind = error == "invalid_grant" || error == "invalid_client" ? ErrorKind.Auth : KindFor(reply);
                return Result<OAuthTokens>.Fail(kind, kind == ErrorKind.Auth
                    ? "Your Scenario sign-in has expired. Sign in again."
                    : "Scenario sign-in failed: " + Describe(reply, data), e => e.SessionExpired = kind == ErrorKind.Auth);
            }
            var expiresIn = Json.Num(data, "expires_in", 3600);
            return Result<OAuthTokens>.Success(new OAuthTokens
            {
                AccessToken = access,
                // Rotation: keep the old refresh token when the server sends none.
                RefreshToken = Json.Str(data, "refresh_token", previousRefresh),
                ExpiresAt = now + expiresIn,
            });
        }

        public async Task Revoke(string token, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(token)) return;
            var form = new Dictionary<string, string> { ["token"] = token, ["client_id"] = App.ClientId, ["token_type_hint"] = "refresh_token" };
            await _transport.SendAsync(HttpRequests.FormPost(App.RevocationEndpoint, form, 15), cancel);
        }

        /// <summary>Email and name for the account chip. Falls back to the access token's claims.</summary>
        public async Task<JObject> UserInfo(string accessToken, CancellationToken cancel = default)
        {
            var request = new HttpRequestSpec { Method = "GET", Url = App.UserInfoEndpoint, TimeoutSeconds = 15 };
            request.Headers["Authorization"] = "Bearer " + accessToken;
            var reply = await _transport.SendAsync(request, cancel);
            var data = reply.IsSuccess ? Json.ParseObject(reply.Body) : null;
            return data ?? Claims(accessToken) ?? new JObject();
        }

        /// <summary>The unverified payload of a JWT, for display only.</summary>
        public static JObject Claims(string jwt)
        {
            var parts = (jwt ?? "").Split('.');
            if (parts.Length != 3) return null;
            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                return Json.ParseObject(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        static ErrorKind KindFor(HttpReply reply)
        {
            if (!reply.Answered) return reply.Error == "timeout" ? ErrorKind.Timeout : reply.Error == "canceled" ? ErrorKind.Canceled : ErrorKind.Transport;
            if (reply.Status >= 500) return ErrorKind.Http5xx;
            if (reply.Status == 429) return ErrorKind.RateLimit;
            return ErrorKind.Invalid;
        }

        static string Describe(HttpReply reply, JObject data)
        {
            if (!reply.Answered) return reply.ErrorDetail.Length > 0 ? reply.ErrorDetail : reply.Error;
            var text = Json.Str(data, "error_description");
            if (text.Length == 0) text = Json.Str(data, "error");
            if (text.Length == 0) text = $"HTTP {reply.Status}";
            return text;
        }
    }

    /// <summary>
    /// The browser comes back to http://127.0.0.1:&lt;port&gt;/callback on any free port:
    /// the provider matches loopback redirect URIs on scheme, host and path only
    /// (RFC 8252, 7.3), so no fixed port can be busy.
    /// </summary>
    public sealed class LoopbackListener : IDisposable
    {
        readonly HttpListener _listener;
        public readonly int Port;
        public string RedirectUri => $"http://127.0.0.1:{Port}/callback";

        LoopbackListener(HttpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public static LoopbackListener Start()
        {
            // Another program can take the probed port before the listener does: try again.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var port = FreePort();
                var listener = TryListen(port);
                if (listener != null) return new LoopbackListener(listener, port);
            }
            return null;
        }

        static HttpListener TryListen(int port)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (Exception e) when (e is HttpListenerException || e is SocketException || e is InvalidOperationException)
            {
                try { listener.Close(); } catch (ObjectDisposedException) { }
                return null;
            }
        }

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public sealed class Callback
        {
            public string Code = "";
            public string State = "";
            public string Error = "";
        }

        /// <summary>Waits for the browser's redirect. Ignores stray requests (favicon, wrong path).</summary>
        public async Task<Callback> WaitForCode(string expectedState, TimeSpan timeout, CancellationToken cancel)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
            {
                var contextTask = _listener.GetContextAsync();
                var remaining = deadline - DateTime.UtcNow;
                var finished = await Task.WhenAny(contextTask, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancel)).ConfigureAwait(false);
                if (finished != contextTask) break;
                HttpListenerContext context;
                try { context = await contextTask.ConfigureAwait(false); }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException) { break; }
                var request = context.Request;
                if (request.Url.AbsolutePath != "/callback")
                {
                    Respond(context, 404, "Not found", "");
                    continue;
                }
                var query = request.QueryString;
                var callback = new Callback { Code = query["code"] ?? "", State = query["state"] ?? "", Error = query["error_description"] ?? query["error"] ?? "" };
                if (callback.State != expectedState)
                {
                    Respond(context, 400, "This sign-in link is out of date", "Start the sign-in again from Unity.");
                    continue;
                }
                if (callback.Error.Length > 0)
                    Respond(context, 200, "Sign-in canceled", "Return to Unity to try again.");
                else
                    Respond(context, 200, "You are signed in to Scenario", "You can close this tab and return to Unity.");
                return callback;
            }
            return new Callback { Error = cancel.IsCancellationRequested ? "canceled" : "timeout" };
        }

        static void Respond(HttpListenerContext context, int status, string title, string body)
        {
            var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Scenario</title>"
                + "<style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:#0d0d0d;color:#f2f2f2;"
                + "font:16px -apple-system,Segoe UI,Roboto,sans-serif}main{text-align:center;max-width:420px;padding:24px}"
                + "h1{font-size:22px;font-weight:600;margin:16px 0 8px}p{color:#a6a6a6;margin:0}.mark{font-weight:700;letter-spacing:.08em;font-size:13px}</style>"
                + $"</head><body><main><div class=\"mark\">SCENARIO</div><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(body)}</p></main></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            try
            {
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is System.IO.IOException)
            {
                // The browser went away; the code (if any) is still valid.
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>The result of a browser sign-in.</summary>
    public sealed class OAuthSession
    {
        public OAuthTokens Tokens;
        public string ClientId = "";
        public string Email = "";
        public string Name = "";

        public JObject Info() => new JObject { ["client_id"] = ClientId, ["email"] = Email, ["name"] = Name };
    }

    public static class OAuthSignIn
    {
        /// <summary>Full browser sign-in. openBrowser receives the authorize URL.</summary>
        public static async Task<Result<OAuthSession>> Run(OAuthClient client, Action<string> openBrowser, TimeSpan timeout, CancellationToken cancel = default)
        {
            using (var listener = LoopbackListener.Start())
            {
                if (listener == null) return Result<OAuthSession>.Fail(ErrorKind.Local, "Could not open a local port for the sign-in. Use an API key instead.");
                var verifier = OAuthClient.RandomToken(48);
                var state = OAuthClient.RandomToken(16);
                openBrowser(client.AuthorizeUrl(listener.RedirectUri, state, OAuthClient.Challenge(verifier)));
                var callback = await listener.WaitForCode(state, timeout, cancel).ConfigureAwait(false);
                if (callback.Code.Length == 0)
                {
                    var kind = callback.Error == "canceled" ? ErrorKind.Canceled : callback.Error == "timeout" ? ErrorKind.Timeout : ErrorKind.Auth;
                    var message = kind == ErrorKind.Timeout ? "The sign-in timed out. Try again." : kind == ErrorKind.Canceled ? "Sign-in canceled." : "Sign-in was not completed: " + callback.Error;
                    return Result<OAuthSession>.Fail(kind, message);
                }
                var tokens = await client.Exchange(callback.Code, listener.RedirectUri, verifier, Json.Now(), cancel).ConfigureAwait(false);
                if (!tokens.Ok) return tokens.Cast<OAuthSession>();
                return await Session(client, tokens.Value, cancel).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sign-in on another device, for a browser that cannot reach 127.0.0.1 (a remote
        /// session, a locked-down machine). showCode receives the code to enter and the page.
        /// </summary>
        public static async Task<Result<OAuthSession>> RunOnDevice(OAuthClient client, Action<DeviceCode> showCode, CancellationToken cancel = default)
        {
            var device = await client.StartDevice(Json.Now(), cancel).ConfigureAwait(false);
            if (!device.Ok) return device.Cast<OAuthSession>();
            showCode(device.Value);
            var tokens = await client.PollDevice(device.Value, Json.Now, cancel).ConfigureAwait(false);
            if (!tokens.Ok) return tokens.Cast<OAuthSession>();
            return await Session(client, tokens.Value, cancel).ConfigureAwait(false);
        }

        static async Task<Result<OAuthSession>> Session(OAuthClient client, OAuthTokens tokens, CancellationToken cancel)
        {
            var user = await client.UserInfo(tokens.AccessToken, cancel).ConfigureAwait(false);
            return Result<OAuthSession>.Success(new OAuthSession
            {
                Tokens = tokens,
                ClientId = client.App.ClientId,
                Email = Json.Str(user, "email"),
                Name = Json.Str(user, "name", Json.Str(user, "given_name")),
            });
        }
    }

    /// <summary>Bearer auth with automatic, serialized refresh.</summary>
    public sealed class OAuthAuth : IAuthProvider
    {
        readonly OAuthClient _client;
        readonly SemaphoreSlim _refreshing = new SemaphoreSlim(1, 1);
        public OAuthTokens Tokens;
        public string Email;

        /// <summary>Raised (on a worker thread) when the refresh token rotated and must be saved.</summary>
        public event Action<OAuthTokens> TokensChanged;

        public OAuthAuth(OAuthClient client, OAuthTokens tokens, string email)
        {
            _client = client;
            Tokens = tokens ?? new OAuthTokens();
            Email = email ?? "";
        }

        public AuthMethod Method => AuthMethod.OAuth;
        public bool HasCredentials => Tokens.RefreshToken.Length > 0 || Tokens.AccessToken.Length > 0;
        public bool NeedsScope => true;
        public string Label => Email.Length > 0 ? Email : "Scenario account";

        public async Task<string> AuthorizationHeaderAsync()
        {
            if (!Tokens.IsFresh(Json.Now())) await RefreshAsync().ConfigureAwait(false);
            return Tokens.AccessToken.Length > 0 ? "Bearer " + Tokens.AccessToken : "";
        }

        public async Task<bool> RefreshAsync()
        {
            var before = Tokens.AccessToken;
            await _refreshing.WaitAsync().ConfigureAwait(false);
            try
            {
                // Another caller refreshed while this one waited.
                if (Tokens.AccessToken != before && Tokens.IsFresh(Json.Now())) return true;
                if (Tokens.RefreshToken.Length == 0) return false;
                var result = await _client.Refresh(Tokens.RefreshToken, Json.Now()).ConfigureAwait(false);
                if (!result.Ok)
                {
                    if (result.Error.SessionExpired) Tokens = new OAuthTokens();
                    return false;
                }
                var rotated = result.Value.RefreshToken != Tokens.RefreshToken;
                Tokens = result.Value;
                if (rotated) TokensChanged?.Invoke(Tokens);
                return true;
            }
            finally
            {
                _refreshing.Release();
            }
        }
    }
}
