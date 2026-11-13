using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// The window on a scripted Scenario: every view and overlay builds without an
    /// error in the console, and browsing never sends a paid call.
    /// </summary>
    public class UiTests
    {
        FakeApi _server;
        ScenarioService _s;
        ScenarioWindow _window;
        string _settings;

        [SetUp]
        public void SetUp()
        {
            // Editor windows need a graphics device: CI runs with -nographics skip these.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("The window tests need a graphics device (run without -nographics).");
            _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);
            // The window opens on the view the user left it on, and Open reuses a window already open:
            // the tests start from a new window on Create.
            foreach (var open in Resources.FindObjectsOfTypeAll<ScenarioWindow>()) open.Close();
            ScenarioUserSettings.instance.View = "create";
            _server = new FakeApi();
            _server.Routes["GET /teams"] = (args, i) => FakeApi.Fixture("teams.json");
            _server.Routes["GET /models"] = (args, i) => FakeApi.Fixture("models_page.json");
            _server.Routes["GET /models/{id}"] = (args, i) => FakeApi.Fixture("schema_flare.json");
            _server.Routes["POST /generate/custom/{id}"] = (args, i) => FakeApi.Fixture("estimate.json");
            _server.Routes["GET /recommendations/models"] = (args, i) => FakeApi.Fixture("recommendations_3d.json");
            _server.Routes["GET /workflows"] = (args, i) => FakeApi.Fixture("workflows.json");
            _server.Routes["POST /search/assets"] = (args, i) => FakeApi.Json(new JObject { ["hits"] = new JArray(), ["estimatedTotalHits"] = 0, ["offset"] = 0 });
            _server.Routes["GET /assets"] = (args, i) => FakeApi.Json(new JObject
            {
                ["assets"] = new JArray(new JObject { ["id"] = "asset_audio0001", ["kind"] = "audio", ["prompt"] = "Sword swing whoosh", ["mimeType"] = "audio/wav" }),
            });
            _server.Routes["GET /jobs"] = (args, i) => FakeApi.Fixture("jobs.json");
            _s = ScenarioService.UseForTests(_server, TempDir.Make("ui"));
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings == null) return;
            if (_window != null) _window.Close();
            ScenarioService.ResetForTests();
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
            ScenarioUserSettings.instance.Save();
        }

        static IEnumerator Frames(int count = 4)
        {
            for (var i = 0; i < count; i++) yield return null;
        }

        VisualElement Q(string name) => _window.rootVisualElement.Q(name);

        /// <summary>Frames until the condition holds: the price check waits 400 ms after the last change.</summary>
        static IEnumerator Until(System.Func<bool> condition, double seconds = 10)
        {
            var end = EditorApplication.timeSinceStartup + seconds;
            while (!condition() && EditorApplication.timeSinceStartup < end) yield return null;
        }

        JToken LastPriceParameter(string name)
        {
            var call = _server.Calls.LastIndexOf("POST /generate/custom/{id}");
            return call < 0 ? null : _server.Arguments[call][name];
        }

        void NoPaidCall()
        {
            for (var i = 0; i < _server.Calls.Count; i++)
                if (_server.Calls[i] == "POST /generate/custom/{id}" || _server.Calls[i] == "PUT /workflows/{id}/run" || _server.Calls[i] == "POST /generate/prompt")
                    Assert.AreEqual(true, (bool?)_server.Arguments[i]["dryRun"], $"{_server.Calls[i]} without dry_run");
        }

        [Test]
        public void CachedThumbnail_IsSetOnATileNotYetInTheWindow()
        {
            // A thumbnail already on disk loads at once, before the Library adds the new tile to the window.
            var key = "asset_thumbtest" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var path = System.IO.Path.Combine("Library/Scenario/thumbs", Files.Slug(key, 80) + ".img");
            System.IO.Directory.CreateDirectory("Library/Scenario/thumbs");
            var image = new Texture2D(4, 4);
            System.IO.File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            try
            {
                var tile = new VisualElement();
                Thumbnails.Load(tile, key, "https://cdn.cloud.scenario.com/assets/" + key);
                Assert.IsNotNull(tile.style.backgroundImage.value.texture, "the tile shows the cached thumbnail");
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [UnityTest]
        public IEnumerator SignedOut_ShowsSignIn_AndTheKeyForm()
        {
            _window = ScenarioWindow.Open();
            yield return Frames();
            Assert.IsNotNull(Q("sign-in-browser") ?? Q("use-api-key"), "the sign-in view is up");
            var useKey = Q("use-api-key");
            Assert.IsNotNull(useKey);
            var mouseUp = new Event { type = EventType.MouseUp, mousePosition = useKey.worldBound.center, button = 0, clickCount = 1 };
            using (var click = ClickEvent.GetPooled(mouseUp))
            {
                click.target = useKey;
                useKey.SendEvent(click);
            }
            yield return Frames();
            Assert.IsTrue(_window.rootVisualElement.Query<TextField>().ToList().Count >= 2, "key and secret fields");
            Assert.AreEqual(0, _server.Calls.Count, "nothing is sent before sign-in");
        }

        [UnityTest]
        public IEnumerator SignedOut_AnotherDevice_ShowsTheCodeToEnter_AndCancelBringsTheButtonsBack()
        {
            _server.Other = request => request.Url == OAuthApp.Production.DeviceAuthorizationEndpoint
                ? new HttpReply
                {
                    Status = 200,
                    Body = "{\"device_code\":\"dc_1\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://id.example.test/device\",\"expires_in\":600,\"interval\":5}",
                }
                : new HttpReply { Status = 400, Body = "{\"error\":\"authorization_pending\"}" };
            // The real network answers later, on a worker thread: the code reaches the view through the main thread.
            _s = ScenarioService.UseForTests(new WorkerThreadTransport(_server), TempDir.Make("ui-device"));
            _window = ScenarioWindow.Open();
            yield return Frames();
            var link = Q("signin-device");
            Assert.IsNotNull(link, "the sign-in view offers another device");
            var mouseUp = new Event { type = EventType.MouseUp, mousePosition = link.worldBound.center, button = 0, clickCount = 1 };
            using (var click = ClickEvent.GetPooled(mouseUp))
            {
                click.target = link;
                link.SendEvent(click);
            }
            yield return Until(() => Q<Label>("device-code")?.text == "ABCD-EFGH");
            Assert.AreEqual("ABCD-EFGH", Q<Label>("device-code").text);
            Assert.AreEqual(DisplayStyle.Flex, Q("device-code-box").style.display.value);
            Assert.AreEqual(DisplayStyle.None, Q("signin-browser").style.display.value, "one action at a time");

            _s.CancelSignIn();
            // The window refreshes through its panel scheduler.
            yield return Until(() => Q("signin-browser").style.display.value == DisplayStyle.Flex);
            Assert.AreEqual(DisplayStyle.Flex, Q("signin-browser").style.display.value, "Cancel brings the sign-in buttons back");
            Assert.AreEqual(DisplayStyle.Flex, Q("signin-device").style.display.value);
            Assert.AreEqual(DisplayStyle.None, Q("device-code-box").style.display.value);
            Assert.AreEqual(0, _server.Calls.Count, "nothing is sent to Scenario before sign-in");
        }

        T Q<T>(string name) where T : VisualElement => _window.rootVisualElement.Q<T>(name);

        [UnityTest]
        public IEnumerator SignedIn_EveryViewAndOverlayBuilds_WithoutSpending()
        {
            Assert.IsTrue(Run.Sync(_s.SignInWithApiKey("key_test", "secret_test")), _s.SignInError);
            ScenarioUserSettings.instance.View = "create";
            _window = ScenarioWindow.Open();
            _window.position = new Rect(40, 40, 460, 780);
            yield return Frames(8);

            foreach (var type in OutputTypes.List.Select(t => t.Id))
            {
                _window.Create.SelectType(type, true);
                yield return Frames(2);
            }
            _window.Create.SelectType(OutputTypes.Image, true);
            _window.Create.SetPrompt("A lantern on a mossy rock");
            yield return Frames(4);
            var improve = Q<Button>("improve");
            Assert.IsNotNull(improve);
            Assert.AreEqual(DisplayStyle.Flex, improve.resolvedStyle.display, "Prompt Spark is offered for a model whose prompt field allows it");

            foreach (var id in new[] { "library", "jobs", "workflows", "agent", "settings", "create" })
            {
                _window.Show(id);
                yield return Frames(4);
                Assert.IsNotNull(_window.ViewForTests(id).panel, $"{id} is shown");
            }

            // Scenario's search has no audio: the Audio filter lists the project's assets by type.
            _window.Show("library");
            ((LibraryView)_window.ViewForTests("library")).SelectKind("audio");
            yield return Frames(6);
            var listCall = _server.Calls.LastIndexOf("GET /assets");
            Assert.GreaterOrEqual(listCall, 0, "the assets were listed");
            CollectionAssert.Contains(((string)_server.Arguments[listCall]["types"]).Split(','), "txt2audio");
            Assert.IsNotNull(Q("asset-asset_audio0001"), "the audio asset shows");
            _window.Show("create");
            yield return Frames(2);

            _window.OpenPalette();
            yield return Frames(2);
            Assert.IsTrue(_window.HasOverlay, "the command palette opens");
            var search = Q<TextField>("palette-search");
            Assert.IsNotNull(search);
            search.value = "sky";
            yield return Frames(2);
            _window.CloseOverlay();
            Assert.IsFalse(_window.HasOverlay);

            _window.OpenOverlay(new ModelBrowser(_window, OutputTypes.Image, "", _ => { }));
            yield return Frames(4);
            Assert.IsNotNull(Q<TextField>("model-search"), "the model browser opens");
            _window.CloseOverlay();

            _window.Toast("Imported.", "success");
            _window.Toast("Price unavailable.", "error");
            yield return Frames(2);

            // Wide layout.
            _window.position = new Rect(40, 40, 1040, 720);
            yield return Frames(6);
            Assert.IsTrue(_window.rootVisualElement.ClassListContains("sc-wide") || _window.rootVisualElement.resolvedStyle.width < 560);

            NoPaidCall();
        }

        static JObject Model(string id, string name, params string[] caps) => new JObject
        {
            ["id"] = id, ["name"] = name, ["capabilities"] = new JArray(caps), ["privacy"] = "public", ["status"] = "trained", ["type"] = "custom",
        };

        static JObject Schema(string id, params JObject[] inputs) => new JObject { ["model"] = new JObject { ["id"] = id, ["name"] = id, ["inputs"] = new JArray(inputs) } };

        static readonly JObject CharacterInput = new JObject { ["name"] = "model", ["type"] = "file", ["kind"] = "3d", ["required"] = true, ["description"] = "The 3D model (GLB)" };

        [UnityTest]
        public IEnumerator ThreeDSteps_OfferTheirModels_AndMeshyMotionsByName()
        {
            _server.Routes["GET /models"] = (args, i) => FakeApi.Json(new JObject
            {
                ["models"] = new JArray(
                    Model("model_meshy-rigging", "Meshy Rigging", "3d23d"), Model("model_uthana-character-rigging", "Uthana Character Rigging", "3d23d"),
                    Model("model_cartwheel-character-rigging", "Cartwheel Character Rigging", "3d23d"), Model("model_tripo-rigging-v1", "Tripo Rigging 1.0 [Biped]", "3d23d"),
                    Model("model_meshy-animation", "Meshy Animation", "3d23d"), Model("model_tencent-texture-edit", "Tencent Texture Edit", "3d23d")),
            });
            _server.Routes["GET /models/{id}"] = (args, i) =>
            {
                var id = (string)args["id"];
                if (id == "model_meshy-animation")
                    return FakeApi.Json(Schema(id, CharacterInput, new JObject
                    {
                        ["name"] = "actionId", ["type"] = "number", ["required"] = true, ["default"] = 0, ["min"] = 0, ["step"] = 1,
                        ["description"] = "The animation to apply, given as its ID number from Meshy's animation library.",
                    }));
                if (id == "model_tencent-texture-edit")
                    return FakeApi.Json(Schema(id, new JObject { ["name"] = "file3d", ["type"] = "file", ["kind"] = "3d", ["required"] = true, ["description"] = "Source 3D model file. Supported format: FBX." },
                        new JObject { ["name"] = "prompt", ["type"] = "string", ["prompt"] = true }));
                return FakeApi.Json(Schema(id, CharacterInput));
            };
            var converted = 0;
            _server.Routes["POST /assets/{id}/download"] = (args, i) =>
            {
                if ((string)args["targetFormat"] == "fbx") converted++;
                return FakeApi.Json(new JObject { ["url"] = "https://cdn.cloud.scenario.com/assets/" + (string)args["id"] });
            };
            _server.DownloadContent = url => System.Text.Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0test");
            _server.Routes["POST /uploads"] = (args, i) => FakeApi.Json(new JObject { ["upload"] = new JObject { ["id"] = "upload_fbx", ["parts"] = new JArray() } });
            _server.Routes["POST /uploads/{id}/action"] = (args, i) => FakeApi.Json(new JObject { ["upload"] = new JObject { ["status"] = "complete" } });
            _server.Routes["GET /uploads/{id}"] = (args, i) => FakeApi.Json(new JObject { ["upload"] = new JObject { ["status"] = "imported", ["entityId"] = "asset_fbxcopy01" } });
            Assert.IsTrue(Run.Sync(_s.SignInWithApiKey("key_test", "secret_test")), _s.SignInError);
            Run.Sync(_s.RefreshCatalog(true));
            _window = ScenarioWindow.Open();
            _window.position = new Rect(40, 40, 460, 780);
            yield return Frames(6);

            // A step offers the models that do it.
            var character = new SlotItem { AssetId = "asset_char0001", Kind = "3d", Label = "Hero" };
            _window.Create.StartStep(OutputTypes.Step("rig"), character);
            yield return Frames(8);
            var hint = Q("step-hint");
            Assert.IsNotNull(hint, "the step says what it does");
            var labels = hint.parent.Query<Label>().ToList().Select(l => l.text).ToList();
            foreach (var name in new[] { "Meshy Rigging", "Uthana Character Rigging", "Cartwheel Character Rigging", "Tripo Rigging 1.0 [Biped]" })
                CollectionAssert.Contains(labels, name);

            // Meshy's library by name: pick a walk, the price check sends its id.
            _window.Create.StartStep(OutputTypes.Step("animate-3d"), new SlotItem { AssetId = "asset_char0001", Kind = "3d", Label = "Hero" });
            yield return Frames(8);
            var current = Q<Button>("motion-current");
            Assert.IsNotNull(current, "the motion picker replaces the number field");
            StringAssert.Contains("Idle", current.Q<Label>().text);
            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = current;
                current.SendEvent(submit);
            }
            yield return Frames(2);
            Q<TextField>("motion-search").value = "casual walk";
            yield return Frames(2);
            var list = Q<ListView>("motion-list");
            Assert.Greater(list.itemsSource.Count, 0);
            var index = list.itemsSource.Cast<MotionLibrary.Motion>().ToList().FindIndex(m => m.Id == 30);
            list.SetSelection(index);
            yield return Until(() => LastPriceParameter("actionId")?.ToString() == "30");
            StringAssert.Contains("Casual Walk", current.Q<Label>().text);
            Assert.AreEqual(30, (int)LastPriceParameter("actionId"));

            // An input that refuses GLB gets an FBX copy before the price check.
            _window.Create.StartStep(OutputTypes.Step("retexture"), new SlotItem { AssetId = "asset_char0001", Kind = "3d", Label = "Hero" });
            yield return Frames(2);
            _window.Create.SelectModel("model_tencent-texture-edit");
            yield return Until(() => (string)LastPriceParameter("file3d") == "asset_fbxcopy01");
            Assert.AreEqual(1, converted, "converted once");
            Assert.AreEqual("asset_fbxcopy01", (string)LastPriceParameter("file3d"));

            NoPaidCall();
        }
    }
}
