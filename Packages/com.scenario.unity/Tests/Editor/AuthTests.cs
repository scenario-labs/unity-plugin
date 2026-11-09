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

            foreach (var wrong in new[]
                     {
                         Env(OAuthApp.IssuerVariable, "https://id.example.test"),
                         Env(OAuthApp.ClientIdVariable, "unity_dev_client"),
                         Env(OAuthApp.IssuerVariable, "http://id.example.test", OAuthApp.ClientIdVariable, "unity_dev_client"),
                     })
            {
                var result = OAuthApp.Select(wrong);
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
        /// <summary>Answers like the real network: later, on a worker thread.</summary>
        sealed class WorkerThreadTransport : ITransport
        {
            readonly FakeMcp _inner;
            public WorkerThreadTransport(FakeMcp inner) { _inner = inner; }

            public async Task<HttpReply> SendAsync(HttpRequestSpec request, CancellationToken cancel = default)
            {
                await Task.Delay(50).ConfigureAwait(false);
                return await _inner.SendAsync(request, cancel).ConfigureAwait(false);
            }

            public Task<HttpReply> DownloadAsync(string url, string destPath, double timeoutSeconds, IProgress<float> progress = null, CancellationToken cancel = default)
                => _inner.DownloadAsync(url, destPath, timeoutSeconds, progress, cancel);

            public Task Delay(double seconds, CancellationToken cancel = default) => _inner.Delay(seconds, cancel);
        }

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

    /// <summary>A saved browser sign-in resumes only when Unity's application issued it.</summary>
    public class SavedSessionTests
    {
        [TearDown]
        public void TearDown() => ScenarioService.ResetForTests();

        static CredentialStore StoreWithSession(string folder, string clientId)
        {
            var store = new CredentialStore(Path.Combine(folder, "credentials.json"), new MemorySecretStore());
            store.SaveRefreshToken("rt_saved", new JObject { ["client_id"] = clientId, ["email"] = "me@x.com" });
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
        public void ASessionFromAnotherClientIsForgottenWithoutARequest()
        {
            var folder = TempDir.Make("saved-other");
            // A client this install registered through the hosted MCP before Unity had its own application.
            var store = StoreWithSession(folder, "registered_per_install");
            var server = new FakeMcp();
            var s = ScenarioService.UseForTests(server, folder, store);
            Assert.AreEqual(SignInState.SignedOut, s.State);
            Assert.IsFalse(s.Client.HasCredentials);
            Assert.AreEqual("", store.Load().Method, "the token is deleted");
            Assert.AreEqual(0, server.Requests.Count, "and never sent to a provider that did not issue it");
        }
    }
}
