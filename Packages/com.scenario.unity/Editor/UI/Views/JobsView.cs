using System.Collections.Generic;
using System.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// The durable job ledger of this Unity project: what was sent, its state,
    /// the quoted and charged Creative Units. Unknown rows explain themselves.
    /// </summary>
    public sealed class JobsView : View
    {
        static readonly (string id, string label)[] Filters = { ("all", "All"), ("running", "Running"), ("done", "Done"), ("problems", "Needs a look") };

        public override string Id => "jobs";

        readonly VisualElement _summary;
        readonly VisualElement _filters;
        readonly VisualElement _list;
        string _filter = "all";
        string _signature = "";

        public JobsView(ScenarioWindow window) : base(window)
        {
            var scroll = Ui.Scroll();
            Add(scroll);
            scroll.Add(Ui.Text("Jobs", "sc-title"));
            scroll.Add(Ui.Text("Every request sent from this Unity project, kept across restarts. Costs are Creative Units: quoted before sending, charged as Scenario reports.", "sc-subtitle"));
            _summary = new VisualElement();
            scroll.Add(_summary);
            _filters = new VisualElement();
            _filters.AddToClassList("sc-row-wrap");
            scroll.Add(_filters);
            _list = new VisualElement();
            scroll.Add(_list);
        }

        public override void OnShow()
        {
            _signature = "";
            Refresh();
        }

        public override void Refresh()
        {
            var rows = S.Ledger.Jobs.Where(Matches).ToList();
            var signature = _filter + "|" + string.Join("|", rows.Select(r => $"{r.LocalId}:{r.State}:{r.Imported}:{r.ChargedCu}:{Mathf.RoundToInt(r.Progress * 10)}"));
            if (signature == _signature) return;
            _signature = signature;
            BuildSummary();
            BuildFilters();
            _list.Clear();
            if (rows.Count == 0)
            {
                _list.Add(Ui.Empty("jobs", _filter == "all" ? "No job yet" : "Nothing here", _filter == "all" ? "Generations from the window, the agent and the C# API land here." : null));
                return;
            }
            string day = null;
            foreach (var row in rows.Take(200))
            {
                var rowDay = System.DateTimeOffset.FromUnixTimeSeconds((long)row.CreatedAt).LocalDateTime.ToString("dddd d MMMM");
                if (rowDay != day)
                {
                    day = rowDay;
                    _list.Add(Ui.Text(day.ToUpperInvariant(), "sc-group-header"));
                }
                _list.Add(Row(row));
            }
        }

        bool Matches(JobRow row)
        {
            switch (_filter)
            {
                case "running": return row.IsActive || row.State == JobState.Submitting;
                case "done": return row.State == JobState.Success;
                case "problems": return row.IsUnresolved || row.State == JobState.Failed || row.State == JobState.Blocked || (row.State == JobState.Success && !row.Imported && row.OutputType != "prompt");
                default: return true;
            }
        }

        void BuildSummary()
        {
            _summary.Clear();
            var unresolved = S.Ledger.Unresolved();
            if (unresolved.Count > 0)
                _summary.Add(Ui.Banner(
                    $"{unresolved.Count} request{(unresolved.Count == 1 ? " has" : "s have")} an unknown outcome: Unity stopped or the network failed while sending. They may have run on Scenario. The same request stays blocked until it is found or dismissed.",
                    "warning", "Look on Scenario", () => _ = LookForUnknown()));
            var active = S.Ledger.Active().Count;
            var today = S.SpentToday();
            var parts = new List<string>();
            if (active > 0) parts.Add($"{active} running");
            if (today > 0) parts.Add($"{Ui.Cu(today)} today in this project");
            if (S.Client.SpendingDisabled) parts.Add("spending off");
            if (parts.Count > 0)
            {
                var line = Ui.Text(string.Join(" · ", parts), "sc-dim", "sc-small");
                line.style.marginBottom = 8;
                _summary.Add(line);
            }
        }

        async System.Threading.Tasks.Task LookForUnknown()
        {
            var linked = await S.CheckUnknown();
            if (linked == 0) Window.Toast("Nothing matched on Scenario yet. Check again in a minute, or dismiss the row if you are sure it did not run.", "info", seconds: 8);
        }

        void BuildFilters()
        {
            _filters.Clear();
            foreach (var (id, label) in Filters)
            {
                var f = id;
                _filters.Add(Ui.Chip(label, null, _filter == id, () =>
                {
                    _filter = f;
                    _signature = "";
                    Refresh();
                }, "sc-chip-sm"));
            }
        }

        VisualElement Row(JobRow row)
        {
            var item = new VisualElement();
            item.AddToClassList("sc-list-row");
            item.name = "job-" + row.LocalId;
            item.style.alignItems = Align.FlexStart;
            var (pillText, pillKind) = Pill(row);
            var pill = Ui.Pill(pillText, pillKind);
            pill.style.marginTop = 1;
            pill.style.minWidth = 64;
            item.Add(pill);
            var text = new VisualElement();
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            text.style.marginLeft = 6;
            var title = Ui.Text(row.OutputType == "prompt" ? row.ModelName : Files.ObjectName(row.Prompt, row.ModelName), "sc-bold", "sc-ellipsis");
            title.tooltip = row.Prompt;
            text.Add(title);
            var cost = row.ChargedCu.HasValue ? $"{Ui.Cu(row.ChargedCu.Value)} charged" : row.QuotedCu > 0 ? $"{Ui.Cu(row.QuotedCu)} quoted" : "";
            var origin = row.Origin == "window" ? "" : row.Origin;
            var meta = string.Join(" · ", new[] { row.OutputType == "prompt" ? "" : row.ModelName, cost, origin, System.DateTimeOffset.FromUnixTimeSeconds((long)row.CreatedAt).LocalDateTime.ToString("HH:mm") }.Where(s => s.Length > 0));
            text.Add(Ui.Text(meta, "sc-faint", "sc-small", "sc-ellipsis"));
            if (row.Error.Length > 0 && !row.IsActive && row.State != JobState.Success)
                text.Add(Ui.Text(row.Error, "sc-dim", "sc-small", "sc-wrap"));
            item.Add(text);

            var buttons = new VisualElement();
            buttons.AddToClassList("sc-row");
            if (row.IsActive) buttons.Add(Ui.Button("Cancel", () => _ = S.Cancel(row), null, "sc-btn-ghost", "sc-btn-sm"));
            if (row.IsUnresolved) buttons.Add(Ui.Button("Dismiss", () => Dismiss(row), null, "sc-btn-ghost", "sc-btn-sm"));
            if (row.State == JobState.Success && !row.Imported && row.OutputType != "prompt") buttons.Add(Ui.Button("Import", () => _ = S.ImportRow(row), null, "sc-btn-sm"));
            var more = Ui.IconButton("more", "More", null, "sc-btn-sm");
            more.clicked += () => Ui.Menu(more, menu => Menu(menu, row));
            if (row.OutputType != "prompt") buttons.Add(more);
            item.Add(buttons);
            item.RegisterCallback<ClickEvent>(e =>
            {
                if (e.clickCount != 2 || row.Files.Count == 0) return;
                var asset = AssetDatabase.LoadMainAssetAtPath(row.Files[0]);
                if (asset == null) return;
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            });
            return item;
        }

        void Dismiss(JobRow row)
        {
            if (!EditorUtility.DisplayDialog("Dismiss this request?",
                    "Dismiss only if you are sure it did not run on Scenario. If it did, its cost was charged and the result stays on Scenario (Library).", "Dismiss", "Keep"))
                return;
            S.Dismiss(row);
        }

        void Menu(GenericMenu menu, JobRow row)
        {
            menu.AddItem(new GUIContent("Use settings (fills the form, sends nothing)"), false, () => Window.Create.UseSettings(row));
            if (row.Files.Count > 0)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(row.Files[0]);
                if (asset != null) menu.AddItem(new GUIContent("Show in Project"), false, () => EditorGUIUtility.PingObject(asset));
            }
            if (row.State == JobState.Success && row.AssetIds.Count > 0)
                menu.AddItem(new GUIContent("Import again"), false, () =>
                {
                    row.Imported = false;
                    row.Place = false;
                    _ = S.ImportRow(row);
                });
            var first = row.AssetIds.FirstOrDefault();
            if (!string.IsNullOrEmpty(first))
            {
                menu.AddItem(new GUIContent("Open on Scenario"), false, () => Application.OpenURL(S.AssetLink(first, row)));
                menu.AddItem(new GUIContent("Copy asset id"), false, () => EditorGUIUtility.systemCopyBuffer = first);
            }
            if (row.JobId.Length > 0) menu.AddItem(new GUIContent("Copy job id"), false, () => EditorGUIUtility.systemCopyBuffer = row.JobId);
            if (row.IsFinal && row.State != JobState.Dismissed)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Hide from the list"), false, () => S.Dismiss(row));
            }
        }

        public static (string text, string kind) Pill(JobRow row)
        {
            switch (row.State)
            {
                case JobState.Submitting: return ("Sending", "info");
                case JobState.Queued: return ("Queued", "info");
                case JobState.Running: return ("Running", "info");
                case JobState.CancelRequested: return ("Canceling", "info");
                case JobState.Success: return row.Imported || row.OutputType == "prompt" ? ("Done", "success") : ("To import", "warning");
                case JobState.Failed: return ("Failed", "error");
                case JobState.Blocked: return ("Blocked", "warning");
                case JobState.Canceled: return ("Canceled", null);
                case JobState.Unknown: return ("Unknown", "warning");
                case JobState.Rejected: return ("Not sent", null);
                case JobState.Dismissed: return ("Hidden", null);
                default: return (row.State, null);
            }
        }
    }
}
