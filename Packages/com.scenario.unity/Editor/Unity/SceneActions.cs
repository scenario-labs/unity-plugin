using System;
using System.Collections.Generic;
using System.Linq;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace Scenario.Editor
{
    /// <summary>A titled list of steps, grouped as in the catalog.</summary>
    public sealed class StepGroup
    {
        public string Title = "";
        public readonly List<AssetStep> Steps = new List<AssetStep>();
    }

    /// <summary>What is selected and what applies to it: the steps, and the scene actions that run at once.</summary>
    public sealed class SelectionContext
    {
        public const string Nothing = "none";
        public const string Object3D = "object";
        public const string TerrainMode = "terrain";
        public const string Image = "image";
        public const string Asset = "asset";
        public const string Other = "other";

        public string Mode = Nothing;
        /// <summary>The step kind (image, 3d, video, audio), or "".</summary>
        public string Kind = "";
        public string Label = "";
        /// <summary>The scene object scene actions work on: a placed model's root, a terrain's object.</summary>
        public GameObject Target;
        public Terrain Terrain;
        /// <summary>What a step uploads: the model or image asset when there is one, else the scene object (as GLB).</summary>
        public Object Source;
        /// <summary>The Scenario asset id when Source came from Scenario: nothing is uploaded.</summary>
        public string AssetId = "";
        public readonly List<StepGroup> Groups = new List<StepGroup>();
        /// <summary>Empty when Make playable applies, else what is missing, in one line.</summary>
        public string PlayableIssue = "";

        public IEnumerable<AssetStep> Steps => Groups.SelectMany(g => g.Steps);
        public bool CanMakePlayable => Mode == Object3D && PlayableIssue.Length == 0;
        public SlotItem Item() => new SlotItem { AssetId = AssetId, Source = Source, Kind = Kind, Label = Label };

        /// <summary>Changes when what the overlay shows would change.</summary>
        public string Signature => $"{Mode}|{Kind}|{Label}|{(Target != null ? Target.GetInstanceID() : 0)}|{(Source != null ? Source.GetInstanceID() : 0)}|{PlayableIssue}";
    }

    /// <summary>
    /// The actions on the selection, shared by the Scene view overlay, the Hierarchy menu and the
    /// command palette: typed steps (they open Create, nothing is sent) and scene actions that run
    /// at once, each one Undo step.
    /// </summary>
    public static class SceneActions
    {
        /// <summary>Generate shortcuts when nothing is selected.</summary>
        public static readonly string[] Shortcuts = { OutputTypes.Image, OutputTypes.Model3D, OutputTypes.Material, OutputTypes.Skybox, OutputTypes.Audio };

        // Playable.Make looks for these words in the character's Animator states.
        static readonly string[] WalkWords = { "walk", "run", "jog", "sprint" };

        public static SelectionContext Current() => For(Selection.activeObject);

        public static SelectionContext For(Object selected)
        {
            var context = new SelectionContext();
            if (selected == null) return context;
            if (selected is GameObject go && !EditorUtility.IsPersistent(go)) return ForSceneObject(go, context);
            var path = AssetDatabase.GetAssetPath(selected);
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return context;
            context.Label = selected.name;
            var kind = InputSlot.KindOf(selected);
            if (kind.Length == 0)
            {
                context.Mode = SelectionContext.Other;
                return context;
            }
            context.Mode = kind == "image" ? SelectionContext.Image : SelectionContext.Asset;
            Fill(context, kind, selected);
            return context;
        }

        static SelectionContext ForSceneObject(GameObject go, SelectionContext context)
        {
            context.Label = go.name;
            context.Target = go;
            var terrain = go.GetComponent<Terrain>();
            if (terrain != null)
            {
                context.Mode = SelectionContext.TerrainMode;
                context.Terrain = terrain;
                return context;
            }
            var sprite = go.GetComponent<SpriteRenderer>();
            if (sprite != null && sprite.sprite != null)
            {
                context.Mode = SelectionContext.Image;
                Fill(context, "image", sprite.sprite);
                return context;
            }
            if (go.GetComponentsInChildren<Renderer>().Any(r => r is MeshRenderer || r is SkinnedMeshRenderer))
            {
                // A part of a placed model, or of a character, stands for the whole model.
                var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                if (root == null)
                {
                    var animator = go.GetComponentInParent<Animator>();
                    root = animator != null ? animator.gameObject : go;
                }
                context.Mode = SelectionContext.Object3D;
                context.Target = root;
                context.Label = root.name;
                Fill(context, "3d", ModelSource(root));
                context.PlayableIssue = PlayableIssue(root);
                return context;
            }
            var audio = go.GetComponent<AudioSource>();
            if (audio != null && audio.clip != null)
            {
                context.Mode = SelectionContext.Asset;
                Fill(context, "audio", audio.clip);
                return context;
            }
            var video = go.GetComponent<VideoPlayer>();
            if (video != null && video.clip != null)
            {
                context.Mode = SelectionContext.Asset;
                Fill(context, "video", video.clip);
                return context;
            }
            context.Mode = SelectionContext.Other;
            return context;
        }

        static void Fill(SelectionContext context, string kind, Object source)
        {
            context.Kind = kind;
            context.Source = source;
            context.AssetId = EditorUtility.IsPersistent(source) ? Provenance.Read(source)?.AssetId ?? "" : "";
            foreach (var step in OutputTypes.StepsFor(kind))
            {
                var title = step.Group.Length > 0 ? step.Group : "Next steps";
                var group = context.Groups.FirstOrDefault(g => g.Title == title);
                if (group == null) context.Groups.Add(group = new StepGroup { Title = title });
                group.Steps.Add(step);
            }
        }

        /// <summary>
        /// The model a placed object comes from: the imported file when it is a Scenario result or a
        /// file Scenario takes as is, else the object itself, exported to GLB when a step uploads it.
        /// </summary>
        static Object ModelSource(GameObject root)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(root)) return root;
            var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root);
            var path = original != null ? AssetDatabase.GetAssetPath(original) : "";
            if (path.Length == 0) return root;
            var info = Provenance.Read(path);
            if ((info != null && info.AssetId.Length > 0) || Files.UploadTypeFor(path) != null) return AssetDatabase.LoadMainAssetAtPath(path);
            return root;
        }

        /// <summary>Empty when Make playable can run on this object, else what is missing, in one line.</summary>
        public static string PlayableIssue(GameObject go)
        {
            if (go == null) return "Select a character.";
            if (go.GetComponentInParent<ScenarioPlayer>() != null) return "Already playable: press Play.";
            var animator = go.GetComponentInChildren<Animator>();
            if (animator == null && go.GetComponentInChildren<SkinnedMeshRenderer>() == null) return "No skeleton: rig it first (Rig and animate).";
            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            var walks = controller != null && controller.layers.SelectMany(l => l.stateMachine.states)
                .Any(s => s.state.motion is AnimationClip && WalkWords.Any(w => s.state.name.ToLowerInvariant().Contains(w)));
            if (!walks) return "No walk yet: animate it first (Rig and animate).";
            if (Placer.FindByPath(Placer.PathOf(go)) != go) return "Another object has the same name and path: rename this one first.";
            return "";
        }

        // --- Steps ------------------------------------------------------------------------

        /// <summary>Opens Create with the step's model and the selection attached. Nothing is sent.</summary>
        public static void OpenStep(AssetStep step, SelectionContext context)
        {
            if (step == null || context == null || context.Source == null) return;
            ScenarioWindow.OpenView("create").Create.StartStep(step, context.Item());
        }

        public static void OpenType(string type)
        {
            var window = ScenarioWindow.OpenView("create");
            window.Create.SelectType(type);
            window.Create.FocusPrompt();
        }

        // --- Scene actions ----------------------------------------------------------------

        public struct Outcome
        {
            public bool Ok;
            public string Message;
            public GameObject Object;

            public static Outcome Done(string message, GameObject go = null) => new Outcome { Ok = true, Message = message, Object = go };
            public static Outcome Fail(string message) => new Outcome { Ok = false, Message = message };
        }

        static Outcome OneUndo(string name, Func<Outcome> action)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            var outcome = action();
            if (outcome.Ok) Undo.CollapseUndoOperations(group);
            else Undo.RevertAllDownToGroup(group);
            return outcome;
        }

        /// <summary>A third-person player from a rigged character with a walk (Playable.Make).</summary>
        public static Outcome MakePlayable(GameObject go)
        {
            var issue = PlayableIssue(go);
            if (issue.Length > 0) return Outcome.Fail(issue);
            return OneUndo("Make playable", () =>
            {
                try
                {
                    var result = Playable.Make(new Playable.Spec { Character = Placer.PathOf(go) });
                    var notes = result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "";
                    return Outcome.Done($"{go.name} is playable: press Play, then WASD to move, Shift to run, Space to jump.{notes}", result.Player);
                }
                catch (ArgumentException e)
                {
                    return Outcome.Fail(e.Message);
                }
            });
        }

        public static Outcome PutOnGround(GameObject go)
        {
            if (go == null) return Outcome.Fail("Select an object.");
            return OneUndo("Put on the ground", () => World.Ground(go)
                ? Outcome.Done($"{go.name} is on the ground.", go)
                : Outcome.Fail($"No ground under {go.name}: add a terrain or a floor with a collider below it."));
        }

        /// <summary>An invisible walkway with rails and ramps on a bridge, stairs or a platform (World.MakeWalkable).</summary>
        public static Outcome MakeWalkable(GameObject go)
        {
            if (go == null) return Outcome.Fail("Select a bridge, stairs or a platform.");
            return OneUndo("Make walkable", () =>
            {
                try
                {
                    var result = World.MakeWalkable(new World.WalkableSpec { Target = Placer.PathOf(go) });
                    var ramps = result.StartRamp && result.EndRamp ? ", ramps at both ends" : result.StartRamp || result.EndRamp ? ", a ramp at one end" : "";
                    var notes = result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "";
                    return Outcome.Done($"{go.name} is walkable: a {result.Length:0.#} m walkway with rails{ramps}.{notes}", result.Walkway);
                }
                catch (ArgumentException e)
                {
                    return Outcome.Fail(e.Message);
                }
            });
        }

        /// <summary>Light and mood presets, in the order the panel lists them.</summary>
        public static IEnumerable<string> Moods => Atmosphere.Presets.Keys;

        public static string MoodTitle(string preset) => preset.Length == 0 ? "" : char.ToUpperInvariant(preset[0]) + preset.Substring(1).Replace('_', ' ');

        /// <summary>A light and mood preset on the open scene: sun, fog, ambient light and, in URP, post-processing (Atmosphere.Apply).</summary>
        public static Outcome SetMood(string preset)
        {
            return OneUndo("Light and mood", () =>
            {
                try
                {
                    var result = Atmosphere.Apply(new Atmosphere.Spec { Preset = preset });
                    var notes = result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "";
                    return Outcome.Done($"Light and mood: {MoodTitle(preset).ToLowerInvariant()}.{notes}", result.Volume != null ? result.Volume : result.Sun.gameObject);
                }
                catch (ArgumentException e)
                {
                    return Outcome.Fail(e.Message);
                }
            });
        }

        public static float Height(GameObject go) => go == null ? 0 : World.BoundsOf(go).size.y;

        /// <summary>Scales the object to this height; its base stays where it was.</summary>
        public static Outcome SetHeight(GameObject go, float meters)
        {
            if (go == null) return Outcome.Fail("Select an object.");
            if (float.IsNaN(meters) || meters <= 0 || meters > 100000) return Outcome.Fail("Type a height in meters, above 0.");
            var before = World.BoundsOf(go);
            if (before.size.y <= 0) return Outcome.Fail($"{go.name} has no visible size to scale.");
            return OneUndo("Set height", () =>
            {
                World.FitHeight(go, meters);
                var after = World.BoundsOf(go);
                Undo.RecordObject(go.transform, "Set height");
                go.transform.position += new Vector3(before.center.x - after.center.x, before.min.y - after.min.y, before.center.z - after.center.z);
                return Outcome.Done($"{go.name} is {meters:0.##} m tall.", go);
            });
        }

        /// <summary>
        /// Copies of a prefab or model on a terrain, in a square around a point (the terrain's
        /// center when the point is off it), grounded, with colliders (World.Scatter).
        /// </summary>
        public static Outcome Scatter(Terrain terrain, Object source, int count, float area, Vector3? around = null, int seed = 0)
        {
            if (terrain == null || terrain.terrainData == null) return Outcome.Fail("Select a terrain.");
            var path = source != null ? AssetDatabase.GetAssetPath(source) : "";
            if (!(source is GameObject) || path.Length == 0) return Outcome.Fail("Pick a prefab or a 3D model from the Project window.");
            if (count < 1) return Outcome.Fail("Type how many copies to place.");
            count = Mathf.Min(count, 2000);
            var size = terrain.terrainData.size;
            var origin = terrain.transform.position;
            var center = origin + size * 0.5f;
            if (around.HasValue && around.Value.x >= origin.x && around.Value.x <= origin.x + size.x && around.Value.z >= origin.z && around.Value.z <= origin.z + size.z)
                center = around.Value;
            area = Mathf.Clamp(area, 1f, Mathf.Max(size.x, size.z));
            var terrainPath = Placer.PathOf(terrain.gameObject);
            return OneUndo("Scatter", () =>
            {
                try
                {
                    var result = World.Scatter(new World.ScatterSpec
                    {
                        Source = path,
                        Count = count,
                        Center = new Vector2(center.x, center.z),
                        Area = new Vector2(area, area),
                        On = Placer.FindByPath(terrainPath) == terrain.gameObject ? terrainPath : "",
                        Seed = seed != 0 ? seed : Environment.TickCount & 0x7fffffff,
                    });
                    if (result.Placed == 0) return Outcome.Fail($"No room for {source.name} there: try a larger area or a flatter spot.");
                    var note = result.Placed < count ? $" Only {result.Placed} of {count} fit: the area is small or steep." : "";
                    return Outcome.Done($"Placed {result.Placed} {source.name} on {terrain.name}.{note}", result.Group);
                }
                catch (ArgumentException e)
                {
                    return Outcome.Fail(e.Message);
                }
            });
        }

        /// <summary>The selection for Ask: hierarchy paths of scene objects, asset paths of assets.</summary>
        public static List<string> SelectionPaths()
        {
            var paths = new List<string>();
            foreach (var o in Selection.objects)
            {
                if (o is GameObject go && !EditorUtility.IsPersistent(go)) paths.Add(Placer.PathOf(go));
                else
                {
                    var path = AssetDatabase.GetAssetPath(o);
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
            }
            return paths.Distinct().Take(20).ToList();
        }

        /// <summary>Says how an action went: the console, and a notification in the Scene view.</summary>
        public static void Report(Outcome outcome)
        {
            if (outcome.Ok) Debug.Log("Scenario: " + outcome.Message);
            else Debug.LogWarning("Scenario: " + outcome.Message);
            if (!Application.isBatchMode && SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.ShowNotification(new GUIContent(outcome.Message), 3);
        }
    }
}
