using System.Collections.Generic;
using System.Linq;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>
    /// Adds a Scenario strip to the Inspector of any asset made with Scenario:
    /// model, prompt, cost and date, with Use settings, Next and Open on Scenario.
    /// </summary>
    [InitializeOnLoad]
    static class InspectorHeader
    {
        static readonly Dictionary<string, (string hash, ProvenanceInfo info)> Cache = new Dictionary<string, (string, ProvenanceInfo)>();
        static GUIStyle _prompt;

        static InspectorHeader()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI += Draw;
        }

        static ProvenanceInfo InfoFor(string path)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return null;
            var hash = importer.userData ?? "";
            if (Cache.TryGetValue(path, out var cached) && cached.hash == hash) return cached.info;
            var info = hash.Contains("\"scenario\"") ? Provenance.Read(path) : null;
            Cache[path] = (hash, info);
            return info;
        }

        static void Draw(UnityEditor.Editor editor)
        {
            if (editor.targets.Length != 1) return;
            var path = AssetDatabase.GetAssetPath(editor.target);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) return;
            var info = InfoFor(path);
            if (info == null) return;
            if (_prompt == null) _prompt = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = false };

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var title = "Scenario · " + (info.ModelName.Length > 0 ? info.ModelName : info.ModelId);
                var meta = new List<string>();
                if (info.Cu > 0) meta.Add(Ui.Cu(info.Cu));
                if (System.DateTime.TryParse(info.CreatedAt, out var created)) meta.Add(created.ToLocalTime().ToString("d MMM yyyy, HH:mm"));
                if (info.Role.Length > 0 && info.Role != "main") meta.Add(info.Role);
                // Two lines: a long model name in the label column would run under the cost.
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                if (meta.Count > 0) EditorGUILayout.LabelField(string.Join(" · ", meta), EditorStyles.miniLabel);
                if (info.Prompt.Length > 0 && !info.Prompt.StartsWith("asset_")) EditorGUILayout.LabelField(info.Prompt, _prompt);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (info.ModelId.Length > 0 && GUILayout.Button("Use settings", EditorStyles.miniButtonLeft))
                    {
                        var window = ScenarioWindow.OpenView("create");
                        window.Create.UseSettings(new JobRow
                        {
                            ModelId = info.ModelId, ModelName = info.ModelName, OutputType = info.OutputType, Parameters = info.Parameters ?? new Newtonsoft.Json.Linq.JObject(),
                            Prompt = info.Prompt,
                        });
                    }
                    var kind = Importer.KindOfPath(path);
                    if (OutputTypes.StepsFor(kind).Any() && GUILayout.Button("Next ▾", EditorStyles.miniButtonMid))
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(path);
                        var menu = new GenericMenu();
                        CreateView.StepsMenu(menu, kind, step => ScenarioWindow.OpenView("create").Create.StartStep(step,
                            new SlotItem { AssetId = info.AssetId, Source = asset, Kind = kind, Label = asset != null ? asset.name : info.AssetId }));
                        menu.ShowAsContext();
                    }
                    if (info.WebUrl.Length > 0 && GUILayout.Button("Open on Scenario", EditorStyles.miniButtonRight)) Application.OpenURL(info.WebUrl);
                }
            }
        }
    }
}
