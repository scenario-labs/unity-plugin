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
    /// The Scenario project's assets (search, kinds, lazy pages) and, in a second
    /// tab, every Unity asset that came from Scenario.
    /// </summary>
    public sealed class LibraryView : View
    {
        // Asset kinds as Scenario's search index stores them. Listing through search (not
        // assets_list) covers every source type (edits, video2video...) and returns
        // thumbnails for videos and 3D. Text and JSON assets stay out of the Library.
        // The index has no audio, so Audio lists the project's assets by type instead.
        static readonly (string id, string label, string[] kinds)[] Kinds =
        {
            ("", "All", new[] { "image", "image-hdr", "video", "3d" }),
            ("image", "Images", new[] { "image", "image-hdr" }),
            ("video", "Videos", new[] { "video" }),
            ("3d", "3D", new[] { "3d" }),
            ("audio", "Audio", new[] { "audio" }),
        };

        const int PageSize = 40;
        static readonly string[] AudioTypes = { "txt2audio", "audio2audio", "video2audio", "uploaded-audio" };

        public override string Id => "library";

        readonly VisualElement _tabs;
        readonly VisualElement _pickBanner;
        readonly VisualElement _toolbar;
        readonly TextField _search;
        readonly VisualElement _kinds;
        readonly ScrollView _scroll;
        readonly VisualElement _grid;
        readonly VisualElement _status;
        readonly VisualElement _actions;
        string _tab = "scenario";
        string _kind = "";
        string _query = "";
        int _offset;
        string _pageToken = "";
        bool _hasMore;
        bool _loading;
        bool _loaded;
        int _version;
        readonly List<JObject> _assets = new List<JObject>();
        JObject _selected;
        string _selectedPath = "";
        string _pickKind = "";
        Action<JObject> _pick;
        IVisualElementScheduledItem _searchTimer;

        public LibraryView(ScenarioWindow window) : base(window)
        {
            var top = new VisualElement();
            top.AddToClassList("sc-bar");
            top.style.paddingLeft = 14;
            top.style.paddingRight = 14;
            top.style.paddingTop = 10;
            Add(top);
            _tabs = new VisualElement();
            _tabs.AddToClassList("sc-tabs");
            top.Add(_tabs);
            _pickBanner = new VisualElement();
            top.Add(_pickBanner);
            _toolbar = new VisualElement();
            top.Add(_toolbar);
            _search = Ui.Search("Search your Scenario assets", text =>
            {
                _query = text;
                _searchTimer?.Pause();
                _searchTimer = schedule.Execute(() => Reload()).StartingIn(350);
            });
            _search.name = "library-search";
            _toolbar.Add(_search);
            _kinds = new VisualElement();
            _kinds.AddToClassList("sc-row-wrap");
            _kinds.style.marginTop = 8;
            _toolbar.Add(_kinds);

            _scroll = Ui.Scroll();
            Add(_scroll);
            _grid = new VisualElement();
            _grid.AddToClassList("sc-grid");
            _scroll.Add(_grid);
            _status = new VisualElement();
            _scroll.Add(_status);
            _scroll.verticalScroller.valueChanged += value =>
            {
                if (_tab == "scenario" && !_loading && _hasMore && value >= _scroll.verticalScroller.highValue - 40) _ = LoadPage();
            };

            _actions = new VisualElement();
            _actions.AddToClassList("sc-footer");
            Add(_actions);
            BuildTabs();
            BuildKinds();
        }

        public override void OnShow()
        {
            if (_tab == "scenario" && !_loaded) Reload();
            else if (_tab == "project") ShowProject();
            BuildActions();
        }

        /// <summary>Picking a reference for a Create input: one click attaches it and returns.</summary>
        public void BeginPick(string kind, Action<JObject> pick)
        {
            _pickKind = kind ?? "";
            _pick = pick;
            _tab = "scenario";
            _kind = Kinds.Any(k => k.id == _pickKind) ? _pickKind : "";
            BuildTabs();
            BuildKinds();
            _pickBanner.Clear();
            _pickBanner.Add(Ui.Banner($"Pick {InputSlot.Noun(_pickKind)} to use as input.", "info", "Cancel", EndPick));
            Reload();
        }

        void EndPick()
        {
            _pick = null;
            _pickKind = "";
            _pickBanner.Clear();
            Window.Show("create");
        }

        void BuildTabs()
        {
            _tabs.Clear();
            foreach (var (id, label) in new[] { ("scenario", "On Scenario"), ("project", "In this project") })
            {
                var tab = Ui.Text(label, "sc-tab");
                tab.EnableInClassList("sc-selected", _tab == id);
                tab.name = "tab-" + id;
                var target = id;
                tab.RegisterCallback<ClickEvent>(_ =>
                {
                    _tab = target;
                    _selected = null;
                    _selectedPath = "";
                    BuildTabs();
                    _toolbar.style.display = _tab == "scenario" ? DisplayStyle.Flex : DisplayStyle.None;
                    if (_tab == "scenario")
                    {
                        if (!_loaded) Reload();
                        else Redraw();
                    }
                    else ShowProject();
                    BuildActions();
                });
                _tabs.Add(tab);
            }
            if (_tab == "scenario")
            {
                _tabs.Add(Ui.Spacer());
                var refresh = Ui.IconButton("refresh", "Reload", () => Reload(), "sc-btn-sm");
                _tabs.Add(refresh);
            }
        }

        void BuildKinds()
        {
            _kinds.Clear();
            foreach (var (id, label, _) in Kinds)
            {
                var k = id;
                _kinds.Add(Ui.Chip(label, null, _kind == id, () => SelectKind(k), "sc-chip-sm"));
            }
        }

        internal void SelectKind(string kind)
        {
            _kind = kind;
            BuildKinds();
            Reload();
        }

        void Reload()
        {
            _version++;
            _assets.Clear();
            _offset = 0;
            _pageToken = "";
            _hasMore = false;
            _selected = null;
            _loaded = true;
            _grid.Clear();
            BuildActions();
            _ = LoadPage();
        }

        async Task LoadPage()
        {
            if (!S.Ready) return;
            var version = _version;
            _loading = true;
            DrawStatus();
            var query = _query.Trim();
            var listed = _kind == "audio";
            Result<JObject> result;
            if (listed)
            {
                result = await S.Client.ListAssets(new JObject { ["types"] = new JArray(AudioTypes) }, PageSize, _pageToken, includeUrls: true);
            }
            else
            {
                var kinds = Kinds.First(k => k.id == _kind).kinds;
                var args = new JObject
                {
                    ["target"] = "assets",
                    ["query"] = query,
                    ["filter"] = "kind IN [" + string.Join(", ", kinds.Select(k => "\"" + k + "\"")) + "]",
                    ["include_urls"] = true,
                    ["limit"] = PageSize,
                    ["offset"] = _offset,
                };
                if (query.Length == 0) args["sort_by"] = new JArray("createdAt:desc");
                result = await S.Client.Search(args);
            }
            if (version != _version) return;
            _loading = false;
            if (!result.Ok)
            {
                DrawStatus(result.Error.Message);
                return;
            }
            var list = result.Value["assets"] as JArray ?? result.Value["results"] as JArray ?? result.Value["hits"] as JArray ?? new JArray();
            if (listed)
            {
                // Newest first; a query keeps the assets whose prompt or description has every word.
                _pageToken = Json.Str(result.Value, "nextPaginationToken");
                _hasMore = _pageToken.Length > 0;
                var words = query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                list = new JArray(list.OfType<JObject>().Where(a =>
                {
                    var text = (Json.Str(a, "prompt") + " " + Json.Str(a, "description")).ToLowerInvariant();
                    return words.All(text.Contains);
                }));
            }
            else
            {
                _offset += list.Count;
                var total = result.Value["estimatedTotalHits"]?.Type == JTokenType.Integer ? (int)result.Value["estimatedTotalHits"] : 0;
                _hasMore = list.Count == PageSize && _offset < total;
            }
            foreach (var asset in list.OfType<JObject>())
            {
                if (_pickKind.Length > 0 && BaseKind(Files.KindOf(asset)) != _pickKind) continue;
                _assets.Add(asset);
                _grid.Add(Tile(asset));
            }
            DrawStatus();
        }

        static string BaseKind(string kind) => kind == "image-hdr" ? "image" : kind;

        void Redraw()
        {
            _grid.Clear();
            foreach (var asset in _assets) _grid.Add(Tile(asset));
            DrawStatus();
        }

        void DrawStatus(string error = "")
        {
            _status.Clear();
            if (error.Length > 0) _status.Add(Ui.Banner(error, "error", "Retry", Reload));
            else if (_loading) _status.Add(Ui.Row(Ui.Spinner(true), Ui.Text("  Loading…", "sc-dim")));
            else if (_assets.Count == 0)
                _status.Add(Ui.Empty("library", _query.Length > 0 ? "Nothing matches" : "No asset yet", "Generated and uploaded assets of this Scenario project show here."));
            else if (_hasMore)
                _status.Add(Ui.Button("Load more", () => _ = LoadPage(), null, "sc-btn-ghost", "sc-btn-sm"));
        }

        VisualElement Tile(JObject asset)
        {
            var id = Json.Str(asset, "id");
            var kind = Files.KindOf(asset);
            var tile = new VisualElement();
            tile.AddToClassList("sc-tile");
            tile.name = "asset-" + id;
            var thumb = new VisualElement();
            thumb.AddToClassList("sc-tile-thumb");
            var thumbUrl = Json.Str(asset["thumbnail"], "url");
            if (thumbUrl.Length == 0 && Json.Str(asset, "mimeType").StartsWith("image/")) thumbUrl = Json.Str(asset, "url");
            if (thumbUrl.Length > 0) Thumbnails.Load(thumb, id, thumbUrl);
            else thumb.Add(Ui.Icon(InputSlot.IconFor(kind), "sc-icon-lg"));
            tile.Add(thumb);
            var label = Json.Str(asset, "description");
            if (label.Length == 0) label = Json.Str(asset, "name", kind);
            tile.Add(Ui.Text(Files.ObjectName(label, kind), "sc-tile-label", "sc-ellipsis"));
            tile.tooltip = label;
            tile.EnableInClassList("sc-selected", _selected != null && Json.Str(_selected, "id") == id);
            tile.RegisterCallback<ClickEvent>(e =>
            {
                if (_pick != null)
                {
                    var pick = _pick;
                    EndPick();
                    pick(asset);
                    return;
                }
                _selected = asset;
                foreach (var child in _grid.Children()) child.RemoveFromClassList("sc-selected");
                tile.AddToClassList("sc-selected");
                BuildActions();
                if (e.clickCount == 2) _ = Import(place: false);
            });
            return tile;
        }

        void ShowProject()
        {
            _toolbar.style.display = DisplayStyle.None;
            _grid.Clear();
            _status.Clear();
            var paths = Provenance.AllPaths();
            if (paths.Count == 0)
            {
                _status.Add(Ui.Empty("layers", "Nothing from Scenario yet", "Every asset made with the plugin carries the Scenario label and its prompt, model and cost."));
                return;
            }
            foreach (var path in paths.Take(400))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) continue;
                var tile = new VisualElement();
                tile.AddToClassList("sc-tile");
                var thumb = new VisualElement();
                thumb.AddToClassList("sc-tile-thumb");
                Thumbnails.LoadAsset(thumb, asset);
                tile.Add(thumb);
                tile.Add(Ui.Text(asset.name, "sc-tile-label", "sc-ellipsis"));
                var info = Provenance.Read(path);
                tile.tooltip = info != null ? $"{info.ModelName}\n{info.Prompt}" : path;
                var p = path;
                tile.RegisterCallback<ClickEvent>(_ =>
                {
                    _selectedPath = p;
                    foreach (var child in _grid.Children()) child.RemoveFromClassList("sc-selected");
                    tile.AddToClassList("sc-selected");
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                    BuildActions();
                });
                _grid.Add(tile);
            }
            _status.Add(Ui.Text($"{paths.Count} asset{(paths.Count == 1 ? "" : "s")} with the Scenario label", "sc-faint", "sc-small"));
        }

        void BuildActions()
        {
            _actions.Clear();
            if (_tab == "project")
            {
                var asset = _selectedPath.Length > 0 ? AssetDatabase.LoadMainAssetAtPath(_selectedPath) : null;
                _actions.style.display = asset != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (asset == null) return;
                var info = Provenance.Read(_selectedPath);
                var kind = Importer.KindOfPath(_selectedPath);
                var item = new SlotItem { AssetId = info?.AssetId ?? "", Source = asset, Kind = kind, Label = asset.name };
                var row = new VisualElement();
                row.AddToClassList("sc-row-wrap");
                row.Add(Ui.Button("Use as input", () =>
                {
                    Window.Show("create");
                    Window.Create.AttachExisting(item);
                }, "upload", "sc-btn-sm"));
                AddStepButton(row, kind, () => new SlotItem { AssetId = item.AssetId, Source = asset, Kind = kind, Label = asset.name });
                if (info != null && info.WebUrl.Length > 0) row.Add(Ui.Button("Open on Scenario", () => Application.OpenURL(info.WebUrl), "external", "sc-btn-ghost", "sc-btn-sm"));
                _actions.Add(Pad(row));
                return;
            }
            _actions.style.display = _selected != null && _pick == null ? DisplayStyle.Flex : DisplayStyle.None;
            if (_selected == null) return;
            var id = Json.Str(_selected, "id");
            var assetKind = Files.KindOf(_selected);
            var buttons = new VisualElement();
            buttons.AddToClassList("sc-row-wrap");
            var import = Ui.Button("Import", () => _ = Import(place: false), "arrow-right", "sc-btn-sm");
            import.name = "library-import";
            buttons.Add(import);
            buttons.Add(Ui.Button("Import and place", () => _ = Import(place: true), "pin", "sc-btn-sm"));
            buttons.Add(Ui.Button("Use as input", () =>
            {
                Window.Show("create");
                Window.Create.AttachScenarioAsset(_selected);
            }, "upload", "sc-btn-sm"));
            var selected = _selected;
            AddStepButton(buttons, assetKind, () => new SlotItem
            {
                AssetId = id, Kind = assetKind, Label = Json.Str(selected, "description", id), ThumbUrl = Json.Str(selected, "url"),
            });
            buttons.Add(Ui.IconButton("external", "Open on Scenario", () => Application.OpenURL(S.AssetLink(id)), "sc-btn-sm"));
            buttons.Add(Ui.IconButton("copy", "Copy asset id", () =>
            {
                EditorGUIUtility.systemCopyBuffer = id;
                Window.Toast("Asset id copied.", "info");
            }, "sc-btn-sm"));
            _actions.Add(Pad(buttons));
        }

        static VisualElement Pad(VisualElement row)
        {
            foreach (var child in row.Children())
            {
                child.style.marginRight = 6;
                child.style.marginBottom = 4;
            }
            return row;
        }

        void AddStepButton(VisualElement row, string kind, Func<SlotItem> item)
        {
            if (!OutputTypes.StepsFor(kind).Any()) return;
            var next = Ui.Button("Next", null, "wand", "sc-btn-sm");
            next.clicked += () => Ui.Menu(next, menu => CreateView.StepsMenu(menu, kind, step => Window.Create.StartStep(step, item())));
            row.Add(next);
        }

        async Task Import(bool place)
        {
            if (_selected == null) return;
            var asset = _selected;
            var kind = Files.KindOf(asset);
            var type = OutputTypes.TypeForKind(kind);
            Window.Toast("Importing…", "info", seconds: 2);
            var prompt = Json.Str(asset, "description");
            var target = place && Selection.activeGameObject != null && !EditorUtility.IsPersistent(Selection.activeGameObject) &&
                         Placer.Describe(type, Selection.activeGameObject).Length > 0
                ? Selection.activeGameObject
                : null;
            var result = await S.ImportAssets(new[] { Json.Str(asset, "id") }, type, prompt, place, target);
            if (!result.Ok)
            {
                Window.Toast(result.Error.Message, "error");
                return;
            }
            var main = Importer.Main(result.Value);
            var unity = main != null ? AssetDatabase.LoadMainAssetAtPath(main.Path) : null;
            Window.Toast($"Imported to {main?.Path}.", "success", "Show", () =>
            {
                if (unity != null) EditorGUIUtility.PingObject(unity);
            });
        }
    }
}
