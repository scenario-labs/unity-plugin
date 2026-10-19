using System;
using System.Collections.Generic;
using System.Linq;
using Scenario.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// Every model in the catalog, searchable as you type. Favorites and the type's
    /// quick picks come first, then the newest. Keyboard: arrows, Enter, Esc.
    /// </summary>
    public sealed class ModelBrowser : VisualElement
    {
        readonly Action<string> _pick;
        readonly string _current;
        readonly TextField _search;
        readonly VisualElement _typeRow;
        readonly ListView _list;
        readonly Label _count;
        readonly VisualElement _status;
        string _type;
        string _capability = "";
        bool _team;
        bool _legacy;
        bool _favorites;
        List<ModelInfo> _items = new List<ModelInfo>();

        ScenarioService S => ScenarioService.Instance;

        public ModelBrowser(ScenarioWindow window, string type, string current, Action<string> pick)
        {
            _type = type;
            _current = current;
            _pick = pick;
            AddToClassList("sc-sheet");
            name = "model-browser";
            style.height = Length.Percent(86);

            var header = new VisualElement();
            header.AddToClassList("sc-sheet-header");
            _search = Ui.Search("Search models: name, provider, capability", _ => Filter());
            _search.name = "model-search";
            _search.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            header.Add(_search);
            _typeRow = new VisualElement();
            _typeRow.AddToClassList("sc-row-wrap");
            _typeRow.style.marginTop = 8;
            header.Add(_typeRow);
            var toggles = new VisualElement();
            toggles.AddToClassList("sc-row");
            toggles.style.marginTop = 2;
            toggles.Add(FilterChip("Favorites", "star", () => _favorites, v => _favorites = v));
            toggles.Add(FilterChip("Team models", null, () => _team, v => _team = v));
            toggles.Add(FilterChip("Show legacy", null, () => _legacy, v => _legacy = v));
            toggles.Add(Ui.Spacer());
            var refresh = Ui.IconButton("refresh", "Refresh the catalog from Scenario", () => _ = Reload(), "sc-btn-sm");
            toggles.Add(refresh);
            header.Add(toggles);
            Add(header);

            _status = new VisualElement();
            Add(_status);
            _list = new ListView
            {
                fixedItemHeight = 58,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = BindRow,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            };
            _list.AddToClassList("sc-fill");
            _list.itemsChosen += chosen =>
            {
                var model = chosen.OfType<ModelInfo>().FirstOrDefault();
                if (model != null) _pick(model.Id);
            };
            Add(_list);

            var footer = new VisualElement();
            footer.AddToClassList("sc-sheet-footer");
            footer.Add(new Label("↑↓ move   ↩ choose   Esc close"));
            footer.Add(Ui.Spacer());
            _count = new Label();
            footer.Add(_count);
            Add(footer);

            BuildTypes();
            Filter();
            RegisterCallback<AttachToPanelEvent>(_ => schedule.Execute(() => _search.Focus()).StartingIn(30));
            if (S.Catalog.IsStale(Json.Now())) _ = Reload(force: false);
        }

        /// <summary>A chip that filters the list while it is on.</summary>
        VisualElement FilterChip(string label, string icon, Func<bool> get, Action<bool> set)
        {
            VisualElement chip = null;
            chip = Ui.Chip(label, icon, get(), () =>
            {
                set(!get());
                chip.EnableInClassList("sc-selected", get());
                Filter();
            }, "sc-chip-sm");
            return chip;
        }

        async System.Threading.Tasks.Task Reload(bool force = true)
        {
            await S.RefreshCatalog(force);
            Filter();
        }

        void BuildTypes()
        {
            _typeRow.Clear();
            foreach (var type in OutputTypes.List)
            {
                var t = type;
                _typeRow.Add(Ui.Chip(t.Title, t.Icon, t.Id == _type, () =>
                {
                    _type = t.Id;
                    _capability = "";
                    BuildTypes();
                    Filter();
                }, "sc-chip-sm"));
            }
            var current = OutputTypes.Get(_type);
            if (current.Capabilities.Length > 1 && current.Family == null)
            {
                var labels = new List<string> { "Any input" };
                labels.AddRange(current.Capabilities.Select(Catalog.CapabilityLabel));
                var dropdown = new DropdownField(labels, _capability.Length == 0 ? 0 : Array.IndexOf(current.Capabilities, _capability) + 1);
                dropdown.style.width = 140;
                dropdown.style.height = 22;
                dropdown.RegisterValueChangedCallback(e =>
                {
                    var index = labels.IndexOf(e.newValue);
                    _capability = index <= 0 ? "" : current.Capabilities[index - 1];
                    Filter();
                });
                _typeRow.Add(dropdown);
            }
        }

        void Filter()
        {
            var user = ScenarioUserSettings.instance;
            _items = S.Catalog.Search(new Catalog.Query
            {
                Text = _search.value,
                OutputType = _type,
                ShowLegacy = _legacy,
                TeamOnly = _team,
                FavoritesOnly = _favorites,
                Favorites = user.Favorites,
                Capability = _capability,
            });
            _list.itemsSource = _items;
            _list.Rebuild();
            var index = _items.FindIndex(m => m.Id == _current);
            _list.SetSelectionWithoutNotify(new[] { index >= 0 && string.IsNullOrEmpty(_search.value) ? index : 0 });
            _count.text = $"{_items.Count} model{(_items.Count == 1 ? "" : "s")}";
            _status.Clear();
            if (S.CatalogLoading && S.Catalog.IsEmpty)
            {
                var row = Ui.Row(Ui.Spinner(true), Ui.Text("  Loading the model catalog…", "sc-dim"));
                row.style.paddingLeft = 14;
                row.style.paddingTop = 12;
                _status.Add(row);
            }
            else if (S.CatalogError.Length > 0 && S.Catalog.IsEmpty)
            {
                var banner = Ui.Banner(S.CatalogError, "error", "Retry", () => _ = Reload());
                banner.style.marginLeft = banner.style.marginRight = banner.style.marginTop = 10;
                _status.Add(banner);
            }
            else if (_items.Count == 0)
            {
                _status.Add(Ui.Empty("search", "No model matches", _legacy ? "Try fewer words." : "Try fewer words, or turn on Show legacy."));
            }
        }

        VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("sc-list-row");
            row.style.height = 54;
            var star = new VisualElement();
            star.AddToClassList("sc-icon");
            star.name = "star";
            star.style.marginRight = 10;
            star.pickingMode = PickingMode.Position;
            row.Add(star);
            var text = new VisualElement();
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            var top = new VisualElement();
            top.AddToClassList("sc-row");
            var name = new Label { name = "name" };
            name.AddToClassList("sc-bold");
            name.AddToClassList("sc-ellipsis");
            name.style.flexShrink = 1;
            top.Add(name);
            var badges = new VisualElement { name = "badges" };
            badges.AddToClassList("sc-row");
            badges.style.marginLeft = 6;
            top.Add(badges);
            text.Add(top);
            var line = new Label { name = "line" };
            line.AddToClassList("sc-dim");
            line.AddToClassList("sc-small");
            line.AddToClassList("sc-ellipsis");
            text.Add(line);
            var description = new Label { name = "description" };
            description.AddToClassList("sc-faint");
            description.AddToClassList("sc-small");
            description.AddToClassList("sc-ellipsis");
            text.Add(description);
            row.Add(text);
            star.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                if (star.userData is string id)
                {
                    ScenarioUserSettings.instance.ToggleFavorite(id);
                    _list.RefreshItems();
                }
            });
            return row;
        }

        void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var model = _items[index];
            var favorite = ScenarioUserSettings.instance.Favorites.Contains(model.Id);
            var star = row.Q("star");
            star.userData = model.Id;
            Ui.SetIcon(star, favorite ? "star-fill" : "star");
            star.tooltip = favorite ? "Remove from favorites" : "Add to favorites";
            row.Q<Label>("name").text = model.Name;
            var badges = row.Q("badges");
            badges.Clear();
            if (model.Id == _current) badges.Add(Ui.Pill("In use"));
            if (model.IsFeatured) badges.Add(Ui.Pill("Featured", "info"));
            if (model.CreatedAt > 0 && Json.Now() - model.CreatedAt < 45 * 86400) badges.Add(Ui.Pill("New", "success"));
            if (model.IsPrivate) badges.Add(Ui.Pill("Team"));
            if (model.IsLegacy) badges.Add(Ui.Pill("Legacy", "warning"));
            row.Q<Label>("line").text = string.Join(" · ", new[] { model.Provider, model.CapabilityText }.Where(s => !string.IsNullOrEmpty(s)));
            var description = model.Description;
            if (model.IsLegacy && model.ReplacementId.Length > 0) description = $"Replaced by {S.Model(model.ReplacementId)?.Name ?? model.ReplacementId}. " + description;
            row.Q<Label>("description").text = description;
            row.tooltip = model.Id;
        }

        void OnKey(KeyDownEvent e)
        {
            if (_items.Count == 0) return;
            var index = Mathf.Max(0, _list.selectedIndex);
            switch (e.keyCode)
            {
                case KeyCode.DownArrow:
                    index = Mathf.Min(_items.Count - 1, index + 1);
                    break;
                case KeyCode.UpArrow:
                    index = Mathf.Max(0, index - 1);
                    break;
                case KeyCode.PageDown:
                    index = Mathf.Min(_items.Count - 1, index + 8);
                    break;
                case KeyCode.PageUp:
                    index = Mathf.Max(0, index - 8);
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    e.StopPropagation();
                    _pick(_items[index].Id);
                    return;
                default:
                    return;
            }
            e.StopPropagation();
            _list.SetSelection(index);
            _list.ScrollToItem(index);
        }
    }
}
