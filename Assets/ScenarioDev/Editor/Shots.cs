using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Scenario.Editor;
using Scenario.Editor.Agent;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): walks the Scenario window through every view and
    /// overlay in batch mode and saves one PNG per state, in the editor's current skin.
    /// Run with -batchmode (graphics on) -executeMethod ScenarioDev.Shots.SignedOut or .SignedIn.
    /// Never generates: no step calls Generate, and batch mode gets no keyboard input.
    /// </summary>
    public static class Shots
    {
        sealed class Step
        {
            public string Name;
            public Action Setup;
            public float MinWait = 1.5f;
            public float MaxWait = 20f;
            public Vector2 Size = new Vector2(460, 780);
            public bool BothThemes = true;
            /// <summary>The window to capture, when it is not the Scenario window.</summary>
            public Func<EditorWindow> Capture;
        }

        static readonly List<Step> Steps = new List<Step>();
        static int _index;
        static double _started;
        static bool _setupDone;
        static ScenarioWindow _window;
        static string _out;
        static string _theme;
        static readonly List<string> Report = new List<string>();

        static ScenarioService S => ScenarioService.Instance;

        public static void SignedOut()
        {
            Steps.Clear();
            Add("signin", () => { });
            Add("signin-apikey", () => Click(Q("use-api-key")));
            Add("signin-narrow", () => { }, size: new Vector2(340, 560));
            Begin();
        }

        public static void SignedIn()
        {
            Steps.Clear();
            // Long enough for the project name and the day's spend to load into the header.
            Add("create-image", () => { _window.Show("create"); _window.Create.SelectType("image", true); }, min: 12, max: 40);
            Add("create-image-prompt", () => _window.Create.SetPrompt("A mossy stone golem guarding a forest shrine, stylized game art, soft rim light"), min: 3);
            Add("create-suggest", () => Click(Q("suggest")), min: 6, max: 40);
            Add("create-model3d", () => _window.Create.SelectType("model3d"), min: 4);
            Add("create-material", () => _window.Create.SelectType("material"), min: 4);
            Add("create-skybox", () => _window.Create.SelectType("skybox"), min: 4);
            Add("create-video", () => _window.Create.SelectType("video"), min: 4);
            Add("create-audio", () => _window.Create.SelectType("audio"), min: 4);
            Add("create-all", () => _window.Create.SelectType("all"), min: 4);
            Add("step-to-3d", () =>
            {
                // A typed step on a generated image: the image fills the input slot and the price shows.
                var row = S.Ledger.Jobs.LastOrDefault(r => r.OutputType == "image" && r.Imported && r.Files.Count > 0 && r.AssetIds.Count > 0);
                if (row == null) { Report.Add("no generated image for the step"); return; }
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(row.Files[0]);
                _window.Create.StartStep(OutputTypes.Step("to-3d"), new SlotItem { AssetId = row.AssetIds[0], Kind = "image", Source = texture, Label = texture != null ? texture.name : "image" });
            }, min: 6, max: 40);
            Add("create-motion", () => _window.Create.SelectType("motion"), min: 4);
            // The 3D steps on a generated model: each prices against Scenario with the model in its slot.
            Add("step-retexture", () => StartModelStep("retexture"), min: 6, max: 40);
            Add("step-remesh", () => StartModelStep("remesh"), min: 6, max: 40);
            Add("step-rig", () => StartModelStep("rig"), min: 6, max: 40);
            Add("step-animate-library", () => StartModelStep("animate-3d"), min: 6, max: 40);
            Add("motion-picker", () => Click(Q("motion-current")), min: 2);
            Add("motion-picker-search", () => { var f = Q<TextField>("motion-search"); if (f != null) f.value = "sword"; }, min: 2);
            Add("create-more-settings", () => { _window.CloseOverlay(); _window.Create.SelectType("image"); _window.Create.SelectModel("model_openai-gpt-image-2-5-flare"); }, min: 4);
            Add("create-more-settings-open", () => Click(Q("more-settings")), min: 1.5f);
            Add("model-browser", () => Click(Q("change-model")), min: 3);
            Add("model-browser-search", () => { var f = Q<TextField>("model-search"); if (f != null) f.value = "flux"; }, min: 1.5f);
            Add("palette", () => { _window.CloseOverlay(); _window.OpenPalette(); }, min: 1.5f);
            Add("palette-search", () => { var f = Q<TextField>("palette-search"); if (f != null) f.value = "sky"; }, min: 1.5f);
            Add("toasts", () =>
            {
                _window.CloseOverlay();
                _window.Toast("Golem.png imported to Assets/Scenario/Images.", "success");
                _window.Toast("Claude Code asks to spend 12 CU: generate a texture.", "warning", "Review", () => { }, 30);
                _window.Toast("Price unavailable: the request timed out.", "error");
            }, min: 1);
            // Long enough for the Scenario thumbnails to arrive.
            Add("library-scenario", () => _window.Show("library"), min: 16, max: 30);
            Add("library-project", () => Click(Q("tab-project")), min: 2);
            Add("library-audio", () =>
            {
                Click(Q("tab-scenario"));
                var library = _window.ViewForTests("library");
                Click(library.Query<Label>().Where(l => l.text == "Audio" && l.parent.ClassListContains("sc-chip")).First()?.parent);
            }, min: 6, max: 30);
            Add("jobs", () => _window.Show("jobs"), min: 2);
            Add("workflows-team", () => _window.Show("workflows"), min: 5, max: 30);
            Add("workflows-featured", () => Click(Q("tab-public") ?? FindByText("Featured")), min: 5, max: 30);
            Add("workflow-detail", () => Click(_window.rootVisualElement.Query<VisualElement>().Where(e => e.name != null && e.name.StartsWith("workflow-") && e.name != "workflow-run").First()), min: 6, max: 30);
            Add("agent-off", () => { _window.Show("agent"); }, min: 1.5f);
            Add("agent-on", () => AgentServer.SetEnabled(true), min: 2);
            Add("agent-restore", () => AgentServer.SetEnabled(false), min: 1);
            Add("settings", () => _window.Show("settings"), min: 2);
            Add("settings-diagnostics", () => Click(Q("run-diagnostics")), min: 12, max: 40);
            Add("create-wide", () => { _window.Show("create"); FormToTop(); }, min: 4, size: new Vector2(1040, 720));
            Add("library-wide", () =>
            {
                _window.Show("library");
                Click(_window.ViewForTests("library").Query<Label>().Where(l => l.text == "All" && l.parent.ClassListContains("sc-chip")).First()?.parent);
            }, min: 6, size: new Vector2(1040, 720));
            Add("hero", () =>
            {
                // The README image: a neutral workspace name (restored at the end) and no test pattern.
                _projectName = ScenarioUserSettings.instance.ProjectName;
                ScenarioUserSettings.instance.ProjectName = "Treasure Hunt";
                typeof(ScenarioWindow).GetMethod("UpdateHeader", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_window, null);
                foreach (var row in S.Ledger.Jobs.Where(r => r.Prompt == "acceptance-source").ToList()) S.Dismiss(row);
                _window.Show("create");
                _window.Create.SelectType("image", true);
                _window.Create.SetPrompt("A stylized wooden treasure chest, game prop, clean background");
                FormToTop();
            }, min: 8, max: 40, size: new Vector2(1040, 720));
            Add("inspector-generated", () =>
            {
                var row = S.Ledger.Jobs.LastOrDefault(r => r.OutputType == "image" && r.Imported && r.Files.Count > 0);
                if (row != null) Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(row.Files[0]);
                Inspector().position = new Rect(520, 40, 420, 560);
            }, min: 3, capture: Inspector);
            Add("inspector-model", () =>
            {
                var row = LastModel();
                if (row != null) Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(row.Files.First(f => f.EndsWith(".glb")));
                Inspector().position = new Rect(520, 40, 420, 560);
            }, min: 4, capture: Inspector);
            Begin();
        }

        /// <summary>A generated character for the 3D step shots: the knight from the paid acceptance run when there.</summary>
        /// <summary>Earlier steps leave the form scrolled to its settings.</summary>
        static void FormToTop() => _window.rootVisualElement.schedule.Execute(() =>
        {
            foreach (var scroll in _window.ViewForTests("create").Query<ScrollView>().ToList()) scroll.scrollOffset = Vector2.zero;
        }).ExecuteLater(300);

        static JobRow LastModel()
        {
            var models = S.Ledger.Jobs.Where(r => r.OutputType == "model3d" && r.Imported && r.AssetIds.Count > 0 && r.Files.Any(f => f.EndsWith(".glb"))).ToList();
            return models.FirstOrDefault(r => r.Files.Any(f => f.Contains("knight"))) ?? models.FirstOrDefault();
        }

        static void StartModelStep(string stepId)
        {
            var row = LastModel();
            if (row == null) { Report.Add("no generated model for " + stepId); return; }
            var file = row.Files.First(f => f.EndsWith(".glb"));
            var prefab = AssetDatabase.LoadMainAssetAtPath(file);
            _window.Create.StartStep(OutputTypes.Step(stepId), new SlotItem { AssetId = row.AssetIds[0], Kind = "3d", Source = prefab, Label = Path.GetFileNameWithoutExtension(file) });
        }

        static void Add(string name, Action setup, float min = 1.5f, float max = 20f, Vector2? size = null, bool both = true, Func<EditorWindow> capture = null)
            => Steps.Add(new Step { Name = name, Setup = setup, MinWait = min, MaxWait = max, Size = size ?? new Vector2(460, 780), BothThemes = both, Capture = capture });

        static EditorWindow _inspector;
        static string _projectName;

        static EditorWindow Inspector()
        {
            if (_inspector == null) _inspector = EditorWindow.GetWindow(typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.InspectorWindow"));
            return _inspector;
        }

        static void Begin()
        {
            _out = Environment.GetEnvironmentVariable("SCENARIO_SHOTS_DIR");
            if (string.IsNullOrEmpty(_out)) _out = "Library/ScenarioShots";
            Directory.CreateDirectory(_out);
            // Switching skin inside a batch editor stalls it: the launcher sets the skin before launch.
            _theme = EditorGUIUtility.isProSkin ? "dark" : "light";
            Debug.Log($"SHOTS begin theme={_theme} pro={EditorGUIUtility.isProSkin} steps={Steps.Count}");
            OpenWindow();
            _index = 0;
            if (_theme == "light") while (_index < Steps.Count && !Steps[_index].BothThemes) _index++;
            _setupDone = false;
            _started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        static void OpenWindow()
        {
            if (_window != null) _window.Close();
            _window = ScenarioWindow.Open();
            _window.position = new Rect(40, 40, 460, 780);
        }

        static void Tick()
        {
            try
            {
                if (_index >= Steps.Count)
                {
                    Finish();
                    return;
                }
                var step = Steps[_index];
                var now = EditorApplication.timeSinceStartup;
                if (!_setupDone)
                {
                    if (now - _started < 0.5) return;
                    var size = step.Size;
                    _window.position = new Rect(40, 40, size.x, size.y);
                    Debug.Log($"SHOTS step {step.Name}");
                    step.Setup();
                    _setupDone = true;
                    _started = now;
                    return;
                }
                var elapsed = now - _started;
                if (elapsed < step.MinWait) return;
                if (Busy() && elapsed < step.MaxWait) return;
                var path = Path.Combine(_out, $"{_theme}-{Steps.IndexOf(step) + 1:00}-{step.Name}.png");
                var error = WindowShots.Capture(step.Capture != null ? step.Capture() : _window, path);
                Report.Add($"{Path.GetFileName(path)} {(error.Length > 0 ? "ERROR " + error : "ok")} busy={Busy()} t={elapsed:0.0}");
                _index++;
                if (_theme == "light") while (_index < Steps.Count && !Steps[_index].BothThemes) _index++;
                _setupDone = false;
                _started = now;
            }
            catch (Exception e)
            {
                Report.Add($"step {_index} threw {e}");
                _index++;
                _setupDone = false;
            }
        }

        static void Finish()
        {
            EditorApplication.update -= Tick;
            if (_projectName != null) ScenarioUserSettings.instance.ProjectName = _projectName;
            foreach (var line in Report) Debug.Log("SHOTS " + line);
            Debug.Log($"SHOTS done theme={_theme}");
            File.WriteAllLines(Path.Combine(_out, $"report-{_theme}.txt"), Report);
            EditorApplication.Exit(0);
        }

        // --- helpers ---------------------------------------------------------------

        static VisualElement Q(string name) => _window.rootVisualElement.Q(name);
        static T Q<T>(string name) where T : VisualElement => _window.rootVisualElement.Q<T>(name);

        static VisualElement FindByText(string text)
            => _window.rootVisualElement.Query<Label>().Where(l => l.text == text).First();

        static bool Busy()
            => _window.rootVisualElement.Query<VisualElement>(className: "sc-spinner").ToList()
                .Any(s => s.resolvedStyle.display != DisplayStyle.None && s.panel != null && IsShown(s));

        static bool IsShown(VisualElement e)
        {
            for (var v = e; v != null; v = v.parent)
                if (v.resolvedStyle.display == DisplayStyle.None || v.resolvedStyle.visibility == Visibility.Hidden) return false;
            return true;
        }

        public static void Click(VisualElement element)
        {
            if (element == null)
            {
                Report.Add("click target missing");
                return;
            }
            if (element is Button button)
            {
                using (var submit = NavigationSubmitEvent.GetPooled())
                {
                    submit.target = button;
                    button.SendEvent(submit);
                }
                return;
            }
            if (element is Toggle toggle)
            {
                toggle.value = !toggle.value;
                return;
            }
            var center = element.worldBound.center;
            var system = new Event { type = EventType.MouseUp, mousePosition = center, button = 0, clickCount = 1 };
            using (var click = ClickEvent.GetPooled(system))
            {
                click.target = element;
                element.SendEvent(click);
            }
        }
    }
}
