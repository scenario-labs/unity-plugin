using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): the free live contract check. With the test key in
    /// SCENARIO_API_KEY/SECRET and SCENARIO_NO_SPEND=1, it prices every quick pick, every
    /// typed step and a workflow with the plugin's own code, and asks recommend per
    /// capability. Only free calls: it stops at once if spending is not switched off or if
    /// a price check ever returns a job. Run with -batchmode -executeMethod ScenarioDev.ContractCheck.Run.
    /// </summary>
    public static class ContractCheck
    {
        static readonly List<string> Lines = new List<string>();
        static int _failures;

        static ScenarioService S => ScenarioService.Instance;

        public static void Run() => EditorApplication.delayCall += () => _ = RunAsync();

        static async Task RunAsync()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("SCENARIO_NO_SPEND") != "1" || !S.Client.SpendingDisabled)
                {
                    Fail("setup", "SCENARIO_NO_SPEND=1 is required: this check never spends.");
                    return;
                }
                if (!S.Ready)
                {
                    Fail("setup", "not signed in (set SCENARIO_API_KEY and SCENARIO_API_SECRET).");
                    return;
                }
                await S.RefreshCatalog(true);
                Log($"catalog {S.Catalog.Models.Count} models");

                var sources = new Dictionary<string, string>();
                foreach (var kind in new[] { "image", "3d", "video", "audio" })
                {
                    // Scenario's search index has no audio: list audio assets by type.
                    var found = kind == "audio"
                        ? await S.Client.ListAssets(new JObject { ["types"] = new JArray("txt2audio", "audio2audio", "video2audio", "uploaded-audio") }, 1, "", includeUrls: false)
                        : await S.Client.Search(new JObject
                        {
                            ["target"] = "assets", ["query"] = "", ["filter"] = $"kind IN [\"{kind}\"]",
                            ["sort_by"] = new JArray("createdAt:desc"), ["limit"] = 1,
                        });
                    var id = found.Ok ? (string)(found.Value["assets"] as JArray)?.FirstOrDefault()?["id"] : null;
                    if (id != null) sources[kind] = id;
                    Log($"source {kind}: {id ?? "none in the project"}");
                }
                // SCENARIO_CONTRACT_SOURCES=3d=asset_x,video=asset_y,fbx=asset_z pins sources: the newest 3D asset may be a splat or a skeleton,
                // and fbx stands in for a converted copy (the upload counts as spending).
                foreach (var pair in (Environment.GetEnvironmentVariable("SCENARIO_CONTRACT_SOURCES") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=');
                    if (parts.Length != 2) continue;
                    sources[parts[0].Trim()] = parts[1].Trim();
                    Log($"source {parts[0].Trim()} pinned: {parts[1].Trim()}");
                }

                foreach (var type in OutputTypes.List.Where(t => t.QuickPicks.Length > 0))
                    foreach (var model in type.QuickPicks)
                    {
                        if (!await Price($"pick {type.Id}", model, type.Id, type.Placeholder, sources, null)) return;
                    }

                foreach (var step in OutputTypes.Steps)
                {
                    if (!sources.ContainsKey(step.Kind))
                    {
                        Log($"SKIP step {step.Kind}/{step.Id}: no {step.Kind} asset in the project");
                        continue;
                    }
                    foreach (var model in step.Models)
                        if (!await Price($"step {step.Kind}/{step.Id}", model, step.OutputType, "Make it look hand-painted", sources, step.Kind)) return;
                }

                foreach (var capability in new[] { "txt2img", "img2img", "txt2video", "img2video", "txt23d", "img23d", "txt2audio" })
                {
                    var picks = await S.Recommend(capability, "A stylized wooden treasure chest, game prop");
                    if (!picks.Ok)
                    {
                        Fail($"recommend {capability}", picks.Error.Message);
                        continue;
                    }
                    var ids = (picks.Value["ranked"] as JArray ?? new JArray())
                        .Select(m => (string)m["model_id"]).Where(i => i != null).ToList();
                    var missing = ids.Where(i => S.Model(i) == null).ToList();
                    if (ids.Count == 0) Fail($"recommend {capability}", "no models in the reply: " + Short(picks.Value.ToString()));
                    else if (missing.Count > 0) Fail($"recommend {capability}", "not in the catalog: " + string.Join(", ", missing));
                    else Log($"OK recommend {capability}: {string.Join(", ", ids.Take(3))}");
                }

                await PriceWorkflow(sources);
            }
            catch (Exception e)
            {
                Fail("exception", e.ToString());
            }
            finally
            {
                var path = Environment.GetEnvironmentVariable("SCENARIO_CONTRACT_REPORT");
                if (!string.IsNullOrEmpty(path)) File.WriteAllLines(path, Lines);
                Debug.Log($"CONTRACT done failures={_failures}");
                EditorApplication.Exit(_failures > 0 ? 1 : 0);
            }
        }

        /// <summary>Prices one model the way the window does. False only when the run must stop.</summary>
        static async Task<bool> Price(string label, string modelId, string outputType, string prompt, Dictionary<string, string> sources, string stepKind)
        {
            var model = S.Model(modelId);
            if (model == null)
            {
                Fail(label, $"{modelId} is not in the catalog");
                return true;
            }
            var schema = await S.Schema(modelId);
            if (!schema.Ok)
            {
                Fail(label, $"{modelId} schema: {schema.Error.Message}");
                return true;
            }
            var fields = SchemaForm.FieldsFromSchema(schema.Value, OutputTypes.Restrict);
            var values = SchemaForm.InitialValues(fields, OutputTypes.Presets(modelId));
            var promptName = SchemaForm.PromptName(fields);
            if (promptName.Length > 0) values[promptName] = prompt;
            var stepField = stepKind != null ? SchemaForm.FileInputFor(fields, stepKind) : null;
            if (stepKind != null && stepField == null)
            {
                Fail(label, $"{modelId} has no {stepKind} input");
                return true;
            }
            // An input Scenario needs although the schema marks it optional: the step's own input when it is one, else the first.
            var needed = OutputTypes.Needs(modelId).Select(n => n.Fields.Contains(stepField?.Name) ? stepField.Name : n.Fields[0]).ToList();
            foreach (var field in fields.Where(f => f.IsFile && (f == stepField || f.Required || needed.Contains(f.Name))))
            {
                var kind = string.IsNullOrEmpty(field.Kind) ? "image" : field.Kind.ToLowerInvariant();
                if (!sources.TryGetValue(kind, out var id))
                {
                    Log($"SKIP {label} {modelId}: needs a {kind} input and the project has none");
                    return true;
                }
                // Inputs that refuse GLB get a converted copy, as in the window.
                var format = SchemaForm.RequiredModelFormat(field);
                if (format.Length > 0 && sources.TryGetValue(format, out var pinned))
                {
                    Log($"pinned {format} source {pinned}");
                    id = pinned;
                }
                else if (format.Length > 0)
                {
                    var converted = await S.Convert(id, format);
                    if (!converted.Ok)
                    {
                        Fail(label, $"{modelId} {format} conversion: {converted.Error.Message}");
                        return true;
                    }
                    Log($"converted {id} to {format}: {converted.Value}");
                    id = converted.Value;
                }
                values[field.Name] = field.Type == FieldType.Files ? (JToken)new JArray(id) : id;
            }
            var problems = SchemaForm.Validate(fields, values, modelId);
            if (problems.Count > 0)
            {
                Fail(label, $"{modelId} form: {string.Join(" ", problems)}");
                return true;
            }
            var request = new GenerateRequest
            {
                OutputType = outputType, ModelId = modelId, ModelName = model.Name,
                Payload = SchemaForm.Payload(fields, values), Prompt = prompt,
            };
            var quote = await S.Estimate(request);
            if (S.EstimateAlarm)
            {
                Fail(label, $"{modelId}: a price check returned a job. Stopping.");
                return false;
            }
            if (!quote.Ok) Fail(label, $"{modelId} price: {quote.Error.Message}");
            else Log($"OK {label} {modelId} ({model.Name}): {quote.Value.Cu} CU");
            return true;
        }

        static async Task PriceWorkflow(Dictionary<string, string> sources)
        {
            var priced = 0;
            foreach (var privacy in new[] { "private", "public" })
            {
                var list = await S.Client.ListWorkflows(privacy, 10);
                if (!list.Ok)
                {
                    Fail($"workflows {privacy}", list.Error.Message);
                    continue;
                }
                var workflows = list.Value["workflows"] as JArray ?? new JArray();
                Log($"workflows {privacy}: {workflows.Count}");
                foreach (var item in workflows.Take(5))
                {
                    var id = (string)item["id"];
                    var detail = await S.Client.GetWorkflow(id);
                    if (!detail.Ok)
                    {
                        Fail($"workflow {id}", detail.Error.Message);
                        continue;
                    }
                    var definition = detail.Value["workflow"] as JObject ?? detail.Value;
                    var fields = SchemaForm.FieldsFromSchema(definition);
                    var values = SchemaForm.InitialValues(fields);
                    var promptName = SchemaForm.PromptName(fields);
                    if (promptName.Length > 0) values[promptName] = "A stylized wooden treasure chest, game prop";
                    var usable = true;
                    foreach (var field in fields.Where(f => f.IsFile && f.Required))
                    {
                        var kind = string.IsNullOrEmpty(field.Kind) ? "image" : field.Kind.ToLowerInvariant();
                        if (!sources.TryGetValue(kind, out var source)) usable = false;
                        else values[field.Name] = field.Type == FieldType.Files ? (JToken)new JArray(source) : source;
                    }
                    var problems = SchemaForm.Validate(fields, values);
                    if (!usable || problems.Count > 0)
                    {
                        Log($"SKIP workflow {id}: {(usable ? string.Join(" ", problems) : "needs an input the project lacks")}");
                        continue;
                    }
                    var quote = await S.Estimate(new GenerateRequest
                    {
                        WorkflowId = id, ModelName = Json.Str(definition, "name", id), OutputType = OutputTypes.All,
                        Payload = SchemaForm.Payload(fields, values), Prompt = "workflow",
                    });
                    if (S.EstimateAlarm)
                    {
                        Fail($"workflow {id}", "a price check returned a job. Stopping.");
                        return;
                    }
                    if (!quote.Ok) Fail($"workflow {id}", quote.Error.Message);
                    else
                    {
                        Log($"OK workflow {privacy} {id} ({Json.Str(definition, "name")}, {fields.Count} inputs): {quote.Value.Cu} CU");
                        priced++;
                    }
                }
            }
            if (priced == 0) Fail("workflow", "no workflow could be priced");
        }

        static string Short(string text) => text.Length > 300 ? text.Substring(0, 300) + "…" : text;

        static void Log(string line)
        {
            Lines.Add(line);
            Debug.Log("CONTRACT " + line);
        }

        static void Fail(string label, string message)
        {
            _failures++;
            Log($"FAIL {label}: {message}");
        }
    }
}
