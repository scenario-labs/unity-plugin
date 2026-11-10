using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

namespace Scenario.Editor
{
    public sealed class PlaceResult
    {
        public List<GameObject> Objects = new List<GameObject>();
        public Object Asset;
        public string Message = "";
        /// <summary>"warning" when something did not land, "info" for a tip.</summary>
        public string MessageKind = "warning";
    }

    /// <summary>
    /// Puts imported results to work: on the target object when there is one, else
    /// as a new object at the Scene view pivot. Every scene change is undoable.
    /// </summary>
    public static class Placer
    {
        /// <summary>What a result of this type would do with this target, in words ("" when it cannot use it).</summary>
        public static string Describe(string outputType, GameObject target)
        {
            if (target == null) return "";
            switch (outputType)
            {
                case OutputTypes.Image:
                    if (Has(target, "SpriteRenderer")) return $"sprite on {target.name}";
                    if (Has(target, "Image") || Has(target, "RawImage")) return $"image on {target.name}";
                    if (target.GetComponent<Renderer>() != null) return $"texture on {target.name}";
                    return "";
                case OutputTypes.Material:
                    return target.GetComponentInChildren<Renderer>() != null ? $"material on {target.name}" : "";
                case OutputTypes.Video:
                    return target.GetComponent<VideoPlayer>() != null || target.GetComponent<Renderer>() != null ? $"video on {target.name}" : "";
                case OutputTypes.Audio:
                    return $"sound on {target.name}";
                default:
                    return "";
            }
        }

        static bool Has(GameObject target, string typeName) => target.GetComponents<Component>().Any(c => c != null && c.GetType().Name == typeName);

        public static PlaceResult Place(string outputType, List<ImportedFile> files, string prompt, GameObject target, bool create)
        {
            var result = new PlaceResult();
            if (files == null || files.Count == 0) return result;
            var main = Importer.Main(files);
            var name = Files.ObjectName(prompt, "Scenario " + OutputTypes.Get(outputType).Title);
            switch (outputType)
            {
                case OutputTypes.Model3D:
                case OutputTypes.Motion:
                    if (main.Kind == "splat")
                    {
                        result.Asset = AssetDatabase.LoadMainAssetAtPath(main.Path);
                        result.Message = "Gaussian splat saved. Unity needs a Gaussian splat renderer package to show it.";
                    }
                    else if (create || target != null) PlaceModel(main.Path, name, target, result);
                    break;
                case OutputTypes.Image:
                    PlaceImage(main.Path, name, target, create, result);
                    break;
                case OutputTypes.Video:
                    PlaceVideo(main.Path, name, target, create, result);
                    break;
                case OutputTypes.Audio:
                    PlaceAudio(main.Path, name, target, create, result);
                    break;
                case OutputTypes.Material:
                    PlaceMaterial(files, name, target, create, result);
                    break;
                case OutputTypes.Skybox:
                    var (material, message) = SkyboxBuilder.Apply(main.Path, create || target != null);
                    result.Asset = material;
                    result.Message = message;
                    break;
                default:
                    // "All models": land by the asset's own kind.
                    var kindType = main.Kind == "3d" || main.Kind == "splat" ? OutputTypes.Model3D : OutputTypes.TypeForKind(main.Kind);
                    if (kindType != OutputTypes.All) return Place(kindType, files, prompt, target, create);
                    break;
            }
            if (result.Objects.Count > 0)
            {
                Selection.objects = result.Objects.Cast<Object>().ToArray();
                EditorGUIUtility.PingObject(result.Objects[0]);
                if (ScenarioProjectSettings.instance.FrameAfterPlace && SceneView.lastActiveSceneView != null && !Application.isBatchMode)
                    SceneView.lastActiveSceneView.FrameSelected();
            }
            else if (result.Asset == null)
            {
                result.Asset = AssetDatabase.LoadMainAssetAtPath(main.Path);
            }
            return result;
        }

        /// <summary>Hierarchy path of a scene object ("Root/Child").</summary>
        public static string PathOf(GameObject go)
        {
            if (go == null) return "";
            var names = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names);
        }

