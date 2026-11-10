using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>Account, output, import, placement, spending, diagnostics, about.</summary>
    public sealed class SettingsView : View
    {
        public override string Id => "settings";

        readonly VisualElement _content;
        readonly List<(string label, string state, string detail)> _checks = new List<(string, string, string)>();
        bool _checking;

        public SettingsView(ScenarioWindow window) : base(window)
        {
            var scroll = Ui.Scroll();
            Add(scroll);
            _content = new VisualElement();
            scroll.Add(_content);
        }

        public override void OnShow() => Build();

        public override void Refresh()
        {
            // Rebuilding steals focus from text fields: only the account card follows live changes.
            var account = _content.Q("account");
            if (account == null) return;
            var index = _content.IndexOf(account);
            account.RemoveFromHierarchy();
            _content.Insert(index, Account());
        }

        void Build()
        {
            _content.Clear();
            _content.Add(Ui.Text("Settings", "sc-title"));
            _content.Add(Ui.Text("Project settings are shared with your team (ProjectSettings/). Your account and workspace stay on this machine.", "sc-subtitle"));
            _content.Add(Account());
            var project = ScenarioProjectSettings.instance;
            var user = ScenarioUserSettings.instance;

            _content.Add(Ui.Section("Output"));
            var root = new TextField("Folder") { value = project.OutputRoot };
            root.RegisterValueChangedCallback(e =>
            {
                var value = e.newValue.Trim().TrimEnd('/');
                if (!value.StartsWith("Assets")) return;
                project.OutputRoot = value;
                project.Save();
            });
            var browse = Ui.Button("Choose…", () =>
            {
                var chosen = EditorUtility.OpenFolderPanel("Where Scenario results go", Application.dataPath, "");
                if (string.IsNullOrEmpty(chosen)) return;
                var data = Application.dataPath.Replace('\\', '/');
                chosen = chosen.Replace('\\', '/');
                if (!chosen.StartsWith(data))
                {
                    Window.Toast("Pick a folder inside Assets.", "warning");
                    return;
                }
                project.OutputRoot = "Assets" + chosen.Substring(data.Length);
                project.Save();
                root.SetValueWithoutNotify(project.OutputRoot);
            }, null, "sc-btn-sm");
            browse.style.marginLeft = 6;
            var rootRow = Ui.Row(root, browse);
            root.style.flexGrow = 1;
            _content.Add(rootRow);
            _content.Add(ProjectToggle("A folder per day (Image/2026-10-18)", project.DatedFolders, v => project.DatedFolders = v));

            _content.Add(Ui.Section("Import"));
            _content.Add(ProjectToggle("Images as sprites (on in 2D projects)", project.ImagesAsSprites, v => project.ImagesAsSprites = v));
            var ppu = new FloatField("Sprite pixels per unit") { value = project.PixelsPerUnit };
            ppu.RegisterValueChangedCallback(e =>
            {
                project.PixelsPerUnit = Mathf.Max(1, e.newValue);
                project.Save();
            });
            _content.Add(ppu);
            var filter = new EnumField("Image filter", project.ImageFilter);
            filter.RegisterValueChangedCallback(e =>
            {
                project.ImageFilter = (FilterMode)e.newValue;
                project.Save();
            });
            _content.Add(filter);
            _content.Add(ProjectToggle("Add a collider to placed 3D models", project.AddCollider, v => project.AddCollider = v));

            _content.Add(Ui.Section("Placement"));
            _content.Add(ProjectToggle("Frame placed objects in the Scene view", project.FrameAfterPlace, v => project.FrameAfterPlace = v));

            _content.Add(Ui.Section("Spending"));
            var confirm = new IntegerField("Ask before requests above (CU)") { value = project.ConfirmAboveCu };
            confirm.tooltip = "0 never asks";
            confirm.RegisterValueChangedCallback(e =>
            {
                project.ConfirmAboveCu = Mathf.Max(0, e.newValue);
                project.Save();
            });
            _content.Add(confirm);
            var spending = new Toggle("Turn spending off (no paid call leaves this editor)") { value = user.SpendingDisabled, name = "spending-off" };
            spending.RegisterValueChangedCallback(e => S.SetSpendingDisabled(e.newValue));
            if (Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") == "1")
            {
                spending.SetEnabled(false);
                spending.tooltip = "SCENARIO_NO_SPEND=1 is set for this editor";
            }
            _content.Add(spending);

            _content.Add(Ui.Section("Agent"));
            var agentText = Ui.Text($"Bridge port {user.AgentPort}. Access, permissions and client setup are in Agent.", "sc-dim", "sc-small", "sc-wrap");
            agentText.style.flexGrow = 1;
            agentText.style.flexShrink = 1;
            agentText.style.marginRight = 8;
            _content.Add(Ui.Row(agentText, Ui.Button("Open Agent", () => Window.Show("agent"), null, "sc-btn-sm")));
            var claudePath = new TextField("Claude Code path") { value = user.ClaudeCodePath, isDelayed = true, name = "claude-path" };
            claudePath.textEdition.placeholder = "Empty: found automatically";
            claudePath.tooltip = "The claude command Ask runs. Leave empty to find it on PATH and in the usual install folders.";
            claudePath.style.marginTop = 6;
            var claudeFound = Ui.Text("", "sc-faint", "sc-small", "sc-wrap");
            claudeFound.name = "claude-found";
            void ShowClaude()
            {
                var found = Agent.ClaudeCode.Find();
                claudeFound.text = found != null ? "Ask runs " + found
                    : user.ClaudeCodePath.Length > 0 ? "Nothing at this path. Clear it to find claude automatically."
                    : "Claude Code not found. " + Agent.ClaudeCode.InstallHint;
            }
            claudePath.RegisterValueChangedCallback(e =>
            {
                user.ClaudeCodePath = (e.newValue ?? "").Trim();
                user.Save();
                ShowClaude();
            });
            ShowClaude();
            _content.Add(claudePath);
            _content.Add(claudeFound);

            _content.Add(Ui.Section("Diagnostics"));
            _content.Add(Ui.Text("A free check of sign-in, project access, the model catalog and one price check. Nothing is generated.", "sc-dim", "sc-small", "sc-wrap"));
            var diagnostics = new VisualElement { name = "diagnostics" };
            _content.Add(diagnostics);
            DrawChecks(diagnostics);
            var run = Ui.Button("Run the check", () => _ = RunChecks(diagnostics), "check", "sc-btn-sm");
            run.name = "run-diagnostics";
            run.style.alignSelf = Align.FlexStart;
            run.style.marginTop = 6;
            _content.Add(run);

            _content.Add(Ui.Section("About"));
            _content.Add(Ui.Text($"Scenario for Unity {McpClient.Version} · Unity {Application.unityVersion}", "sc-dim", "sc-small"));
            var links = Ui.Row(
                Link("Documentation", Links.Repository + "#readme"),
                Link("Report an issue", Links.Repository + "/issues"),
                Link("Scenario", S.HomeLink));
            links.style.marginTop = 6;
            _content.Add(links);
        }

        VisualElement Account()
        {
            var card = Ui.Card();
            card.name = "account";
            var user = ScenarioUserSettings.instance;
            var method = S.Method == AuthMethod.OAuth ? "Signed in with Scenario" : S.Method == AuthMethod.Environment ? "API key from environment variables" : "API key";
            var row = new VisualElement();
            row.AddToClassList("sc-card-row");
            var icon = new VisualElement();
            icon.AddToClassList("sc-thumb");
            icon.Add(Ui.Icon(S.Method == AuthMethod.OAuth ? "user" : "key", "sc-icon-lg"));
            row.Add(icon);
            var text = new VisualElement();
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            text.Add(Ui.Text(S.AccountLabel.Length > 0 ? S.AccountLabel : "Signed out", "sc-bold", "sc-ellipsis"));
            text.Add(Ui.Text(method, "sc-dim", "sc-small"));
            var where = string.Join(" / ", new[] { user.TeamName, user.ProjectName }.Where(s => !string.IsNullOrEmpty(s)));
            if (where.Length > 0) text.Add(Ui.Text(where + (user.Plan.Length > 0 ? $" · {user.Plan} plan" : ""), "sc-faint", "sc-small", "sc-ellipsis"));
            row.Add(text);
            if (S.Method == AuthMethod.OAuth && S.Teams.SelectMany(t => t.Projects).Count() > 1)
            {
                var change = Ui.Button("Switch project", null, null, "sc-btn-sm");
                change.clicked += () => Ui.Menu(change, menu =>
                {
                    foreach (var team in S.Teams)
                        foreach (var project in team.Projects)
                        {
                            var t = team;
                            var p = project;
                            menu.AddItem(new GUIContent($"{t.Name.Replace("/", "∕")}/{p.Name.Replace("/", "∕")}"), p.Id == S.Client.ProjectId, () => S.SelectWorkspace(t.Id, p.Id));
                        }
                });
                row.Add(change);
            }
            if (!S.FromEnvironment)
            {
                var signOut = Ui.Button("Sign out", S.SignOut, null, "sc-btn-ghost", "sc-btn-sm");
                signOut.style.marginLeft = 4;
                row.Add(signOut);
            }
            card.Add(row);
            return card;
        }

        static VisualElement ProjectToggle(string label, bool value, Action<bool> set)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.RegisterValueChangedCallback(e =>
            {
                set(e.newValue);
                ScenarioProjectSettings.instance.Save();
            });
            return toggle;
        }

        static Label Link(string text, string url)
        {
            var link = Ui.Text(text, "sc-link", "sc-small");
            link.style.marginRight = 14;
            link.RegisterCallback<ClickEvent>(_ => Application.OpenURL(url));
            return link;
        }

        void DrawChecks(VisualElement box)
        {
            box.Clear();
            foreach (var (label, state, detail) in _checks)
            {
                var row = new VisualElement();
                row.AddToClassList("sc-row");
                row.style.marginTop = 4;
                row.Add(state == "running" ? Ui.Spinner(true) : Ui.Icon(state == "ok" ? "check" : state == "warn" ? "warning" : "error", "sc-icon-sm"));
                var text = Ui.Text($"  {label}" + (detail.Length > 0 ? $": {detail}" : ""), "sc-small", "sc-wrap");
                text.style.flexShrink = 1;
                row.Add(text);
                box.Add(row);
            }
            if (_checks.Count > 0 && !_checking)
            {
                var copy = Ui.Text("Copy the report", "sc-link", "sc-small");
                copy.style.marginTop = 6;
                copy.RegisterCallback<ClickEvent>(_ =>
                {
                    var report = new StringBuilder($"Scenario for Unity {McpClient.Version}, Unity {Application.unityVersion}, {SystemInfo.operatingSystem}\n");
                    foreach (var (label, state, detail) in _checks) report.AppendLine($"[{state}] {label}: {detail}");
                    EditorGUIUtility.systemCopyBuffer = report.ToString();
                    Window.Toast("Report copied.", "info");
                });
                box.Add(copy);
            }
        }

        async Task RunChecks(VisualElement box)
        {
            if (_checking) return;
            _checking = true;
            _checks.Clear();
            void Set(int index, string label, string state, string detail)
            {
                if (index < _checks.Count) _checks[index] = (label, state, detail);
                else _checks.Add((label, state, detail));
                DrawChecks(box);
            }

            Set(0, "Sign-in", "running", "");
            var teams = await S.Client.ListTeams();
            Set(0, "Sign-in", teams.Ok ? "ok" : "fail", teams.Ok ? S.AccountLabel : teams.Error.Message);
            if (teams.Ok)
            {
                Set(1, "Project access", "running", "");
                var assets = await S.Client.ListAssets(new JObject(), 1, "", includeUrls: false);
                Set(1, "Project access", assets.Ok ? "ok" : "fail", assets.Ok ? ScenarioUserSettings.instance.ProjectName : assets.Error.Message);
                Set(2, "Model catalog", "running", "");
                await S.RefreshCatalog(false);
                Set(2, "Model catalog", S.Catalog.IsEmpty ? "fail" : "ok", S.Catalog.IsEmpty ? S.CatalogError : $"{S.Catalog.Models.Count} models");
                Set(3, "Price check", "running", "");
                var model = OutputTypes.Get(OutputTypes.Image).QuickPicks[0];
                var price = await S.Estimate(model, new JObject { ["prompt"] = "diagnostic check" });
                Set(3, "Price check", price.Ok ? "ok" : "fail", price.Ok ? $"{S.Model(model)?.Name ?? model}: {Ui.Cu(price.Value.Cu)}" : price.Error.Message);
            }
            Set(_checks.Count, "Spending", S.Client.SpendingDisabled ? "warn" : "ok", S.Client.SpendingDisabled ? "off" : $"on, asks above {ScenarioProjectSettings.instance.ConfirmAboveCu} CU");
            Set(_checks.Count, "Render pipeline", "ok", MaterialBuilder.Current.ToString());
            Set(_checks.Count, "Agent bridge", Agent.AgentServer.Running ? "ok" : "warn", Agent.AgentServer.Running ? Agent.AgentServer.Url : "off");
            _checking = false;
            DrawChecks(box);
        }
    }
}
