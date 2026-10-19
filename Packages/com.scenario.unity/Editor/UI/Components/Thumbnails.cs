using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// Thumbnails from Scenario's CDN, cached on disk (Library/Scenario/thumbs) by
    /// asset id and in memory for the session. Only Scenario CDN URLs are fetched.
    /// Full-size images are shrunk to MaxSize once, so the cache and memory hold
    /// small textures (40 tiles of 2K images would otherwise take about 600 MB).
    /// </summary>
    public static class Thumbnails
    {
        const string Folder = "Library/Scenario/thumbs";
        const int MaxSize = 256;
        static readonly Dictionary<string, Texture2D> Memory = new Dictionary<string, Texture2D>();
        static readonly Dictionary<string, Task<Texture2D>> Pending = new Dictionary<string, Task<Texture2D>>();
        static readonly HttpTransport Transport = new HttpTransport();

        /// <summary>Sets the element's background to the thumbnail when it arrives.</summary>
        public static void Load(VisualElement target, string key, string url)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (Memory.TryGetValue(key, out var cached) && cached != null)
            {
                target.style.backgroundImage = new StyleBackground(cached);
                return;
            }
            _ = Apply(target, key, url);
        }

        static async Task Apply(VisualElement target, string key, string url)
        {
            // A thumbnail cached on disk arrives before the new tile is in the window: set it anyway.
            var texture = await Get(key, url);
            if (texture != null) target.style.backgroundImage = new StyleBackground(texture);
        }

        public static Task<Texture2D> Get(string key, string url)
        {
            if (Memory.TryGetValue(key, out var cached) && cached != null) return Task.FromResult(cached);
            if (Pending.TryGetValue(key, out var pending)) return pending;
            var task = Fetch(key, url);
            Pending[key] = task;
            return task;
        }

        static async Task<Texture2D> Fetch(string key, string url)
        {
            try
            {
                var path = Path.Combine(Folder, Files.Slug(key, 80) + ".img");
                if (!File.Exists(path))
                {
                    if (!Files.IsScenarioCdn(url)) return null;
                    Directory.CreateDirectory(Folder);
                    var reply = await Transport.DownloadAsync(url, Path.GetFullPath(path), 60);
                    if (!reply.IsSuccess) return null;
                }
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave };
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    return null;
                }
                if (Mathf.Max(texture.width, texture.height) > MaxSize && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var small = Shrink(texture, MaxSize);
                    UnityEngine.Object.DestroyImmediate(texture);
                    texture = small;
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                }
                texture.filterMode = FilterMode.Bilinear;
                Memory[key] = texture;
                return texture;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                Pending.Remove(key);
            }
        }

        /// <summary>A copy at most max pixels on its long side, filtered through the mip chain.</summary>
        static Texture2D Shrink(Texture2D source, int max)
        {
            var scale = (float)max / Mathf.Max(source.width, source.height);
            var w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            var h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            source.filterMode = FilterMode.Trilinear;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var small = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                small.Apply();
                return small;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static bool _previewCache;

        /// <summary>
        /// Preview of an imported Unity asset (AssetPreview), polled until ready, for up to a minute: after
        /// a large import Unity renders previews one by one. The preview cache is raised so the window's
        /// cards are not evicted by the Project window's tiles.
        /// </summary>
        public static void LoadAsset(VisualElement target, UnityEngine.Object asset)
        {
            if (asset == null) return;
            if (!_previewCache)
            {
                AssetPreview.SetPreviewTextureCacheSize(256);
                _previewCache = true;
            }
            var preview = AssetPreview.GetAssetPreview(asset);
            if (preview != null)
            {
                target.style.backgroundImage = new StyleBackground(preview);
                return;
            }
            var mini = AssetPreview.GetMiniThumbnail(asset);
            if (mini != null) target.style.backgroundImage = new StyleBackground(mini);
            var tries = 0;
            target.schedule.Execute(() =>
            {
                var ready = AssetPreview.GetAssetPreview(asset);
                if (ready != null) target.style.backgroundImage = new StyleBackground(ready);
                tries++;
            }).Every(250).Until(() => tries > 240 || asset == null || AssetPreview.GetAssetPreview(asset) != null && tries > 0);
        }
    }
}
