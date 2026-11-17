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
                File.WriteAllLines(Path.Combine(Out, "render.txt"), Lines);
                EditorApplication.Exit(code);
            }
        };

        static JObject LoadState() => File.Exists(StatePath) ? Json.ParseObject(File.ReadAllText(StatePath)) ?? new JObject() : new JObject();

        static async Task RunPaid()
        {
            var ledger = new List<string>();
            var failures = 0;
            Directory.CreateDirectory(Out);
            try
            {
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
                    var missing = c.From.Values.Where(step => state[step]?["asset"] == null).ToList();
                    if (missing.Count > 0)
                    {
                        Log($"FAIL {c.Name}: needs {string.Join(", ", missing)} first");
                        failures++;
                        break;
                    }
                    var (row, quoted, problems) = await Send(c, state);
                    var ok = problems.Count == 0;
                    if (ok)
                    {
                        var file = row.Files.FirstOrDefault(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) ?? row.Files.FirstOrDefault() ?? "";
                        state[c.Name] = new JObject
                        {
                            ["job"] = row.JobId,
                            ["asset"] = Provenance.Read(file)?.AssetId is string id && id.Length > 0 ? id : row.AssetIds.FirstOrDefault() ?? "",
                            ["file"] = file,
                            ["files"] = new JArray(row.Files),
                            ["placed"] = new JArray(row.Placed),
                        };
                        File.WriteAllText(StatePath, state.ToString());
                        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
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
                    File.AppendAllText(Ledger, string.Join("\n", section) + "\n");
                }
                File.WriteAllLines(Path.Combine(Out, "paid.txt"), Lines);
                Log($"STORY done failures={failures}");
                EditorApplication.Exit(failures > 0 ? 1 : 0);
            }
        }

        /// <summary>The window's path: the form from the live schema, its presets, a price check, one send, then the import and placement.</summary>
        static async Task<(JobRow row, double quoted, List<string> problems)> Send(Case c, JObject state)
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
            var started = await S.Generate(request);
            if (!started.Ok)
                return (S.Ledger.Jobs.LastOrDefault(), quote.Value.Cu, new List<string> { (started.Error.Ambiguous ? "UNKNOWN outcome, check Jobs: " : "not sent: ") + started.Error.Message });
            var row = started.Value;
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

            // The finished scene: only the sand, the cheering hunter and the chest, at golden hour under the generated sky.
            var keep = new HashSet<GameObject>();
            var sand = GameObject.Find("Sand");
            keep.Add(sand);
            var cheerPath = (string)state["cheer"]?["placed"]?.FirstOrDefault();
            var hunter = cheerPath != null ? Placer.FindByPath(cheerPath) : null;
            if (hunter == null) throw new Exception("no placed cheer");
            keep.Add(hunter);
            hunter.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, 200, 0));
            World.Ground(hunter, 0, sand);
            var chestPath = (string)state["chest"]?["placed"]?.FirstOrDefault();
            var chest = chestPath != null ? Placer.FindByPath(chestPath) : null;
            if (chest == null) throw new Exception("no placed chest");
            keep.Add(chest);
            chest.transform.SetPositionAndRotation(new Vector3(1.1f, 0, -0.6f), Quaternion.Euler(0, 230, 0));
            SceneActions.SetHeight(chest, 0.7f);
            World.Ground(chest, 0, sand);
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (!keep.Contains(root) && root.GetComponent<Light>() == null) root.SetActive(false);
            var mood = SceneActions.SetMood("golden_hour");
            Log("mood: " + mood.Message);

            var animator = hunter.GetComponentInChildren<Animator>(true);
            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            var clip = controller != null ? controller.animationClips.FirstOrDefault(a => a.length > 0.1f) : null;
            var baked = Media.Skins(hunter);
            try
            {
                if (clip != null) clip.SampleAnimation(animator.gameObject, clip.length * 0.45f);
                Media.Bake(baked);
                var focus = Media.BoundsOf(hunter).center;
                Write(Shot(focus + new Vector3(-1.4f, 0.1f, -3.4f), focus + new Vector3(0.35f, -0.15f, 0), 1600, 900, 40), "scene.png");
            }
            finally
            {
                Media.Unbake(baked);
            }
            Log("rendered " + string.Join(", ", Directory.GetFiles(Out, "*.png").Select(Path.GetFileName)));
        }

        static Texture2D Shot(Vector3 from, Vector3 at, int width, int height, float fov)
        {
            var holder = new GameObject("Story Camera");
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            try
            {
                var camera = holder.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = fov;
                camera.transform.position = from;
                camera.transform.LookAt(at);
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 1000;
                camera.targetTexture = rt;
                camera.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
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
                Object.DestroyImmediate(holder);
            }
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
