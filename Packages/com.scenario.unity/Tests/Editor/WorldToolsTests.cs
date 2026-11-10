using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Agent;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// The tools that finish a walkable world: measuring the ground, making a generated bridge walkable,
    /// lining a path with props, the light and mood, and looking at the result from several angles.
    /// </summary>
    public class WorldToolsTests
    {
        const string Folder = "Assets/ScenarioWorldToolsTests";
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
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made.Where(g => g != null)) Object.DestroyImmediate(go);
            World.Root = World.DefaultFolder;
            AssetDatabase.DeleteAsset(Folder);
            EditorJsonUtility.FromJsonOverwrite(_settings, ScenarioUserSettings.instance);
        }

        static JObject Call(string name, JObject arguments) => Run.Sync(AgentTools.Call(name, arguments, new AgentTools.Context()));

        static string TextOf(JObject result) => (string)result["content"].Last()["text"];

        GameObject Box(string name, Vector3 center, Vector3 size, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = center;
            go.transform.localScale = size;
            if (parent != null) go.transform.SetParent(parent, true);
            else _made.Add(go);
            return go;
        }

        // --- ground_height --------------------------------------------------------------------

        [Test]
        public void GroundHeight_MeasuresPointsAndALine()
        {
            Box("Plateau", new Vector3(0, 1, 0), new Vector3(10, 2, 10));
            var result = Call("ground_height", new JObject
            {
                ["points"] = new JArray(new JArray(0, 0), new JArray(50, 50)),
                ["from"] = new JArray(-4, 0),
                ["to"] = new JArray(8, 0),
            });
            Assert.IsNull(result["isError"], TextOf(result));
            var data = JObject.Parse(TextOf(result));
            Assert.AreEqual(2, (double)data["points"][0]["height"], 0.01);
            Assert.AreEqual("Plateau", (string)data["points"][0]["surface"]);
            Assert.AreEqual(JTokenType.Null, data["points"][1]["height"].Type);
            Assert.AreEqual(0, (double)data["line"]["steepest_slope"], 0.1);
            Assert.Greater((int)data["line"]["no_ground"], 0, "the line runs off the plateau");
        }

        // --- make_walkable --------------------------------------------------------------------

        /// <summary>
        /// A bridge like the generated ones: two planks with a hole between them and a 15 cm lip,
        /// half a meter above the banks at either end.
        /// </summary>
        GameObject Bridge()
        {
            Box("Bank West", new Vector3(-8, -0.5f, 0), new Vector3(6, 1, 6));
            Box("Bank East", new Vector3(8, -0.5f, 0), new Vector3(6, 1, 6));
            var bridge = new GameObject("Bridge");
            _made.Add(bridge);
            Box("Plank West", new Vector3(-2.75f, 0.4f, 0), new Vector3(5.5f, 0.2f, 2), bridge.transform);
            Box("Plank East", new Vector3(3.15f, 0.55f, 0), new Vector3(4.7f, 0.2f, 2), bridge.transform);
            Physics.SyncTransforms();
            return bridge;
        }

        static RaycastHit Down(float x, float z)
        {
            Assert.IsTrue(Physics.Raycast(new Vector3(x, 20, z), Vector3.down, out var hit, 50f), $"Nothing under ({x}, {z}).");
            return hit;
        }

        [Test]
        public void MakeWalkable_BridgesTheHole_AddsRailsAndRamps_AndTurnsOffTheMesh()
        {
            var bridge = Bridge();
            var result = Call("make_walkable", new JObject { ["target"] = "Bridge" });
            Assert.IsNull(result["isError"], TextOf(result));
            var data = JObject.Parse(TextOf(result));
            var walkway = bridge.transform.Find("Walkway");
            Assert.IsNotNull(walkway, "a uniformly scaled bridge carries its walkway");
            Assert.AreEqual("Bridge/Walkway", (string)data["walkway"]);
            Assert.GreaterOrEqual((int)data["holes_bridged"], 1);
            Assert.AreEqual(2, (int)data["own_colliders_off"]);
            Assert.IsTrue(bridge.GetComponentsInChildren<BoxCollider>().Where(c => !c.transform.IsChildOf(walkway)).All(c => !c.enabled));

            // The hole between the planks is walkable now, near deck height.
            var hole = Down(0.5f, 0);
            Assert.IsTrue(hole.collider.transform.IsChildOf(walkway), hole.collider.name);
            Assert.That(hole.point.y, Is.InRange(0.45f, 0.75f));
            // Rails on both sides.
            Assert.IsTrue(Physics.Raycast(new Vector3(-2.75f, 1f, 0), Vector3.forward, out var rail, 3f));
            Assert.IsTrue(rail.collider.transform.IsChildOf(walkway));
            // Ramps down to both banks.
            Assert.AreEqual("ramp", (string)data["start"]["joined"]);
            Assert.AreEqual("ramp", (string)data["end"]["joined"]);
            var ramp = Down(6.5f, 0);
            Assert.IsTrue(ramp.collider.transform.IsChildOf(walkway), ramp.collider.name);
            Assert.That(ramp.point.y, Is.InRange(0.02f, 0.65f));

            // Again: the walkway is replaced, not doubled.
            Assert.IsNull(Call("make_walkable", new JObject { ["target"] = "Bridge" })["isError"]);
            Assert.AreEqual(1, bridge.transform.Cast<Transform>().Count(t => t.name == "Walkway"));
        }

        [Test]
        public void MakeWalkable_FarAboveTheGround_SaysSo()
        {
            var bridge = Bridge();
            bridge.transform.position = new Vector3(0, 3, 0);
            var data = JObject.Parse(TextOf(Call("make_walkable", new JObject { ["target"] = "Bridge" })));
            Assert.AreEqual("no", (string)data["start"]["joined"]);
            StringAssert.Contains("above the ground", data["notes"].ToString());
        }

        // --- place_along ----------------------------------------------------------------------

        [Test]
        public void PlaceAlong_LinesBothSides_FacingThePath_WithLights()
        {
            Box("Meadow", new Vector3(0, -0.5f, 10), new Vector3(40, 1, 40));
            Box("Post", new Vector3(100, 1, 100), new Vector3(0.3f, 2, 0.3f));
            var result = Call("place_along", new JObject
            {
                ["source"] = "Post",
                ["path"] = new JArray(new JArray(0, 0), new JArray(0, 20)),
                ["spacing"] = 5,
                ["offset"] = 2,
                ["side"] = "both",
                ["light"] = new JObject(),
            });
            Assert.IsNull(result["isError"], TextOf(result));
            var data = JObject.Parse(TextOf(result));
            var group = AgentTools.Find((string)data["group"]);
            _made.Add(group);
            Assert.AreEqual(10, (int)data["placed"]);
            foreach (Transform copy in group.transform)
            {
                var x = copy.position.x;
                Assert.AreEqual(2f, Mathf.Abs(x), 0.01f, copy.name);
                Assert.AreEqual(0f, World.BoundsOf(copy.gameObject).min.y, 0.06f, copy.name + " sits on the meadow");
                Assert.Greater(Vector3.Dot(copy.forward, new Vector3(-Mathf.Sign(x), 0, 0)), 0.99f, copy.name + " faces the path");
                var light = copy.GetComponentInChildren<Light>();
                Assert.IsNotNull(light, copy.name);
                Assert.AreEqual(LightType.Point, light.type);
            }
        }

        // --- atmosphere -----------------------------------------------------------------------

        static IEnumerable<GameObject> Volumes() => Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
            .Where(t => t.parent == null && t.name == Atmosphere.VolumeName).Select(t => t.gameObject);

        [Test]
        public void Atmosphere_GoldenHour_SetsSunFogAndAVolume_AndReusesIt()
        {
            var fog = RenderSettings.fog;
            var density = RenderSettings.fogDensity;
            var fogColor = RenderSettings.fogColor;
            var ambient = RenderSettings.ambientMode;
            var sunBefore = RenderSettings.sun;
            var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).ToDictionary(l => l, l => (l.transform.rotation, l.color, l.intensity, l.shadows));
            try
            {
                var result = Call("atmosphere", new JObject { ["preset"] = "golden_hour" });
                Assert.IsNull(result["isError"], TextOf(result));
                var data = JObject.Parse(TextOf(result));
                Assert.AreEqual(12, (double)data["sun_elevation"], 0.1);
                Assert.IsTrue(RenderSettings.fog);
                Assert.AreEqual(0.004f, RenderSettings.fogDensity, 1e-5f);
                Assert.AreEqual(Atmosphere.Presets["golden_hour"].SunColor, RenderSettings.sun.color);
                if (MaterialBuilder.Current == Pipeline.Universal)
                {
                    var profile = (string)data["profile"];
                    StringAssert.StartsWith(Folder, profile);
                    Assert.GreaterOrEqual(AssetDatabase.LoadAllAssetsAtPath(profile).Length, 6, "profile plus tone mapping, color, white balance, bloom, vignette");
                    var again = JObject.Parse(TextOf(Call("atmosphere", new JObject { ["exposure"] = 1 })));
                    Assert.AreEqual(profile, (string)again["profile"]);
                    Assert.AreEqual(1, Volumes().Count());
                }
            }
            finally
            {
                foreach (var volume in Volumes().ToList()) Object.DestroyImmediate(volume);
                foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (!lights.TryGetValue(light, out var was))
                    {
                        Object.DestroyImmediate(light.gameObject);
                        continue;
                    }
                    light.transform.rotation = was.rotation;
                    light.color = was.color;
                    light.intensity = was.intensity;
                    light.shadows = was.shadows;
                }
                RenderSettings.fog = fog;
                RenderSettings.fogDensity = density;
                RenderSettings.fogColor = fogColor;
                RenderSettings.ambientMode = ambient;
                RenderSettings.sun = sunBefore;
            }
        }

        [Test]
        public void Atmosphere_UnknownPreset_IsRefused()
        {
            var result = Call("atmosphere", new JObject { ["preset"] = "noon_on_mars" });
            Assert.AreEqual(true, (bool)result["isError"]);
            StringAssert.Contains("golden_hour", TextOf(result));
        }

        // --- capture_view ---------------------------------------------------------------------

        [Test]
        public void CaptureView_Around_ReturnsFourViewsInOneImage_AndFromLooksAtAPoint()
        {
            Box("Statue", new Vector3(0, 1, 0), new Vector3(1, 2, 1));
            var around = Call("capture_view", new JObject { ["around"] = "Statue", ["max_size"] = 512 });
            Assert.IsNull(around["isError"], TextOf(around));
            Assert.AreEqual("image", (string)around["content"][0]["type"]);
            var image = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(image.LoadImage(System.Convert.FromBase64String((string)around["content"][0]["data"])));
                Assert.AreEqual(512, image.width);
                Assert.AreEqual(2 * Mathf.RoundToInt(254 * 9f / 16f) + 4, image.height);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
            var from = Call("capture_view", new JObject { ["from"] = new JArray(0, 5, -10), ["at"] = new JArray(0, 1, 0), ["max_size"] = 256 });
            Assert.IsNull(from["isError"], TextOf(from));
            Assert.AreEqual("image", (string)from["content"][0]["type"]);
        }
    }
}
