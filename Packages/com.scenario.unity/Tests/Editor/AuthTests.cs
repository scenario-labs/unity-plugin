using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine.TestTools;

namespace Scenario.Editor.Tests
{
    public class CredentialStoreTests
    {
        string _path;

        [SetUp]
        public void SetUp() => _path = Path.Combine(TempDir.Make("creds"), "account.json");

        [Test]
        public void ApiKeySecretGoesToTheSecretStore()
        {
            var secrets = new MemorySecretStore();
            var store = new CredentialStore(_path, secrets);
            Assert.AreEqual("memory", store.SaveApiKey(" key_1 ", " s3cret value "));
            StringAssert.DoesNotContain("s3cret", File.ReadAllText(_path), "the secret never lands in the file");
            var loaded = store.Load();
            Assert.AreEqual("apikey", loaded.Method);
            Assert.AreEqual("key_1", loaded.ApiKey);
            Assert.AreEqual("s3cret value", loaded.Secret);
            Assert.AreEqual("memory", loaded.Source);
            store.Clear();
            Assert.AreEqual("", store.Load().Method);
            Assert.IsEmpty(secrets.Values);
        }

        [Test]
        public void FallsBackToAPrivateFileWhenNoStoreWorks()
        {
            var store = new CredentialStore(_path, new MemorySecretStore { Fail = true });
            Assert.AreEqual("file", store.SaveRefreshToken("rt_123", new JObject { ["email"] = "a@b.c" }));
            var loaded = store.Load();
            Assert.AreEqual("oauth", loaded.Method);
            Assert.AreEqual("rt_123", loaded.Secret);
            Assert.AreEqual("a@b.c", (string)loaded.Info["email"]);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                var (code, output) = RunStat(_path);
                if (code == 0) StringAssert.StartsWith("600", output.Trim());
            }
        }

        [Test]
        public void UpdateInfoKeepsTheSecret()
        {
            var store = new CredentialStore(_path, new MemorySecretStore());
            store.SaveRefreshToken("rt", new JObject { ["email"] = "x" });
            store.UpdateInfo(new JObject { ["email"] = "y", ["team_id"] = "t" });
            var loaded = store.Load();
            Assert.AreEqual("rt", loaded.Secret);
            Assert.AreEqual("t", (string)loaded.Info["team_id"]);
        }

        [Test]
        public void PastedKeysAreSplit()
        {
            Assert.IsTrue(ApiKeyAuth.TrySplitPasted("api_abc:secret_xyz", out var k, out var s));
            Assert.AreEqual(("api_abc", "secret_xyz"), (k, s));
            Assert.IsTrue(ApiKeyAuth.TrySplitPasted("api_abc\nsecret_xyz\n", out k, out s));
            Assert.AreEqual("secret_xyz", s);
            Assert.IsFalse(ApiKeyAuth.TrySplitPasted("only-one", out _, out _));
            Assert.AreEqual("API key api_ab…", new ApiKeyAuth("api_abc", "s").Label);
        }

