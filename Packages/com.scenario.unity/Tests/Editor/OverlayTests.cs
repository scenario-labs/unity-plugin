using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// The Scene view panel, the Hierarchy menu and the palette's selection commands: what
    /// they offer for a placed 3D object, a terrain, an image or sprite and nothing selected,
    /// and the scene actions they run (each one Undo step). Nothing is sent to Scenario.
    /// </summary>
    public class OverlayTests
    {
        const string Folder = "Assets/ScenarioOverlayTests";
        readonly List<Object> _made = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
            Playable.Root = Folder + "/Playable";
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var player in Object.FindObjectsByType<ScenarioPlayer>(FindObjectsSortMode.None)) Object.DestroyImmediate(player.gameObject);
            foreach (var o in _made.Where(o => o != null)) Object.DestroyImmediate(o);
            Playable.Root = Playable.DefaultFolder;
            AssetDatabase.DeleteAsset(Folder);
        }

        T Made<T>(T o) where T : Object
        {
            _made.Add(o);
            return o;
        }

        GameObject Cube(string name, Vector3 position)
        {
            var cube = Made(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.name = name;
            cube.transform.position = position;
            return cube;
        }

        static string[] Ids(IEnumerable<AssetStep> steps) => steps.Select(s => s.Id).ToArray();

        // --- What is offered --------------------------------------------------------------------

        [Test]
        public void A3DObject_GetsTheStepsGroupedAsTheCatalog_AndSaysWhyItIsNotPlayable()
        {
            var cube = Cube("Overlay Crate", Vector3.zero);
            var context = SceneActions.For(cube);

            Assert.AreEqual(SelectionContext.Object3D, context.Mode);
            Assert.AreEqual("3d", context.Kind);
            CollectionAssert.AreEqual(new[] { "Texture", "Shape", "Rig and animate", "Parts and style" }, context.Groups.Select(g => g.Title));
            CollectionAssert.AreEqual(Ids(OutputTypes.StepsFor("3d")), Ids(context.Steps));
            Assert.AreSame(cube, context.Target);
            Assert.AreSame(cube, context.Source, "a scene object without a model file is exported as GLB when a step runs");
            Assert.AreEqual("", context.AssetId);
            Assert.IsFalse(context.CanMakePlayable);
            StringAssert.Contains("rig it first", context.PlayableIssue);

            var item = context.Item();
            Assert.AreEqual("3d", item.Kind);
            Assert.AreEqual("Overlay Crate", item.Label);

            // Hierarchy > Scenario > Next Step: the same steps.
            var menu = CreateView.StepsMenu(new GenericMenu(), context.Kind, _ => { });
            Assert.AreEqual(OutputTypes.StepsFor("3d").Count(), menu.GetItemCount());
        }

        [Test]
        public void APlacedScenarioModel_UsesItsScenarioAsset_AndItsRoot()
        {
            var path = Folder + "/crate.obj";
            File.WriteAllText(path, "o Crate\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvn 0 0 1\nf 1//1 2//1 3//1 4//1\n");
            AssetDatabase.ImportAsset(path);
            Provenance.Write(path, new ProvenanceInfo { AssetId = "asset_overlaycrate01", ModelId = "model_test" });
            var instance = Made((GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path)));
            var part = instance.GetComponentInChildren<MeshRenderer>().gameObject;

            var context = SceneActions.For(part);

            Assert.AreEqual(SelectionContext.Object3D, context.Mode);
            Assert.AreSame(instance, context.Target, "a part stands for the placed model");
            Assert.AreEqual(path, AssetDatabase.GetAssetPath(context.Source));
            Assert.AreEqual("asset_overlaycrate01", context.AssetId, "nothing to upload");
            Assert.AreEqual("asset_overlaycrate01", context.Item().AssetId);
        }

        [Test]
        public void ATerrain_GetsScatter_AndNoSteps()
        {
            var terrain = MakeTerrain();
            var context = SceneActions.For(terrain.gameObject);
            Assert.AreEqual(SelectionContext.TerrainMode, context.Mode);
            Assert.AreSame(terrain, context.Terrain);
            Assert.IsEmpty(context.Steps);
        }

        [Test]
        public void AnImageOrASprite_GetsTheImageSteps()
        {
            var path = Folder + "/swatch.png";
            var texture = new Texture2D(8, 8);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var image = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            var context = SceneActions.For(image);
            Assert.AreEqual(SelectionContext.Image, context.Mode);
            Assert.AreEqual("image", context.Kind);
            Assert.AreEqual(1, context.Groups.Count);
            CollectionAssert.AreEqual(Ids(OutputTypes.StepsFor("image")), Ids(context.Steps));
            CollectionAssert.IsSubsetOf(new[] { "edit", "remove-bg", "upscale", "to-3d", "to-material" }, Ids(context.Steps));
            Assert.AreSame(image, context.Source);

            var go = Made(new GameObject("Overlay Sprite"));
            var sprite = Made(Sprite.Create(image, new Rect(0, 0, 8, 8), Vector2.one * 0.5f));
            go.AddComponent<SpriteRenderer>().sprite = sprite;
            var scene = SceneActions.For(go);
            Assert.AreEqual(SelectionContext.Image, scene.Mode, "a sprite in the scene offers the image steps");
            Assert.AreSame(sprite, scene.Source);
            Assert.AreSame(go, scene.Target);
        }

        [Test]
        public void NothingSelected_OffersGenerateShortcuts()
        {
            var context = SceneActions.For(null);
            Assert.AreEqual(SelectionContext.Nothing, context.Mode);
            Assert.IsEmpty(context.Steps);
            CollectionAssert.AreEqual(new[] { OutputTypes.Image, OutputTypes.Model3D, OutputTypes.Material, OutputTypes.Skybox, OutputTypes.Audio }, SceneActions.Shortcuts);

            var empty = Made(new GameObject("Overlay Empty"));
            Assert.AreEqual(SelectionContext.Other, SceneActions.For(empty).Mode);
        }

        [Test]
        public void ThePanel_BuildsWhatEachSelectionOffers()
        {
            var panel = new ScenarioPanel();
            Assert.IsNotNull(panel.Q<TextField>("ask-field"), "Ask is always there");

            panel.Show(SceneActions.For(Cube("Overlay Crate", Vector3.zero)));
            Assert.AreEqual(4, panel.Query<Foldout>().ToList().Count);
            Assert.IsNotNull(panel.Q("step-retexture"));
            Assert.IsNotNull(panel.Q("step-rig"));
            Assert.IsFalse(panel.Q<Button>("action-playable").enabledSelf, "Make playable waits for a rig");
            StringAssert.Contains("rig it first", panel.Q<Label>("playable-issue").text);
            Assert.IsNotNull(panel.Q("action-ground"));
            Assert.IsNotNull(panel.Q("action-walkable"));
            Assert.IsNull(panel.Q("mood-apply"), "light and mood is for the scene, not an object");
            Assert.AreEqual(1f, panel.Q<FloatField>("height-field").value, 0.01f);
            Assert.AreEqual("Overlay Crate", panel.Q<Label>("panel-title").text);

            panel.Show(SceneActions.For(MakeTerrain().gameObject));
            Assert.IsNotNull(panel.Q("scatter-source"));
            Assert.IsNotNull(panel.Q("scatter-run"));
            Assert.IsNull(panel.Q("step-retexture"));
            Assert.IsNull(panel.Q("action-playable"));
            Assert.AreEqual("Golden hour", panel.Q<DropdownField>("mood-preset").value);

            panel.Show(SceneActions.For(null));
            foreach (var type in SceneActions.Shortcuts) Assert.IsNotNull(panel.Q("generate-" + type), type);
            Assert.IsNull(panel.Q("scatter-run"));
            Assert.IsNotNull(panel.Q("mood-apply"));
            Assert.IsNotNull(panel.Q<TextField>("ask-field"), "the request being typed survives");
        }

        // --- Scene actions --------------------------------------------------------------------------

        Terrain MakeTerrain()
        {
            var data = Made(new TerrainData { heightmapResolution = 33 });
            data.size = new Vector3(40, 10, 40);
            var go = Made(Terrain.CreateTerrainGameObject(data));
            go.name = "Overlay Terrain";
            go.transform.position = new Vector3(-20, 0, -20);
            return go.GetComponent<Terrain>();
        }

        [Test]
        public void PutOnTheGround_AndSetHeight_KeepTheBase_InOneUndoEach()
        {
            var floor = Made(GameObject.CreatePrimitive(PrimitiveType.Plane));
            floor.name = "Overlay Floor";
            floor.transform.localScale = new Vector3(5, 1, 5);
            var cube = Cube("Overlay Block", new Vector3(1, 5, 1));

            var grounded = SceneActions.PutOnGround(cube);
            Assert.IsTrue(grounded.Ok, grounded.Message);
            Assert.AreEqual(0f, World.BoundsOf(cube).min.y, 0.01f);

            var sized = SceneActions.SetHeight(cube, 3);
            Assert.IsTrue(sized.Ok, sized.Message);
            Assert.AreEqual(3f, SceneActions.Height(cube), 0.01f);
            Assert.AreEqual(0f, World.BoundsOf(cube).min.y, 0.01f, "the base stays on the floor");
            Assert.AreEqual(1f, World.BoundsOf(cube).center.x, 0.01f);

            Undo.PerformUndo();
            Assert.AreEqual(1f, SceneActions.Height(cube), 0.01f, "one Undo puts the size back");

            Assert.IsFalse(SceneActions.SetHeight(cube, 0).Ok);
            Object.DestroyImmediate(floor);
            var floating = SceneActions.PutOnGround(cube);
            Assert.IsFalse(floating.Ok);
            StringAssert.Contains("No ground", floating.Message);
        }

        [Test]
        public void MakeWalkable_LaysAWalkway_InOneUndo()
        {
            Cube("Overlay Bank West", new Vector3(-6, -0.5f, 0)).transform.localScale = new Vector3(4, 1, 4);
            Cube("Overlay Bank East", new Vector3(6, -0.5f, 0)).transform.localScale = new Vector3(4, 1, 4);
            var plank = Cube("Overlay Plank", new Vector3(0, 0.3f, 0));
            plank.transform.localScale = new Vector3(9, 0.2f, 2);

            var outcome = SceneActions.MakeWalkable(plank);
            Assert.IsTrue(outcome.Ok, outcome.Message);
            StringAssert.Contains("walkway with rails", outcome.Message);
            Assert.AreEqual("Overlay Plank Walkway", outcome.Object.name, "a stretched plank gets its walkway beside it");
            Assert.IsFalse(plank.GetComponent<BoxCollider>().enabled);

            Undo.PerformUndo();
            Assert.IsNull(GameObject.Find("Overlay Plank Walkway"), "one Undo removes the walkway");
            Assert.IsTrue(plank.GetComponent<BoxCollider>().enabled, "and turns the plank's collider back on");
            Assert.IsFalse(SceneActions.MakeWalkable(null).Ok);
        }

        [Test]
        public void SetMood_UnknownPreset_SaysWhichExist()
        {
            var outcome = SceneActions.SetMood("noon_on_mars");
            Assert.IsFalse(outcome.Ok);
            StringAssert.Contains("golden_hour", outcome.Message);
            CollectionAssert.Contains(SceneActions.Moods.ToList(), "misty_morning");
            Assert.AreEqual("Misty morning", SceneActions.MoodTitle("misty_morning"));
        }

        [Test]
        public void Scatter_PlacesCopiesOnTheTerrain()
        {
            var terrain = MakeTerrain();
            var template = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var prefab = PrefabUtility.SaveAsPrefabAsset(template, Folder + "/block.prefab");
            Object.DestroyImmediate(template);

            var outcome = SceneActions.Scatter(terrain, prefab, 6, 20, null, seed: 7);
            Assert.IsTrue(outcome.Ok, outcome.Message);
            var group = Made(outcome.Object);
            Assert.That(group.transform.childCount, Is.InRange(1, 6));
            foreach (Transform copy in group.transform)
            {
                Assert.AreEqual(0f, World.BoundsOf(copy.gameObject).min.y, 0.2f, "sat on the terrain");
                Assert.That(Mathf.Abs(copy.position.x), Is.LessThanOrEqualTo(11f), "inside the area around the terrain's center");
            }

            var nothing = SceneActions.Scatter(terrain, null, 6, 20);
            Assert.IsFalse(nothing.Ok);
            StringAssert.Contains("Pick a prefab", nothing.Message);
        }

        [Test]
        public void MakePlayable_NeedsAWalk_ThenBuildsThePlayer()
        {
            var hero = Made(new GameObject("Overlay Hero"));
            var animator = hero.AddComponent<Animator>();
            var hips = new GameObject("Hips").transform;
            hips.SetParent(new GameObject("Armature").transform, false);
            hips.parent.SetParent(hero.transform, false);
            hips.localPosition = new Vector3(0, 0.9f, 0);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(hips, false);
            body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);

            StringAssert.Contains("animate it first", SceneActions.For(hero).PlayableIssue, "a rig without a walk");

            var walk = new AnimationClip { name = "walk" };
            walk.SetCurve("Armature/Hips", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0, 1, 0));
            walk.SetCurve("Armature/Hips", typeof(Transform), "localPosition.y", AnimationCurve.Constant(0, 1, 0.9f));
            walk.SetCurve("Armature/Hips", typeof(Transform), "localPosition.z", AnimationCurve.Linear(0, 0, 1, 1.2f));
            AssetDatabase.CreateAsset(walk, Folder + "/walk.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/hero.controller");
            controller.AddMotion(walk).name = "Walk";
            animator.runtimeAnimatorController = controller;

            Assert.AreSame(hero, SceneActions.For(body).Target, "the body stands for the character");
            var context = SceneActions.For(hero);
            Assert.IsTrue(context.CanMakePlayable, context.PlayableIssue);

            var camera = Camera.main;
            if (camera == null)
            {
                camera = Made(new GameObject("Overlay Camera") { tag = "MainCamera" }).AddComponent<Camera>();
            }
            var hadFollow = camera.GetComponent<ScenarioFollowCamera>() != null;
            try
            {
                var outcome = SceneActions.MakePlayable(context.Target);
                Assert.IsTrue(outcome.Ok, outcome.Message);
                Assert.IsNotNull(outcome.Object.GetComponent<ScenarioPlayer>());
                Assert.AreSame(outcome.Object.transform, hero.transform.parent);
                StringAssert.Contains("Already playable", SceneActions.For(hero).PlayableIssue);
            }
            finally
            {
                if (!hadFollow && camera != null) Object.DestroyImmediate(camera.GetComponent<ScenarioFollowCamera>());
            }
        }
    }
}
