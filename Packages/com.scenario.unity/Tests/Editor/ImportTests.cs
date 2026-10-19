using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// Import and placement per output type, on real files made by the test (PNG, WAV,
    /// GLB) and a tiny MP4 fixture, served by the fake Scenario. Everything lands in a
    /// temporary Assets folder that the test removes, and every placed object is undone.
    /// </summary>
    public class ImportTests
    {
        FakeMcp _server;
        McpClient _client;
        string _folder;
        string _projectSettings;
        readonly Dictionary<string, (JObject asset, byte[] bytes)> _assets = new Dictionary<string, (JObject, byte[])>();
        readonly List<GameObject> _made = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _projectSettings = EditorJsonUtility.ToJson(ScenarioProjectSettings.instance);
            ScenarioProjectSettings.instance.ImagesAsSprites = false;
            ScenarioProjectSettings.instance.AddCollider = true;
            _folder = "Assets/ScenarioTestTmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _server = new FakeMcp();
            _server.Tools["asset_get"] = (args, i) => FakeMcp.Result(new JObject { ["asset"] = _assets[(string)args["asset_id"]].asset });
            _server.Tools["asset_download"] = (args, i) => FakeMcp.Result(new JObject
            {
                ["url"] = "https://cdn.cloud.scenario.com/assets/" + (string)args["asset_id"] + (args["format"] != null ? "?format=" + (string)args["format"] : ""),
            });
            _server.DownloadContent = url =>
            {
                var id = url.Substring(url.LastIndexOf('/') + 1).Split('?')[0];
                return url.Contains("?format=") && _converted.TryGetValue(id, out var converted) ? converted : _assets[id].bytes;
            };
            _client = new McpClient(_server, new ApiKeyAuth("key_test", "secret_test"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made.Where(g => g != null)) UnityEngine.Object.DestroyImmediate(go);
            AssetDatabase.DeleteAsset(_folder);
            EditorJsonUtility.FromJsonOverwrite(_projectSettings, ScenarioProjectSettings.instance);
        }

        // --- Test files ------------------------------------------------------------------

        void Serve(string id, string kind, string mime, byte[] bytes, string type = "", JObject properties = null, string parent = "")
        {
            var asset = new JObject { ["id"] = id, ["mimeType"] = mime, ["kind"] = kind, ["metadata"] = new JObject { ["kind"] = kind } };
            if (type.Length > 0) asset["metadata"]["type"] = type;
            if (parent.Length > 0) asset["metadata"]["parentId"] = parent;
            if (properties != null) asset["properties"] = properties;
            _assets[id] = (asset, bytes);
        }

        /// <summary>What Scenario returns when the asset is downloaded in another format.</summary>
        readonly Dictionary<string, byte[]> _converted = new Dictionary<string, byte[]>();

        static JObject Rig(int bones, bool textured = true) => new JObject
        {
            ["hasSkeleton"] = true, ["boneCount"] = bones, ["materialCount"] = textured ? 1 : 0, ["hasUVs"] = textured,
        };

        static byte[] Png(int width, int height, Color color)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels(Enumerable.Repeat(color, width * height).ToArray());
            texture.Apply();
            var bytes = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return bytes;
        }

        /// <summary>A quarter second of 440 Hz, 16-bit mono PCM.</summary>
        static byte[] Wav()
        {
            const int rate = 8000;
            var samples = rate / 4;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(rate);
                writer.Write(rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(samples * 2);
                for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000));
                return stream.ToArray();
            }
        }

        /// <summary>
        /// A binary glTF with one triangle; animated, the triangle rises one unit in a second.
        /// Wrapped, the triangle sits under a "scene" node, as in Tripo's rigs. Z-up, its node is turned
        /// 90 degrees to stand up, as in converted FBX results and Uthana rigs.
        /// </summary>
        static byte[] Glb(bool animated = false, bool wrapped = false, bool zUp = false)
        {
            var data = new List<float> { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            if (animated) data.AddRange(new float[] { 0, 1, 0, 0, 0, 0, 1, 0 }); // times 0 and 1, then translations (0,0,0) and (0,1,0)
            var bin = new byte[data.Count * 4];
            Buffer.BlockCopy(data.ToArray(), 0, bin, 0, bin.Length);
            var nodes = wrapped ? "[{\"name\":\"scene\",\"children\":[1]},{\"mesh\":0,\"name\":\"Triangle\"}]"
                : zUp ? "[{\"mesh\":0,\"name\":\"Triangle\",\"rotation\":[0.7071068,0,0,0.7071068]}]" : "[{\"mesh\":0,\"name\":\"Triangle\"}]";
            var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":" + nodes + "," +
                       "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]," +
                       "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}" +
                       (animated
                           ? ",{\"bufferView\":1,\"componentType\":5126,\"count\":2,\"type\":\"SCALAR\",\"min\":[0],\"max\":[1]},{\"bufferView\":2,\"componentType\":5126,\"count\":2,\"type\":\"VEC3\"}],"
                             + "\"animations\":[{\"name\":\"Rise\",\"channels\":[{\"sampler\":0,\"target\":{\"node\":" + (wrapped ? 1 : 0) + ",\"path\":\"translation\"}}],\"samplers\":[{\"input\":1,\"output\":2,\"interpolation\":\"LINEAR\"}]}],"
                             + "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":8},{\"buffer\":0,\"byteOffset\":44,\"byteLength\":24}],"
                           : "],\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36}],") +
                       "\"buffers\":[{\"byteLength\":" + bin.Length + "}]}";
            return Pack(json, bin);
        }

        /// <summary>A skinned triangle this tall, as a rigging model returns it.</summary>
        static byte[] RiggedGlb(float height)
        {
            var bin = new byte[96];
            Buffer.BlockCopy(new float[] { 0, 0, 0, height * 0.3f, 0, 0, 0, height, 0 }, 0, bin, 0, 36);
            // Joints: every vertex on joint 0 (bytes 36-47, already zero); weights 1, 0, 0, 0.
            Buffer.BlockCopy(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }, 0, bin, 48, 48);
            var h = height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var w = (height * 0.3f).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0,1]}]," +
                       "\"nodes\":[{\"name\":\"Body\",\"mesh\":0,\"skin\":0},{\"name\":\"Hips\"}],\"skins\":[{\"joints\":[1]}]," +
                       "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"JOINTS_0\":1,\"WEIGHTS_0\":2}}]}]," +
                       "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[" + w + "," + h + ",0]}," +
                       "{\"bufferView\":1,\"componentType\":5121,\"count\":3,\"type\":\"VEC4\"},{\"bufferView\":2,\"componentType\":5126,\"count\":3,\"type\":\"VEC4\"}]," +
                       "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":12},{\"buffer\":0,\"byteOffset\":48,\"byteLength\":48}]," +
                       "\"buffers\":[{\"byteLength\":96}]}";
            return Pack(json, bin);
        }

        static byte[] Pack(string json, byte[] bin)
        {
            while (json.Length % 4 != 0) json += " ";
            var jsonBytes = Encoding.ASCII.GetBytes(json);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546C67); // "glTF"
                writer.Write(2);
                writer.Write(12 + 8 + jsonBytes.Length + 8 + bin.Length);
                writer.Write(jsonBytes.Length);
                writer.Write(0x4E4F534A); // JSON
                writer.Write(jsonBytes);
                writer.Write(bin.Length);
                writer.Write(0x004E4942); // BIN
                writer.Write(bin);
                return stream.ToArray();
            }
        }

        List<ImportedFile> Import(string outputType, string prompt, params string[] ids)
        {
            var result = Run.Sync(Importer.Import(_client, ids, new ImportContext
            {
                OutputType = outputType,
                Prompt = prompt,
                ModelId = "model_test",
                ModelName = "Test model",
                JobId = "job_test",
                Folder = _folder,
                Cu = 4,
            }));
            Assert.IsTrue(result.Ok, result.Ok ? "" : result.Error.Message);
            return result.Value;
        }

        PlaceResult Place(string outputType, List<ImportedFile> files, string prompt, GameObject target, bool create)
        {
            Undo.IncrementCurrentGroup();
            var placed = Placer.Place(outputType, files, prompt, target, create);
            _made.AddRange(placed.Objects);
            return placed;
        }

        // --- Per type ------------------------------------------------------------------------

        [Test]
        public void Image_IsImportedWithProvenance_AndPlacedAsAnUndoableQuad()
        {
            Serve("asset_img00001", "image", "image/png", Png(16, 8, Color.red));
            var files = Import(OutputTypes.Image, "Stone golem", "asset_img00001");
            Assert.AreEqual(1, files.Count);
            var file = files[0];
            StringAssert.StartsWith(_folder + "/stone-golem", file.Path);
            StringAssert.EndsWith(".png", file.Path);
            Assert.AreEqual("image", file.Kind);
            Assert.IsFalse(File.Exists(Path.Combine(Files.Downloads, "asset_img00001.download")), "the download is moved, not copied");
            Assert.GreaterOrEqual(((TextureImporter)AssetImporter.GetAtPath(file.Path)).maxTextureSize, 4096);

            var info = Provenance.Read(file.Path);
            Assert.AreEqual("asset_img00001", info.AssetId);
            Assert.AreEqual("job_test", info.JobId);
            Assert.AreEqual("model_test", info.ModelId);
            Assert.AreEqual("Stone golem", info.Prompt);
            Assert.AreEqual(4, info.Cu);

            var placed = Place(OutputTypes.Image, files, "Stone golem", null, create: true);
            Assert.AreEqual(1, placed.Objects.Count);
            var quad = placed.Objects[0];
            if (EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D)
                Assert.IsNotNull(quad.GetComponent<SpriteRenderer>().sprite);
            else
            {
                Assert.AreEqual(2f * quad.transform.localScale.y, quad.transform.localScale.x, 1e-4, "the quad keeps the image's 2:1 aspect");
                Assert.IsNotNull(quad.GetComponent<Renderer>().sharedMaterial.mainTexture);
            }
            Undo.PerformUndo();
            Assert.IsTrue(quad == null, "one Undo removes the placed image");
        }

        [Test]
        public void Download_FromAnUnexpectedHost_IsRefused()
        {
            Serve("asset_img00002", "image", "image/png", Png(4, 4, Color.blue));
            _server.Tools["asset_download"] = (args, i) => FakeMcp.Result(new JObject { ["url"] = "https://evil.example/asset_img00002" });
            var result = Run.Sync(Importer.Import(_client, new[] { "asset_img00002" }, new ImportContext { Folder = _folder, Prompt = "x" }));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("unexpected host", result.Error.Message);
            Assert.IsFalse(_server.Requests.Any(r => r.Method == "GET"), "nothing was downloaded");
            Assert.IsFalse(Directory.Exists(_folder) && Directory.GetFiles(_folder).Any(f => !f.EndsWith(".meta")));
        }

        [Test]
        public void PbrMaps_GetTheirImportSettings_AndBuildOneMaterialOnTheTarget()
        {
            Serve("asset_alb00001", "image", "image/png", Png(8, 8, Color.gray), "texture-albedo");
            Serve("asset_nrm00001", "image", "image/png", Png(8, 8, new Color(0.5f, 0.5f, 1f)), "texture-normal");
            Serve("asset_rgh00001", "image", "image/png", Png(8, 8, Color.white), "texture-roughness");
            Serve("asset_mtl00001", "image", "image/png", Png(8, 8, Color.black), "texture-metallic");
            var files = Import(OutputTypes.Material, "Mossy stone", "asset_alb00001", "asset_nrm00001", "asset_rgh00001", "asset_mtl00001");
            CollectionAssert.AreEquivalent(new[] { "albedo", "normal", "roughness", "metallic" }, files.Select(f => f.Role));
            TextureImporter Importer_(string role) => (TextureImporter)AssetImporter.GetAtPath(files.First(f => f.Role == role).Path);
            Assert.AreEqual(TextureImporterType.NormalMap, Importer_("normal").textureType);
            Assert.IsFalse(Importer_("roughness").sRGBTexture);
            Assert.IsFalse(Importer_("metallic").sRGBTexture);
            Assert.IsTrue(Importer_("albedo").sRGBTexture);
            Assert.AreEqual("albedo", Importer.Main(files).Role);

            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _made.Add(target);
            var placed = Place(OutputTypes.Material, files, "Mossy stone", target, create: false);
            var material = target.GetComponent<Renderer>().sharedMaterial;
            Assert.IsTrue(placed.Asset != null, "the returned material is alive");
            Assert.AreEqual(placed.Asset.GetInstanceID(), material.GetInstanceID());
            StringAssert.StartsWith(_folder + "/", AssetDatabase.GetAssetPath(material));
            Assert.IsNotNull(material.mainTexture, "albedo");
            var normalSlot = material.HasProperty("_BumpMap") ? "_BumpMap" : "_NormalMap";
            Assert.IsNotNull(material.GetTexture(normalSlot), "normal map");
            if (MaterialBuilder.Current != Pipeline.HighDefinition)
                Assert.IsNotNull(material.GetTexture("_MetallicGlossMap"), "packed metallic and smoothness");
            var albedoStem = Path.GetFileNameWithoutExtension(files.First(f => f.Role == "albedo").Path);
            Assert.AreEqual(albedoStem.Substring(0, albedoStem.Length - "-albedo".Length), material.name, "named after the result, with its id");

            // A second material from the same maps keeps the first one's files intact.
            var target2 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _made.Add(target2);
            var second = (Material)Place(OutputTypes.Material, files, "Mossy stone", target2, create: false).Asset;
            Assert.AreNotEqual(AssetDatabase.GetAssetPath(material), AssetDatabase.GetAssetPath(second));
            var maskSlot = MaterialBuilder.Current == Pipeline.HighDefinition ? "_MaskMap" : "_MetallicGlossMap";
            Assert.AreNotEqual(AssetDatabase.GetAssetPath(material.GetTexture(maskSlot)), AssetDatabase.GetAssetPath(second.GetTexture(maskSlot)), "each material packs its own map");

            // A finished job's files come back with their roles from provenance.
            var again = Importer.FromPaths(files.Select(f => f.Path));
            CollectionAssert.AreEquivalent(files.Select(f => f.Role), again.Select(f => f.Role));
            CollectionAssert.AreEquivalent(files.Select(f => f.AssetId), again.Select(f => f.AssetId));
        }

        [Test]
        public void Panorama_BecomesTheSkybox_AndCanBeUndone()
        {
            Serve("asset_sky00001", "image", "image/png", Png(64, 32, Color.cyan), "skybox-base-360");
            var files = Import(OutputTypes.Skybox, "Sunset over dunes", "asset_sky00001");
            Assert.AreEqual("panorama", files[0].Role);
            var previous = RenderSettings.skybox;
            try
            {
                var placed = Place(OutputTypes.Skybox, files, "Sunset over dunes", null, create: true);
                var material = (Material)placed.Asset;
                Assert.IsNotNull(material, placed.Message);
                Assert.AreEqual("Skybox/Panoramic", material.shader.name);
                Assert.IsNotNull(material.GetTexture("_MainTex"));
                if (MaterialBuilder.Current != Pipeline.HighDefinition) Assert.AreEqual(material.GetInstanceID(), RenderSettings.skybox.GetInstanceID());
            }
            finally
            {
                RenderSettings.skybox = previous;
            }
        }

        [Test]
        public void Model_IsDownloadedAsGlb_AndPlacedWithACollider()
        {
            Serve("asset_mdl00001", "3d", "model/gltf-binary", Glb());
            var files = Import(OutputTypes.Model3D, "Triangle", "asset_mdl00001");
            var download = _server.ToolArguments[_server.ToolCalls.IndexOf("asset_download")];
            Assert.IsNull(download["format"], "a GLB comes as stored, with no conversion");
            StringAssert.EndsWith(".glb", files[0].Path);
            Assert.AreEqual("3d", files[0].Kind);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(files[0].Path), "glTFast imports the model");

            var placed = Place(OutputTypes.Model3D, files, "Triangle", null, create: true);
            Assert.AreEqual(1, placed.Objects.Count, placed.Message);
            var instance = placed.Objects[0];
            Assert.IsNotNull(instance.GetComponentInChildren<MeshFilter>());
            Assert.IsNotNull(instance.GetComponentInChildren<Renderer>().sharedMaterial, "a model without material gets the default one, not magenta");
            Assert.IsNotNull(instance.GetComponentInChildren<MeshCollider>(), "AddCollider is on");
            Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(instance));
            Undo.PerformUndo();
            Assert.IsTrue(instance == null, "one Undo removes the model");
        }

        [Test]
        public void ModelWithARotatedRootNode_StandsUnderAPlainRoot()
        {
            // On the prefab root, a node's rotation is ignored by previews: the result card and the Project
            // window would show the model lying down.
            Serve("asset_zupn0001", "3d", "model/gltf-binary", Glb(zUp: true));
            var files = Import(OutputTypes.Model3D, "Standing", "asset_zupn0001");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(files[0].Path);
            Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, prefab.transform.localRotation), 0.01f, "the prefab root is plain");
            Assert.AreEqual(90f, Quaternion.Angle(Quaternion.identity, prefab.GetComponentInChildren<MeshFilter>().transform.rotation), 0.5f, "the node keeps its turn");
        }

        [Test]
        public void ModelsWithoutMaterial_EachKeepAMaterialInTheAsset()
        {
            // Polygen and Hitem3D Multicolor return meshes with no material (Multicolor paints with vertex colors).
            Serve("asset_nomt0001", "3d", "model/gltf-binary", Glb());
            Serve("asset_nomt0002", "3d", "model/gltf-binary", Glb());
            var files = Import(OutputTypes.Model3D, "Plain", "asset_nomt0001").Concat(Import(OutputTypes.Model3D, "Plain", "asset_nomt0002"));
            foreach (var file in files)
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(file.Path).GetComponentInChildren<Renderer>().sharedMaterial, file.Path + " has glTFast's default material");
        }

        [Test]
        public void ResultLabelledGlb_ButStoredInAnotherFormat_IsConverted()
        {
            // Claude Opus 3D edits store a Blender file under the GLB type.
            Serve("asset_blnd0001", "3d", "model/gltf-binary", Encoding.ASCII.GetBytes("BLENDER-v405 not a glb"), "3d23d");
            _converted["asset_blnd0001"] = Glb();
            var files = Import(OutputTypes.Model3D, "Chest", "asset_blnd0001");
            var formats = _server.ToolCalls.Select((c, i) => (c, i)).Where(x => x.c == "asset_download").Select(x => (string)_server.ToolArguments[x.i]["format"]).ToList();
            CollectionAssert.AreEqual(new[] { null, "glb" }, formats, "as stored first, then converted");
            StringAssert.EndsWith(".glb", files[0].Path);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(files[0].Path));
        }

        [Test]
        public void MultiFormatResult_ImportsOnlyTheModel()
        {
            // Meshy and Hunyuan return the GLB with OBJ, MTL and PBR map copies of it.
            Serve("asset_mesh0001", "3d", "model/gltf-binary", Glb(), "txt23d");
            Serve("asset_mesh0002", "3d", "model/obj", Encoding.ASCII.GetBytes("o Triangle\nv 0 0 0\n"), "txt23d");
            Serve("asset_mesh0003", "3d", "model/mtl", Encoding.ASCII.GetBytes("newmtl m\n"), "3d-texture-mtl");
            Serve("asset_mesh0004", "image", "image/png", Png(4, 4, Color.gray), "3d-texture-albedo");
            Serve("asset_mesh0005", "image", "image/png", Png(4, 4, Color.blue), "3d-texture-normal");
            var files = Import(OutputTypes.Model3D, "Hero", "asset_mesh0001", "asset_mesh0002", "asset_mesh0003", "asset_mesh0004", "asset_mesh0005");
            Assert.AreEqual(1, files.Count, string.Join(", ", files.Select(f => f.Path)));
            StringAssert.EndsWith(".glb", files[0].Path);
            Assert.AreEqual(1, _server.ToolCalls.Count(c => c == "asset_download"), "the copies are not downloaded");
        }

        [Test]
        public void AnimatedModel_GetsAController_AndItsClipMovesThePlacedModel()
        {
            Serve("asset_anim0001", "3d", "model/gltf-binary", Glb(animated: true), "txt23d");
            var files = Import(OutputTypes.Motion, "Rise", "asset_anim0001");
            var clip = Animations.Clips(files[0].Path).SingleOrDefault();
            Assert.IsNotNull(clip, "glTFast imports the clip");
            Assert.IsTrue(clip.isLooping || AnimationUtility.GetAnimationClipSettings(clip).loopTime, "clips loop");
            var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(Animations.ControllerPath(files[0].Path));
            Assert.IsNotNull(controller, "a controller next to the model");
            Assert.AreSame(clip, controller.layers[0].stateMachine.defaultState.motion);

            var placed = Place(OutputTypes.Motion, files, "Rise", null, create: true);
            var instance = placed.Objects.Single();
            var animator = instance.GetComponentInChildren<Animator>();
            Assert.IsNotNull(animator);
            Assert.AreSame(controller, animator.runtimeAnimatorController);
            Assert.AreEqual("info", placed.MessageKind, placed.Message);
            // The clip is bound to the placed hierarchy: sampling it moves the triangle.
            var mover = instance.GetComponentInChildren<MeshFilter>().transform;
            clip.SampleAnimation(animator.gameObject, 1f);
            Assert.AreEqual(1f, mover.localPosition.y, 0.01f);
            clip.SampleAnimation(animator.gameObject, 0f);
            Assert.AreEqual(0f, mover.localPosition.y, 0.01f);
            Undo.PerformUndo();
            Assert.IsTrue(instance == null, "one Undo removes the model");
        }

        [Test]
        public void RigSet_OneControllerOnTheRiggedModel_WithTheOtherFilesClips()
        {
            // Meshy Rigging: the rigged character, the character walking, and the walk on a bare skeleton.
            // The walk files point to the rigged model, whatever order the job lists them in.
            Serve("asset_rigs0001", "3d", "model/gltf-binary", Glb(), "3d23d", Rig(24), parent: "asset_source01");
            Serve("asset_rigs0002", "3d", "model/gltf-binary", Glb(animated: true), "3d23d", Rig(24), parent: "asset_rigs0001");
            Serve("asset_rigs0003", "3d", "model/gltf-binary", Glb(animated: true), "3d23d", Rig(24, textured: false), parent: "asset_rigs0001");
            var files = Import(OutputTypes.Model3D, "Hero", "asset_rigs0002", "asset_rigs0003", "asset_rigs0001");
            Assert.AreEqual(2, files.Count, "the bare skeleton is left out");
            var rigged = files.Single(f => f.AssetId == "asset_rigs0001");
            Assert.AreSame(rigged, Importer.Main(files), "the rigged model is the one placed");
            var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(Animations.ControllerPath(rigged.Path));
            Assert.IsNotNull(controller, "the rigged model gets the controller");
            Assert.AreEqual("Rise", controller.layers[0].stateMachine.defaultState.name, "the walk file's clip");
            var walk = files.Single(f => f.AssetId == "asset_rigs0002");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Animations.ControllerPath(walk.Path)), "one controller per result");
            var placed = Place(OutputTypes.Model3D, files, "Hero", null, create: true);
            Assert.AreSame(controller, placed.Objects.Single().GetComponentInChildren<Animator>().runtimeAnimatorController);
        }

        [Test]
        public void RigSet_ClipFromAFileWithAnExtraRootNode_StillDrivesTheRiggedModel()
        {
            // Tripo: both files have one root node, "scene". Left to its default, glTFast merges it into the
            // prefab root of the rigged file but keeps it in the walk file, whose clip then addresses
            // "scene/Triangle" on a model that has "Triangle".
            Serve("asset_trip0001", "3d", "model/gltf-binary", Glb(wrapped: true), "3d23d", Rig(41), parent: "asset_source01");
            Serve("asset_trip0002", "3d", "model/gltf-binary", Glb(animated: true, wrapped: true), "3d23d", Rig(41), parent: "asset_source01");
            var files = Import(OutputTypes.Model3D, "Walker", "asset_trip0001", "asset_trip0002");
            var placed = Place(OutputTypes.Model3D, files, "Walker", null, create: true);
            var instance = placed.Objects.Single();
            var animator = instance.GetComponentInChildren<Animator>();
            var clip = animator.runtimeAnimatorController.animationClips.Single();
            var mover = instance.GetComponentInChildren<MeshFilter>().transform;
            clip.SampleAnimation(animator.gameObject, 1f);
            Assert.AreEqual(1f, mover.localPosition.y, 0.01f, "the walk file's clip moves the rigged model");
            clip.SampleAnimation(animator.gameObject, 0f);
            Assert.AreEqual(0f, mover.localPosition.y, 0.01f);
        }

        [Test]
        public void RigInCentimeters_IsPlacedAtMeterScale()
        {
            // Cartwheel rigging writes centimeters: a 180 m character is 1.8 m.
            Serve("asset_cmrg0001", "3d", "model/gltf-binary", RiggedGlb(180), "3d23d", Rig(1));
            var files = Import(OutputTypes.Model3D, "Giant", "asset_cmrg0001");
            var placed = Place(OutputTypes.Model3D, files, "Giant", null, create: true);
            var instance = placed.Objects.Single();
            Assert.AreEqual(0.01f, instance.transform.localScale.y, 1e-6f);
            StringAssert.Contains("centimeters", placed.Message);
        }

        [Test]
        public void StateNames_ReadAsWords()
        {
            Assert.AreEqual("Walking man", Animations.StateName("Armature|walking_man|baselayer", 1));
            Assert.AreEqual("Casual Walk", Animations.StateName("Armature|Casual_Walk|baselayer", 1));
            Assert.AreEqual("Motion 2", Animations.StateName("export-SHAB--detzquuddz_interpolated_30.0fps", 2));
            Assert.AreEqual("Walk", Animations.StateName("walk", 1));
            Assert.AreEqual("Motion 1", Animations.StateName("", 1));
        }

        [Test]
        public void Splat_IsKeptAsAFile_AndNotPlaced()
        {
            Serve("asset_splt0001", "3d", "model/spz", new byte[] { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0 }, "img2splat");
            var files = Import(OutputTypes.Model3D, "Chest", "asset_splt0001");
            var download = _server.ToolArguments[_server.ToolCalls.IndexOf("asset_download")];
            Assert.IsNull(download["format"], "no conversion for a splat");
            StringAssert.EndsWith(".spz", files[0].Path);
            Assert.AreEqual("splat", files[0].Kind);
            var placed = Place(OutputTypes.Model3D, files, "Chest", null, create: true);
            Assert.AreEqual(0, placed.Objects.Count);
            StringAssert.Contains("splat", placed.Message);
            Assert.IsFalse(OutputTypes.StepsFor(Importer.KindOfPath(files[0].Path)).Any(), "no 3D steps on a splat");
        }

        [Test]
        public void Audio_GetsAnAudioSource()
        {
            Serve("asset_aud00001", "audio", "audio/wav", Wav());
            var files = Import(OutputTypes.Audio, "Door creak", "asset_aud00001");
            StringAssert.EndsWith(".wav", files[0].Path);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(files[0].Path);
            Assert.IsNotNull(clip);

            var placed = Place(OutputTypes.Audio, files, "Door creak", null, create: true);
            var source = placed.Objects.Single().GetComponent<AudioSource>();
            Assert.AreSame(clip, source.clip);
            Assert.IsFalse(source.playOnAwake);
        }

        [Test]
        public void Video_GetsAVideoPlayerOnAQuad()
        {
            Serve("asset_vid00001", "video", "video/mp4", File.ReadAllBytes(Path.Combine(Fixtures.Folder, "tiny-video.mp4.bytes")));
            var files = Import(OutputTypes.Video, "Waves", "asset_vid00001");
            StringAssert.EndsWith(".mp4", files[0].Path);
            var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(files[0].Path);
            Assert.IsNotNull(clip, "Unity imports the MP4");

            var placed = Place(OutputTypes.Video, files, "Waves", null, create: true);
            var player = placed.Objects.Single().GetComponent<VideoPlayer>();
            Assert.AreSame(clip, player.clip);
            Assert.IsTrue(player.isLooping);
            Assert.AreEqual(VideoRenderMode.MaterialOverride, player.renderMode);
        }

        [Test]
        public void PlaceTarget_ResolvesByPath_WhenTheSceneHasNoGlobalId()
        {
            var parent = new GameObject("ScenarioTestParent-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.name = "Target";
            child.transform.SetParent(parent.transform);
            try
            {
                // An object in an unsaved scene: its GlobalObjectId does not resolve, the path does.
                var byPath = new JobRow { PlaceTarget = "", PlaceTargetPath = Placer.PathOf(child) };
                Assert.AreEqual(child.GetInstanceID(), ScenarioService.ResolveTarget(byPath)?.GetInstanceID());
                var both = new JobRow { PlaceTarget = GlobalObjectId.GetGlobalObjectIdSlow(child).ToString(), PlaceTargetPath = Placer.PathOf(child) };
                Assert.AreEqual(child.GetInstanceID(), ScenarioService.ResolveTarget(both)?.GetInstanceID());
                Assert.IsNull(ScenarioService.ResolveTarget(new JobRow { PlaceTargetPath = parent.name + "/Missing" }));
                Assert.IsNull(ScenarioService.ResolveTarget(new JobRow()));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void KindOfPath_ReadsTheExtension()
        {
            Assert.AreEqual("3d", Importer.KindOfPath("Assets/a.glb"));
            Assert.AreEqual("3d", Importer.KindOfPath("Assets/a.fbx"));
            Assert.AreEqual("splat", Importer.KindOfPath("Assets/a.spz"));
            Assert.AreEqual("audio", Importer.KindOfPath("Assets/a.wav"));
            Assert.AreEqual("video", Importer.KindOfPath("Assets/a.mp4"));
            Assert.AreEqual("image", Importer.KindOfPath("Assets/a.png"));
        }
    }
}
