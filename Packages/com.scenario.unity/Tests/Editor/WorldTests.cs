using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Agent;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// Walkable worlds: terrain from a heightmap with flat areas and painted layers,
    /// scattering on the ground, sizing and grounding, a rigged character made playable,
    /// and a playtest walk in Play mode.
    /// </summary>
    public class WorldTests
    {
        const string Folder = "Assets/ScenarioWorldTests";
        readonly List<GameObject> _made = new List<GameObject>();
        string _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = EditorJsonUtility.ToJson(ScenarioUserSettings.instance);
            ScenarioUserSettings.instance.AgentCanEditScene = true;
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
            World.Root = Folder + "/World";
            Playable.Root = Folder + "/Playable";
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made.Where(g => g != null)) Object.DestroyImmediate(go);
            foreach (var player in Object.FindObjectsByType<ScenarioPlayer>(FindObjectsSortMode.None)) Object.DestroyImmediate(player.gameObject);
            foreach (var terrain in Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None)) Object.DestroyImmediate(terrain.gameObject);
            foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t != null && t.parent == null && t.name.Contains("(scattered)")))
                Object.DestroyImmediate(go.gameObject);
            World.Root = World.DefaultFolder;
            Playable.Root = Playable.DefaultFolder;
            AssetDatabase.DeleteAsset(Folder);
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
        }

        static JObject Call(string name, JObject arguments) => Run.Sync(AgentTools.Call(name, arguments, new AgentTools.Context()));

        static string TextOf(JObject result) => (string)result["content"].First()["text"];

        /// <summary>A grayscale ramp: black on the left (-x), white on the right (+x).</summary>
        static string Ramp()
        {
            const int size = 64;
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var v = x / (float)(size - 1);
                image.SetPixel(x, y, new Color(v, v, v, 1));
            }
            var path = Folder + "/ramp.png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path);
            return path;
        }

        static string Swatch(string name, Color color)
        {
            var image = new Texture2D(4, 4);
            image.SetPixels(Enumerable.Repeat(color, 16).ToArray());
            var path = $"{Folder}/{name}.png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path);
            return path;
        }

        static float Ground(Terrain terrain, float x, float z) => terrain.SampleHeight(new Vector3(x, 0, z)) + terrain.transform.position.y;

        // --- Terrain -------------------------------------------------------------------------

        [Test]
        public void Terrain_FollowsTheHeightmap_LevelsFlatAreas_AndIsWalkable()
        {
            var result = World.CreateTerrain(new World.TerrainSpec
            {
                Heightmap = Ramp(),
                Size = new Vector3(100, 20, 100),
                Resolution = 129,
                Smoothing = 0,
                Flats = { new World.Flat { Center = new Vector2(-25, 0), Radius = 8, Height = 5 } },
            });
            var terrain = result.Terrain;
            Assert.Less(Ground(terrain, -48, 30), 2f, "black is low");
            Assert.Greater(Ground(terrain, 48, 30), 18f, "white is high");
            Assert.AreEqual(5f, Ground(terrain, -25, 0), 0.3f, "the flat area is at its height");
            Assert.AreEqual(5f, Ground(terrain, -21, 3), 0.3f, "the whole radius is flat");
            Assert.IsNotNull(terrain.GetComponent<TerrainCollider>());
            Assert.AreEqual(Ground(terrain, 30, -10), World.HeightAt(new Vector3(30, 0, -10)), 0.1f, "the collider is where the terrain is");
            Assert.AreEqual(new Vector3(-50, 0, -50), terrain.transform.position, "centered on the origin");
        }

        [Test]
        public void Terrain_PaintsLayersByHeight()
        {
            var terrain = World.CreateTerrain(new World.TerrainSpec
            {
                Heightmap = Ramp(),
                Size = new Vector3(100, 20, 100),
                Resolution = 129,
                Smoothing = 0,
                Layers =
                {
                    new World.Layer { Source = Swatch("grass", Color.green) },
                    new World.Layer { Source = Swatch("snow", Color.white), MinHeight = 0.6f },
                },
            }).Terrain;
            var data = terrain.terrainData;
            Assert.AreEqual(2, data.terrainLayers.Length);
            var n = data.alphamapResolution;
            var maps = data.GetAlphamaps(0, 0, n, n);
            Assert.AreEqual(1f, maps[n / 2, 2, 0], 0.01f, "low ground is the base layer");
            Assert.AreEqual(1f, maps[n / 2, n - 3, 1], 0.01f, "high ground is the high layer");
            Assert.AreEqual(1f, maps[n / 2, n / 2, 0] + maps[n / 2, n / 2, 1], 0.001f, "weights add up to one");
        }

        // --- Scatter --------------------------------------------------------------------------

        [Test]
        public void Scatter_SitsCopiesOnTheGround_AtTheirSize_OutsideKeepClearZones_AndRepeats()
        {
            var terrain = World.CreateTerrain(new World.TerrainSpec { Size = new Vector3(120, 15, 120), Resolution = 129, Seed = 3 }).Terrain;
            var rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rock.name = "Rock";
            rock.transform.position = new Vector3(1000, 0, 1000);
            _made.Add(rock);

            List<Vector3> Layout()
            {
                var result = World.Scatter(new World.ScatterSpec
                {
                    Source = "Rock",
                    Count = 25,
                    Area = new Vector2(60, 60),
                    On = terrain.name,
                    Height = new Vector2(1, 2),
                    KeepClear = { (Vector2.zero, 10f) },
                    Seed = 7,
                });
                _made.Add(result.Group);
                Assert.GreaterOrEqual(result.Placed, 15);
                var positions = new List<Vector3>();
                foreach (Transform copy in result.Group.transform)
                {
                    var bounds = World.BoundsOf(copy.gameObject);
                    Assert.That(bounds.size.y, Is.InRange(0.99f, 2.01f), copy.name + " height");
                    Assert.GreaterOrEqual(new Vector2(bounds.center.x, bounds.center.z).magnitude, 10f, copy.name + " is outside the keep-clear zone");
                    var ground = Ground(terrain, bounds.center.x, bounds.center.z);
                    Assert.That(bounds.min.y, Is.InRange(ground - 1.2f, ground + 0.05f), copy.name + " sits on the ground");
                    Assert.IsNotNull(copy.GetComponentInChildren<Collider>(), copy.name + " has a collider");
                    positions.Add(copy.position);
                }
                return positions;
            }

            var first = Layout();
            var second = Layout();
            Assert.AreEqual(first, second, "the same seed gives the same layout");
        }

        [Test]
        public void ObjectTools_SizeInMeters_AndSitOnTheGround()
        {
            World.CreateTerrain(new World.TerrainSpec { Size = new Vector3(60, 10, 60), Resolution = 65, Seed = 5 });
            var result = Call("object_create", new JObject
            {
                ["name"] = "Pillar",
                ["primitive"] = "cylinder",
                ["position"] = new JArray(5, 40, 5),
                ["height"] = 3,
                ["ground"] = true,
            });
            Assert.IsNull(result["isError"], TextOf(result));
            var pillar = Placer.FindByPath("Pillar");
            _made.Add(pillar);
            var bounds = World.BoundsOf(pillar);
            Assert.AreEqual(3f, bounds.size.y, 0.01f);
            Assert.AreEqual(World.HeightAt(new Vector3(5, 0, 5), Object.FindFirstObjectByType<Terrain>().gameObject), bounds.min.y, 0.3f);
        }

        [Test]
        public void TerrainAndScatterTools_ReportWhatTheyBuilt()
        {
            var terrain = Call("terrain_create", new JObject
            {
                ["name"] = "Island",
                ["size"] = new JArray(80, 12, 80),
                ["resolution"] = 129,
                ["edge_falloff"] = 0.2,
                ["flat_areas"] = new JArray(new JObject { ["center"] = new JArray(0, 0), ["radius"] = 6 }),
            });
            Assert.IsNull(terrain["isError"], TextOf(terrain));
            var data = JObject.Parse(TextOf(terrain));
            Assert.AreEqual("Island", (string)data["path"]);
            StringAssert.StartsWith(Folder, (string)data["data"]);

            var tree = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            tree.name = "Tree";
            tree.transform.position = new Vector3(500, 0, 500);
            _made.Add(tree);
            var scatter = Call("scatter", new JObject
            {
                ["source"] = "Tree",
                ["count"] = 8,
                ["on"] = "Island",
                ["area"] = new JArray(50, 50),
                ["height"] = new JArray(3, 5),
                ["collider"] = "box",
                ["name"] = "Trees",
            });
            Assert.IsNull(scatter["isError"], TextOf(scatter));
            var placed = JObject.Parse(TextOf(scatter));
            Assert.AreEqual("Trees", (string)placed["group"]);
            Assert.Greater((int)placed["placed"], 0);
            _made.Add(Placer.FindByPath("Trees"));

            var missing = Call("scatter", new JObject { ["source"] = "Nothing here" });
            Assert.IsTrue((bool)missing["isError"]);
            StringAssert.Contains("No object", TextOf(missing));
        }

        GameObject Slab(string name, Vector3 center, Vector3 size, float roll = 0)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetPositionAndRotation(center, Quaternion.Euler(0, 0, roll));
            go.transform.localScale = size;
            _made.Add(go);
            return go;
        }

        [Test]
        public void Scatter_MinSlope_DressesOnlyTheSteepGround()
        {
            // Flat ground west of x = 0, a 50 degree cliff face east of it.
            Slab("Floor", new Vector3(-10, -0.5f, 0), new Vector3(20, 1, 20));
            Slab("Cliff", new Vector3(10, 0, 0), new Vector3(20, 1, 20), 50);
            Slab("Boulder", new Vector3(1000, 0, 1000), new Vector3(0.6f, 0.6f, 0.6f));
            Physics.SyncTransforms();
            var result = Call("scatter", new JObject
            {
                ["source"] = "Boulder",
                ["count"] = 12,
                ["area"] = new JArray(36, 16),
                ["min_slope"] = 30,
                ["max_slope"] = 70,
                ["tilt"] = 45,
                ["name"] = "Cliff Rocks",
            });
            Assert.IsNull(result["isError"], TextOf(result));
            var data = JObject.Parse(TextOf(result));
            _made.Add(Placer.FindByPath("Cliff Rocks"));
            Assert.Greater((int)data["placed"], 0);
            Assert.Greater((int)data["rejected_spots"]["too_flat"], 0, "spots on the floor are turned down");
            foreach (Transform rock in Placer.FindByPath("Cliff Rocks").transform)
            {
                Assert.Greater(rock.position.x, 2f, rock.name + " is on the cliff");
                Assert.Greater(Vector3.Angle(rock.up, Vector3.up), 30f, rock.name + " leans with the cliff");
            }
        }

        // --- Set-piece collision -------------------------------------------------------------

        /// <summary>A generated set piece: a root with two meshes and no colliders, as models import.</summary>
        GameObject SetPiece(string name, Vector3 at)
        {
            var root = new GameObject(name);
            root.transform.position = at;
            _made.Add(root);
            foreach (var (part, offset, size) in new[] { ("Base", new Vector3(0, 0.5f, 0), new Vector3(6, 1, 6)), ("Column", new Vector3(2, 3, 2), new Vector3(1, 4, 1)) })
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.name = part;
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = offset;
                go.transform.localScale = size;
            }
            return root;
        }

        [Test]
        public void ObjectSet_Collider_MakesASetPieceSolid_Once()
        {
            var temple = SetPiece("Temple", new Vector3(0, 0, 0));
            var result = Call("object_set", new JObject { ["path"] = "Temple", ["collider"] = "mesh" });
            Assert.IsNull(result["isError"], TextOf(result));
            Assert.AreEqual(2, (int)JObject.Parse(TextOf(result))["colliders_added"]);
            Assert.AreEqual(2, temple.GetComponentsInChildren<MeshCollider>().Length);
            Physics.SyncTransforms();
            Assert.IsTrue(Physics.Raycast(new Vector3(2, 20, 2), Vector3.down, out var hit, 50f));
            Assert.AreEqual("Column", hit.collider.name, "a player can stand on the column top");
            Assert.AreEqual(5f, hit.point.y, 0.01f);

            var again = JObject.Parse(TextOf(Call("object_set", new JObject { ["path"] = "Temple", ["collider"] = "mesh" })));
            Assert.AreEqual(0, (int)again["colliders_added"]);
            StringAssert.Contains("already", (string)again["collider_note"]);
            Assert.AreEqual(2, temple.GetComponentsInChildren<Collider>().Length, "not doubled");

            var shrine = SetPiece("Shrine", new Vector3(20, 0, 0));
            Assert.IsNull(Call("object_set", new JObject { ["path"] = "Shrine", ["collider"] = "box" })["isError"]);
            var box = shrine.GetComponent<BoxCollider>();
            Assert.IsNotNull(box, "one box on the root");
            Assert.AreEqual(new Vector3(6, 5, 6), box.bounds.size);

            var wrong = Call("object_set", new JObject { ["path"] = "Shrine", ["collider"] = "sphere" });
            Assert.IsTrue((bool)wrong["isError"]);
        }

        // --- Playable character ----------------------------------------------------------------

        /// <summary>
        /// A stand-in rigged character: Hero (Animator) / Armature / Hips / Body, 1.8 m tall,
        /// with a walk that travels 1.2 m per second and a still idle, saved as clip assets.
        /// </summary>
        GameObject Hero(out string walk, out string idle, out string jump)
        {
            var hero = new GameObject("Hero");
            hero.AddComponent<Animator>();
            var armature = new GameObject("Armature").transform;
            armature.SetParent(hero.transform, false);
            var hips = new GameObject("Hips").transform;
            hips.SetParent(armature, false);
            hips.localPosition = new Vector3(0, 0.9f, 0);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.name = "Body";
            body.transform.SetParent(hips, false);
            body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            _made.Add(hero);

            AnimationClip Clip(string name, float travel, float length)
            {
                var clip = new AnimationClip { name = name };
                clip.SetCurve("Armature/Hips", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0, length, 0));
                clip.SetCurve("Armature/Hips", typeof(Transform), "localPosition.y", new AnimationCurve(new Keyframe(0, 0.9f), new Keyframe(length / 2, 0.95f), new Keyframe(length, 0.9f)));
                clip.SetCurve("Armature/Hips", typeof(Transform), "localPosition.z", AnimationCurve.Linear(0, 0, length, travel * length));
                return clip;
            }
            walk = Folder + "/walk.anim";
            idle = Folder + "/idle.anim";
            jump = Folder + "/jump.anim";
            AssetDatabase.CreateAsset(Clip("walk", 1.2f, 1f), walk);
            AssetDatabase.CreateAsset(Clip("idle", 0, 2f), idle);
            AssetDatabase.CreateAsset(Clip("jump", 0, 1f), jump);
            return hero;
        }

        GameObject Floor()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(10, 1, 10);
            _made.Add(floor);
            return floor;
        }

        [Test]
        public void MakePlayable_BuildsThePlayer_TheController_AndTheCamera()
        {
            Floor();
            if (Camera.main == null)
            {
                var camera = new GameObject("Main Camera") { tag = "MainCamera" };
                camera.AddComponent<Camera>();
                _made.Add(camera);
            }
            var hero = Hero(out var walk, out var idle, out var jump);
            hero.transform.position = new Vector3(2, 3, 2);

            var result = Playable.Make(new Playable.Spec { Character = "Hero", Walk = walk, Idle = idle, Jump = jump });

            Assert.AreEqual("Hero Player", result.Player.name);
            Assert.AreSame(result.Player.transform, hero.transform.parent);
            Assert.AreEqual("Player", result.Player.tag);
            Assert.IsNotNull(result.Player.GetComponent<CharacterController>());
            Assert.AreEqual(1.2f, result.Component.walkSpeed, 0.05f, "the walk's own speed");
            Assert.Greater(result.Component.runSpeed, result.Component.walkSpeed);
            Assert.AreEqual("Armature/Hips", result.Component.inPlaceBone, "the travelling bone plays in place");
            Assert.AreEqual(0f, result.Player.transform.position.y, 0.05f, "on the floor");
            Assert.AreEqual(result.Height, World.BoundsOf(hero).size.y, 0.05f);

            var controller = result.Controller;
            CollectionAssert.AreEquivalent(new[] { "Speed", "Grounded", "Jump" }, controller.parameters.Select(p => p.name));
            var machine = controller.layers[0].stateMachine;
            Assert.AreEqual("Locomotion", machine.defaultState.name);
            Assert.AreEqual(2, ((BlendTree)machine.defaultState.motion).children.Length, "idle and walk (no run given)");
            Assert.IsTrue(machine.states.Any(s => s.state.name == "Jump"));
            Assert.IsTrue(AnimationUtility.GetAnimationClipSettings(AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(controller)).OfType<AnimationClip>().First(c => c.name == "Walk")).loopTime);

            Assert.IsNotNull(result.Camera, "the main camera follows");
            Assert.IsTrue(result.Camera.CompareTag("MainCamera"));
            var follow = result.Camera.GetComponent<ScenarioFollowCamera>();
            Assert.IsNotNull(follow);
            Assert.AreSame(result.Player.transform, follow.target);
        }

        [Test]
        public void MakePlayable_RefusesAModelWithoutASkeleton()
        {
            var crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            crate.name = "Crate";
            _made.Add(crate);
            var result = Call("make_playable", new JObject { ["character"] = "Crate" });
            Assert.IsTrue((bool)result["isError"]);
            StringAssert.Contains("no skeleton", TextOf(result));
        }

        // --- Play mode -------------------------------------------------------------------------------

        static IEnumerator Wait(Task task, float seconds = 120)
        {
            var start = EditorApplication.timeSinceStartup;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup - start < seconds) yield return null;
            Assert.IsTrue(task.IsCompleted, "timed out");
            if (task.IsFaulted) throw task.Exception.InnerException ?? task.Exception;
        }

        [UnityTest]
        public IEnumerator Playtest_WalksRunsAndJumpsThroughWaypoints_WithoutFalling()
        {
            Floor();
            var camera = new GameObject("Main Camera") { tag = "MainCamera" };
            camera.AddComponent<Camera>();
            _made.Add(camera);
            Hero(out var walk, out var idle, out var jump);
            Playable.Make(new Playable.Spec { Character = "Hero", Walk = walk, Idle = idle, Jump = jump });
            var options = EditorSettings.enterPlayModeOptions;
            var enabled = EditorSettings.enterPlayModeOptionsEnabled;

            var start = PlaySession.Start();
            yield return Wait(start);
            Assert.IsTrue(start.Result, "entered Play mode");
            Assert.IsTrue(EditorApplication.isPlaying);
            Assert.IsTrue((EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0, "no domain reload, so the bridge keeps serving");

            var frames = Path.Combine(Path.GetTempPath(), "scenario-unity-tests", "frames-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            var walkTask = PlaySession.Walk(new PlaySession.WalkSpec
            {
                Points =
                {
                    new ScenarioAutopilot.Waypoint { position = new Vector3(0, 0, 4) },
                    new ScenarioAutopilot.Waypoint { position = new Vector3(4, 0, 4), run = true, jump = true },
                    new ScenarioAutopilot.Waypoint { position = new Vector3(4, 0, 0), wait = 0.5f },
                },
                Timeout = 30,
                Record = new PlaySession.RecordSpec { Folder = frames, Width = 160, Height = 90, FrameRate = 30 },
            });
            yield return Wait(walkTask, 240);
            var result = walkTask.Result;
            Assert.AreEqual(3, result.ReachedAt.Count);
            Assert.IsTrue(result.ReachedAt.All(t => t >= 0), "every waypoint reached: " + string.Join(", ", result.ReachedAt));
            Assert.IsFalse(result.TimedOut);
            Assert.AreEqual(0, result.Falls);
            Assert.AreEqual(1, result.Jumps);
            Assert.AreEqual(4f, result.End.x, 0.7f);
            Assert.AreEqual(0f, result.End.z, 0.7f);
            Assert.Greater(result.Frames, 30);
            Assert.AreEqual(result.Frames, Directory.GetFiles(frames, "*.png").Length);
            Assert.AreEqual(0, Time.captureFramerate, "the recorder gives the frame rate back");

            var stop = PlaySession.Stop();
            yield return Wait(stop);
            Assert.IsFalse(EditorApplication.isPlaying);
            Assert.AreEqual(options, EditorSettings.enterPlayModeOptions, "the user's setting is back");
            Assert.AreEqual(enabled, EditorSettings.enterPlayModeOptionsEnabled);
            Directory.Delete(frames, true);
        }

        [UnityTest]
        public IEnumerator Player_CountsALongDropAsAFall()
        {
            Floor();
            var camera = new GameObject("Main Camera") { tag = "MainCamera" };
            camera.AddComponent<Camera>();
            _made.Add(camera);
            Hero(out var walk, out var idle, out var jump);
            Playable.Make(new Playable.Spec { Character = "Hero", Walk = walk, Idle = idle, Jump = jump });
            var start = PlaySession.Start();
            yield return Wait(start);
            var player = Object.FindFirstObjectByType<ScenarioPlayer>();
            Assert.AreEqual(0f, player.GetComponent<CharacterController>().minMoveDistance, "small steps at high frame rates still move");

            var body = player.GetComponent<CharacterController>();
            body.enabled = false;
            player.transform.position += Vector3.up * 8;
            body.enabled = true;
            yield return null;
            yield return null;
            var until = EditorApplication.timeSinceStartup + 10;
            while (!player.Grounded && EditorApplication.timeSinceStartup < until) yield return null;
            yield return null;
            Assert.IsTrue(player.Grounded, "landed");
            Assert.AreEqual(1, player.Falls, "an 8 m drop is a fall");
            Assert.AreEqual(0, player.Jumps);

            var stop = PlaySession.Stop();
            yield return Wait(stop);
        }

        static Task<JObject> PlayCall(JObject arguments) => AgentTools.Call("play", arguments, new AgentTools.Context());

        static JObject Route() => new JObject
        {
            ["action"] = "walk",
            ["waypoints"] = new JArray(new JObject { ["position"] = new JArray(0, 0, 6) }, new JObject { ["position"] = new JArray(0, 0, 0) }),
        };

        [UnityTest]
        public IEnumerator PlayTool_AnswersWithinItsTime_ThenStatusReturnsTheWalk()
        {
            Floor();
            var camera = new GameObject("Main Camera") { tag = "MainCamera" };
            camera.AddComponent<Camera>();
            _made.Add(camera);
            Hero(out var walk, out var idle, out var jump);
            Playable.Make(new Playable.Spec { Character = "Hero", Walk = walk, Idle = idle, Jump = jump });
            var start = PlaySession.Start();
            yield return Wait(start);
            Assert.IsTrue(start.Result, "entered Play mode");

            var budget = AgentTools.CallSeconds;
            AgentTools.CallSeconds = 1;
            try
            {
                // About 10 s of walking, against a 1 s call: the call answers while the walk goes on.
                var walkCall = PlayCall(Route());
                yield return Wait(walkCall, 30);
                Assert.IsNull(walkCall.Result["isError"], TextOf(walkCall.Result));
                var answer = JObject.Parse(TextOf(walkCall.Result));
                Assert.AreEqual("walk", (string)answer["running"]);
                StringAssert.Contains("status", (string)answer["next"]);
                Assert.AreEqual(2, (int)answer["walk"]["of"]);

                var second = PlayCall(Route());
                yield return Wait(second, 30);
                Assert.IsTrue((bool)second.Result["isError"], "one walk at a time");

                JObject result = null;
                for (var i = 0; i < 60 && result == null; i++)
                {
                    var status = PlayCall(new JObject { ["action"] = "status", ["wait_seconds"] = 5 });
                    yield return Wait(status, 30);
                    var data = JObject.Parse(TextOf(status.Result));
                    if (data["reached"] != null) result = data;
                }
                Assert.IsNotNull(result, "status returned the walk's result");
                Assert.AreEqual(2, ((JArray)result["reached"]).Count);
                Assert.IsTrue(((JArray)result["reached"]).All(r => r["seconds"].Type != JTokenType.String), result.ToString());
                Assert.AreEqual(0, (int)result["falls"]);

                var after = PlayCall(new JObject { ["action"] = "status" });
                yield return Wait(after, 30);
                Assert.IsNull(JObject.Parse(TextOf(after.Result))["running"], "the result is returned once");
            }
            finally
            {
                AgentTools.CallSeconds = budget;
            }

            var stop = PlayCall(new JObject { ["action"] = "stop" });
            yield return Wait(stop);
            Assert.IsFalse(EditorApplication.isPlaying);
        }
    }
}
