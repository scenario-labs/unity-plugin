using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
#if SCENARIO_URP
using UnityEngine.Rendering.Universal;
#endif

namespace Scenario.Editor
{
    /// <summary>Renders a camera (Scene view, Main Camera) to a PNG to use as a reference.</summary>
    public static class Capture
    {
        public const string Folder = "Library/Scenario/captures";

        public static string SceneView(int maxSize = 1536)
        {
            var view = UnityEditor.SceneView.lastActiveSceneView;
            if (view == null || view.camera == null) return null;
            var size = view.position.size;
            return Render(view.camera, size.x, size.y, maxSize, "scene-view");
        }

        public static string MainCamera(int maxSize = 1536)
        {
            var camera = Camera.main;
            if (camera == null) return null;
            return Render(camera, 16, 9, maxSize, "main-camera");
        }

        /// <summary>
        /// The open scenes from a temporary camera, three-quarter view on everything that renders, for when
        /// there is no Main Camera and no Scene view (a new empty scene, the Scene view closed, batch mode).
        /// A temporary light is added only when the scenes have none. Both are removed after the render.
        /// </summary>
        public static string Overview(int maxSize = 1536)
        {
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            var first = true;
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (first) bounds = renderer.bounds; else bounds.Encapsulate(renderer.bounds);
                first = false;
            }
            var made = new System.Collections.Generic.List<GameObject>();
            try
            {
                var cameraObject = new GameObject("Scenario Overview Camera") { hideFlags = HideFlags.HideAndDontSave };
                made.Add(cameraObject);
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = 40;
                var radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
                var distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.1f;
                var direction = Quaternion.Euler(25, -35, 0) * Vector3.back;
                cameraObject.transform.position = bounds.center + direction * distance;
                cameraObject.transform.LookAt(bounds.center);
                camera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 2);
                camera.farClipPlane = distance + radius * 4;
                if (UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length == 0)
                {
                    var lightObject = new GameObject("Scenario Overview Light") { hideFlags = HideFlags.HideAndDontSave };
                    made.Add(lightObject);
                    var light = lightObject.AddComponent<Light>();
                    light.type = LightType.Directional;
                    lightObject.transform.rotation = Quaternion.Euler(50, -30, 0);
                }
                return Render(camera, 16, 9, maxSize, "overview");
            }
            finally
            {
                foreach (var go in made) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        public static string Render(Camera camera, float aspectWidth, float aspectHeight, int maxSize, string label)
        {
            var aspect = aspectHeight > 0 ? aspectWidth / aspectHeight : 1;
            var width = aspect >= 1 ? maxSize : Mathf.RoundToInt(maxSize * aspect);
            var height = aspect >= 1 ? Mathf.RoundToInt(maxSize / aspect) : maxSize;
            var image = Shoot(camera, width, height);
            try
            {
                return Save(image, label);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        /// <summary>A 16:9 view from eye toward look, through a temporary camera with the main camera's look (sky, post-processing).</summary>
        public static string From(Vector3 eye, Vector3 look, int maxSize = 1536, float fieldOfView = 50f)
        {
            var camera = TemporaryCamera(fieldOfView);
            try
            {
                camera.transform.position = eye;
                camera.transform.LookAt(look);
                return Render(camera, 16, 9, maxSize, "view");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }

        /// <summary>The four three-quarter views of a region, from the south-west, south-east, north-west and north-east (+z is north), in a 2 x 2 grid.</summary>
        public static readonly string[] AroundOrder = { "south-west", "south-east", "north-west", "north-east" };

        public static string Around(Bounds region, int maxSize = 1536, float elevation = 25f, float distance = 0f, float fieldOfView = 45f)
        {
            const int gap = 4;
            var tileWidth = Mathf.Max(64, (maxSize - gap) / 2);
            var tileHeight = Mathf.RoundToInt(tileWidth * 9f / 16f);
            var radius = Mathf.Max(region.extents.magnitude, 1f);
            if (distance <= 0) distance = radius / Mathf.Sin(fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
            var camera = TemporaryCamera(fieldOfView);
            var grid = new Texture2D(tileWidth * 2 + gap, tileHeight * 2 + gap, TextureFormat.RGB24, false);
            var tiles = new List<Texture2D>();
            try
            {
                grid.SetPixels32(new Color32[grid.width * grid.height]);
                // Compass yaw of the camera's place around the region, and its grid cell (row 0 at the top).
                var views = new[] { (yaw: 45f, column: 0, row: 0), (yaw: 315f, column: 1, row: 0), (yaw: 135f, column: 0, row: 1), (yaw: 225f, column: 1, row: 1) };
                foreach (var view in views)
                {
                    camera.transform.position = region.center + Quaternion.Euler(Mathf.Clamp(elevation, -10, 89), view.yaw, 0) * Vector3.back * distance;
                    camera.transform.LookAt(region.center);
                    var tile = Shoot(camera, tileWidth, tileHeight);
                    tiles.Add(tile);
                    // Texture rows start at the bottom.
                    grid.SetPixels(view.column * (tileWidth + gap), (1 - view.row) * (tileHeight + gap), tileWidth, tileHeight, tile.GetPixels());
                }
                grid.Apply();
                return Save(grid, "around");
            }
            finally
            {
                foreach (var tile in tiles) UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(grid);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }

        /// <summary>A hidden camera set up like the main camera (sky, clipping, post-processing), for views no scene camera has.</summary>
        static Camera TemporaryCamera(float fieldOfView)
        {
            var go = new GameObject("Scenario Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = go.AddComponent<Camera>();
            if (Camera.main != null) camera.CopyFrom(Camera.main);
            camera.targetTexture = null;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = Mathf.Max(camera.farClipPlane, 5000f);
#if SCENARIO_URP
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
#endif
            return camera;
        }

        static Texture2D Shoot(Camera camera, int width, int height)
        {
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = camera.targetTexture;
            var active = RenderTexture.active;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                return image;
            }
            finally
            {
                camera.targetTexture = previous;
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        static string Save(Texture2D image, string label)
        {
            Directory.CreateDirectory(Folder);
            var path = Path.GetFullPath(Path.Combine(Folder, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"));
            File.WriteAllBytes(path, image.EncodeToPNG());
            return path;
        }

        /// <summary>A PNG copy of any texture asset (PSD, TGA, EXR...), for uploading.</summary>
        public static string TextureToPng(Texture texture, int maxSize = 2048)
        {
            if (texture == null) return null;
            var scale = Mathf.Min(1f, (float)maxSize / Mathf.Max(texture.width, texture.height));
            var width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var active = RenderTexture.active;
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                Directory.CreateDirectory(Folder);
                var path = Path.GetFullPath(Path.Combine(Folder, $"{Core.Files.Slug(texture.name)}-{DateTime.Now:HHmmss}.png"));
                File.WriteAllBytes(path, image.EncodeToPNG());
                return path;
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
