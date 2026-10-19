using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace Scenario.Editor.UI
{
    /// <summary>One reference attached to a file input.</summary>
    public sealed class SlotItem
    {
        public string AssetId = "";
        public string Label = "";
        public string Kind = "";
        public UnityEngine.Object Source;
        public string ThumbUrl = "";
        public string ThumbPath = "";
        public bool Uploading;
        public string Error = "";
        /// <summary>Set when AssetId is a converted copy (FBX for inputs that refuse GLB): the format and the original id.</summary>
        public string Format = "";
        public string OriginalAssetId = "";
    }

    /// <summary>
    /// A file input of the model: drop zone, thumbnails, and a "+" menu with the
    /// sources Unity offers (selection, Scene view, main camera, project, Scenario).
    /// </summary>
    public sealed class InputSlot : VisualElement
    {
        public readonly Field Field;
        readonly Func<List<SlotItem>> _items;
        readonly Action<UnityEngine.Object> _attachObject;
        readonly Action<string> _attachPath;
        readonly Action<SlotItem> _remove;
        readonly Action _pickScenario;
        readonly VisualElement _row;

        public InputSlot(Field field, Func<List<SlotItem>> items, Action<UnityEngine.Object> attachObject, Action<string> attachPath,
            Action<SlotItem> remove, Action pickScenario)
        {
            Field = field;
            _items = items;
            _attachObject = attachObject;
            _attachPath = attachPath;
            _remove = remove;
            _pickScenario = pickScenario;
            AddToClassList("sc-field");
            name = "slot-" + field.Name;

            var label = new VisualElement();
            label.AddToClassList("sc-field-label");
            label.Add(new Label(field.Label));
            var hint = field.Required ? "required" : "optional";
            if (field.Type == FieldType.Files && field.MaxItems > 0) hint += $" · up to {field.MaxItems}";
            label.Add(Ui.Text(hint, "sc-field-hint"));
            label.tooltip = field.Description;
            Add(label);

            _row = new VisualElement();
            _row.AddToClassList("sc-slot");
            Add(_row);
            RegisterDrop(_row);
            Draw();
        }

        public bool Full
        {
            get
            {
                var count = _items().Count;
                if (Field.Type == FieldType.File) return count >= 1;
                return Field.MaxItems > 0 && count >= Field.MaxItems;
            }
        }

        public void Draw()
        {
            _row.Clear();
            var items = _items();
            _row.EnableInClassList("sc-empty", items.Count == 0);
            foreach (var item in items) _row.Add(Thumb(item));
            if (items.Count == 0)
            {
                var icon = Ui.Icon(IconFor(Field.Kind));
                icon.style.marginLeft = 6;
                icon.style.marginRight = 8;
                _row.Add(icon);
                var text = Ui.Text($"Drop {Noun(Field.Kind)} here", "sc-dim", "sc-small");
                text.style.flexGrow = 1;
                _row.Add(text);
            }
            else
            {
                _row.Add(Ui.Spacer());
            }
            if (!Full)
            {
                var add = Ui.IconButton("plus", "Add from the selection, a capture, the project or Scenario", null, "sc-btn-sm");
                add.clicked += () => Menu(add);
                _row.Add(add);
            }
        }

        VisualElement Thumb(SlotItem item)
        {
            var thumb = new VisualElement();
            thumb.AddToClassList("sc-slot-item");
            thumb.tooltip = item.Error.Length > 0 ? item.Error : item.Label + (item.AssetId.Length > 0 ? "\n" + item.AssetId : "");
            if (item.Source != null) Thumbnails.LoadAsset(thumb, item.Source);
            else if (item.ThumbPath.Length > 0) SetFileThumb(thumb, item.ThumbPath);
            else if (item.ThumbUrl.Length > 0) Thumbnails.Load(thumb, item.AssetId, item.ThumbUrl);
            if (item.Uploading)
            {
                thumb.style.alignItems = Align.Center;
                thumb.style.justifyContent = Justify.Center;
                thumb.Add(Ui.Spinner(true));
            }
            else if (item.Error.Length > 0)
            {
                thumb.style.alignItems = Align.Center;
                thumb.style.justifyContent = Justify.Center;
                thumb.Add(Ui.Icon("warning"));
            }
            else if (item.Source == null && item.ThumbPath.Length == 0 && item.ThumbUrl.Length == 0)
            {
                thumb.style.alignItems = Align.Center;
                thumb.style.justifyContent = Justify.Center;
                thumb.Add(Ui.Icon(IconFor(item.Kind)));
            }
            var remove = new VisualElement();
            remove.AddToClassList("sc-slot-remove");
            remove.Add(Ui.Icon("close"));
            remove.tooltip = "Remove";
            remove.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                _remove(item);
            });
            if (!item.Uploading)
            {
                remove.style.position = Position.Absolute;
                remove.style.right = 0;
                remove.style.top = 0;
                thumb.Add(remove);
            }
            thumb.RegisterCallback<ClickEvent>(_ =>
            {
                if (item.Source != null) EditorGUIUtility.PingObject(item.Source);
            });
            return thumb;
        }

        static void SetFileThumb(VisualElement target, string path)
        {
            try
            {
                var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                if (texture.LoadImage(File.ReadAllBytes(path))) target.style.backgroundImage = new StyleBackground(texture);
            }
            catch (IOException)
            {
                // A missing capture just shows no thumbnail.
            }
        }

        void Menu(VisualElement anchor)
        {
            var kind = Field.Kind;
            Ui.Menu(anchor, menu =>
            {
                var selected = Selection.objects.Where(o => Accepts(o, kind)).ToList();
                if (selected.Count > 0)
                    menu.AddItem(new GUIContent(selected.Count == 1 ? $"Selection ({Safe(selected[0].name)})" : $"Selection ({selected.Count} items)"), false,
                        () => { foreach (var o in selected.Take(Room())) _attachObject(o); });
                else menu.AddDisabledItem(new GUIContent($"Selection (select {Noun(kind)} first)"));
                if (kind == "image")
                {
                    menu.AddItem(new GUIContent("Capture the Scene view"), false, () =>
                    {
                        var path = Capture.SceneView();
                        if (path != null) _attachPath(path);
                    });
                    menu.AddItem(new GUIContent("Capture the main camera"), false, () =>
                    {
                        var path = Capture.MainCamera();
                        if (path != null) _attachPath(path);
                    });
                }
                menu.AddItem(new GUIContent("Browse files…"), false, Browse);
                menu.AddItem(new GUIContent("Scenario asset…"), false, () => _pickScenario());
            });
        }

        int Room()
        {
            if (Field.Type == FieldType.File) return 1;
            return Field.MaxItems > 0 ? Math.Max(0, Field.MaxItems - _items().Count) : 16;
        }

        void Browse()
        {
            var extensions = kindExtensions.TryGetValue(Field.Kind ?? "", out var list) ? list : "png,jpg,jpeg,webp";
            var path = EditorUtility.OpenFilePanelWithFilters("Choose a reference", Application.dataPath, new[] { Noun(Field.Kind), extensions });
            if (string.IsNullOrEmpty(path)) return;
            var projectRoot = Path.GetFullPath(".").Replace('\\', '/') + "/";
            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith(projectRoot))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(normalized.Substring(projectRoot.Length));
                if (asset != null)
                {
                    _attachObject(asset);
                    return;
                }
            }
            _attachPath(path);
        }

        static readonly Dictionary<string, string> kindExtensions = new Dictionary<string, string>
        {
            { "image", "png,jpg,jpeg,webp" },
            { "video", "mp4,mov,webm" },
            { "audio", "wav,mp3,ogg" },
            { "3d", "glb,fbx,obj" },
        };

        void RegisterDrop(VisualElement zone)
        {
            zone.RegisterCallback<DragEnterEvent>(_ => zone.AddToClassList("sc-drop"));
            zone.RegisterCallback<DragLeaveEvent>(_ => zone.RemoveFromClassList("sc-drop"));
            zone.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                var ok = !Full && (DragAndDrop.objectReferences.Any(o => Accepts(o, Field.Kind)) || DragAndDrop.paths.Any(p => PathKind(p) == Field.Kind));
                DragAndDrop.visualMode = ok ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            });
            zone.RegisterCallback<DragPerformEvent>(_ =>
            {
                zone.RemoveFromClassList("sc-drop");
                DragAndDrop.AcceptDrag();
                var room = Room();
                var objects = DragAndDrop.objectReferences.Where(o => Accepts(o, Field.Kind)).Take(room).ToList();
                foreach (var o in objects) _attachObject(o);
                if (objects.Count == 0)
                    foreach (var p in DragAndDrop.paths.Where(p => PathKind(p) == Field.Kind).Take(room)) _attachPath(p);
            });
            zone.RegisterCallback<DragExitedEvent>(_ => zone.RemoveFromClassList("sc-drop"));
        }

        /// <summary>The input kind an object can feed (image, video, audio, 3d), or "".</summary>
        public static string KindOf(UnityEngine.Object o)
        {
            switch (o)
            {
                case null: return "";
                case Texture _:
                case Sprite _: return "image";
                case AudioClip _: return "audio";
                case VideoClip _: return "video";
                case GameObject _:
                case Mesh _: return "3d";
            }
            return PathKind(AssetDatabase.GetAssetPath(o));
        }

        public static bool Accepts(UnityEngine.Object o, string kind) => !string.IsNullOrEmpty(kind) && KindOf(o) == kind;

        public static string PathKind(string path) => string.IsNullOrEmpty(path) ? "" : Files.UploadTypeFor(path)?.Kind ?? "";

        public static string Noun(string kind)
        {
            switch (kind)
            {
                case "video": return "a video";
                case "audio": return "a sound";
                case "3d": return "a 3D model";
                default: return "an image";
            }
        }

        public static string IconFor(string kind)
        {
            switch (kind)
            {
                case "video": return "video";
                case "audio": return "audio";
                case "3d": return "cube";
                default: return "image";
            }
        }

        static string Safe(string text) => (text ?? "").Replace("/", "∕");
    }
}
