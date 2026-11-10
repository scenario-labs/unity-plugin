using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scenario.Editor.Agent
{
    /// <summary>A tool refused the call; the message goes back to the agent as is.</summary>
    public sealed class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }

    /// <summary>
    /// The agent's tools: the same action layer as the window (ScenarioService),
    /// plus scene tools. Every scene edit is one named Undo step.
    /// </summary>
    public static class AgentTools
    {
        public sealed class Context
        {
            public string Client = "Agent";
        }

        sealed class Tool
        {
            public string Name;
            public string Description;
            public JObject Schema;
            public bool EditsScene;
            public bool ReadOnly;
            public Func<JObject, Context, Task<JObject>> Run;
        }

        public const string Instructions =
            "Scenario for Unity: generate images, video, 3D models, audio, PBR materials and skyboxes with Scenario models and put them in the open Unity project. " +
            "Flow: scenario_status, then models_find (add recommend_for_prompt to get Scenario's ranked picks), model_schema, estimate (free), generate (paid: it waits, imports and places the result, and returns Unity asset paths and scene object paths). " +
            "Then capture_view to look at the scene. Inputs that take files accept Scenario asset ids or Unity asset paths (Assets/...), uploaded for you. " +
            "Money rules: a paid call is never retried by the bridge. If generate reports an unknown outcome, call jobs instead of generating again. Calls above the user's limit wait for approval in Unity. " +
            "No call blocks longer than 45 s: slow work (3D, video, audio, a long playtest) answers \"still running\" and names the call that follows it (jobs or play status, with wait_seconds). " +
            "For a batch, call generate with wait false for each request, then jobs with wait_seconds. " +
            "Scene objects are addressed by hierarchy path (Root/Child). Each scene edit is one Undo step the user can revert. " +
            "Walkable worlds: terrain_create (from a generated heightmap image and generated materials), ground_height to measure it, then generated models sized and grounded with object_set (height, ground), scattered with scatter or lined up with place_along, " +
            "make_walkable on bridges, stairs and platforms, atmosphere for the light and mood, a rigged and animated character turned into a player with make_playable, then play (walk) to playtest it. " +
            "Look at the result with capture_view (around for four angles at once) after each stage.";

        /// <summary>
        /// Longest a tool call blocks. MCP clients commonly give up on a call after 60 s, so slower work
        /// answers "still running" with the call that follows it. Tests lower it.
        /// </summary>
        internal static int CallSeconds = 45;

        /// <summary>Seconds left to wait in a call that began at started, out of the asked wait.</summary>
        static int Budget(double started, int asked) => (int)Math.Max(0, Math.Min(asked, CallSeconds - (Json.Now() - started)));

        static ScenarioService S => ScenarioService.Instance;
        // Built on first use: Build() reads static helpers declared further down.
        static List<Tool> _tools;
        static List<Tool> Tools => _tools ?? (_tools = Build());

        public static JArray List()
        {
            return new JArray(Tools.Select(t =>
            {
                var tool = new JObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema };
                var annotations = new JObject();
                if (t.ReadOnly) annotations["readOnlyHint"] = true;
                if (t.EditsScene) annotations["destructiveHint"] = t.Name == "object_delete";
                if (annotations.HasValues) tool["annotations"] = annotations;
                return tool;
            }));
        }

        public static IEnumerable<string> Names => Tools.Select(t => t.Name);

        public static async Task<JObject> Call(string name, JObject arguments, Context context)
        {
            var tool = Tools.FirstOrDefault(t => t.Name == name);
            if (tool == null) return Error($"Unknown tool {name}. Call tools/list.");
            if (tool.EditsScene && !ScenarioUserSettings.instance.AgentCanEditScene)
                return Error("Scene edits are turned off in Scenario > Agent. Ask the user to turn them on.");
            var group = -1;
            if (tool.EditsScene)
            {
                Undo.IncrementCurrentGroup();
                group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Scenario agent: " + name);
            }
            try
            {
                var result = await tool.Run(arguments ?? new JObject(), context);
                if (tool.EditsScene && EditorApplication.isPlaying && result["isError"] == null)
                    ((JArray)result["content"]).Add(new JObject { ["type"] = "text", ["text"] = "Unity is in Play mode: this edit is lost when Play mode stops (play with action stop, then edit)." });
                return result;
            }
            catch (ToolError e)
            {
                return Error(e.Message);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                return Error(e.Message);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return Error($"Unity error in {name}: {e.Message}");
            }
            finally
            {
                if (group >= 0) Undo.CollapseUndoOperations(group);
            }
        }

        // --- Results --------------------------------------------------------------------

        static JObject Ok(JToken data) => new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = data.ToString(Formatting.Indented) }),
        };

        static JObject Error(string message) => new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = message }),
            ["isError"] = true,
        };

        static JObject Image(byte[] png, JToken data) => new JObject
        {
            ["content"] = new JArray(
                new JObject { ["type"] = "image", ["data"] = System.Convert.ToBase64String(png), ["mimeType"] = "image/png" },
                new JObject { ["type"] = "text", ["text"] = data.ToString(Formatting.Indented) }),
        };

        // --- Schema helpers -----------------------------------------------------------------

        static JObject Schema(string[] required, params (string name, JObject schema)[] properties)
        {
            var props = new JObject();
            foreach (var (name, schema) in properties) props[name] = schema;
            var result = new JObject { ["type"] = "object", ["properties"] = props };
            if (required != null && required.Length > 0) result["required"] = new JArray(required);
            return result;
        }

        static readonly string[] None = new string[0];
        static JObject Str(string description, params string[] options)
        {
            var schema = new JObject { ["type"] = "string", ["description"] = description };
            if (options.Length > 0) schema["enum"] = new JArray(options);
            return schema;
        }
        static JObject Num(string description) => new JObject { ["type"] = "number", ["description"] = description };
        static JObject Int(string description) => new JObject { ["type"] = "integer", ["description"] = description };
        static JObject Bool(string description) => new JObject { ["type"] = "boolean", ["description"] = description };
        static JObject Obj(string description) => new JObject { ["type"] = "object", ["description"] = description };
        static JObject Arr(string itemType, string description) => new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = itemType }, ["description"] = description };
        static JObject Objects(string description) => new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" }, ["description"] = description };
        static JObject Pair(string description) => new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2, ["description"] = description };
        static JObject Vec(string description) => new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3, ["description"] = description };

        static string S_(JObject args, string key, string fallback = "") => Json.Str(args, key, fallback);
        static bool B_(JObject args, string key, bool fallback) => args[key] == null ? fallback : Json.Truthy(args[key]);
        static int I_(JObject args, string key, int fallback) => args[key] == null ? fallback : (int)Math.Round(Json.ToDouble(args[key], fallback));

        static string Need(JObject args, string key)
        {
            var value = Json.Str(args, key);
            if (value.Length == 0) throw new ToolError($"{key} is required.");
            return value;
        }

        static readonly string[] Types = OutputTypes.List.Select(t => t.Id).ToArray();

        // --- Registry ---------------------------------------------------------------------------

        static List<Tool> Build()
        {
            return new List<Tool>
            {
                new Tool
                {
                    Name = "scenario_status", ReadOnly = true,
                    Description = "Who is signed in, which Scenario project receives generations, spending state and limits, the Unity render pipeline and scene. Call this first.",
                    Schema = Schema(None), Run = Status,
                },
                new Tool
                {
                    Name = "models_find", ReadOnly = true,
                    Description = "Find Scenario models. Search the catalog by text, output type and capability, or pass recommend_for_prompt to get Scenario's ranked recommendation with cost and speed notes.",
                    Schema = Schema(None,
                        ("query", Str("Words to match: name, provider, style.")),
                        ("output_type", Str("image, video, model3d, motion, audio, material, skybox or all.", Types)),
                        ("capability", Str("txt2img, img2img, txt2video, img2video, video2video, txt23d, img23d, 3d23d, txt2audio, audio2audio, video2audio.")),
                        ("recommend_for_prompt", Str("A prompt: returns Scenario's ranked picks for it (free).")),
                        ("include_legacy", Bool("Include deprecated models.")),
                        ("sort", Str("relevance (default) or newest: the most recently added models first.", "relevance", "newest")),
                        ("limit", Int("Max results, default 15."))),
                    Run = ModelsFind,
                },
                new Tool
                {
                    Name = "model_schema", ReadOnly = true,
                    Description = "The inputs a model takes: names, types, options, ranges, defaults, which inputs are files (and their kind), and the presets the plugin applies.",
                    Schema = Schema(new[] { "model_id" }, ("model_id", Str("Model id, e.g. model_openai-gpt-image-2-5-sunburst."))),
                    Run = ModelSchema,
                },
                new Tool
                {
                    Name = "estimate", ReadOnly = true,
                    Description = "Free price check in Creative Units for a model or workflow run with these parameters. Nothing is generated. The quote is valid 10 minutes.",
                    Schema = Schema(None,
                        ("model_id", Str("Model id.")),
                        ("workflow_id", Str("Workflow id, instead of model_id.")),
                        ("parameters", Obj("Model parameters (or workflow inputs). File inputs take Scenario asset ids or Unity asset paths."))),
                    Run = Estimate,
                },
                new Tool
                {
                    Name = "generate",
                    Description = "Paid. Generates with a Scenario model, waits up to 45 s, imports the result into Assets and places it in the scene (on target when given). Returns Unity asset paths and scene object paths. A slower job answers \"still running\" with its local_id: it imports and places itself when done, and jobs with wait_seconds follows it. Above the user's limit it waits for approval in Unity. Never call it again for the same request after an unknown outcome: call jobs.",
                    Schema = Schema(new[] { "model_id" },
                        ("model_id", Str("Model id.")),
                        ("parameters", Obj("Model parameters from model_schema. File inputs take Scenario asset ids or Unity asset paths.")),
                        ("output_type", Str("How the result lands in Unity; defaults to the model's type.", Types)),
                        ("place", Bool("Place the result in the scene (default true).")),
                        ("target", Str("Hierarchy path of an object to apply the result to (sprite, material, audio source, video player).")),
                        ("folder", Str("Assets folder for the files (default Assets/Scenario/<Type>/<date>).")),
                        ("max_cu", Num("Refuse without sending if the price is above this.")),
                        ("wait", Bool("Wait for the result, up to 45 s (default true). False returns at once: use it to send a batch, then follow with jobs.")),
                        ("timeout_seconds", Int("How long to wait, at most 45 (the default)."))),
                    Run = (a, c) => Generate(a, c, workflow: false),
                },
                new Tool
                {
                    Name = "workflow_run",
                    Description = "Paid. Runs a Scenario workflow (see workflows_find) with its inputs, waits up to 45 s and imports the outputs; a slower run is followed with jobs. Same approval and money rules as generate.",
                    Schema = Schema(new[] { "workflow_id" },
                        ("workflow_id", Str("Workflow id.")),
                        ("inputs", Obj("Workflow inputs by name. File inputs take Scenario asset ids or Unity asset paths.")),
                        ("max_cu", Num("Refuse without sending if the price is above this.")),
                        ("wait", Bool("Wait for the outputs, up to 45 s (default true).")),
                        ("timeout_seconds", Int("How long to wait, at most 45 (the default)."))),
                    Run = (a, c) => Generate(a, c, workflow: true),
                },
                new Tool
                {
                    Name = "workflows_find", ReadOnly = true,
                    Description = "Lists Scenario workflows: the team's own, or featured public ones, with their inputs.",
                    Schema = Schema(None, ("query", Str("Words to match.")), ("scope", Str("team or featured (default featured).", "team", "featured"))),
                    Run = WorkflowsFind,
                },
                new Tool
                {
                    Name = "motions_find", ReadOnly = true,
                    Description = "Searches Meshy's motion library (walks, runs, attacks, dances, idles...). Pass a motion's id as actionId to model_meshy-animation. Free, works offline.",
                    Schema = Schema(None,
                        ("query", Str("Words to match in the motion's name or group, e.g. sword attack.")),
                        ("category", Str("Walk and run, Daily actions, Fighting, Body movements or Dancing; empty searches all.")),
                        ("limit", Int("Max results, default 20."))),
                    Run = MotionsFind,
                },
                new Tool
                {
                    Name = "jobs", ReadOnly = true,
                    Description = "Recent requests from this Unity project with state, cost, files and placed objects. Give local_id or job_id to get one, and wait_seconds to wait for it to finish (call again while it says still running).",
                    Schema = Schema(None, ("local_id", Str("Ledger id from generate.")), ("job_id", Str("Scenario job id.")),
                        ("wait_seconds", Int("Wait up to this long, at most 45, for the job to finish.")), ("limit", Int("Rows to list, default 10."))),
                    Run = Jobs,
                },
                new Tool
                {
                    Name = "import_asset",
                    Description = "Imports existing Scenario assets (by id) into the Unity project, optionally placing them. Free.",
                    Schema = Schema(new[] { "asset_ids" }, ("asset_ids", Arr("string", "Scenario asset ids.")),
                        ("output_type", Str("How it lands; defaults to the asset's kind.", Types)), ("place", Bool("Place in the scene (default false).")),
                        ("target", Str("Hierarchy path to apply it to."))),
                    Run = ImportAsset,
                },
                new Tool
                {
                    Name = "upload_reference",
                    Description = "Uploads a reference to Scenario and returns its asset id (free): a Unity asset path, a scene object (exported as GLB), a capture of the Scene view or main camera, or a file path.",
                    Schema = Schema(None, ("asset_path", Str("Unity asset path, e.g. Assets/Art/hero.png.")), ("object_path", Str("Scene object to export as GLB.")),
                        ("capture", Str("scene_view or main_camera.", "scene_view", "main_camera")), ("file_path", Str("Absolute path of a file."))),
                    Run = Upload,
                },
                new Tool
                {
                    Name = "prompt_spark",
                    Description = "Paid (a few CU). Rewrites a prompt for a given model with Scenario's Prompt Spark. Returns the improved prompt; nothing is generated.",
                    Schema = Schema(new[] { "model_id", "prompt" }, ("model_id", Str("Model the prompt is for.")), ("prompt", Str("The prompt to improve."))),
                    Run = PromptSpark,
                },
                new Tool
                {
                    Name = "scene_info", ReadOnly = true,
                    Description = "Open scenes, the hierarchy (paths and components), the selection, render pipeline and 2D or 3D mode.",
                    Schema = Schema(None, ("depth", Int("Hierarchy depth, default 3.")), ("max_objects", Int("Default 150."))),
                    Run = SceneInfo,
                },
                new Tool
                {
                    Name = "object_get", ReadOnly = true,
                    Description = "One scene object: transform, components, and with properties=true their editable serialized properties.",
                    Schema = Schema(new[] { "path" }, ("path", Str("Hierarchy path.")), ("properties", Bool("Include component properties."))),
                    Run = ObjectGet,
                },
                new Tool
                {
                    Name = "object_create", EditsScene = true,
                    Description = "Creates an empty object, a primitive or a prefab instance in the active scene.",
                    Schema = Schema(null, ("name", Str("Object name.")),
                        ("primitive", Str("cube, sphere, capsule, cylinder, plane or quad. Omit for an empty object.", "cube", "sphere", "capsule", "cylinder", "plane", "quad")),
                        ("prefab", Str("Asset path of a prefab or model to instantiate.")), ("parent", Str("Parent hierarchy path.")),
                        ("position", Vec("World position [x, y, z].")), ("rotation", Vec("Euler angles in degrees.")), ("scale", Vec("Local scale.")),
                        ("height", Num("Scale uniformly to this height in meters (after scale).")), ("ground", Bool("Sit it on the ground below (terrain or any collider)."))),
                    Run = ObjectCreate,
                },
                new Tool
                {
                    Name = "object_set", EditsScene = true,
                    Description = "Changes an object's name, active state, parent, transform, tag or layer, and can give it collision.",
                    Schema = Schema(new[] { "path" }, ("path", Str("Hierarchy path.")), ("name", Str("New name.")), ("active", Bool("Active state.")),
                        ("parent", Str("New parent path, or empty for the scene root.")), ("position", Vec("Position.")), ("rotation", Vec("Euler angles.")),
                        ("scale", Vec("Local scale.")), ("local", Bool("Position and rotation in parent space (default false).")), ("tag", Str("Tag.")), ("layer", Str("Layer name.")),
                        ("height", Num("Scale uniformly to this height in meters (after scale).")), ("ground", Bool("Sit it on the ground below, after the other changes.")),
                        ("collider", Str("Collision so a player can walk on and into it: mesh (follows the shape) or box. Added only when no part has a collider yet.", "mesh", "box"))),
                    Run = ObjectSet,
                },
                new Tool
                {
                    Name = "component_add", EditsScene = true,
                    Description = "Adds a component by type name (Light, Rigidbody, AudioSource, BoxCollider, or any script).",
                    Schema = Schema(new[] { "path", "type" }, ("path", Str("Hierarchy path.")), ("type", Str("Component type name."))),
                    Run = ComponentAdd,
                },
                new Tool
                {
                    Name = "component_set", EditsScene = true,
                    Description = "Sets a component property: a serialized property path (m_Intensity, m_Color) or a public C# property (intensity, color). Values: numbers, booleans, strings, enums by name, [x,y,z] vectors, colors as [r,g,b,a] or #RRGGBB, asset paths or hierarchy paths for references.",
                    Schema = Schema(new[] { "path", "type", "property", "value" }, ("path", Str("Hierarchy path.")), ("type", Str("Component type name.")),
                        ("property", Str("Property name or serialized path.")), ("value", new JObject { ["description"] = "New value." })),
                    Run = ComponentSet,
                },
                new Tool
                {
                    Name = "object_delete", EditsScene = true,
                    Description = "Deletes a scene object (undoable).",
                    Schema = Schema(new[] { "path" }, ("path", Str("Hierarchy path."))),
                    Run = ObjectDelete,
                },
                new Tool
                {
                    Name = "select",
                    Description = "Selects objects or assets in the editor and frames them in the Scene view.",
                    Schema = Schema(new[] { "paths" }, ("paths", Arr("string", "Hierarchy paths or asset paths."))),
                    Run = Select,
                },
                new Tool
                {
                    Name = "material_assign", EditsScene = true,
                    Description = "Sets a material asset on an object's renderer.",
                    Schema = Schema(new[] { "path", "material" }, ("path", Str("Hierarchy path.")), ("material", Str("Material asset path.")),
                        ("index", Int("Material slot, default 0.")), ("children", Bool("Also on child renderers."))),
                    Run = MaterialAssign,
                },
                new Tool
                {
                    Name = "skybox_set", EditsScene = true,
                    Description = "Sets the scene skybox from a panorama texture or a skybox material asset.",
                    Schema = Schema(None, ("texture", Str("Panorama texture asset path.")), ("material", Str("Skybox material asset path."))),
                    Run = SkyboxSet,
                },
                new Tool
                {
                    Name = "assets_find", ReadOnly = true,
                    Description = "Finds project assets by name, type (Texture2D, Material, Prefab, AudioClip, VideoClip, Model, Scene), folder or label (Scenario marks every generated asset).",
                    Schema = Schema(None, ("query", Str("Name words.")), ("type", Str("Asset type.")), ("folder", Str("Folder, e.g. Assets/Scenario.")),
                        ("label", Str("Asset label, e.g. Scenario.")), ("limit", Int("Default 50."))),
                    Run = AssetsFind,
                },
                new Tool
                {
                    Name = "scene_save", EditsScene = true,
                    Description = "Saves the open scenes. An untitled scene needs a path (Assets/Scenes/Name.unity).",
                    Schema = Schema(None, ("path", Str("Path for an untitled active scene."))),
                    Run = SceneSave,
                },
                new Tool
                {
                    Name = "console_logs", ReadOnly = true,
                    Description = "Recent Unity console messages since the bridge started.",
                    Schema = Schema(None, ("limit", Int("Default 30.")), ("level", Str("all, warning or error.", "all", "warning", "error"))),
                    Run = ConsoleLogs,
                },
                new Tool
                {
                    Name = "capture_view", ReadOnly = true,
                    Description = "Renders the Scene view, the main camera or a given camera and returns the image, so you can see the result. " +
                                  "around gives four three-quarter views of an object or point in one 2 x 2 image (south-west, south-east, north-west, north-east); from and at give one view from any point. Optionally uploads it to Scenario as a reference.",
                    Schema = Schema(None, ("source", Str("scene_view, main_camera, or a camera's hierarchy path (default main_camera, then scene_view, then an overview of the open scenes).")),
                        ("around", new JObject { ["description"] = "Object path or [x, y, z]: four views around it in one image." }),
                        ("from", Vec("Eye position [x, y, z] for a single view; a point under the terrain is raised to 1.7 m above it.")), ("at", new JObject { ["description"] = "What the view from looks at: object path or [x, y, z]." }),
                        ("distance", Num("around: camera distance in meters, default to frame it.")), ("elevation", Num("around: degrees above the horizon, default 25.")),
                        ("max_size", Int("Longest side in pixels, default 768 (1024 for around).")), ("upload", Bool("Also upload to Scenario and return the asset id."))),
                    Run = CaptureView,
                },
                new Tool
                {
                    Name = "terrain_create", EditsScene = true,
                    Description = "Creates walkable ground: a Unity Terrain with a collider, shaped by a heightmap image (generate one first: a top-down grayscale heightmap, white is high) or by gentle noise, " +
                                  "with flat areas for buildings and ground materials (generated PBR materials or textures) painted by height and slope. The first layer covers everything else.",
                    Schema = Schema(None, ("name", Str("Object name, default Terrain.")), ("heightmap", Str("Image asset path (Assets/...). Omit for rolling noise.")),
                        ("size", Vec("[width, max height, length] in meters, default [200, 30, 200].")), ("center", Vec("World position of the terrain's center at its lowest level, default [0, 0, 0].")),
                        ("resolution", Int("Heightmap resolution: 129, 257, 513 (default) or 1025.")), ("smoothing", Int("Blur passes against stair steps from 8-bit images, default 2.")),
                        ("edge_falloff", Num("0 to 0.5: share of the width over which the edges drop to the bottom (an island or plateau). Default 0.")),
                        ("seed", Int("Noise seed when there is no heightmap.")),
                        ("flat_areas", Objects("Areas leveled for buildings or paths: {center: [x, z], radius, height (world y, optional: default the ground there)}.")),
                        ("layers", Objects("Ground materials: {material or texture (asset path), normal (optional), tile (meters, default 8), min_height, max_height (0 to 1 of the terrain's height), min_slope, max_slope (degrees)}. The first is the base."))),
                    Run = TerrainCreate,
                },
                new Tool
                {
                    Name = "scatter", EditsScene = true,
                    Description = "Places many copies of a model on the ground in one call: random positions in an area, random turn, a size range in meters, slopes and keep-clear zones avoided, colliders added. " +
                                  "For trees, rocks, grass clumps, lanterns. Copies go under one group object.",
                    Schema = Schema(new[] { "source" }, ("source", Str("Prefab or model asset path, or a scene object to copy.")), ("count", Int("How many, default 10.")),
                        ("center", Pair("[x, z] center of the area, default [0, 0].")), ("area", Pair("[width, length] in meters, default [50, 50].")),
                        ("on", Str("Ground object path (a terrain): only its surface counts. Default: any collider.")),
                        ("height", Pair("[min, max] height of each copy in meters. Omit to keep the source's size.")), ("scale", Pair("[min, max] scale factor when height is not given.")),
                        ("random_yaw", Bool("Random turn, default true.")), ("tilt", Num("Most degrees a copy leans with the ground, default 0.")),
                        ("max_slope", Num("Skip ground steeper than this, degrees, default 35.")),
                        ("min_slope", Num("Skip ground flatter than this, degrees, default 0. With tilt, dresses cliff faces with rocks.")),
                        ("min_distance", Num("Meters between copies, default from their size.")),
                        ("keep_clear", Objects("Zones to leave empty: {center: [x, z], radius}.")), ("collider", Str("mesh (default), box or none.", "mesh", "box", "none")),
                        ("seed", Int("Same seed, same layout.")), ("name", Str("Group object name."))),
                    Run = Scatter,
                },
                new Tool
                {
                    Name = "ground_height", ReadOnly = true,
                    Description = "Measures the ground and changes nothing: height, slope and surface at points [x, z], or a profile along a line (from, to) with its steepest slope and biggest step. " +
                                  "Use it before placing things: a ravine's depth and both rims for a bridge, heights for flat_areas, whether a path is walkable (slopes under 50 degrees, steps under the player's step height).",
                    Schema = Schema(None, ("points", new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" } }, ["description"] = "Points [x, z], up to 200." }),
                        ("from", Pair("Line start [x, z].")), ("to", Pair("Line end [x, z].")), ("step", Num("Meters between line samples, default 1.")),
                        ("on", Str("Only this object's surface (a terrain). Default: the highest collider."))),
                    Run = GroundHeight,
                },
                new Tool
                {
                    Name = "make_walkable", EditsScene = true,
                    Description = "Makes a generated bridge, ramp, stairs or platform walkable in one call: invisible colliders follow its deck with holes bridged and bumps smoothed, side rails keep the player on it, " +
                                  "short ramps join each end to the ground beyond, and its own mesh colliders are turned off. Returns where the deck starts and ends and how each end meets the ground. Run it again after moving the object.",
                    Schema = Schema(new[] { "target" }, ("target", Str("Hierarchy path of the bridge or platform.")),
                        ("from", Pair("Walking line start [x, z]. Default: one end of the object's long side.")), ("to", Pair("Walking line end [x, z].")),
                        ("width", Num("Walking width in meters. Default: measured on the deck.")), ("rails", Bool("Invisible side rails, default true.")),
                        ("rail_height", Num("Meters, default 1.1.")), ("ramps", Bool("Ramps to the ground within 1.5 m of each end, default true.")),
                        ("keep_colliders", Bool("Keep the object's own colliders on, default false."))),
                    Run = MakeWalkable,
                },
                new Tool
                {
                    Name = "place_along", EditsScene = true,
                    Description = "Places copies of a model at even spacing along a path, beside it or on it: lanterns or fence posts lining a trail, torches on a bridge, stepping stones. " +
                                  "Copies sit on the ground (or a deck), face the path and get colliders; light adds a point light to each. Copies go under one group object.",
                    Schema = Schema(new[] { "source", "path" }, ("source", Str("Prefab or model asset path, or a scene object to copy.")),
                        ("path", new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" } }, ["description"] = "Path points [x, z] in walking order, at least two." }),
                        ("spacing", Num("Meters between copies along the path, default 6.")), ("offset", Num("Meters to the side of the path, default 2; 0 puts copies on it.")),
                        ("side", Str("right, left, both or alternate (default), seen walking along the path.", "right", "left", "both", "alternate")),
                        ("start", Num("Meters along the path before the first copy, default 0.")), ("height", Num("Height of each copy in meters. Omit to keep the source's size.")),
                        ("face", Str("path (default: front turned to the path), along (front in the walking direction), random or keep.", "path", "along", "random", "keep")),
                        ("on", Str("Ground object path: only its surface counts. Default: any collider.")), ("collider", Str("mesh (default), box or none.", "mesh", "box", "none")),
                        ("max_slope", Num("Skip ground steeper than this, degrees, default 40.")), ("jitter", Num("Most meters each copy moves off its spot at random, default 0.")),
                        ("light", Obj("A point light on each copy: {color (#RRGGBB or [r, g, b]), intensity (default 2), range (meters, default 8), at (share of the copy's height, default 0.75)}. {} for a warm default.")),
                        ("seed", Int("Same seed, same layout.")), ("name", Str("Group object name."))),
                    Run = PlaceAlong,
                },
                new Tool
                {
                    Name = "atmosphere", EditsScene = true,
                    Description = "Sets the light and mood in one call. A preset (golden_hour, midday, overcast, misty_morning, sunset, night) sets the sun's angle, color and strength, fog and ambient light, " +
                                  "and in URP a post-processing volume (tone mapping, exposure, contrast, saturation, warmth, bloom, vignette) that the scene's cameras render. Any value can be set alone or on top of a preset. Check it with capture_view.",
                    Schema = Schema(None, ("preset", Str("Starting look.", Atmosphere.Presets.Keys.ToArray())),
                        ("sun_elevation", Num("Degrees above the horizon.")), ("sun_azimuth", Num("Compass degrees the sunlight travels toward (0 is north, +z).")),
                        ("sun_color", new JObject { ["description"] = "#RRGGBB or [r, g, b]." }), ("sun_intensity", Num("Sun strength, about 0.2 (moonlight) to 2 (hard sun).")),
                        ("fog", Bool("Fog on or off.")), ("fog_density", Num("0.001 (clear air) to 0.03 (thick mist).")), ("fog_color", new JObject { ["description"] = "#RRGGBB or [r, g, b]." }),
                        ("ambient_color", new JObject { ["description"] = "Flat ambient light, #RRGGBB or [r, g, b]. Default: from the skybox." }),
                        ("exposure", Num("Post exposure in stops, -2 to 2.")), ("contrast", Num("-100 to 100.")), ("saturation", Num("-100 to 100.")),
                        ("temperature", Num("-100 (cold) to 100 (warm).")), ("bloom", Num("Glow around bright lights, 0 to 2.")), ("vignette", Num("Darker corners, 0 to 1.")),
                        ("post", Bool("Post-processing volume in URP, default true."))),
                    Run = AtmosphereSet,
                },
                new Tool
                {
                    Name = "make_playable", EditsScene = true,
                    Description = "Turns a rigged, animated character in the scene into a third-person player: WASD or gamepad to walk, Shift to run, Space to jump, a follow camera. " +
                                  "Builds an Animator Controller from its motions (idle, walk, run, jump), sizes the character, and matches speeds to the clips so feet do not slide. " +
                                  "Motions: Unity asset paths of animation results (the Animate step or model_meshy-animation with a motions_find id), or state names in its controller; found by name when omitted.",
                    Schema = Schema(new[] { "character" }, ("character", Str("Hierarchy path of the rigged character.")),
                        ("idle", Str("Idle motion.")), ("walk", Str("Walk motion (required unless it can be found by name).")), ("run", Str("Run motion.")), ("jump", Str("Jump motion.")),
                        ("height", Num("Character height in meters. Default: kept when 1.2 to 2.6 m, else 1.8 m.")),
                        ("walk_speed", Num("Meters per second; default from the walk clip.")), ("run_speed", Num("Meters per second; default from the run clip.")),
                        ("turn", Num("Degrees to turn the model if it faces away from its direction of travel (180 for a model facing -Z).")),
                        ("run_by_default", Bool("Run unless Shift is held.")), ("camera", Bool("Set up the follow camera on the main camera, default true.")),
                        ("footstep", Str("Audio asset path played at each step: ideally one short footstep; a longer recording is sliced."))),
                    Run = MakePlayable,
                },
                new Tool
                {
                    Name = "play",
                    Description = "Play mode: start, stop, status, walk (playtest: the player walks or runs through waypoints and the result says which were reached, falls, jumps, time) and record (PNG frames of the main camera at a fixed frame rate). " +
                                  "Walk and record start Play mode when needed and keep running in Unity: a call answers within 45 s, and if the walk or recording is not over, status with wait_seconds returns its result. " +
                                  "Edits made in Play mode are lost when it stops.",
                    Schema = Schema(new[] { "action" }, ("action", Str("start, stop, status, walk or record.", "start", "stop", "status", "walk", "record")),
                        ("wait_seconds", Int("Status: wait up to this long, at most 45, for a running walk or recording to end.")),
                        ("player", Str("Player path, default the first ScenarioPlayer.")),
                        ("waypoints", Objects("For walk: {position: [x, y, z] or at: object path, run (bool), jump (bool: jump on reaching it), wait (seconds)}.")),
                        ("timeout", Num("Walk: seconds of game time before giving up, default 90.")),
                        ("record", Bool("Walk: also record frames.")), ("seconds", Num("Record: length in seconds of game time.")),
                        ("folder", Str("Frames folder, default Recordings/<time> in the project.")), ("width", Int("Default 1920.")), ("height", Int("Default 1080.")),
                        ("fps", Int("Frames per second of game time, default 30.")), ("camera", Str("Camera path, default the main camera."))),
                    Run = Play,
                },
            };
        }

        // --- Scenario tools ---------------------------------------------------------------------

        static void RequireReady()
        {
            if (!S.IsSignedIn) throw new ToolError("Not signed in to Scenario. Ask the user to sign in: Window > Scenario.");
            if (!S.Ready) throw new ToolError("No Scenario project picked. Ask the user to pick one in Window > Scenario.");
        }

        static Task<JObject> Status(JObject args, Context context)
        {
            var user = ScenarioUserSettings.instance;
            var scene = SceneManager.GetActiveScene();
            var data = new JObject
            {
                ["signed_in"] = S.IsSignedIn,
                ["ready"] = S.Ready,
                ["account"] = S.AccountLabel,
                ["team"] = user.TeamName,
                ["project"] = user.ProjectName,
                ["plan"] = user.Plan,
                ["spending"] = S.Client.SpendingDisabled ? "off" : "on",
                ["spent_today_cu"] = Math.Round(S.SpentToday(), 2),
                ["agent_limits"] = new JObject
                {
                    ["auto_approve_cu_per_call"] = user.AgentAutoApproveCu,
                    ["session_budget_cu"] = user.AgentSessionBudgetCu,
                    ["session_approved_cu"] = Math.Round(AgentServer.SessionSpent, 2),
                    ["scene_edits"] = user.AgentCanEditScene,
                },
                ["unity"] = new JObject
                {
                    ["version"] = Application.unityVersion,
                    ["render_pipeline"] = MaterialBuilder.Current.ToString(),
                    ["mode"] = EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D ? "2D" : "3D",
                    ["active_scene"] = scene.path.Length > 0 ? scene.path : scene.name,
                },
                ["catalog_models"] = S.Catalog.Models.Count,
                ["output_root"] = ScenarioProjectSettings.instance.OutputRoot,
                ["output_types"] = new JArray(OutputTypes.List.Select(t => t.Id)),
            };
            if (!S.IsSignedIn) data["next"] = "Ask the user to sign in: Window > Scenario.";
            else if (!S.Ready) data["next"] = "Ask the user to pick a Scenario project in Window > Scenario.";
            return Task.FromResult(Ok(data));
        }

        static string CapabilityFor(string type, bool hasImage = false)
        {
            switch (type)
            {
                case OutputTypes.Video: return hasImage ? "img2video" : "txt2video";
                case OutputTypes.Model3D: return hasImage ? "img23d" : "txt23d";
                case OutputTypes.Audio: return "txt2audio";
                default: return hasImage ? "img2img" : "txt2img";
            }
        }

        static async Task<JObject> ModelsFind(JObject args, Context context)
        {
            RequireReady();
            var limit = Math.Max(1, Math.Min(50, I_(args, "limit", 15)));
            var type = S_(args, "output_type");
            var capability = S_(args, "capability");
            var prompt = S_(args, "recommend_for_prompt");
            if (prompt.Length > 0)
            {
                var cap = capability.Length > 0 ? capability : CapabilityFor(type);
                var result = await S.Recommend(cap, prompt, Math.Min(limit, 5));
                if (!result.Ok) throw new ToolError(result.Error.Message);
                return Ok(new JObject { ["capability"] = cap, ["rationale"] = result.Value["rationale"], ["ranked"] = result.Value["ranked"] });
            }
            if (S.Catalog.IsEmpty) await S.RefreshCatalog(false);
            IEnumerable<ModelInfo> models = S.Catalog.Search(new Catalog.Query
            {
                Text = S_(args, "query"),
                OutputType = type,
                Capability = capability,
                ShowLegacy = B_(args, "include_legacy", false),
                Favorites = ScenarioUserSettings.instance.Favorites,
            });
            if (S_(args, "sort") == "newest") models = models.OrderByDescending(m => m.CreatedAt);
            models = models.Take(limit);
            var list = new JArray(models.Select(m => new JObject
            {
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["provider"] = m.Provider,
                ["output_type"] = OutputTypes.TypeFor(m),
                ["capabilities"] = new JArray(m.Capabilities),
                ["description"] = m.Description,
                ["featured"] = m.IsFeatured,
                ["team_model"] = m.IsPrivate,
                ["legacy"] = m.IsLegacy,
                ["added"] = m.CreatedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)m.CreatedAt).ToString("yyyy-MM-dd") : null,
            }));
            var data = new JObject { ["models"] = list };
            if (type.Length > 0) data["quick_picks"] = new JArray(OutputTypes.Get(type).QuickPicks);
            if (list.Count == 0) data["hint"] = S.Catalog.IsEmpty ? "The catalog is empty: " + S.CatalogError : "Nothing matched. Try fewer words, or recommend_for_prompt.";
            return Ok(data);
        }

        static async Task<List<Field>> FieldsFor(string modelId)
        {
            var schema = await S.Schema(modelId);
            if (!schema.Ok) throw new ToolError(schema.Error.Message);
            return SchemaForm.FieldsFromSchema(schema.Value, OutputTypes.Restrict);
        }

        static async Task<JObject> ModelSchema(JObject args, Context context)
        {
            RequireReady();
            var id = Need(args, "model_id");
            var schema = await S.Schema(id);
            if (!schema.Ok) throw new ToolError(schema.Error.Message);
            var fields = SchemaForm.FieldsFromSchema(schema.Value, OutputTypes.Restrict);
            var model = S.Model(id);
            return Ok(new JObject
            {
                ["model_id"] = id,
                ["name"] = model?.Name ?? id,
                ["output_type"] = OutputTypes.TypeFor(model),
                ["prompt_field"] = SchemaForm.PromptName(fields),
                ["prompt_spark"] = SchemaForm.SparkAvailable(schema.Value, fields),
                ["presets"] = OutputTypes.Presets(id),
                ["fields"] = new JArray(fields.Select(FieldJson)),
            });
        }

        static JObject FieldJson(Field f)
        {
            var o = new JObject { ["name"] = f.Name, ["type"] = f.Type.ToString().ToLowerInvariant(), ["required"] = f.Required };
            if (f.Label != f.Name) o["label"] = f.Label;
            if (f.Description.Length > 0) o["description"] = f.Description;
            if (f.Default != null) o["default"] = f.Default;
            if (f.Options.Count > 0) o["options"] = new JArray(f.Options);
            if (f.Min.HasValue) o["min"] = f.Min.Value;
            if (f.Max.HasValue) o["max"] = f.Max.Value;
            if (f.IsFile) o["file_kind"] = f.Kind;
            if (f.MaxItems > 0) o["max_items"] = f.MaxItems;
            if (f.CostImpact) o["changes_price"] = true;
            return o;
        }

        /// <summary>Validates parameters against the schema and uploads Unity paths given for file inputs.</summary>
        static async Task<GenerateRequest> BuildRequest(JObject args, Context context, bool workflow)
        {
            RequireReady();
            var parameters = (args[workflow ? "inputs" : "parameters"] as JObject ?? args["parameters"] as JObject ?? new JObject()).DeepClone() as JObject;
            List<Field> fields;
            string id, name, type;
            JObject values;
            if (workflow)
            {
                id = Need(args, "workflow_id");
                var wf = await S.Client.GetWorkflow(id);
                if (!wf.Ok) throw new ToolError(wf.Error.Message);
                var definition = wf.Value["workflow"] as JObject ?? wf.Value;
                fields = SchemaForm.FieldsFromSchema(definition);
                name = Json.Str(definition, "name", id);
                type = OutputTypes.All;
                values = SchemaForm.InitialValues(fields);
            }
            else
            {
                id = Need(args, "model_id");
                fields = await FieldsFor(id);
                var model = S.Model(id);
                name = model?.Name ?? id;
                type = S_(args, "output_type");
                if (type.Length == 0) type = model != null ? OutputTypes.TypeFor(model) : OutputTypes.All;
                values = SchemaForm.InitialValues(fields, OutputTypes.Presets(id));
            }
            var unknown = parameters.Properties().Select(p => p.Name).Where(n => fields.All(f => f.Name != n)).ToList();
            if (unknown.Count > 0)
                throw new ToolError($"Unknown {(workflow ? "inputs" : "parameters")}: {string.Join(", ", unknown)}. Accepted: {string.Join(", ", fields.Select(f => f.Name))}.");
            foreach (var property in parameters.Properties()) values[property.Name] = property.Value;
            foreach (var field in fields.Where(f => f.IsFile && values[f.Name] != null)) values[field.Name] = await UploadPaths(values[field.Name], SchemaForm.RequiredModelFormat(field));
            var problems = SchemaForm.Validate(fields, values, workflow ? "" : id);
            if (problems.Count > 0) throw new ToolError(string.Join(" ", problems));
            var canPlace = ScenarioUserSettings.instance.AgentCanEditScene;
            var targetPath = S_(args, "target");
            var target = targetPath.Length > 0 ? Find(targetPath) : null;
            var folder = S_(args, "folder").TrimEnd('/');
            if (folder.Length > 0 && !folder.StartsWith("Assets/") && folder != "Assets") throw new ToolError("folder must be inside Assets.");
            var promptName = SchemaForm.PromptName(fields);
            return new GenerateRequest
            {
                OutputType = type,
                ModelId = workflow ? "" : id,
                WorkflowId = workflow ? id : "",
                ModelName = name,
                Payload = SchemaForm.Payload(fields, values),
                Prompt = promptName.Length > 0 ? Json.Str(values, promptName) : name,
                Place = !workflow && canPlace && B_(args, "place", true),
                Target = canPlace ? target : null,
                Origin = "agent: " + context.Client,
                Folder = folder,
            };
        }

        /// <summary>
        /// File inputs: Unity asset paths and absolute paths become Scenario asset ids, in
        /// the 3D format the input requires when it is not GLB.
        /// </summary>
        static async Task<JToken> UploadPaths(JToken value, string format = "")
        {
            async Task<JToken> One(JToken item)
            {
                var id = await Resolve(item);
                if (format.Length == 0 || !(id?.ToString() ?? "").StartsWith("asset_")) return id;
                var converted = await S.Convert(id.ToString(), format);
                if (!converted.Ok) throw new ToolError(converted.Error.Message);
                return converted.Value;
            }
            async Task<JToken> Resolve(JToken item)
            {
                var text = item?.ToString() ?? "";
                if (text.StartsWith("asset_")) return text;
                if (text.StartsWith("Assets/") || text.StartsWith("Packages/"))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(text);
                    if (asset == null) throw new ToolError($"No asset at {text}.");
                    var uploaded = await S.Upload(asset);
                    if (!uploaded.Ok) throw new ToolError(uploaded.Error.Message);
                    return uploaded.Value;
                }
                if (Path.IsPathRooted(text) && File.Exists(text))
                {
                    var uploaded = await S.UploadPath(text);
                    if (!uploaded.Ok) throw new ToolError(uploaded.Error.Message);
                    return uploaded.Value;
                }
                return item;
            }
            if (value is JArray array)
            {
                var result = new JArray();
                foreach (var item in array) result.Add(await One(item));
                return result;
            }
            return await One(value);
        }

        static string ApprovalText(double cu)
        {
            var user = ScenarioUserSettings.instance;
            return cu <= user.AgentAutoApproveCu && AgentServer.SessionSpent + cu <= user.AgentSessionBudgetCu
                ? "automatic (within the user's limits)"
                : "the user approves it in Unity (above the auto-approve limit or the session budget)";
        }

        static async Task<JObject> Estimate(JObject args, Context context)
        {
            var workflow = S_(args, "workflow_id").Length > 0;
            if (workflow && args["inputs"] == null && args["parameters"] != null) args["inputs"] = args["parameters"];
            var request = await BuildRequest(args, context, workflow);
            var quote = await S.Estimate(request);
            if (!quote.Ok) throw new ToolError(quote.Error.Message);
            return Ok(new JObject
            {
                ["cu"] = quote.Value.Cu,
                [workflow ? "workflow_id" : "model_id"] = workflow ? request.WorkflowId : request.ModelId,
                ["parameters"] = request.Payload,
                ["quote_valid_seconds"] = (int)QuoteBook.MaxAgeSeconds,
                ["approval"] = ApprovalText(quote.Value.Cu),
                ["spending"] = S.Client.SpendingDisabled ? "off: generate will be refused" : "on",
            });
        }

        static async Task<JObject> Generate(JObject args, Context context, bool workflow)
        {
            var started = Json.Now();
            var request = await BuildRequest(args, context, workflow);
            var block = S.BlockReason(request);
            if (block.Length > 0) throw new ToolError(block + " Nothing was sent.");
            var quote = S.QuoteFor(request);
            if (quote == null)
            {
                var priced = await S.Estimate(request);
                if (!priced.Ok) throw new ToolError(priced.Error.Message + " Nothing was sent.");
                quote = priced.Value;
            }
            var max = args["max_cu"] != null ? Json.ToDouble(args["max_cu"], -1) : -1;
            if (max >= 0 && quote.Cu > max) throw new ToolError($"This costs {quote.Cu} CU, above max_cu {max}. Nothing was sent.");
            var label = request.Prompt.Length > 60 ? request.Prompt.Substring(0, 60) + "…" : request.Prompt;
            if (!await AgentServer.RequestSpend(context.Client, workflow ? "workflow_run" : "generate", $"run {request.ModelName}: \"{label}\"", quote.Cu))
                throw new ToolError($"Not approved in Unity ({quote.Cu} CU). Nothing was sent.");
            var result = await S.Generate(request);
            if (!result.Ok)
            {
                if (result.Error.Ambiguous)
                    throw new ToolError(result.Error.Message + " The outcome is unknown: it may have run. Do not call generate again for this request; call jobs in a minute.");
                throw new ToolError(result.Error.Message);
            }
            AgentServer.AddSpent(quote.Cu);
            var row = result.Value;
            if (B_(args, "wait", true)) await WaitFor(row, Budget(started, I_(args, "timeout_seconds", CallSeconds)));
            return Ok(Following(row));
        }

        static bool Done(JobRow row)
        {
            if (row.IsUnresolved) return false;
            if (!row.IsFinal) return false;
            return row.State != JobState.Success || row.Imported || row.Error.StartsWith("Import failed");
        }

        static async Task WaitFor(JobRow row, int seconds)
        {
            var until = Json.Now() + Math.Max(0, Math.Min(CallSeconds, seconds));
            while (!Done(row) && Json.Now() < until) await Task.Delay(1000);
        }

        /// <summary>The row, plus the call to make next while it is still running.</summary>
        static JObject Following(JobRow row)
        {
            var data = RowJson(row);
            if (!Done(row))
                data["next"] = $"Still {row.State}: it imports{(row.Place ? " and places" : "")} itself when it finishes. Call jobs with local_id {row.LocalId} and wait_seconds {CallSeconds} to follow it. Do not generate it again.";
            return data;
        }

        static JObject RowJson(JobRow row)
        {
            var data = new JObject
            {
                ["local_id"] = row.LocalId,
                ["job_id"] = row.JobId,
                ["state"] = row.State,
                ["model"] = row.WorkflowId.Length > 0 ? row.WorkflowId : row.ModelId,
                ["prompt"] = row.Prompt,
                ["quoted_cu"] = row.QuotedCu,
                ["created"] = DateTimeOffset.FromUnixTimeSeconds((long)row.CreatedAt).ToString("u"),
            };
            if (row.ChargedCu.HasValue) data["charged_cu"] = row.ChargedCu.Value;
            if (row.AssetIds.Count > 0) data["asset_ids"] = new JArray(row.AssetIds);
            if (row.Files.Count > 0) data["files"] = new JArray(row.Files);
            if (row.Placed.Count > 0) data["placed"] = new JArray(row.Placed);
            if (row.Error.Length > 0) data["error"] = row.Error;
            if (row.Progress > 0 && row.IsActive) data["progress"] = row.Progress;
            return data;
        }

        static async Task<JObject> Jobs(JObject args, Context context)
        {
            var localId = S_(args, "local_id");
            var jobId = S_(args, "job_id");
            if (localId.Length > 0 || jobId.Length > 0)
            {
                var row = localId.Length > 0 ? S.Ledger.Find(localId) : S.Ledger.FindByJob(jobId);
                if (row == null) throw new ToolError("No such job in this Unity project.");
                var wait = I_(args, "wait_seconds", 0);
                if (wait > 0) await WaitFor(row, wait);
                return Ok(Following(row));
            }
            var limit = Math.Max(1, Math.Min(50, I_(args, "limit", 10)));
            return Ok(new JObject { ["jobs"] = new JArray(S.Ledger.Jobs.Where(r => r.State != JobState.Dismissed).Take(limit).Select(RowJson)) });
        }

        static async Task<JObject> WorkflowsFind(JObject args, Context context)
        {
            RequireReady();
            var team = S_(args, "scope") == "team";
            var result = await S.Client.ListWorkflows(team ? "private" : "public", 60);
            if (!result.Ok) throw new ToolError(result.Error.Message);
            var words = S_(args, "query").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = (result.Value["workflows"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(w => words.All(word => (Json.Str(w, "name") + " " + Json.Str(w, "description")).ToLowerInvariant().Contains(word)))
                .Select(w => new JObject
                {
                    ["id"] = Json.Str(w, "id"),
                    ["name"] = Json.Str(w, "name"),
                    ["description"] = Json.Str(w, "shortDescription", Json.Str(w, "description")),
                    ["inputs"] = new JArray(SchemaForm.FieldsFromSchema(w).Select(FieldJson)),
                    ["output_kinds"] = w["outputAssetKinds"],
                });
            return Ok(new JObject { ["workflows"] = new JArray(list) });
        }

        static Task<JObject> MotionsFind(JObject args, Context context)
        {
            var limit = Math.Max(1, Math.Min(100, I_(args, "limit", 20)));
            var found = MotionLibrary.Search(S_(args, "query"), S_(args, "category"));
            return Task.FromResult(Ok(new JObject
            {
                ["total"] = found.Count,
                ["motions"] = new JArray(found.Take(limit).Select(m => new JObject { ["id"] = m.Id, ["name"] = m.Name, ["category"] = m.Category, ["group"] = m.Group })),
                ["categories"] = new JArray(MotionLibrary.Categories),
            }));
        }

        static async Task<JObject> ImportAsset(JObject args, Context context)
        {
            RequireReady();
            var ids = (args["asset_ids"] as JArray)?.Select(a => a.ToString()).Where(a => a.StartsWith("asset_")).ToList() ?? new List<string>();
            if (ids.Count == 0 && S_(args, "asset_id").Length > 0) ids.Add(S_(args, "asset_id"));
            if (ids.Count == 0) throw new ToolError("asset_ids is required.");
            var type = S_(args, "output_type");
            if (type.Length == 0)
            {
                var first = await S.Client.GetAsset(ids[0]);
                if (!first.Ok) throw new ToolError(first.Error.Message);
                type = OutputTypes.TypeForKind(Files.KindOf(first.Value));
            }
            var canPlace = ScenarioUserSettings.instance.AgentCanEditScene;
            var target = S_(args, "target").Length > 0 ? Find(S_(args, "target")) : null;
            var result = await S.ImportAssets(ids, type, "", canPlace && B_(args, "place", false), canPlace ? target : null);
            if (!result.Ok) throw new ToolError(result.Error.Message);
            return Ok(new JObject
            {
                ["files"] = new JArray(result.Value.Select(f => f.Path)),
                ["selection"] = new JArray(Selection.gameObjects.Where(g => !EditorUtility.IsPersistent(g)).Select(PathOf)),
            });
        }

        static async Task<JObject> Upload(JObject args, Context context)
        {
            RequireReady();
            Result<string> result;
            var assetPath = S_(args, "asset_path");
            var objectPath = S_(args, "object_path");
            var capture = S_(args, "capture");
            var filePath = S_(args, "file_path");
            if (assetPath.Length > 0)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null) throw new ToolError($"No asset at {assetPath}.");
                result = await S.Upload(asset);
            }
            else if (objectPath.Length > 0) result = await Uploader.UploadObject(S.Client, Find(objectPath));
            else if (capture.Length > 0)
            {
                var path = capture == "scene_view" ? Capture.SceneView() : Capture.MainCamera();
                if (path == null) throw new ToolError(capture == "scene_view" ? "No Scene view is open." : "No camera is tagged MainCamera.");
                result = await S.UploadPath(path);
            }
            else if (filePath.Length > 0)
            {
                if (!File.Exists(filePath)) throw new ToolError($"No file at {filePath}.");
                result = await S.UploadPath(filePath);
            }
            else throw new ToolError("Give asset_path, object_path, capture or file_path.");
            if (!result.Ok) throw new ToolError(result.Error.Message);
            return Ok(new JObject { ["asset_id"] = result.Value });
        }

        static async Task<JObject> PromptSpark(JObject args, Context context)
        {
            RequireReady();
            var modelId = Need(args, "model_id");
            var prompt = Need(args, "prompt");
            var price = await S.PromptSparkPrice(modelId);
            if (!price.Ok) throw new ToolError(price.Error.Message);
            if (!await AgentServer.RequestSpend(context.Client, "prompt_spark", "improve a prompt with Prompt Spark", price.Value))
                throw new ToolError("Not approved in Unity. Nothing was sent.");
            var result = await S.ImprovePrompt(modelId, S.Model(modelId)?.Name ?? modelId, prompt, "agent: " + context.Client);
            if (!result.Ok) throw new ToolError(result.Error.Message);
            AgentServer.AddSpent(price.Value);
            return Ok(new JObject { ["prompt"] = result.Value, ["cu"] = price.Value });
        }

        // --- Scene tools ----------------------------------------------------------------------

        public static GameObject Find(string path)
        {
            var go = FindOrNull(path);
            if (go == null) throw new ToolError($"No object at \"{path}\". Call scene_info for paths.");
            return go;
        }

        public static GameObject FindOrNull(string path) => Placer.FindByPath(path);

        public static string PathOf(GameObject go) => Placer.PathOf(go);

        static Vector3? Vec3(JToken token)
        {
            if (token is JArray a && a.Count >= 3) return new Vector3((float)Json.ToDouble(a[0]), (float)Json.ToDouble(a[1]), (float)Json.ToDouble(a[2]));
            if (token is JObject o) return new Vector3((float)Json.Num(o, "x"), (float)Json.Num(o, "y"), (float)Json.Num(o, "z"));
            return null;
        }

        static JArray V(Vector3 v) => new JArray(Math.Round(v.x, 3), Math.Round(v.y, 3), Math.Round(v.z, 3));

        static Task<JObject> SceneInfo(JObject args, Context context)
        {
            var depth = Math.Max(1, Math.Min(8, I_(args, "depth", 3)));
            var max = Math.Max(10, Math.Min(1000, I_(args, "max_objects", 150)));
            var scenes = new JArray();
            var objects = new JArray();
            var count = 0;
            void Walk(Transform t, int level)
            {
                if (count >= max) return;
                count++;
                var entry = new JObject
                {
                    ["path"] = PathOf(t.gameObject),
                    ["components"] = new JArray(t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name)),
                };
                if (!t.gameObject.activeSelf) entry["active"] = false;
                if (t.childCount > 0 && level >= depth) entry["children"] = t.childCount;
                objects.Add(entry);
                if (level < depth)
                    foreach (Transform child in t) Walk(child, level + 1);
            }
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["loaded"] = scene.isLoaded,
                    ["dirty"] = scene.isDirty,
                    ["active"] = scene == SceneManager.GetActiveScene(),
                });
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects()) Walk(root.transform, 1);
            }
            var data = new JObject
            {
                ["scenes"] = scenes,
                ["objects"] = objects,
                ["selection"] = new JArray(Selection.gameObjects.Where(g => !EditorUtility.IsPersistent(g)).Select(PathOf)),
                ["render_pipeline"] = MaterialBuilder.Current.ToString(),
                ["mode"] = EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D ? "2D" : "3D",
            };
            if (count >= max) data["truncated"] = true;
            var view = SceneView.lastActiveSceneView;
            if (view != null) data["scene_view_pivot"] = V(view.pivot);
            return Task.FromResult(Ok(data));
        }

        static Task<JObject> ObjectGet(JObject args, Context context)
        {
            var go = Find(Need(args, "path"));
            var t = go.transform;
            var components = new JArray();
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue;
                var entry = new JObject { ["type"] = component.GetType().Name };
                if (component is Behaviour behaviour) entry["enabled"] = behaviour.enabled;
                if (B_(args, "properties", false) && !(component is Transform)) entry["properties"] = Properties(component);
                components.Add(entry);
            }
            return Task.FromResult(Ok(new JObject
            {
                ["path"] = PathOf(go),
                ["active"] = go.activeSelf,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["position"] = V(t.position),
                ["rotation"] = V(t.eulerAngles),
                ["scale"] = V(t.localScale),
                ["children"] = new JArray(t.Cast<Transform>().Select(c => c.name)),
                ["components"] = components,
            }));
        }

        static JObject Properties(Component component)
        {
            var result = new JObject();
            var so = new SerializedObject(component);
            var it = so.GetIterator();
            var count = 0;
            if (!it.NextVisible(true)) return result;
            do
            {
                if (it.name == "m_Script") continue;
                result[it.propertyPath] = ValueOf(it);
                if (++count >= 40) break;
            }
            while (it.NextVisible(false));
            return result;
        }

        static JToken ValueOf(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return Math.Round(p.doubleValue, 4);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Color: return "#" + ColorUtility.ToHtmlStringRGBA(p.colorValue);
                case SerializedPropertyType.Enum:
                    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length ? p.enumDisplayNames[p.enumValueIndex] : (JToken)p.intValue;
                case SerializedPropertyType.Vector2: return new JArray(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3: return V(p.vector3Value);
                case SerializedPropertyType.Vector4: return new JArray(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.ObjectReference:
                    var o = p.objectReferenceValue;
                    if (o == null) return null;
                    var path = AssetDatabase.GetAssetPath(o);
                    return path.Length > 0 ? path : o is GameObject g ? PathOf(g) : o is Component c ? PathOf(c.gameObject) : o.name;
                default: return $"({p.propertyType})";
            }
        }

        static Task<JObject> ObjectCreate(JObject args, Context context)
        {
            GameObject go;
            var prefabPath = S_(args, "prefab");
            var primitive = S_(args, "primitive");
            if (prefabPath.Length > 0)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) throw new ToolError($"No prefab or model at {prefabPath}.");
                go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            }
            else if (primitive.Length > 0)
            {
                if (!Enum.TryParse(primitive, true, out PrimitiveType type)) throw new ToolError("primitive must be cube, sphere, capsule, cylinder, plane or quad.");
                go = GameObject.CreatePrimitive(type);
            }
            else go = new GameObject();
            go.name = S_(args, "name", go.name.Length > 0 ? go.name : "GameObject");
            Undo.RegisterCreatedObjectUndo(go, "Scenario agent: object_create");
            var parentPath = S_(args, "parent");
            if (parentPath.Length > 0) Undo.SetTransformParent(go.transform, Find(parentPath).transform, "Scenario agent: parent");
            var position = Vec3(args["position"]);
            if (position.HasValue) go.transform.position = position.Value;
            else if (parentPath.Length == 0 && prefabPath.Length + primitive.Length > 0) go.transform.position = Placer.Pivot();
            var rotation = Vec3(args["rotation"]);
            if (rotation.HasValue) go.transform.eulerAngles = rotation.Value;
            var scale = Vec3(args["scale"]);
            if (scale.HasValue) go.transform.localScale = scale.Value;
            return Task.FromResult(Ok(SizeAndGround(go, args)));
        }

        /// <summary>Applies height and ground, and reports where the object ended up.</summary>
        static JObject SizeAndGround(GameObject go, JObject args)
        {
            var data = new JObject { ["path"] = PathOf(go) };
            var height = Json.ToDouble(args["height"], 0);
            if (height > 0) World.FitHeight(go, (float)height);
            if (B_(args, "ground", false) && !World.Ground(go)) data["note"] = "No ground below it: nothing with a collider under its position.";
            if (height > 0 || args["ground"] != null)
            {
                var bounds = World.BoundsOf(go);
                data["position"] = V(go.transform.position);
                data["size"] = V(bounds.size);
            }
            return data;
        }

        static Task<JObject> ObjectSet(JObject args, Context context)
        {
            var go = Find(Need(args, "path"));
            var collider = S_(args, "collider");
            if (collider.Length > 0 && collider != "mesh" && collider != "box") throw new ToolError("collider is mesh or box.");
            Undo.RecordObject(go, "Scenario agent: object_set");
            Undo.RecordObject(go.transform, "Scenario agent: object_set");
            if (args["name"] != null) go.name = S_(args, "name");
            if (args["active"] != null) go.SetActive(Json.Truthy(args["active"]));
            if (args["tag"] != null) go.tag = S_(args, "tag");
            if (args["layer"] != null)
            {
                var layer = LayerMask.NameToLayer(S_(args, "layer"));
                if (layer < 0) throw new ToolError($"No layer named {S_(args, "layer")}.");
                go.layer = layer;
            }
            if (args["parent"] != null)
            {
                var parentPath = S_(args, "parent");
                Undo.SetTransformParent(go.transform, parentPath.Length == 0 ? null : Find(parentPath).transform, "Scenario agent: parent");
            }
            var local = B_(args, "local", false);
            var position = Vec3(args["position"]);
            if (position.HasValue)
            {
                if (local) go.transform.localPosition = position.Value;
                else go.transform.position = position.Value;
            }
            var rotation = Vec3(args["rotation"]);
            if (rotation.HasValue)
            {
                if (local) go.transform.localEulerAngles = rotation.Value;
                else go.transform.eulerAngles = rotation.Value;
            }
            var scale = Vec3(args["scale"]);
            if (scale.HasValue) go.transform.localScale = scale.Value;
            var data = SizeAndGround(go, args);
            if (collider.Length > 0)
            {
                var added = World.AddColliders(go, collider);
                data["colliders_added"] = added;
                if (added == 0) data["collider_note"] = go.GetComponentInChildren<Collider>() != null ? "It already has a collider: left as is." : "No mesh to build a collider from.";
            }
            return Task.FromResult(Ok(data));
        }

        static Type ComponentType(string name)
        {
            var types = TypeCache.GetTypesDerivedFrom<Component>();
            var exact = types.FirstOrDefault(t => t.FullName == name);
            if (exact != null) return exact;
            var matches = types.Where(t => t.Name == name && !t.IsAbstract).ToList();
            return matches.FirstOrDefault(t => t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) ?? matches.FirstOrDefault();
        }

        static Task<JObject> ComponentAdd(JObject args, Context context)
        {
            var go = Find(Need(args, "path"));
            var type = ComponentType(Need(args, "type"));
            if (type == null) throw new ToolError($"No component type named {S_(args, "type")}.");
            var existing = go.GetComponent(type);
            if (existing != null && Attribute.IsDefined(type, typeof(DisallowMultipleComponent)))
                return Task.FromResult(Ok(new JObject { ["path"] = PathOf(go), ["type"] = type.Name, ["note"] = "Already present." }));
            var component = Undo.AddComponent(go, type);
            if (component == null) throw new ToolError($"Unity refused to add {type.Name} to {go.name}.");
            return Task.FromResult(Ok(new JObject { ["path"] = PathOf(go), ["type"] = type.Name }));
        }

        static Task<JObject> ComponentSet(JObject args, Context context)
        {
            var go = Find(Need(args, "path"));
            var typeName = Need(args, "type");
            var type = ComponentType(typeName);
            var component = type != null ? go.GetComponent(type) : go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == typeName);
            if (component == null) throw new ToolError($"{go.name} has no {typeName}. Add it with component_add.");
            var property = Need(args, "property");
            var value = args["value"];
            var so = new SerializedObject(component);
            var prop = so.FindProperty(property) ?? so.FindProperty("m_" + char.ToUpperInvariant(property[0]) + property.Substring(1));
            if (prop != null)
            {
                SetSerialized(prop, value);
                so.ApplyModifiedProperties();
                return Task.FromResult(Ok(new JObject { ["path"] = PathOf(go), ["property"] = prop.propertyPath, ["value"] = ValueOf(prop) }));
            }
            var member = component.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (member != null && member.CanWrite)
            {
                Undo.RecordObject(component, "Scenario agent: component_set");
                member.SetValue(component, Convert(value, member.PropertyType));
                EditorUtility.SetDirty(component);
                return Task.FromResult(Ok(new JObject { ["path"] = PathOf(go), ["property"] = member.Name, ["value"] = JToken.FromObject(member.GetValue(component)?.ToString() ?? "") }));
            }
            throw new ToolError($"{type?.Name ?? typeName} has no property {property}. Call object_get with properties=true to list them.");
        }

        static void SetSerialized(SerializedProperty prop, JToken value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.longValue = (long)Math.Round(Json.ToDouble(value));
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = Json.Truthy(value);
                    break;
                case SerializedPropertyType.Float:
                    prop.doubleValue = Json.ToDouble(value);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value?.ToString() ?? "";
                    break;
                case SerializedPropertyType.Color:
                    prop.colorValue = ToColor(value);
                    break;
                case SerializedPropertyType.Enum:
                    var text = value?.ToString() ?? "";
                    var index = Array.FindIndex(prop.enumNames, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) index = Array.FindIndex(prop.enumDisplayNames, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 && int.TryParse(text, out var number)) index = number;
                    if (index < 0) throw new ToolError($"{prop.name} takes one of: {string.Join(", ", prop.enumDisplayNames)}.");
                    prop.enumValueIndex = index;
                    break;
                case SerializedPropertyType.Vector2:
                    var v2 = value as JArray ?? throw new ToolError("Give [x, y].");
                    prop.vector2Value = new Vector2((float)Json.ToDouble(v2[0]), (float)Json.ToDouble(v2[1]));
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = Vec3(value) ?? throw new ToolError("Give [x, y, z].");
                    break;
                case SerializedPropertyType.Vector4:
                    var v4 = value as JArray ?? throw new ToolError("Give [x, y, z, w].");
                    prop.vector4Value = new Vector4((float)Json.ToDouble(v4[0]), (float)Json.ToDouble(v4[1]), (float)Json.ToDouble(v4[2]), (float)Json.ToDouble(v4[3]));
                    break;
                case SerializedPropertyType.Quaternion:
                    prop.quaternionValue = Quaternion.Euler(Vec3(value) ?? throw new ToolError("Give euler angles [x, y, z]."));
                    break;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = Reference(value?.ToString() ?? "", prop.type);
                    break;
                default:
                    throw new ToolError($"{prop.name} is a {prop.propertyType}; set its child properties instead (object_get lists them).");
            }
        }

        static UnityEngine.Object Reference(string text, string pptrType)
        {
            if (text.Length == 0) return null;
            var wanted = pptrType.StartsWith("PPtr<$") ? pptrType.Substring(6).TrimEnd('>') : "";
            if (text.StartsWith("Assets/") || text.StartsWith("Packages/"))
            {
                var all = AssetDatabase.LoadAllAssetsAtPath(text);
                var match = all.FirstOrDefault(a => a != null && a.GetType().Name == wanted) ?? AssetDatabase.LoadMainAssetAtPath(text);
                if (match == null) throw new ToolError($"No asset at {text}.");
                return match;
            }
            var go = Find(text);
            if (wanted.Length == 0 || wanted == "GameObject") return go;
            var component = go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == wanted);
            return component != null ? component : (UnityEngine.Object)go;
        }

        static Color ToColor(JToken value)
        {
            if (value is JArray a && a.Count >= 3)
                return new Color((float)Json.ToDouble(a[0]), (float)Json.ToDouble(a[1]), (float)Json.ToDouble(a[2]), a.Count > 3 ? (float)Json.ToDouble(a[3]) : 1f);
            var text = value?.ToString() ?? "";
            if (!text.StartsWith("#")) text = "#" + text;
            if (ColorUtility.TryParseHtmlString(text, out var color)) return color;
            throw new ToolError("Colors are [r, g, b, a] from 0 to 1, or #RRGGBB.");
        }

        static object Convert(JToken value, Type type)
        {
            if (type == typeof(float)) return (float)Json.ToDouble(value);
            if (type == typeof(double)) return Json.ToDouble(value);
            if (type == typeof(int)) return (int)Math.Round(Json.ToDouble(value));
            if (type == typeof(bool)) return Json.Truthy(value);
            if (type == typeof(string)) return value?.ToString() ?? "";
            if (type == typeof(Vector3)) return Vec3(value) ?? throw new ToolError("Give [x, y, z].");
            if (type == typeof(Color)) return ToColor(value);
            if (type.IsEnum) return Enum.Parse(type, value?.ToString() ?? "", true);
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return Reference(value?.ToString() ?? "", "PPtr<$" + type.Name + ">");
            return value?.ToObject(type);
        }

        static Task<JObject> ObjectDelete(JObject args, Context context)
        {
            var go = Find(Need(args, "path"));
            var path = PathOf(go);
            Undo.DestroyObjectImmediate(go);
            return Task.FromResult(Ok(new JObject { ["deleted"] = path }));
        }

        static Task<JObject> Select(JObject args, Context context)
        {
            var paths = (args["paths"] as JArray)?.Select(p => p.ToString()).ToList() ?? new List<string>();
            var objects = new List<UnityEngine.Object>();
            foreach (var path in paths)
            {
                if (path.StartsWith("Assets/") || path.StartsWith("Packages/"))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset != null) objects.Add(asset);
                }
                else
                {
                    var go = FindOrNull(path);
                    if (go != null) objects.Add(go);
                }
            }
            Selection.objects = objects.ToArray();
            if (objects.OfType<GameObject>().Any() && SceneView.lastActiveSceneView != null && !Application.isBatchMode) SceneView.lastActiveSceneView.FrameSelected();
            return Task.FromResult(Ok(new JObject { ["selected"] = objects.Count }));
        }

        static Task<JObject> MaterialAssign(JObject args, Context context)
        {
            var go = Find(Need(args, "path"));
            var material = AssetDatabase.LoadAssetAtPath<Material>(Need(args, "material"));
            if (material == null) throw new ToolError($"No material at {S_(args, "material")}.");
            var renderers = B_(args, "children", false) ? go.GetComponentsInChildren<Renderer>() : go.GetComponents<Renderer>();
            if (renderers.Length == 0) throw new ToolError($"{go.name} has no renderer.");
            var index = Math.Max(0, I_(args, "index", 0));
            foreach (var renderer in renderers)
            {
                Undo.RecordObject(renderer, "Scenario agent: material_assign");
                var materials = renderer.sharedMaterials;
                if (index >= materials.Length) Array.Resize(ref materials, index + 1);
                materials[index] = material;
                renderer.sharedMaterials = materials;
            }
            return Task.FromResult(Ok(new JObject { ["renderers"] = renderers.Length }));
        }

        static Task<JObject> SkyboxSet(JObject args, Context context)
        {
            var texture = S_(args, "texture");
            var materialPath = S_(args, "material");
            if (texture.Length > 0)
            {
                var (material, message) = SkyboxBuilder.Apply(texture, true);
                if (material == null) throw new ToolError(message.Length > 0 ? message : "Could not build a skybox from that texture.");
                return Task.FromResult(Ok(new JObject { ["material"] = AssetDatabase.GetAssetPath(material), ["note"] = message }));
            }
            if (materialPath.Length > 0)
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null) throw new ToolError($"No material at {materialPath}.");
                RenderSettings.skybox = material;
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                return Task.FromResult(Ok(new JObject { ["material"] = materialPath }));
            }
            throw new ToolError("Give texture or material.");
        }

        static Task<JObject> AssetsFind(JObject args, Context context)
        {
            var filter = S_(args, "query");
            var type = S_(args, "type");
            if (type.Length > 0) filter += " t:" + type;
            var label = S_(args, "label");
            if (label.Length > 0) filter += " l:" + label;
            var folder = S_(args, "folder").TrimEnd('/');
            var guids = folder.Length > 0 ? AssetDatabase.FindAssets(filter.Trim(), new[] { folder }) : AssetDatabase.FindAssets(filter.Trim());
            var limit = Math.Max(1, Math.Min(200, I_(args, "limit", 50)));
            var list = new JArray(guids.Take(limit).Select(AssetDatabase.GUIDToAssetPath).Select(p => new JObject
            {
                ["path"] = p,
                ["type"] = AssetDatabase.GetMainAssetTypeAtPath(p)?.Name ?? "",
            }));
            return Task.FromResult(Ok(new JObject { ["assets"] = list, ["total"] = guids.Length }));
        }

        static Task<JObject> SceneSave(JObject args, Context context)
        {
            var scene = SceneManager.GetActiveScene();
            var path = S_(args, "path");
            if (scene.path.Length == 0)
            {
                if (path.Length == 0) throw new ToolError("The active scene is untitled. Give path, e.g. Assets/Scenes/Level.unity.");
                if (!path.StartsWith("Assets/") || !path.EndsWith(".unity")) throw new ToolError("path must be Assets/....unity.");
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Assets");
                if (!EditorSceneManager.SaveScene(scene, path)) throw new ToolError("Unity could not save the scene.");
            }
            else if (!EditorSceneManager.SaveOpenScenes()) throw new ToolError("Unity could not save the scenes.");
            return Task.FromResult(Ok(new JObject { ["saved"] = SceneManager.GetActiveScene().path }));
        }

        static Task<JObject> ConsoleLogs(JObject args, Context context)
        {
            var entries = AgentLogs.Recent(Math.Min(200, I_(args, "limit", 30)), S_(args, "level", "all"));
            return Task.FromResult(Ok(new JObject
            {
                ["messages"] = new JArray(entries.Select(e => new JObject { ["type"] = e.Type, ["message"] = e.Message, ["where"] = e.Where })),
            }));
        }

        /// <summary>A point given as [x, y, z] or a scene object's path, with the object's bounds.</summary>
        static Bounds Region(JToken token, string key)
        {
            var point = Vec3(token);
            if (point.HasValue) return new Bounds(point.Value, Vector3.one * 4f);
            var path = token?.Type == JTokenType.String ? (string)token : "";
            if (path.Length == 0) throw new ToolError($"{key} is an object path or [x, y, z].");
            return World.BoundsOf(Find(path));
        }

        static async Task<JObject> CaptureView(JObject args, Context context)
        {
            var source = S_(args, "source", "main_camera");
            var around = args["around"] != null;
            var size = Math.Max(128, Math.Min(2048, I_(args, "max_size", around ? 1024 : 768)));
            string path;
            var data = new JObject();
            if (around)
            {
                var elevation = F_(args, "elevation", 25);
                path = Capture.Around(Region(args["around"], "around"), size, elevation, F_(args, "distance", 0));
                data["views"] = "Top row from the south-west and south-east, bottom row from the north-west and north-east (+z is north).";
            }
            else if (args["from"] != null)
            {
                var eye = Vec3(args["from"]) ?? throw new ToolError("from is [x, y, z].");
                var look = args["at"] != null ? Region(args["at"], "at").center : eye + Vector3.forward;
                // A point under the terrain would show the world from below.
                var ground = World.TerrainHeightAt(eye);
                if (!float.IsNaN(ground) && eye.y < ground + 0.3f)
                {
                    eye.y = ground + 1.7f;
                    data["note"] = $"from was under the terrain: raised to a player's eye, y = {eye.y:0.0}.";
                }
                data["from"] = V(eye);
                path = Capture.From(eye, look, size);
            }
            else if (source == "scene_view") path = Capture.SceneView(size) ?? Capture.Overview(size);
            else if (source == "main_camera") path = Capture.MainCamera(size) ?? Capture.SceneView(size) ?? Capture.Overview(size);
            else
            {
                var camera = Find(source).GetComponent<Camera>();
                if (camera == null) throw new ToolError($"{source} has no Camera.");
                path = Capture.Render(camera, 16, 9, size, "camera");
            }
            if (path == null) throw new ToolError("Nothing to capture: the render failed.");
            var png = File.ReadAllBytes(path);
            data["file"] = path;
            if (B_(args, "upload", false))
            {
                RequireReady();
                var uploaded = await S.UploadPath(path);
                if (!uploaded.Ok) throw new ToolError(uploaded.Error.Message);
                data["asset_id"] = uploaded.Value;
            }
            return Image(png, data);
        }
        // --- World tools ----------------------------------------------------------------------

        static Vector2? Vec2(JToken token)
        {
            if (token is JArray a && a.Count >= 2) return new Vector2((float)Json.ToDouble(a[0]), (float)Json.ToDouble(a[1]));
            return null;
        }

        static float F_(JToken token, string key, float fallback) => token?[key] == null ? fallback : (float)Json.ToDouble(token[key], fallback);

        static Task<JObject> TerrainCreate(JObject args, Context context)
        {
            var spec = new World.TerrainSpec
            {
                Name = S_(args, "name", "Terrain"),
                Heightmap = S_(args, "heightmap"),
                Resolution = I_(args, "resolution", 513),
                Smoothing = Math.Max(0, Math.Min(10, I_(args, "smoothing", 2))),
                EdgeFalloff = Mathf.Clamp(F_(args, "edge_falloff", 0), 0, 0.5f),
                Seed = I_(args, "seed", 1),
            };
            spec.Size = Vec3(args["size"]) ?? spec.Size;
            spec.Center = Vec3(args["center"]) ?? spec.Center;
            foreach (var area in (args["flat_areas"] as JArray ?? new JArray()).OfType<JObject>())
                spec.Flats.Add(new World.Flat
                {
                    Center = Vec2(area["center"]) ?? throw new ToolError("Each flat area needs center [x, z]."),
                    Radius = F_(area, "radius", 10),
                    Height = area["height"] == null ? float.NaN : F_(area, "height", 0),
                });
            foreach (var layer in (args["layers"] as JArray ?? new JArray()).OfType<JObject>())
                spec.Layers.Add(new World.Layer
                {
                    Source = Json.Str(layer, "material", Json.Str(layer, "texture")),
                    Normal = Json.Str(layer, "normal"),
                    Tile = F_(layer, "tile", 8),
                    MinHeight = F_(layer, "min_height", 0),
                    MaxHeight = F_(layer, "max_height", 1),
                    MinSlope = F_(layer, "min_slope", 0),
                    MaxSlope = F_(layer, "max_slope", 90),
                });
            var result = World.CreateTerrain(spec);
            var terrain = result.Terrain;
            var size = terrain.terrainData.size;
            var data = new JObject
            {
                ["path"] = PathOf(terrain.gameObject),
                ["data"] = AssetDatabase.GetAssetPath(terrain.terrainData),
                ["corner"] = V(terrain.transform.position),
                ["size"] = V(size),
                ["ground_height"] = new JArray(Math.Round(result.MinHeight, 2), Math.Round(result.MaxHeight, 2)),
                ["layers"] = terrain.terrainData.terrainLayers.Length,
                ["next"] = $"Place models with object_set (ground: true) or scatter (on: \"{PathOf(terrain.gameObject)}\").",
            };
            if (result.Notes.Count > 0) data["notes"] = new JArray(result.Notes);
            return Task.FromResult(Ok(data));
        }

        static Task<JObject> Scatter(JObject args, Context context)
        {
            var spec = new World.ScatterSpec
            {
                Source = Need(args, "source"),
                Count = Math.Max(1, Math.Min(500, I_(args, "count", 10))),
                On = S_(args, "on"),
                RandomYaw = B_(args, "random_yaw", true),
                Tilt = Mathf.Clamp(F_(args, "tilt", 0), 0, 45),
                MaxSlope = F_(args, "max_slope", 35),
                MinSlope = Mathf.Max(0, F_(args, "min_slope", 0)),
                MinDistance = Math.Max(0, F_(args, "min_distance", 0)),
                Collider = S_(args, "collider", "mesh"),
                Seed = I_(args, "seed", 1),
                Group = S_(args, "name"),
            };
            spec.Center = Vec2(args["center"]) ?? spec.Center;
            spec.Area = Vec2(args["area"]) ?? spec.Area;
            spec.Height = Vec2(args["height"]) ?? Vector2.zero;
            spec.Scale = Vec2(args["scale"]) ?? Vector2.one;
            foreach (var zone in (args["keep_clear"] as JArray ?? new JArray()).OfType<JObject>())
                spec.KeepClear.Add((Vec2(zone["center"]) ?? throw new ToolError("Each keep_clear zone needs center [x, z]."), F_(zone, "radius", 5)));
            var result = World.Scatter(spec);
            var data = new JObject
            {
                ["group"] = PathOf(result.Group),
                ["placed"] = result.Placed,
            };
            var skipped = new JObject();
            if (result.SkippedSlope > 0) skipped["too_steep"] = result.SkippedSlope;
            if (result.SkippedFlat > 0) skipped["too_flat"] = result.SkippedFlat;
            if (result.SkippedClear > 0) skipped["keep_clear"] = result.SkippedClear;
            if (result.SkippedCrowded > 0) skipped["crowded"] = result.SkippedCrowded;
            if (result.SkippedNoGround > 0) skipped["no_ground"] = result.SkippedNoGround;
            if (skipped.HasValues) data["rejected_spots"] = skipped;
            if (result.Placed < spec.Count)
                data["note"] = $"Only {result.Placed} of {spec.Count} fit: widen the area, lower min_distance or raise max_slope" + (spec.MinSlope > 0 ? ", or lower min_slope." : ".");
            return Task.FromResult(Ok(data));
        }

        static IEnumerable<Vector2> Points(JToken token, string key)
        {
            if (!(token is JArray list)) throw new ToolError($"{key} is a list of [x, z] points.");
            return list.Select(p => Vec2(p) ?? throw new ToolError($"Each point in {key} is [x, z]."));
        }

        static GameObject FindOptional(string path) => path.Length > 0 ? Find(path) : null;

        static JObject SampleJson(World.Sample sample)
        {
            var o = new JObject { ["at"] = new JArray(Math.Round(sample.At.x, 2), Math.Round(sample.At.y, 2)) };
            if (!sample.Found)
            {
                o["height"] = null;
                o["note"] = "no ground";
                return o;
            }
            o["height"] = Math.Round(sample.Height, 2);
            o["slope"] = Math.Round(sample.Slope, 1);
            o["surface"] = PathOf(sample.Surface);
            return o;
        }

        static Task<JObject> GroundHeight(JObject args, Context context)
        {
            var on = FindOptional(S_(args, "on"));
            var data = new JObject();
            if (args["points"] != null)
            {
                var points = Points(args["points"], "points").Take(200).ToList();
                data["points"] = new JArray(World.Measure(points, on).Select(SampleJson));
            }
            if (args["from"] != null || args["to"] != null)
            {
                var from = Vec2(args["from"]) ?? throw new ToolError("from is [x, z].");
                var to = Vec2(args["to"]) ?? throw new ToolError("to is [x, z].");
                var length = Vector2.Distance(from, to);
                var step = Math.Max(0.1f, F_(args, "step", 1));
                if (length / step > 400) step = length / 400;
                var count = Mathf.Max(2, Mathf.CeilToInt(length / step) + 1);
                var points = Enumerable.Range(0, count).Select(i => Vector2.Lerp(from, to, count == 1 ? 0 : i / (float)(count - 1))).ToList();
                var samples = World.Measure(points, on);
                var found = samples.Where(x => x.Found).ToList();
                var line = new JObject
                {
                    ["from"] = new JArray(from.x, from.y),
                    ["to"] = new JArray(to.x, to.y),
                    ["length"] = Math.Round(length, 2),
                    ["step"] = Math.Round(length / (count - 1), 2),
                    ["heights"] = new JArray(samples.Select(x => x.Found ? (JToken)Math.Round(x.Height, 2) : JValue.CreateNull())),
                };
                if (found.Count > 0)
                {
                    line["lowest"] = Math.Round(found.Min(x => x.Height), 2);
                    line["highest"] = Math.Round(found.Max(x => x.Height), 2);
                }
                float steepest = 0, biggest = 0;
                for (var i = 1; i < samples.Count; i++)
                {
                    if (!samples[i].Found || !samples[i - 1].Found) continue;
                    var rise = Mathf.Abs(samples[i].Height - samples[i - 1].Height);
                    var run = Vector2.Distance(samples[i].At, samples[i - 1].At);
                    biggest = Mathf.Max(biggest, rise);
                    steepest = Mathf.Max(steepest, Mathf.Atan2(rise, run) * Mathf.Rad2Deg);
                }
                line["steepest_slope"] = Math.Round(steepest, 1);
                line["biggest_step"] = Math.Round(biggest, 2);
                var missing = samples.Count(x => !x.Found);
                if (missing > 0) line["no_ground"] = missing;
                data["line"] = line;
            }
            if (!data.HasValues) throw new ToolError("Give points, or from and to.");
            return Task.FromResult(Ok(data));
        }

        static Task<JObject> MakeWalkable(JObject args, Context context)
        {
            var result = World.MakeWalkable(new World.WalkableSpec
            {
                Target = Need(args, "target"),
                From = Vec2(args["from"]),
                To = Vec2(args["to"]),
                Width = Math.Max(0, F_(args, "width", 0)),
                Rails = B_(args, "rails", true),
                RailHeight = Mathf.Clamp(F_(args, "rail_height", 1.1f), 0.3f, 3f),
                Ramps = B_(args, "ramps", true),
                KeepColliders = B_(args, "keep_colliders", false),
            });
            JObject End(Vector3 point, float ground, bool ramp)
            {
                var o = new JObject { ["deck"] = V(point) };
                if (!float.IsNaN(ground)) o["ground_beyond"] = Math.Round(ground, 2);
                o["joined"] = ramp ? "ramp" : !float.IsNaN(ground) && Mathf.Abs(point.y - ground) < 0.3f ? "level" : "no";
                return o;
            }
            var data = new JObject
            {
                ["walkway"] = PathOf(result.Walkway),
                ["length"] = Math.Round(result.Length, 2),
                ["width"] = Math.Round(result.Width, 2),
                ["start"] = End(result.Start, result.StartGround, result.StartRamp),
                ["end"] = End(result.End, result.EndGround, result.EndRamp),
                ["holes_bridged"] = result.Holes,
                ["bumps_smoothed"] = result.Bumps,
                ["own_colliders_off"] = result.CollidersOff,
                ["next"] = $"Playtest across it: play walk with waypoints at {V(result.Start)} and {V(result.End)}.",
            };
            if (result.Notes.Count > 0) data["notes"] = new JArray(result.Notes);
            return Task.FromResult(Ok(data));
        }

        static Task<JObject> PlaceAlong(JObject args, Context context)
        {
            var spec = new World.AlongSpec
            {
                Source = Need(args, "source"),
                Path = Points(args["path"], "path").ToList(),
                Spacing = F_(args, "spacing", 6),
                Offset = Math.Max(0, F_(args, "offset", 2)),
                Side = S_(args, "side", "alternate"),
                Start = Math.Max(0, F_(args, "start", 0)),
                Height = Math.Max(0, F_(args, "height", 0)),
                Face = S_(args, "face", "path"),
                On = S_(args, "on"),
                Collider = S_(args, "collider", "mesh"),
                MaxSlope = F_(args, "max_slope", 40),
                Jitter = Math.Max(0, F_(args, "jitter", 0)),
                Seed = I_(args, "seed", 1),
                Group = S_(args, "name"),
            };
            if (args["light"] is JObject light)
            {
                spec.Light = new World.PointLight
                {
                    Intensity = F_(light, "intensity", 2),
                    Range = F_(light, "range", 8),
                    At = Mathf.Clamp01(F_(light, "at", 0.75f)),
                };
                if (light["color"] != null) spec.Light.Color = ToColor(light["color"]);
            }
            if (spec.Path.Count > 1000) throw new ToolError("path has more than 1,000 points.");
            var result = World.PlaceAlong(spec);
            var data = new JObject
            {
                ["group"] = PathOf(result.Group),
                ["placed"] = result.Placed,
                ["path_length"] = Math.Round(result.PathLength, 2),
                ["positions"] = new JArray(result.Positions.Select(V)),
            };
            var skipped = new JObject();
            if (result.SkippedSlope > 0) skipped["too_steep"] = result.SkippedSlope;
            if (result.SkippedNoGround > 0) skipped["no_ground"] = result.SkippedNoGround;
            if (skipped.HasValues) data["rejected_spots"] = skipped;
            return Task.FromResult(Ok(data));
        }

        static Task<JObject> AtmosphereSet(JObject args, Context context)
        {
            float? Opt(string key) => args[key] == null ? (float?)null : (float)Json.ToDouble(args[key]);
            Color? Tint(string key) => args[key] == null ? (Color?)null : ToColor(args[key]);
            var result = Atmosphere.Apply(new Atmosphere.Spec
            {
                Preset = S_(args, "preset"),
                SunElevation = Opt("sun_elevation"),
                SunAzimuth = Opt("sun_azimuth"),
                SunIntensity = Opt("sun_intensity"),
                SunColor = Tint("sun_color"),
                Fog = args["fog"] == null ? (bool?)null : Json.Truthy(args["fog"]),
                FogDensity = Opt("fog_density"),
                FogColor = Tint("fog_color"),
                Ambient = Tint("ambient_color"),
                Exposure = Opt("exposure"),
                Contrast = Opt("contrast"),
                Saturation = Opt("saturation"),
                Temperature = Opt("temperature"),
                Bloom = Opt("bloom"),
                Vignette = Opt("vignette"),
                Post = B_(args, "post", true),
            });
            var sun = result.Sun.transform.eulerAngles;
            var data = new JObject
            {
                ["sun"] = PathOf(result.Sun.gameObject),
                ["sun_elevation"] = Math.Round(sun.x > 180 ? sun.x - 360 : sun.x, 1),
                ["sun_azimuth"] = Math.Round(sun.y, 1),
                ["fog"] = RenderSettings.fog ? (JToken)Math.Round(RenderSettings.fogDensity, 4) : "off",
                ["ambient"] = RenderSettings.ambientMode.ToString().ToLowerInvariant(),
            };
            if (result.Volume != null)
            {
                data["volume"] = PathOf(result.Volume);
                data["profile"] = result.Profile;
            }
            if (result.Notes.Count > 0) data["notes"] = new JArray(result.Notes);
            data["next"] = "capture_view (around or from) to check the look.";
            return Task.FromResult(Ok(data));
        }

        static Task<JObject> MakePlayable(JObject args, Context context)
        {
            var result = Playable.Make(new Playable.Spec
            {
                Character = Need(args, "character"),
                Idle = S_(args, "idle"),
                Walk = S_(args, "walk"),
                Run = S_(args, "run"),
                Jump = S_(args, "jump"),
                Height = Math.Max(0, F_(args, "height", 0)),
                WalkSpeed = Math.Max(0, F_(args, "walk_speed", 0)),
                RunSpeed = Math.Max(0, F_(args, "run_speed", 0)),
                Turn = F_(args, "turn", 0),
                RunByDefault = B_(args, "run_by_default", false),
                Footstep = S_(args, "footstep"),
                Camera = B_(args, "camera", true),
            });
            var data = new JObject
            {
                ["player"] = PathOf(result.Player),
                ["position"] = V(result.Player.transform.position),
                ["height"] = Math.Round(result.Height, 2),
                ["walk_speed"] = Math.Round(result.Component.walkSpeed, 2),
                ["run_speed"] = Math.Round(result.Component.runSpeed, 2),
                ["controller"] = AssetDatabase.GetAssetPath(result.Controller),
                ["motions"] = JObject.FromObject(result.Clips),
                ["controls"] = "WASD or left stick to move, Shift to run, Space to jump, right mouse button or right stick to look around.",
            };
            if (result.Camera != null) data["camera"] = PathOf(result.Camera.gameObject);
            if (result.Notes.Count > 0) data["notes"] = new JArray(result.Notes);
            return Task.FromResult(Ok(data));
        }

        static JObject PlayerJson(ScenarioPlayer player) => new JObject
        {
            ["path"] = PathOf(player.gameObject),
            ["position"] = V(player.transform.position),
            ["grounded"] = player.Grounded,
            ["falls"] = player.Falls,
        };

        // The walk or recording in progress, kept until its result is returned.
        static Task<JObject> _playRun;
        static string _playRunKind = "";

        static JObject PlayStatus()
        {
            var data = new JObject
            {
                ["playing"] = EditorApplication.isPlaying,
                ["players"] = new JArray(UnityEngine.Object.FindObjectsByType<ScenarioPlayer>(FindObjectsSortMode.None).Select(PlayerJson)),
            };
            if (_playRun == null) return data;
            data["running"] = _playRunKind;
            var pilot = PlaySession.Walking;
            if (pilot != null && pilot.waypoints.Count > 0)
                data["walk"] = new JObject
                {
                    ["toward_waypoint"] = Math.Min(pilot.Current + 1, pilot.waypoints.Count),
                    ["of"] = pilot.waypoints.Count,
                    ["seconds"] = Math.Round(pilot.Elapsed, 1),
                };
            data["next"] = $"The {_playRunKind} is still running. Call play with action status and wait_seconds {CallSeconds} to get its result.";
            return data;
        }

        /// <summary>Waits for the running walk or recording within the call's time, then returns its result or where it is.</summary>
        static async Task<JObject> FollowPlay(double started, int seconds)
        {
            var run = _playRun;
            if (run == null) return Ok(PlayStatus());
            await PlaySession.Until(() => run.IsCompleted, Budget(started, seconds));
            if (!run.IsCompleted) return Ok(PlayStatus());
            _playRun = null;
            if (run.IsFaulted) throw new ToolError(run.Exception.GetBaseException().Message);
            return Ok(run.Result);
        }

        static async Task<JObject> WalkResultJson(PlaySession.WalkSpec spec)
        {
            var walk = await PlaySession.Walk(spec);
            var reached = walk.ReachedAt.Count(t => t >= 0);
            var data = new JObject
            {
                ["reached"] = new JArray(walk.ReachedAt.Select((t, i) => new JObject { ["waypoint"] = i + 1, ["seconds"] = t >= 0 ? Math.Round(t, 1) : (JToken)"given up (stuck)" })),
                ["falls"] = walk.Falls,
                ["jumps"] = walk.Jumps,
                ["seconds"] = Math.Round(walk.Seconds, 1),
                ["longest_stuck"] = Math.Round(walk.LongestStuck, 1),
                ["start"] = V(walk.Start),
                ["end"] = V(walk.End),
                ["summary"] = $"Reached {reached} of {spec.Points.Count} points in {walk.Seconds:0.0} s" + (walk.Falls > 0 ? $", fell {walk.Falls} time(s)" : ", no falls") + (walk.TimedOut ? ", timed out." : "."),
            };
            if (walk.Folder.Length > 0)
            {
                data["frames"] = walk.Frames;
                data["folder"] = walk.Folder;
            }
            return data;
        }

        static async Task<JObject> RecordResultJson(PlaySession.RecordSpec spec)
        {
            var (frames, folder) = await PlaySession.Record(spec);
            return new JObject { ["frames"] = frames, ["folder"] = folder, ["encode"] = $"ffmpeg -framerate {spec.FrameRate} -i \"{folder}/%05d.png\" -c:v libx264 -pix_fmt yuv420p -crf 16 out.mp4" };
        }

        static PlaySession.RecordSpec RecordSpec(JObject args) => new PlaySession.RecordSpec
        {
            Folder = S_(args, "folder"),
            Camera = S_(args, "camera"),
            Width = I_(args, "width", 1920),
            Height = I_(args, "height", 1080),
            FrameRate = I_(args, "fps", 30),
            Seconds = Math.Max(0, F_(args, "seconds", 0)),
        };

        static async Task Playing(double started)
        {
            if (EditorApplication.isPlaying) return;
            if (!await PlaySession.Start(Math.Max(1, Budget(started, CallSeconds))))
                throw new ToolError(EditorApplication.isPlayingOrWillChangePlaymode
                    ? "Unity is still entering Play mode. Call play with action status, then try again."
                    : "Unity did not enter Play mode. Check console_logs for compile errors.");
        }

        static void NotRunning()
        {
            if (_playRun != null && !_playRun.IsCompleted)
                throw new ToolError($"A {_playRunKind} is running. Call play with action status and wait_seconds {CallSeconds}, or stop.");
        }

        static async Task<JObject> Play(JObject args, Context context)
        {
            var started = Json.Now();
            switch (Need(args, "action"))
            {
                case "start":
                    await Playing(started);
                    return Ok(PlayStatus());
                case "stop":
                    _playRun = null;
                    if (!await PlaySession.Stop(Math.Max(1, Budget(started, CallSeconds)))) throw new ToolError("Unity did not leave Play mode yet. Call play with action status.");
                    return Ok(PlayStatus());
                case "status":
                    return await FollowPlay(started, I_(args, "wait_seconds", 0));
                case "record":
                    NotRunning();
                    await Playing(started);
                    _playRun = RecordResultJson(RecordSpec(args));
                    _playRunKind = "recording";
                    return await FollowPlay(started, CallSeconds);
                case "walk":
                    var points = new List<ScenarioAutopilot.Waypoint>();
                    foreach (var point in (args["waypoints"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var at = Json.Str(point, "at");
                        Vector3 position;
                        if (at.Length > 0) position = Find(at).transform.position;
                        else position = Vec3(point["position"]) ?? throw new ToolError("Each waypoint needs position [x, y, z] or at (an object path).");
                        points.Add(new ScenarioAutopilot.Waypoint { position = position, run = Json.Truthy(point["run"]), jump = Json.Truthy(point["jump"]), wait = F_(point, "wait", 0) });
                    }
                    if (points.Count == 0) throw new ToolError("Give waypoints.");
                    NotRunning();
                    await Playing(started);
                    _playRun = WalkResultJson(new PlaySession.WalkSpec
                    {
                        Player = S_(args, "player"),
                        Points = points,
                        Timeout = Math.Max(5, F_(args, "timeout", 90)),
                        Record = B_(args, "record", false) ? RecordSpec(args) : null,
                    });
                    _playRunKind = "walk";
                    return await FollowPlay(started, CallSeconds);
                default:
                    throw new ToolError("action is start, stop, status, walk or record.");
            }
        }
    }
}
