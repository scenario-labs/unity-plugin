using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>
    /// Uploads references to Scenario (free): files, texture assets, scene objects as
    /// GLB. Each file is uploaded once per workspace: a content hash maps to its asset id.
    /// </summary>
    public static class Uploader
    {
        const string DefaultCachePath = "Library/Scenario/uploads.json";
        /// <summary>Content hash and conversion to asset id, per workspace. Tests point it elsewhere.</summary>
        internal static string CachePath = DefaultCachePath;

        internal static void ResetCachePath() => CachePath = DefaultCachePath;
        const string UploadHostSuffix = ".amazonaws.com";

        public static async Task<Result<string>> UploadFile(McpClient client, string path, CancellationToken cancel = default)
        {
            var type = Files.UploadTypeFor(path);
            if (type == null) return Result<string>.Fail(ErrorKind.Local, $"{Path.GetFileName(path)} cannot be used as a reference (use PNG, JPG, WebP, GLB, FBX, OBJ, WAV, MP3, OGG, MP4, MOV or WebM).");
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return Result<string>.Fail(ErrorKind.Local, "Could not read " + path);
            }
            var key = $"{client.TeamId}/{client.ProjectId}/{Hash(bytes)}";
            var cache = ReadCache();
            var cached = Json.Str(cache, key);
            if (cached.StartsWith("asset_", StringComparison.Ordinal)) return Result<string>.Success(cached);

            var start = await client.UploadStart(Path.GetFileName(path), type.Mime, type.Kind, bytes.Length, cancel);
            if (!start.Ok) return start.Cast<string>();
            var partSize = (long)Json.Num(start.Value, "part_size", bytes.Length);
            if (partSize <= 0) partSize = bytes.Length;
            var parts = start.Value["parts"] as JArray ?? new JArray();
            foreach (var part in parts.OfType<JObject>())
            {
                var url = Json.Str(part, "upload_url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.EndsWith(UploadHostSuffix, StringComparison.OrdinalIgnoreCase))
                    return Result<string>.Fail(ErrorKind.Local, "Refused to upload to an unexpected host.");
                var index = (long)Json.Num(part, "part_number", 1) - 1;
                var offset = index * partSize;
                var length = (int)Math.Min(partSize, bytes.Length - offset);
                if (length <= 0) continue;
                var chunk = new byte[length];
                Buffer.BlockCopy(bytes, (int)offset, chunk, 0, length);
                var put = await client.Transport.SendAsync(new HttpRequestSpec { Method = "PUT", Url = url, Body = chunk, TimeoutSeconds = 300 }, cancel);
                if (!put.IsSuccess) return Result<string>.Fail(ErrorKind.Transport, $"Upload failed ({(put.Answered ? "HTTP " + put.Status : put.Error)}).");
            }
            var assetId = Json.Str(start.Value, "asset_id");
            var uploadId = Json.Str(start.Value, "upload_id");
            if (uploadId.Length > 0)
            {
                var done = await client.UploadComplete(uploadId, cancel);
                if (!done.Ok) return done.Cast<string>();
                assetId = Json.Str(done.Value, "asset_id", assetId);
            }
            if (!assetId.StartsWith("asset_", StringComparison.Ordinal)) return Result<string>.Fail(ErrorKind.Protocol, "Scenario did not finish the upload.");
            cache[key] = assetId;
            WriteCache(cache);
            return Result<string>.Success(assetId);
        }

        /// <summary>
        /// A Scenario 3D asset in another format, as a new asset (free): for inputs that
        /// refuse GLB, such as Tencent Texture Edit (FBX only). Converted once per workspace.
        /// </summary>
        public static async Task<Result<string>> Convert(McpClient client, string assetId, string format, CancellationToken cancel = default)
        {
            var key = $"{client.TeamId}/{client.ProjectId}/convert/{assetId}.{format}";
            var cache = ReadCache();
            var cached = Json.Str(cache, key);
            if (cached.StartsWith("asset_", StringComparison.Ordinal)) return Result<string>.Success(cached);
            var url = await client.DownloadUrl(assetId, format, cancel);
            if (!url.Ok) return url.Cast<string>();
            if (!Files.IsScenarioCdn(url.Value)) return Result<string>.Fail(ErrorKind.Local, "Refused to download from an unexpected host.");
            var path = Path.GetFullPath(Path.Combine(Files.Downloads, $"{assetId}.{format}"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var reply = await client.Transport.DownloadAsync(url.Value, path, 600, null, cancel);
            if (!reply.IsSuccess)
                return Result<string>.Fail(ErrorKind.Transport, $"Converting {assetId} to {format.ToUpperInvariant()} failed ({(reply.Answered ? "HTTP " + reply.Status : reply.Error)}).");
            var uploaded = await UploadFile(client, path, cancel);
            if (!uploaded.Ok) return uploaded;
            cache = ReadCache();
            cache[key] = uploaded.Value;
            WriteCache(cache);
            return uploaded;
        }

        /// <summary>A project asset: textures in any format, models, audio, video.</summary>
        public static Task<Result<string>> UploadAsset(McpClient client, UnityEngine.Object asset, CancellationToken cancel = default)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            if (asset is GameObject go && (string.IsNullOrEmpty(path) || Files.UploadTypeFor(path) == null))
                return UploadObject(client, go, cancel);
            if (Files.UploadTypeFor(path) == null && asset is Texture texture)
            {
                var png = Capture.TextureToPng(texture);
                if (png == null) return Task.FromResult(Result<string>.Fail(ErrorKind.Local, "Could not convert the texture."));
                return UploadFile(client, png, cancel);
            }
            if (asset is Sprite sprite) return UploadAsset(client, sprite.texture, cancel);
            return UploadFile(client, Path.GetFullPath(path), cancel);
        }

        /// <summary>A scene object or prefab, exported to GLB with glTFast.</summary>
        public static async Task<Result<string>> UploadObject(McpClient client, GameObject root, CancellationToken cancel = default)
        {
            var path = await ExportGlb(root);
            if (path == null) return Result<string>.Fail(ErrorKind.Local, $"Could not export {root.name} to GLB.");
            return await UploadFile(client, path, cancel);
        }

        public static async Task<string> ExportGlb(GameObject root)
        {
            Directory.CreateDirectory(Capture.Folder);
            var path = Path.GetFullPath(Path.Combine(Capture.Folder, $"{Files.Slug(root.name)}-{DateTime.Now:HHmmss}.glb"));
            var export = new GLTFast.Export.GameObjectExport(
                new GLTFast.Export.ExportSettings { Format = GLTFast.Export.GltfFormat.Binary, FileConflictResolution = GLTFast.Export.FileConflictResolution.Overwrite },
                new GLTFast.Export.GameObjectExportSettings { OnlyActiveInHierarchy = true, DisabledComponents = false });
            export.AddScene(new[] { root }, root.name);
            var ok = await export.SaveToFileAndDispose(path);
            return ok && File.Exists(path) ? path : null;
        }

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        static JObject ReadCache()
        {
            try { return File.Exists(CachePath) ? Json.ParseObject(File.ReadAllText(CachePath)) ?? new JObject() : new JObject(); }
            catch (IOException) { return new JObject(); }
        }

        static void WriteCache(JObject cache)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, cache.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (IOException)
            {
                // The cache only saves re-uploads.
            }
        }
    }
}
