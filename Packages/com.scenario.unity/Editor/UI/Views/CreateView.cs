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
    /// Create: output type, model, prompt, inputs, settings, destination, and a
    /// sticky Generate button that always shows the price Scenario quoted for
    /// exactly this request. Results land in the tray below.
    /// </summary>
    public sealed class CreateView : View
    {
        enum PriceState { Idle, Checking, Ready, Error }

        public override string Id => "create";

        // Form state
        string _type;
        string _modelId = "";
        /// <summary>The typed step that opened the form, while one of its models is selected.</summary>
        AssetStep _step;
        JObject _schema;
        List<Field> _fields = new List<Field>();
        JObject _values = new JObject();
        string _promptName = "";
        string _schemaError = "";
        bool _schemaLoading;
        int _schemaVersion;
        bool _moreOpen;
        bool _place = true;
        string _folder = "";
        readonly Dictionary<string, List<SlotItem>> _slots = new Dictionary<string, List<SlotItem>>();
        readonly List<SlotItem> _pendingAttach = new List<SlotItem>();
        JObject _pendingValues;
        readonly Dictionary<string, InputSlot> _slotViews = new Dictionary<string, InputSlot>();

        // Price
        PriceState _price;
        string _priceError = "";
        QuoteBook.Quote _quote;
        int _priceVersion;
        bool _sending;

        // Prompt Spark and suggestions
        double _sparkCu = -1;
        bool _sparking;
        bool _suggestOpen;
        bool _suggestLoading;
        JArray _suggestions;
        string _suggestError = "";

        // Elements
        readonly VisualElement _typeRow;
        readonly VisualElement _banners;
        readonly VisualElement _modelBox;
        readonly VisualElement _suggestBox;
        readonly VisualElement _promptBox;
        readonly TextField _prompt;
        readonly Button _improve;
        readonly VisualElement _inputsBox;
        readonly VisualElement _settingsBox;
        readonly VisualElement _destination;
        readonly Button _generate;
        readonly Label _generateLabel;
        readonly VisualElement _generateSpinner;
        readonly Label _footerNote;
        readonly TwoPaneSplitView _split;
        readonly VisualElement _results;
        readonly Label _trayCount;
        bool _trayCollapsed;
        bool _trayWanted;
        string _traySignature = "";
        double _catalogStamp = -1;
        bool _initialized;

        public CreateView(ScenarioWindow window) : base(window)
        {
            var user = ScenarioUserSettings.instance;
            _type = OutputTypes.List.Any(t => t.Id == user.OutputType) ? user.OutputType : OutputTypes.Image;

            _split = new TwoPaneSplitView(1, Mathf.Max(90, user.TrayHeight), TwoPaneSplitViewOrientation.Vertical);
            _split.AddToClassList("sc-fill");
            _split.RegisterCallback<GeometryChangedEvent>(_ => ApplyTray());
            Add(_split);

            var top = new VisualElement();
            top.style.flexGrow = 1;
            top.style.minHeight = 160;
            _split.Add(top);
            var scroll = Ui.Scroll();
            top.Add(scroll);

            _banners = new VisualElement();
            scroll.Add(_banners);
            _typeRow = new VisualElement();
            _typeRow.AddToClassList("sc-row-wrap");
            _typeRow.style.marginBottom = 4;
            scroll.Add(_typeRow);
            BuildTypes();

            _modelBox = new VisualElement();
            scroll.Add(_modelBox);
            _suggestBox = new VisualElement();
            scroll.Add(_suggestBox);

            _promptBox = new VisualElement();
            _promptBox.AddToClassList("sc-field");
            var promptLabel = new VisualElement();
            promptLabel.AddToClassList("sc-field-label");
            promptLabel.style.marginTop = 6;
            promptLabel.Add(new Label("Prompt"));
            promptLabel.Add(Ui.Spacer());
            _improve = Ui.Button("Improve", ImprovePrompt, "wand", "sc-btn-ghost", "sc-btn-sm");
            _improve.name = "improve";
            _improve.tooltip = "Prompt Spark rewrites your prompt for this model. Its price shows on the button first.";
            promptLabel.Add(_improve);
            _promptBox.Add(promptLabel);
            _prompt = new TextField { multiline = true, name = "prompt" };
            _prompt.AddToClassList("sc-prompt");
            _prompt.textEdition.hidePlaceholderOnFocus = false;
            _prompt.value = user.LastPrompt ?? "";
            _prompt.RegisterValueChangedCallback(e =>
            {
                if (_promptName.Length > 0) _values[_promptName] = e.newValue;
                user.LastPrompt = e.newValue;
                FormChanged();
            });
            _prompt.RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && e.actionKey)
                {
                    e.StopPropagation();
                    _ = Generate();
                }
            }, TrickleDown.TrickleDown);
            _promptBox.Add(_prompt);
            scroll.Add(_promptBox);

            _inputsBox = new VisualElement();
            scroll.Add(_inputsBox);
            _settingsBox = new VisualElement();
            scroll.Add(_settingsBox);
            _destination = new VisualElement();
            _destination.AddToClassList("sc-destination");
            scroll.Add(_destination);

            var footer = new VisualElement();
            footer.AddToClassList("sc-footer");
            _generate = new Button(() => _ = Generate()) { text = "", name = "generate" };
            _generate.AddToClassList("sc-btn");
            _generate.AddToClassList("sc-btn-primary");
            _generateSpinner = Ui.Spinner(true);
            _generateSpinner.style.marginRight = 8;
            _generateSpinner.style.display = DisplayStyle.None;
            _generate.Add(_generateSpinner);
            _generateLabel = new Label("Generate") { pickingMode = PickingMode.Ignore };
            _generate.Add(_generateLabel);
            footer.Add(_generate);
            _footerNote = Ui.Text("", "sc-footer-note");
            footer.Add(_footerNote);
            top.Add(footer);

            var tray = new VisualElement();
            tray.AddToClassList("sc-tray");
            tray.style.minHeight = 90;
            _split.Add(tray);
            var trayHeader = new VisualElement();
            trayHeader.AddToClassList("sc-tray-header");
            var trayTitle = Ui.Text("RESULTS", "sc-section");
            trayTitle.style.marginTop = trayTitle.style.marginBottom = 0; // centered with the count
            trayHeader.Add(trayTitle);
            _trayCount = Ui.Text("", "sc-faint", "sc-small");
            _trayCount.style.marginLeft = 6;
            trayHeader.Add(_trayCount);
            trayHeader.Add(Ui.Spacer());
            trayHeader.Add(Ui.Button("All jobs", () => Window.Show("jobs"), null, "sc-btn-ghost", "sc-btn-sm"));
            trayHeader.Add(Ui.Button("Clear", ClearTray, null, "sc-btn-ghost", "sc-btn-sm"));
            tray.Add(trayHeader);
            var trayScroll = Ui.Scroll(padded: false);
            tray.Add(trayScroll);
            _results = new VisualElement();
            _results.AddToClassList("sc-results");
            trayScroll.Add(_results);
            tray.RegisterCallback<GeometryChangedEvent>(e =>
            {
                if (!_trayCollapsed && e.newRect.height >= 90 && Math.Abs(e.newRect.height - user.TrayHeight) > 4) user.TrayHeight = e.newRect.height;
            });
        }

        // --- Lifecycle -------------------------------------------------------

        public override void OnShow()
        {
            if (!_initialized)
            {
                _initialized = true;
                SelectType(_type, force: true);
            }
            Refresh();
        }

        public override void Refresh()
        {
            if (S.Catalog.FetchedAt != _catalogStamp)
            {
                _catalogStamp = S.Catalog.FetchedAt;
                if (string.IsNullOrEmpty(_modelId) || (S.Model(_modelId) == null && !S.Catalog.IsEmpty && _schema == null)) SelectType(_type, force: true);
                else BuildModelCard();
            }
            BuildBanners();
            UpdateFooter();
            UpdateTray();
        }

        string _bannerSignature = "";

        void BuildBanners()
        {
            var unresolved = S.Ledger.Unresolved().Count;
            var catalogFailed = S.CatalogError.Length > 0 && S.Catalog.IsEmpty;
            var signature = $"{S.EstimateAlarm}|{unresolved}|{catalogFailed}";
            if (signature == _bannerSignature) return;
            _bannerSignature = signature;
            _banners.Clear();
            if (S.EstimateAlarm)
                _banners.Add(Ui.Banner("Price checks are paused: a price check returned a job. Check Jobs, then restart Unity.", "error", "Jobs", () => Window.Show("jobs")));
            if (unresolved > 0)
                _banners.Add(Ui.Banner($"{unresolved} request{(unresolved == 1 ? " has" : "s have")} an unknown outcome and may have run on Scenario.", "warning", "Review", () => Window.Show("jobs")));
            if (catalogFailed)
                _banners.Add(Ui.Banner("The model catalog did not load: " + S.CatalogError, "warning", "Retry", () => _ = S.RefreshCatalog(true)));
        }

        public override void OnSelectionChange()
        {
            UpdateDestination();
            foreach (var slot in _slotViews.Values) slot.Draw();
        }

        // --- Output type and model --------------------------------------------

        void BuildTypes()
        {
            _typeRow.Clear();
            foreach (var type in OutputTypes.List)
            {
                var t = type;
                var chip = Ui.Chip(t.Title, t.Icon, t.Id == _type, () => SelectType(t.Id));
                chip.name = "type-" + t.Id;
                chip.tooltip = t.Hint;
                _typeRow.Add(chip);
            }
        }

        public void SelectType(string typeId, bool force = false)
        {
            if (!force && typeId == _type) return;
            _type = OutputTypes.Get(typeId).Id;
            _step = null;
            var user = ScenarioUserSettings.instance;
            user.OutputType = _type;
            BuildTypes();
            SelectModel(DefaultModel(_type));
        }

        string DefaultModel(string typeId)
        {
            var user = ScenarioUserSettings.instance;
            var saved = user.ModelFor(typeId);
            if (!string.IsNullOrEmpty(saved) && (S.Catalog.IsEmpty || S.Model(saved) != null)) return saved;
            var type = OutputTypes.Get(typeId);
            foreach (var pick in type.QuickPicks)
                if (S.Catalog.IsEmpty || S.Model(pick) != null) return pick;
            if (typeId == OutputTypes.All && user.RecentModels.Count > 0) return user.RecentModels[0];
            return S.Catalog.Search(new Catalog.Query { OutputType = typeId }).FirstOrDefault()?.Id ?? (type.QuickPicks.FirstOrDefault() ?? "");
        }

        /// <summary>Switches the model; the prompt and matching references carry over.</summary>
        public void SelectModel(string modelId, JObject presetValues = null)
        {
            if (string.IsNullOrEmpty(modelId)) return;
            if (modelId != _modelId) ScenarioUserSettings.instance.SetModelFor(_type, modelId);
            _modelId = modelId;
            if (_step != null && !_step.Models.Contains(modelId)) _step = null;
            _pendingValues = presetValues;
            _suggestOpen = false;
            _sparkCu = -1;
            BuildModelCard();
            BuildSuggest();
            _ = LoadSchema();
        }

        async Task LoadSchema()
        {
            var version = ++_schemaVersion;
            _schemaLoading = true;
            _schemaError = "";
            _schema = null;
            UpdateFooter();
            if (!S.Ready)
            {
                _schemaLoading = false;
                BuildForm();
                return;
            }
            var result = await S.Schema(_modelId);
            if (version != _schemaVersion) return;
            _schemaLoading = false;
            if (!result.Ok)
            {
                _schemaError = result.Error.Message;
                _fields = new List<Field>();
                BuildForm();
                return;
            }
            _schema = result.Value;
            var carried = _slots.Values.SelectMany(v => v).Where(i => i.Error.Length == 0).ToList();
            carried.AddRange(_pendingAttach);
            _pendingAttach.Clear();
            _fields = SchemaForm.FieldsFromSchema(_schema, OutputTypes.Restrict);
            _promptName = SchemaForm.PromptName(_fields);
            _values = SchemaForm.InitialValues(_fields, OutputTypes.Presets(_modelId));
            if (_pendingValues != null)
            {
                foreach (var field in _fields)
                    if (_pendingValues[field.Name] != null && !field.IsFile) _values[field.Name] = _pendingValues[field.Name].DeepClone();
                if (_promptName.Length > 0 && _pendingValues[_promptName] != null) _prompt.SetValueWithoutNotify(_pendingValues[_promptName].ToString());
                foreach (var field in _fields.Where(f => f.IsFile && _pendingValues[f.Name] != null))
                {
                    var ids = _pendingValues[field.Name] is JArray array ? array.Select(a => a.ToString()) : new[] { _pendingValues[field.Name].ToString() };
                    carried.InsertRange(0, ids.Where(id => id.StartsWith("asset_")).Select(id => ItemForAsset(id, field.Kind)));
                }
                _pendingValues = null;
            }
            if (_promptName.Length > 0) _values[_promptName] = _prompt.value;
            _slots.Clear();
            var unplaced = new List<SlotItem>();
            foreach (var item in carried)
            {
                var field = _fields.Where(f => f.IsFile && string.Equals(f.Kind, item.Kind, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(f => Items(f.Name).Count < Capacity(f) && !Items(f.Name).Any(i => i.AssetId.Length > 0 && i.AssetId == item.AssetId));
                if (field == null)
                {
                    unplaced.Add(item);
                    continue;
                }
                Items(field.Name).Add(item);
            }
            foreach (var field in _fields.Where(f => f.IsFile)) SyncSlot(field);
            if (unplaced.Any(i => i.Source != null || i.AssetId.Length > 0) && _pendingNotice)
                Window.Toast($"{Name(_modelId)} takes no {unplaced[0].Kind} input, so the reference was left out.", "info");
            _pendingNotice = false;
            BuildForm();
            if (SchemaForm.SparkAvailable(_schema, _fields)) _ = LoadSparkPrice(version);
        }

        bool _pendingNotice;

        async Task LoadSparkPrice(int version)
        {
            var price = await S.PromptSparkPrice(_modelId);
            if (version != _schemaVersion) return;
            _sparkCu = price.Ok ? price.Value : -1;
            UpdateImprove();
        }

        static int Capacity(Field field) => field.Type == FieldType.File ? 1 : field.MaxItems > 0 ? field.MaxItems : 16;

        string Name(string modelId)
        {
            var model = S.Model(modelId);
            return model != null ? model.Name : OutputTypes.KnownName(modelId) ?? modelId.Replace("model_", "");
        }

        void BuildModelCard()
        {
            _modelBox.Clear();
            var model = S.Model(_modelId);
            var card = Ui.Card("sc-model-card");
            card.name = "model-card";
            var thumb = new VisualElement();
            thumb.AddToClassList("sc-thumb");
            thumb.Add(Ui.Icon(OutputTypes.Get(model != null ? OutputTypes.TypeFor(model) : _type).Icon, "sc-icon-lg"));
            card.Add(thumb);
            var text = new VisualElement();
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            var name = Ui.Text(Name(_modelId), "sc-model-name", "sc-ellipsis");
            text.Add(name);
            var line = model == null
                ? (S.CatalogLoading ? "Loading the model catalog…" : "Model details load with the catalog")
                : string.Join(" · ", new[] { model.Provider, model.CapabilityText }.Where(s => !string.IsNullOrEmpty(s)));
            text.Add(Ui.Text(line, "sc-dim", "sc-small", "sc-ellipsis"));
            var badges = new VisualElement();
            badges.AddToClassList("sc-badges");
            if (model != null)
            {
                if (model.IsFeatured) badges.Add(Ui.Pill("Featured", "info"));
                if (Json.Now() - model.CreatedAt < 45 * 86400 && model.CreatedAt > 0) badges.Add(Ui.Pill("New", "success"));
                if (model.IsPrivate) badges.Add(Ui.Pill("Team"));
                if (model.IsLegacy) badges.Add(Ui.Pill("Legacy", "warning"));
            }
            if (badges.childCount > 0) text.Add(badges);
            card.Add(text);
            card.tooltip = model?.Description ?? "";
            var type = OutputTypes.Get(_type);
            if (type.Family == null && _type != OutputTypes.All)
            {
                var suggest = Ui.Button("Suggest", ToggleSuggest, "wand", "sc-btn-ghost", "sc-btn-sm");
                suggest.name = "suggest";
                suggest.tooltip = "Suggest models: Scenario ranks the best ones for your prompt";
                suggest.Q<Label>()?.AddToClassList("sc-wide-only");
                card.Add(suggest);
            }
            var change = Ui.Button("Change", OpenBrowser, null, "sc-btn-outline", "sc-btn-sm");
            change.name = "change-model";
            change.style.marginLeft = 4;
            card.Add(change);
            _modelBox.Add(card);

            // A step offers the models that do that step; otherwise the type's picks.
            var pickIds = _step != null ? _step.Models.ToArray() : type.QuickPicks;
            if (_step != null)
            {
                var stepLine = Ui.Text(_step.Title + (_step.Hint.Length > 0 ? ". " + _step.Hint : ""), "sc-dim", "sc-small");
                stepLine.name = "step-hint";
                stepLine.style.whiteSpace = WhiteSpace.Normal;
                stepLine.style.marginBottom = 4;
                _modelBox.Add(stepLine);
            }
            if (pickIds.Length > 1)
            {
                var picks = new VisualElement();
                picks.AddToClassList("sc-row-wrap");
                picks.style.marginTop = -2;
                picks.style.marginBottom = 4;
                foreach (var id in pickIds.Where(p => S.Catalog.IsEmpty || S.Model(p) != null))
                {
                    var pick = id;
                    var chip = Ui.Chip(ShortName(pick), null, pick == _modelId, () => SelectModel(pick), "sc-chip-sm");
                    chip.tooltip = Name(pick);
                    picks.Add(chip);
                }
                _modelBox.Add(picks);
            }
        }

        /// <summary>"GPT Image 2.5 Sunburst" stays; long provider prefixes go.</summary>
        string ShortName(string modelId)
        {
            var name = Name(modelId);
            foreach (var prefix in new[] { "Google ", "OpenAI ", "ByteDance ", "Alibaba ", "Hyper3D " })
                if (name.StartsWith(prefix)) name = name.Substring(prefix.Length);
            return name.Length > 30 ? name.Substring(0, 29) + "…" : name;
        }

        void OpenBrowser()
        {
            Window.OpenOverlay(new ModelBrowser(Window, _type, _modelId, id =>
            {
                Window.CloseOverlay();
                var model = S.Model(id);
                var natural = OutputTypes.TypeFor(model);
                if (_type != OutputTypes.All && model != null && !OutputTypes.Get(_type).Accepts(model) && natural != OutputTypes.All)
                {
                    _type = natural;
                    ScenarioUserSettings.instance.OutputType = _type;
                    BuildTypes();
                }
                SelectModel(id);
            }));
        }

        // --- Suggest (recommend) --------------------------------------------

        string CapabilityForSuggest()
        {
            var hasImage = _slots.Values.SelectMany(v => v).Any(i => i.Kind == "image");
            switch (_type)
            {
                case OutputTypes.Video: return hasImage ? "img2video" : "txt2video";
                case OutputTypes.Model3D: return hasImage ? "img23d" : "txt23d";
                case OutputTypes.Audio: return "txt2audio";
                default: return hasImage ? "img2img" : "txt2img";
            }
        }

        void ToggleSuggest()
        {
            _suggestOpen = !_suggestOpen;
            if (_suggestOpen) _ = LoadSuggestions();
            BuildSuggest();
        }

        async Task LoadSuggestions()
        {
            _suggestLoading = true;
            _suggestError = "";
            _suggestions = null;
            BuildSuggest();
            var result = await S.Recommend(CapabilityForSuggest(), _prompt.value, 3);
            _suggestLoading = false;
            if (result.Ok) _suggestions = result.Value["ranked"] as JArray ?? new JArray();
            else _suggestError = result.Error.Message;
            BuildSuggest();
        }

        void BuildSuggest()
        {
            _suggestBox.Clear();
            if (!_suggestOpen) return;
            var card = Ui.Card();
            card.name = "suggestions";
            var header = Ui.Row(Ui.Text("Suggested for this prompt", "sc-bold"), Ui.Spacer(), Ui.IconButton("close", "Close", () =>
            {
                _suggestOpen = false;
                BuildSuggest();
            }, "sc-btn-sm"));
            card.Add(header);
            if (_suggestLoading)
            {
                card.Add(Ui.Row(Ui.Spinner(true), Ui.Text("  Asking Scenario…", "sc-dim")));
            }
            else if (_suggestError.Length > 0)
            {
                card.Add(Ui.Text(_suggestError, "sc-dim", "sc-wrap"));
            }
            else if (_suggestions != null)
            {
                if (_suggestions.Count == 0) card.Add(Ui.Text("No suggestion for this prompt.", "sc-dim"));
                foreach (var entry in _suggestions.OfType<JObject>().Take(3))
                {
                    var id = Json.Str(entry, "model_id");
                    var row = new VisualElement();
                    row.AddToClassList("sc-list-row");
                    row.style.alignItems = Align.FlexStart;
                    var text = new VisualElement();
                    text.style.flexGrow = 1;
                    text.style.flexShrink = 1;
                    text.Add(Ui.Text($"{Json.Str(entry, "rank", "")}. {Json.Str(entry, "name", Name(id))}", "sc-bold"));
                    var why = Json.Str(entry, "explanation");
                    if (why.Length > 0) text.Add(Ui.Text(why, "sc-dim", "sc-small", "sc-wrap"));
                    var meta = string.Join(" · ", new[] { Json.Str(entry, "cost_summary"), Json.Str(entry, "speed_summary") }.Where(s => s.Length > 0));
                    if (meta.Length > 0) text.Add(Ui.Text(meta, "sc-faint", "sc-small", "sc-wrap"));
                    row.Add(text);
                    var inputs = entry["suggested_inputs"] as JObject;
                    var use = Ui.Button(id == _modelId ? "In use" : "Use", () => SelectModel(id, inputs), null, "sc-btn-sm");
                    use.SetEnabled(id != _modelId || (inputs != null && inputs.HasValues));
                    row.Add(use);
                    card.Add(row);
                }
            }
            _suggestBox.Add(card);
        }

        // --- Form ------------------------------------------------------------------

        void BuildForm()
        {
            var type = OutputTypes.Get(_type);
            _prompt.textEdition.placeholder = type.Placeholder;
            _promptBox.style.display = _promptName.Length > 0 || _schema == null ? DisplayStyle.Flex : DisplayStyle.None;
            var promptField = _fields.FirstOrDefault(f => f.Name == _promptName);
            if (promptField != null && promptField.MaxLength > 0) _prompt.maxLength = promptField.MaxLength;
            UpdateImprove();

            _inputsBox.Clear();
            _slotViews.Clear();
            if (_schemaError.Length > 0)
                _inputsBox.Add(Ui.Banner(_schemaError, "error", "Retry", () => _ = LoadSchema()));
            var files = _fields.Where(f => f.IsFile).ToList();
            if (files.Count > 0)
            {
                _inputsBox.Add(Ui.Section("Inputs"));
                foreach (var field in files)
                {
                    var f = field;
                    var slot = new InputSlot(f, () => Items(f.Name), o => _ = AttachObject(f, o), p => _ = AttachPath(f, p), item => RemoveItem(f, item),
                        () => Window.PickScenarioAsset(f.Kind, asset => AttachScenarioAsset(asset, f)));
                    _slotViews[f.Name] = slot;
                    _inputsBox.Add(slot);
                }
            }

            _settingsBox.Clear();
            var (essentials, more) = FieldControls.Split(_fields, _promptName);
            if (essentials.Count + more.Count > 0) _settingsBox.Add(Ui.Section("Settings"));
            foreach (var field in essentials) _settingsBox.Add(FieldControls.Build(field, _values, FormChanged));
            if (more.Count > 0)
            {
                var toggle = new VisualElement();
                toggle.AddToClassList("sc-row");
                toggle.AddToClassList("sc-link");
                toggle.name = "more-settings";
                toggle.style.marginBottom = 8;
                var chevron = Ui.Icon(_moreOpen ? "chevron-down" : "chevron-right", "sc-icon-sm");
                chevron.style.marginRight = 4;
                toggle.Add(chevron);
                toggle.Add(new Label($"More settings ({more.Count})"));
                var moreBox = new VisualElement();
                moreBox.style.display = _moreOpen ? DisplayStyle.Flex : DisplayStyle.None;
                foreach (var field in more) moreBox.Add(FieldControls.Build(field, _values, FormChanged));
                toggle.RegisterCallback<ClickEvent>(_ =>
                {
                    _moreOpen = !_moreOpen;
                    moreBox.style.display = _moreOpen ? DisplayStyle.Flex : DisplayStyle.None;
                    Ui.SetIcon(chevron, _moreOpen ? "chevron-down" : "chevron-right");
                });
                _settingsBox.Add(toggle);
                _settingsBox.Add(moreBox);
            }
            UpdateDestination();
            FormChanged();
        }

        void UpdateImprove()
        {
            var available = SchemaForm.SparkAvailable(_schema, _fields);
            _improve.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
            Ui.SetButtonText(_improve, _sparking ? "Improving…" : _sparkCu == 0 ? "Improve · free" : _sparkCu > 0 ? $"Improve · {Ui.Cu(_sparkCu)}" : "Improve");
            _improve.SetEnabled(!_sparking);
        }

        string EffectiveType()
        {
            if (_type != OutputTypes.All) return _type;
            var natural = OutputTypes.TypeFor(S.Model(_modelId));
            return natural;
        }

        GameObject PlaceTarget()
        {
            var go = Selection.activeGameObject;
            if (go == null || EditorUtility.IsPersistent(go)) return null;
            return Placer.Describe(EffectiveType(), go).Length > 0 ? go : null;
        }

        void UpdateDestination()
        {
            _destination.Clear();
            var type = OutputTypes.Get(EffectiveType());
            var settings = ScenarioProjectSettings.instance;
            var folder = _folder.Length > 0 ? _folder : Files.OutputFolder(settings.OutputRoot, type.Folder, DateTime.Now, settings.DatedFolders);
            var icon = Ui.Icon("folder", "sc-icon-sm");
            icon.style.marginRight = 6;
            _destination.Add(icon);
            var text = new Label();
            text.AddToClassList("sc-ellipsis");
            text.style.flexShrink = 1;
            var target = PlaceTarget();
            string where;
            if (!_place) where = "not placed";
            else if (target != null) where = "places " + Placer.Describe(type.Id, target);
            else
                switch (type.Id)
                {
                    case OutputTypes.Model3D: where = "places at the Scene view pivot"; break;
                    case OutputTypes.Image: where = "places as a new sprite"; break;
                    case OutputTypes.Video: where = "places on a new screen"; break;
                    case OutputTypes.Audio: where = "places a new AudioSource"; break;
                    case OutputTypes.Material: where = "places on a preview sphere"; break;
                    case OutputTypes.Skybox: where = "sets the scene skybox"; break;
                    default: where = "places by kind"; break;
                }
            text.text = $"Saves to {folder} · {where}";
            text.tooltip = text.text;
            text.name = "destination";
            _destination.Add(text);
            _destination.Add(Ui.Spacer());
            var placeLabel = Ui.Text("Place", "sc-dim", "sc-small");
            placeLabel.style.marginLeft = 8;
            placeLabel.style.marginRight = 6;
            _destination.Add(placeLabel);
            var place = Ui.Switch(_place, value =>
            {
                _place = value;
                UpdateDestination();
            });
            place.name = "place";
            place.tooltip = "Place the result in the open scene when it arrives";
            _destination.Add(place);
        }

        /// <summary>Output folder for the next generations (from the Project window's "Generate…").</summary>
        public void SetFolder(string folder)
        {
            _folder = folder ?? "";
            UpdateDestination();
        }

        // --- Inputs ------------------------------------------------------------------

        List<SlotItem> Items(string field)
        {
            if (!_slots.TryGetValue(field, out var list)) _slots[field] = list = new List<SlotItem>();
            return list;
        }

        void SyncSlot(Field field)
        {
            // Inputs that refuse GLB get a converted copy; other inputs get the original back.
            var format = SchemaForm.RequiredModelFormat(field);
            foreach (var item in Items(field.Name).Where(i => i.AssetId.Length > 0 && !i.Uploading && i.Error.Length == 0 && i.Format != format).ToList())
            {
                if (format.Length > 0) _ = Convert(field, item, format);
                else
                {
                    item.AssetId = item.OriginalAssetId;
                    item.Format = "";
                }
            }
            var ids = Items(field.Name).Where(i => i.AssetId.Length > 0 && !i.Uploading).Select(i => i.AssetId).ToList();
            if (ids.Count == 0) _values.Remove(field.Name);
            else if (field.Type == FieldType.File) _values[field.Name] = ids[0];
            else _values[field.Name] = new JArray(ids);
        }

        async Task Convert(Field field, SlotItem item, string format)
        {
            item.Uploading = true;
            RedrawSlot(field);
            var original = item.Format.Length > 0 ? item.OriginalAssetId : item.AssetId;
            var result = await S.Convert(original, format);
            item.Uploading = false;
            if (result.Ok)
            {
                item.OriginalAssetId = original;
                item.AssetId = result.Value;
                item.Format = format;
            }
            else
            {
                item.Error = result.Error.Message;
                Window.Toast(result.Error.Message, "error");
            }
            SyncSlot(field);
            RedrawSlot(field);
            FormChanged();
        }

        void RedrawSlot(Field field)
        {
            if (_slotViews.TryGetValue(field.Name, out var view)) view.Draw();
        }

        void AddItem(Field field, SlotItem item)
        {
            var items = Items(field.Name);
            if (field.Type == FieldType.File) items.Clear();
            else if (items.Count >= Capacity(field)) items.RemoveAt(0);
            items.Add(item);
            SyncSlot(field);
            RedrawSlot(field);
            FormChanged();
        }

        async Task AttachObject(Field field, UnityEngine.Object source)
        {
            var item = new SlotItem { Label = source.name, Kind = field.Kind, Source = source, Uploading = true };
            AddItem(field, item);
            await Upload(field, item, () => S.Upload(source));
        }

        async Task AttachPath(Field field, string path)
        {
            var item = new SlotItem { Label = System.IO.Path.GetFileName(path), Kind = field.Kind, ThumbPath = field.Kind == "image" ? path : "", Uploading = true };
            AddItem(field, item);
            await Upload(field, item, () => S.UploadPath(path));
        }

        async Task Upload(Field field, SlotItem item, Func<Task<Result<string>>> upload)
        {
            var result = await upload();
            item.Uploading = false;
            if (result.Ok) item.AssetId = result.Value;
            else
            {
                item.Error = result.Error.Message;
                Window.Toast(result.Error.Message, "error");
            }
            SyncSlot(field);
            RedrawSlot(field);
            FormChanged();
        }

        void RemoveItem(Field field, SlotItem item)
        {
            Items(field.Name).Remove(item);
            SyncSlot(field);
            RedrawSlot(field);
            FormChanged();
        }

        static SlotItem ItemForAsset(string assetId, string kind)
        {
            var item = new SlotItem { AssetId = assetId, Kind = kind ?? "", Label = assetId };
            var local = LocalAsset(assetId);
            if (local != null)
            {
                item.Source = local;
                item.Label = local.name;
            }
            return item;
        }

        /// <summary>The Unity asset imported from this Scenario asset, if any.</summary>
        public static UnityEngine.Object LocalAsset(string assetId)
        {
            foreach (var path in Provenance.AllPaths())
            {
                var info = Provenance.Read(path);
                if (info != null && info.AssetId == assetId) return AssetDatabase.LoadMainAssetAtPath(path);
            }
            return null;
        }

        /// <summary>Attaches a Scenario asset (from the Library) to the first input of its kind.</summary>
        public void AttachScenarioAsset(JObject asset, Field field = null)
        {
            var kind = Files.KindOf(asset);
            field = field ?? SchemaForm.FileInputFor(_fields, kind);
            if (field == null)
            {
                Window.Toast($"{Name(_modelId)} takes no {kind} input. Pick a model that does, or use Next on the asset.", "info");
                return;
            }
            var id = Json.Str(asset, "id");
            AddItem(field, new SlotItem { AssetId = id, Kind = field.Kind, Label = Json.Str(asset, "name", id), ThumbUrl = Json.Str(asset, "url") });
        }

        /// <summary>Attaches Unity objects (selection, context menus) to the inputs that take them.</summary>
        public void AttachObjects(IEnumerable<UnityEngine.Object> objects)
        {
            foreach (var o in objects)
            {
                var kind = InputSlot.KindOf(o);
                if (_schemaLoading || _schema == null)
                {
                    _pendingAttach.Add(new SlotItem { Kind = kind, Label = o.name, Source = o });
                    continue;
                }
                var field = SchemaForm.FileInputFor(_fields, kind);
                if (field == null)
                {
                    Window.Toast($"{Name(_modelId)} takes no {kind} input.", "info");
                    continue;
                }
                _ = AttachObject(field, o);
            }
        }

        /// <summary>Attaches an asset already on Scenario (a result) to the first input of its kind.</summary>
        public void AttachExisting(SlotItem item)
        {
            var field = SchemaForm.FileInputFor(_fields, item.Kind);
            if (field == null)
            {
                Window.Toast($"{Name(_modelId)} takes no {item.Kind} input. Use Next to pick a step for it.", "info");
                return;
            }
            if (item.AssetId.Length == 0 && item.Source != null)
            {
                _ = AttachObject(field, item.Source);
                return;
            }
            AddItem(field, item);
        }

        /// <summary>Restores a past request's model and settings. Nothing is sent.</summary>
        public void UseSettings(JobRow row)
        {
            if (row.WorkflowId.Length > 0)
            {
                Window.Show("workflows");
                ((WorkflowsView)Window.ViewForTests("workflows")).Open(row.WorkflowId, row.Parameters);
                return;
            }
            var type = OutputTypes.List.Any(t => t.Id == row.OutputType) ? row.OutputType : OutputTypes.All;
            _type = type;
            ScenarioUserSettings.instance.OutputType = _type;
            BuildTypes();
            _slots.Clear();
            SelectModel(row.ModelId, row.Parameters);
            Window.Show("create");
            Window.Toast("Settings restored. Nothing was sent.", "info");
        }

        // --- Typed steps -------------------------------------------------------------

        /// <summary>Opens Create on a step's model with the asset attached; nothing is sent.</summary>
        public void StartStep(AssetStep step, SlotItem source)
        {
            var model = step.DefaultModel;
            if (!S.Catalog.IsEmpty && S.Model(model) == null)
            {
                var fallback = S.Catalog.Search(new Catalog.Query { OutputType = step.OutputType, Capability = step.Capability }).FirstOrDefault();
                if (fallback != null) model = fallback.Id;
            }
            _type = step.OutputType;
            _step = step;
            ScenarioUserSettings.instance.OutputType = _type;
            BuildTypes();
            _slots.Clear();
            source.Kind = step.Kind;
            _pendingAttach.Clear();
            _pendingAttach.Add(source);
            _pendingNotice = true;
            SelectModel(model);
            _ = UploadPending(source);
            Window.Show("create");
        }

        /// <summary>A step on a Unity asset that is not on Scenario yet: upload while the schema loads.</summary>
        async Task UploadPending(SlotItem item)
        {
            if (item.AssetId.Length > 0 || item.Source == null) return;
            item.Uploading = true;
            var result = await S.Upload(item.Source);
            item.Uploading = false;
            if (result.Ok) item.AssetId = result.Value;
            else
            {
                item.Error = result.Error.Message;
                Window.Toast(result.Error.Message, "error");
            }
            foreach (var field in _fields.Where(f => f.IsFile && Items(f.Name).Contains(item)))
            {
                SyncSlot(field);
                RedrawSlot(field);
            }
            FormChanged();
        }

        public static GenericMenu StepsMenu(GenericMenu menu, string kind, Action<AssetStep> run, string prefix = "")
        {
            foreach (var step in OutputTypes.StepsFor(kind))
            {
                var s = step;
                menu.AddItem(new GUIContent(prefix + (s.Group.Length > 0 ? s.Group + "/" : "") + s.Title), false, () => run(s));
            }
            return menu;
        }

        // --- Prompt Spark ----------------------------------------------------------

        async void ImprovePrompt()
        {
            var text = _prompt.value ?? "";
            if (text.Trim().Length == 0)
            {
                Window.Toast("Write a few words first: Prompt Spark builds on them.", "info");
                return;
            }
            var threshold = ScenarioProjectSettings.instance.ConfirmAboveCu;
            if (threshold > 0 && _sparkCu > threshold && !EditorUtility.DisplayDialog("Improve the prompt", $"Prompt Spark costs {Ui.Cu(_sparkCu)}.", "Improve", "Cancel"))
                return;
            _sparking = true;
            UpdateImprove();
            var result = await S.ImprovePrompt(_modelId, Name(_modelId), text);
            _sparking = false;
            UpdateImprove();
            if (!result.Ok)
            {
                Window.Toast(result.Error.Message, result.Error.NotSent ? "warning" : "error");
                return;
            }
            var previous = text;
            _prompt.value = result.Value;
            Window.Toast("Prompt improved.", "success", "Undo", () => _prompt.value = previous, 8);
        }

        public void FocusPrompt() => _prompt.Focus();

        public void SetPrompt(string text) => _prompt.value = text ?? "";

        // --- Price and generate ------------------------------------------------------

        void FormChanged()
        {
            _priceVersion++;
            var version = _priceVersion;
            _price = PriceState.Idle;
            _quote = null;
            UpdateFooter();
            schedule.Execute(() =>
            {
                if (version == _priceVersion) _ = CheckPrice(version);
            }).StartingIn(400);
        }

        GenerateRequest BuildRequest(out List<string> problems)
        {
            problems = new List<string>();
            if (_schemaLoading) problems.Add("Loading the model…");
            else if (_schema == null) problems.Add(_schemaError.Length > 0 ? "The model could not be loaded." : S.Ready ? "Pick a model." : "Sign in first.");
            if (problems.Count > 0) return null;
            if (_slots.Values.SelectMany(v => v).Any(i => i.Uploading)) problems.Add("Uploading references…");
            problems.AddRange(SchemaForm.Validate(_fields, _values, _modelId));
            return new GenerateRequest
            {
                OutputType = EffectiveType(),
                ModelId = _modelId,
                ModelName = Name(_modelId),
                Payload = SchemaForm.Payload(_fields, _values),
                Prompt = _prompt.value ?? "",
                Place = _place,
                Target = _place ? PlaceTarget() : null,
                Origin = "window",
                Folder = _folder,
            };
        }

        async Task CheckPrice(int version)
        {
            var request = BuildRequest(out var problems);
            if (request == null || problems.Count > 0 || !S.Ready)
            {
                UpdateFooter();
                return;
            }
            var cached = S.QuoteFor(request);
            if (cached != null)
            {
                _quote = cached;
                _price = PriceState.Ready;
                UpdateFooter();
                return;
            }
            _price = PriceState.Checking;
            UpdateFooter();
            var result = await S.Estimate(request);
            if (version != _priceVersion) return;
            if (result.Ok)
            {
                _quote = result.Value;
                _price = PriceState.Ready;
            }
            else
            {
                _price = PriceState.Error;
                _priceError = result.Error.Message;
            }
            UpdateFooter();
        }

        void UpdateFooter()
        {
            if (_generate == null) return;
            var request = BuildRequest(out var problems);
            string label;
            var note = "";
            var noteClass = "";
            var enabled = false;
            var busy = false;
            var block = request != null ? S.BlockReason(request) : "";
            if (_sending)
            {
                label = "Sending…";
                busy = true;
            }
            else if (!S.Ready)
            {
                label = "Generate";
                note = S.IsSignedIn ? "Pick a Scenario project first." : "Sign in to generate.";
            }
            else if (problems.Count > 0)
            {
                label = problems[0].StartsWith("Write a prompt") ? "Needs a prompt" : problems[0].StartsWith("Add ") ? "Needs an input" : problems[0].StartsWith("Loading") || problems[0].StartsWith("Uploading") ? problems[0] : "Generate";
                busy = problems[0].StartsWith("Loading") || problems[0].StartsWith("Uploading");
                if (!busy) note = problems[0];
            }
            else if (S.Client.SpendingDisabled)
            {
                label = "Spending off";
                note = "Spending is turned off in Settings.";
                noteClass = "sc-warn";
            }
            else if (block.Length > 0)
            {
                label = "Blocked";
                note = block;
                noteClass = "sc-warn";
            }
            else if (S.EstimateAlarm)
            {
                label = "Price checks paused";
                note = "A price check returned a job. Check Jobs, then restart Unity.";
                noteClass = "sc-err";
            }
            else if (_price == PriceState.Ready && _quote != null)
            {
                label = $"Generate · {Ui.Cu(_quote.Cu)}";
                enabled = true;
                var threshold = ScenarioProjectSettings.instance.ConfirmAboveCu;
                note = threshold > 0 && _quote.Cu > threshold
                    ? $"Above your {threshold} CU limit: you will be asked to confirm."
                    : (Application.platform == RuntimePlatform.OSXEditor ? "⌘↩ to generate" : "Ctrl+Enter to generate");
            }
            else if (_price == PriceState.Error)
            {
                label = "Price unavailable";
                note = _priceError;
                noteClass = "sc-err";
            }
            else
            {
                label = "Checking price…";
                busy = true;
            }
            _generateLabel.text = label;
            _generate.SetEnabled(enabled);
            _generateSpinner.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            _footerNote.text = note;
            _footerNote.EnableInClassList("sc-warn", noteClass == "sc-warn");
            _footerNote.EnableInClassList("sc-err", noteClass == "sc-err");
            _footerNote.style.display = note.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_price == PriceState.Error && noteClass == "sc-err")
            {
                _footerNote.tooltip = "Click to check the price again";
                _footerNote.UnregisterCallback<ClickEvent>(RetryPrice);
                _footerNote.RegisterCallback<ClickEvent>(RetryPrice);
            }
        }

        void RetryPrice(ClickEvent _) => FormChanged();

        public async Task Generate()
        {
            if (_sending) return;
            var request = BuildRequest(out var problems);
            if (request == null || problems.Count > 0)
            {
                if (problems.Count > 0) Window.Toast(problems[0], "info");
                return;
            }
            var block = S.BlockReason(request);
            if (block.Length > 0)
            {
                Window.Toast(block, "warning");
                return;
            }
            var quote = S.QuoteFor(request);
            if (quote == null)
            {
                // The quote expired (10 minutes) or the form changed: price again, and ask again if it moved.
                var fresh = await S.Estimate(request);
                if (!fresh.Ok)
                {
                    Window.Toast(fresh.Error.Message, "error");
                    return;
                }
                var moved = _quote == null || Math.Abs(fresh.Value.Cu - _quote.Cu) > 1e-6;
                _quote = fresh.Value;
                _price = PriceState.Ready;
                UpdateFooter();
                if (moved)
                {
                    Window.Toast($"The price is now {Ui.Cu(fresh.Value.Cu)}. Generate again to confirm.", "info");
                    return;
                }
                quote = fresh.Value;
            }
            var threshold = ScenarioProjectSettings.instance.ConfirmAboveCu;
            if (threshold > 0 && quote.Cu > threshold &&
                !EditorUtility.DisplayDialog("Generate with Scenario", $"{request.ModelName} costs {Ui.Cu(quote.Cu)} for this request, above your {threshold} CU confirmation limit.",
                    $"Generate · {Ui.Cu(quote.Cu)}", "Cancel"))
                return;
            _sending = true;
            UpdateFooter();
            var result = await S.Generate(request);
            _sending = false;
            if (!result.Ok) Window.Toast(result.Error.Message, result.Error.NotSent ? "warning" : "error");
            FormChanged();
            UpdateTray();
        }

        // --- Results tray --------------------------------------------------------------

        static double TrayClearedAt
        {
            get => SessionState.GetFloat("Scenario.TrayClearedAt", 0);
            set => SessionState.SetFloat("Scenario.TrayClearedAt", (float)value);
        }

        void ClearTray()
        {
            TrayClearedAt = Json.Now();
            UpdateTray();
        }

        List<JobRow> TrayRows() => S.Ledger.Jobs
            .Where(r => r.OutputType != "prompt" && r.State != JobState.Dismissed && r.CreatedAt > TrayClearedAt && r.State != JobState.Rejected)
            .Take(30).ToList();

        /// <summary>An empty tray gives its height to the form; it opens with the first result.
        /// The split view only collapses once laid out, so this runs again on its first layout.</summary>
        void ApplyTray()
        {
            if (_split.fixedPane == null) return;
            if (!_trayWanted && !_trayCollapsed)
            {
                _split.CollapseChild(1);
                _trayCollapsed = true;
            }
            else if (_trayWanted && _trayCollapsed)
            {
                _split.UnCollapse();
                _trayCollapsed = false;
            }
        }

        void UpdateTray()
        {
            if (_results == null) return;
            var rows = TrayRows();
            var signature = string.Join("|", rows.Select(r => $"{r.LocalId}:{r.State}:{r.Imported}:{Math.Round(r.Progress, 1)}:{r.Files.Count}"));
            if (signature == _traySignature) return;
            _traySignature = signature;
            _results.Clear();
            _trayCount.text = rows.Count > 0 ? rows.Count.ToString() : "";
            _trayWanted = rows.Count > 0;
            ApplyTray();
            foreach (var row in rows) _results.Add(ResultCard.Build(row, Window));
        }
    }
}
