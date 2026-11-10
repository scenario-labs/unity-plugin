using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>
    /// Where an imported file came from, stored in its importer's userData (so it
    /// travels with the .meta file) plus the "Scenario" asset label for search.
    /// </summary>
    public sealed class ProvenanceInfo
    {
        public string AssetId = "";
        public string JobId = "";
        public string ModelId = "";
        public string ModelName = "";
        public string Prompt = "";
        public string OutputType = "";
        public string Role = "";
        public JObject Parameters = new JObject();
        public string CreatedAt = "";
        public string TeamId = "";
        public string ProjectId = "";
        /// <summary>Creative Units of the whole job (charged when known, else quoted).</summary>
        public double Cu;

        public string WebUrl => Links.Asset(AssetId, TeamId, ProjectId);
    }

    public static class Provenance
    {
        public const string Label = "Scenario";
        const string Key = "scenario";

        public static void Write(string assetPath, ProvenanceInfo info)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null) return;
            var data = Json.ParseObject(importer.userData) ?? new JObject();
            data[Key] = JObject.FromObject(info);
            importer.userData = data.ToString(Formatting.None);
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset != null)
            {
                var labels = AssetDatabase.GetLabels(asset).ToList();
                if (!labels.Contains(Label))
                {
                    labels.Add(Label);
                    AssetDatabase.SetLabels(asset, labels.ToArray());
                }
            }
            importer.SaveAndReimport();
        }

        public static ProvenanceInfo Read(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null || string.IsNullOrEmpty(importer.userData) || !importer.userData.Contains("\"" + Key + "\"")) return null;
            var data = Json.ParseObject(importer.userData);
            return (data?[Key] as JObject)?.ToObject<ProvenanceInfo>();
        }

        public static ProvenanceInfo Read(Object asset) => asset == null ? null : Read(AssetDatabase.GetAssetPath(asset));

        /// <summary>Every Scenario result in the project, newest first.</summary>
        public static List<string> AllPaths()
        {
            return AssetDatabase.FindAssets("l:" + Label)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderByDescending(p => System.IO.File.GetLastWriteTimeUtc(p))
                .ToList();
        }
    }
}
