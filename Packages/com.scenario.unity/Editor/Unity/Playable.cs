using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scenario.Editor
{
    /// <summary>
    /// Turns a rigged model in the scene into a character you play in third person: a
    /// player object with a CharacterController and ScenarioPlayer, an Animator Controller
    /// that blends idle, walk and run on speed and plays a jump, and a follow camera.
    /// Speeds come from the clips themselves when they travel, so feet do not slide.
    /// </summary>
    public static class Playable
    {
        public const string DefaultFolder = "Assets/Scenario/Playable";
        /// <summary>Where controllers are saved when a spec names no folder (tests point it elsewhere).</summary>
        internal static string Root = DefaultFolder;

        public sealed class Spec
        {
            public string Character = "";
            /// <summary>Per slot: an AnimationClip asset, a model file with a motion, or a state name in the character's controller. Empty: found by name.</summary>
            public string Idle = "", Walk = "", Run = "", Jump = "";
            /// <summary>Character height in meters; 0 keeps it when between 1.2 and 2.6 m, else 1.8 m.</summary>
            public float Height;
            public float WalkSpeed, RunSpeed;
            /// <summary>Degrees the model turns inside the player, for models that face away from +Z.</summary>
            public float Turn;
            public bool RunByDefault;
            /// <summary>An audio asset played at each step.</summary>
            public string Footstep = "";
            public bool Camera = true;
            public string Folder = "";
        }

        public sealed class Result
        {
            public GameObject Player;
            public ScenarioPlayer Component;
            public AnimatorController Controller;
            public Camera Camera;
            public float Height;
            public Dictionary<string, string> Clips = new Dictionary<string, string>();
            public List<string> Notes = new List<string>();
        }

        static readonly Dictionary<string, string[]> Keywords = new Dictionary<string, string[]>
        {
            ["idle"] = new[] { "idle", "stand", "breath", "rest" },
            ["walk"] = new[] { "walk" },
            ["run"] = new[] { "run", "jog", "sprint" },
            ["jump"] = new[] { "jump", "leap", "hop" },
        };

        public static Result Make(Spec spec)
        {
            var character = Placer.FindByPath(spec.Character);
            if (character == null) throw new ArgumentException($"No object at \"{spec.Character}\".");
            if (character.GetComponentInParent<ScenarioPlayer>() != null) throw new ArgumentException($"{character.name} is already playable.");
            var animator = character.GetComponentInChildren<Animator>();
            if (animator == null)
            {
                if (character.GetComponentInChildren<SkinnedMeshRenderer>() == null)
                    throw new ArgumentException($"{character.name} has no skeleton. Rig it first (the 3D Rig step), then animate it.");
                animator = Undo.AddComponent<Animator>(character);
            }
            var result = new Result();

            // Size first: travel speeds are measured on the scaled character.
            var bounds = World.BoundsOf(character);
            var height = spec.Height > 0 ? spec.Height : bounds.size.y >= 1.2f && bounds.size.y <= 2.6f ? bounds.size.y : 1.8f;
            if (Mathf.Abs(bounds.size.y - height) > 0.01f)
            {
                if (spec.Height <= 0) result.Notes.Add($"Scaled from {bounds.size.y:0.##} m to {height:0.##} m tall.");
                World.FitHeight(character, height);
                bounds = World.BoundsOf(character);
            }
            result.Height = height;

            var folder = (spec.Folder.Length > 0 ? spec.Folder : Root).TrimEnd('/');
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var controllerPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Core.Files.Slug(character.name)}.controller");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);

            var clips = new Dictionary<string, AnimationClip>();
            foreach (var (slot, value) in new[] { ("idle", spec.Idle), ("walk", spec.Walk), ("run", spec.Run), ("jump", spec.Jump) })
            {
                var clip = Resolve(value, slot, animator, controller, result.Notes);
                if (clip == null) continue;
                clips[slot] = Own(clip, slot, controller);
                result.Clips[slot] = value.Length > 0 ? value : clip.name;
            }
            if (!clips.ContainsKey("walk") && clips.ContainsKey("run")) clips["walk"] = clips["run"];
            if (!clips.ContainsKey("walk")) throw new ArgumentException($"No walk motion for {character.name}. Animate it with a walk (the Animate step or motions_find), then pass walk.");
            if (!clips.ContainsKey("idle"))
            {
                clips["idle"] = Pose(clips["walk"], controller);
                result.Notes.Add("No idle motion: the character stands in the walk's first pose.");
            }

            // Clips that travel set the speed and play in place; clips that stay put use defaults.
            var bone = "";
            float Travel(string slot)
            {
                if (!clips.TryGetValue(slot, out var clip)) return 0;
                var (path, meters) = Travelling(animator, clip);
                if (meters / Mathf.Max(0.01f, clip.length) > 0.3f && bone.Length == 0) bone = path;
                return meters / Mathf.Max(0.01f, clip.length);
            }
            var walkTravel = Travel("walk");
            var runTravel = clips.ContainsKey("run") ? Travel("run") : 0;
            Travel("jump");
            var walkSpeed = spec.WalkSpeed > 0 ? spec.WalkSpeed : walkTravel > 0.3f ? walkTravel : 1.6f * height / 1.8f;
            var runSpeed = spec.RunSpeed > 0 ? spec.RunSpeed : runTravel > 0.3f ? runTravel : walkSpeed * 2.8f;
            if (runSpeed <= walkSpeed + 0.1f) runSpeed = walkSpeed * 2f;

            Build(controller, clips, walkSpeed, runSpeed, height);
            AssetDatabase.SaveAssets();
            Undo.RecordObject(animator, "Make playable");
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            // The player: a parent at the feet with unit scale, so the controller's sizes read in meters.
            var feet = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            var player = new GameObject(character.name + " Player") { tag = "Player" };
            Undo.RegisterCreatedObjectUndo(player, "Make playable");
            player.transform.SetParent(character.transform.parent, false);
            player.transform.SetPositionAndRotation(feet, Quaternion.Euler(0, character.transform.eulerAngles.y + spec.Turn, 0));
            Undo.SetTransformParent(character.transform, player.transform, "Make playable");
            if (Mathf.Abs(spec.Turn) > 0.01f) character.transform.Rotate(0, -spec.Turn, 0, Space.World);

            var body = Undo.AddComponent<CharacterController>(player);
            body.skinWidth = 0.04f;
            body.height = height;
            body.radius = Mathf.Clamp(Mathf.Min(bounds.size.x, bounds.size.z) * 0.35f, 0.18f, 0.5f);
            body.center = new Vector3(0, height / 2 + body.skinWidth, 0);
            body.stepOffset = Mathf.Min(0.4f, height * 0.2f);
            body.slopeLimit = 50f;
            // At high frame rates a step is under the default 1 mm minimum and the controller would not move.
            body.minMoveDistance = 0f;

            var component = Undo.AddComponent<ScenarioPlayer>(player);
            component.animator = animator;
            component.walkSpeed = walkSpeed;
            component.runSpeed = runSpeed;
            component.runByDefault = spec.RunByDefault;
            component.jumpHeight = Mathf.Clamp(height * 0.6f, 0.6f, 2f);
            component.inPlaceBone = bone;
            if (spec.Footstep.Length > 0)
            {
                component.footstep = AssetDatabase.LoadAssetAtPath<AudioClip>(spec.Footstep);
                if (component.footstep == null) result.Notes.Add($"No audio clip at {spec.Footstep}: no footsteps.");
            }

            var ground = World.HeightAt(player.transform.position + Vector3.up * 0.5f);
            if (!float.IsNaN(ground)) player.transform.position = new Vector3(feet.x, ground, feet.z);
            else result.Notes.Add("No ground under the character: add a terrain or a floor with a collider, or it falls when Play starts.");

            if (spec.Camera) result.Camera = Follow(player.transform, height, component);

            result.Player = player;
            result.Component = component;
            result.Controller = controller;
            return result;
        }

        static AnimationClip Resolve(string value, string slot, Animator animator, AnimatorController container, List<string> notes)
        {
            AnimationClip clip = null;
            if (value.StartsWith("Assets/") || value.StartsWith("Packages/"))
            {
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(value)
                       ?? Animations.Clips(value).Where(c => c.length >= 0.1f).OrderByDescending(c => c.length).FirstOrDefault();
                if (clip == null) throw new ArgumentException($"No motion in {value}.");
                return Animations.Fit(clip, animator.transform, container);
            }
            var states = (animator.runtimeAnimatorController as AnimatorController)?.layers.SelectMany(l => l.stateMachine.states).Select(s => s.state).ToList()
                         ?? new List<AnimatorState>();
            if (value.Length > 0)
            {
                var state = states.FirstOrDefault(s => string.Equals(s.name, value, StringComparison.OrdinalIgnoreCase));
                clip = state?.motion as AnimationClip;
                if (clip == null) throw new ArgumentException($"No state named \"{value}\" with a motion in {animator.name}'s controller. Pass an asset path instead.");
                return clip;
            }
            foreach (var word in Keywords[slot])
            {
                var state = states.FirstOrDefault(s => s.motion is AnimationClip && s.name.ToLowerInvariant().Contains(word));
                if (state != null)
                {
                    notes.Add($"{Cap(slot)}: state \"{state.name}\".");
                    return (AnimationClip)state.motion;
                }
            }
            return null;
        }

        /// <summary>A copy stored in the controller, looping except the jump.</summary>
        static AnimationClip Own(AnimationClip clip, string slot, AnimatorController container)
        {
            var owned = AssetDatabase.GetAssetPath(clip) == AssetDatabase.GetAssetPath(container) ? clip : Object.Instantiate(clip);
            owned.name = Cap(slot);
            var settings = AnimationUtility.GetAnimationClipSettings(owned);
            settings.loopTime = slot != "jump";
            AnimationUtility.SetAnimationClipSettings(owned, settings);
            if (owned != clip) AssetDatabase.AddObjectToAsset(owned, container);
            return owned;
        }

        /// <summary>A still clip holding a motion's first frame.</summary>
        static AnimationClip Pose(AnimationClip from, AnimatorController container)
        {
            var pose = new AnimationClip { name = "Idle", frameRate = from.frameRate };
            var bindings = AnimationUtility.GetCurveBindings(from);
            AnimationUtility.SetEditorCurves(pose, bindings,
                bindings.Select(b => AnimationCurve.Constant(0, 1, AnimationUtility.GetEditorCurve(from, b).Evaluate(0))).ToArray());
            var settings = AnimationUtility.GetAnimationClipSettings(pose);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(pose, settings);
            AssetDatabase.AddObjectToAsset(pose, container);
            return pose;
        }

        /// <summary>The bone that travels most over the clip (path under the Animator) and how far it goes on the ground, in meters.</summary>
        static (string path, float meters) Travelling(Animator animator, AnimationClip clip)
        {
            var paths = AnimationUtility.GetCurveBindings(clip).Where(b => b.propertyName.StartsWith("m_LocalPosition") && b.path.Length > 0)
                .Select(b => b.path).Distinct().Where(p => animator.transform.Find(p) != null).ToList();
            if (paths.Count == 0) return ("", 0);
            var root = animator.gameObject;
            var start = new Dictionary<string, Vector3>();
            var best = ("", 0f);
            var wasSampling = AnimationMode.InAnimationMode();
            if (!wasSampling) AnimationMode.StartAnimationMode();
            try
            {
                foreach (var t in new[] { 0f, clip.length })
                {
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(root, clip, t);
                    AnimationMode.EndSampling();
                    foreach (var path in paths)
                    {
                        var position = animator.transform.Find(path).position;
                        if (t == 0) start[path] = position;
                        else
                        {
                            var moved = position - start[path];
                            moved.y = 0;
                            if (moved.magnitude > best.Item2) best = (path, moved.magnitude);
                        }
                    }
                }
            }
            finally
            {
                if (!wasSampling) AnimationMode.StopAnimationMode();
            }
            return best;
        }

        static void Build(AnimatorController controller, Dictionary<string, AnimationClip> clips, float walkSpeed, float runSpeed, float height)
        {
            controller.AddParameter(ScenarioPlayer.SpeedParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = ScenarioPlayer.GroundedParameter, type = AnimatorControllerParameterType.Bool, defaultBool = true });
            controller.AddParameter(ScenarioPlayer.JumpParameter, AnimatorControllerParameterType.Trigger);
            var machine = controller.layers[0].stateMachine;
            var locomotion = controller.CreateBlendTreeInController("Locomotion", out var tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = ScenarioPlayer.SpeedParameter;
            tree.useAutomaticThresholds = false;
            tree.AddChild(clips["idle"], 0);
            tree.AddChild(clips["walk"], walkSpeed);
            if (clips.TryGetValue("run", out var run) && run != clips["walk"]) tree.AddChild(run, runSpeed);
            machine.defaultState = locomotion;
            if (!clips.TryGetValue("jump", out var jumpClip)) return;
            var jump = machine.AddState("Jump");
            jump.motion = jumpClip;
            // Fit the clip to the time in the air plus a short landing.
            var jumpHeight = Mathf.Clamp(height * 0.6f, 0.6f, 2f);
            var airtime = 2f * Mathf.Sqrt(2f * jumpHeight / 20f);
            jump.speed = Mathf.Clamp(jumpClip.length / (airtime + 0.35f), 0.5f, 3f);
            var into = machine.AddAnyStateTransition(jump);
            into.AddCondition(AnimatorConditionMode.If, 0, ScenarioPlayer.JumpParameter);
            into.duration = 0.08f;
            into.canTransitionToSelf = false;
            var back = jump.AddTransition(locomotion);
            back.hasExitTime = true;
            back.exitTime = 0.85f;
            back.duration = 0.2f;
        }

        static Camera Follow(Transform target, float height, ScenarioPlayer player)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                Undo.RegisterCreatedObjectUndo(go, "Make playable");
                camera = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            var follow = camera.GetComponent<ScenarioFollowCamera>() ?? Undo.AddComponent<ScenarioFollowCamera>(camera.gameObject);
            Undo.RecordObject(follow, "Make playable");
            follow.target = target;
            follow.distance = Mathf.Max(3f, height * 2.5f);
            follow.lookHeight = height * 0.8f;
            Undo.RecordObject(camera.transform, "Make playable");
            follow.Snap();
            player.cameraTransform = camera.transform;
            return camera;
        }

        static string Cap(string slot) => char.ToUpperInvariant(slot[0]) + slot.Substring(1);
    }

    /// <summary>
    /// Play mode for the agent: enters it without a domain reload (so the bridge keeps
    /// serving), walks a ScenarioPlayer through waypoints, records frames, and puts the
    /// user's Enter Play Mode setting back when Play mode ends.
    /// </summary>
    public static class PlaySession
    {
        public sealed class WalkSpec
        {
            public string Player = "";
            public List<ScenarioAutopilot.Waypoint> Points = new List<ScenarioAutopilot.Waypoint>();
            /// <summary>Seconds of game time before the walk is called off.</summary>
            public float Timeout = 90f;
            public RecordSpec Record;
        }

        public sealed class RecordSpec
        {
            public string Folder = "";
            public string Camera = "";
            public int Width = 1920, Height = 1080, FrameRate = 30;
            public float Seconds;
        }

        static bool _saved;
        static bool _enabled;
        static EnterPlayModeOptions _options;

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.playModeStateChanged -= Changed;
            EditorApplication.playModeStateChanged += Changed;
        }

        static void Changed(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode) Restore();
        }

        static void Restore()
        {
            if (!_saved) return;
            EditorSettings.enterPlayModeOptionsEnabled = _enabled;
            EditorSettings.enterPlayModeOptions = _options;
            _saved = false;
        }

        public static async Task<bool> Start(float seconds = 120f)
        {
            if (EditorApplication.isPlaying) return true;
            if (!_saved)
            {
                _enabled = EditorSettings.enterPlayModeOptionsEnabled;
                _options = EditorSettings.enterPlayModeOptions;
                _saved = true;
            }
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = (_enabled ? _options : EnterPlayModeOptions.None) | EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.EnterPlaymode();
            var playing = await Until(() => EditorApplication.isPlaying, seconds);
            // Let Awake and Start run.
            if (playing) await Frames(3);
            return playing;
        }

        public static async Task<bool> Stop(float seconds = 60f)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Restore();
                return true;
            }
            EditorApplication.ExitPlaymode();
            var stopped = await Until(() => !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode, seconds);
            Restore();
            return stopped;
        }

        public static ScenarioPlayer FindPlayer(string path)
        {
            if (path.Length == 0) return Object.FindFirstObjectByType<ScenarioPlayer>();
            var go = Placer.FindByPath(path);
            return go == null ? null : go.GetComponentInChildren<ScenarioPlayer>();
        }

        public sealed class WalkResult
        {
            public List<float> ReachedAt = new List<float>();
            public int Falls, Jumps, Frames;
            public float Seconds, LongestStuck;
            public Vector3 Start, End;
            public bool TimedOut;
            public string Folder = "";
        }

        /// <summary>The autopilot of the walk in progress, or null.</summary>
        public static ScenarioAutopilot Walking { get; private set; }

        public static async Task<WalkResult> Walk(WalkSpec spec)
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Unity is not in Play mode: start it first.");
            var player = FindPlayer(spec.Player);
            if (player == null) throw new ArgumentException(spec.Player.Length > 0 ? $"No ScenarioPlayer at \"{spec.Player}\"." : "No ScenarioPlayer in the scene: call make_playable first.");
            var result = new WalkResult { Start = player.transform.position };
            var falls = player.Falls;
            var jumps = player.Jumps;
            var pilot = player.gameObject.AddComponent<ScenarioAutopilot>();
            pilot.waypoints = spec.Points;
            Walking = pilot;
            ScenarioFrameRecorder recorder = null;
            if (spec.Record != null) recorder = AddRecorder(spec.Record, out result.Folder);
            try
            {
                // Real-time guard: a recording runs slower than game time.
                var guard = spec.Timeout * (recorder != null ? 30f : 3f) + 30f;
                await Until(() => pilot == null || pilot.Done || pilot.Elapsed > spec.Timeout || !EditorApplication.isPlaying, guard);
                if (pilot == null || !EditorApplication.isPlaying) throw new InvalidOperationException("Play mode stopped during the walk.");
                result.TimedOut = !pilot.Done;
                result.ReachedAt.AddRange(pilot.ReachedAt);
                result.Seconds = pilot.Elapsed;
                result.LongestStuck = pilot.LongestStuck;
                result.Falls = player.Falls - falls;
                result.Jumps = player.Jumps - jumps;
                result.End = player.transform.position;
                if (recorder != null) result.Frames = recorder.Frames;
                return result;
            }
            finally
            {
                if (Walking == pilot) Walking = null;
                if (pilot != null) Object.Destroy(pilot);
                if (recorder != null) Object.Destroy(recorder);
            }
        }

        public static async Task<(int frames, string folder)> Record(RecordSpec spec)
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Unity is not in Play mode: start it first.");
            if (spec.Seconds <= 0) throw new ArgumentException("Give seconds.");
            var recorder = AddRecorder(spec, out var folder);
            try
            {
                await Until(() => recorder == null || recorder.Done || !EditorApplication.isPlaying, spec.Seconds * 30f + 30f);
                return (recorder != null ? recorder.Frames : 0, folder);
            }
            finally
            {
                if (recorder != null) Object.Destroy(recorder);
            }
        }

        static ScenarioFrameRecorder AddRecorder(RecordSpec spec, out string folder)
        {
            Camera camera;
            if (spec.Camera.Length > 0)
            {
                var go = Placer.FindByPath(spec.Camera);
                camera = go != null ? go.GetComponent<Camera>() : null;
                if (camera == null) throw new ArgumentException($"No camera at \"{spec.Camera}\".");
            }
            else camera = Camera.main;
            if (camera == null) throw new ArgumentException("No camera to record: tag one MainCamera or pass camera.");
            folder = spec.Folder.Length > 0 ? Path.GetFullPath(spec.Folder) : Path.GetFullPath(Path.Combine("Recordings", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            var recorder = camera.gameObject.AddComponent<ScenarioFrameRecorder>();
            recorder.source = camera;
            recorder.folder = folder;
            recorder.width = Mathf.Clamp(spec.Width, 64, 3840);
            recorder.height = Mathf.Clamp(spec.Height, 64, 2160);
            recorder.frameRate = Mathf.Clamp(spec.FrameRate, 1, 120);
            recorder.seconds = spec.Seconds;
            return recorder;
        }

        /// <summary>Completes when the condition holds or after this many real seconds; the value says which.</summary>
        public static Task<bool> Until(Func<bool> condition, float seconds)
        {
            var done = new TaskCompletionSource<bool>();
            var start = EditorApplication.timeSinceStartup;
            void Tick()
            {
                bool met;
                try
                {
                    met = condition();
                }
                catch (Exception e)
                {
                    EditorApplication.update -= Tick;
                    done.TrySetException(e);
                    return;
                }
                if (!met && EditorApplication.timeSinceStartup - start < seconds) return;
                EditorApplication.update -= Tick;
                done.TrySetResult(met);
            }
            EditorApplication.update += Tick;
            return done.Task;
        }

        public static Task Frames(int count)
        {
            var left = count;
            return Until(() => --left <= 0, 30f);
        }
    }
}
