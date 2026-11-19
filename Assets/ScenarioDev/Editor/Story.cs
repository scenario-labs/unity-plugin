using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): the handbook's worked example, one treasure hunter from a prompt to a
    /// dressed scene, sent through the plugin as the window sends it.
    /// Paid (needs SCENARIO_STORY=1 and spending on): generate, retexture, rig, a library motion, a motion
    /// from a prompt, a chest, the sand and the sky, each priced first, refused above its cap and sent once. Each done
    /// step is kept in Library/ScenarioDevLogs/story/story.json and never sent again; rows go to
    /// tests/live/LEDGER.md. The scene is saved to Assets/Scenario/Story.unity.
    /// Render (free, needs SCENARIO_NO_SPEND=1): the finished scene at golden hour and the before-and-after
    /// stills, to Library/ScenarioDevLogs/story.
    /// Run with -batchmode -executeMethod ScenarioDev.Story.Paid (or .Render).
    /// </summary>
    public static class Story
    {
        sealed class Case
        {
            public string Name, Type, Model, Prompt;
            public double Cap;
            /// <summary>Inputs from earlier steps: field name to step name (its model's asset id).</summary>
            public Dictionary<string, string> From = new Dictionary<string, string>();
            public JObject Inputs = new JObject();
            /// <summary>The step whose placed result is the placement target, or "sand".</summary>
            public string Target;
        }

        static readonly Case[] Cases =
        {
            new Case
            {
                Name = "hunter", Type = OutputTypes.Model3D, Model = "model_meshy-7-1-txt23d", Cap = 150,
                Prompt = "A stylized treasure hunter, full body, wide-brimmed hat, rolled-up sleeves, leather satchel and boots, game character",
                Inputs = new JObject { ["poseMode"] = "a-pose" },
            },
            new Case
            {
                Name = "retexture", Type = OutputTypes.Model3D, Model = "model_meshy-7-retexture", Cap = 150, Target = "hunter",
                Prompt = "Desert explorer: sand-colored linen shirt, olive-green jacket, worn brown leather, brass buckles",
                From = { ["model"] = "hunter" },
            },
            new Case
            {
                Name = "rig", Type = OutputTypes.Model3D, Model = "model_meshy-rigging", Cap = 150, Target = "retexture",
                Prompt = "Rig the treasure hunter", From = { ["model"] = "retexture" }, Inputs = new JObject { ["heightMeters"] = 1.8 },
            },
            new Case
            {
                Name = "cheer", Type = OutputTypes.Motion, Model = "model_meshy-animation", Cap = 100, Target = "retexture",
                Prompt = "Victory Cheer", From = { ["model"] = "retexture" }, Inputs = new JObject { ["actionId"] = 59, ["heightMeters"] = 1.8 },
            },
            new Case
            {
                Name = "dig", Type = OutputTypes.Motion, Model = "model_uthana-text-to-motion-3.0", Cap = 240, Target = "retexture",
                Prompt = "Looks around, kneels and digs in the sand with both hands, then stands up and lifts something high above the head",
                From = { ["characterFile"] = "retexture" },
            },
            new Case
            {
                Name = "chest", Type = OutputTypes.Model3D, Model = "model_rodin-hyper3d-v2-5-text-to-3d", Cap = 70,
                Prompt = "An open wooden treasure chest overflowing with gold coins, iron bands, stylized game prop",
            },
            new Case
            {
                Name = "sand", Type = OutputTypes.Material, Model = "model_patina-material", Cap = 40, Target = "sand",
                Prompt = "Fine desert sand with small pebbles and soft wind ripples",
            },
            new Case
            {
                Name = "sky", Type = OutputTypes.Skybox, Model = "model_scenario-skybox-gpt", Cap = 60,
                Prompt = "A desert canyon at golden hour, red rock arches, warm hazy sky, distant dunes",
            },
            // The hero shot's set, in more detail: a sky of dunes upscaled four times, clean sand, a chest at
            // Rodin's high tier with 4K textures, and the shovel and coins of the find.
            new Case
            {
                Name = "dunes-sky", Type = OutputTypes.Skybox, Model = "model_scenario-skybox-gpt", Cap = 60,
                Prompt = "Endless golden sand dunes rolling to the horizon at golden hour, the sun low and glowing just above the horizon, a dramatic sky of long luminous clouds, faint distant mesas in the warm haze, cinematic, highly detailed",
            },
            new Case
            {
                Name = "dunes-sky-4x", Type = OutputTypes.Skybox, Model = "model_sc-upscale-flux-skybox", Cap = 150,
                Prompt = "Golden sand dunes and a dramatic golden hour sky, crisp cloud detail, fine sand ripples",
                From = { ["image"] = "dunes-sky" }, Inputs = new JObject { ["upscaleFactor"] = 4, ["baseModel"] = "FLUX.1-Krea-dev" },
            },
            new Case
            {
                Name = "clean-sand", Type = OutputTypes.Material, Model = "model_patina-material", Cap = 40, Target = "sand",
                Prompt = "Fine clean golden desert sand with soft wind ripples, no stones",
            },
            new Case
            {
                Name = "rich-chest", Type = OutputTypes.Model3D, Model = "model_rodin-hyper3d-v2-5-text-to-3d", Cap = 200,
                Prompt = "An open ornate wooden treasure chest overflowing with shiny gold coins, jewels and pearls, brass corners and iron bands, weathered wood, highly detailed game prop",
                Inputs = new JObject { ["tier"] = "Gen-2.5-High", ["addons"] = "HighPack", ["qualityMeshOption"] = "150K Triangle", ["material"] = "PBR", ["hdTexture"] = true },
            },
            new Case
            {
                Name = "shovel", Type = OutputTypes.Model3D, Model = "model_rodin-hyper3d-v2-5-text-to-3d", Cap = 90,
                Prompt = "A worn shovel with a long wooden handle and a rusty iron blade, highly detailed game prop",
                Inputs = new JObject { ["material"] = "PBR", ["hdTexture"] = true },
            },
            new Case
            {
                Name = "coins", Type = OutputTypes.Model3D, Model = "model_rodin-hyper3d-v2-5-text-to-3d", Cap = 90,
                Prompt = "A low scattered pile of shiny gold coins with a few red and green gems, highly detailed game prop",
                Inputs = new JObject { ["material"] = "PBR", ["hdTexture"] = true },
            },
        };

        const string Ledger = "tests/live/LEDGER.md";
        const string ScenePath = "Assets/Scenario/Story.unity";
        const string Out = "Library/ScenarioDevLogs/story";
        static string StatePath => Path.Combine(Out, "story.json");
        static readonly List<string> Lines = new List<string>();
        static ScenarioService S => ScenarioService.Instance;

        public static void Paid() => EditorApplication.delayCall += () => _ = RunPaid();

        public static void Render() => EditorApplication.delayCall += () =>
        {
            var code = 0;
            try { RenderAll(); }
            catch (Exception e)
            {
                code = 1;
                Log("FAIL render " + e);
            }
            finally
            {
                Finish("render.txt", code);
            }
        };

        /// <summary>Writes the run's log, then exits the batch editor even when the log cannot be written.</summary>
        static void Finish(string log, int code)
        {
            try
            {
                Directory.CreateDirectory(Out);
                File.WriteAllLines(Path.Combine(Out, log), Lines);
            }
            catch (Exception e)
            {
                Debug.LogError("STORY could not write " + log + ": " + e.Message);
            }
            finally
            {
                EditorApplication.Exit(code);
            }
        }

        /// <summary>
        /// The steps already sent. No file is a first run; a file that cannot be read, or a write cut short, stops
        /// the run before anything is sent: it may hold steps already paid for.
        /// </summary>
        static JObject LoadState()
        {
            if (File.Exists(StatePath))
                return Json.ParseObject(File.ReadAllText(StatePath)) ?? throw new Exception($"{StatePath} cannot be read: restore it, it holds the steps already paid for");
            if (File.Exists(StatePath + ".tmp")) throw new Exception($"{StatePath}.tmp is left from a write cut short: check it and rename it to story.json");
            return new JObject();
        }

        /// <summary>Writes a temporary file first, so a write cut short keeps the previous checkpoints.</summary>
        static void SaveState(JObject state)
        {
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, state.ToString());
            if (File.Exists(StatePath)) File.Delete(StatePath);
            File.Move(tmp, StatePath);
        }

        static async Task RunPaid()
        {
            var ledger = new List<string>();
            var failures = 0;
            try
            {
                Directory.CreateDirectory(Out);
                if (!S.Ready || Environment.GetEnvironmentVariable("SCENARIO_STORY") != "1" || S.Client.SpendingDisabled)
                {
                    Log($"FAIL setup: needs a signed-in account, SCENARIO_STORY=1 and spending on (state {S.State}, credentials {S.Client.HasCredentials}, needs workspace {S.Client.NeedsWorkspace}, spending off {S.Client.SpendingDisabled})");
                    failures++;
                    return;
                }
                await S.RefreshCatalog(true);
                var state = LoadState();
                if (File.Exists(ScenePath)) EditorSceneManager.OpenScene(ScenePath);
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                    var sand = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    sand.name = "Sand";
                    sand.transform.localScale = new Vector3(4, 1, 4);
                    Directory.CreateDirectory("Assets/Scenario");
                    EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
                }
                foreach (var c in Cases)
                {
                    if (state[c.Name]?["asset"] != null)
                    {
                        Log($"skip {c.Name}: done in an earlier run");
                        continue;
                    }
                    if (state[c.Name]?["job"] != null)
                    {
                        Log($"FAIL {c.Name}: job {state[c.Name]["job"]} was sent in an earlier run and did not finish here; import it from Jobs, never send it again");
                        failures++;
                        break;
                    }
                    if (state[c.Name]?["sending"] != null)
                    {
                        Log($"FAIL {c.Name}: an earlier run sent it with an unknown outcome; settle it in Jobs (import it if it ran), never send it again");
                        failures++;
                        break;
                    }
                    var missing = c.From.Values.Where(step => state[step]?["asset"] == null).ToList();
                    if (missing.Count > 0)
                    {
                        Log($"FAIL {c.Name}: needs {string.Join(", ", missing)} first");
                        failures++;
                        break;
                    }
                    var (row, quoted, problems) = await Send(c, state, mark =>
                    {
                        // Recorded before the paid request, then as soon as Scenario has the job (before the wait
                        // and the import); removed only when nothing ran.
                        if (mark != null) state[c.Name] = mark;
                        else state.Remove(c.Name);
                        SaveState(state);
                    });
                    var ok = problems.Count == 0;
                    if (ok)
                    {
                        var file = row.Files.FirstOrDefault(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) ?? row.Files.FirstOrDefault() ?? "";
                        // The placement counts only once the scene holding it is saved.
                        var saved = EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
                        state[c.Name] = new JObject
                        {
                            ["job"] = row.JobId,
                            ["asset"] = Provenance.Read(file)?.AssetId is string id && id.Length > 0 ? id : row.AssetIds.FirstOrDefault() ?? "",
                            ["file"] = file,
                            ["files"] = new JArray(row.Files),
                            ["placed"] = saved ? new JArray(row.Placed) : new JArray(),
                        };
                        SaveState(state);
                        if (!saved)
                        {
                            problems.Add($"{ScenePath} could not be saved: place {Path.GetFileName(file)} again, it is imported");
                            ok = false;
                        }
                    }
                    var charged = row?.ChargedCu.HasValue == true ? row.ChargedCu.Value.ToString() : "?";
                    var files = row != null ? string.Join(", ", row.Files.Select(Path.GetFileName)) : "";
                    ledger.Add($"| story {c.Name} | `{c.Model}` | `{row?.JobId ?? ""}` | {quoted} | {charged} | {(ok ? "ok: " + files : "FAIL " + string.Join("; ", problems)).Replace("|", "/")} |");
                    Log($"{(ok ? "OK" : "FAIL")} {c.Name}: job {row?.JobId} quoted {quoted} charged {charged} {files} {string.Join("; ", problems)}");
                    if (!ok)
                    {
                        failures++;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Log("FAIL exception " + e);
                failures++;
            }
            finally
            {
                if (ledger.Count > 0)
                {
                    var section = new List<string>
                    {
                        "",
                        $"## {DateTime.Now:yyyy-MM-dd HH:mm} handbook story run ({(failures == 0 ? "pass" : failures + " failure(s)")})",
                        "",
                        "| Case | Model or workflow | Job | Quoted CU | Charged CU | Result |",
                        "|---|---|---|---|---|---|",
                    };
                    section.AddRange(ledger);
                    try { File.AppendAllText(Ledger, string.Join("\n", section) + "\n"); }
                    catch (Exception e)
                    {
                        // The jobs and charges are in this run's log lines above.
                        Log($"FAIL could not append to {Ledger}: {e.Message}");
                        failures++;
                    }
                }
                Log($"STORY done failures={failures}");
                Finish("paid.txt", failures > 0 ? 1 : 0);
            }
        }

        /// <summary>The window's path: the form from the live schema, its presets, a price check, one send, then the import and placement.</summary>
        static async Task<(JobRow row, double quoted, List<string> problems)> Send(Case c, JObject state, Action<JObject> checkpoint)
        {
            var schema = await S.Schema(c.Model);
            if (!schema.Ok) return (null, 0, new List<string> { "schema: " + schema.Error.Message });
            var fields = SchemaForm.FieldsFromSchema(schema.Value, OutputTypes.Restrict);
            var values = SchemaForm.InitialValues(fields, OutputTypes.Presets(c.Model));
            var promptName = SchemaForm.PromptName(fields);
            if (promptName.Length > 0) values[promptName] = c.Prompt;
            foreach (var input in c.Inputs) values[input.Key] = input.Value;
            foreach (var from in c.From) values[from.Key] = (string)state[from.Value]["asset"];
            var problems = SchemaForm.Validate(fields, values, c.Model).Select(p => "form: " + p).ToList();
            if (problems.Count > 0) return (null, 0, problems);
            var request = new GenerateRequest
            {
                OutputType = c.Type, ModelId = c.Model, ModelName = S.Model(c.Model)?.Name ?? c.Model,
                Payload = SchemaForm.Payload(fields, values), Prompt = c.Prompt, Place = true, Origin = "story", Target = TargetOf(c, state),
            };
            var quote = await S.Estimate(request);
            if (!quote.Ok || S.EstimateAlarm) return (null, 0, new List<string> { "price: " + (quote.Ok ? "a price check returned a job" : quote.Error.Message) });
            if (quote.Value.Cu > c.Cap) return (null, quote.Value.Cu, new List<string> { $"not sent: {quote.Value.Cu} CU is above the cap of {c.Cap}" });
            // A lost answer (or the editor closing) keeps the step blocked here, even once Jobs links the request
            // to its job and the ledger stops blocking the same request.
            checkpoint(new JObject { ["sending"] = S.Fingerprint(request) });
            var before = new HashSet<string>(S.Ledger.Jobs.Select(r => r.LocalId));
            var started = await S.Generate(request);
            if (!started.Ok)
            {
                // This request's own ledger row, none when it was refused before one was written. The step opens
                // again only when nothing ran and no earlier send of the same request is still unresolved.
                var intent = S.Ledger.Jobs.FirstOrDefault(r => !before.Contains(r.LocalId));
                if (!started.Error.Ambiguous && !S.Ledger.Blocks(S.Fingerprint(request))) checkpoint(null);
                return (intent, quote.Value.Cu, new List<string> { (started.Error.Ambiguous ? "UNKNOWN outcome, check Jobs: " : "not sent: ") + started.Error.Message });
            }
            var row = started.Value;
            checkpoint(new JObject { ["job"] = row.JobId });
            Log($"sent {c.Name}: {request.ModelName} job {row.JobId} quoted {quote.Value.Cu}");
            var start = Json.Now();
            while (!(row.IsFinal && (row.State != JobState.Success || row.Imported || row.Error.StartsWith("Import failed"))) && Json.Now() - start < 1500)
                await Task.Delay(2000);
            if (row.State != JobState.Success) problems.Add($"state {row.State} {row.Error}");
            else if (!row.Imported) problems.Add("not imported " + row.Error);
            return (row, quote.Value.Cu, problems);
        }

        static GameObject TargetOf(Case c, JObject state)
        {
            if (c.Target == "sand") return GameObject.Find("Sand");
            var placed = c.Target != null ? (string)state[c.Target]?["placed"]?.FirstOrDefault() : null;
            return placed != null ? Placer.FindByPath(placed) : null;
        }

        // --- Render ----------------------------------------------------------------

        static void RenderAll()
        {
            if (Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") != "1") throw new Exception("SCENARIO_NO_SPEND=1 is required: the render never spends.");
            Directory.CreateDirectory(Out);
            var state = LoadState();
            EditorSceneManager.OpenScene(ScenePath);

            // Before and after: the generated hunter and its retexture, from one camera.
            foreach (var step in new[] { "hunter", "retexture" })
            {
                var file = (string)state[step]?["file"];
                if (string.IsNullOrEmpty(file)) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(file));
                go.transform.position = new Vector3(100, 0, 100);
                var bounds = Media.BoundsOf(go);
                Write(Media.Frame(bounds.center, bounds.extents.magnitude, 1.0f), $"still-{step}.png");
                Object.DestroyImmediate(go);
            }

            Hero(state);
            Log("rendered " + string.Join(", ", Directory.GetFiles(Out, "*.png").Select(Path.GetFileName)));
        }

        /// <summary>The pose to show in the hero shot: STORY_POSE as "cheer:0.15" or "dig:0.8" (step and point in its clip).</summary>
        static (string step, float time) HeroPose()
        {
            var parts = (Environment.GetEnvironmentVariable("STORY_POSE") ?? "cheer:0.15").Split(':');
            return (parts[0], parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0.15f);
        }

        /// <summary>Contact sheet frames to choose the hero pose: the cheer and the dig, every tenth of their clips.</summary>
        public static void Poses() => EditorApplication.delayCall += () =>
        {
            var code = 0;
            try
            {
                var state = LoadState();
                EditorSceneManager.OpenScene(ScenePath);
                foreach (var step in new[] { "cheer", "dig" })
                {
                    var go = Placed(state, step);
                    ShowOnly(go);
                    go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    var (animator, clip) = ClipOf(go);
                    var baked = Media.Skins(go);
                    try
                    {
                        for (var i = 0; i < 10; i++)
                        {
                            var t = 0.05f + i * 0.1f;
                            clip.SampleAnimation(animator.gameObject, clip.length * t);
                            Media.Bake(baked);
                            var bounds = Baked(baked);
                            Write(Media.Frame(bounds.center, bounds.extents.magnitude, 1.0f), $"pose-{step}-{t:0.00}.png");
                        }
                    }
                    finally
                    {
                        Media.Unbake(baked);
                    }
                }
            }
            catch (Exception e)
            {
                code = 1;
                Log("FAIL poses " + e);
            }
            finally
            {
                Finish("poses.txt", code);
            }
        };

        static GameObject Placed(JObject state, string step) => PlacedOrNull(state, step) ?? throw new Exception("no placed " + step);

        /// <summary>The step's object in the open scene, or null when it has none (not run, not finished, or not saved).</summary>
        static GameObject PlacedOrNull(JObject state, string step)
        {
            var path = (string)state[step]?["placed"]?.FirstOrDefault();
            return path != null ? Placer.FindByPath(path) : null;
        }

        static void ShowOnly(params GameObject[] keep)
        {
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                root.SetActive(keep.Contains(root) || root.GetComponent<Light>() != null);
        }

        static (Animator animator, AnimationClip clip) ClipOf(GameObject go)
        {
            var animator = go.GetComponentInChildren<Animator>(true);
            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            var clip = controller != null ? controller.animationClips.FirstOrDefault(a => a.length > 0.1f) : null;
            if (clip == null) throw new Exception("no clip on " + go.name);
            return (animator, clip);
        }

        static Bounds Baked(List<(SkinnedMeshRenderer skin, GameObject proxy, Mesh mesh)> baked)
        {
            var renderers = baked.Select(b => b.proxy.GetComponent<MeshRenderer>()).ToList();
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        // The finished scene: the hunter cheering on a dune crest at golden hour, the chest he dug up half buried
        // beside him with its spilled coins and his shovel, under the generated sky of dunes. The sky's sun sits on
        // the horizon at +X (a panoramic skybox shows the middle of its image there), a little right of the frame's
        // middle; the light comes from a little nearer the camera so his face catches it. The camera frames the
        // bounds of everything so nothing is cut.
        static void Hero(JObject state)
        {
            var (step, time) = HeroPose();
            const float look = 68, sunYaw = 90, lightYaw = 130;
            var forward = Quaternion.Euler(0, look, 0) * Vector3.forward;
            var right = Quaternion.Euler(0, look + 90, 0) * Vector3.forward;
            var hunter = Placed(state, step);
            // The hero set's upgrades only when placed: else the first chest, and no shovel or coins.
            var chest = PlacedOrNull(state, "rich-chest") ?? Placed(state, "chest");
            var props = new[] { "shovel", "coins" }.Select(p => (name: p, go: PlacedOrNull(state, p))).Where(p => p.go != null).ToDictionary(p => p.name, p => p.go);
            ShowOnly(props.Values.Append(hunter).Append(chest).ToArray());
            var dunes = Dunes(state);
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var shadowDistance = urp != null ? urp.shadowDistance : 0;
            if (urp != null) urp.shadowDistance = 30;
            var (animator, clip) = ClipOf(hunter);
            var baked = Media.Skins(hunter);
            try
            {
                // glTF models face +Z: he faces the camera, turned toward the sun.
                hunter.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, look + 180 - 48, 0));
                clip.SampleAnimation(animator.gameObject, clip.length * time);
                Media.Bake(baked);
                var feet = Baked(baked);
                hunter.transform.position += new Vector3(-feet.center.x, GroundAt(dunes, Vector3.zero) - feet.min.y - 0.02f, -feet.center.z);
                Media.Bake(baked);

                Set(chest, dunes, right * 1.35f - forward * 0.55f, Quaternion.Euler(3, look + 180 + 25, -2), height: 0.72f, sink: 0.14f);
                if (props.TryGetValue("coins", out var coins))
                    Set(coins, dunes, right * 1.95f - forward * 1.15f, Quaternion.Euler(0, look + 40, 0), width: 0.7f, sink: 0.03f);
                if (props.TryGetValue("shovel", out var shovel))
                    Set(shovel, dunes, -right * 0.8f + forward * 0.35f, Quaternion.AngleAxis(180 - 14, forward) * Quaternion.Euler(0, look + 60, 0), height: 1.15f, sink: 0.38f);

                var mood = Atmosphere.Apply(new Atmosphere.Spec { Preset = "golden_hour", SunElevation = 11, SunAzimuth = lightYaw + 180, Temperature = 0, Exposure = 0, Saturation = 0, Contrast = 14, Bloom = 0.5f });
                DynamicGI.UpdateEnvironment();
                Log($"mood: {string.Join(" ", mood.Notes)} sun in the sky at {sunYaw}, light from {lightYaw}");
                var subject = Baked(baked);
                subject.Encapsulate(World.BoundsOf(chest));
                foreach (var prop in props.Values) subject.Encapsulate(World.BoundsOf(prop));
                Write(Shot(subject, dunes, forward), "scene.png");
            }
            finally
            {
                Media.Unbake(baked);
                if (urp != null) urp.shadowDistance = shadowDistance;
                Object.DestroyImmediate(dunes.gameObject);
            }
        }

        static float GroundAt(Terrain dunes, Vector3 point) => dunes.SampleHeight(point) + dunes.transform.position.y;

        /// <summary>Places a prop on the dunes at this height (or footprint width), its base sunk into the sand.</summary>
        static void Set(GameObject go, Terrain dunes, Vector3 at, Quaternion rotation, float height = 0, float width = 0, float sink = 0)
        {
            go.transform.SetPositionAndRotation(at, rotation);
            if (height > 0) World.FitHeight(go, height);
            else
            {
                var size = World.BoundsOf(go).size;
                go.transform.localScale *= width / Mathf.Max(size.x, size.z, 1e-4f);
            }
            var bounds = World.BoundsOf(go);
            go.transform.position += new Vector3(at.x - bounds.center.x, GroundAt(dunes, bounds.center) - bounds.min.y - sink, at.z - bounds.center.z);
        }

        /// <summary>A broad dune crest in the generated sand, falling away so the sky's own dunes and sun meet the horizon.</summary>
        static Terrain Dunes(JObject state)
        {
            var files = (state["clean-sand"] ?? state["sand"])?["files"]?.Select(f => (string)f).ToList() ?? new List<string>();
            Texture2D Map(string kind) => AssetDatabase.LoadAssetAtPath<Texture2D>(files.FirstOrDefault(f => f.EndsWith($"-{kind}.png")) ?? "");
            const int resolution = 1025;
            const float size = 400, height = 80, top = 60;
            var data = new TerrainData { heightmapResolution = resolution, size = new Vector3(size, height, size) };
            var heights = new float[resolution, resolution];
            for (var z = 0; z < resolution; z++)
            for (var x = 0; x < resolution; x++)
                heights[z, x] = Mathf.Clamp01((Dune((x / (resolution - 1f) - 0.5f) * size, (z / (resolution - 1f) - 0.5f) * size) + top) / height);
            data.SetHeights(0, 0, heights);
            data.terrainLayers = new[]
            {
                new TerrainLayer { diffuseTexture = Map("albedo"), normalMapTexture = Map("normal"), tileSize = new Vector2(2.2f, 2.2f), smoothness = 0.08f, normalScale = 1.4f },
            };
            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Story Dunes";
            go.transform.position = new Vector3(-size / 2, -top - Dune(0, 0), -size / 2);
            var terrain = go.GetComponent<Terrain>();
            terrain.heightmapPixelError = 1;
            terrain.basemapDistance = 2000;
            terrain.materialTemplate = GraphicsSettings.currentRenderPipeline?.defaultTerrainMaterial;
            terrain.Flush();
            return terrain;
        }

        static float Dune(float x, float z)
        {
            var r = Mathf.Sqrt(x * x + z * z);
            // Away from the sun (+X), dunes rise behind him; toward it the crest falls away to the sky's horizon.
            var away = r > 1e-3f ? Mathf.InverseLerp(0.8f, 0.35f, x / r) : 0;
            var crest = -0.0009f * r * r * (1 - 0.92f * away);
            var dunes = away * Mathf.InverseLerp(30, 90, r) * (Mathf.Pow(1 - Mathf.Abs(Mathf.Sin((x * 0.5f - z * 0.85f + Mathf.PerlinNoise(x * 0.01f, z * 0.01f) * 40) * 0.035f)), 2) * 9f + Mathf.PerlinNoise(x * 0.012f + 5, z * 0.012f + 9) * 5f);
            var along = x * 0.8f + z * 0.6f;
            var warp = Mathf.PerlinNoise(x * 0.02f + 3, z * 0.02f + 7) * 12;
            var ridges = (1 - Mathf.Abs(Mathf.Sin((along + warp) * 0.11f))) * 0.45f * Mathf.InverseLerp(4, 25, r);
            var swell = (Mathf.PerlinNoise(x * 0.04f + 1, z * 0.04f + 2) - 0.5f) * 0.6f;
            return crest + ridges + swell + dunes;
        }

        /// <summary>
        /// A 2400 by 1350 frame of the subject, rendered at twice the size with the URP post-processing on and scaled
        /// down. The camera, low and looking along `forward`, backs away until the subject's corners sit inside the
        /// frame's margins, the subject on the left third.
        /// </summary>
        static Texture2D Shot(Bounds subject, Terrain dunes, Vector3 forward)
        {
            const int width = 2400, height = 1350, scale = 2;
            const float fov = 34, eye = 0.5f, shift = 0.17f;
            var holder = new GameObject("Story Camera");
            var rt = new RenderTexture(width * scale, height * scale, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            var small = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                var camera = holder.AddComponent<Camera>();
                var data = camera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 2000;
                camera.aspect = (float)width / height;
                camera.fieldOfView = fov;
                for (var distance = 3f; distance < 30; distance += 0.1f)
                {
                    var position = subject.center - forward * distance;
                    position.y = GroundAt(dunes, position) + eye;
                    camera.transform.position = position;
                    camera.transform.LookAt(new Vector3(subject.center.x, subject.min.y + subject.size.y * 0.6f, subject.center.z));
                    camera.transform.position += camera.transform.right * (distance * Mathf.Tan(Camera.VerticalToHorizontalFieldOfView(fov, camera.aspect) * 0.5f * Mathf.Deg2Rad) * shift * 2);
                    if (Inside(camera, subject, 0.05f)) break;
                }
                camera.targetTexture = rt;
                camera.Render();
                Graphics.Blit(rt, small);
                var previous = RenderTexture.active;
                RenderTexture.active = small;
                var frame = new Texture2D(width, height, TextureFormat.RGB24, false);
                frame.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                frame.Apply();
                RenderTexture.active = previous;
                camera.targetTexture = null;
                return frame;
            }
            finally
            {
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(small);
                Object.DestroyImmediate(holder);
            }
        }

        static bool Inside(Camera camera, Bounds b, float margin)
        {
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                var v = camera.WorldToViewportPoint(corner);
                if (v.z <= 0 || v.x < margin || v.x > 1 - margin || v.y < margin || v.y > 1 - margin) return false;
            }
            return true;
        }

        static void Write(Texture2D frame, string name)
        {
            File.WriteAllBytes(Path.Combine(Out, name), frame.EncodeToPNG());
            Object.DestroyImmediate(frame);
        }

        static void Log(string line)
        {
            Lines.Add(line);
            Debug.Log("STORY " + line);
        }
    }
}
