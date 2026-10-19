using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>
    /// Animated models (rigging with a motion, text, video or library to motion): glTFast
    /// imports the clips, looping, and puts an Animator on the model without a controller.
    /// This builds the controller next to the file, one state per clip, so a placed
    /// instance plays in Play mode with nothing to set up.
    /// </summary>
    public static class Animations
    {
        public static string ControllerPath(string modelPath) => Path.ChangeExtension(modelPath, ".controller").Replace('\\', '/');

        public static AnimationClip[] Clips(string modelPath) =>
            AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();

        /// <summary>
        /// The model's controller, created on first use with its clips and those of other
        /// files on the same skeleton; null when there is no clip at all.
        /// </summary>
        public static RuntimeAnimatorController Ensure(string modelPath, IEnumerable<string> clipSources = null)
        {
            var path = ControllerPath(modelPath);
            var existing = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(path);
            if (existing != null) return existing;
            var own = Clips(modelPath);
            var borrowed = (clipSources ?? Enumerable.Empty<string>()).SelectMany(Clips).ToList();
            if (own.Length + borrowed.Count == 0) return null;
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var holder = prefab == null ? null : prefab.GetComponentInChildren<Animator>(true) != null ? prefab.GetComponentInChildren<Animator>(true).transform : prefab.transform;
            var clips = own.Concat(borrowed.Select(c => holder != null ? Fit(c, holder, controller) : c)).ToList();
            // A real motion is the default state; one-frame rest poses go last.
            var names = new HashSet<string>();
            foreach (var clip in clips.OrderBy(c => c.length < 0.1f ? 1 : 0))
            {
                var state = controller.AddMotion(clip);
                var name = StateName(clip.name, names.Count + 1);
                while (!names.Add(name)) name += " 2";
                state.name = name;
            }
            AssetDatabase.SaveAssets();
            return controller;
        }

        /// <summary>
        /// A clip from another file of the same rig, addressed to this model's hierarchy. Scenario imports
        /// give every file a scene root, so clips fit as they are. A file imported with glTFast's default
        /// has its single root node merged into the root when it has no clip, so "scene/Armature/Hip" in a
        /// walk file is "Armature/Hip" on the rigged model: a copy with the paths fixed is stored in the
        /// controller.
        /// </summary>
        internal static AnimationClip Fit(AnimationClip clip, Transform holder, Object container)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var paths = bindings.Select(b => b.path).Where(p => p.Length > 0).Distinct().ToList();
            bool Fits(System.Func<string, string> map)
            {
                var mapped = paths.Select(map).Where(p => p.Length > 0).ToList();
                return mapped.Count > 0 && mapped.All(p => holder.Find(p) != null);
            }
            if (paths.Count == 0 || Fits(p => p)) return clip;
            var candidates = new List<System.Func<string, string>> { p => p.IndexOf('/') >= 0 ? p.Substring(p.IndexOf('/') + 1) : "" };
            foreach (Transform child in holder)
            {
                var prefix = child.name + "/";
                candidates.Add(p => prefix + p);
            }
            var fix = candidates.FirstOrDefault(Fits);
            if (fix == null) return clip;
            var kept = bindings.Where(b => b.path.Length == 0 || fix(b.path).Length > 0).ToArray();
            var copy = new AnimationClip { name = clip.name, frameRate = clip.frameRate };
            AnimationUtility.SetEditorCurves(copy,
                kept.Select(b => { var moved = b; moved.path = b.path.Length == 0 ? "" : fix(b.path); return moved; }).ToArray(),
                kept.Select(b => AnimationUtility.GetEditorCurve(clip, b)).ToArray());
            AnimationUtility.SetAnimationClipSettings(copy, AnimationUtility.GetAnimationClipSettings(clip));
            AssetDatabase.AddObjectToAsset(copy, container);
            return copy;
        }

        /// <summary>"Armature|walking_man|baselayer" reads "Walking man"; machine names read "Motion 2".</summary>
        public static string StateName(string clipName, int index)
        {
            var parts = (clipName ?? "").Split('|').Select(p => p.Trim())
                .Where(p => p.Length > 0 && !Skip.Contains(p.ToLowerInvariant())).ToList();
            var name = (parts.Count > 0 ? parts[0] : "").Replace('_', ' ').Trim();
            var machine = name.Length == 0 || name.Length > 32 || name.Any(char.IsDigit) && name.Count(char.IsLetter) < name.Length / 2 || name.StartsWith("export");
            if (machine) return "Motion " + index;
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        static readonly HashSet<string> Skip = new HashSet<string> { "armature", "baselayer", "base layer", "reference", "smplh_animation", "animation" };

        /// <summary>Gives a placed model its controller. The Animator is on the glTF scene root.</summary>
        public static Animator Attach(GameObject instance, string modelPath)
        {
            var controller = Ensure(modelPath);
            if (controller == null) return null;
            var animator = instance.GetComponentInChildren<Animator>();
            if (animator == null) animator = Undo.AddComponent<Animator>(instance);
            Undo.RecordObject(animator, "Set Scenario animation");
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            return animator;
        }
    }
}
