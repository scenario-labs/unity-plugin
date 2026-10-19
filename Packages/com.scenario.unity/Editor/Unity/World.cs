using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Scenario.Editor
{
    /// <summary>
    /// Builds walkable ground and dresses it: a Terrain from a heightmap image (or gentle
    /// noise) with ground materials painted by height and slope, models scattered on the
    /// ground with colliders, and helpers that size a model and sit it on the ground.
    /// Every object created is one Undo step for the caller to group.
    /// </summary>
    public static class World
    {
        public const string DefaultFolder = "Assets/Scenario/World";
        /// <summary>Where terrain data and layers are saved when a spec names no folder (tests point it elsewhere).</summary>
        internal static string Root = DefaultFolder;

        // --- Terrain ---------------------------------------------------------------------

        public sealed class Layer
        {
            /// <summary>A material (its base and normal maps are used) or a texture.</summary>
            public string Source = "";
            public string Normal = "";
            /// <summary>Meters covered by one tile of the texture.</summary>
            public float Tile = 8f;
            /// <summary>Where this layer shows, as a share of the terrain's height (0 to 1) and in degrees of slope.</summary>
            public float MinHeight, MaxHeight = 1f, MinSlope, MaxSlope = 90f;
        }

        public sealed class Flat
        {
            public Vector2 Center;
            public float Radius = 10f;
            /// <summary>World height of the flat area; NaN keeps the ground height at the center.</summary>
            public float Height = float.NaN;
        }

        public sealed class TerrainSpec
        {
            public string Name = "Terrain";
            /// <summary>A grayscale image asset (white is high). Empty: rolling noise.</summary>
            public string Heightmap = "";
            public Vector3 Size = new Vector3(200, 30, 200);
            public Vector3 Center = Vector3.zero;
            public int Resolution = 513;
            public int Smoothing = 2;
            /// <summary>Share of the width over which the edges fall to the bottom (0: none). Makes an island or a plateau.</summary>
            public float EdgeFalloff;
            public int Seed = 1;
            public List<Flat> Flats = new List<Flat>();
            public List<Layer> Layers = new List<Layer>();
            public string Folder = "";
        }

        public sealed class TerrainResult
        {
            public Terrain Terrain;
            public float MinHeight, MaxHeight;
            public List<string> Notes = new List<string>();
        }

        public static TerrainResult CreateTerrain(TerrainSpec spec)
        {
            var resolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(spec.Resolution, 33, 4097) - 1) + 1;
            var size = new Vector3(Mathf.Max(1, spec.Size.x), Mathf.Max(0.1f, spec.Size.y), Mathf.Max(1, spec.Size.z));
            var result = new TerrainResult();
            var heights = spec.Heightmap.Length > 0 ? ReadHeightmap(spec.Heightmap, resolution) : Noise(resolution, spec.Seed);
            for (var i = 0; i < spec.Smoothing; i++) Blur(heights);
            if (spec.EdgeFalloff > 0) Falloff(heights, Mathf.Clamp01(spec.EdgeFalloff));

            var origin = spec.Center - new Vector3(size.x / 2, 0, size.z / 2);
            foreach (var flat in spec.Flats) Flatten(heights, flat, origin, size);

            var folder = Folder(spec.Folder, spec.Name);
            var data = new TerrainData { heightmapResolution = resolution };
            data.size = size;
            data.SetHeights(0, 0, heights);
            AssetDatabase.CreateAsset(data, Unique(folder + "/" + Core.Files.Slug(spec.Name) + ".asset"));

            if (spec.Layers.Count > 0)
            {
                data.alphamapResolution = Mathf.Clamp(resolution - 1, 16, 1024);
                data.terrainLayers = spec.Layers.Select((l, i) => MakeLayer(l, folder, spec.Name, i, result.Notes)).ToArray();
                Paint(data, spec.Layers);
            }

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = spec.Name;
            go.transform.position = origin;
            var terrain = go.GetComponent<Terrain>();
            var template = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial : null;
            if (template != null) terrain.materialTemplate = template;
            Undo.RegisterCreatedObjectUndo(go, "Create terrain");
            AssetDatabase.SaveAssets();
            Physics.SyncTransforms();

            result.Terrain = terrain;
            var all = heights.Cast<float>().ToArray();
            result.MinHeight = origin.y + all.Min() * size.y;
            result.MaxHeight = origin.y + all.Max() * size.y;
            return result;
        }

        static float[,] ReadHeightmap(string assetPath, int resolution)
        {
            var source = LoadPixels(assetPath);
            try
            {
                var heights = new float[resolution, resolution];
                // Terrain heights are [row = z, column = x]; image row 0 is the bottom, which is -z.
                for (var z = 0; z < resolution; z++)
                for (var x = 0; x < resolution; x++)
                {
                    var c = source.GetPixelBilinear(x / (float)(resolution - 1), z / (float)(resolution - 1));
                    heights[z, x] = Mathf.Clamp01(c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f);
                }
                return heights;
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
        }

        /// <summary>
        /// The image's own pixels: PNG and JPEG files are decoded from disk (16-bit PNGs keep their
        /// precision, and the imported texture's compression never reaches the heights); other
        /// formats are copied from the imported texture.
        /// </summary>
        static Texture2D LoadPixels(string assetPath)
        {
            var extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if ((extension == ".png" || extension == ".jpg" || extension == ".jpeg") && File.Exists(assetPath))
            {
                var decoded = new Texture2D(2, 2, TextureFormat.RGBA64, false, true);
                if (decoded.LoadImage(File.ReadAllBytes(assetPath))) return decoded;
                Object.DestroyImmediate(decoded);
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
            if (texture == null) throw new ArgumentException($"No image at {assetPath}.");
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var active = RenderTexture.active;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply();
                return image;
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        static float[,] Noise(int resolution, int seed)
        {
            var random = new System.Random(seed);
            var ox = (float)random.NextDouble() * 1000f;
            var oz = (float)random.NextDouble() * 1000f;
            var heights = new float[resolution, resolution];
            for (var z = 0; z < resolution; z++)
            for (var x = 0; x < resolution; x++)
            {
                float u = (float)x / resolution, v = (float)z / resolution, value = 0, amplitude = 0.5f, frequency = 3f;
                for (var octave = 0; octave < 4; octave++)
                {
                    value += amplitude * Mathf.PerlinNoise(ox + u * frequency, oz + v * frequency);
                    amplitude *= 0.5f;
                    frequency *= 2f;
                }
                heights[z, x] = Mathf.Clamp01(value * 0.6f + 0.1f);
            }
            return heights;
        }

        static void Blur(float[,] heights)
        {
            var n = heights.GetLength(0);
            var copy = (float[,])heights.Clone();
            for (var z = 1; z < n - 1; z++)
            for (var x = 1; x < n - 1; x++)
            {
                var sum = 0f;
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    sum += copy[z + dz, x + dx];
                heights[z, x] = sum / 9f;
            }
        }

        static void Falloff(float[,] heights, float share)
        {
            var n = heights.GetLength(0);
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var edge = Mathf.Min(Mathf.Min(x, n - 1 - x), Mathf.Min(z, n - 1 - z)) / (float)(n - 1);
                var t = Mathf.Clamp01(edge / share);
                heights[z, x] *= t * t * (3 - 2 * t);
            }
        }

        static void Flatten(float[,] heights, Flat flat, Vector3 origin, Vector3 size)
        {
            var n = heights.GetLength(0);
            float Column(float worldX) => (worldX - origin.x) / size.x * (n - 1);
            float Row(float worldZ) => (worldZ - origin.z) / size.z * (n - 1);
            int cx = Mathf.Clamp(Mathf.RoundToInt(Column(flat.Center.x)), 0, n - 1), cz = Mathf.Clamp(Mathf.RoundToInt(Row(flat.Center.y)), 0, n - 1);
            var level = float.IsNaN(flat.Height) ? heights[cz, cx] : Mathf.Clamp01((flat.Height - origin.y) / size.y);
            var blend = flat.Radius * 0.6f;
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var worldX = origin.x + x / (float)(n - 1) * size.x;
                var worldZ = origin.z + z / (float)(n - 1) * size.z;
                var distance = Vector2.Distance(new Vector2(worldX, worldZ), flat.Center);
                if (distance >= flat.Radius + blend) continue;
                var t = distance <= flat.Radius ? 1f : 1f - (distance - flat.Radius) / blend;
                t = t * t * (3 - 2 * t);
                heights[z, x] = Mathf.Lerp(heights[z, x], level, t);
            }
        }

        static TerrainLayer MakeLayer(Layer layer, string folder, string name, int index, List<string> notes)
        {
            var terrainLayer = new TerrainLayer { tileSize = new Vector2(layer.Tile, layer.Tile) };
            var material = AssetDatabase.LoadAssetAtPath<Material>(layer.Source);
            if (material != null)
            {
                terrainLayer.diffuseTexture = FirstTexture(material, "_BaseMap", "_MainTex", "_BaseColorMap");
                terrainLayer.normalMapTexture = FirstTexture(material, "_BumpMap", "_NormalMap");
            }
            else
            {
                terrainLayer.diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(layer.Source);
                if (terrainLayer.diffuseTexture == null) notes.Add($"Layer {index + 1}: no material or texture at {layer.Source}.");
            }
            if (layer.Normal.Length > 0) terrainLayer.normalMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(layer.Normal);
            AssetDatabase.CreateAsset(terrainLayer, Unique($"{folder}/{Core.Files.Slug(name)}-layer-{index + 1}.terrainlayer"));
            return terrainLayer;
        }

        static Texture2D FirstTexture(Material material, params string[] names)
        {
            foreach (var property in names)
                if (material.HasProperty(property) && material.GetTexture(property) is Texture2D texture)
                    return texture;
            return material.mainTexture as Texture2D;
        }

        /// <summary>Each cell takes the last layer whose height and slope rules it meets, with soft edges; the first layer is the base.</summary>
        static void Paint(TerrainData data, List<Layer> layers)
        {
            var n = data.alphamapResolution;
            var maps = new float[n, n, layers.Count];
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                float u = x / (float)(n - 1), v = z / (float)(n - 1);
                var height = data.GetInterpolatedHeight(u, v) / data.size.y;
                var slope = data.GetSteepness(u, v);
                var remaining = 1f;
                for (var i = layers.Count - 1; i >= 1 && remaining > 0; i--)
                {
                    var l = layers[i];
                    var weight = Mathf.Min(Band(height, l.MinHeight, l.MaxHeight, 0.03f, 1f), Band(slope, l.MinSlope, l.MaxSlope, 4f, 90f)) * remaining;
                    maps[z, x, i] = weight;
                    remaining -= weight;
                }
                maps[z, x, 0] += remaining;
            }
            data.SetAlphamaps(0, 0, maps);
        }

        /// <summary>1 inside [min, max], 0 outside, with a ramp of width soft; a bound at 0 or at top is open.</summary>
        static float Band(float value, float min, float max, float soft, float top)
        {
            var low = min <= 0 ? 1f : Mathf.Clamp01((value - min) / soft + 0.5f);
            var high = max >= top ? 1f : Mathf.Clamp01((max - value) / soft + 0.5f);
            return Mathf.Min(low, high);
        }

        // --- Scatter ---------------------------------------------------------------------

        public sealed class ScatterSpec
        {
            /// <summary>A prefab or model asset path, or a scene object to copy.</summary>
            public string Source = "";
            public int Count = 10;
            public Vector2 Center;
            public Vector2 Area = new Vector2(50, 50);
            /// <summary>Ground object: only hits on its colliders count. Empty: any collider outside the scattered group.</summary>
            public string On = "";
            /// <summary>Height of each copy in meters, picked between the two. Zero keeps the source's size.</summary>
            public Vector2 Height;
            public Vector2 Scale = Vector2.one;
            public bool RandomYaw = true;
            /// <summary>Most degrees a copy leans to follow the ground.</summary>
            public float Tilt;
            public float MaxSlope = 35f;
            /// <summary>Skips ground flatter than this, degrees: with Tilt, dresses cliff faces.</summary>
            public float MinSlope;
            /// <summary>Meters between copies; 0 picks 80% of a copy's footprint.</summary>
            public float MinDistance;
            public List<(Vector2 center, float radius)> KeepClear = new List<(Vector2, float)>();
            /// <summary>mesh, box or none.</summary>
            public string Collider = "mesh";
            public int Seed = 1;
            public string Group = "";
        }

        public sealed class ScatterResult
        {
            public GameObject Group;
            public int Placed;
            public int SkippedSlope, SkippedFlat, SkippedClear, SkippedNoGround, SkippedCrowded;
        }

        public static ScatterResult Scatter(ScatterSpec spec)
        {
            var template = Template(spec.Source, out var owned);
            try
            {
                var result = new ScatterResult();
                var groupName = spec.Group.Length > 0 ? spec.Group : template.name + " (scattered)";
                var group = new GameObject(groupName);
                Undo.RegisterCreatedObjectUndo(group, "Scatter");
                result.Group = group;
                var ground = spec.On.Length > 0 ? Placer.FindByPath(spec.On) : null;
                if (spec.On.Length > 0 && ground == null) throw new ArgumentException($"No object at \"{spec.On}\".");
                var groundColliders = ground != null ? new HashSet<Collider>(ground.GetComponentsInChildren<Collider>()) : null;
                Physics.SyncTransforms();

                var random = new System.Random(spec.Seed);
                float Range(Vector2 r) => r.y > r.x ? Mathf.Lerp(r.x, r.y, (float)random.NextDouble()) : r.x;
                var baseBounds = BoundsOf(template);
                var footprint = Mathf.Max(baseBounds.size.x, baseBounds.size.z);
                var placed = new List<(Vector2 at, float radius)>();
                var attempts = Mathf.Max(30, spec.Count * 30);
                for (var attempt = 0; attempt < attempts && result.Placed < spec.Count; attempt++)
                {
                    var at = spec.Center + new Vector2(((float)random.NextDouble() - 0.5f) * spec.Area.x, ((float)random.NextDouble() - 0.5f) * spec.Area.y);
                    var yaw = spec.RandomYaw ? (float)random.NextDouble() * 360f : template.transform.eulerAngles.y;
                    var height = Range(spec.Height);
                    var factor = height > 0 && baseBounds.size.y > 0 ? height / baseBounds.size.y : Range(spec.Scale);
                    var radius = footprint * factor * 0.5f;
                    var spacing = spec.MinDistance > 0 ? spec.MinDistance : radius * 1.6f;
                    if (spec.KeepClear.Any(k => Vector2.Distance(at, k.center) < k.radius + radius))
                    {
                        result.SkippedClear++;
                        continue;
                    }
                    if (placed.Any(p => Vector2.Distance(at, p.at) < Mathf.Max(spacing, p.radius + radius) * (spec.MinDistance > 0 ? 1f : 0.8f)))
                    {
                        result.SkippedCrowded++;
                        continue;
                    }
                    if (!GroundHit(new Vector3(at.x, 0, at.y), groundColliders, group.transform, out var hit))
                    {
                        result.SkippedNoGround++;
                        continue;
                    }
                    var slope = Vector3.Angle(hit.normal, Vector3.up);
                    if (slope > spec.MaxSlope)
                    {
                        result.SkippedSlope++;
                        continue;
                    }
                    if (slope < spec.MinSlope)
                    {
                        result.SkippedFlat++;
                        continue;
                    }

                    var copy = Instance(spec.Source, template);
                    Undo.RegisterCreatedObjectUndo(copy, "Scatter");
                    copy.name = template.name + " " + (result.Placed + 1);
                    copy.transform.SetParent(group.transform, true);
                    var lean = spec.Tilt > 0 ? Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, hit.normal), spec.Tilt) : Quaternion.identity;
                    copy.transform.rotation = lean * Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(template.transform.eulerAngles.x, 0, template.transform.eulerAngles.z);
                    copy.transform.localScale = template.transform.lossyScale * factor;
                    // Sink a little so no edge floats on a slope.
                    var sink = 0.03f * Mathf.Max(height, baseBounds.size.y * factor) + radius * Mathf.Tan(Mathf.Min(slope, 60f) * Mathf.Deg2Rad) * 0.5f;
                    SitOn(copy, hit.point, sink);
                    AddColliders(copy, spec.Collider);
                    placed.Add((at, radius));
                    result.Placed++;
                }
                Physics.SyncTransforms();
                return result;
            }
            finally
            {
                if (owned) Object.DestroyImmediate(template);
            }
        }

        /// <summary>The object copies are made from: a scene object as is, or a hidden instance of an asset.</summary>
        static GameObject Template(string source, out bool owned)
        {
            owned = false;
            if (source.StartsWith("Assets/") || source.StartsWith("Packages/"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(source);
                if (asset == null) throw new ArgumentException($"No prefab or model at {source}.");
                owned = true;
                var template = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                template.hideFlags = HideFlags.HideAndDontSave;
                return template;
            }
            var go = Placer.FindByPath(source);
            if (go == null) throw new ArgumentException($"No object at \"{source}\".");
            return go;
        }

        static GameObject Instance(string source, GameObject template)
        {
            if (source.StartsWith("Assets/") || source.StartsWith("Packages/"))
                return (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            var prefab = PrefabUtility.GetCorrespondingObjectFromSource(template);
            if (prefab != null && PrefabUtility.GetOutermostPrefabInstanceRoot(template) == template)
                return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            return Object.Instantiate(template);
        }

        /// <summary>
        /// Gives an object collision when none of its parts has any: one box around it, or a mesh collider
        /// on each mesh. Returns how many colliders were added.
        /// </summary>
        public static int AddColliders(GameObject go, string kind)
        {
            if (kind == "none") return 0;
            if (go.GetComponentInChildren<Collider>() != null) return 0;
            if (kind == "box")
            {
                var bounds = BoundsOf(go);
                var box = Undo.AddComponent<BoxCollider>(go);
                box.center = go.transform.InverseTransformPoint(bounds.center);
                var scale = go.transform.lossyScale;
                box.size = new Vector3(bounds.size.x / Mathf.Abs(scale.x), bounds.size.y / Mathf.Abs(scale.y), bounds.size.z / Mathf.Abs(scale.z));
                return 1;
            }
            var added = 0;
            foreach (var filter in go.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                var collider = Undo.AddComponent<MeshCollider>(filter.gameObject);
                collider.sharedMesh = filter.sharedMesh;
                added++;
            }
            return added;
        }

        // --- Ground and size ---------------------------------------------------------------

        /// <summary>World bounds of an object's renderers (colliders when it has none).</summary>
        public static Bounds BoundsOf(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>().Where(r => !(r is ParticleSystemRenderer)).ToArray();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
                return bounds;
            }
            var colliders = root.GetComponentsInChildren<Collider>();
            if (colliders.Length > 0)
            {
                var bounds = colliders[0].bounds;
                foreach (var c in colliders.Skip(1)) bounds.Encapsulate(c.bounds);
                return bounds;
            }
            return new Bounds(root.transform.position, Vector3.zero);
        }

        /// <summary>Scales an object uniformly so its bounds are this many meters tall.</summary>
        public static void FitHeight(GameObject go, float meters)
        {
            var bounds = BoundsOf(go);
            if (bounds.size.y <= 0 || meters <= 0) return;
            Undo.RecordObject(go.transform, "Set size");
            go.transform.localScale *= meters / bounds.size.y;
        }

        /// <summary>Sits an object on the ground under it, its lowest point at the ground. False when there is no ground below.</summary>
        public static bool Ground(GameObject go, float sink = 0f, GameObject on = null)
        {
            Physics.SyncTransforms();
            var colliders = on != null ? new HashSet<Collider>(on.GetComponentsInChildren<Collider>()) : null;
            var bounds = BoundsOf(go);
            if (!GroundHit(bounds.center, colliders, go.transform, out var hit)) return false;
            Undo.RecordObject(go.transform, "Put on the ground");
            SitOn(go, hit.point, sink);
            return true;
        }

        /// <summary>Ground height at a point, or NaN.</summary>
        public static float HeightAt(Vector3 point, GameObject on = null)
        {
            Physics.SyncTransforms();
            var colliders = on != null ? new HashSet<Collider>(on.GetComponentsInChildren<Collider>()) : null;
            return GroundHit(point, colliders, null, out var hit) ? hit.point.y : float.NaN;
        }

        /// <summary>Height of the terrain surface under a point, or NaN where no terrain is.</summary>
        public static float TerrainHeightAt(Vector3 point)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                var corner = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (point.x >= corner.x && point.x <= corner.x + size.x && point.z >= corner.z && point.z <= corner.z + size.z)
                    return terrain.SampleHeight(point) + corner.y;
            }
            return float.NaN;
        }

        static void SitOn(GameObject go, Vector3 point, float sink)
        {
            var bounds = BoundsOf(go);
            var offset = go.transform.position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            go.transform.position = point + offset - Vector3.up * sink;
        }

        /// <summary>The highest hit straight below (from far above) on the ground colliders, skipping anything under ignore.</summary>
        static bool GroundHit(Vector3 point, HashSet<Collider> only, Transform ignore, out RaycastHit best, Transform ignoreToo = null)
        {
            best = default;
            var found = false;
            var origin = new Vector3(point.x, 10000f, point.z);
            foreach (var hit in Physics.RaycastAll(origin, Vector3.down, 20000f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (only != null && !only.Contains(hit.collider)) continue;
                if (ignore != null && hit.transform.IsChildOf(ignore)) continue;
                if (ignoreToo != null && hit.transform.IsChildOf(ignoreToo)) continue;
                if (hit.collider.GetComponentInParent<Scenario.ScenarioPlayer>() != null) continue;
                if (!found || hit.point.y > best.point.y)
                {
                    best = hit;
                    found = true;
                }
            }
            return found;
        }

        // --- Measure -------------------------------------------------------------------------

        public struct Sample
        {
            public Vector2 At;
            public bool Found;
            public float Height, Slope;
            public GameObject Surface;
        }

        /// <summary>The ground at each [x, z]: height, slope in degrees and the object hit. Changes nothing.</summary>
        public static List<Sample> Measure(IEnumerable<Vector2> points, GameObject on = null)
        {
            Physics.SyncTransforms();
            var colliders = on != null ? new HashSet<Collider>(on.GetComponentsInChildren<Collider>()) : null;
            var samples = new List<Sample>();
            foreach (var at in points)
            {
                var sample = new Sample { At = at };
                if (GroundHit(new Vector3(at.x, 0, at.y), colliders, null, out var hit))
                {
                    sample.Found = true;
                    sample.Height = hit.point.y;
                    sample.Slope = Vector3.Angle(hit.normal, Vector3.up);
                    sample.Surface = hit.collider.gameObject;
                }
                samples.Add(sample);
            }
            return samples;
        }

        // --- Walkable props ------------------------------------------------------------------

        public sealed class WalkableSpec
        {
            public string Target = "";
            /// <summary>Ends of the walking line [x, z]; default the object's long axis.</summary>
            public Vector2? From, To;
            /// <summary>Walking width in meters; 0 measures the deck.</summary>
            public float Width;
            public bool Rails = true;
            public float RailHeight = 1.1f;
            /// <summary>Slopes from each end to the ground beyond it, when that ground is within 1.5 m up or down.</summary>
            public bool Ramps = true;
            /// <summary>Keeps the object's own colliders on. By default they are turned off so their bumps and holes do not trip the player.</summary>
            public bool KeepColliders;
        }

        public sealed class WalkableResult
        {
            public GameObject Walkway;
            public Vector3 Start, End;
            public float Length, Width;
            public int Segments, Holes, Bumps, CollidersOff;
            public float StartGround = float.NaN, EndGround = float.NaN;
            public bool StartRamp, EndRamp;
            public List<string> Notes = new List<string>();
        }

        const float WalkStep = 0.5f, DeckThickness = 0.3f, RailThickness = 0.15f;

        /// <summary>
        /// Lays invisible colliders on a model's walking surface (a generated bridge, ramp, stairs or platform):
        /// the deck is measured along its length, holes are bridged and bumps smoothed, rails keep the player on
        /// it, and short ramps join each end to the ground beyond. Running it again replaces the walkway.
        /// </summary>
        public static WalkableResult MakeWalkable(WalkableSpec spec)
        {
            var target = Placer.FindByPath(spec.Target);
            if (target == null) throw new ArgumentException($"No object at \"{spec.Target}\".");
            var result = new WalkableResult();
            // A walkway from an earlier run would be measured as deck.
            var old = FindWalkway(target);
            if (old != null) Undo.DestroyObjectImmediate(old);

            GameObject walkway;
            var temporary = new List<Collider>();
            try
            {
                foreach (var filter in target.GetComponentsInChildren<MeshFilter>())
                {
                    if (filter.sharedMesh == null || filter.GetComponents<Collider>().Any(c => c.enabled)) continue;
                    var collider = filter.gameObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = filter.sharedMesh;
                    temporary.Add(collider);
                }
                Physics.SyncTransforms();
                var deck = new HashSet<Collider>(target.GetComponentsInChildren<Collider>().Where(c => c.enabled));
                if (deck.Count == 0) throw new ArgumentException($"{target.name} has no mesh to walk on.");
                var bounds = BoundsOf(target);
                var top = bounds.max.y + 5f;

                var axis = LongAxis(target);
                var a = spec.From ?? axis.a;
                var b = spec.To ?? axis.b;
                var length = Vector2.Distance(a, b);
                if (length < 0.5f) throw new ArgumentException("The walking line is shorter than half a meter: give from and to.");
                var dir = (b - a) / length;
                var right = new Vector2(dir.y, -dir.x);

                // The highest upward-facing surface of the object at a point, or NaN.
                float DeckAt(Vector2 p)
                {
                    var best = float.NaN;
                    foreach (var hit in Physics.RaycastAll(new Vector3(p.x, top, p.y), Vector3.down, top - bounds.min.y + 10f, ~0, QueryTriggerInteraction.Ignore))
                        if (deck.Contains(hit.collider) && hit.normal.y > 0.5f && (float.IsNaN(best) || hit.point.y > best)) best = hit.point.y;
                    return best;
                }

                // Width: the run of deck around the line at a quarter, half and three quarters of the way.
                var width = spec.Width;
                if (width <= 0)
                {
                    var spans = new List<float>();
                    foreach (var t in new[] { 0.25f, 0.5f, 0.75f })
                    {
                        var center = Vector2.Lerp(a, b, t);
                        var level = DeckAt(center);
                        if (float.IsNaN(level)) continue;
                        float Reach(float sign)
                        {
                            var reach = 0f;
                            for (var o = 0.1f; o <= axis.width; o += 0.1f)
                            {
                                var h = DeckAt(center + right * (sign * o));
                                if (float.IsNaN(h) || Mathf.Abs(h - level) > 0.25f) break;
                                reach = o;
                            }
                            return reach;
                        }
                        spans.Add(Reach(1) + Reach(-1));
                    }
                    var measured = spans.Count > 0 ? spans.OrderBy(w => w).ElementAt(spans.Count / 2) : axis.width;
                    if (spans.Count == 0) result.Notes.Add("No deck found across the middle: the width is the model's. Give width if it is wrong.");
                    width = Mathf.Max(0.8f, spec.Rails ? measured * 0.85f : measured);
                }

                // The deck's height every half meter, in the middle and a quarter of the width to each side.
                var count = Mathf.Max(2, Mathf.CeilToInt(length / WalkStep) + 1);
                var at = new float[count];
                var raw = new float[count];
                for (var i = 0; i < count; i++)
                {
                    at[i] = Mathf.Min(length, i * WalkStep);
                    var p = a + dir * at[i];
                    var lanes = new[] { -0.25f, 0f, 0.25f }.Select(l => DeckAt(p + right * (width * l))).Where(h => !float.IsNaN(h)).OrderBy(h => h).ToList();
                    raw[i] = lanes.Count > 0 ? lanes[lanes.Count / 2] : float.NaN;
                }
                var first = Array.FindIndex(raw, h => !float.IsNaN(h));
                var last = Array.FindLastIndex(raw, h => !float.IsNaN(h));
                if (first < 0 || last - first < 1)
                    throw new ArgumentException($"No walking surface found on {target.name} along its length: give from and to across its deck.");

                // A spike well off its neighbors (a post, a lantern) is not deck.
                var heights = (float[])raw.Clone();
                var off = new bool[count];
                for (var i = first + 1; i < last; i++)
                {
                    if (float.IsNaN(raw[i])) continue;
                    var near = Enumerable.Range(Math.Max(first, i - 3), Math.Min(last, i + 3) - Math.Max(first, i - 3) + 1)
                        .Where(j => j != i && !float.IsNaN(raw[j])).Select(j => raw[j]).OrderBy(h => h).ToList();
                    if (near.Count < 2 || Mathf.Abs(raw[i] - near[near.Count / 2]) <= 0.6f) continue;
                    heights[i] = float.NaN;
                    off[i] = true;
                }
                // Holes are bridged in a straight line.
                for (var i = first; i <= last; i++)
                {
                    if (!float.IsNaN(heights[i])) continue;
                    var before = i - 1;
                    var after = i;
                    while (after <= last && float.IsNaN(heights[after])) after++;
                    if (after > last) after = before;
                    for (var j = i; j < after; j++)
                    {
                        heights[j] = after == before ? heights[before] : Mathf.Lerp(heights[before], heights[after], (at[j] - at[before]) / (at[after] - at[before]));
                        if (float.IsNaN(raw[j]) && (j == i || !float.IsNaN(raw[j - 1]))) result.Holes++;
                    }
                    i = after;
                }
                // Bumps are smoothed over two meters.
                var smooth = new float[count];
                for (var i = first; i <= last; i++)
                {
                    var from = Math.Max(first, i - 2);
                    var to = Math.Min(last, i + 2);
                    var sum = 0f;
                    for (var j = from; j <= to; j++) sum += heights[j];
                    smooth[i] = sum / (to - from + 1);
                    if (!float.IsNaN(raw[i]) && Mathf.Abs(smooth[i] - raw[i]) > 0.1f) off[i] = true;
                }
                for (var i = first; i <= last; i++)
                    if (off[i] && (i == first || !off[i - 1])) result.Bumps++;

                Vector3 Point(int i) => new Vector3((a + dir * at[i]).x, smooth[i] + 0.03f, (a + dir * at[i]).y);

                // Under a uniformly scaled object the walkway is its child and moves with it; otherwise a sibling.
                var scale = target.transform.lossyScale;
                var uniform = scale.x > 0 && Mathf.Abs(scale.y - scale.x) <= 0.01f * scale.x && Mathf.Abs(scale.z - scale.x) <= 0.01f * scale.x;
                walkway = new GameObject(uniform ? "Walkway" : target.name + " Walkway");
                Undo.RegisterCreatedObjectUndo(walkway, "Make walkable");
                walkway.transform.SetParent(uniform ? target.transform : target.transform.parent, true);
                if (!uniform) walkway.transform.SetSiblingIndex(target.transform.GetSiblingIndex() + 1);
                result.Walkway = walkway;

                GameObject Segment(string name, Vector3 from, Vector3 to, bool rails)
                {
                    var go = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(go, "Make walkable");
                    go.transform.SetPositionAndRotation((from + to) / 2, Quaternion.LookRotation(to - from, Vector3.up));
                    go.transform.SetParent(walkway.transform, true);
                    var span = Vector3.Distance(from, to) + 0.1f;
                    var floor = go.AddComponent<BoxCollider>();
                    floor.center = new Vector3(0, -DeckThickness / 2, 0);
                    floor.size = new Vector3(width, DeckThickness, span);
                    if (rails)
                        foreach (var side in new[] { -1f, 1f })
                        {
                            var rail = go.AddComponent<BoxCollider>();
                            rail.center = new Vector3(side * (width + RailThickness) / 2, spec.RailHeight / 2, 0);
                            rail.size = new Vector3(RailThickness, spec.RailHeight, span);
                        }
                    return go;
                }

                var stops = new List<int>();
                for (var i = first; i < last; i += 2) stops.Add(i);
                stops.Add(last);
                for (var k = 0; k + 1 < stops.Count; k++) Segment("Deck " + (k + 1), Point(stops[k]), Point(stops[k + 1]), spec.Rails);
                result.Segments = stops.Count - 1;
                result.Start = Point(first);
                result.End = Point(last);
                result.Length = at[last] - at[first];
                result.Width = width;

                // Each end meets the ground beyond it with a short ramp when the ground is within reach.
                (float ground, bool ramp) Join(Vector3 end, Vector2 outward, string label)
                {
                    var beyond = new Vector2(end.x, end.z) + outward * 1.5f;
                    if (!GroundHit(new Vector3(beyond.x, 0, beyond.y), null, target.transform, out var hit, walkway.transform))
                    {
                        result.Notes.Add($"No ground beyond the {label} end.");
                        return (float.NaN, false);
                    }
                    var gap = end.y - hit.point.y;
                    if (Mathf.Abs(gap) < 0.05f) return (hit.point.y, false);
                    if (!spec.Ramps || Mathf.Abs(gap) > 1.5f)
                    {
                        if (Mathf.Abs(gap) > 0.3f)
                            result.Notes.Add($"The {label} end is {Mathf.Abs(gap):0.0} m {(gap > 0 ? "above" : "below")} the ground beyond it: move or resize the object, or level the ground there (terrain_create flat_areas).");
                        return (hit.point.y, false);
                    }
                    var run = Mathf.Clamp(Mathf.Abs(gap) / Mathf.Tan(25f * Mathf.Deg2Rad), 1.5f, 4f);
                    var foot = new Vector2(end.x, end.z) + outward * run;
                    var footHeight = GroundHit(new Vector3(foot.x, 0, foot.y), null, target.transform, out var footHit, walkway.transform) ? footHit.point.y : hit.point.y;
                    Segment(label == "start" ? "Ramp at start" : "Ramp at end", end, new Vector3(foot.x, footHeight - 0.05f, foot.y), false);
                    return (hit.point.y, true);
                }
                (result.StartGround, result.StartRamp) = Join(result.Start, -dir, "start");
                (result.EndGround, result.EndRamp) = Join(result.End, dir, "end");
            }
            finally
            {
                foreach (var collider in temporary) Object.DestroyImmediate(collider);
            }

            if (!spec.KeepColliders)
                foreach (var collider in target.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.transform.IsChildOf(walkway.transform)))
                {
                    Undo.RecordObject(collider, "Make walkable");
                    collider.enabled = false;
                    result.CollidersOff++;
                }
            Physics.SyncTransforms();
            return result;
        }

        static GameObject FindWalkway(GameObject target)
        {
            var child = target.transform.Find("Walkway");
            if (child != null) return child.gameObject;
            var name = target.name + " Walkway";
            var siblings = target.transform.parent != null
                ? target.transform.parent.Cast<Transform>()
                : target.scene.GetRootGameObjects().Select(g => g.transform);
            return siblings.FirstOrDefault(t => t.name == name)?.gameObject;
        }

        /// <summary>The object's long horizontal axis (its local x or z): both ends at the middle of its width, and that width.</summary>
        static (Vector2 a, Vector2 b, float width) LongAxis(GameObject go)
        {
            var points = new List<Vector2>();
            foreach (var filter in go.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                var matrix = filter.transform.localToWorldMatrix;
                if (mesh.isReadable)
                {
                    var vertices = mesh.vertices;
                    var stride = Math.Max(1, vertices.Length / 20000);
                    for (var i = 0; i < vertices.Length; i += stride)
                    {
                        var p = matrix.MultiplyPoint3x4(vertices[i]);
                        points.Add(new Vector2(p.x, p.z));
                    }
                }
                else
                {
                    var box = mesh.bounds;
                    for (var corner = 0; corner < 8; corner++)
                    {
                        var p = matrix.MultiplyPoint3x4(new Vector3(
                            (corner & 1) == 0 ? box.min.x : box.max.x, (corner & 2) == 0 ? box.min.y : box.max.y, (corner & 4) == 0 ? box.min.z : box.max.z));
                        points.Add(new Vector2(p.x, p.z));
                    }
                }
            }
            if (points.Count == 0)
            {
                var bounds = BoundsOf(go);
                points.Add(new Vector2(bounds.min.x, bounds.min.z));
                points.Add(new Vector2(bounds.max.x, bounds.max.z));
            }
            var best = (a: Vector2.zero, b: Vector2.zero, width: 0f, length: -1f);
            foreach (var axis in new[] { go.transform.right, go.transform.forward })
            {
                var d = new Vector2(axis.x, axis.z);
                if (d.sqrMagnitude < 1e-6f) continue;
                d.Normalize();
                var n = new Vector2(-d.y, d.x);
                float min = float.MaxValue, max = float.MinValue, nMin = float.MaxValue, nMax = float.MinValue;
                foreach (var p in points)
                {
                    var s = Vector2.Dot(p, d);
                    var t = Vector2.Dot(p, n);
                    min = Mathf.Min(min, s);
                    max = Mathf.Max(max, s);
                    nMin = Mathf.Min(nMin, t);
                    nMax = Mathf.Max(nMax, t);
                }
                if (max - min <= best.length) continue;
                var mid = (nMin + nMax) / 2;
                best = (d * min + n * mid, d * max + n * mid, nMax - nMin, max - min);
            }
            return (best.a, best.b, best.width);
        }

        // --- Along a path ---------------------------------------------------------------------

        public sealed class AlongSpec
        {
            /// <summary>A prefab or model asset path, or a scene object to copy.</summary>
            public string Source = "";
            /// <summary>Path points [x, z] in walking order.</summary>
            public List<Vector2> Path = new List<Vector2>();
            public float Spacing = 6f;
            /// <summary>Meters from the path line; 0 puts copies on it.</summary>
            public float Offset = 2f;
            /// <summary>right, left, both or alternate, seen walking along the path.</summary>
            public string Side = "alternate";
            /// <summary>Meters along the path before the first copy.</summary>
            public float Start;
            /// <summary>Height of each copy in meters; 0 keeps the source's size.</summary>
            public float Height;
            /// <summary>path (front turned to the path), along (front in the walking direction), random or keep.</summary>
            public string Face = "path";
            /// <summary>Ground object: only hits on its colliders count. Empty: any collider.</summary>
            public string On = "";
            public string Collider = "mesh";
            public float MaxSlope = 40f;
            /// <summary>Most meters each copy moves off its spot at random.</summary>
            public float Jitter;
            public int Seed = 1;
            public string Group = "";
            /// <summary>A point light on each copy, when set.</summary>
            public PointLight Light;
        }

        public sealed class PointLight
        {
            public Color Color = new Color(1f, 0.72f, 0.42f);
            public float Intensity = 2f, Range = 8f;
            /// <summary>Height of the light as a share of the copy's height.</summary>
            public float At = 0.75f;
        }

        public sealed class AlongResult
        {
            public GameObject Group;
            public int Placed, SkippedNoGround, SkippedSlope;
            public float PathLength;
            public List<Vector3> Positions = new List<Vector3>();
        }

        /// <summary>Copies of a model at even spacing along a path, beside it or on it, sitting on the ground and facing the path.</summary>
        public static AlongResult PlaceAlong(AlongSpec spec)
        {
            if (spec.Path.Count < 2) throw new ArgumentException("path needs at least two points [x, z].");
            if (spec.Spacing < 0.1f) throw new ArgumentException("spacing must be at least 0.1 m.");
            var template = Template(spec.Source, out var owned);
            try
            {
                var result = new AlongResult();
                var group = new GameObject(spec.Group.Length > 0 ? spec.Group : template.name + " (along path)");
                Undo.RegisterCreatedObjectUndo(group, "Place along");
                result.Group = group;
                var ground = spec.On.Length > 0 ? Placer.FindByPath(spec.On) : null;
                if (spec.On.Length > 0 && ground == null) throw new ArgumentException($"No object at \"{spec.On}\".");
                var groundColliders = ground != null ? new HashSet<Collider>(ground.GetComponentsInChildren<Collider>()) : null;
                Physics.SyncTransforms();

                var random = new System.Random(spec.Seed);
                var baseBounds = BoundsOf(template);
                var factor = spec.Height > 0 && baseBounds.size.y > 0 ? spec.Height / baseBounds.size.y : 1f;
                var copyHeight = baseBounds.size.y * factor;
                var lengths = new List<float> { 0 };
                for (var i = 1; i < spec.Path.Count; i++) lengths.Add(lengths[i - 1] + Vector2.Distance(spec.Path[i - 1], spec.Path[i]));
                result.PathLength = lengths[lengths.Count - 1];

                var station = 0;
                for (var d = Mathf.Max(0, spec.Start); d <= result.PathLength + 0.001f; d += spec.Spacing, station++)
                {
                    var segment = 1;
                    while (segment < lengths.Count - 1 && lengths[segment] < d) segment++;
                    var span = lengths[segment] - lengths[segment - 1];
                    var tangent = span > 0 ? (spec.Path[segment] - spec.Path[segment - 1]) / span : Vector2.up;
                    var point = span > 0 ? Vector2.Lerp(spec.Path[segment - 1], spec.Path[segment], (d - lengths[segment - 1]) / span) : spec.Path[segment - 1];
                    var right = new Vector2(tangent.y, -tangent.x);
                    foreach (var side in spec.Offset > 0 ? Sides(spec.Side, station) : new[] { 0f })
                    {
                        var spot = point + right * (side * spec.Offset);
                        if (spec.Jitter > 0) spot += new Vector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * (2 * spec.Jitter);
                        if (!GroundHit(new Vector3(spot.x, 0, spot.y), groundColliders, group.transform, out var hit, owned ? null : template.transform))
                        {
                            result.SkippedNoGround++;
                            continue;
                        }
                        if (Vector3.Angle(hit.normal, Vector3.up) > spec.MaxSlope)
                        {
                            result.SkippedSlope++;
                            continue;
                        }
                        Vector2 front;
                        switch (spec.Face)
                        {
                            case "along": front = tangent; break;
                            case "keep": front = Vector2.zero; break;
                            case "random":
                                var turn = (float)random.NextDouble() * Mathf.PI * 2;
                                front = new Vector2(Mathf.Sin(turn), Mathf.Cos(turn));
                                break;
                            default: front = side == 0 ? tangent : point - spot; break;
                        }
                        var yaw = front.sqrMagnitude > 1e-6f ? Mathf.Atan2(front.x, front.y) * Mathf.Rad2Deg : template.transform.eulerAngles.y;

                        var copy = Instance(spec.Source, template);
                        Undo.RegisterCreatedObjectUndo(copy, "Place along");
                        copy.name = template.name + " " + (result.Placed + 1);
                        copy.transform.SetParent(group.transform, true);
                        copy.transform.rotation = Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(template.transform.eulerAngles.x, 0, template.transform.eulerAngles.z);
                        copy.transform.localScale = template.transform.lossyScale * factor;
                        SitOn(copy, hit.point, 0.02f * copyHeight);
                        AddColliders(copy, spec.Collider);
                        if (spec.Light != null) AddLight(copy, spec.Light, copyHeight);
                        result.Positions.Add(copy.transform.position);
                        result.Placed++;
                    }
                }
                Physics.SyncTransforms();
                return result;
            }
            finally
            {
                if (owned) Object.DestroyImmediate(template);
            }
        }

        static float[] Sides(string side, int station)
        {
            switch (side)
            {
                case "right": return new[] { 1f };
                case "left": return new[] { -1f };
                case "both": return new[] { 1f, -1f };
                case "alternate": return new[] { station % 2 == 0 ? 1f : -1f };
                default: throw new ArgumentException("side is right, left, both or alternate.");
            }
        }

        static void AddLight(GameObject copy, PointLight spec, float height)
        {
            var go = new GameObject("Light");
            Undo.RegisterCreatedObjectUndo(go, "Place along");
            go.transform.SetParent(copy.transform, false);
            var bounds = BoundsOf(copy);
            go.transform.position = new Vector3(bounds.center.x, bounds.min.y + height * spec.At, bounds.center.z);
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = spec.Color;
            light.intensity = spec.Intensity;
            light.range = spec.Range;
            light.shadows = LightShadows.None;
        }

        // --- Files -----------------------------------------------------------------------

        static string Folder(string root, string name)
        {
            var folder = (root.Length > 0 ? root : Root).TrimEnd('/') + "/" + Core.Files.Slug(name);
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            return folder;
        }

        static string Unique(string path) => AssetDatabase.GenerateUniqueAssetPath(path);
    }
}
