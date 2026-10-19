using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>
    /// Meshy's animation library: the motions Meshy Animation applies by number.
    /// The list ships with the package (Editor/Data/meshy-actions.json).
    /// </summary>
    public static class MotionLibrary
    {
        public sealed class Motion
        {
            public int Id;
            public string Name;
            public string Category;
            public string Group;
        }

        public const string DataPath = "Packages/com.scenario.unity/Editor/Data/meshy-actions.json";

        static List<Motion> _all;

        /// <summary>Loader for the shipped JSON; tests replace it.</summary>
        public static Func<string> Load = () => UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(DataPath)?.text ?? "";

        public static IReadOnlyList<Motion> All => _all ?? (_all = Parse(Load()));

        public static void Reset() => _all = null;

        public static List<Motion> Parse(string json)
        {
            var list = new List<Motion>();
            var root = Json.ParseObject(json);
            if (!(root?["actions"] is JArray rows)) return list;
            foreach (var row in rows.OfType<JArray>())
            {
                if (row.Count < 4) continue;
                list.Add(new Motion { Id = (int)Json.ToDouble(row[0]), Name = (string)row[1] ?? "", Category = (string)row[2] ?? "", Group = (string)row[3] ?? "" });
            }
            return list;
        }

        public static IEnumerable<string> Categories => All.Select(m => m.Category).Distinct();

        public static Motion Find(int id) => All.FirstOrDefault(m => m.Id == id);

        /// <summary>Motions whose name or group holds every word of the query, in a category ("" for all).</summary>
        public static List<Motion> Search(string query, string category = "")
        {
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return All.Where(m => (category.Length == 0 || m.Category == category)
                                  && words.All(w => m.Name.ToLowerInvariant().Contains(w) || m.Group.ToLowerInvariant().Contains(w) || m.Id.ToString() == w))
                .ToList();
        }

        /// <summary>A number field that takes a Meshy library id.</summary>
        public static bool IsActionField(Field field) =>
            field != null && (field.Type == FieldType.Integer || field.Type == FieldType.Number) && field.Name == "actionId"
            && (field.Description ?? "").IndexOf("animation", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