        static (int, string) RunStat(string path)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat", $"-f %Lp \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false };
                using (var p = System.Diagnostics.Process.Start(info))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return (p.ExitCode, output);
                }
            }
            catch (Exception)
            {
                return (-1, "");
            }
        }
    }

    public class OAuthTests
    {
        [Test]
        public void PkceMatchesTheRfcVector()
        {
            // Expected value from Python: base64url(sha256(verifier)) without padding.
            Assert.AreEqual("RGyg3Hd4DFMTKe-h1uk-39FDU0jOR9quW8J-pHAVzQY", OAuthClient.Challenge("dBjftJeZ4CVP-mJ92K9qnBpFWU2nKR-kXKJjkdN2u9I"));
            var token = OAuthClient.RandomToken(48);
            Assert.AreEqual(64, token.Length);
            Assert.IsFalse(token.Contains("+") || token.Contains("/") || token.Contains("="));
        }

        /// <summary>A development provider and application, as the SCENARIO_OAUTH_* variables name them.</summary>
        static readonly OAuthApp DevApp = new OAuthApp("https://id.example.test/", "unity_dev_client");

        [Test]
        public void AuthorizeUrlSignsInAsUnitysApplication()
        {
            var client = new OAuthClient(new FakeMcp(), OAuthApp.Production);
            var url = new Uri(client.AuthorizeUrl("http://127.0.0.1:50123/callback", "st", "ch"));
            Assert.AreEqual("https://clerk.scenario.com/oauth/authorize", url.GetLeftPart(UriPartial.Path));
            var query = ParseQuery(url.Query);
            Assert.AreEqual(OAuthApp.ProductionClientId, query["client_id"]);
            Assert.AreEqual("code", query["response_type"]);
            Assert.AreEqual("S256", query["code_challenge_method"]);
            Assert.AreEqual("ch", query["code_challenge"]);
            Assert.AreEqual("st", query["state"]);
            Assert.AreEqual("http://127.0.0.1:50123/callback", query["redirect_uri"]);
            StringAssert.Contains("offline_access", query["scope"]);
            Assert.IsFalse(query.ContainsKey("resource"), "the provider issues tokens for its own application, not for the MCP resource");
        }

        [Test]
        public void ExchangeAndRefreshRotateTokens()
        {
            var server = new FakeMcp();
            var forms = new List<string>();
            var urls = new List<string>();
            server.Other = request =>
            {
                var body = Encoding.UTF8.GetString(request.Body ?? new byte[0]);
                forms.Add(body);
                urls.Add(request.Url);
                if (body.Contains("grant_type=authorization_code"))
                    return new HttpReply { Status = 200, Body = "{\"access_token\":\"at1\",\"refresh_token\":\"rt1\",\"expires_in\":3600}" };
                if (body.Contains("refresh_token=rt1"))
                    return new HttpReply { Status = 200, Body = "{\"access_token\":\"at2\",\"refresh_token\":\"rt2\",\"expires_in\":3600}" };
                return new HttpReply { Status = 400, Body = "{\"error\":\"invalid_grant\"}" };
            };
            var client = new OAuthClient(server, DevApp);
            var tokens = Run.Sync(client.Exchange("code", "http://127.0.0.1:1/callback", "verifier", 100));
            Assert.AreEqual("at1", tokens.Value.AccessToken);
            Assert.AreEqual(3700, tokens.Value.ExpiresAt);
            Assert.AreEqual("https://id.example.test/oauth/token", urls[0]);
            StringAssert.Contains("code_verifier=verifier", forms[0]);
            StringAssert.Contains("client_id=unity_dev_client", forms[0]);
            StringAssert.DoesNotContain("resource=", forms[0]);

            var auth = new OAuthAuth(client, new OAuthTokens { AccessToken = "old", RefreshToken = "rt1", ExpiresAt = 0 }, "me@x.com");
            OAuthTokens rotated = null;
            auth.TokensChanged += t => rotated = t;
            Assert.AreEqual("Bearer at2", Run.Sync(auth.AuthorizationHeaderAsync()));
            Assert.AreEqual("rt2", rotated.RefreshToken);
            Assert.AreEqual("me@x.com", auth.Label);
            StringAssert.Contains("client_id=unity_dev_client", forms[1]);
            StringAssert.DoesNotContain("resource=", forms[1]);

            var expired = Run.Sync(client.Refresh("rt_dead", 100));
            Assert.AreEqual(ErrorKind.Auth, expired.Error.Kind);
            Assert.IsTrue(expired.Error.SessionExpired);
        }

        [Test]
        public void RevokeSendsTheTokenToTheAppsProvider()
        {
            var server = new FakeMcp();
            HttpRequestSpec sent = null;
            server.Other = request =>
            {
                sent = request;
                return new HttpReply { Status = 200 };
            };
            Run.Sync(new OAuthClient(server, DevApp).Revoke("rt_1"));
            Assert.AreEqual("https://id.example.test/oauth/token/revoke", sent.Url);
            var form = Encoding.UTF8.GetString(sent.Body);
            StringAssert.Contains("client_id=unity_dev_client", form);
            StringAssert.Contains("token=rt_1", form);
        }

        [Test]
        public void TheAppIsProductionOrBothDeveloperVariables()
        {
            Func<string, string> Env(params string[] pairs)
            {
                var values = new Dictionary<string, string>();
                for (var i = 0; i < pairs.Length; i += 2) values[pairs[i]] = pairs[i + 1];
                return name => values.TryGetValue(name, out var value) ? value : null;
            }

            var production = OAuthApp.Select(Env());
            Assert.AreEqual(OAuthApp.ProductionIssuer, production.Value.Issuer);
            Assert.AreEqual(OAuthApp.ProductionClientId, production.Value.ClientId);

            var dev = OAuthApp.Select(Env(OAuthApp.IssuerVariable, " https://id.example.test/ ", OAuthApp.ClientIdVariable, "unity_dev_client"));
            Assert.AreEqual("https://id.example.test", dev.Value.Issuer);
            Assert.AreEqual("unity_dev_client", dev.Value.ClientId);
            Assert.AreEqual("https://id.example.test/oauth/userinfo", dev.Value.UserInfoEndpoint);

            var wrong = new[]
            {
                Env(OAuthApp.IssuerVariable, "https://id.example.test"),
                Env(OAuthApp.ClientIdVariable, "unity_dev_client"),
                Env(OAuthApp.IssuerVariable, "http://id.example.test", OAuthApp.ClientIdVariable, "unity_dev_client"),
            };
            foreach (var result in wrong.Select(env => OAuthApp.Select(env)))
            {
                Assert.IsFalse(result.Ok);
                StringAssert.Contains(OAuthApp.IssuerVariable, result.Error.Message);
                StringAssert.Contains(OAuthApp.ClientIdVariable, result.Error.Message);
            }
        }

        [Test]
        public void ClaimsAreReadForDisplay()
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"a@b.c\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.AreEqual("a@b.c", (string)OAuthClient.Claims($"x.{payload}.y")["email"]);
            Assert.IsNull(OAuthClient.Claims("nope"));
        }

        [Test]
        public void LoopbackListenerReturnsTheCodeAndChecksState()
        {
            using (var listener = LoopbackListener.Start())
            {
                Assert.IsNotNull(listener);
                Assert.AreEqual($"http://127.0.0.1:{listener.Port}/callback", listener.RedirectUri);
                var wait = listener.WaitForCode("good", TimeSpan.FromSeconds(10), default);
                using (var http = new HttpClient())
                {
                    var wrong = http.GetAsync(listener.RedirectUri + "?code=c1&state=bad").Result;
                    Assert.AreEqual(400, (int)wrong.StatusCode);
                    var right = http.GetAsync(listener.RedirectUri + "?code=c2&state=good").Result;
                    Assert.AreEqual(200, (int)right.StatusCode);
                    StringAssert.Contains("signed in", right.Content.ReadAsStringAsync().Result);
                }
                Assert.IsTrue(wait.Wait(TimeSpan.FromSeconds(10)));
                Assert.AreEqual("c2", wait.Result.Code);
            }
        }

        static Dictionary<string, string> ParseQuery(string query)
            => query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    }

    /// <summary>
    /// "Sign in with Scenario" through the service: the browser opens even when the network
    /// answers on a worker thread, and Cancel always brings the sign-in panel back.
    /// </summary>
    public class BrowserSignInTests
    {
        Action<string> _openUrl;

        [SetUp]
        public void SetUp() => _openUrl = ScenarioService.OpenUrl;

        [TearDown]
        public void TearDown()
        {
            ScenarioService.OpenUrl = _openUrl;
            ScenarioService.ResetForTests();
        }

        [UnityTest]
        public IEnumerator OpensTheBrowserFromTheMainThread_AndCancelBringsThePanelBack()
        {
            var server = new FakeMcp { Other = request => new HttpReply { Status = 500 } };
            var s = ScenarioService.UseForTests(new WorkerThreadTransport(server), TempDir.Make("signin"));
            string opened = null;
            var onMainThread = false;
            ScenarioService.OpenUrl = url =>
            {
                opened = url;
                onMainThread = MainThread.IsMainThread;
            };

            var signIn = s.SignInWithBrowser();
            var until = EditorApplication.timeSinceStartup + 20;
            while (opened == null && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.IsNotNull(opened, "the browser was asked to open");
            Assert.IsTrue(onMainThread, "Unity opens URLs only from the main thread");
            StringAssert.StartsWith("https://clerk.scenario.com/oauth/authorize?", opened);
            StringAssert.Contains("client_id=" + OAuthApp.ProductionClientId, opened);
            Assert.IsFalse(server.Requests.Any(r => r.Url.Contains("/register")), "no client registration: Unity signs in as its registered application");
            Assert.AreEqual(SignInState.SigningIn, s.State);

            s.CancelSignIn();
            Assert.AreEqual(SignInState.SignedOut, s.State, "Cancel brings the panel back at once");
            while (!signIn.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.IsTrue(signIn.IsCompleted, "the sign-in ended");
            Assert.AreEqual(SignInState.SignedOut, s.State);
            Assert.AreEqual("", s.SignInError);
        }
    }

    /// <summary>A saved browser sign-in resumes only when Scenario's identity provider issued it to Unity's application.</summary>
    public class SavedSessionTests
    {
        [TearDown]
        public void TearDown() => ScenarioService.ResetForTests();

        static CredentialStore StoreWithSession(string folder, string clientId, string issuer = OAuthApp.ProductionIssuer)
        {
            var store = new CredentialStore(Path.Combine(folder, "credentials.json"), new MemorySecretStore());
            var info = new JObject { ["client_id"] = clientId, ["email"] = "me@x.com" };
            if (issuer != null) info["issuer"] = issuer;
            store.SaveRefreshToken("rt_saved", info);
            return store;
        }

        [Test]
        public void ASessionFromUnitysApplicationResumes()
        {
            var folder = TempDir.Make("saved-app");
            var store = StoreWithSession(folder, OAuthApp.ProductionClientId);
            var s = ScenarioService.UseForTests(new FakeMcp(), folder, store);
            Assert.AreEqual(SignInState.SignedIn, s.State);
            Assert.AreEqual(AuthMethod.OAuth, s.Method);
            Assert.AreEqual("me@x.com", s.AccountLabel);
            Assert.AreEqual("oauth", store.Load().Method);
        }

        [Test]
        public void ASessionFromAnotherClient_IsNotResumed_NorSentAnywhere()
        {
            var folder = TempDir.Make("saved-other");
            // A client this install registered through the hosted MCP before Unity had its own application.
            var store = StoreWithSession(folder, "registered_per_install");
            var server = new FakeMcp();
            var s = ScenarioService.UseForTests(server, folder, store);
            Assert.AreEqual(SignInState.SignedOut, s.State);
            Assert.IsFalse(s.Client.HasCredentials);
            Assert.AreEqual(0, server.Requests.Count, "never sent to a provider that did not issue it");
            // The store is shared by every Unity project on the machine: another may still run that version.
            Assert.AreEqual("oauth", store.Load().Method, "left for the version that issued it, until the next sign-in");
        }

        // Unity's client id with another provider's token (a development provider set with production's
        // client id), or an entry saved without its provider: refreshing would send that token to a
        // provider that may not have issued it.
        [TestCase("https://id.example.test")]
        [TestCase(null)]
        public void ASessionFromAnotherProvider_WithUnitysClientId_IsNotResumed_NorSentAnywhere(string issuer)
        {
            var folder = TempDir.Make("saved-issuer");
            var store = StoreWithSession(folder, OAuthApp.ProductionClientId, issuer);
            var server = new FakeMcp();
            var s = ScenarioService.UseForTests(server, folder, store);
            Assert.AreEqual(SignInState.SignedOut, s.State);
            Assert.IsFalse(s.Client.HasCredentials);
            Assert.AreEqual(0, server.Requests.Count, "never sent to a provider that may not have issued it");
            Assert.AreEqual("oauth", store.Load().Method, "left in place until the next sign-in");
        }
    }

    /// <summary>Sign-in on another device (RFC 8628): the code, the polling rules, and the service around them.</summary>
    public class DeviceSignInTests
    {
        static readonly OAuthApp DevApp = new OAuthApp("https://id.example.test", "unity_dev_client");

        const string DeviceReply = "{\"device_code\":\"dc_1\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://id.example.test/device\","
                                   + "\"verification_uri_complete\":\"https://id.example.test/device?user_code=ABCD-EFGH\",\"expires_in\":600,\"interval\":5}";
        const string TokensReply = "{\"access_token\":\"at1\",\"refresh_token\":\"rt1\",\"expires_in\":3600}";

        static HttpReply OAuthError(string code) => new HttpReply { Status = 400, Body = "{\"error\":\"" + code + "\"}" };

        static DeviceCode Code(double expiresAt = double.MaxValue)
            => new DeviceCode { Code = "dc_1", UserCode = "ABCD-EFGH", VerificationUri = "https://id.example.test/device", Interval = 5, ExpiresAt = expiresAt };

        string _settings;

        [SetUp]
        public void SetUp() => _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);

        [TearDown]
        public void TearDown()
        {
            ScenarioService.ResetForTests();
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
            ScenarioUserSettings.instance.Save();
        }

        [Test]
        public void StartAsksTheAppsProviderForACode()
        {
            var server = new FakeMcp();
            HttpRequestSpec sent = null;
            server.Other = request =>
            {
                sent = request;
                return new HttpReply { Status = 200, Body = DeviceReply };
            };
            var device = Run.Sync(new OAuthClient(server, DevApp).StartDevice(1000)).Value;
            Assert.AreEqual("https://id.example.test/oauth/device_authorization", sent.Url);
            var form = Uri.UnescapeDataString(Encoding.UTF8.GetString(sent.Body));
            StringAssert.Contains("client_id=unity_dev_client", form);
            StringAssert.Contains("offline_access", form);
            Assert.AreEqual("dc_1", device.Code);
            Assert.AreEqual("ABCD-EFGH", device.UserCode);
            Assert.AreEqual("https://id.example.test/device", device.VerificationUri);
            Assert.AreEqual("https://id.example.test/device?user_code=ABCD-EFGH", device.PageToOpen);
            Assert.AreEqual(5, device.Interval);
            Assert.AreEqual(1600, device.ExpiresAt);
        }

        [Test]
        public void StartRefusesAPageThatIsNotHttps()
        {
            var server = new FakeMcp { Other = _ => new HttpReply { Status = 200, Body = DeviceReply.Replace("\"verification_uri\":\"https://", "\"verification_uri\":\"http://") } };
            var result = Run.Sync(new OAuthClient(server, DevApp).StartDevice(0));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("HTTPS", result.Error.Message);
        }

        [Test]
        public void StartSaysWhenTheAppMayNotUseTheDeviceGrant()
        {
            // What the provider answers while the grant is off for the application (checked 2026-11-09).
            var server = new FakeMcp
            {
                Other = _ => new HttpReply
                {
                    Status = 400,
                    Body = "{\"error\":\"invalid_grant\",\"error_description\":\"The requested OAuth 2.0 Client does not have the 'urn:ietf:params:oauth:grant-type:device_code' grant.\"}",
                },
            };
            var result = Run.Sync(new OAuthClient(server, DevApp).StartDevice(0));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("not available", result.Error.Message);
            StringAssert.DoesNotContain("urn:ietf", result.Error.Message);
        }

        [Test]
        public void StartShowsOtherProviderErrorsAsTheyAre()
        {
            // A mistyped SCENARIO_OAUTH_CLIENT_ID is not "not available yet".
            var server = new FakeMcp { Other = _ => new HttpReply { Status = 401, Body = "{\"error\":\"invalid_client\",\"error_description\":\"Client not found\"}" } };
            var result = Run.Sync(new OAuthClient(server, DevApp).StartDevice(0));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("Client not found", result.Error.Message);
            StringAssert.DoesNotContain("not available", result.Error.Message);
        }

        [Test]
        public void PollTreatsARateLimitLikeSlowDown()
        {
            var replies = new Queue<HttpReply>(new[] { FakeMcp.Status(429), new HttpReply { Status = 200, Body = TokensReply } });
            var server = new FakeMcp { Other = _ => replies.Dequeue() };
            var tokens = Run.Sync(new OAuthClient(server, DevApp).PollDevice(Code(), () => 0));
            Assert.AreEqual("at1", tokens.Value.AccessToken);
            CollectionAssert.AreEqual(new[] { 5.0, 10.0 }, server.Delays);
        }

        [Test]
        public void PollWaitsSlowsDownAndReturnsTheTokens()
        {
            var forms = new List<string>();
            var replies = new Queue<HttpReply>(new[] { OAuthError("authorization_pending"), OAuthError("slow_down"), new HttpReply { Status = 200, Body = TokensReply } });
            var server = new FakeMcp();
            server.Other = request =>
            {
                forms.Add(Uri.UnescapeDataString(Encoding.UTF8.GetString(request.Body)));
                return replies.Dequeue();
            };
            var tokens = Run.Sync(new OAuthClient(server, DevApp).PollDevice(Code(), () => 100));
            Assert.AreEqual("at1", tokens.Value.AccessToken);
            Assert.AreEqual("rt1", tokens.Value.RefreshToken);
            CollectionAssert.AreEqual(new[] { 5.0, 5.0, 10.0 }, server.Delays, "the interval first, then 5 seconds more after slow_down");
            StringAssert.Contains("grant_type=urn:ietf:params:oauth:grant-type:device_code", forms[0]);
            StringAssert.Contains("device_code=dc_1", forms[0]);
            StringAssert.Contains("client_id=unity_dev_client", forms[0]);
        }

        [Test]
        public void PollStopsWhenDeclinedOrExpired()
        {
            var server = new FakeMcp { Other = _ => OAuthError("access_denied") };
            var client = new OAuthClient(server, DevApp);
            var declined = Run.Sync(client.PollDevice(Code(), () => 0));
            Assert.AreEqual(ErrorKind.Auth, declined.Error.Kind);
            StringAssert.Contains("declined", declined.Error.Message);

            server.Other = _ => OAuthError("expired_token");
            Assert.AreEqual(ErrorKind.Timeout, Run.Sync(client.PollDevice(Code(), () => 0)).Error.Kind);

            var sent = server.Requests.Count;
            var late = Run.Sync(client.PollDevice(Code(expiresAt: 100), () => 200));
            Assert.AreEqual(ErrorKind.Timeout, late.Error.Kind);
            Assert.AreEqual(sent, server.Requests.Count, "an expired code is not polled");
        }

        [Test]
        public void PollKeepsWaitingThroughANetworkOrServerFailure()
        {
            var replies = new Queue<HttpReply>(new[] { FakeMcp.Fail("transport"), FakeMcp.Status(503), new HttpReply { Status = 200, Body = TokensReply } });
            var server = new FakeMcp { Other = _ => replies.Dequeue() };
            Assert.AreEqual("at1", Run.Sync(new OAuthClient(server, DevApp).PollDevice(Code(), () => 0)).Value.AccessToken);
        }

        [Test]
        public void PollStopsWhenCanceled()
        {
            using (var cancel = new CancellationTokenSource())
            {
                var server = new FakeMcp
                {
                    Other = _ =>
                    {
                        cancel.Cancel();
                        return OAuthError("authorization_pending");
                    },
                };
                var result = Run.Sync(new OAuthClient(server, DevApp).PollDevice(Code(), () => 0, cancel.Token));
                Assert.AreEqual(ErrorKind.Canceled, result.Error.Kind);
            }
        }

        /// <summary>The production provider as the service meets it: a code, some polls before approval, then who signed in.</summary>
        static FakeMcp Provider(int pendingPolls)
        {
            var polls = 0;
            var server = new FakeMcp();
            server.Tools["teams_list"] = (args, i) => FakeMcp.Fixture("teams.sse.txt");
            server.Other = request =>
            {
                if (request.Url == OAuthApp.Production.DeviceAuthorizationEndpoint) return new HttpReply { Status = 200, Body = DeviceReply };
                if (request.Url == OAuthApp.Production.TokenEndpoint)
                    return polls++ < pendingPolls ? OAuthError("authorization_pending") : new HttpReply { Status = 200, Body = TokensReply };
                if (request.Url == OAuthApp.Production.UserInfoEndpoint) return new HttpReply { Status = 200, Body = "{\"email\":\"me@x.com\"}" };
                return new HttpReply { Status = 404 };
            };
            return server;
        }

        [UnityTest]
        public IEnumerator TheServiceShowsTheCode_ThenSignsInOnceApproved()
        {
            var s = ScenarioService.UseForTests(new WorkerThreadTransport(Provider(pendingPolls: 20)), TempDir.Make("device"));
            var signIn = s.SignInOnAnotherDevice();
            Assert.IsTrue(s.SigningInOnDevice);
            Assert.AreEqual(SignInState.SigningIn, s.State);
            var until = EditorApplication.timeSinceStartup + 20;
            while (s.PendingDevice == null && !signIn.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.AreEqual("ABCD-EFGH", s.PendingDevice?.UserCode, "the code shows while Unity waits for the approval");
            while (!signIn.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.AreEqual(SignInState.SignedIn, s.State, s.SignInError);
            Assert.AreEqual("me@x.com", s.AccountLabel);
            Assert.IsNull(s.PendingDevice);
            Assert.IsFalse(s.SigningInOnDevice);
            var saved = s.Credentials.Load();
            Assert.AreEqual("oauth", saved.Method);
            Assert.AreEqual("rt1", saved.Secret);
            Assert.AreEqual(OAuthApp.ProductionClientId, Json.Str(saved.Info, "client_id"));
            Assert.AreEqual(OAuthApp.ProductionIssuer, Json.Str(saved.Info, "issuer"), "saved with the provider that issued it");
        }

        [UnityTest]
        public IEnumerator CancelDropsTheCode_AndBringsThePanelBack()
        {
            var s = ScenarioService.UseForTests(new WorkerThreadTransport(Provider(pendingPolls: int.MaxValue)), TempDir.Make("device-cancel"));
            var signIn = s.SignInOnAnotherDevice();
            var until = EditorApplication.timeSinceStartup + 20;
            while (s.PendingDevice == null && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.IsNotNull(s.PendingDevice);

            s.CancelSignIn();
            Assert.AreEqual(SignInState.SignedOut, s.State, "Cancel brings the panel back at once");
            Assert.IsNull(s.PendingDevice);
            Assert.IsFalse(s.SigningInOnDevice);
            while (!signIn.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.IsTrue(signIn.IsCompleted, "the polling stopped");
            Assert.AreEqual(SignInState.SignedOut, s.State);
            Assert.AreEqual("", s.SignInError);
            Assert.IsNull(s.PendingDevice);
        }
    }
}
