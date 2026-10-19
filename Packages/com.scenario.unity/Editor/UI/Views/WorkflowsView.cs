using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// Team and featured Scenario workflows: pick one, fill its inputs, see the
    /// price, run. Outputs import like any job.
    /// </summary>
    public sealed class WorkflowsView : View
    {
        public override string Id => "workflows";

        readonly VisualElement _body;
        string _tab = "team";
        bool _loading;
        string _error = "";
        readonly Dictionary<string, List<JObject>> _lists = new Dictionary<string, List<JObject>>();
        string _query = "";

        // Open workflow
        JObject _workflow;
        List<Field> _fields = new List<Field>();
        JObject _values = new JObject();
        readonly Dictionary<string, List<SlotItem>> _slots = new Dictionary<string, List<SlotItem>>();
        readonly Dictionary<string, InputSlot> _slotViews = new Dictionary<string, InputSlot>();
        QuoteBook.Quote _quote;
        string _priceError = "";
        bool _checking;
        bool _sending;
        int _priceVersion;
        Button _run;
        Label _runLabel;
        Label _note;

        public WorkflowsView(ScenarioWindow window) : base(window)
        {
            _body = new VisualElement();
            _body.AddToClassList("sc-fill");
            Add(_body);
        }

        public override void OnShow()
        {
            if (_workflow == null) ShowList();
            if (!_lists.ContainsKey(_tab)) _ = Load(_tab);
        }

        async Task Load(string tab)
        {
            if (!S.Ready || _loading) return;
            _loading = true;
            _error = "";
            if (_workflow == null) ShowList();
            var result = await S.Client.ListWorkflows(tab == "team" ? "private" : "public", 60);
            _loading = false;
            if (result.Ok) _lists[tab] = (result.Value["workflows"] as JArray ?? new JArray()).OfType<JObject>().Where(w => Json.Str(w, "status", "ready") == "ready").ToList();
            else _error = result.Error.Message;
            if (_workflow == null) ShowList();
        }

        void ShowList()
        {
            _body.Clear();
            var scroll = Ui.Scroll();
            _body.Add(scroll);
            scroll.Add(Ui.Text("Workflows", "sc-title"));
            scroll.Add(Ui.Text("Multi-step recipes built on Scenario: turnarounds, sprite sheets, texture sets. Each run shows its price first.", "sc-subtitle"));
            var tabs = new VisualElement();
            tabs.AddToClassList("sc-tabs");
            foreach (var (id, label) in new[] { ("team", "Your team"), ("public", "Featured") })
            {
                var tab = Ui.Text(label, "sc-tab");
                tab.EnableInClassList("sc-selected", _tab == id);
                var target = id;
                tab.RegisterCallback<ClickEvent>(e =>
                {
                    _tab = target;
                    if (!_lists.ContainsKey(_tab)) _ = Load(_tab);
                    ShowList();
                });
                tabs.Add(tab);
            }
            scroll.Add(tabs);
            var search = Ui.Search("Filter workflows", text =>
            {
                _query = text;
                ShowList();
            });
            search.SetValueWithoutNotify(_query);
            search.style.marginBottom = 8;
            scroll.Add(search);
            if (_loading)
            {
                scroll.Add(Ui.Row(Ui.Spinner(true), Ui.Text("  Loading workflows…", "sc-dim")));
                return;
            }
            if (_error.Length > 0)
            {
                scroll.Add(Ui.Banner(_error, "error", "Retry", () => _ = Load(_tab)));
                return;
            }
            if (!_lists.TryGetValue(_tab, out var list)) return;
            var words = _query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var shown = list.Where(w => words.All(word => (Json.Str(w, "name") + " " + Json.Str(w, "description")).ToLowerInvariant().Contains(word))).ToList();
            if (shown.Count == 0)
            {
                scroll.Add(Ui.Empty("workflows", _tab == "team" ? "No team workflow yet" : "Nothing matches",
                    _tab == "team" ? "Workflows your team builds on Scenario appear here. Featured ones are in the other tab." : null));
                return;
            }
            foreach (var workflow in shown)
            {
                var w = workflow;
                var row = new VisualElement();
                row.AddToClassList("sc-list-row");
                row.name = "workflow-" + Json.Str(w, "id");
                var thumb = new VisualElement();
                thumb.AddToClassList("sc-thumb");
                var url = Json.Str(w["thumbnail"], "url");
                if (url.Length > 0) Thumbnails.Load(thumb, Json.Str(w["thumbnail"], "assetId", Json.Str(w, "id")), url);
                else thumb.Add(Ui.Icon("workflows"));
                row.Add(thumb);
                var text = new VisualElement();
                text.style.flexGrow = 1;
                text.style.flexShrink = 1;
                text.Add(Ui.Text(Json.Str(w, "name"), "sc-bold", "sc-ellipsis"));
                var line = Json.Str(w, "shortDescription");
                if (line.Length == 0) line = Json.Str(w, "description");
                text.Add(Ui.Text(line, "sc-dim", "sc-small", "sc-ellipsis"));
                var inputs = (w["inputs"] as JArray ?? new JArray()).Count;
                text.Add(Ui.Text($"{inputs} input{(inputs == 1 ? "" : "s")}", "sc-faint", "sc-small"));
                row.Add(text);
                row.Add(Ui.Icon("chevron-right", "sc-icon-sm"));
                row.RegisterCallback<ClickEvent>(_ => Open(w));
                scroll.Add(row);
            }
        }

        /// <summary>Opens a workflow by id (from "Use settings" on a past run), with its inputs.</summary>
        public void Open(string workflowId, JObject values)
        {
            _ = OpenById(workflowId, values);
        }

        async Task OpenById(string workflowId, JObject values)
        {
            var result = await S.Client.GetWorkflow(workflowId);
            if (!result.Ok)
            {
                Window.Toast(result.Error.Message, "error");
                return;
            }
            Open(result.Value["workflow"] as JObject ?? result.Value, values);
        }

        void Open(JObject workflow, JObject values = null)
        {
            _workflow = workflow;
            _fields = SchemaForm.FieldsFromSchema(workflow);
            _values = SchemaForm.InitialValues(_fields);
            _slots.Clear();
            if (values != null)
                foreach (var field in _fields.Where(f => values[f.Name] != null))
                {
                    if (!field.IsFile) _values[field.Name] = values[field.Name].DeepClone();
                    else
                    {
                        var ids = values[field.Name] is JArray array ? array.Select(a => a.ToString()) : new[] { values[field.Name].ToString() };
                        foreach (var id in ids) Items(field.Name).Add(new SlotItem { AssetId = id, Kind = field.Kind, Label = id });
                        Sync(field);
                    }
                }
            ShowDetail();
            Changed();
        }

        void ShowDetail()
        {
            _body.Clear();
            _slotViews.Clear();
            var scroll = Ui.Scroll();
            _body.Add(scroll);
            var back = Ui.Button("Workflows", () =>
            {
                _workflow = null;
                ShowList();
            }, "chevron-left", "sc-btn-ghost", "sc-btn-sm");
            back.style.alignSelf = Align.FlexStart;
            back.style.marginBottom = 8;
            scroll.Add(back);
            var header = Ui.Card("sc-card-row");
            var thumb = new VisualElement();
            thumb.AddToClassList("sc-thumb");
            thumb.style.width = thumb.style.height = 64;
            var url = Json.Str(_workflow["thumbnail"], "url");
            if (url.Length > 0) Thumbnails.Load(thumb, Json.Str(_workflow["thumbnail"], "assetId", Json.Str(_workflow, "id")), url);
            else thumb.Add(Ui.Icon("workflows", "sc-icon-lg"));
            header.Add(thumb);
            var text = new VisualElement();
            text.style.flexShrink = 1;
            text.style.flexGrow = 1;
            text.Add(Ui.Text(Json.Str(_workflow, "name"), "sc-model-name", "sc-wrap"));
            text.Add(Ui.Text(Json.Str(_workflow, "description"), "sc-dim", "sc-small", "sc-wrap"));
            var workflowId = Json.Str(_workflow, "id");
            var openWeb = Ui.Button("Open on Scenario", () => Application.OpenURL(Links.Workflow(workflowId, S.Client.TeamId, S.Client.ProjectId)), "external", "sc-btn-outline", "sc-btn-sm");
            openWeb.style.alignSelf = Align.FlexStart;
            openWeb.style.marginTop = 8;
            text.Add(openWeb);
            header.Add(text);
            scroll.Add(header);

            if (_fields.Count > 0) scroll.Add(Ui.Section("Inputs"));
            foreach (var field in _fields)
            {
                var f = field;
                if (f.IsFile)
                {
                    var slot = new InputSlot(f, () => Items(f.Name), o => _ = Attach(f, new SlotItem { Source = o, Label = o.name, Kind = f.Kind }, () => S.Upload(o)),
                        p => _ = Attach(f, new SlotItem { ThumbPath = f.Kind == "image" ? p : "", Label = System.IO.Path.GetFileName(p), Kind = f.Kind }, () => S.UploadPath(p)),
                        item =>
                        {
                            Items(f.Name).Remove(item);
                            Sync(f);
                            Redraw(f);
                            Changed();
                        },
                        () => Window.PickScenarioAsset(f.Kind, asset =>
                        {
                            Window.Show("workflows");
                            AddItem(f, new SlotItem { AssetId = Json.Str(asset, "id"), Kind = f.Kind, ThumbUrl = Json.Str(asset, "url"), Label = Json.Str(asset, "id") });
                        }));
                    _slotViews[f.Name] = slot;
                    scroll.Add(slot);
                }
                else scroll.Add(FieldControls.Build(f, _values, Changed));
            }
            var place = Ui.Text("Outputs import into " + Files.OutputFolder(ScenarioProjectSettings.instance.OutputRoot, "Workflows", DateTime.Now, ScenarioProjectSettings.instance.DatedFolders),
                "sc-faint", "sc-small", "sc-wrap");
            place.style.marginTop = 4;
            scroll.Add(place);

            var footer = new VisualElement();
            footer.AddToClassList("sc-footer");
            _run = new Button(() => _ = Run()) { text = "", name = "workflow-run" };
            _run.AddToClassList("sc-btn");
            _run.AddToClassList("sc-btn-primary");
            _runLabel = new Label("Run") { pickingMode = PickingMode.Ignore };
            _run.Add(_runLabel);
            footer.Add(_run);
            _note = Ui.Text("", "sc-footer-note");
            footer.Add(_note);
            _body.Add(footer);
        }

        List<SlotItem> Items(string name)
        {
            if (!_slots.TryGetValue(name, out var list)) _slots[name] = list = new List<SlotItem>();
            return list;
        }

        void Sync(Field field)
        {
            var ids = Items(field.Name).Where(i => i.AssetId.Length > 0).Select(i => i.AssetId).ToList();
            if (ids.Count == 0) _values.Remove(field.Name);
            else if (field.Type == FieldType.File) _values[field.Name] = ids[0];
            else _values[field.Name] = new JArray(ids);
        }

        void Redraw(Field field)
        {
            if (_slotViews.TryGetValue(field.Name, out var view)) view.Draw();
        }

        void AddItem(Field field, SlotItem item)
        {
            if (field.Type == FieldType.File) Items(field.Name).Clear();
            Items(field.Name).Add(item);
            Sync(field);
            Redraw(field);
            Changed();
        }

        async Task Attach(Field field, SlotItem item, Func<Task<Result<string>>> upload)
        {
            item.Uploading = true;
            AddItem(field, item);
            var result = await upload();
            item.Uploading = false;
            if (result.Ok) item.AssetId = result.Value;
            else
            {
                item.Error = result.Error.Message;
                Window.Toast(result.Error.Message, "error");
            }
            Sync(field);
            Redraw(field);
            Changed();
        }

        GenerateRequest Request(out List<string> problems)
        {
            problems = SchemaForm.Validate(_fields, _values);
            if (_slots.Values.SelectMany(v => v).Any(i => i.Uploading)) problems.Insert(0, "Uploading references…");
            return new GenerateRequest
            {
                OutputType = OutputTypes.All,
                WorkflowId = Json.Str(_workflow, "id"),
                ModelName = Json.Str(_workflow, "name"),
                Payload = SchemaForm.Payload(_fields, _values),
                Prompt = Json.Str(_workflow, "name"),
                Place = false,
                Origin = "window",
                Folder = Files.OutputFolder(ScenarioProjectSettings.instance.OutputRoot, "Workflows", DateTime.Now, ScenarioProjectSettings.instance.DatedFolders),
            };
        }

        void Changed()
        {
            _priceVersion++;
            var version = _priceVersion;
            _quote = null;
            _priceError = "";
            UpdateFooter();
            schedule.Execute(() =>
            {
                if (version == _priceVersion) _ = Price(version);
            }).StartingIn(400);
        }

        async Task Price(int version)
        {
            var request = Request(out var problems);
            if (problems.Count > 0 || _workflow == null) return;
            _checking = true;
            UpdateFooter();
            var result = await S.Estimate(request);
            if (version != _priceVersion) return;
            _checking = false;
            if (result.Ok) _quote = result.Value;
            else _priceError = result.Error.Message;
            UpdateFooter();
        }

        void UpdateFooter()
        {
            if (_run == null || _workflow == null) return;
            var request = Request(out var problems);
            var block = S.BlockReason(request);
            var enabled = false;
            string label;
            var note = "";
            if (_sending) label = "Sending…";
            else if (problems.Count > 0)
            {
                label = problems[0].StartsWith("Uploading") ? problems[0] : "Run";
                note = problems[0].StartsWith("Uploading") ? "" : problems[0];
            }
            else if (block.Length > 0)
            {
                label = "Blocked";
                note = block;
            }
            else if (_quote != null)
            {
                label = $"Run · {Ui.Cu(_quote.Cu)}";
                enabled = true;
            }
            else if (_priceError.Length > 0)
            {
                label = "Price unavailable";
                note = _priceError;
            }
            else label = _checking ? "Checking price…" : "Run";
            _runLabel.text = label;
            _run.SetEnabled(enabled);
            _note.text = note;
            _note.style.display = note.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        async Task Run()
        {
            var request = Request(out var problems);
            if (problems.Count > 0 || _quote == null) return;
            var quote = S.QuoteFor(request);
            if (quote == null)
            {
                Changed();
                Window.Toast("The price check expired. Check the new price, then run again.", "info");
                return;
            }
            var threshold = ScenarioProjectSettings.instance.ConfirmAboveCu;
            if (threshold > 0 && quote.Cu > threshold &&
                !EditorUtility.DisplayDialog("Run the workflow", $"{request.ModelName} costs {Ui.Cu(quote.Cu)} for this run, above your {threshold} CU confirmation limit.", $"Run · {Ui.Cu(quote.Cu)}", "Cancel"))
                return;
            _sending = true;
            UpdateFooter();
            var result = await S.Generate(request);
            _sending = false;
            if (result.Ok) Window.Toast($"{request.ModelName} is running. Outputs import when it finishes.", "success", "Jobs", () => Window.Show("jobs"));
            else Window.Toast(result.Error.Message, result.Error.NotSent ? "warning" : "error");
            Changed();
        }
    }
}
