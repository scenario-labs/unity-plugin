using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Video;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): the paid acceptance run. One real generation per
    /// output type through the plugin's own path (price, generate, poll, import, place),
    /// a typed step on an uploaded Unity texture, a workflow and Prompt Spark. Needs
    /// SCENARIO_ACCEPT=1 and the test key; each paid call is sent once and logged to
    /// tests/live/LEDGER.md. Run with -batchmode -executeMethod ScenarioDev.Acceptance.Run.
    /// SCENARIO_ACCEPT_CASES=material,skybox runs only the named cases (image, material,
    /// skybox, model3d, audio, video, step, workflow, spark), to re-run failures alone.
    /// SCENARIO_ACCEPT_RESUME=job_x,job_y checks jobs of an earlier run that were still
    /// running when it ended: the service resumes them after the restart, nothing is resent.
    /// </summary>
    public static class Acceptance
    {
        sealed class Case
        {
            public string Name;
            public string Type;
            public string Model;
            public string Workflow = "";
            public string Prompt;
            public Dictionary<string, JToken> Inputs = new Dictionary<string, JToken>();
            public GameObject Target;
            public int TimeoutSeconds = 300;
            public JobRow Row;
            public double Quoted;
            public string Result = "";
            public bool Ok;
        }

        const string Ledger = "tests/live/LEDGER.md";
        static readonly string Scene = $"Assets/Scenario/Acceptance-{DateTime.Now:yyyyMMdd-HHmm}.unity";
        static readonly List<string> Lines = new List<string>();
        static ScenarioService S => ScenarioService.Instance;

        public static void Run() => EditorApplication.delayCall += () => _ = RunAsync();

        static readonly string[] Only = (Environment.GetEnvironmentVariable("SCENARIO_ACCEPT_CASES") ?? "")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();

        static readonly string[] Resume = (Environment.GetEnvironmentVariable("SCENARIO_ACCEPT_RESUME") ?? "")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();

        static bool Selected(string name) => Only.Length == 0 || Only.Any(o => name.StartsWith(o, StringComparison.Ordinal));

        static async Task RunAsync()
        {
            var cases = new List<Case>();
            var failures = 0;
            try
            {
                if (Environment.GetEnvironmentVariable("SCENARIO_ACCEPT") != "1" || S.Client.SpendingDisabled || !S.Ready)
                {
                    Log("FAIL setup: needs SCENARIO_ACCEPT=1, spending on, and the test key.");
                    failures++;
                    return;
                }
                await S.RefreshCatalog(true);
                Directory.CreateDirectory("Assets/Scenario");
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Material Target";
                cube.transform.position = new Vector3(3, 0.5f, 0);

                var upload = Selected("step") ? await UploadSource() : null;
                if (upload != null && !upload.Ok) failures++;

                cases.Add(new Case { Name = "image", Type = OutputTypes.Image, Model = "model_openai-gpt-image-2-5-sunburst", Prompt = "A stylized wooden treasure chest, game prop, clean background", TimeoutSeconds = 240 });
                cases.Add(new Case { Name = "material", Type = OutputTypes.Material, Model = "model_patina-material", Prompt = "Weathered copper plates with green patina", Target = cube, TimeoutSeconds = 300 });
                cases.Add(new Case { Name = "skybox", Type = OutputTypes.Skybox, Model = "model_scenario-skybox-flux", Prompt = "Alien desert at golden hour, twin moons, soft haze", TimeoutSeconds = 900 });
                cases.Add(new Case { Name = "model3d", Type = OutputTypes.Model3D, Model = "model_rodin-hyper3d-v2-5-text-to-3d", Prompt = "A stylized wooden treasure chest, game-ready prop", TimeoutSeconds = 900 });
                cases.Add(new Case { Name = "audio", Type = OutputTypes.Audio, Model = "model_sonilo-v1-1-text-to-sound-effects", Prompt = "Short metallic sword swing with a whoosh", TimeoutSeconds = 300 });
                cases.Add(new Case { Name = "video", Type = OutputTypes.Video, Model = "model_alibaba-wan-3-0", Prompt = "Slow dolly shot through a misty pine forest at dawn", TimeoutSeconds = 900 });
                if (upload != null && upload.Ok)
                    cases.Add(new Case { Name = "step remove-bg (uploaded texture)", Type = OutputTypes.Image, Model = "model_birefnet-background-removal", Prompt = "acceptance-source", Inputs = { ["image"] = upload.Value }, TimeoutSeconds = 240 });

                cases.RemoveAll(c => !Selected(c.Name));
                foreach (var c in cases) await Submit(c);
                foreach (var jobId in Resume)
                {
                    var row = S.Ledger.FindByJob(jobId);
                    if (row == null) cases.Add(new Case { Name = "resumed " + jobId, Result = "not in this project's job list" });
                    else cases.Add(new Case { Name = $"resumed {row.OutputType} (after restart)", Type = row.OutputType, Model = row.ModelId, Workflow = row.WorkflowId, Prompt = row.Prompt, Row = row, Quoted = row.QuotedCu, TimeoutSeconds = 300 });
                }

                // The workflow takes the generated image as its input.
                var image = cases.FirstOrDefault(c => c.Name == "image");
                if (image != null && Selected("workflow")) await WaitFor(new[] { image });
                if (image?.Row != null && image.Row.AssetIds.Count > 0 && Selected("workflow"))
                {
                    var workflow = new Case { Name = "workflow True 4-Pose", Type = OutputTypes.All, Workflow = "wflow_true-4-pose", Prompt = "workflow", Inputs = { ["image1"] = image.Row.AssetIds[0] }, TimeoutSeconds = 600 };
                    cases.Add(workflow);
                    await Submit(workflow);
                }

                await WaitFor(cases);
                foreach (var c in cases) Check(c);

                if (Selected("spark"))
                {
                    var spark = await S.ImprovePrompt("model_openai-gpt-image-2-5-sunburst", "GPT Image 2.5 Sunburst", "treasure chest");
                    var sparkRow = S.Ledger.Jobs.LastOrDefault(r => r.OutputType == "prompt");
                    Log(spark.Ok ? $"OK prompt spark: \"{Short(spark.Value)}\"" : $"FAIL prompt spark: {spark.Error.Message}");
                    LedgerLine("prompt spark", "model_openai-gpt-image-2-5-sunburst", sparkRow?.JobId ?? "", sparkRow?.QuotedCu ?? 0, sparkRow?.ChargedCu, spark.Ok ? "ok" : "FAIL " + spark.Error.Message);
                    if (!spark.Ok) failures++;
                }

                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), Scene);
                failures += cases.Count(c => !c.Ok);
            }
            catch (Exception e)
            {
                Log("FAIL exception: " + e);
                failures++;
            }
            finally
            {
                WriteLedger(failures);
                Debug.Log($"ACCEPT done failures={failures}");
                EditorApplication.Exit(failures > 0 ? 1 : 0);
            }
        }

        /// <summary>A Unity texture made here and uploaded, the input of the typed step.</summary>
        static async Task<Result<string>> UploadSource()
        {
            var uploadPath = "Assets/Scenario/Uploads/acceptance-source.png";
            Directory.CreateDirectory(Path.GetDirectoryName(uploadPath));
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            for (var y = 0; y < 256; y++)
                for (var x = 0; x < 256; x++)
                {
                    var inside = (x - 128) * (x - 128) + (y - 128) * (y - 128) < 80 * 80;
                    texture.SetPixel(x, y, inside ? new Color(0.9f, 0.45f, 0.1f) : ((x / 32 + y / 32) % 2 == 0 ? Color.white : new Color(0.75f, 0.85f, 1f)));
                }
            File.WriteAllBytes(uploadPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(uploadPath, ImportAssetOptions.ForceSynchronousImport);
            var upload = await S.Upload(AssetDatabase.LoadAssetAtPath<Texture2D>(uploadPath));
            Log(upload.Ok ? $"OK upload {uploadPath} -> {upload.Value}" : $"FAIL upload: {upload.Error.Message}");
            return upload;
        }

        static async Task Submit(Case c)
        {
            List<Field> fields;
            string name;
            if (c.Workflow.Length > 0)
            {
                var wf = await S.Client.GetWorkflow(c.Workflow);
                if (!wf.Ok)
                {
                    c.Result = "workflow: " + wf.Error.Message;
                    return;
                }
                var definition = wf.Value["workflow"] as JObject ?? wf.Value;
                fields = SchemaForm.FieldsFromSchema(definition);
                name = Json.Str(definition, "name", c.Workflow);
            }
            else
            {
                var schema = await S.Schema(c.Model);
                if (!schema.Ok)
                {
                    c.Result = "schema: " + schema.Error.Message;
                    return;
                }
                fields = SchemaForm.FieldsFromSchema(schema.Value, OutputTypes.Restrict);
                name = S.Model(c.Model)?.Name ?? c.Model;
            }
            var values = SchemaForm.InitialValues(fields, c.Workflow.Length > 0 ? null : OutputTypes.Presets(c.Model));
            var promptName = SchemaForm.PromptName(fields);
            if (promptName.Length > 0 && c.Workflow.Length == 0) values[promptName] = c.Prompt;
            foreach (var input in c.Inputs)
            {
                var field = fields.FirstOrDefault(f => f.Name == input.Key);
                values[input.Key] = field != null && field.Type == FieldType.Files ? new JArray(input.Value) : input.Value;
            }
            var problems = SchemaForm.Validate(fields, values, c.Workflow.Length > 0 ? "" : c.Model);
            if (problems.Count > 0)
            {
                c.Result = "form: " + string.Join(" ", problems);
                return;
            }
            var request = new GenerateRequest
            {
                OutputType = c.Type, ModelId = c.Workflow.Length > 0 ? "" : c.Model, WorkflowId = c.Workflow, ModelName = name,
                Payload = SchemaForm.Payload(fields, values), Prompt = c.Prompt, Place = true, Target = c.Target, Origin = "acceptance",
            };
            var quote = await S.Estimate(request);
            if (!quote.Ok || S.EstimateAlarm)
            {
                c.Result = "price: " + (quote.Ok ? "a price check returned a job" : quote.Error.Message);
                return;
            }
            c.Quoted = quote.Value.Cu;
            var started = await S.Generate(request);
            if (!started.Ok)
            {
                c.Result = (started.Error.Ambiguous ? "UNKNOWN outcome: " : "not sent: ") + started.Error.Message;
                c.Row = S.Ledger.Jobs.LastOrDefault();
                return;
            }
            c.Row = started.Value;
            Log($"sent {c.Name}: {name} job {c.Row.JobId}");
        }

        static async Task WaitFor(IEnumerable<Case> cases)
        {
            var list = cases.Where(c => c.Row != null && c.Row.JobId.Length > 0).ToList();
            var start = Json.Now();
            while (list.Any(c => !Done(c.Row) && Json.Now() - start < c.TimeoutSeconds)) await Task.Delay(2000);
        }

        static bool Done(JobRow row) => row.IsFinal && (row.State != JobState.Success || row.Imported || row.Error.StartsWith("Import failed"));

        static void Check(Case c)
        {
            var row = c.Row;
            if (row == null || c.Result.Length > 0 && !c.Result.StartsWith("UNKNOWN"))
            {
                Log($"FAIL {c.Name}: {c.Result}");
                LedgerLine(c.Name, c.Workflow.Length > 0 ? c.Workflow : c.Model, row?.JobId ?? "", c.Quoted, row?.ChargedCu, "FAIL " + c.Result);
                return;
            }
            var problems = new List<string>();
            if (row.State != JobState.Success) problems.Add($"state {row.State} {row.Error}");
            else if (!row.Imported) problems.Add("not imported " + row.Error);
            var assets = row.Files.Select(AssetDatabase.LoadMainAssetAtPath).ToList();
            if (row.Imported && (row.Files.Count == 0 || assets.Any(a => a == null))) problems.Add("missing files");
            if (row.Imported)
                switch (c.Type)
                {
                    case OutputTypes.Image:
                        if (!assets.Any(a => a is Texture2D)) problems.Add("no texture");
                        if (row.Placed.Count == 0) problems.Add("not placed");
                        break;
                    case OutputTypes.Material:
                        var material = c.Target != null ? c.Target.GetComponent<Renderer>().sharedMaterial : null;
                        if (material == null || !AssetDatabase.GetAssetPath(material).EndsWith(".mat") || material.mainTexture == null) problems.Add("target has no built material");
                        break;
                    case OutputTypes.Skybox:
                        // A resumed job may have been placed in the startup scene, before this run opened its own.
                        var built = row.Files.Select(f => AssetDatabase.LoadAssetAtPath<Material>(Path.ChangeExtension(f, null) + "-skybox.mat")).FirstOrDefault(m => m != null);
                        if (built == null || built.shader.name != "Skybox/Panoramic") problems.Add("no skybox material");
                        else if (!c.Name.StartsWith("resumed") && RenderSettings.skybox != built) problems.Add("skybox not set");
                        break;
                    case OutputTypes.Model3D:
                        var model = assets.OfType<GameObject>().FirstOrDefault();
                        if (model == null || model.GetComponentInChildren<MeshFilter>() == null) problems.Add("no mesh");
                        if (row.Placed.Count == 0) problems.Add("not placed");
                        break;
                    case OutputTypes.Audio:
                        if (!assets.Any(a => a is AudioClip)) problems.Add("no audio clip");
                        break;
                    case OutputTypes.Video:
                        if (!assets.Any(a => a is VideoClip)) problems.Add("no video clip");
                        break;
                }
            c.Ok = problems.Count == 0;
            var files = string.Join(", ", row.Files.Select(Path.GetFileName));
            var placed = row.Placed.Count > 0 ? $"; placed {string.Join(", ", row.Placed)}" : "";
            Log($"{(c.Ok ? "OK" : "FAIL")} {c.Name}: {row.State}, {row.Files.Count} file(s) {files}{placed}{(c.Ok ? "" : " | " + string.Join("; ", problems))}");
            LedgerLine(c.Name, c.Workflow.Length > 0 ? c.Workflow : c.Model, row.JobId, c.Quoted, row.ChargedCu, c.Ok ? $"ok: {files}{placed}" : "FAIL " + string.Join("; ", problems));
        }

        static readonly List<string> Rows = new List<string>();

        static void LedgerLine(string name, string model, string job, double quoted, double? charged, string result)
            => Rows.Add($"| {name} | `{model}` | `{job}` | {quoted} | {(charged.HasValue ? charged.Value.ToString() : "?")} | {result.Replace("|", "/")} |");

        static void WriteLedger(int failures)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ledger));
            var header = File.Exists(Ledger) ? "" :
                "# Live test ledger\n\nPaid runs against the Scenario test project, one section per run. Each paid call is sent once; " +
                "an unknown outcome is checked in Jobs, never resent. Quoted = the free price check, charged = what the job reported.\n";
            var section = new List<string>
            {
                "",
                $"## {DateTime.Now:yyyy-MM-dd HH:mm} acceptance run{(Only.Length + Resume.Length > 0 ? " of " + string.Join(", ", Only.Concat(Resume.Select(j => "resumed " + j))) : "")} ({(failures == 0 ? "pass" : failures + " failure(s)")})",
                "",
                "| Case | Model or workflow | Job | Quoted CU | Charged CU | Result |",
                "|---|---|---|---|---|---|",
            };
            section.AddRange(Rows);
            File.AppendAllText(Ledger, header + string.Join("\n", section) + "\n");
        }

        static string Short(string text) => text.Length > 120 ? text.Substring(0, 120) + "…" : text;

        static void Log(string line)
        {
            Lines.Add(line);
            Debug.Log("ACCEPT " + line);
        }
    }
}
