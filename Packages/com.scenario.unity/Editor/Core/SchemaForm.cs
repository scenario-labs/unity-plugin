using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    public enum FieldType
    {
        Prompt,
        Text,
        Enum,
        Number,
        Integer,
        Boolean,
        Multi,
        File,
        Files,
    }

    /// <summary>One model input, from model_schema_get's normalised "parameters" array.</summary>
    public sealed class Field
    {
        public string Name;
        public string Label;
        public string Description = "";
        public FieldType Type = FieldType.Text;
        public bool Required;
        public JToken Default;
        public double? Min;
        public double? Max;
        public double? Step;
        public List<JToken> Options = new List<JToken>();
        /// <summary>For files: image, video, audio or 3d.</summary>
        public string Kind = "";
        public int MaxItems;
        public int MaxLength;
        public bool CostImpact;
        public bool PromptSpark;

        public bool IsFile => Type == FieldType.File || Type == FieldType.Files;
        public bool IsEssential => Required || Type == FieldType.Prompt || IsFile || CostImpact || SchemaForm.EssentialNames.Contains(Name);
    }

    /// <summary>
    /// Turns a model schema into form fields, validates values, and builds the
    /// model_run payload. Ported from the Godot plugin's schema_form.gd.
    /// </summary>
    public static class SchemaForm
    {
        static readonly string[] IntegerHints = { "seed", "num", "count", "steps", "face", "polycount", "frames" };

        /// <summary>Settings shown above "More settings" even when they do not change the price.</summary>
        public static readonly HashSet<string> EssentialNames = new HashSet<string>
        {
            "aspectRatio", "aspect_ratio", "numOutputs", "num_outputs", "duration", "resolution", "quality", "style", "background",
            "audioFormat", "outputFormat", "topology", "loop",
            // 3D operations: the choice that defines the result.
            "actionId", "animation", "rigType", "targetPolycount", "faceLimit", "quad", "polygonType", "segmentationGranularity", "moveInPlace",
        };

        /// <summary>
        /// The 3D format a file input insists on when it does not take GLB, from its
        /// description ("Supported format: FBX"). The plugin converts the asset first.
        /// </summary>
        public static string RequiredModelFormat(Field field)
        {
            if (field == null || !string.Equals(field.Kind, "3d", StringComparison.OrdinalIgnoreCase)) return "";
            var text = field.Description ?? "";
            if (text.IndexOf("GLB", StringComparison.OrdinalIgnoreCase) >= 0) return "";
            var at = text.IndexOf("format", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";
            var tail = text.Substring(at);
            if (tail.IndexOf("FBX", StringComparison.OrdinalIgnoreCase) >= 0) return "fbx";
            if (tail.IndexOf("OBJ", StringComparison.OrdinalIgnoreCase) >= 0) return "obj";
            return "";
        }

        public static List<Field> FieldsFromSchema(JObject schema, IDictionary<string, JToken[]> restrict = null)
        {
            var fields = new List<Field>();
            // Models list "parameters"; workflow definitions list "inputs_definition".
            var parameters = schema?["parameters"] as JArray ?? schema?["inputs"] as JArray ?? schema?["inputs_definition"] as JArray;
            if (parameters == null) return fields;
            foreach (var token in parameters)
            {
                if (!(token is JObject p) || p["name"] == null) continue;
                var field = ToField(p);
                if (restrict != null && restrict.TryGetValue(field.Name, out var allowed) && (field.Type == FieldType.Enum || field.Type == FieldType.Multi))
                {
                    field.Options = field.Options.Where(o => allowed.Any(a => JToken.DeepEquals(a, o))).ToList();
                    if (field.Type == FieldType.Enum && field.Options.Count > 0 && !InOptions(field.Default, field.Options))
                        field.Default = field.Options[0];
                }
                fields.Add(field);
            }
            // The main prompt goes first, then required fields, then the rest in schema order.
            var mainPrompt = fields.FirstOrDefault(f => f.Type == FieldType.Prompt);
            var ordered = new List<Field>();
            if (mainPrompt != null) ordered.Add(mainPrompt);
            ordered.AddRange(fields.Where(f => f != mainPrompt && f.Required));
            ordered.AddRange(fields.Where(f => f != mainPrompt && !f.Required));
            return ordered;
        }

        static Field ToField(JObject p)
        {
            var name = (string)p["name"];
            var rawType = Json.Str(p, "type", "string");
            var options = p["allowed_values"] is JArray values ? values.ToList()
                : p["allowedValues"] is JArray raw ? raw.ToList() : new List<JToken>();
            var field = new Field
            {
                Name = name,
                Label = Json.Str(p, "label").Length > 0 ? Json.Str(p, "label") : Humanize(name),
                Description = Json.Str(p, "description"),
                Required = Json.Truthy(p["required"]),
                Default = p["default"]?.DeepClone(),
                Min = p["min"] != null ? Json.ToDouble(p["min"]) : (double?)null,
                Max = p["max"] != null ? Json.ToDouble(p["max"]) : (double?)null,
                Step = p["step"] != null ? Json.ToDouble(p["step"]) : (double?)null,
                Options = options,
                Kind = Json.Str(p, "kind"),
                MaxItems = rawType.EndsWith("array") ? (int)Json.Num(p, "max_length") : 0,
                MaxLength = rawType == "string" ? (int)Json.Num(p, "max_length") : 0,
                CostImpact = Json.Truthy(p["cost_impact"]) || Json.Truthy(p["costImpact"]),
                PromptSpark = Json.Truthy(p["prompt_spark"]) || Json.Truthy(p["promptSpark"]),
            };
            switch (rawType)
            {
                case "string":
                    if (options.Count > 0) field.Type = FieldType.Enum;
                    else if (Json.Truthy(p["prompt"]) || name == "prompt") field.Type = FieldType.Prompt;
                    break;
                case "number":
                case "integer":
                    if (options.Count > 0) field.Type = FieldType.Enum;
                    else if (rawType == "integer" || LooksInteger(name, field.Step)) field.Type = FieldType.Integer;
                    else field.Type = FieldType.Number;
                    break;
                case "boolean":
                    field.Type = FieldType.Boolean;
                    break;
                case "string_array":
                    field.Type = FieldType.Multi;
                    break;
                case "file":
                    field.Type = FieldType.File;
                    break;
                case "file_array":
                    field.Type = FieldType.Files;
                    break;
                default:
                    field.Type = FieldType.Text;
                    break;
            }
            return field;
        }

        static bool LooksInteger(string name, double? step)
        {
            if (step.HasValue) return Math.Floor(step.Value) == step.Value && step.Value >= 1;
            var lower = name.ToLowerInvariant();
            return IntegerHints.Any(lower.Contains);
        }

        /// <summary>camelCase or snake_case to "Sentence case".</summary>
        public static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var words = new List<string>();
            var current = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c == '_' || c == '-')
                {
                    if (current.Length > 0) words.Add(current.ToString());
                    current.Clear();
                }
                else if (char.IsUpper(c) && current.Length > 0 && char.IsLower(current[current.Length - 1]))
                {
                    words.Add(current.ToString());
                    current.Clear();
                    current.Append(c);
                }
                else
                {
                    current.Append(c);
                }
            }
            if (current.Length > 0) words.Add(current.ToString());
            var text = string.Join(" ", words).ToLowerInvariant();
            text = text.Replace("num outputs", "outputs").Replace(" 3d", " 3D").Replace("pbr", "PBR").Replace("uv", "UV").Replace("sfx", "SFX");
            return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>Name of the main prompt field ("prompt", "text", "texturePrompt"...), "" when the model takes no text.</summary>
        public static string PromptName(IEnumerable<Field> fields) => fields.FirstOrDefault(f => f.Type == FieldType.Prompt)?.Name ?? "";

        /// <summary>
        /// Whether Prompt Spark can rewrite this model's prompt. model_schema_get flags it on the prompt
        /// field and lists it in prompting.prompt_spark_fields; a top-level prompt_spark is also accepted.
        /// </summary>
        public static bool SparkAvailable(JObject schema, IList<Field> fields)
        {
            if (schema == null || fields == null) return false;
            var prompt = PromptName(fields);
            if (prompt.Length == 0) return false;
            if (Json.Truthy(schema["prompt_spark"]) || fields.Any(f => f.Name == prompt && f.PromptSpark)) return true;
            return (schema["prompting"] as JObject)?["prompt_spark_fields"] is JArray listed && listed.Any(t => (string)t == prompt);
        }

        /// <summary>The first file input that takes this kind of asset (image, video, audio, 3d).</summary>
        public static Field FileInputFor(IEnumerable<Field> fields, string kind)
        {
            var list = fields.Where(f => f.IsFile && string.Equals(f.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToList();
            return list.FirstOrDefault(f => f.Required) ?? list.FirstOrDefault();
        }

        /// <summary>Starting values: schema defaults, then presets on top.</summary>
        public static JObject InitialValues(IEnumerable<Field> fields, JObject presets = null)
        {
            var values = new JObject();
            foreach (var field in fields)
            {
                if (presets != null && presets[field.Name] != null) values[field.Name] = presets[field.Name].DeepClone();
                else if (field.Default != null && field.Default.Type != JTokenType.Null) values[field.Name] = field.Default.DeepClone();
            }
            return values;
        }

        /// <summary>Problems that block a price check, as short sentences. Empty means valid.</summary>
        public static List<string> Validate(IEnumerable<Field> fields, JObject values, string modelId = "")
        {
            var problems = new List<string>();
            foreach (var field in fields)
            {
                var value = values?[field.Name];
                if (field.Required && Json.IsEmpty(value))
                {
                    problems.Add(field.Type == FieldType.Prompt ? "Write a prompt." : field.IsFile ? $"Add {Article(field.Label)}." : $"{field.Label}: required.");
                    continue;
                }
                if (Json.IsEmpty(value)) continue;
                switch (field.Type)
                {
                    case FieldType.Number:
                    case FieldType.Integer:
                        if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                            problems.Add($"{field.Label} must be a number.");
                        else if (field.Min.HasValue && Json.ToDouble(value) < field.Min.Value)
                            problems.Add($"{field.Label} must be at least {Json.NumberText(field.Min.Value)}.");
                        else if (field.Max.HasValue && Json.ToDouble(value) > field.Max.Value)
                            problems.Add($"{field.Label} must be at most {Json.NumberText(field.Max.Value)}.");
                        break;
                    case FieldType.Enum:
                        if (field.Options.Count > 0 && !InOptions(value, field.Options))
                            problems.Add($"{field.Label} has an unsupported value.");
                        break;
                    case FieldType.Prompt:
                    case FieldType.Text:
                        if (field.MaxLength > 0 && value.ToString().Length > field.MaxLength)
                            problems.Add($"{field.Label} is longer than {field.MaxLength} characters.");
                        break;
                    case FieldType.Files:
                    case FieldType.Multi:
                        if (field.MaxItems > 0 && value is JArray array && array.Count > field.MaxItems)
                            problems.Add($"{field.Label} takes at most {field.MaxItems} items.");
                        break;
                }
            }
            // Inputs the schema marks optional but Scenario needs to price this model.
            foreach (var need in OutputTypes.Needs(modelId))
            {
                if (!fields.Any(f => need.Fields.Contains(f.Name))) continue;
                if (need.Unless.Length > 0 && values?[need.Unless] is JValue off && off.Type == JTokenType.Boolean && !(bool)off) continue;
                if (need.Fields.Any(n => !Json.IsEmpty(values?[n]))) continue;
                problems.Add(need.Message);
            }
            return problems;
        }

        static string Article(string label)
        {
            var lower = label.ToLowerInvariant();
            return ("aeiou".IndexOf(lower[0]) >= 0 ? "an " : "a ") + lower;
        }

        /// <summary>The model_run "parameters": known fields only, empty values dropped, typed.</summary>
        public static JObject Payload(IEnumerable<Field> fields, JObject values)
        {
            var output = new JObject();
            foreach (var field in fields)
            {
                var value = values?[field.Name];
                if (Json.IsEmpty(value)) continue;
                switch (field.Type)
                {
                    case FieldType.Integer:
                        output[field.Name] = (long)Math.Round(Json.ToDouble(value));
                        break;
                    case FieldType.Number:
                        output[field.Name] = Json.ToDouble(value);
                        break;
                    case FieldType.Boolean:
                        output[field.Name] = Json.Truthy(value);
                        break;
                    case FieldType.Files:
                    case FieldType.Multi:
                        output[field.Name] = value is JArray array ? array.DeepClone() : new JArray(value.DeepClone());
                        break;
                    case FieldType.Prompt:
                    case FieldType.Text:
                        output[field.Name] = value.ToString().Trim();
                        break;
                    default:
                        output[field.Name] = value.DeepClone();
                        break;
                }
            }
            return output;
        }

        public static bool InOptions(JToken value, IEnumerable<JToken> options)
        {
            if (value == null) return false;
            foreach (var option in options)
            {
                if (JToken.DeepEquals(option, value)) return true;
                var bothNumbers = (option.Type == JTokenType.Integer || option.Type == JTokenType.Float) && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float);
                if (bothNumbers && Math.Abs(Json.ToDouble(option) - Json.ToDouble(value)) < 1e-9) return true;
            }
            return false;
        }

        /// <summary>Display text of an option ("" shows as "Auto", -1 durations as "Auto").</summary>
        public static string OptionLabel(JToken option)
        {
            if (option == null || option.Type == JTokenType.Null) return "Auto";
            if (option.Type == JTokenType.String)
            {
                var s = (string)option;
                if (s.Length == 0) return "None";
                // "biped:walk" reads "Biped walk", "change_fps" reads "Change fps"; "16:9" stays.
                var colon = s.IndexOf(':');
                if (colon > 0 && colon < s.Length - 1 && char.IsLetter(s[colon - 1]) && char.IsLetter(s[colon + 1]))
                    s = s.Replace(':', ' ');
                else if (!s.Contains('_')) return s;
                s = s.Replace('_', ' ');
                return char.ToUpperInvariant(s[0]) + s.Substring(1);
            }
            if (option.Type == JTokenType.Integer || option.Type == JTokenType.Float)
            {
                var d = Json.ToDouble(option);
                return d < 0 ? "Auto" : Json.NumberText(d);
            }
            return option.ToString();
        }
    }
}
