using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): frame sequences of real Scenario results rendered by Unity, for the
    /// README and the handbook. Models turn once; animated characters play their clip in place (the hips
    /// stay over the spot, so a walk does not leave the frame). Needs the results of Accept3D.Import and the
    /// paid runs in Assets/Scenario. Frames go to Library/ScenarioDevLogs/media/name/0000.png with an
    /// info.json; tools/build_media.py turns them into the GIFs and videos in docs/images.
    /// Run with -batchmode -executeMethod ScenarioDev.Media.Run; SCENARIO_MEDIA=fox,knight renders only those.
    /// </summary>
    public static class Media
    {
        sealed class Show
        {
            public string Name, Path, State, Label;
            /// <summary>Instead of a path: the newest model file imported from this Scenario asset.</summary>
            public string Asset;
            /// <summary>Turntables: one turn in this time.</summary>
            public float Seconds = 4;
            public bool Turn => State == null;
        }

        const string Models = "Assets/Scenario/Models/2026-10-18/";
        const string Motions = "Assets/Scenario/Animations/2026-10-18/";

        static readonly Show[] Shows =
        {
            new Show { Name = "fox", Path = Models + "accept3d-creature-jbfmsf.glb", Label = "Generate · Meshy 7.1" },
            new Show { Name = "knight", Path = Models + "a-stylized-cartoon-knight-full-body-hdj4qe.glb", Label = "Generate · Meshy 7.1" },
            new Show { Name = "armor", Path = Models + "silver-plate-armor-with-deep-blue-cloth-wz2g2o.glb", Label = "Texture edit · Tencent" },
            new Show { Name = "adventurer", Path = Models + "accept3d-character-30k-ft6xxn.glb", Label = "Remesh to 30k · Meshy" },
            new Show { Name = "chest", Path = Models + "a-stylized-wooden-treasure-chest-game-83pjsb.glb", Label = "Generate · Rodin Gen-2.5" },
            new Show { Name = "chest-remesh", Path = Models + "accept3d-remesh-quad-v4tj2w.glb", Label = "Remesh to quads · Meshy" },
            new Show { Name = "chest-retexture", Path = Models + "accept3d-tripo-texturing-tnrpv7.glb", Label = "Retexture · Tripo 3.0" },
            new Show { Name = "chest-texture-edit", Path = Models + "accept3d-texture-edit-fbx-zcqgjz.glb", Label = "Texture edit · Tencent" },
            new Show { Name = "chest-prompt-edit", Path = Models + "accept3d-opus-edit-rdyxtn.glb", Label = "Edit with a prompt · Claude Opus 5.5 3D" },
            new Show { Name = "chest-lego", Path = Models + "accept3d-stylize-lego-y9uydq.glb", Label = "Stylize as LEGO · Tripo" },
            new Show { Name = "chest-voxel", Path = Models + "accept3d-stylize-voxel-lmanh5.glb", Label = "Stylize as voxels · Tripo" },
            new Show { Name = "chest-parts", Path = Models + "accept3d-hy-part-3aqgyh.glb", Label = "Split into parts · Hunyuan 3D Part" },
            new Show { Name = "chest-multicolor", Path = Models + "accept3d-multicolor-xs9ud5.glb", Label = "Multicolor · Hitem3D" },
            new Show { Name = "chest-retopo", Path = Models + "accept3d-polygen-gr38sf.glb", Label = "Retopology · Hunyuan Polygen" },
            new Show { Name = "tree-voxels", Path = Models + "accept3d-voxels-vlr9ea.glb", Label = "Generate · Voxel Crafter" },
            new Show { Name = "rig-meshy-walk", Path = Models + "accept3d-rig-meshy-u5mv7w.glb", State = "walk", Label = "Rig + walk · Meshy" },
            new Show { Name = "rig-meshy-run", Path = Models + "accept3d-rig-meshy-u5mv7w.glb", State = "run", Label = "Rig + run · Meshy" },
            new Show { Name = "rig-tripo-walk", Path = Models + "accept3d-rig-tripo-walk-hxmf6g.glb", State = "", Label = "Rig + walk · Tripo" },
            new Show { Name = "motion-library", Path = Motions + "accept3d-anim-meshy-y18xmq.glb", State = "", Label = "Motion library · Meshy" },
            new Show { Name = "motion-attack", Path = Motions + "attack-4yuedx.glb", State = "", Label = "Motion library · Meshy" },
            new Show { Name = "motion-cartwheel", Path = Motions + "accept3d-motion-cartwheel-dru2ws.glb", State = "", Label = "Text to motion · Cartwheel" },
            new Show { Name = "motion-uthana", Path = Motions + "accept3d-motion-uthana-dmdjfc.glb", State = "", Label = "Text to motion · Uthana" },
            new Show { Name = "motion-meshy", Path = Motions + "accept3d-meshy-motion-tofjg4.glb", State = "", Label = "Text to motion · Meshy" },
            new Show { Name = "run-jump-cartwheel", Asset = "asset_VD96zqP78q2rpaj53iX9KYNU", State = "", Label = "Text to motion · Cartwheel" },
            new Show { Name = "run-jump-uthana", Asset = "asset_vCotdBCwSqJ69Ud7JCka33fQ", State = "", Label = "Text to motion · Uthana" },
        };

        const string Out = "Library/ScenarioDevLogs/media";
        const int Size = 960;
        const int Fps = 24;
        static readonly Color Background = new Color(0.165f, 0.172f, 0.192f);

        static readonly string[] Only = (Environment.GetEnvironmentVariable("SCENARIO_MEDIA") ?? "")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();

        public static void Run() => EditorApplication.delayCall += () =>
        {
            var failures = 0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                Light();
                foreach (var show in Shows.Where(s => Only.Length == 0 || Only.Contains(s.Name)))
                {
                    try { Render(show); }
                    catch (Exception e)
                    {
                        failures++;
                        Debug.Log($"MEDIA FAIL {show.Name}: {e}");
                    }
                }
            }
            catch (Exception e)
            {
                failures++;
                Debug.Log("MEDIA FAIL " + e);
            }
            finally
            {
                Debug.Log($"MEDIA done failures={failures}");
                EditorApplication.Exit(failures > 0 ? 1 : 0);
            }
        };

        /// <summary>Key light from the front left (the default scene's light), a cool fill from the right, a rim from behind.</summary>
        static void Light()
        {
            var key = Object.FindFirstObjectByType<Light>();
            if (key != null)
            {
                key.transform.rotation = Quaternion.Euler(40, 150, 0);
                key.intensity = 1.2f;
                key.shadows = LightShadows.Soft;
            }
            Add("Fill", 0.4f, new Color(0.82f, 0.88f, 1f), Quaternion.Euler(12, 250, 0));
            Add("Rim", 0.7f, Color.white, Quaternion.Euler(25, 20, 0));
            static void Add(string name, float intensity, Color color, Quaternion rotation)
            {
                var light = new GameObject(name).AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = intensity;
                light.color = color;
                light.shadows = LightShadows.None;
                light.transform.rotation = rotation;
            }
        }

        static void Render(Show show)
        {
            if (show.Asset != null)
                show.Path = Provenance.AllPaths().Where(p => p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && Provenance.Read(p)?.AssetId == show.Asset)
                    .OrderByDescending(p => File.GetLastWriteTimeUtc(p)).FirstOrDefault() ?? show.Asset;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(show.Path);
            if (prefab == null) throw new FileNotFoundException(show.Path);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var dir = System.IO.Path.Combine(Out, show.Name);
            Directory.CreateDirectory(dir);
            try
            {
                foreach (var skinned in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skinned.updateWhenOffscreen = true;
                var info = Provenance.Read(show.Path);
                var meta = new JObject { ["name"] = show.Name, ["label"] = show.Label, ["file"] = show.Path, ["model"] = info?.ModelName ?? "", ["prompt"] = info?.Prompt ?? "" };
                var frames = show.Turn ? Turntable(go, show, dir) : Play(go, show, dir, meta);
                meta["frames"] = frames;
                meta["fps"] = Fps;
                File.WriteAllText(System.IO.Path.Combine(dir, "info.json"), meta.ToString());
                Debug.Log($"MEDIA OK {show.Name}: {frames} frames, {meta["clip"] ?? "turntable"}");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static int Turntable(GameObject go, Show show, string dir)
        {
            var bounds = Bounds(go);
            var center = bounds.center;
            var radius = bounds.extents.magnitude;
            var start = go.transform.position;
            var count = Mathf.RoundToInt(show.Seconds * Fps);
            for (var i = 0; i < count; i++)
            {
                // Starts on the front three-quarter view and turns once.
                var turn = Quaternion.Euler(0, 360f * i / count, 0);
                go.transform.SetPositionAndRotation(turn * (start - center) + center, turn);
                Save(Frame(center, radius, 1.0f), dir, i);
            }
            return count;
        }

        static int Play(GameObject go, Show show, string dir, JObject meta)
        {
            var controller = Animations.Ensure(show.Path) as AnimatorController;
            var clips = controller != null ? controller.layers[0].stateMachine.states.Select(s => (s.state.name, clip: s.state.motion as AnimationClip)).Where(s => s.clip != null).ToList() : new List<(string name, AnimationClip clip)>();
            var pick = clips.FirstOrDefault(c => show.State.Length > 0 && c.name.IndexOf(show.State, StringComparison.OrdinalIgnoreCase) >= 0);
            if (pick.clip == null) pick = clips.FirstOrDefault(c => c.clip.length >= 0.1f);
            if (pick.clip == null) throw new Exception("no clip; states: " + string.Join(", ", clips.Select(c => c.name)));
            var clip = pick.clip;
            meta["clip"] = pick.name;
            meta["length"] = Math.Round(clip.length, 2);
            var animator = go.GetComponentInChildren<Animator>(true);
            var target = animator != null ? animator.gameObject : go;
            var hips = Hips(go);
            var origin = go.transform.position;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var unbound = bindings.Where(b => AnimationUtility.GetAnimatedObject(target, b) == null).Select(b => b.path).Distinct().ToList();
            var skinnedBones = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(t => t != null).Distinct().ToList();
            var driven = new HashSet<string>(bindings.Select(b => b.path));
            var moved = skinnedBones.Count(t => driven.Contains(AnimationUtility.CalculateTransformPath(t, target.transform)));
            meta["bindings"] = $"{bindings.Length} curves, {unbound.Count} unbound paths ({string.Join(" | ", unbound.Take(3))}), {moved}/{skinnedBones.Count} skin bones driven, target {target.name}, hips {hips?.name}";

            // In place: after each pose, the model moves back so its hips stay over the start.
            Vector3 anchor = Vector3.zero;
            void Pose(float t)
            {
                go.transform.position = origin;
                clip.SampleAnimation(target, t);
                if (hips == null) return;
                // Relative to where the clip put the root: some clips move the root, others only the hips.
                var drift = hips.position - anchor;
                go.transform.position -= new Vector3(drift.x, 0, drift.z);
            }
            clip.SampleAnimation(target, 0);
            anchor = hips != null ? hips.position : Vector3.zero;

            // The whole clip, so the loop is seamless; long takes stop at 10 s.
            var seconds = Mathf.Min(clip.length, 10);
            var count = Mathf.Max(1, Mathf.RoundToInt(seconds * Fps));
            // One camera for the whole clip, framing the posed mesh over every pose (a skinned renderer's
            // own bounds only follow the pose in the player loop).
            var baked = Skins(go);
            Bounds? reach = null;
            for (var i = 0; i < count; i += Mathf.Max(1, count / 32))
            {
                Pose(i / (float)Fps);
                Bake(baked);
                foreach (var (_, proxy, _) in baked)
                {
                    var r = proxy.GetComponent<MeshRenderer>().bounds;
                    if (reach.HasValue) { var u = reach.Value; u.Encapsulate(r); reach = u; }
                    else reach = r;
                }
            }
            var bounds = reach ?? Bounds(go);
            meta["bounds"] = bounds.ToString("F2");
            var radius = Mathf.Max(bounds.extents.y * 1.05f, new Vector2(bounds.extents.x, bounds.extents.z).magnitude);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    Pose(i / (float)Fps);
                    Bake(baked);
                    Save(Frame(bounds.center, radius, 1.0f), dir, i);
                }
            }
            finally
            {
                Unbake(baked);
                Pose(0);
            }
            return count;
        }

        // Unity skins meshes in its player loop, which does not run between the renders of a batch loop:
        // each frame bakes the pose into a plain mesh drawn in place of the skinned one. BakeMesh ignores
        // an armature's scale, so a rig written in centimeters bakes 100 times too large: the proxy is
        // scaled by the power of ten that brings the baked height back to the skeleton's.
        internal static List<(SkinnedMeshRenderer skin, GameObject proxy, Mesh mesh)> Skins(GameObject go)
        {
            var list = new List<(SkinnedMeshRenderer, GameObject, Mesh)>();
            foreach (var skin in go.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.sharedMesh != null))
            {
                var proxy = new GameObject("Media Pose") { hideFlags = HideFlags.HideAndDontSave };
                var mesh = new Mesh { indexFormat = skin.sharedMesh.indexFormat };
                proxy.AddComponent<MeshFilter>().sharedMesh = mesh;
                proxy.AddComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
                skin.forceRenderingOff = true;
                skin.BakeMesh(mesh, true);
                mesh.RecalculateBounds();
                var bones = skin.bones.Where(b => b != null).Select(b => b.position.y).DefaultIfEmpty(0).ToList();
                var skeleton = bones.Max() - bones.Min();
                var baked = mesh.bounds.size.y * skin.transform.lossyScale.y;
                var power = skeleton > 1e-4f && baked > 1e-4f ? Mathf.Round(Mathf.Log10(baked / skeleton)) : 0;
                proxy.transform.localScale = skin.transform.lossyScale / Mathf.Pow(10, power);
                list.Add((skin, proxy, mesh));
            }
            return list;
        }

        internal static void Unbake(List<(SkinnedMeshRenderer skin, GameObject proxy, Mesh mesh)> baked)
        {
            foreach (var (skin, proxy, mesh) in baked)
            {
                skin.forceRenderingOff = false;
                Object.DestroyImmediate(proxy);
                Object.DestroyImmediate(mesh);
            }
        }

        internal static void Bake(List<(SkinnedMeshRenderer skin, GameObject proxy, Mesh mesh)> baked)
        {
            foreach (var (skin, proxy, mesh) in baked)
            {
                skin.BakeMesh(mesh, true);
                mesh.RecalculateBounds();
                proxy.transform.SetPositionAndRotation(skin.transform.position, skin.transform.rotation);
            }
        }

        static Transform Hips(GameObject go)
        {
            var all = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(s => s.bones).Where(b => b != null).Distinct().ToList();
            return all.FirstOrDefault(b => b.name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0 || b.name.IndexOf("pelvis", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s => s.rootBone).FirstOrDefault(b => b != null);
        }

        static Texture2D Frame(Vector3 center, float radius, float margin)
        {
            var holder = new GameObject("Media Camera");
            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            try
            {
                var camera = holder.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Background;
                camera.fieldOfView = 30;
                // Front three-quarter view, a little above: glTF models face +Z.
                var direction = new Vector3(0.42f, 0.22f, 0.88f).normalized;
                var distance = Mathf.Max(0.01f, radius) / Mathf.Sin(15 * Mathf.Deg2Rad) * margin;
                camera.transform.position = center + direction * distance;
                camera.transform.LookAt(center);
                camera.nearClipPlane = distance * 0.01f;
                camera.farClipPlane = distance * 4;
                camera.targetTexture = rt;
                camera.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var frame = new Texture2D(Size, Size, TextureFormat.RGB24, false);
                frame.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                frame.Apply();
                RenderTexture.active = previous;
                camera.targetTexture = null;
                return frame;
            }
            finally
            {
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(holder);
            }
        }

        static void Save(Texture2D frame, string dir, int index)
        {
            File.WriteAllBytes(System.IO.Path.Combine(dir, $"{index:0000}.png"), frame.EncodeToPNG());
            Object.DestroyImmediate(frame);
        }

        static Bounds Bounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            return bounds;
        }
    }
}
