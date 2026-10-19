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
        FakeMcp _server;
        ScenarioService _s;
        ScenarioWindow _window;
        string _settings;

        [SetUp]
        public void SetUp()
        {
            // Editor windows need a graphics device: CI runs with -nographics skip these.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("The window tests need a graphics device (run without -nographics).");
            _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);
            _server = new FakeMcp();
            _server.Tools["teams_list"] = (args, i) => FakeMcp.Fixture("teams.sse.txt");
            _server.Tools["models_list"] = (args, i) => FakeMcp.Fixture("models_page.sse.txt");
            _server.Tools["model_schema_get"] = (args, i) => FakeMcp.Fixture("schema_flare.sse.txt");
            _server.Tools["model_run"] = (args, i) => FakeMcp.Fixture("estimate.sse.txt");
            _server.Tools["recommend"] = (args, i) => FakeMcp.Fixture("recommend_3d.sse.txt");
            _server.Tools["workflows_list"] = (args, i) => FakeMcp.Fixture("workflows.sse.txt");
            _server.Tools["search"] = (args, i) => FakeMcp.Result(new JObject { ["assets"] = new JArray(), ["estimatedTotalHits"] = 0, ["offset"] = 0 });
            _server.Tools["assets_list"] = (args, i) => FakeMcp.Result(new JObject
            {
                ["assets"] = new JArray(new JObject { ["id"] = "asset_audio0001", ["kind"] = "audio", ["prompt"] = "Sword swing whoosh", ["mimeType"] = "audio/wav" }),
            });
            _server.Tools["jobs_list"] = (args, i) => FakeMcp.Fixture("jobs.sse.txt");
            _s = ScenarioService.UseForTests(_server, TempDir.Make("ui"));
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings == null) return;
            if (_window != null) _window.Close();
            ScenarioService.ResetForTests();
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
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
            var call = _server.ToolCalls.LastIndexOf("model_run");
            return call < 0 ? null : _server.ToolArguments[call]["parameters"]?[name];
        }

        void NoPaidCall()
        {
            for (var i = 0; i < _server.ToolCalls.Count; i++)
                if (_server.ToolCalls[i] == "model_run" || _server.ToolCalls[i] == "workflow_run" || _server.ToolCalls[i] == "prompt_spark")
                    Assert.AreEqual(true, (bool?)_server.ToolArguments[i]["dry_run"], $"{_server.ToolCalls[i]} without dry_run");
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
            Assert.AreEqual(0, _server.ToolCalls.Count, "nothing is sent before sign-in");
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
            var listCall = _server.ToolCalls.LastIndexOf("assets_list");
            Assert.GreaterOrEqual(listCall, 0, "assets_list was called");
            CollectionAssert.Contains(_server.ToolArguments[listCall]["filters"]["types"].Select(t => (string)t), "txt2audio");
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

        static JObject Schema(string id, params JObject[] parameters) => new JObject { ["model_id"] = id, ["name"] = id, ["parameters"] = new JArray(parameters) };

        static readonly JObject CharacterInput = new JObject { ["name"] = "model", ["type"] = "file", ["kind"] = "3d", ["required"] = true, ["description"] = "The 3D model (GLB)" };

        [UnityTest]
        public IEnumerator ThreeDSteps_OfferTheirModels_AndMeshyMotionsByName()
        {
            _server.Tools["models_list"] = (args, i) => FakeMcp.Result(new JObject
            {
                ["models"] = new JArray(
                    Model("model_meshy-rigging", "Meshy Rigging", "3d23d"), Model("model_uthana-character-rigging", "Uthana Character Rigging", "3d23d"),
                    Model("model_cartwheel-character-rigging", "Cartwheel Character Rigging", "3d23d"), Model("model_tripo-rigging-v1", "Tripo Rigging 1.0 [Biped]", "3d23d"),
                    Model("model_meshy-animation", "Meshy Animation", "3d23d"), Model("model_tencent-texture-edit", "Tencent Texture Edit", "3d23d")),
            });
            _server.Tools["model_schema_get"] = (args, i) =>
            {
                var id = (string)args["model_id"];
                if (id == "model_meshy-animation")
                    return FakeMcp.Result(Schema(id, CharacterInput, new JObject
                    {
                        ["name"] = "actionId", ["type"] = "number", ["required"] = true, ["default"] = 0, ["min"] = 0, ["step"] = 1,
                        ["description"] = "The animation to apply, given as its ID number from Meshy's animation library.",
                    }));
                if (id == "model_tencent-texture-edit")
                    return FakeMcp.Result(Schema(id, new JObject { ["name"] = "file3d", ["type"] = "file", ["kind"] = "3d", ["required"] = true, ["description"] = "Source 3D model file. Supported format: FBX." },
                        new JObject { ["name"] = "prompt", ["type"] = "string", ["prompt"] = true }));
                return FakeMcp.Result(Schema(id, CharacterInput));
            };
            var converted = 0;
            _server.Tools["asset_download"] = (args, i) =>
            {
                if ((string)args["format"] == "fbx") converted++;
                return FakeMcp.Result(new JObject { ["url"] = "https://cdn.cloud.scenario.com/assets/" + (string)args["asset_id"] });
            };
            _server.DownloadContent = url => System.Text.Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0test");
            _server.Tools["upload_asset"] = (args, i) => FakeMcp.Result(new JObject { ["asset_id"] = "asset_fbxcopy01", ["parts"] = new JArray() });
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