        /// <summary>The object at a hierarchy path ("Parent/Child") in the loaded scenes, or null.</summary>
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Trim('/').Split('/');
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    var current = root.transform;
                    for (var p = 1; p < parts.Length && current != null; p++)
                    {
                        Transform next = null;
                        foreach (Transform child in current)
                            if (child.name == parts[p])
                            {
                                next = child;
                                break;
                            }
                        current = next;
                    }
                    if (current != null) return current.gameObject;
                }
            }
            return null;
        }

        public static Vector3 Pivot()
        {
            var view = SceneView.lastActiveSceneView;
            return view != null ? view.pivot : Vector3.zero;
        }

        static void PlaceModel(string path, string name, GameObject target, PlaceResult result)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                result.Message = "The model was imported but Unity could not load it as a prefab.";
                return;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = name;
            var notes = new List<string>();
            // glTF is in meters; a character tens of meters tall was exported in centimeters.
            if (instance.GetComponentInChildren<SkinnedMeshRenderer>() != null && Bounds(instance).size.y > 50)
            {
                instance.transform.localScale *= 0.01f;
                notes.Add("Scaled to 1%: the file is in centimeters.");
            }
            // Untextured results can come without a material, which Unity draws magenta.
            var fallback = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.defaultMaterial : AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
                if (fallback != null && renderer.sharedMaterials.All(m => m == null))
                    renderer.sharedMaterials = Enumerable.Repeat(fallback, Mathf.Max(1, renderer.sharedMaterials.Length)).ToArray();
            var position = target != null ? target.transform.position + Vector3.right * Size(target) : Pivot();
            instance.transform.position = position;
            // Sit the model on the point instead of around it.
            var bounds = Bounds(instance);
            if (bounds.size != Vector3.zero) instance.transform.position += Vector3.up * (position.y - bounds.min.y);
            Undo.RegisterCreatedObjectUndo(instance, "Place Scenario model");
            if (Animations.Attach(instance, path) != null)
            {
                notes.Add("Animated: press Play to see it move.");
                result.MessageKind = "info";
            }
            if (notes.Count > 0) result.Message = string.Join(" ", notes);
            if (ScenarioProjectSettings.instance.AddCollider)
                foreach (var filter in instance.GetComponentsInChildren<MeshFilter>())
                    if (filter.sharedMesh != null && filter.GetComponent<Collider>() == null)
                        Undo.AddComponent<MeshCollider>(filter.gameObject).sharedMesh = filter.sharedMesh;
            result.Objects.Add(instance);
        }

        static float Size(GameObject target)
        {
            var bounds = Bounds(target);
            return bounds.size == Vector3.zero ? 1.5f : Mathf.Max(bounds.size.x, bounds.size.z) + 0.5f;
        }

        static Bounds Bounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.zero);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        static void PlaceImage(string path, string name, GameObject target, bool create, PlaceResult result)
        {
            var sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (target != null)
            {
                if (SetObjectReference(target, "SpriteRenderer", "m_Sprite", EnsureSprite(path, ref sprite))
                    || SetObjectReference(target, "Image", "m_Sprite", EnsureSprite(path, ref sprite))
                    || SetObjectReference(target, "RawImage", "m_Texture", texture))
                {
                    result.Objects.Add(target);
                    return;
                }
                var renderer = target.GetComponent<Renderer>();
                if (renderer != null && texture != null)
                {
                    var material = MaterialBuilder.Build(new Dictionary<string, string> { ["albedo"] = path }, Path.ChangeExtension(path, ".mat"));
                    Undo.RecordObject(renderer, "Apply Scenario image");
                    renderer.sharedMaterial = material;
                    result.Objects.Add(target);
                    return;
                }
            }
            if (!create || texture == null) return;
            GameObject created;
            if (sprite != null || EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D)
            {
                created = new GameObject(name);
                var spriteRenderer = created.AddComponent<SpriteRenderer>();
                spriteRenderer.sprite = EnsureSprite(path, ref sprite);
                var pivot = Pivot();
                created.transform.position = new Vector3(pivot.x, pivot.y, 0);
            }
            else
            {
                created = GameObject.CreatePrimitive(PrimitiveType.Quad);
                created.name = name;
                Object.DestroyImmediate(created.GetComponent<Collider>());
                var aspect = texture.height > 0 ? (float)texture.width / texture.height : 1;
                created.transform.localScale = new Vector3(aspect * 2, 2, 1);
                created.transform.position = Pivot() + Vector3.up;
                created.GetComponent<Renderer>().sharedMaterial = MaterialBuilder.Unlit(texture, Path.ChangeExtension(path, ".mat"));
            }
            Undo.RegisterCreatedObjectUndo(created, "Place Scenario image");
            result.Objects.Add(created);
        }

        /// <summary>Switches a texture to Sprite import when a sprite is needed.</summary>
        static Sprite EnsureSprite(string path, ref Sprite sprite)
        {
            if (sprite != null) return sprite;
            if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }
            sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
            return sprite;
        }

        /// <summary>Sets a serialized object field on a component found by type name (no UI package reference needed).</summary>
        static bool SetObjectReference(GameObject target, string componentType, string property, Object value)
        {
            if (value == null) return false;
            var component = target.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == componentType);
            if (component == null) return false;
            var serialized = new SerializedObject(component);
            var field = serialized.FindProperty(property);
            if (field == null) return false;
            Undo.RecordObject(component, "Apply Scenario image");
            field.objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        static void PlaceVideo(string path, string name, GameObject target, bool create, PlaceResult result)
        {
            var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(path);
            if (clip == null)
            {
                result.Message = "Unity could not import this video. It stays in the project folder.";
                return;
            }
            GameObject host = target;
            if (host == null)
            {
                if (!create) return;
                host = GameObject.CreatePrimitive(PrimitiveType.Quad);
                host.name = name;
                Object.DestroyImmediate(host.GetComponent<Collider>());
                var aspect = clip.height > 0 ? (float)clip.width / clip.height : 16f / 9f;
                host.transform.localScale = new Vector3(aspect * 2, 2, 1);
                host.transform.position = Pivot() + Vector3.up;
                host.GetComponent<Renderer>().sharedMaterial = MaterialBuilder.Unlit(Texture2D.blackTexture, Path.ChangeExtension(path, ".mat"));
                Undo.RegisterCreatedObjectUndo(host, "Place Scenario video");
            }
            var player = host.GetComponent<VideoPlayer>();
            if (player == null) player = Undo.AddComponent<VideoPlayer>(host);
            else Undo.RecordObject(player, "Apply Scenario video");
            player.clip = clip;
            player.isLooping = true;
            player.playOnAwake = true;
            var renderer = host.GetComponent<Renderer>();
            if (renderer != null && player.renderMode != VideoRenderMode.RenderTexture)
            {
                player.renderMode = VideoRenderMode.MaterialOverride;
                player.targetMaterialRenderer = renderer;
            }
            result.Objects.Add(host);
        }

        static void PlaceAudio(string path, string name, GameObject target, bool create, PlaceResult result)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
            {
                result.Message = "Unity could not import this audio format. It stays in the project folder.";
                return;
            }
            var host = target;
            if (host == null)
            {
                if (!create) return;
                host = new GameObject(name);
                host.transform.position = Pivot();
                Undo.RegisterCreatedObjectUndo(host, "Place Scenario audio");
            }
            var source = host.GetComponent<AudioSource>();
            if (source == null) source = Undo.AddComponent<AudioSource>(host);
            else Undo.RecordObject(source, "Apply Scenario audio");
            source.clip = clip;
            source.playOnAwake = false;
            // Spatial in 3D scenes, flat in 2D ones (and on UI or music hosts the user picked).
            if (target == null) source.spatialBlend = EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D ? 0f : 1f;
            result.Objects.Add(host);
        }

        static void PlaceMaterial(List<ImportedFile> files, string name, GameObject target, bool create, PlaceResult result)
        {
            var maps = new Dictionary<string, string>();
            foreach (var file in files.Where(f => f.Kind == "image" || f.Kind == ""))
                if (!maps.ContainsKey(file.Role)) maps[file.Role] = file.Path;
            if (maps.Count == 0) return;
            // Named after the color map, which carries the result's id: prompt-id.mat.
            var main = maps.TryGetValue("albedo", out var albedo) ? albedo : maps.TryGetValue("base", out var b) ? b : maps.Values.First();
            var folder = Path.GetDirectoryName(main)?.Replace('\\', '/');
            var stem = Path.GetFileNameWithoutExtension(main);
            foreach (var suffix in new[] { "-albedo", "-base", "-main" })
                if (stem.EndsWith(suffix)) stem = stem.Substring(0, stem.Length - suffix.Length);
            var material = MaterialBuilder.Build(maps, $"{folder}/{stem}.mat");
            result.Asset = material;
            var renderers = new List<Renderer>();
            if (target != null) renderers.AddRange(target.GetComponentsInChildren<Renderer>());
            else if (create)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = name;
                sphere.transform.position = Pivot() + Vector3.up * 0.5f;
                Undo.RegisterCreatedObjectUndo(sphere, "Place Scenario material");
                renderers.Add(sphere.GetComponent<Renderer>());
            }
            foreach (var renderer in renderers)
            {
                Undo.RecordObject(renderer, "Apply Scenario material");
                renderer.sharedMaterial = material;
                if (!result.Objects.Contains(renderer.gameObject)) result.Objects.Add(renderer.gameObject);
            }
        }
    }
}
