using System.Linq;
using Scenario.Editor.Agent;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// A request for Claude Code on this project ("make the bridge rustier"), with its
    /// progress: each tool call, Claude's text, approvals waiting for you and the result.
    /// Shared by the Scene view overlay (last lines only) and the Agent view (everything).
    /// </summary>
    public sealed class AskBox : VisualElement
    {
        readonly TextField _field;
        readonly Button _ask;
        readonly Button _stop;
        readonly VisualElement _log;
        readonly int _lines;

        /// <param name="lines">How many recent lines to show; 0 shows them all.</param>
        public AskBox(int lines = 0)
        {
            _lines = lines;
            name = "ask-box";
            var row = new VisualElement();
            row.AddToClassList("sc-row");
            _field = new TextField { name = "ask-field" };
            _field.textEdition.placeholder = "Ask Claude: make the bridge rustier";
            _field.tooltip = "Claude Code does it in this project through the agent bridge, on the selection. Paid calls follow the limits in Agent.";
            _field.style.flexGrow = 1;
            _field.style.flexShrink = 1;
            _field.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            row.Add(_field);
            _ask = Ui.Button("Ask", Run, null, "sc-btn-sm");
            _ask.name = "ask-run";
            _ask.style.marginLeft = 4;
            row.Add(_ask);
            _stop = Ui.Button("Stop", Ask.Stop, "stop", "sc-btn-sm");
            _stop.name = "ask-stop";
            _stop.style.marginLeft = 4;
            row.Add(_stop);
            Add(row);
            _log = new VisualElement { name = "ask-log" };
            Add(_log);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                Ask.Changed += Draw;
                AgentServer.StateChanged += Draw;
                Draw();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                Ask.Changed -= Draw;
                AgentServer.StateChanged -= Draw;
            });
            Draw();
        }

        public string Text
        {
            get => _field.value;
            set => _field.value = value ?? "";
        }

        public void FocusField() => _field.Focus();

        void OnKey(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            e.StopPropagation();
            Run();
        }

        void Run()
        {
            if (Ask.Running) return;
            if (Ask.Start(_field.value, SceneActions.SelectionPaths())) _field.SetValueWithoutNotify("");
            Draw();
        }

        void Draw()
        {
            _ask.style.display = Ask.Running ? DisplayStyle.None : DisplayStyle.Flex;
            _stop.style.display = Ask.Running ? DisplayStyle.Flex : DisplayStyle.None;
            _log.Clear();
            if (Ask.Events.Count == 0 && !Ask.Running) return;
            _log.style.marginTop = 6;

            if (Ask.Request.Length > 0)
            {
                var request = Ui.Text(Ask.Request, "sc-bold", "sc-small", _lines > 0 ? "sc-ellipsis" : "sc-wrap");
                request.tooltip = Ask.Request;
                _log.Add(request);
            }
            var events = _lines > 0 ? Ask.Events.Skip(Mathf.Max(0, Ask.Events.Count - _lines)).ToList() : Ask.Events;
            if (_lines > 0 && Ask.Events.Count > _lines)
            {
                var hidden = Ask.Events.Count - _lines;
                var more = Ui.Text($"{hidden} earlier {(hidden == 1 ? "line" : "lines")} in Agent", "sc-link", "sc-small");
                more.RegisterCallback<ClickEvent>(_ => ScenarioWindow.OpenView("agent"));
                _log.Add(more);
            }
            foreach (var e in events) _log.Add(Line(e));

            if (Ask.Running)
            {
                foreach (var approval in AgentServer.Pending.ToList()) _log.Add(ApprovalCard(approval));
                var working = Ui.Row(Ui.Spinner(true), Ui.Text("  Working", "sc-faint", "sc-small"));
                working.style.marginTop = 3;
                _log.Add(working);
            }
            else
            {
                var clear = Ui.Text("Clear", "sc-link", "sc-small");
                clear.name = "ask-clear";
                clear.style.marginTop = 4;
                clear.RegisterCallback<ClickEvent>(_ => Ask.Clear());
                _log.Add(clear);
            }
        }

        VisualElement Line(AskEvent e)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-row");
            row.style.alignItems = Align.FlexStart;
            row.style.marginTop = 3;
            var icon = e.Kind == AskEventKind.Tool ? "wand" : e.Kind == AskEventKind.Result ? "check" : e.Kind == AskEventKind.Error ? "error"
                : e.Kind == AskEventKind.Warning ? "warning" : e.Kind == AskEventKind.Text ? "agent" : "info";
            var glyph = Ui.Icon(icon, "sc-icon-sm");
            glyph.style.marginTop = 1;
            glyph.style.marginRight = 6;
            row.Add(glyph);
            var body = new VisualElement();
            body.style.flexGrow = 1;
            body.style.flexShrink = 1;
            row.Add(body);
            var text = e.Text;
            if (_lines > 0 && text.Length > 280) text = text.Substring(0, 279) + "…";
            switch (e.Kind)
            {
                case AskEventKind.Tool:
                    var tool = Ui.Text(e.Text, "sc-bold", "sc-small");
                    var line = Ui.Row(tool);
                    if (e.Detail.Length > 0)
                    {
                        var detail = Ui.Text("  " + e.Detail, "sc-faint", "sc-small", "sc-ellipsis");
                        detail.style.flexShrink = 1;
                        detail.tooltip = e.Detail;
                        line.Add(detail);
                    }
                    body.Add(line);
                    break;
                case AskEventKind.Text:
                    body.Add(Ui.Text(text, "sc-dim", "sc-small", "sc-wrap"));
                    break;
                case AskEventKind.Info:
                    body.Add(Ui.Text(text, "sc-faint", "sc-small", "sc-wrap"));
                    break;
                default:
                    body.Add(Ui.Text(text, e.Kind == AskEventKind.Result ? "sc-bold" : "sc-dim", "sc-small", "sc-wrap"));
                    if (e.Detail.Length > 0) body.Add(Ui.Text(e.Detail, "sc-faint", "sc-small", "sc-wrap"));
                    break;
            }
            return row;
        }

        static VisualElement ApprovalCard(Approval approval)
        {
            var card = Ui.Card();
            card.name = "ask-approval-" + approval.Id;
            card.style.marginTop = 6;
            card.style.marginBottom = 0;
            card.Add(Ui.Text($"{approval.Client} wants to {approval.Summary}", "sc-bold", "sc-small", "sc-wrap"));
            card.Add(Ui.Text($"{Ui.Cu(approval.Cu)} · {approval.Reason}", "sc-faint", "sc-small", "sc-wrap"));
            var deny = Ui.Button("Deny", approval.Deny, null, "sc-btn-ghost", "sc-btn-sm");
            var approve = Ui.Button($"Approve · {Ui.Cu(approval.Cu)}", approval.Approve, null, "sc-btn-sm");
            approve.style.marginLeft = 4;
            var buttons = Ui.Row(Ui.Spacer(), deny, approve);
            buttons.style.marginTop = 6;
            card.Add(buttons);
            return card;
        }
    }
}
