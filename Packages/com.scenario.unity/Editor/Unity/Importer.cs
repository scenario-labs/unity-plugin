using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    public sealed class ImportedFile
    {
        public string Path = "";
        public string AssetId = "";
        public string Role = "main";
        public string Kind = "";
        /// <summary>Bones of a rigged model, as Scenario measured them; 0 otherwise.</summary>
        public int Bones;
        /// <summary>The asset this one was made from (a rig's walk clip points to the rigged model).</summary>
        public string ParentId = "";
    }

    /// <summary>What a set of Scenario assets is, so it lands in the right folder with its provenance.</summary>
    public sealed class ImportContext
    {
        public string OutputType = OutputTypes.Image;
        public string Prompt = "";
        public string ModelId = "";
        public string ModelName = "";
        public string JobId = "";
        public JObject Parameters = new JObject();
        /// <summary>Overrides the dated type folder.</summary>
        public string Folder = "";
        public double Cu;
    }

    /// <summary>
    /// Downloads Scenario assets into the project and configures their import.
    /// Must be started on the main thread: AssetDatabase calls happen after each await.
    /// </summary>
    public static class Importer
    {
        static readonly HashSet<string> UnityImageFormats = new HashSet<string> { "png", "jpg", "jpeg", "tga", "psd", "exr", "hdr", "tif", "tiff", "bmp", "gif" };

        public static async Task<Result<List<ImportedFile>>> Import(McpClient client, IList<string> assetIds, ImportContext context, CancellationToken cancel = default)
        {
            var settings = ScenarioProjectSettings.instance;
            var typeFolder = OutputTypes.Get(context.OutputType).Folder;
            var folder = string.IsNullOrEmpty(context.Folder)
                ? Files.OutputFolder(settings.OutputRoot, typeFolder, DateTime.Now, settings.DatedFolders)
                : context.Folder;
            var assets = new List<(string id, JObject asset)>();
            foreach (var assetId in assetIds)
            {
                var asset = await client.GetAsset(assetId, cancel);
                if (!asset.Ok) return asset.Cast<List<ImportedFile>>();
                assets.Add((assetId, asset.Value));
            }
            // A 3D result is one model plus copies of it (OBJ, MTL, PBR maps): keep the model.
            var hasModel = assets.Any(a => Files.KindOf(a.asset) == "3d" && !Files.IsModelCompanion(a.asset) && !Files.IsSplat(a.asset));
            var hasTexturedRig = assets.Any(a => Files.BoneCount(a.asset) > 0 && !Files.IsSkeletonOnly(a.asset));
            var downloads = new List<(string temp, string ext, JObject asset, string assetId)>();
            foreach (var (assetId, asset) in assets)
            {
                if (hasModel && Files.IsModelCompanion(asset)) continue;
                if (hasTexturedRig && Files.IsSkeletonOnly(asset)) continue;
                var kind = Files.KindOf(asset);
                var mime = Json.Str(asset, "mimeType");
                var model = kind == "3d" && !Files.IsSplat(asset);
                var format = "";
                // Any model as GLB: Scenario converts FBX, Blender, VOX and OBJ results, animations and skins included.
                // A GLB comes as stored: converting a large one on the fly can time out.
                if (model && mime != "model/gltf-binary") format = "glb";
                else if (kind == "image" && !UnityImageFormats.Contains(Files.ExtensionForMime(mime))) format = "png";
                var temp = System.IO.Path.GetFullPath(System.IO.Path.Combine(Files.Downloads, assetId + ".download"));
                var fetched = await Fetch(client, assetId, format, temp, cancel);
                if (!fetched.Ok) return fetched.Cast<List<ImportedFile>>();
                var ext = Files.SniffFile(temp);
                // Some results are labelled GLB but stored in another format (a Blender file for 3D edits).
                if (model && format.Length == 0 && ext != "glb")
                {
                    fetched = await Fetch(client, assetId, "glb", temp, cancel);
                    if (!fetched.Ok) return fetched.Cast<List<ImportedFile>>();
                    ext = Files.SniffFile(temp);
                }
                if (ext.Length == 0) ext = Files.ExtensionForMime(mime);
                if (ext.Length == 0) ext = kind == "3d" ? "glb" : "bin";
                downloads.Add((temp, ext, asset, assetId));
            }

            Directory.CreateDirectory(folder);
            var imported = new List<ImportedFile>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (temp, ext, asset, assetId) in downloads)
                {
                    var role = Files.RoleFor(asset);
                    var path = AssetDatabase.GenerateUniqueAssetPath(Files.BasePath(folder, context.Prompt, assetId, role) + "." + ext);
                    GltfImportFix.Incoming.Add(path);
                    File.Move(temp, path);
                    imported.Add(new ImportedFile { Path = path, AssetId = assetId, Role = role, Kind = Files.IsSplat(asset) ? "splat" : Files.KindOf(asset), Bones = Files.BoneCount(asset),
                        ParentId = Json.Str(asset["metadata"], "parentId") });
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            foreach (var file in imported)
            {
                AssetDatabase.ImportAsset(file.Path, ImportAssetOptions.ForceSynchronousImport);
                Configure(file, context.OutputType, settings);
                Provenance.Write(file.Path, new ProvenanceInfo
                {
                    AssetId = file.AssetId,
                    JobId = context.JobId,
                    ModelId = context.ModelId,
                    ModelName = context.ModelName,
                    Prompt = context.Prompt,
                    OutputType = context.OutputType,
                    Role = file.Role,
                    Parameters = context.Parameters ?? new JObject(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    TeamId = client.TeamId,
                    ProjectId = client.ProjectId,
                    Cu = context.Cu,
                });
                GltfImportFix.Incoming.Remove(file.Path);
            }
            // One controller on the model that gets placed, with the clips of every file
            // on the same skeleton (rigging that also returns walk and run, for example).
            var main = MainModel(imported);
            if (main != null)
                Animations.Ensure(main.Path, imported.Where(f => f.Kind == "3d" && f != main && main.Bones > 0 && f.Bones == main.Bones).Select(f => f.Path));
            return Result<List<ImportedFile>>.Success(imported);
        }

        static async Task<Result<bool>> Fetch(McpClient client, string assetId, string format, string temp, CancellationToken cancel)
        {
            var url = await client.DownloadUrl(assetId, format, cancel);
            if (!url.Ok) return url.Cast<bool>();
            if (!Files.IsScenarioCdn(url.Value))
                return Result<bool>.Fail(ErrorKind.Local, "Refused to download from an unexpected host.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temp));
            var reply = await client.Transport.DownloadAsync(url.Value, temp, 600, null, cancel);
            if (!reply.IsSuccess)
                return Result<bool>.Fail(reply.Error == "canceled" ? ErrorKind.Canceled : ErrorKind.Transport,
                    $"Downloading {assetId} failed ({(reply.Answered ? "HTTP " + reply.Status : reply.Error)}).");
            return Result<bool>.Success(true);
        }

        static void Configure(ImportedFile file, string outputType, ScenarioProjectSettings settings)
        {
            if (!(AssetImporter.GetAtPath(file.Path) is TextureImporter texture)) return;
            var changed = false;
            switch (file.Role)
            {
                case "normal":
                    texture.textureType = TextureImporterType.NormalMap;
                    changed = true;
                    break;
                case "roughness":
                case "metallic":
                case "ao":
                case "height":
                    texture.sRGBTexture = false;
                    changed = true;
                    break;
            }
            if (outputType == OutputTypes.Image && (settings.ImagesAsSprites || EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D))
            {
                texture.textureType = TextureImporterType.Sprite;
                texture.spriteImportMode = SpriteImportMode.Single;
                texture.alphaIsTransparency = true;
                texture.spritePixelsPerUnit = Mathf.Max(1, settings.PixelsPerUnit);
                changed = true;
            }
            if (outputType == OutputTypes.Image && texture.filterMode != settings.ImageFilter)
            {
                texture.filterMode = settings.ImageFilter;
                changed = true;
            }
            if (texture.maxTextureSize < 4096)
            {
                texture.maxTextureSize = 4096;
                changed = true;
            }
            if (changed) texture.SaveAndReimport();
        }

        /// <summary>Imported files of a finished job, rebuilt from their paths and provenance.</summary>
        public static List<ImportedFile> FromPaths(IEnumerable<string> paths)
        {
            var list = new List<ImportedFile>();
            foreach (var path in paths ?? new string[0])
            {
                if (string.IsNullOrEmpty(path) || AssetDatabase.LoadMainAssetAtPath(path) == null) continue;
                var info = Provenance.Read(path);
                list.Add(new ImportedFile
                {
                    Path = path,
                    AssetId = info?.AssetId ?? "",
                    Role = string.IsNullOrEmpty(info?.Role) ? "main" : info.Role,
                    Kind = KindOfPath(path),
                });
            }
            return list;
        }

        public static string KindOfPath(string path)
        {
            var ext = System.IO.Path.GetExtension(path ?? "").TrimStart('.').ToLowerInvariant();
            if (ext == "glb" || ext == "gltf" || ext == "fbx" || ext == "obj" || ext == "prefab") return "3d";
            if (ext == "spz" || ext == "ply") return "splat";
            return Files.UploadTypeFor(path)?.Kind ?? "image";
        }

        /// <summary>The main file of a result set: the panorama, the albedo, the model, or the first file.</summary>
        /// <summary>The model the others were made from (a rig's walk and run point to the rigged mesh), else the first model.</summary>
        public static ImportedFile MainModel(List<ImportedFile> files)
        {
            var models = files.Where(f => f.Kind == "3d").ToList();
            return models.FirstOrDefault(m => models.Any(o => o.ParentId == m.AssetId)) ?? models.FirstOrDefault();
        }

        public static ImportedFile Main(List<ImportedFile> files)
        {
            return files.FirstOrDefault(f => f.Role == "panorama")
                ?? MainModel(files)
                ?? files.FirstOrDefault(f => f.Role == "main")
                ?? files.FirstOrDefault(f => f.Role == "albedo")
                ?? files.FirstOrDefault();
        }
    }

    /// <summary>
    /// Two glTFast import fixes. glTFast makes one fallback material for meshes without one (Polygen,
    /// Hitem3D Multicolor) and caches it in a static; the import that stores it destroys it, so every
    /// later glTF import gets none and draws magenta. Clearing the flag before each glTF import gives
    /// every file its own, vertex colors included; placement still sets the pipeline's default material.
    /// Scenario files also always get a scene root: glTFast otherwise merges a file's single root node
    /// into the prefab root, so a Z-up node's rotation lands on the root, where previews ignore it (the
    /// model shows lying down), and a file with clips keeps the node as a child while one without merges
    /// it, so the rig files of one character do not share a hierarchy.
    /// </summary>
    sealed class GltfImportFix : AssetPostprocessor
    {
        // The base generator (URP, HDRP) and the Built-in one each keep their own flag.
        static readonly FieldInfo[] Made = typeof(GLTFast.Materials.MaterialGenerator).Assembly.GetTypes()
            .Where(t => typeof(GLTFast.Materials.IMaterialGenerator).IsAssignableFrom(t))
            .Select(t => t.GetField("s_DefaultMaterialGenerated", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(f => f != null && f.FieldType == typeof(bool)).ToArray();

        void OnPreprocessAsset()
        {
            if (!assetPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && !assetPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var flag in Made) flag.SetValue(null, false);
            if (!IsScenarioFile()) return;
            var field = assetImporter.GetType().GetField("instantiationSettings", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(GLTFast.InstantiationSettings)) return;
            var settings = field.GetValue(assetImporter) as GLTFast.InstantiationSettings ?? new GLTFast.InstantiationSettings();
            settings.SceneObjectCreation = GLTFast.SceneObjectCreation.Always;
            field.SetValue(assetImporter, settings);
        }

        /// <summary>Files the importer is bringing in (no provenance yet), set when they are written.</summary>
        internal static readonly HashSet<string> Incoming = new HashSet<string>();

        bool IsScenarioFile() => Incoming.Contains(assetPath) || (assetImporter.userData ?? "").Contains("\"scenario\"");
    }
}
