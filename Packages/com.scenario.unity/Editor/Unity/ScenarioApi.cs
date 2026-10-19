using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scenario.Editor;
using Scenario.Editor.Core;

namespace Scenario
{
    /// <summary>A Scenario call refused or failed. NotSent is true when nothing reached Scenario.</summary>
    public sealed class ScenarioException : Exception
    {
        public readonly bool NotSent;
        public readonly bool Ambiguous;

        public ScenarioException(ScenarioError error) : base(error.Message)
        {
            NotSent = error.NotSent;
            Ambiguous = error.Ambiguous;
        }

        public ScenarioException(string message, bool notSent) : base(message)
        {
            NotSent = notSent;
        }
    }

    /// <summary>A finished (or still running) generation.</summary>
    public sealed class GenerationResult
    {
        public string LocalId;
        public string JobId;
        public string State;
        public double QuotedCu;
        public double? ChargedCu;
        public IReadOnlyList<string> AssetIds;
        public IReadOnlyList<string> Files;
        public IReadOnlyList<string> Placed;
        public string Error;
    }

    /// <summary>
    /// Scenario from editor scripts (build steps, tools, tests). Same service and
    /// money rules as the window: every call is priced first, maxCu is required for
    /// paid calls, and a paid call is never retried.
    /// </summary>
    public static class ScenarioApi
    {
        static ScenarioService S => ScenarioService.Instance;

        public static bool IsReady => S.Ready;

        /// <summary>Free: the price in Creative Units.</summary>
        public static async Task<double> Estimate(string modelId, JObject parameters)
        {
            var quote = await S.Estimate(modelId, parameters ?? new JObject());
            if (!quote.Ok) throw new ScenarioException(quote.Error);
            return quote.Value.Cu;
        }

        /// <summary>
        /// Paid: generates, waits and imports. Refused without sending when the price is above maxCu.
        /// </summary>
        public static async Task<GenerationResult> Generate(string modelId, JObject parameters, double maxCu, string outputType = null,
            bool place = false, int timeoutSeconds = 300)
        {
            var model = S.Model(modelId);
            var request = new GenerateRequest
            {
                ModelId = modelId,
                ModelName = model?.Name ?? modelId,
                Payload = parameters ?? new JObject(),
                OutputType = outputType ?? (model != null ? OutputTypes.TypeFor(model) : OutputTypes.All),
                Prompt = Json.Str(parameters, "prompt"),
                Place = place,
                Origin = "script",
            };
            var quote = await S.Estimate(request);
            if (!quote.Ok) throw new ScenarioException(quote.Error);
            if (quote.Value.Cu > maxCu) throw new ScenarioException($"This costs {quote.Value.Cu} CU, above maxCu {maxCu}. Nothing was sent.", true);
            var result = await S.Generate(request);
            if (!result.Ok) throw new ScenarioException(result.Error);
            var row = result.Value;
            var until = Json.Now() + timeoutSeconds;
            while (Json.Now() < until && !(row.IsFinal && (row.State != JobState.Success || row.Imported || row.Error.Length > 0)))
                await Task.Delay(1000);
            return new GenerationResult
            {
                LocalId = row.LocalId,
                JobId = row.JobId,
                State = row.State,
                QuotedCu = row.QuotedCu,
                ChargedCu = row.ChargedCu,
                AssetIds = row.AssetIds.ToList(),
                Files = row.Files.ToList(),
                Placed = row.Placed.ToList(),
                Error = row.Error,
            };
        }

        /// <summary>Free: uploads a project asset (or a scene object as GLB) and returns its Scenario asset id.</summary>
        public static async Task<string> Upload(UnityEngine.Object asset)
        {
            var result = await S.Upload(asset);
            if (!result.Ok) throw new ScenarioException(result.Error);
            return result.Value;
        }

        /// <summary>Free: imports Scenario assets into the project and returns the Unity paths.</summary>
        public static async Task<IReadOnlyList<string>> Import(IEnumerable<string> assetIds, string outputType = OutputTypes.Image)
        {
            var result = await S.ImportAssets(assetIds.ToList(), outputType, "", false, null);
            if (!result.Ok) throw new ScenarioException(result.Error);
            return result.Value.Select(f => f.Path).ToList();
        }
    }
}
