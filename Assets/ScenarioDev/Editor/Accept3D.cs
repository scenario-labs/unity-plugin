using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using Scenario.Editor.Agent;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): 3D acceptance on real Scenario results.
    /// Import (free, needs SCENARIO_NO_SPEND=1): imports one result per 3D operation (the
    /// 2026-10-18 discovery runs) through the plugin's importer and placer, then checks what
    /// Unity got: one model per result, the rig's controller and clips, a pose that changes
    /// when the clip plays, the splat kept as a file. Each result is rendered (three poses
    /// when animated) to Library/ScenarioDevLogs/accept3d.
    /// Paid (needs SCENARIO_ACCEPT=1): four 3D operations sent once each, three through the
    /// window's path and one through the agent's (with its FBX conversion), logged to
    /// tests/live/LEDGER.md. SCENARIO_ACCEPT_CASES=rig,animate runs only those.
    /// Run with -batchmode -executeMethod ScenarioDev.Accept3D.Import (or .Paid).
    /// </summary>
    public static class Accept3D
    {
        sealed class Expect
        {
            public int Files = 1;
            /// <summary>true: a controller whose clip moves the model; null: report only.</summary>
            public bool? Animated = false;
            public bool Splat;
        }

        // Results of the discovery runs in the test project, every file of each job.
        static readonly (string label, string model, string[] ids, Expect expect)[] Results =
        {
            ("creature", "model_meshy-7-1-txt23d", new[] { "asset_MDhi5PNMRdwypK7xpdJbFMsf", "asset_F3xKxwLBgCGi8N1odRMv4QZw", "asset_hiZke3WUEoESSGHfadbvtgbU", "asset_1RTY2VxvmZhU5wLxMJqofBF5", "asset_68NZQMsprryHndsrZXfG7wum", "asset_bcApdf3Q8ATv5XPUTXmJYvx3", "asset_zxTxNRRsMy8ekRGVA4knRuH6" }, new Expect()),
            ("character-30k", "model_meshy-remesh", new[] { "asset_Vv93WrrNrXKuQd4APofT6XxN", "asset_rGey3p1bFaqdZHybR9GGWzpS" }, new Expect()),
            ("remesh-quad", "model_meshy-remesh", new[] { "asset_yn57SusUZk2aSjfFqpv4tj2w", "asset_czxNAv1BxNbSyHmWbHxueKvd" }, new Expect()),
            ("polygen", "model_tencent-smarttopology", new[] { "asset_ERRLQUh5LVm1mxY6vaGR38SF", "asset_KJk9JxFAKupYyRov3siD6XMA" }, new Expect()),
            ("meshy-uv", "model_meshy-uv-unwrap", new[] { "asset_oxTeozpt36B1rG1A99rhM5gQ" }, new Expect()),
            ("tripo-texturing", "model_tripo-v3-0-texturing", new[] { "asset_1MNXDVLsu6ALqkJRs4TnRPV7" }, new Expect()),
            ("texture-edit-fbx", "model_tencent-texture-edit", new[] { "asset_XCBZtxJ48zFEqc6Kp6zcQGJz" }, new Expect()),
            ("opus-edit", "model_scenario-claude-opus-5-5-3d", new[] { "asset_A5ZpRQvzGLDy8JzWYGRDyxTn" }, new Expect()),
            ("stylize-lego", "model_tripo-stylization", new[] { "asset_FpP5dMwAhFAbyUUKyYy9uydQ" }, new Expect()),
            ("stylize-voxel", "model_tripo-stylization", new[] { "asset_oPo65bDuzZ8QTJbvAgLMANH5" }, new Expect()),
            ("multicolor", "model_hitem-3d-multicolor", new[] { "asset_aRAaibdjR6Woc7YnEexS9Ud5" }, new Expect()),
            ("hy-part", "model_hunyuan-3d-part", new[] { "asset_Urq6STJjwcC5a2Hqg43AQgYh" }, new Expect()),
            ("seg-v2", "model_tripo-segmentation-v2", new[] { "asset_pgjFh1J2cb4zGN5spQ88JzYh" }, new Expect()),
            ("hitem-split", "model_hitem-3d-split", new[] { "asset_hNnJMXRssXULyaUFWTb6eEEk" }, new Expect()),
            ("rodin-bang", "model_rodin-hyper3d-bang", new[] { "asset_Hke4ZLzCEsSrXSYQvthLegoG" }, new Expect()),
            ("voxels", "model_scenario-voxels", new[] { "asset_j8iLNwRkcgSQTQhH9QVLR9Ea" }, new Expect()),
            ("voxel-img", "model_scenario-voxels", new[] { "asset_CwWKxGC8JC3rxcLSkK9CTtwr" }, new Expect()),
            ("triposplat", "model_vast-ai-triposplat", new[] { "asset_dyrfHRkTts6SDQvjZbvDsme1" }, new Expect { Splat = true }),
            ("rig-meshy", "model_meshy-rigging", new[] { "asset_iTL54q1y5PgZQhZAcSU5MV7w", "asset_sohULaF5HZMXn7nuWWKiUtGR", "asset_zmnbEzekYKEaJHCTkuzPLnSE", "asset_tFwQD9CHeCSm5m3Nn5RWWHUF", "asset_wRts5kjDmddcC7wRDYMbkwgm" }, new Expect { Files = 3, Animated = true }),
            ("rig-tripo-walk", "model_tripo-rigging-v1", new[] { "asset_zLjazj1oZhaXpYv2EEHxmf6g", "asset_mL74oj26BmJRLwicPshyqgTk" }, new Expect { Files = 2, Animated = true }),
            ("rig-uthana", "model_uthana-character-rigging", new[] { "asset_DDGAu1G5MTPogau6Ac1GJApk" }, new Expect { Animated = null }),
            ("rig-cartwheel", "model_cartwheel-character-rigging", new[] { "asset_SMF1W7Pe447JbY1qbTa2wf9p" }, new Expect { Animated = null }),
            ("anim-meshy", "model_meshy-animation", new[] { "asset_MiPQ5mUp15TCU9cNamy18XMQ" }, new Expect { Animated = true }),
            ("meshy-motion", "model_meshy-text-to-motion", new[] { "asset_DhavVUGnVWGkpqkeaaToFJg4" }, new Expect { Animated = true }),
            ("motion-cartwheel", "model_cartwheel-text-to-motion", new[] { "asset_aBX2SDGcSBCTb8oioRDRu2Ws" }, new Expect { Animated = true }),
            ("motion-uthana", "model_uthana-text-to-motion-3.0", new[] { "asset_7PdPMTgz9CRtenoP5qdMDjFc" }, new Expect { Animated = true }),
            ("uthana-motion-nochar", "model_uthana-text-to-motion-3.0", new[] { "asset_NXHV6vi97CdBV8H9HsEQNDDW" }, new Expect { Animated = true }),
            // 2026-10-19, for the README: "runs forward fast, takes a big jump forward, lands and keeps running, then jumps high in the air".
            ("run-jump-cartwheel", "model_cartwheel-text-to-motion", new[] { "asset_VD96zqP78q2rpaj53iX9KYNU" }, new Expect { Animated = true }),
            ("run-jump-uthana", "model_uthana-text-to-motion-3.0", new[] { "asset_vCotdBCwSqJ69Ud7JCka33fQ" }, new Expect { Animated = true }),
        };

        const string Ledger = "tests/live/LEDGER.md";
        const string Character = "asset_Vv93WrrNrXKuQd4APofT6XxN";
        static readonly string Out = "Library/ScenarioDevLogs/accept3d";
        static readonly List<string> Lines = new List<string>();
        static readonly List<GameObject> Placed = new List<GameObject>();
        static ScenarioService S => ScenarioService.Instance;

        static readonly string[] Only = (Environment.GetEnvironmentVariable("SCENARIO_ACCEPT_CASES") ?? "")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();

        static bool Selected(string name) => Only.Length == 0 || Only.Any(o => name.StartsWith(o, StringComparison.Ordinal));

        public static void Import() => EditorApplication.delayCall += () => _ = Run(paid: false);

        public static void Paid() => EditorApplication.delayCall += () => _ = Run(paid: true);

        /// <summary>Reimports every Scenario model in the project, after a change to how models import.</summary>
        public static void ReimportModels()
        {
            var paths = Provenance.AllPaths().Where(p => p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var path in paths) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"ACCEPT3D reimported {paths.Count} model(s)");
            EditorApplication.Exit(0);
        }

        static async Task Run(bool paid)
        {
            var failures = 0;
            var ledger = new List<string>();
            try
            {
                if (!S.Ready)
                {
                    Fail("setup", "sign in with the test key first");
                    failures++;
                    return;
                }
                if (!paid && Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") != "1")
                {
                    Fail("setup", "SCENARIO_NO_SPEND=1 is required: the import run never spends.");
                    failures++;
                    return;
                }
                if (paid && (Environment.GetEnvironmentVariable("SCENARIO_ACCEPT") != "1" || S.Client.SpendingDisabled))
                {
                    Fail("setup", "needs SCENARIO_ACCEPT=1 and spending on.");
                    failures++;
                    return;
                }
                await S.RefreshCatalog(true);
                Directory.CreateDirectory(Out);
                Directory.CreateDirectory("Assets/Scenario");
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                failures += paid ? await RunPaid(ledger) : await RunImports();
                Layout();
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), $"Assets/Scenario/Accept3D-{(paid ? "paid" : "import")}-{DateTime.Now:yyyyMMdd-HHmm}.unity");
            }
            catch (Exception e)
            {
                Fail("exception", e.ToString());
                failures++;
            }
            finally
            {
                if (paid) WriteLedger(ledger, failures);
                File.WriteAllLines(Path.Combine(Out, paid ? "paid.txt" : "import.txt"), Lines);
                Debug.Log($"ACCEPT3D done failures={failures}");
                EditorApplication.Exit(failures > 0 ? 1 : 0);
            }
        }

        // --- Free: real results through the importer ------------------------------

        static async Task<int> RunImports()
        {
            var failures = 0;
            var index = 0;
            foreach (var (label, model, ids, expect) in Results)
            {
                index++;
                if (!Selected(label)) continue;
                var info = S.Model(model);
                var type = info != null && OutputTypes.IsMotion(info) ? OutputTypes.Motion : OutputTypes.Model3D;
                Selection.activeGameObject = null;
                var started = DateTime.Now;
                var imported = await S.ImportAssets(ids, type, "Accept3D " + label, place: true, target: null);
                var seconds = (DateTime.Now - started).TotalSeconds;
                if (!imported.Ok)
                {
                    Fail(label, "import: " + imported.Error.Message);
                    failures++;
                    continue;
                }
                var files = imported.Value;
                var problems = new List<string>();
                if (files.Count != expect.Files) problems.Add($"{files.Count} files, expected {expect.Files}");
                var go = Selection.activeGameObject;
                string detail;
                if (expect.Splat)
                {
                    var main = Importer.Main(files);
                    // TripoSplat labels its file SPZ but stores a PLY, the format splat renderers for Unity read.
                    if (main == null || main.Kind != "splat" || !(main.Path.EndsWith(".spz") || main.Path.EndsWith(".ply"))) problems.Add("not kept as a splat file");
                    if (go != null) problems.Add("a splat was placed");
                    detail = string.Join(", ", files.Select(f => Path.GetFileName(f.Path)));
                }
                else if (go == null)
                {
                    problems.Add("nothing placed");
                    detail = string.Join(", ", files.Select(f => Path.GetFileName(f.Path)));
                }
                else
                {
                    Placed.Add(go);
                    detail = Inspect(go, expect.Animated, problems, $"{index:00}-{label}");
                }
                var line = $"{label} ({info?.Name ?? model}, {ids.Length} asset(s), {seconds:0}s): {string.Join(", ", files.Select(f => Path.GetExtension(f.Path)))} | {detail}";
                if (problems.Count == 0) Log("OK " + line);
                else
                {
                    Fail(label, line + " | " + string.Join("; ", problems));
                    failures++;
                }
            }
            return failures;
        }

        // --- Paid: operations sent through the plugin -----------------------------

        static async Task<int> RunPaid(List<string> ledger)
        {
            var failures = 0;
            var cases = new List<(string name, string type, string model, string prompt, JObject inputs, bool? animated, int timeout)>
            {
                ("generate", OutputTypes.Model3D, "model_meshy-7-1-txt23d", "A stylized cartoon knight, full body, standing, game-ready character", new JObject(), false, 900),
                ("rig", OutputTypes.Model3D, "model_uthana-character-rigging", "Rig the adventurer", new JObject { ["characterFile"] = Character }, null, 900),
                ("animate", OutputTypes.Motion, "model_meshy-animation", "Attack", new JObject { ["model"] = Character, ["actionId"] = 4 }, true, 900),
            };
            foreach (var c in cases.Where(c => Selected(c.name)))
            {
                var outcome = await SendFromWindow(c.name, c.type, c.model, c.prompt, c.inputs, c.timeout);
                var problems = new List<string>(outcome.problems);
                var detail = "";
                var go = outcome.row != null && outcome.row.Placed.Count > 0 ? Placer.FindByPath(outcome.row.Placed[0]) : null;
                if (outcome.row?.Imported == true && go == null) problems.Add("nothing placed");
                if (go != null)
                {
                    Placed.Add(go);
                    detail = Inspect(go, c.animated, problems, "paid-" + c.name);
                    if (c.name == "generate" && Triangles(go) > 40000) problems.Add($"{Triangles(go)} triangles: the 30k remesh preset did not apply");
                }
                failures += Report(ledger, c.name, c.model, outcome.row, outcome.quoted, detail, problems);
            }
            if (Selected("agent")) failures += await AgentTextureEdit(ledger);
            return failures;
        }

        static async Task<(JobRow row, double quoted, List<string> problems)> SendFromWindow(string name, string type, string modelId, string prompt, JObject inputs, int timeout)
        {
            var problems = new List<string>();
            var schema = await S.Schema(modelId);
            if (!schema.Ok) return (null, 0, new List<string> { "schema: " + schema.Error.Message });
            var fields = SchemaForm.FieldsFromSchema(schema.Value, OutputTypes.Restrict);
            var values = SchemaForm.InitialValues(fields, OutputTypes.Presets(modelId));
            var promptName = SchemaForm.PromptName(fields);
            if (promptName.Length > 0) values[promptName] = prompt;
            foreach (var input in inputs) values[input.Key] = input.Value;
            problems.AddRange(SchemaForm.Validate(fields, values, modelId).Select(p => "form: " + p));
            if (problems.Count > 0) return (null, 0, problems);
            var request = new GenerateRequest
            {
                OutputType = type, ModelId = modelId, ModelName = S.Model(modelId)?.Name ?? modelId,
                Payload = SchemaForm.Payload(fields, values), Prompt = prompt, Place = true, Origin = "acceptance",
            };
            var quote = await S.Estimate(request);
            if (!quote.Ok || S.EstimateAlarm) return (null, 0, new List<string> { "price: " + (quote.Ok ? "a price check returned a job" : quote.Error.Message) });
            var started = await S.Generate(request);
            if (!started.Ok)
                return (S.Ledger.Jobs.LastOrDefault(), quote.Value.Cu, new List<string> { (started.Error.Ambiguous ? "UNKNOWN outcome: " : "not sent: ") + started.Error.Message });
            var row = started.Value;
            Log($"sent {name}: {request.ModelName} job {row.JobId} quoted {quote.Value.Cu}");
            var start = Json.Now();
            while (!(row.IsFinal && (row.State != JobState.Success || row.Imported || row.Error.StartsWith("Import failed"))) && Json.Now() - start < timeout)
                await Task.Delay(2000);
            if (row.State != JobState.Success) problems.Add($"state {row.State} {row.Error}");
            else if (!row.Imported) problems.Add("not imported " + row.Error);
            return (row, quote.Value.Cu, problems);
        }

        /// <summary>Tencent Texture Edit takes FBX only: the agent passes the GLB id and the plugin converts it.</summary>
        static async Task<int> AgentTextureEdit(List<string> ledger)
        {
            const string model = "model_tencent-texture-edit";
            var user = ScenarioUserSettings.instance;
            var (auto, budget) = (user.AgentAutoApproveCu, user.AgentSessionBudgetCu);
            var problems = new List<string>();
            JobRow row = null;
            double quoted = 0;
            var detail = "";
            try
            {
                // Batch mode has no one to approve: raise the limits for this call only.
                user.AgentAutoApproveCu = 100;
                user.AgentSessionBudgetCu = Math.Max(budget, 1000);
                var reply = await AgentTools.Call("generate", new JObject
                {
                    ["model_id"] = model,
                    ["parameters"] = new JObject { ["file3d"] = Character, ["prompt"] = "Silver plate armor with deep blue cloth and brown leather straps" },
                    ["max_cu"] = 100,
                    ["timeout_seconds"] = 900,
                }, new AgentTools.Context { Client = "Accept3D" });
                var text = reply.ToString(Newtonsoft.Json.Formatting.None);
                Log("agent reply: " + (text.Length > 600 ? text.Substring(0, 600) + "…" : text));
                var data = Json.ParseObject((string)reply.SelectToken("content[0].text") ?? "") ?? reply;
                var jobId = Json.Str(data, "job_id");
                row = jobId.Length > 0 ? S.Ledger.FindByJob(jobId) : null;
                if (row == null) problems.Add("no job: " + (text.Length > 300 ? text.Substring(0, 300) : text));
                else
                {
                    quoted = row.QuotedCu;
                    if (row.State != JobState.Success) problems.Add($"state {row.State} {row.Error}");
                    var file3d = (string)row.Parameters?["file3d"] ?? "";
                    if (file3d == Character || file3d.Length == 0) problems.Add("the GLB was sent without an FBX conversion");
                    else Log($"agent sent the FBX copy {file3d} for {Character}");
                    var go = row.Placed.Count > 0 ? Placer.FindByPath(row.Placed[0]) : null;
                    if (go == null) problems.Add("nothing placed");
                    else
                    {
                        Placed.Add(go);
                        detail = Inspect(go, false, problems, "paid-agent-texture-edit");
                    }
                }
            }
            finally
            {
                user.AgentAutoApproveCu = auto;
                user.AgentSessionBudgetCu = budget;
                user.Save();
            }
            return Report(ledger, "agent texture edit (GLB to FBX)", model, row, quoted, detail, problems);
        }

        static int Report(List<string> ledger, string name, string model, JobRow row, double quoted, string detail, List<string> problems)
        {
            var ok = problems.Count == 0;
            var files = row != null ? string.Join(", ", row.Files.Select(Path.GetFileName)) : "";
            var line = $"{name}: {row?.State.ToString() ?? "not sent"}, {files} | {detail}";
            if (ok) Log("OK " + line);
            else Fail(name, line + " | " + string.Join("; ", problems));
            ledger.Add($"| 3D {name} | `{model}` | `{row?.JobId ?? ""}` | {quoted} | {(row?.ChargedCu.HasValue == true ? row.ChargedCu.Value.ToString() : "?")} | {(ok ? "ok: " + files : "FAIL " + string.Join("; ", problems)).Replace("|", "/")} |");
            return ok ? 0 : 1;
        }

        static void WriteLedger(List<string> rows, int failures)
        {
            if (rows.Count == 0) return;
            var section = new List<string>
            {
                "",
                $"## {DateTime.Now:yyyy-MM-dd HH:mm} 3D acceptance run{(Only.Length > 0 ? " of " + string.Join(", ", Only) : "")} ({(failures == 0 ? "pass" : failures + " failure(s)")})",
                "",
                "| Case | Model or workflow | Job | Quoted CU | Charged CU | Result |",
                "|---|---|---|---|---|---|",
            };
            section.AddRange(rows);
            File.AppendAllText(Ledger, string.Join("\n", section) + "\n");
        }

        // --- What Unity got --------------------------------------------------------

        /// <summary>Mesh, materials, rig and animation of a placed result, with a render; problems are added to the list.</summary>
        static string Inspect(GameObject go, bool? animated, List<string> problems, string shot)
        {
            var triangles = Triangles(go);
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            var materials = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            var textured = materials.Count(m => m.GetTexturePropertyNames().Any(p => m.GetTexture(p) != null));
            var bones = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s => s.bones.Length).DefaultIfEmpty(0).Max();
            if (triangles == 0) problems.Add("no mesh");
            if (renderers.Any(r => r.sharedMaterials.All(m => m == null))) problems.Add("a renderer has no material (magenta)");
            var parts = new List<string> { $"{triangles:N0} tris", $"{renderers.Length} renderer(s)", $"{materials.Count} material(s), {textured} textured" };
            var fallback = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.defaultMaterial : null;
            if (fallback != null && materials.Contains(fallback)) parts.Add("placement fallback material");
            if (bones > 0) parts.Add($"{bones} bones");
            if (renderers.Length > 0)
            {
                var size = Bounds(go).size;
                parts.Add($"size {size.x:0.##} x {size.y:0.##} x {size.z:0.##} m");
            }

            var animator = go.GetComponentInChildren<Animator>();
            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            AnimationClip clip = null;
            if (controller != null)
            {
                var states = controller.layers[0].stateMachine.states.Select(s => s.state).ToList();
                var main = controller.layers[0].stateMachine.defaultState;
                clip = main?.motion as AnimationClip;
                var moved = clip != null ? Movement(animator.gameObject, clip) : 0;
                parts.Add($"controller: {string.Join(", ", states.Select(s => $"{s.name} {(s.motion as AnimationClip)?.length ?? 0:0.0}s"))}; default {main?.name}, moves {moved:0}°");
                if (animated == true && moved < 5) problems.Add($"the default clip barely moves ({moved:0.0}°)");
                if (animated == false) problems.Add("unexpected controller");
            }
            else
            {
                if (animated == true) problems.Add("no controller");
                if (animated == null) parts.Add("no controller (no clip in the file)");
            }
            Capture(go, animator != null ? animator.gameObject : go, clip, Path.Combine(Out, shot + ".png"));
            return string.Join(", ", parts);
        }

        static int Triangles(GameObject go)
        {
            long total = 0;
            foreach (var mesh in go.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh)
                         .Concat(go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s => s.sharedMesh)).Where(m => m != null))
                for (var i = 0; i < mesh.subMeshCount; i++) total += mesh.GetIndexCount(i) / 3;
            return (int)total;
        }

        /// <summary>The largest bone turn, in degrees, from the clip's start (a walk cycle is back near it halfway).</summary>
        static float Movement(GameObject root, AnimationClip clip)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            clip.SampleAnimation(root, 0);
            var start = transforms.Select(t => t.localRotation).ToArray();
            var max = 0f;
            foreach (var at in new[] { 0.25f, 0.5f, 0.75f })
            {
                clip.SampleAnimation(root, clip.length * at);
                for (var i = 0; i < transforms.Length; i++) max = Mathf.Max(max, Quaternion.Angle(start[i], transforms[i].localRotation));
            }
            clip.SampleAnimation(root, 0);
            return max;
        }

        static Bounds Bounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        /// <summary>Renders the object alone, three poses side by side when it has a clip.</summary>
        static void Capture(GameObject go, GameObject animated, AnimationClip clip, string path)
        {
            var others = EditorSceneManager.GetActiveScene().GetRootGameObjects()
                .Where(r => r != go && r.activeSelf && r.GetComponent<Camera>() == null && r.GetComponent<Light>() == null).ToList();
            foreach (var o in others) o.SetActive(false);
            const int size = 420;
            var times = clip != null ? new[] { 0f, clip.length * 0.33f, clip.length * 0.66f } : new[] { 0f };
            var sheet = new Texture2D(size * times.Length, size, TextureFormat.RGB24, false);
            try
            {
                // One camera for every pose, framing all of them: a walk leaves its first spot. Skinned
                // bounds only follow the pose on render, so the bones mark where the body went.
                var bounds = Bounds(go);
                var bones = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(s => s.bones).Where(b => b != null).ToList();
                foreach (var t in times.Where(_ => clip != null))
                {
                    clip.SampleAnimation(animated, t);
                    foreach (var bone in bones) bounds.Encapsulate(new Bounds(bone.position, Vector3.one * 0.4f * Mathf.Max(0.01f, go.transform.lossyScale.y)));
                }
                // The batch loop never runs Unity's skinning pass: each pose is baked (see Media).
                var baked = clip != null ? Media.Skins(go) : null;
                try
                {
                    for (var i = 0; i < times.Length; i++)
                    {
                        if (clip != null)
                        {
                            clip.SampleAnimation(animated, times[i]);
                            Media.Bake(baked);
                        }
                        var frame = Render(bounds, size);
                        sheet.SetPixels(i * size, 0, size, size, frame.GetPixels());
                        Object.DestroyImmediate(frame);
                    }
                }
                finally
                {
                    if (baked != null) Media.Unbake(baked);
                }
                sheet.Apply();
                File.WriteAllBytes(path, sheet.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(sheet);
                if (clip != null) clip.SampleAnimation(animated, 0);
                foreach (var o in others) o.SetActive(true);
            }
        }

        static Texture2D Render(Bounds bounds, int size)
        {
            var holder = new GameObject("Accept3D Camera");
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                var camera = holder.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.2f, 0.21f, 0.23f);
                camera.fieldOfView = 30;
                // Front three-quarter view: glTF models face +Z.
                var direction = new Vector3(0.45f, 0.3f, 0.85f).normalized;
                var distance = Mathf.Max(0.01f, bounds.extents.magnitude) / Mathf.Sin(15 * Mathf.Deg2Rad) * 1.05f;
                camera.transform.position = bounds.center + direction * distance;
                camera.transform.LookAt(bounds.center);
                camera.nearClipPlane = distance * 0.01f;
                camera.farClipPlane = distance * 4;
                camera.targetTexture = rt;
                camera.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var frame = new Texture2D(size, size, TextureFormat.RGB24, false);
                frame.ReadPixels(new Rect(0, 0, size, size), 0, 0);
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

        /// <summary>Spreads the placed results along X for the saved scene.</summary>
        static void Layout()
        {
            var x = 0f;
            foreach (var go in Placed.Where(g => g != null))
            {
                var bounds = Bounds(go);
                var width = Mathf.Max(0.5f, bounds.size.x);
                go.transform.position += new Vector3(x + width / 2 - bounds.center.x, -bounds.min.y, -bounds.center.z);
                x += width + 0.5f;
            }
        }

        static void Log(string line)
        {
            Lines.Add(line);
            Debug.Log("ACCEPT3D " + line);
        }

        static void Fail(string label, string message) => Log($"FAIL {label}: {message}");
    }
}
