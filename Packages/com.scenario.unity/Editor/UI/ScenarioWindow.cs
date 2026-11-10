using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Agent;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>Base for the window's views.</summary>
    public abstract class View : VisualElement
    {
        protected readonly ScenarioWindow Window;
        protected ScenarioService S => ScenarioService.Instance;

        protected View(ScenarioWindow window)
        {
            Window = window;
            AddToClassList("sc-view");
        }

        public abstract string Id { get; }
        /// <summary>Called when the view becomes visible.</summary>
        public virtual void OnShow() { }
        /// <summary>Called when the service state changed (at most once per frame).</summary>
        public virtual void Refresh() { }
        public virtual void OnSelectionChange() { }
    }

    /// <summary>
    /// The Scenario window: header, navigation rail, one view at a time, overlays
    /// (model browser, command palette) and toasts.
    /// </summary>
    public sealed class ScenarioWindow : EditorWindow
    {
        public const string StylePath = "Packages/com.scenario.unity/Editor/UI/Styles/Scenario.uss";

        static readonly (string id, string label, string icon)[] Nav =
        {
            ("create", "Create", "create"),
            ("library", "Library", "library"),
            ("jobs", "Jobs", "jobs"),
            ("workflows", "Workflows", "workflows"),
            ("agent", "Agent", "agent"),
            ("settings", "Settings", "settings"),
        };

        VisualElement _root;
        VisualElement _header;
        VisualElement _body;
        VisualElement _rail;
        VisualElement _content;
        VisualElement _toasts;
        VisualElement _overlay;
        Label _workspaceText;
        Label _statChip;
        VisualElement _agentDot;
        Label _jobsBadge;
        readonly Dictionary<string, View> _views = new Dictionary<string, View>();
        readonly Dictionary<string, VisualElement> _railItems = new Dictionary<string, VisualElement>();
        View _current;
        bool _refreshQueued;
        string _mode = "";

        public CreateView Create => (CreateView)GetView("create");

        [MenuItem("Window/Scenario", priority = 1600)]
        public static ScenarioWindow Open()
        {
            var window = GetWindow<ScenarioWindow>();
            window.titleContent = new GUIContent("Scenario", Icons.Get("scenario-mark"));
            window.minSize = new Vector2(320, 420);
            window.Show();
            return window;
        }

        [Shortcut("Scenario/Open Window", KeyCode.S, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        static void OpenShortcut() => Open().Focus();

        public static ScenarioWindow OpenView(string view)
        {
            var window = Open();
            window.Show(view);
            return window;
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Scenario", Icons.Get("scenario-mark"));
        }

        void CreateGUI()
        {
            _root = rootVisualElement;
            _root.Clear();
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet != null) _root.styleSheets.Add(sheet);
            _root.AddToClassList("sc-root");
            _root.AddToClassList(EditorGUIUtility.isProSkin ? "sc-dark" : "sc-light");
            _root.focusable = true;

            _header = BuildHeader();
            _root.Add(_header);
            _body = new VisualElement();
            _body.AddToClassList("sc-body");
            _root.Add(_body);
            _rail = BuildRail();
            _body.Add(_rail);
            _content = new VisualElement();
            _content.AddToClassList("sc-content");
            _body.Add(_content);
            _overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = _overlay.style.right = _overlay.style.top = _overlay.style.bottom = 0;
            _root.Add(_overlay);
            _toasts = new VisualElement();
            _toasts.AddToClassList("sc-toasts");
            _toasts.pickingMode = PickingMode.Ignore;
            _root.Add(_toasts);

            _root.RegisterCallback<GeometryChangedEvent>(_ => _root.EnableInClassList("sc-wide", _root.resolvedStyle.width >= 560));
            _root.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);

            var service = ScenarioService.Instance;
            service.Changed += QueueRefresh;
            service.Notified += OnNotified;
            AgentServer.StateChanged += QueueRefresh;
            ApplyMode(force: true);
            _root.schedule.Execute(UpdateHeader).Every(2000);
        }

        void OnDisable()
        {
            if (!ScenarioService.Exists) return;
            ScenarioService.Instance.Changed -= QueueRefresh;
            ScenarioService.Instance.Notified -= OnNotified;
            AgentServer.StateChanged -= QueueRefresh;
        }

        void OnFocus()
        {
            if (_root != null && ScenarioService.Exists && ScenarioService.Instance.Ready) _ = ScenarioService.Instance.RefreshCatalog(false);
        }

        void OnSelectionChange() => _current?.OnSelectionChange();

        void QueueRefresh()
        {
            if (_refreshQueued || _root == null) return;
            _refreshQueued = true;
            _root.schedule.Execute(() =>
            {
                _refreshQueued = false;
                ApplyMode(force: false);
                UpdateHeader();
                CheckApprovals();
                _current?.Refresh();
            });
        }

        // --- Modes: signed out, picking a workspace, working ----------------

        void ApplyMode(bool force)
        {
            var s = ScenarioService.Instance;
            var mode = !s.IsSignedIn ? "signin" : s.NeedsWorkspace ? "workspace" : "app";
            if (!force && mode == _mode) return;
            _mode = mode;
            _rail.style.display = mode == "app" ? DisplayStyle.Flex : DisplayStyle.None;
            _header.style.display = mode == "signin" ? DisplayStyle.None : DisplayStyle.Flex;
            CloseOverlay();
            if (mode == "signin") ShowView(GetView("signin"));
            else if (mode == "workspace") ShowView(GetView("workspace"));
            else Show(ScenarioUserSettings.instance.View);
            UpdateHeader();
        }

        View GetView(string id)
        {
            if (_views.TryGetValue(id, out var view)) return view;
            switch (id)
            {
                case "signin": view = new SignInView(this); break;
                case "workspace": view = new WorkspaceView(this); break;
                case "library": view = new LibraryView(this); break;
                case "jobs": view = new JobsView(this); break;
                case "workflows": view = new WorkflowsView(this); break;
                case "agent": view = new AgentView(this); break;
                case "settings": view = new SettingsView(this); break;
                default: view = new CreateView(this); break;
            }
            _views[view.Id] = view;
            return view;
        }

        /// <summary>Shows a main view (create, library, jobs, workflows, agent, settings).</summary>
        public void Show(string id)
        {
            if (_root == null) return;
            if (_mode != "app")
            {
                ScenarioUserSettings.instance.View = id;
                return;
            }
            if (!Nav.Any(n => n.id == id)) id = "create";
            ScenarioUserSettings.instance.View = id;
            ShowView(GetView(id));
            foreach (var pair in _railItems) pair.Value.EnableInClassList("sc-selected", pair.Key == id);
        }

        void ShowView(View view)
        {
            if (_current == view && view.parent == _content) return;
            _content.Clear();
            _content.Add(view);
            _current = view;
            view.OnShow();
        }

        // --- Header and rail ----------------------------------------------------

        VisualElement BuildHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("sc-header");
            var logo = new VisualElement();
            logo.AddToClassList("sc-logo");
            logo.style.backgroundImage = new StyleBackground(Icons.Get("scenario-mark"));
            logo.tooltip = "Scenario";
            header.Add(logo);

            var workspace = new VisualElement();
            workspace.AddToClassList("sc-workspace");
            _workspaceText = new Label("Scenario");
            _workspaceText.AddToClassList("sc-workspace-text");
            workspace.Add(_workspaceText);
            var chevron = Ui.Icon("chevron-down", "sc-icon-sm");
            chevron.style.marginLeft = 4;
            workspace.Add(chevron);
            workspace.RegisterCallback<ClickEvent>(_ => WorkspaceMenu(workspace));
            workspace.tooltip = "Switch team or project";
            header.Add(workspace);
            header.Add(Ui.Spacer());

            _statChip = new Label();
            _statChip.AddToClassList("sc-chip-stat");
            _statChip.tooltip = "Creative Units spent today in this project, from this machine's job history. Plan from your Scenario team.";
            header.Add(_statChip);

            var palette = new VisualElement();
            palette.AddToClassList("sc-palette-btn");
            palette.Add(Ui.Icon("search", "sc-icon-sm"));
            var kbd = new Label(Application.platform == RuntimePlatform.OSXEditor ? "⌘K" : "Ctrl K");
            kbd.AddToClassList("sc-kbd");
            palette.Add(kbd);
            palette.tooltip = "Command palette";
            palette.RegisterCallback<ClickEvent>(_ => OpenPalette());
            header.Add(palette);

            _agentDot = new VisualElement();
            _agentDot.AddToClassList("sc-agent-dot");
            _agentDot.RegisterCallback<ClickEvent>(_ => Show("agent"));
            header.Add(_agentDot);

            var account = Ui.IconButton("user", "Account", null);
            account.clicked += () => AccountMenu(account);
            header.Add(account);
            return header;
        }

        VisualElement BuildRail()
        {
            var rail = new VisualElement();
            rail.AddToClassList("sc-rail");
            foreach (var (id, label, icon) in Nav)
            {
                var item = new VisualElement();
                item.AddToClassList("sc-rail-item");
                item.Add(Ui.Icon(icon));
                var text = new Label(label);
                text.AddToClassList("sc-rail-label");
                item.Add(text);
                item.tooltip = label;
                var target = id;
                item.RegisterCallback<ClickEvent>(_ => Show(target));
                if (id == "jobs")
                {
                    _jobsBadge = new Label();
                    _jobsBadge.AddToClassList("sc-rail-badge");
                    item.Add(_jobsBadge);
                }
                rail.Add(item);
                _railItems[id] = item;
            }
            return rail;
        }

        void UpdateHeader()
        {
            if (_root == null) return;
            var s = ScenarioService.Instance;
            var user = ScenarioUserSettings.instance;
            _workspaceText.text = s.Method == AuthMethod.OAuth
                ? (string.IsNullOrEmpty(user.ProjectName) ? "Pick a project" : user.ProjectName)
                : (string.IsNullOrEmpty(user.ProjectName) ? "API key" : user.ProjectName);
            var plan = string.IsNullOrEmpty(user.Plan) ? "" : char.ToUpperInvariant(user.Plan[0]) + user.Plan.Substring(1);
            var spent = s.SpentToday();
            _statChip.text = spent > 0 ? (plan.Length > 0 ? $"{plan} · {Ui.Cu(spent)} today" : $"{Ui.Cu(spent)} today") : plan;
            _statChip.style.display = _statChip.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            var active = s.Ledger.Active().Count;
            var unresolved = s.Ledger.Unresolved().Count;
            if (_jobsBadge != null)
            {
                _jobsBadge.text = (active + unresolved).ToString();
                _jobsBadge.style.display = active + unresolved > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            _agentDot.EnableInClassList("sc-on", AgentServer.Running);
            _agentDot.EnableInClassList("sc-busy", AgentServer.Running && AgentServer.Busy);
            _agentDot.tooltip = AgentServer.Running
                ? (string.IsNullOrEmpty(AgentServer.LastClient) ? "Agent bridge on, no client yet" : $"Agent bridge on · {AgentServer.LastClient}")
                : "Agent bridge off";
        }

        void WorkspaceMenu(VisualElement anchor)
        {
            var s = ScenarioService.Instance;
            if (s.Method != AuthMethod.OAuth)
            {
                Ui.Menu(anchor, menu =>
                {
                    menu.AddDisabledItem(new GUIContent("An API key works in its own project"));
                    menu.AddItem(new GUIContent("Open project on Scenario"), false, () => Application.OpenURL(s.HomeLink));
                });
                return;
            }
            Ui.Menu(anchor, menu =>
            {
                foreach (var team in s.Teams)
                    foreach (var project in team.Projects)
                    {
                        var t = team;
                        var p = project;
                        var name = s.Teams.Count > 1 ? $"{t.Name.Replace("/", "∕")}/{p.Name.Replace("/", "∕")}" : p.Name.Replace("/", "∕");
                        menu.AddItem(new GUIContent(name), s.Client.ProjectId == p.Id, () => s.SelectWorkspace(t.Id, p.Id));
                    }
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Reload teams and projects"), false, () => _ = s.LoadTeams());
            });
        }

        void AccountMenu(VisualElement anchor)
        {
            var s = ScenarioService.Instance;
            Ui.Menu(anchor, menu =>
            {
                menu.AddDisabledItem(new GUIContent(s.AccountLabel.Replace("/", "∕")));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Open Scenario"), false, () => Application.OpenURL(s.HomeLink));
                menu.AddItem(new GUIContent("Settings"), false, () => Show("settings"));
                menu.AddSeparator("");
                if (s.FromEnvironment) menu.AddDisabledItem(new GUIContent("Signed in from environment variables"));
                else menu.AddItem(new GUIContent("Sign out"), false, s.SignOut);
            });
        }

        // --- Keyboard ------------------------------------------------------------

        void OnKey(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.None) return;
            if (e.actionKey && e.keyCode == KeyCode.K)
            {
                OpenPalette();
                e.StopPropagation();
                return;
            }
            if (e.keyCode == KeyCode.Escape && HasOverlay)
            {
                CloseOverlay();
                e.StopPropagation();
                return;
            }
            if (e.actionKey && e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha6 && _mode == "app")
            {
                Show(Nav[e.keyCode - KeyCode.Alpha1].id);
                e.StopPropagation();
            }
        }

        // --- Overlays and toasts ---------------------------------------------

        public bool HasOverlay => _overlay != null && _overlay.childCount > 0;

        public void OpenOverlay(VisualElement sheet, Action onClose = null)
        {
            CloseOverlay();
            var backdrop = new VisualElement();
            backdrop.AddToClassList("sc-overlay");
            backdrop.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == backdrop) CloseOverlay();
            });
            backdrop.Add(sheet);
            backdrop.userData = onClose;
            _overlay.Add(backdrop);
            _overlay.pickingMode = PickingMode.Position;
        }

        public void CloseOverlay()
        {
            if (_overlay == null || _overlay.childCount == 0) return;
            var backdrop = _overlay[0];
            _overlay.Clear();
            _overlay.pickingMode = PickingMode.Ignore;
            (backdrop.userData as Action)?.Invoke();
            _root.Focus();
        }

        public void OpenPalette()
        {
            if (_mode != "app") return;
            OpenOverlay(new CommandPalette(this));
        }

        void OnNotified(string message, string level) => Toast(message, level);

        public void Toast(string message, string level = "info", string actionText = null, Action action = null, float seconds = 5)
        {
            if (_toasts == null) return;
            var toast = new VisualElement();
            toast.AddToClassList("sc-toast");
            toast.AddToClassList("sc-toast-" + level);
            toast.pickingMode = PickingMode.Position;
            var text = new Label(message);
            text.AddToClassList("sc-toast-text");
            toast.Add(text);
            if (actionText != null && action != null)
                toast.Add(Ui.Button(actionText, () => { action(); toast.RemoveFromHierarchy(); }, null, "sc-btn-sm"));
            toast.Add(Ui.IconButton("close", "Dismiss", () => toast.RemoveFromHierarchy(), "sc-btn-sm"));
            _toasts.Add(toast);
            while (_toasts.childCount > 3) _toasts.RemoveAt(0);
            var hovered = false;
            toast.RegisterCallback<PointerEnterEvent>(_ => hovered = true);
            toast.RegisterCallback<PointerLeaveEvent>(_ => hovered = false);
            var started = EditorApplication.timeSinceStartup;
            toast.schedule.Execute(() =>
            {
                if (hovered) started = EditorApplication.timeSinceStartup;
                else if (EditorApplication.timeSinceStartup - started > (level == "error" ? seconds * 2 : seconds)) toast.RemoveFromHierarchy();
            }).Every(250);
        }

        /// <summary>Opens the Library to pick an asset for an input; returns to the caller's view on pick.</summary>
        public void PickScenarioAsset(string kind, Action<JObject> pick)
        {
            Show("library");
            ((LibraryView)GetView("library")).BeginPick(kind, pick);
        }

        int _pendingApprovals;

        void CheckApprovals()
        {
            var count = AgentServer.Pending.Count;
            if (count > _pendingApprovals)
            {
                var approval = AgentServer.Pending.Last();
                Toast($"{approval.Client} asks to spend {Ui.Cu(approval.Cu)}: {approval.Summary}.", "warning", "Review", () => Show("agent"), 30);
            }
            _pendingApprovals = count;
        }

        /// <summary>For tests and screenshots: the view with this id, built if needed.</summary>
        public VisualElement ViewForTests(string id) => GetView(id);
    }
}
