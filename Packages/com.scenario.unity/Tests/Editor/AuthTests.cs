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

        [Test]
        public void AuthorizeUrlCarriesPkceStateAndResource()
        {
            var client = new OAuthClient(new FakeMcp());
            var url = new Uri(client.AuthorizeUrl("client_1", "http://127.0.0.1:47321/callback", "st", "ch"));
            Assert.AreEqual("mcp.scenario.com", url.Host);
            var query = ParseQuery(url.Query);
            Assert.AreEqual("code", query["response_type"]);
            Assert.AreEqual("S256", query["code_challenge_method"]);
            Assert.AreEqual("ch", query["code_challenge"]);
            Assert.AreEqual("st", query["state"]);
            Assert.AreEqual("https://mcp.scenario.com/mcp", query["resource"]);
            Assert.AreEqual("http://127.0.0.1:47321/callback", query["redirect_uri"]);
            StringAssert.Contains("offline_access", query["scope"]);
        }

        [Test]
        public void ExchangeAndRefreshRotateTokens()
        {
            var server = new FakeMcp();
            var forms = new List<string>();
            server.Other = request =>
            {
                var body = Encoding.UTF8.GetString(request.Body ?? new byte[0]);
                forms.Add(body);
                if (body.Contains("grant_type=authorization_code"))
                    return new HttpReply { Status = 200, Body = "{\"access_token\":\"at1\",\"refresh_token\":\"rt1\",\"expires_in\":3600}" };
                if (body.Contains("refresh_token=rt1"))
                    return new HttpReply { Status = 200, Body = "{\"access_token\":\"at2\",\"refresh_token\":\"rt2\",\"expires_in\":3600}" };
                return new HttpReply { Status = 400, Body = "{\"error\":\"invalid_grant\"}" };
            };
            var client = new OAuthClient(server);
            var tokens = Run.Sync(client.Exchange("c", "code", "http://127.0.0.1:1/callback", "verifier", 100));
            Assert.AreEqual("at1", tokens.Value.AccessToken);
            Assert.AreEqual(3700, tokens.Value.ExpiresAt);
            StringAssert.Contains("code_verifier=verifier", forms[0]);
            StringAssert.Contains("resource=https%3A%2F%2Fmcp.scenario.com%2Fmcp", forms[0]);

            var auth = new OAuthAuth(client, "c", new OAuthTokens { AccessToken = "old", RefreshToken = "rt1", ExpiresAt = 0 }, "me@x.com");
            OAuthTokens rotated = null;
            auth.TokensChanged += t => rotated = t;
            Assert.AreEqual("Bearer at2", Run.Sync(auth.AuthorizationHeaderAsync()));
            Assert.AreEqual("rt2", rotated.RefreshToken);
            Assert.AreEqual("me@x.com", auth.Label);

            var expired = Run.Sync(client.Refresh("c", "rt_dead", 100));
            Assert.AreEqual(ErrorKind.Auth, expired.Error.Kind);
            Assert.IsTrue(expired.Error.SessionExpired);
        }

        [Test]
        public void RegistrationIsAPublicClient()
        {
            var server = new FakeMcp();
            JObject sent = null;
            server.Other = request =>
            {
                sent = JObject.Parse(Encoding.UTF8.GetString(request.Body));
                return new HttpReply { Status = 201, Body = "{\"client_id\":\"client_new\"}" };
            };
            var result = Run.Sync(new OAuthClient(server).Register("http://127.0.0.1:47321/callback"));
            Assert.AreEqual("client_new", result.Value);
            Assert.AreEqual("none", (string)sent["token_endpoint_auth_method"]);
            Assert.AreEqual("http://127.0.0.1:47321/callback", (string)sent["redirect_uris"][0]);
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
            var server = new FakeMcp { Other = request => new HttpReply { Status = 201, Body = "{\"client_id\":\"client_new\"}" } };
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
            StringAssert.StartsWith("https://mcp.scenario.com/oauth/authorize?", opened);
            Assert.AreEqual(SignInState.SigningIn, s.State);

            s.CancelSignIn();
            Assert.AreEqual(SignInState.SignedOut, s.State, "Cancel brings the panel back at once");
            while (!signIn.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.IsTrue(signIn.IsCompleted, "the sign-in ended");
            Assert.AreEqual(SignInState.SignedOut, s.State);
            Assert.AreEqual("", s.SignInError);
        }
    }
}
