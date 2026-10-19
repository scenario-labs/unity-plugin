using System.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>A job in the results tray: live state, preview, and what to do next.</summary>
    public static class ResultCard
    {
        public static VisualElement Build(JobRow row, ScenarioWindow window)
        {
            var s = ScenarioService.Instance;
            var card = new VisualElement();
            card.AddToClassList("sc-result");
            card.name = "result-" + row.LocalId;
            var thumb = new VisualElement();
            thumb.AddToClassList("sc-result-thumb");
            card.Add(thumb);

            var files = Importer.FromPaths(row.Files);
            var main = files.Count > 0 ? Importer.Main(files) : null;
            var asset = main != null ? AssetDatabase.LoadMainAssetAtPath(main.Path) : null;
            string meta;
            if (row.State == JobState.Success && row.Imported && asset != null)
            {
                Thumbnails.LoadAsset(thumb, asset);
                meta = $"{row.ModelName} · {Ui.Cu(row.ChargedCu ?? row.QuotedCu)}";
            }
            else if (row.IsActive || row.State == JobState.Submitting || (row.State == JobState.Success && !row.Imported))
            {
                thumb.Add(Ui.Spinner());
                var progress = row.Progress > 1 ? row.Progress / 100f : row.Progress;
                if (row.State == JobState.Running && progress > 0)
                {
                    var bar = new VisualElement();
                    bar.AddToClassList("sc-progress");
                    var fill = new VisualElement();
                    fill.AddToClassList("sc-progress-fill");
                    fill.style.width = Length.Percent(Mathf.Clamp01(progress) * 100);
                    bar.Add(fill);
                    thumb.Add(bar);
                }
                meta = row.State == JobState.Success ? "Importing…"
                    : row.State == JobState.Submitting ? "Sending…"
                    : row.State == JobState.CancelRequested ? "Canceling…"
                    : row.State == JobState.Queued ? "Queued" : progress > 0 ? $"Running · {Mathf.RoundToInt(progress * 100)}%" : "Running";
            }
            else
            {
                var (icon, text) = StateLook(row);
                thumb.Add(Ui.Icon(icon, "sc-icon-lg"));
                meta = text;
                thumb.tooltip = row.Error;
            }

            var title = Ui.Text(Files.ObjectName(row.Prompt, row.ModelName), "sc-result-title", "sc-ellipsis");
            title.tooltip = row.Prompt;
            card.Add(title);
            card.Add(Ui.Text(meta, "sc-result-meta", "sc-ellipsis"));

            thumb.RegisterCallback<ClickEvent>(_ =>
            {
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
                else window.Show("jobs");
            });

            var actions = new VisualElement();
            actions.AddToClassList("sc-result-actions");
            thumb.Add(actions);
            if (asset != null && main != null)
            {
                var item = new SlotItem { AssetId = main.AssetId, Source = asset, Kind = main.Kind, Label = asset.name };
                actions.Add(Ui.IconButton("pin", "Place in the scene", () =>
                {
                    var placed = Placer.Place(row.OutputType, files, row.Prompt, PlaceTarget(row.OutputType), create: true);
                    if (placed.Message.Length > 0) window.Toast(placed.Message, placed.MessageKind);
                }));
                if (OutputTypes.StepsFor(main.Kind).Any())
                {
                    var next = Ui.IconButton("arrow-right", "Next: edit, upscale, make 3D, animate…", null);
                    next.clicked += () => Ui.Menu(next, menu => CreateView.StepsMenu(menu, main.Kind, step => window.Create.StartStep(step, Copy(item))));
                    actions.Add(next);
                }
            }
            var more = Ui.IconButton("more", "More", null);
            more.clicked += () => Ui.Menu(more, menu =>
            {
                if (asset != null)
                {
                    menu.AddItem(new GUIContent("Show in Project"), false, () => EditorGUIUtility.PingObject(asset));
                    menu.AddItem(new GUIContent("Use as input"), false, () =>
                    {
                        window.Show("create");
                        window.Create.AttachExisting(new SlotItem { AssetId = main.AssetId, Source = asset, Kind = main.Kind, Label = asset.name });
                    });
                }
                menu.AddItem(new GUIContent("Use settings"), false, () => window.Create.UseSettings(row));
                var firstAsset = row.AssetIds.FirstOrDefault();
                if (!string.IsNullOrEmpty(firstAsset))
                {
                    menu.AddItem(new GUIContent("Open on Scenario"), false, () => Application.OpenURL(ScenarioService.Instance.AssetLink(firstAsset, row)));
                    menu.AddItem(new GUIContent("Copy asset id"), false, () => EditorGUIUtility.systemCopyBuffer = firstAsset);
                }
                if (row.State == JobState.Success && !row.Imported)
                    menu.AddItem(new GUIContent("Import again"), false, () => _ = s.ImportRow(row));
                if (row.IsActive)
                {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Cancel"), false, () => _ = s.Cancel(row));
                }
                if (row.IsUnresolved)
                {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Look for it on Scenario"), false, () => _ = s.CheckUnknown());
                    menu.AddItem(new GUIContent("Dismiss"), false, () => s.Dismiss(row));
                }
            });
            actions.Add(more);
            return card;
        }

        static SlotItem Copy(SlotItem item) => new SlotItem { AssetId = item.AssetId, Source = item.Source, Kind = item.Kind, Label = item.Label };

        static GameObject PlaceTarget(string outputType)
        {
            var go = Selection.activeGameObject;
            if (go == null || EditorUtility.IsPersistent(go)) return null;
            return Placer.Describe(outputType, go).Length > 0 ? go : null;
        }

        public static (string icon, string text) StateLook(JobRow row)
        {
            switch (row.State)
            {
                case JobState.Failed: return ("error", "Failed");
                case JobState.Blocked: return ("warning", "Blocked by moderation");
                case JobState.Canceled: return ("stop", "Canceled");
                case JobState.Unknown: return ("warning", "Unknown: check Jobs");
                case JobState.Rejected: return ("error", "Not sent");
                case JobState.Success: return ("warning", "Files missing");
                default: return ("info", row.State);
            }
        }
    }
}
