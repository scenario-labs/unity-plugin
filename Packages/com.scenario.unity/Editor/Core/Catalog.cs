using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>One model from models_list, with the flags the browser needs.</summary>
    public sealed class ModelInfo
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public string Type = "";
        public string Privacy = "public";
        public string Provider = "";
        public List<string> Capabilities = new List<string>();
        public List<string> Tags = new List<string>();
        public double CreatedAt;
        public double UpdatedAt;
        public string ThumbnailUrl = "";

        [JsonIgnore] public bool IsPrivate => Privacy == "private";
        [JsonIgnore] public bool IsLegacy => Tags.Contains("sc:legacy") || Tags.Contains("Deprecated");
        [JsonIgnore] public bool IsFeatured => Tags.Contains("sc:featured");
        [JsonIgnore] public bool IsLora => Type.EndsWith("-lora", StringComparison.Ordinal);
        /// <summary>The model Scenario recommends instead of a legacy one, or "".</summary>
        [JsonIgnore] public string ReplacementId => Tags.FirstOrDefault(t => t.StartsWith("deprecated:model_", StringComparison.Ordinal))?.Substring(11) ?? "";
        /// <summary>Capabilities in words: "Text to image · Image to image".</summary>
        [JsonIgnore] public string CapabilityText => string.Join(" · ", Capabilities.Select(Catalog.CapabilityLabel).Distinct());

        public static ModelInfo FromJson(JObject model)
        {
            if (model == null) return null;
            var info = new ModelInfo
            {
                Id = Json.Str(model, "id"),
                Name = Json.Str(model, "name"),
                Description = Json.Str(model, "shortDescription"),
                Type = Json.Str(model, "type"),
                Privacy = Json.Str(model, "privacy", "public"),
                Provider = Json.Str(model["complianceMetadata"], "modelProvider"),
                CreatedAt = Json.IsoToUnix(Json.Str(model, "createdAt")),
                UpdatedAt = Json.IsoToUnix(Json.Str(model, "updatedAt")),
            };
            if (info.Name.Length == 0) info.Name = info.Id;
            if (model["capabilities"] is JArray caps) info.Capabilities = caps.Select(c => c.ToString()).ToList();
            if (model["tags"] is JArray tags) info.Tags = tags.Select(t => t.ToString()).ToList();
            var thumb = model["thumbnail"];
            if (thumb is JObject thumbObject) info.ThumbnailUrl = Json.Str(thumbObject, "url");
            else if (thumb != null && thumb.Type == JTokenType.String) info.ThumbnailUrl = (string)thumb;
            return info;
        }
    }

    /// <summary>
    /// The model catalog: every public trained model plus the team's own, cached
    /// for a day in Library/Scenario/catalog.json. The cache is a convenience:
    /// a miss or a stale file never blocks generating with a known model id.
    /// </summary>
    public sealed class Catalog
    {
        public const double MaxAgeSeconds = 24 * 3600;
        const int PageSize = 100;
        const int MaxPublicPages = 20;
        const int MaxPrivatePages = 5;

        public readonly string Path;
        public List<ModelInfo> Models = new List<ModelInfo>();
        public double FetchedAt;
        /// <summary>The workspace the private models came from.</summary>
        public string Scope = "";

        public Catalog(string path)
        {
            Path = path ?? "";
        }

        public bool IsEmpty => Models.Count == 0;
        public bool IsStale(double now) => IsEmpty || now - FetchedAt > MaxAgeSeconds;

        public ModelInfo Find(string id) => string.IsNullOrEmpty(id) ? null : Models.FirstOrDefault(m => m.Id == id);

        public void Load()
        {
            Models = new List<ModelInfo>();
            if (Path.Length == 0 || !File.Exists(Path)) return;
            try
            {
                var data = Json.ParseObject(File.ReadAllText(Path));
                if (data == null) return;
                FetchedAt = Json.Num(data, "fetchedAt");
                Scope = Json.Str(data, "scope");
                Models = data["models"]?.ToObject<List<ModelInfo>>() ?? new List<ModelInfo>();
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                Models = new List<ModelInfo>();
            }
        }

        public void Save()
        {
            if (Path.Length == 0) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");
                var data = new JObject { ["fetchedAt"] = FetchedAt, ["scope"] = Scope, ["models"] = JArray.FromObject(Models) };
                File.WriteAllText(Path + ".tmp", data.ToString(Formatting.None));
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(Path + ".tmp", Path);
            }
            catch (IOException)
            {
                // The cache is optional.
            }
        }

        /// <summary>Fetches public and private trained models. Keeps the old list when the public fetch fails.</summary>
        public async Task<Result<int>> Refresh(McpClient client, string scope, double now, CancellationToken cancel = default)
        {
            var publicModels = await FetchAll(client, "public", MaxPublicPages, cancel);
            if (!publicModels.Ok) return publicModels.Cast<int>();
            // Private models are a bonus: a failure (no workspace yet) leaves them out.
            var privateModels = await FetchAll(client, "private", MaxPrivatePages, cancel);
            var merged = new Dictionary<string, ModelInfo>();
            foreach (var model in publicModels.Value) merged[model.Id] = model;
            if (privateModels.Ok)
                foreach (var model in privateModels.Value) merged[model.Id] = model;
            Models = merged.Values.ToList();
            FetchedAt = now;
            Scope = scope ?? "";
            Save();
            return Result<int>.Success(Models.Count);
        }

        static async Task<Result<List<ModelInfo>>> FetchAll(McpClient client, string privacy, int maxPages, CancellationToken cancel)
        {
            var models = new List<ModelInfo>();
            var token = "";
            for (var page = 0; page < maxPages; page++)
            {
                var filters = new JObject { ["privacy"] = privacy, ["status"] = "trained" };
                var result = await client.ListModels(filters, PageSize, token, cancel);
                if (!result.Ok) return page == 0 ? result.Cast<List<ModelInfo>>() : Result<List<ModelInfo>>.Success(models);
                if (result.Value["models"] is JArray rows)
                    foreach (var row in rows.OfType<JObject>())
                    {
                        var info = ModelInfo.FromJson(row);
                        if (info != null && info.Id.Length > 0) models.Add(info);
                    }
                token = Json.Str(result.Value, "nextPaginationToken");
                if (token.Length == 0) break;
            }
            return Result<List<ModelInfo>>.Success(models);
        }

        public sealed class Query
        {
            public string Text = "";
            /// <summary>Output type id; "all" or "" for every model.</summary>
            public string OutputType = "";
            public bool ShowLegacy;
            public bool TeamOnly;
            public bool FavoritesOnly;
            public ICollection<string> Favorites;
            /// <summary>Only models with this capability ("img2img"), for typed steps.</summary>
            public string Capability = "";
        }

        /// <summary>Models matching the query, best first.</summary>
        public List<ModelInfo> Search(Query query)
        {
            var type = string.IsNullOrEmpty(query.OutputType) || query.OutputType == OutputTypes.All ? null : OutputTypes.Get(query.OutputType);
            var words = (query.Text ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var favorites = query.Favorites ?? new string[0];
            var scored = new List<(ModelInfo model, double score)>();
            foreach (var model in Models)
            {
                if (!query.ShowLegacy && model.IsLegacy) continue;
                if (query.TeamOnly && !model.IsPrivate) continue;
                if (query.FavoritesOnly && !favorites.Contains(model.Id)) continue;
                if (type != null && !type.Accepts(model)) continue;
                if (!string.IsNullOrEmpty(query.Capability) && !model.Capabilities.Contains(query.Capability)) continue;
                var score = Score(model, words);
                if (score <= 0) continue;
                if (favorites.Contains(model.Id)) score += 3;
                if (model.IsFeatured) score += 2;
                if (type != null && Array.IndexOf(type.QuickPicks, model.Id) >= 0) score += 4;
                if (model.IsLegacy) score -= 3;
                scored.Add((model, score));
            }
            return scored
                .OrderByDescending(s => s.score)
                .ThenByDescending(s => s.model.CreatedAt)
                .Select(s => s.model)
                .ToList();
        }

        /// <summary>0 means no match. Every query word must appear somewhere.</summary>
        static double Score(ModelInfo model, string[] words)
        {
            if (words.Length == 0) return 1;
            var name = model.Name.ToLowerInvariant();
            var id = model.Id.ToLowerInvariant();
            var provider = model.Provider.ToLowerInvariant();
            var rest = (model.Description + " " + string.Join(" ", model.Tags) + " " + model.CapabilityText).ToLowerInvariant();
            double total = 1;
            foreach (var word in words)
            {
                if (name.StartsWith(word)) total += 10;
                else if (name.Contains(" " + word)) total += 8;
                else if (name.Contains(word)) total += 6;
                else if (id.Contains(word)) total += 5;
                else if (provider.Contains(word)) total += 4;
                else if (rest.Contains(word)) total += 1;
                else return 0;
            }
            return total;
        }

        public static string CapabilityLabel(string capability)
        {
            switch (capability)
            {
                case "txt2img": return "Text to image";
                case "img2img": return "Image to image";
                case "txt2video": return "Text to video";
                case "img2video": return "Image to video";
                case "video2video": return "Video to video";
                case "txt23d": return "Text to 3D";
                case "img23d": return "Image to 3D";
                case "3d23d": return "3D to 3D";
                case "video23d": return "Video to motion";
                case "txt2audio": return "Text to audio";
                case "audio2audio": return "Audio to audio";
                case "video2audio": return "Video to audio";
                case "video2img": return "Video to image";
                case "txt2txt": return "Text";
                case "img2txt": return "Image to text";
                default: return SchemaForm.Humanize(capability);
            }
        }
    }
}
