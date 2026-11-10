using System;
using System.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// Scenario in the Scene view: a panel that follows the selection. Dock it, float it or
    /// collapse it to an icon; the Overlays menu (the Scene view's ⋮) shows or hides it.
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Scenario", defaultDisplay = true, defaultDockZone = DockZone.RightColumn)]
    public sealed class ScenarioOverlay : Overlay
    {
        public const string Id = "scenario-overlay";

        public override void OnCreated() => collapsedIcon = Icons.Get("scenario-mark");

        public override VisualElement CreatePanelContent() => new ScenarioPanel();

        /// <summary>Shows the overlay, expanded, in the last active Scene view.</summary>
        public static bool Reveal()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) view = EditorWindow.GetWindow<SceneView>();
            if (view == null || !view.TryGetOverlay(Id, out var overlay)) return false;
            view.Focus();
            overlay.displayed = true;
            overlay.collapsed = false;
            return true;
        }
    }

    /// <summary>
    /// The overlay's content. A placed 3D object: its steps grouped as in the catalog, then
    /// Make playable, Put on the ground and Set height. A terrain: Scatter here. An image or a
    /// sprite: the image steps. Nothing: Generate shortcuts. Ask Claude is always on top.
    /// Steps open Create (nothing is sent before Generate, which shows the price); scene
    /// actions run at once, each one Undo step.
    /// </summary>
    public sealed class ScenarioPanel : VisualElement
    {
        static Object _scatterSource;
        static int _scatterCount = 20;
        static string _mood = "golden_hour";
        static float _scatterArea = 40;

        readonly VisualElement _icon;
        readonly Label _title;
        readonly Label _subtitle;
        readonly VisualElement _body;
        readonly Label _status;
        SelectionContext _context = new SelectionContext();
        string _signature;
        double _statusTime;

        public ScenarioPanel()
        {
            name = "scenario-panel";
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(ScenarioWindow.StylePath);
            if (sheet != null) styleSheets.Add(sheet);
            AddToClassList("sc-root");
            AddToClassList(EditorGUIUtility.isProSkin ? "sc-dark" : "sc-light");
            style.width = 272;
            style.paddingLeft = style.paddingRight = 4;
            style.paddingTop = 2;
            style.paddingBottom = 4;

            var header = new VisualElement();
            header.AddToClassList("sc-row");
            header.style.marginBottom = 8;
            _icon = Ui.Icon("scenario-mark");
            _icon.style.marginRight = 8;
            header.Add(_icon);
            var titles = new VisualElement();
            titles.style.flexGrow = 1;
            titles.style.flexShrink = 1;
            _title = Ui.Text("Scenario", "sc-bold", "sc-ellipsis");
            _title.name = "panel-title";
            _subtitle = Ui.Text("", "sc-faint", "sc-small", "sc-ellipsis");
            _subtitle.name = "panel-subtitle";
            titles.Add(_title);
            titles.Add(_subtitle);
            header.Add(titles);
            var open = Ui.IconButton("external", "Open the Scenario window", () => ScenarioWindow.Open(), "sc-btn-sm");
            header.Add(open);
            Add(header);

            Add(new AskBox(4));
            _body = new VisualElement { name = "panel-body" };
            Add(_body);
            _status = Ui.Text("", "sc-small", "sc-wrap");
            _status.name = "panel-status";
            _status.style.marginTop = 6;
            _status.style.display = DisplayStyle.None;
            Add(_status);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                Selection.selectionChanged += Follow;
                EditorApplication.hierarchyChanged += Follow;
                Refresh(true);
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                Selection.selectionChanged -= Follow;
                EditorApplication.hierarchyChanged -= Follow;
            });
            Show(SceneActions.Current());
        }

        /// <summary>What the panel shows now.</summary>
        public SelectionContext Context => _context;

        void Follow() => Refresh(false);

        /// <summary>Rebuilds for the selection when what it offers changed (forced: always).</summary>
        public void Refresh(bool force)
        {
            var context = SceneActions.Current();
            if (!force && context.Signature == _signature) return;
            Show(context);
        }

        /// <summary>Builds the panel for a selection (tests pass one directly).</summary>
        public void Show(SelectionContext context)
        {
            var moved = context.Target != _context.Target || context.Source != _context.Source;
            _context = context;
            _signature = context.Signature;
            if (moved && EditorApplication.timeSinceStartup - _statusTime > 1) SetStatus("", true);

            Ui.SetIcon(_icon, IconFor(context));
            _title.text = context.Mode == SelectionContext.Nothing ? "Scenario" : context.Label;
            _subtitle.text = Subtitle(context);
            _body.Clear();
            switch (context.Mode)
            {
                case SelectionContext.Object3D:
                    AddSteps(context);
                    AddSceneActions(context);
                    break;
                case SelectionContext.TerrainMode:
                    AddScatter(context);
                    AddMood();
                    break;
                case SelectionContext.Image:
                case SelectionContext.Asset:
                    AddSteps(context);
                    break;
                default:
                    AddGenerate(context);
                    AddMood();
                    break;
            }
        }

        static string IconFor(SelectionContext c)
        {
            switch (c.Mode)
            {
                case SelectionContext.Object3D: return "cube";
                case SelectionContext.TerrainMode: return "layers";
                case SelectionContext.Image: return "image";
                case SelectionContext.Asset: return InputSlot.IconFor(c.Kind);
                default: return "scenario-mark";
            }
        }

        static string Subtitle(SelectionContext c)
        {
            var fromScenario = c.AssetId.Length > 0 ? " from Scenario" : "";
            switch (c.Mode)
            {
                case SelectionContext.Object3D: return (c.AssetId.Length > 0 ? "3D model" : "3D object") + fromScenario;
                case SelectionContext.TerrainMode: return "Terrain";
                case SelectionContext.Image: return (c.Source is Sprite ? "Sprite" : "Image") + fromScenario;
                case SelectionContext.Asset: return (c.Kind == "3d" ? "3D model" : c.Kind == "audio" ? "Sound" : "Video") + fromScenario;
                case SelectionContext.Other: return "No Scenario steps for this";
                default: return "Nothing selected";
            }
        }

        // --- Steps ------------------------------------------------------------------------

        void AddSteps(SelectionContext c)
        {
            var first = true;
            foreach (var group in c.Groups)
            {
                var key = "Scenario.Overlay.Open." + group.Title;
                var foldout = new Foldout { text = group.Title, value = EditorPrefs.GetBool(key, first), name = "group-" + Files.Slug(group.Title) };
                foldout.RegisterValueChangedCallback(e =>
                {
                    if (e.target == foldout) EditorPrefs.SetBool(key, e.newValue);
                });
                var chips = new VisualElement();
                chips.AddToClassList("sc-row-wrap");
                chips.style.marginTop = 4;
                foreach (var step in group.Steps)
                {
                    var s = step;
                    var chip = Ui.Chip(s.Title, null, false, () => SceneActions.OpenStep(s, _context), "sc-chip-sm");
                    chip.name = "step-" + s.Id;
                    chip.tooltip = (s.Hint.Length > 0 ? s.Hint + "\n\n" : "") + "Opens Create with the model and this asset. Nothing is sent until you press Generate, which shows the price.";
                    chips.Add(chip);
                }
                foldout.Add(chips);
                _body.Add(foldout);
                first = false;
            }
        }

        // --- Scene actions ----------------------------------------------------------------

        void AddSceneActions(SelectionContext c)
        {
            _body.Add(Ui.Section("Scene"));
            var playable = Ui.Button("Make playable", () => Act(SceneActions.MakePlayable(_context.Target), select: true), "play", "sc-btn-sm");
            playable.name = "action-playable";
            playable.tooltip = "A third-person player: idle, walk, run and jump on a CharacterController, with a follow camera. Press Play to try it.";
            playable.SetEnabled(c.CanMakePlayable);
            var ground = Ui.Button("Put on the ground", () => Act(SceneActions.PutOnGround(_context.Target)), null, "sc-btn-sm");
            ground.name = "action-ground";
            ground.style.marginLeft = 4;
            _body.Add(Ui.Row(playable, ground));
            if (!c.CanMakePlayable)
            {
                var issue = Ui.Text(c.PlayableIssue, "sc-faint", "sc-small", "sc-wrap");
                issue.name = "playable-issue";
                issue.style.marginTop = 3;
                _body.Add(issue);
            }

            var height = new FloatField { name = "height-field", value = (float)Math.Round(SceneActions.Height(c.Target), 2) };
            height.style.width = 64;
            height.style.marginLeft = 6;
            height.tooltip = "Height in meters. The object is scaled evenly; its base stays where it is.";
            var set = Ui.Button("Set", () => Act(SceneActions.SetHeight(_context.Target, height.value)), null, "sc-btn-sm");
            set.name = "action-height";
            set.style.marginLeft = 4;
            height.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation();
                height.schedule.Execute(() => Act(SceneActions.SetHeight(_context.Target, height.value)));
            }, TrickleDown.TrickleDown);
            var row = Ui.Row(Ui.Text("Height (m)", "sc-dim", "sc-small"), height, set);
            row.style.marginTop = 6;
            _body.Add(row);

            var walkable = Ui.Button("Make walkable", () => Act(SceneActions.MakeWalkable(_context.Target)), null, "sc-btn-sm");
            walkable.name = "action-walkable";
            walkable.tooltip = "For a bridge, stairs or a platform: an invisible walkway on its deck (holes filled, bumps smoothed), side rails, and ramps to the ground. Run it again after moving it.";
            walkable.style.alignSelf = Align.FlexStart;
            walkable.style.marginTop = 6;
            _body.Add(walkable);
        }

        void AddMood()
        {
            _body.Add(Ui.Section("Light and mood"));
            var titles = SceneActions.Moods.Select(SceneActions.MoodTitle).ToList();
            var presets = SceneActions.Moods.ToList();
            var pick = new DropdownField(titles, Math.Max(0, presets.IndexOf(_mood))) { name = "mood-preset" };
            pick.style.flexGrow = 1;
            pick.style.flexShrink = 1;
            pick.RegisterValueChangedCallback(e => _mood = presets[Math.Max(0, titles.IndexOf(e.newValue))]);
            var apply = Ui.Button("Apply", () => Act(SceneActions.SetMood(_mood)), null, "sc-btn-sm");
            apply.name = "mood-apply";
            apply.style.marginLeft = 4;
            apply.tooltip = "Sets the sun, fog and ambient light, and in URP a post-processing volume. One Undo step.";
            _body.Add(Ui.Row(pick, apply));
        }

        void AddScatter(SelectionContext c)
        {
            _body.Add(Ui.Section("Scatter here"));
            var source = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = false, value = _scatterSource, name = "scatter-source" };
            source.tooltip = "A prefab or a 3D model from the Project window.";
            source.style.flexGrow = 1;
            source.style.flexShrink = 1;
            source.style.marginLeft = 6;
            source.RegisterValueChangedCallback(e => _scatterSource = e.newValue);
            _body.Add(Ui.Row(Caption("Model", 44), source));

            var count = new IntegerField { value = _scatterCount, name = "scatter-count" };
            count.style.width = 52;
            count.RegisterValueChangedCallback(e => _scatterCount = e.newValue);
            var area = new FloatField { value = _scatterArea, name = "scatter-area" };
            area.style.width = 52;
            area.tooltip = "Side of the square area, in meters.";
            area.RegisterValueChangedCallback(e => _scatterArea = e.newValue);
            var sizes = Ui.Row(Caption("Count", 44), count, Caption("Area (m)", 0), area);
            sizes.style.marginTop = 4;
            sizes[2].style.marginLeft = 10;
            _body.Add(sizes);

            var run = Ui.Button("Scatter", () =>
            {
                var pivot = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : (Vector3?)null;
                Act(SceneActions.Scatter(_context.Terrain, source.value, count.value, area.value, pivot), ping: true);
            }, "grid", "sc-btn-sm");
            run.name = "scatter-run";
            run.style.alignSelf = Align.FlexStart;
            run.style.marginTop = 6;
            _body.Add(run);
            _body.Add(Ui.Text("Around the Scene view's center on this terrain, sat on the ground, with colliders. One Undo step.", "sc-faint", "sc-small", "sc-wrap"));
        }

        static Label Caption(string text, float width)
        {
            var label = Ui.Text(text, "sc-dim", "sc-small");
            if (width > 0) label.style.width = width;
            return label;
        }

        void AddGenerate(SelectionContext c)
        {
            _body.Add(Ui.Section("Generate"));
            var chips = new VisualElement();
            chips.AddToClassList("sc-row-wrap");
            foreach (var type in SceneActions.Shortcuts)
            {
                var t = OutputTypes.Get(type);
                var title = type == OutputTypes.Model3D ? "3D model" : type == OutputTypes.Audio ? "Sound" : t.Title;
                var chip = Ui.Chip(title, t.Icon, false, () => SceneActions.OpenType(t.Id), "sc-chip-sm");
                chip.name = "generate-" + type;
                chip.tooltip = t.Hint;
                chips.Add(chip);
            }
            _body.Add(chips);
            _body.Add(Ui.Text(c.Mode == SelectionContext.Other
                ? "Select a model, a terrain, an image or a sprite for its steps."
                : "Or select a model, a terrain or an image for its steps.", "sc-faint", "sc-small", "sc-wrap"));
        }

        // --- Outcomes ---------------------------------------------------------------------

        void Act(SceneActions.Outcome outcome, bool select = false, bool ping = false)
        {
            SceneActions.Report(outcome);
            SetStatus(outcome.Message, outcome.Ok);
            if (outcome.Ok && outcome.Object != null)
            {
                if (select) Selection.activeGameObject = outcome.Object;
                else if (ping) EditorGUIUtility.PingObject(outcome.Object);
            }
            Refresh(true);
        }

        void SetStatus(string message, bool ok)
        {
            _statusTime = message.Length > 0 ? EditorApplication.timeSinceStartup : 0;
            _status.text = message;
            _status.style.display = message.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _status.EnableInClassList("sc-dim", ok);
            _status.EnableInClassList("sc-warning-text", !ok);
        }
    }
}
