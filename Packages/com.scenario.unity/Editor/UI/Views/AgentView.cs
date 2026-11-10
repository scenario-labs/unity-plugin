using System.Linq;
using Scenario.Editor.Agent;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// The agent bridge: Ask Claude, on/off, the local endpoint and token, one-click
    /// setup per client, spending permissions, pending approvals and the activity log.
    /// </summary>
    public sealed class AgentView : View
    {
        public override string Id => "agent";

        readonly VisualElement _content;
        readonly VisualElement _live;
        readonly AskBox _ask;
        string _signature = "";

        public AgentView(ScenarioWindow window) : base(window)
        {
            var scroll = Ui.Scroll();
            Add(scroll);
            scroll.Add(Ui.Text("Agent", "sc-title"));
            scroll.Add(Ui.Text("Let Claude Code, Codex, Cursor or any MCP client use Scenario and this Unity project: find models, generate, import, build the scene and look at it. Paid calls follow the limits below.", "sc-subtitle"));
            // Built once, so a request being typed survives the view's rebuilds.
            var askCard = Ui.Card();
            askCard.name = "ask-card";
            _ask = new AskBox();
            askCard.Add(_ask);
            var askHint = Ui.Text("Runs your Claude Code on this project through the bridge, with the selection. It turns the bridge on if needed.", "sc-faint", "sc-small", "sc-wrap");
            askHint.style.marginTop = 6;
            askCard.Add(askHint);
            scroll.Add(askCard);
            _content = new VisualElement();
            scroll.Add(_content);
            _live = new VisualElement();
            scroll.Add(_live);
        }

        /// <summary>Puts a request in the Ask field (the command palette's Ask Claude).</summary>
        public void FocusAsk(string text = null)
        {
            if (text != null) _ask.Text = text;
            _ask.FocusField();
        }

        public override void OnShow()
        {
            Build();
            _signature = "";
            Refresh();
        }

        public override void Refresh()
        {
            var signature = $"{AgentServer.Running}|{AgentServer.LastClient}|{AgentServer.Error}|{AgentServer.Pending.Count}|{AgentServer.Activity.Count}|{AgentServer.SessionSpent}";
            if (signature == _signature) return;
            var runningChanged = !_signature.StartsWith(AgentServer.Running + "|");
            _signature = signature;
            if (runningChanged) Build();
            BuildLive();
        }

        void Build()
        {
            _content.Clear();
            var user = ScenarioUserSettings.instance;
            var toggleCard = Ui.Card("sc-card-row");
            var dot = new VisualElement();
            dot.AddToClassList("sc-agent-dot");
            dot.EnableInClassList("sc-on", AgentServer.Running);
            dot.style.marginRight = 10;
            toggleCard.Add(dot);
            var text = new VisualElement();
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            text.Add(Ui.Text(AgentServer.Running ? "Agents can use Scenario and this project" : "Agent bridge off", "sc-bold", "sc-wrap"));
            text.Add(Ui.Text(AgentServer.Running ? AgentServer.Url : "Only this computer can connect, with the token below.", "sc-dim", "sc-small"));
            toggleCard.Add(text);
            var toggle = Ui.Switch(AgentServer.Running, AgentServer.SetEnabled);
            toggle.name = "agent-toggle";
            toggle.tooltip = AgentServer.Running ? "Turn the agent bridge off" : "Turn the agent bridge on";
            toggleCard.Add(toggle);
            _content.Add(toggleCard);
            if (AgentServer.Error.Length > 0) _content.Add(Ui.Banner(AgentServer.Error, "error"));

            _content.Add(Ui.Section("Connect a client"));
            foreach (var client in ClientSetup.Clients)
            {
                var c = client;
                var row = new VisualElement();
                row.AddToClassList("sc-list-row");
                row.name = "client-" + c.Id;
                row.Add(Ui.Icon(c.Icon));
                var name = Ui.Text(c.Name, "sc-bold");
                name.style.marginLeft = 10;
                row.Add(name);
                row.Add(Ui.Text("  " + c.Where, "sc-faint", "sc-small", "sc-ellipsis"));
                row.Add(Ui.Spacer());
                row.Add(Ui.Button("Copy setup", () =>
                {
                    EditorGUIUtility.systemCopyBuffer = ClientSetup.Snippet(c.Id);
                    Window.Toast($"{c.Name} setup copied. It contains your bridge token: keep it out of shared files.", "info", seconds: 7);
                }, "copy", "sc-btn-sm"));
                if (c.CanConfigure)
                {
                    var configure = Ui.Button("Configure", () => Configure(c), null, "sc-btn-outline", "sc-btn-sm");
                    configure.style.marginLeft = 4;
                    row.Add(configure);
                }
                _content.Add(row);
            }
            var tokenRow = Ui.Row(Ui.Text("Token stored for your user, outside the project.", "sc-faint", "sc-small"), Ui.Spacer(),
                Ui.Button("Regenerate", () =>
                {
                    if (!EditorUtility.DisplayDialog("Regenerate the token?", "Connected clients stop working until you copy the new setup into them.", "Regenerate", "Cancel")) return;
                    AgentServer.RegenerateToken();
                    Window.Toast("New token ready. Copy the setup again into your clients.", "info");
                }, "refresh", "sc-btn-ghost", "sc-btn-sm"));
            tokenRow.style.marginTop = 4;
            _content.Add(tokenRow);

            _content.Add(Ui.Section("Permissions"));
            var auto = new IntegerField("Approve paid calls up to (CU)") { value = user.AgentAutoApproveCu, name = "agent-auto" };
            auto.tooltip = "Above this, an approval card appears here. 0 asks every time.";
            auto.RegisterValueChangedCallback(e =>
            {
                user.AgentAutoApproveCu = Mathf.Max(0, e.newValue);
                user.Save();
            });
            _content.Add(auto);
            var budget = new IntegerField("Session budget (CU)") { value = user.AgentSessionBudgetCu, name = "agent-budget" };
            budget.tooltip = "Past this total since Unity started, every paid call asks.";
            budget.RegisterValueChangedCallback(e =>
            {
                user.AgentSessionBudgetCu = Mathf.Max(0, e.newValue);
                user.Save();
            });
            _content.Add(budget);
            var scene = new Toggle("Agents can edit the scene (each edit is one Undo step)") { value = user.AgentCanEditScene };
            scene.RegisterValueChangedCallback(e =>
            {
                user.AgentCanEditScene = e.newValue;
                user.Save();
            });
            _content.Add(scene);
            var port = new IntegerField("Port") { value = user.AgentPort };
            port.isDelayed = true;
            port.RegisterValueChangedCallback(e =>
            {
                if (e.newValue < 1024 || e.newValue > 65535) return;
                user.AgentPort = e.newValue;
                user.Save();
                if (AgentServer.Running)
                {
                    AgentServer.Stop();
                    AgentServer.Start();
                }
                Build();
            });
            _content.Add(port);
            var skill = Ui.Text("Agent guide (skill file shipped with the package)", "sc-link", "sc-small");
            skill.style.marginTop = 6;
            skill.RegisterCallback<ClickEvent>(_ => EditorUtility.RevealInFinder(System.IO.Path.GetFullPath("Packages/com.scenario.unity/Documentation~/agent-skill/SKILL.md")));
            _content.Add(skill);
        }

        void Configure(ClientSetup.Client client)
        {
            var (ok, output) = ClientSetup.Configure(client.Id);
            Window.Toast(ok ? $"{client.Name} is set up for this project." : $"{client.Name} setup failed: {output}", ok ? "success" : "error", seconds: 8);
        }

        void BuildLive()
        {
            _live.Clear();
            if (AgentServer.Pending.Count > 0)
            {
                _live.Add(Ui.Section("Waiting for you"));
                foreach (var approval in AgentServer.Pending.ToList())
                {
                    var a = approval;
                    var card = Ui.Card();
                    card.name = "approval-" + a.Id;
                    card.Add(Ui.Text($"{a.Client} wants to {a.Summary}", "sc-bold", "sc-wrap"));
                    card.Add(Ui.Text($"{Ui.Cu(a.Cu)} · {a.Reason}", "sc-dim", "sc-small", "sc-wrap"));
                    var buttons = Ui.Row(Ui.Spacer(), Ui.Button("Deny", a.Deny, null, "sc-btn-ghost", "sc-btn-sm"), Ui.Button($"Approve · {Ui.Cu(a.Cu)}", a.Approve, null, "sc-btn-sm"));
                    buttons.style.marginTop = 6;
                    card.Add(buttons);
                    _live.Add(card);
                }
            }
            _live.Add(Ui.Section("Activity"));
            var spent = AgentServer.SessionSpent;
            _live.Add(Ui.Text($"{(string.IsNullOrEmpty(AgentServer.LastClient) ? "No client yet" : "Last client: " + AgentServer.LastClient)} · {Ui.Cu(spent)} approved this session",
                "sc-faint", "sc-small"));
            if (AgentServer.Activity.Count == 0)
            {
                _live.Add(Ui.Text("Tool calls appear here.", "sc-faint", "sc-small"));
                return;
            }
            foreach (var entry in Enumerable.Reverse(AgentServer.Activity).Take(60))
            {
                var row = new VisualElement();
                row.AddToClassList("sc-row");
                row.style.marginTop = 3;
                row.Add(Ui.Text(System.DateTimeOffset.FromUnixTimeSeconds((long)entry.Time).LocalDateTime.ToString("HH:mm:ss"), "sc-faint", "sc-small"));
                var tool = Ui.Text(entry.Tool, "sc-bold", "sc-small");
                tool.style.marginLeft = 8;
                row.Add(tool);
                var detail = Ui.Text(entry.Summary, entry.IsError ? "sc-dim" : "sc-faint", "sc-small", "sc-ellipsis");
                detail.style.marginLeft = 8;
                detail.style.flexShrink = 1;
                detail.tooltip = entry.Summary;
                row.Add(detail);
                if (entry.IsError) row.Add(Ui.Icon("warning", "sc-icon-sm"));
                _live.Add(row);
            }
        }
    }
}
