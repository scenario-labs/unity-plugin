using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>
    /// An output type is a preset over the whole catalog: which capabilities it
    /// shows, its quick picks, presets, and how results land in Unity. It never
    /// limits which model can be used: "All models" reaches every one.
    /// </summary>
    public sealed class OutputType
    {
        public string Id;
        public string Title;
        public string Icon;
        public string Folder;
        public string[] Capabilities;
        /// <summary>Quick picks, the first is the default (chosen 2026-10-18 from recommend and the catalog).</summary>
        public string[] QuickPicks;
        public string Placeholder;
        public string Hint;
        /// <summary>Extra filter for types that are a family of models (Material, Skybox).</summary>
        public System.Func<ModelInfo, bool> Family;
        public string DownloadFormat = "";

        public bool Accepts(ModelInfo model)
        {
            if (Family != null) return Family(model);
            if (Capabilities == null || Capabilities.Length == 0) return true;
            return model.Capabilities.Any(c => Capabilities.Contains(c));
        }
    }

    /// <summary>A typed step: something an asset of a kind can become.</summary>
    public sealed class AssetStep
    {
        public string Id;
        public string Title;
        public string Kind;
        public string OutputType;
        public string Capability;
        public string DefaultModel;
        /// <summary>Other models that do the same step, offered as quick picks while the step is open.</summary>
        public string[] Alternatives = new string[0];
        /// <summary>Submenu in the Next menus ("Rig and animate"), or "" for the top level.</summary>
        public string Group = "";
        /// <summary>One line under the model card: what the step does and what else it needs.</summary>
        public string Hint = "";

        public IEnumerable<string> Models => new[] { DefaultModel }.Concat(Alternatives);
    }

    public static class OutputTypes
    {
        /// <summary>
        /// Names of the models this file refers to, as the catalog listed them on 2026-10-18.
        /// Shown only until the catalog has loaded (first open of a project).
        /// </summary>
        static readonly Dictionary<string, string> KnownNames = new Dictionary<string, string>
        {
            ["model_openai-gpt-image-2-5-sunburst"] = "GPT Image 2.5 Sunburst",
            ["model_openai-gpt-image-2-5-flare"] = "GPT Image 2.5 Flare",
            ["model_google-gemini-nano-banana-2-1"] = "Nano Banana 2.1",
            ["model_bytedance-seedream-5-0-pro"] = "Seedream 5.0 Pro",
            ["model_retrodiffusion-plus"] = "Retro Diffusion Plus",
            ["model_minimax-h3"] = "Minimax H3",
            ["model_google-omni-1-1-flash"] = "Gemini Omni 1.1 Flash",
            ["model_alibaba-wan-3-0"] = "Wan 3.0",
            ["model_bytedance-seedance-2-5"] = "Seedance 2.5",
            ["model_meshy-7-1-txt23d"] = "Meshy 7.1 - Text-to-3D",
            ["model_meshy-7-1-img23d"] = "Meshy 7.1 - Image to 3D",
            ["model_rodin-hyper3d-v2-5-text-to-3d"] = "Rodin Gen-2.5 Text to 3D",
            ["model_hunyuan-3d-pro-3-1-i23d"] = "Hunyuan 3D 3.1 Pro",
            ["model_tripo-p2-text-to-3d"] = "Tripo P2 Text to 3D",
            ["model_meshy-t2-5-smart-topology"] = "Meshy T2.5 Smart Topology",
            ["model_mirelo-sfx-2-0-text-to-audio"] = "Mirelo SFX 2.0 Text to Audio",
            ["model_elevenlabs-sound-effects-v2"] = "ElevenLabs Sound Effects 2",
            ["model_sonilo-v1-1-text-to-sound-effects"] = "Sonilo V1.1 Text to SFX",
            ["model_elevenlabs-music-v2-5"] = "ElevenLabs Music v2.5",
            ["model_google-gemini-3-8-flash-tts"] = "Gemini 3.8 Flash TTS",
            ["model_patina-material"] = "PATINA Material",
            ["model_patina"] = "PATINA Image to Maps",
            ["model_patina-material-extract"] = "PATINA Material Extract",
            ["model_scenario-skybox-gpt"] = "Scenario Skybox GPT",
            ["model_scenario-skybox-flux"] = "Scenario Skybox Flux.1",
            ["model_hunyuan-world-image-to-skybox"] = "HY World - Image to Skybox",
            ["model_rodin-hyper3d-v2-5"] = "Rodin Gen-2.5",
            ["model_birefnet-background-removal"] = "Birefnet Background Removal",
            ["model_clarity-pro-upscaler"] = "Clarity Pro Upscaler",
            ["model_sc-vectorizer"] = "Scenario Vectorize",
            ["model_sc-pixelate"] = "Pixelate",
            ["model_meshy-7-retexture"] = "Meshy 7 - Retexture",
            ["model_tripo-retopology-v3"] = "Tripo Retopology v3",
            ["model_tripo-smart-uv"] = "Tripo Smart UV",
            ["model_meshy-rigging"] = "Meshy Rigging",
            ["model_scenario-claude-opus-5-5-3d"] = "Claude Opus 5.5 3D",
            ["model_tripo-segmentation-v2"] = "Tripo Segmentation v2",
            ["model_kandinsky-6-vsr-video-upscale"] = "Kandinsky 6.0 VSR Video Upscale",
            ["model_mirelo-sfx-2-0-video-to-audio"] = "Mirelo SFX 2.0 Video to Audio",
            ["model_elevenlabs-voice-changer"] = "ElevenLabs Voice Changer",
            ["model_elevenlabs-voice-isolator"] = "ElevenLabs Voice Isolator",
            ["model_scenario-voxels"] = "Voxel Crafter 1.0",
            ["model_tripo-p2-image-to-3d"] = "Tripo P2",
            ["model_hitem-3d-3-0"] = "Hitem3D 3.0",
            ["model_trellis-2"] = "Trellis 2",
            ["model_scenario-gpt6-astra-3d"] = "GPT-6 Astra 3D",
            ["model_vast-ai-triposplat"] = "TripoSplat",
            ["model_hunyuan-world-image-to-splat"] = "HY World - Image to Splat",
            ["model_worldlabs-marble-1-1"] = "Marble 1.1 - World Generation",
            ["model_tripo-v3-0-texturing"] = "Tripo 3.0 Texturing",
            ["model_tencent-texture-edit"] = "Tencent Texture Edit",
            ["model_trellis-2-retexture"] = "Trellis 2 Retexture",
            ["model_yvo3d-retexture"] = "YVO3D Retexture",
            ["model_tripo-retopology"] = "Tripo Retopology",
            ["model_tencent-smarttopology"] = "Hunyuan Polygen 1.5",
            ["model_meshy-remesh"] = "Meshy Remesh",
            ["model_ultrashape-1-0"] = "Ultrashape 1.0",
            ["model_meshy-uv-unwrap"] = "Meshy UV Unwrap",
            ["model_tencent-uv-unwrapping"] = "Tencent UV Unwrapping",
            ["model_uthana-character-rigging"] = "Uthana Character Rigging",
            ["model_cartwheel-character-rigging"] = "Cartwheel Character Rigging",
            ["model_tripo-rigging-v1"] = "Tripo Rigging 1.0 [Biped]",
            ["model_tripo-rigging-v2-5"] = "Tripo Rigging 2.5",
            ["model_meshy-animation"] = "Meshy Animation",
            ["model_cartwheel-text-to-motion"] = "Cartwheel Text to Motion",
            ["model_uthana-text-to-motion-3.0"] = "Uthana Text to Motion 3.0",
            ["model_meshy-text-to-motion"] = "Meshy Text to Motion",
            ["model_cartwheel-video-to-motion"] = "Cartwheel Video to Motion",
            ["model_uthana-video-to-motion-2.1"] = "Uthana Video to Motion 2.1",
            ["model_hunyuan-3d-part"] = "Hunyuan 3D Part",
            ["model_hitem-3d-split"] = "Hitem3D Split",
            ["model_rodin-hyper3d-bang"] = "Rodin Hyper3D Bang!",
            ["model_tripo-segmentation-v1"] = "Tripo Segmentation v1",
            ["model_tripo-stylization"] = "Tripo Stylization",
            ["model_hitem-3d-multicolor"] = "Hitem3D Multicolor",
            ["model_minimax-h3-max-3d-to-video"] = "MiniMax H3 Max 3D to Video",
        };

        public static string KnownName(string modelId) => modelId != null && KnownNames.TryGetValue(modelId, out var name) ? name : null;

        public const string Image = "image";
        public const string Video = "video";
        public const string Model3D = "model3d";
        public const string Motion = "motion";
        public const string Audio = "audio";
        public const string Material = "material";
        public const string Skybox = "skybox";
        public const string All = "all";

        public static readonly List<OutputType> List = new List<OutputType>
        {
            new OutputType
            {
                Id = Image, Title = "Image", Icon = "image", Folder = "Image", Capabilities = new[] { "txt2img", "img2img" },
                QuickPicks = new[]
                {
                    "model_openai-gpt-image-2-5-sunburst", "model_openai-gpt-image-2-5-flare", "model_google-gemini-nano-banana-2-1",
                    "model_bytedance-seedream-5-0-pro", "model_retrodiffusion-plus",
                },
                Placeholder = "A stylized wooden treasure chest, game prop, clean background",
                Hint = "Sprites, icons, textures, concept art. Lands on the selected sprite, image or renderer.",
            },
            new OutputType
            {
                Id = Video, Title = "Video", Icon = "video", Folder = "Video", Capabilities = new[] { "txt2video", "img2video", "video2video" },
                QuickPicks = new[] { "model_minimax-h3", "model_google-omni-1-1-flash", "model_alibaba-wan-3-0", "model_bytedance-seedance-2-5" },
                Placeholder = "Slow dolly shot through a misty pine forest at dawn",
                Hint = "Clips for cinematics, trailers and backgrounds. Lands on the selected VideoPlayer or a new screen.",
            },
            new OutputType
            {
                Id = Model3D, Title = "3D", Icon = "cube", Folder = "Models", Capabilities = new[] { "txt23d", "img23d", "3d23d" }, DownloadFormat = "glb",
                QuickPicks = new[]
                {
                    "model_meshy-7-1-txt23d", "model_meshy-7-1-img23d", "model_rodin-hyper3d-v2-5-text-to-3d", "model_hunyuan-3d-pro-3-1-i23d",
                    "model_tripo-p2-text-to-3d", "model_meshy-t2-5-smart-topology", "model_scenario-voxels",
                },
                Placeholder = "A stylized wooden treasure chest, game-ready prop",
                Hint = "Textured GLB models, imported as prefabs at the Scene view pivot. For the closest match, make an image first, then Make a 3D model from it. Next steps retexture, remesh, rig, animate and split them.",
            },
            new OutputType
            {
                Id = Motion, Title = "Motion", Icon = "motion", Folder = "Animations", Capabilities = new[] { "txt23d", "3d23d", "video23d" }, DownloadFormat = "glb",
                QuickPicks = new[]
                {
                    "model_cartwheel-text-to-motion", "model_uthana-text-to-motion-3.0", "model_meshy-text-to-motion", "model_meshy-animation",
                    "model_cartwheel-video-to-motion", "model_uthana-video-to-motion-2.1",
                },
                Family = IsMotion,
                Placeholder = "A character walks forward, looks around, then waves",
                Hint = "Character animation from a prompt, a video or Meshy's motion library, on your character or a default one. Imported with an Animator, ready to play.",
            },
            new OutputType
            {
                Id = Audio, Title = "Audio", Icon = "audio", Folder = "Audio", Capabilities = new[] { "txt2audio", "audio2audio", "video2audio" },
                QuickPicks = new[]
                {
                    "model_mirelo-sfx-2-0-text-to-audio", "model_elevenlabs-sound-effects-v2", "model_sonilo-v1-1-text-to-sound-effects",
                    "model_elevenlabs-music-v2-5", "model_google-gemini-3-8-flash-tts",
                },
                Placeholder = "Short metallic sword swing with a whoosh",
                Hint = "Sound effects, music and voice. Lands on the selected AudioSource or a new one.",
            },
            new OutputType
            {
                Id = Material, Title = "Material", Icon = "material", Folder = "Materials", Capabilities = new[] { "txt2img", "img2img" },
                QuickPicks = new[] { "model_patina-material", "model_patina", "model_patina-material-extract" },
                Family = m => m.Id.StartsWith("model_patina"),
                Placeholder = "Weathered copper plates with green patina",
                Hint = "Tileable PBR materials for your render pipeline, set on the selected renderers.",
            },
            new OutputType
            {
                Id = Skybox, Title = "Skybox", Icon = "skybox", Folder = "Skyboxes", Capabilities = new[] { "txt2img", "img2img" },
                QuickPicks = new[] { "model_scenario-skybox-gpt", "model_scenario-skybox-flux", "model_hunyuan-world-image-to-skybox" },
                Family = m => m.Id.Contains("skybox") && !m.Capabilities.Contains("img23d"),
                Placeholder = "Alien desert at golden hour, twin moons, soft haze",
                Hint = "360 panoramas, set as the scene's skybox.",
            },
            new OutputType
            {
                Id = All, Title = "All models", Icon = "grid", Folder = "Generated", Capabilities = new string[0], QuickPicks = new string[0],
                Placeholder = "Describe what you want", Hint = "Every model in the catalog. Results land by their kind.",
            },
        };

        public static OutputType Get(string id) => List.FirstOrDefault(t => t.Id == id) ?? List[0];

        /// <summary>Character animation models: text, video or library to motion.</summary>
        public static bool IsMotion(ModelInfo model) =>
            model != null && (model.Id.Contains("-to-motion") || model.Id == "model_meshy-animation" || model.Capabilities.Contains("video23d"));

        /// <summary>The output type a model naturally belongs to.</summary>
        public static string TypeFor(ModelInfo model)
        {
            if (model == null) return Image;
            if (model.Id.StartsWith("model_patina")) return Material;
            if (model.Id.Contains("skybox") && !model.Capabilities.Contains("img23d")) return Skybox;
            if (IsMotion(model)) return Motion;
            var caps = model.Capabilities;
            if (caps.Any(c => c.EndsWith("23d"))) return Model3D;
            if (caps.Any(c => c.EndsWith("2video"))) return Video;
            if (caps.Any(c => c.EndsWith("2audio"))) return Audio;
            if (caps.Any(c => c.EndsWith("2img") || c == "img2img")) return Image;
            return All;
        }

        /// <summary>The output type for a downloaded asset kind (image, video, audio, 3d).</summary>
        public static string TypeForKind(string kind)
        {
            switch (kind)
            {
                case "video": return Video;
                case "audio": return Audio;
                case "3d": return Model3D;
                case "image": return Image;
                default: return All;
            }
        }

        /// <summary>Values set on top of the schema defaults, per model.</summary>
        public static JObject Presets(string modelId)
        {
            switch (modelId)
            {
                case "model_rodin-hyper3d-v2-5-text-to-3d":
                case "model_rodin-hyper3d-v2-5":
                    return new JObject { ["material"] = "PBR" };
                case "model_hunyuan-3d-pro-3-1-i23d":
                    return new JObject { ["enablePbr"] = true };
                // Same price; Meshy's full detail is about a million triangles, heavy for a game engine.
                case "model_meshy-7-1-txt23d":
                case "model_meshy-7-1-img23d":
                case "model_meshy-7-1-multi-image-to-3d":
                    return new JObject { ["shouldRemesh"] = true, ["targetPolycount"] = 30000 };
                case "model_sonilo-v1-1-text-to-sound-effects":
                    return new JObject { ["audioFormat"] = "wav", ["duration"] = 2 };
                case "model_scenario-skybox-flux":
                    return new JObject { ["numOutputs"] = 1 };
                // Rigged results: keep the rigged mesh, not only the animation.
                case "model_tripo-rigging-v1":
                case "model_tripo-rigging-v2-5":
                    return new JObject { ["includeRiggedModel"] = true };
                // Game parts, not 3D-print templates with joints.
                case "model_hitem-3d-split":
                    return new JObject { ["modelName"] = "general", ["joint"] = "none" };
                case "model_cartwheel-text-to-motion":
                case "model_cartwheel-video-to-motion":
                    return new JObject { ["outputFormat"] = "glb", ["frameRate"] = 30 };
                default:
                    return new JObject();
            }
        }

        /// <summary>An input a model needs although its schema marks it optional: one of Fields, unless the Unless switch is off.</summary>
        public sealed class Need
        {
            public string[] Fields;
            public string Unless = "";
            public string Message;
        }

        /// <summary>Inputs Scenario refuses to price without, per model (checked against the live API).</summary>
        public static Need[] Needs(string modelId)
        {
            switch (modelId)
            {
                // Cartwheel animates a character: a 3D model, or an image it first turns into one (dearer).
                case "model_cartwheel-text-to-motion":
                case "model_cartwheel-video-to-motion":
                    return new[] { new Need { Fields = new[] { "characterModel", "characterFile" }, Message = "Add a character: a 3D model, or an image of one." } };
                case "model_yvo3d-retexture":
                    return new[] { new Need { Fields = new[] { "referenceImage" }, Unless = "retexture", Message = "Add a reference image, or turn off Retexture." } };
                default:
                    return new Need[0];
            }
        }

        /// <summary>Field values Unity can import (it has no FLAC or AAC importer).</summary>
        public static readonly Dictionary<string, JToken[]> Restrict = new Dictionary<string, JToken[]>
        {
            { "audioFormat", new JToken[] { "wav", "mp3", "ogg" } },
            { "outputFormat", new JToken[] { "mp4", "png", "jpg", "jpeg", "webp", "wav", "mp3", "ogg", "glb" } },
        };

        /// <summary>
        /// Typed steps per asset kind. Defaults and alternatives checked against the live
        /// catalog and Scenario's recommendations (quality first) on 2026-10-18.
        /// </summary>
        public static readonly List<AssetStep> Steps = new List<AssetStep>
        {
            new AssetStep { Id = "edit", Title = "Edit with a prompt", Kind = "image", OutputType = Image, Capability = "img2img", DefaultModel = "model_openai-gpt-image-2-5-sunburst" },
            new AssetStep { Id = "remove-bg", Title = "Remove background", Kind = "image", OutputType = Image, Capability = "img2img", DefaultModel = "model_birefnet-background-removal" },
            new AssetStep { Id = "upscale", Title = "Upscale", Kind = "image", OutputType = Image, Capability = "img2img", DefaultModel = "model_clarity-pro-upscaler" },
            new AssetStep
            {
                Id = "to-3d", Title = "Make a 3D model", Kind = "image", OutputType = Model3D, Capability = "img23d", DefaultModel = "model_meshy-7-1-img23d",
                Alternatives = new[]
                {
                    "model_hunyuan-3d-pro-3-1-i23d", "model_tripo-p2-image-to-3d", "model_rodin-hyper3d-v2-5", "model_hitem-3d-3-0", "model_trellis-2",
                    "model_scenario-claude-opus-5-5-3d", "model_scenario-gpt6-astra-3d", "model_scenario-voxels",
                },
                Hint = "A textured model from this image. Add more views of the same object for a closer match.",
            },
            new AssetStep
            {
                Id = "to-splat", Title = "Make a Gaussian splat", Kind = "image", OutputType = Model3D, Capability = "img23d", DefaultModel = "model_vast-ai-triposplat",
                Alternatives = new[] { "model_hunyuan-world-image-to-splat", "model_worldlabs-marble-1-1" },
                Hint = "An object (TripoSplat) or a whole place (HY World, Marble) as a splat. Unity needs a Gaussian splat renderer to show it.",
            },
            new AssetStep { Id = "animate", Title = "Animate to video", Kind = "image", OutputType = Video, Capability = "img2video", DefaultModel = "model_minimax-h3" },
            new AssetStep { Id = "to-material", Title = "Make a PBR material", Kind = "image", OutputType = Material, Capability = "img2img", DefaultModel = "model_patina" },
            new AssetStep { Id = "to-skybox", Title = "Make a skybox", Kind = "image", OutputType = Skybox, Capability = "img2img", DefaultModel = "model_hunyuan-world-image-to-skybox" },
            new AssetStep { Id = "vectorize", Title = "Vectorize", Kind = "image", OutputType = Image, Capability = "img2img", DefaultModel = "model_sc-vectorizer" },
            new AssetStep { Id = "pixelate", Title = "Pixelate", Kind = "image", OutputType = Image, Capability = "img2img", DefaultModel = "model_sc-pixelate" },

            // 3D: texture
            new AssetStep
            {
                Id = "retexture", Title = "Retexture", Group = "Texture", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_meshy-7-retexture",
                Alternatives = new[] { "model_tripo-v3-0-texturing", "model_tencent-texture-edit", "model_trellis-2-retexture", "model_yvo3d-retexture" },
                Hint = "New PBR textures from a prompt or a style image. The shape stays.",
            },
            new AssetStep
            {
                Id = "uv", Title = "UV unwrap", Group = "Texture", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_tripo-smart-uv",
                Alternatives = new[] { "model_meshy-uv-unwrap", "model_tencent-uv-unwrapping" },
                Hint = "A clean UV layout with the textures baked onto it. Remesh dense models first.",
            },
            // 3D: shape
            new AssetStep
            {
                Id = "edit-3d", Title = "Edit with a prompt", Group = "Shape", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_scenario-claude-opus-5-5-3d",
                Alternatives = new[] { "model_scenario-gpt6-astra-3d" },
                Hint = "Describe the change: add or remove a part, change a material, make LODs. Comes back in the same format.",
            },
            new AssetStep
            {
                Id = "retopo", Title = "Retopology", Group = "Shape", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_tripo-retopology-v3",
                Alternatives = new[] { "model_tencent-smarttopology", "model_tripo-retopology" },
                Hint = "Clean low-poly topology, quads or triangles, with the textures baked on.",
            },
            new AssetStep
            {
                Id = "remesh", Title = "Remesh to a polycount", Group = "Shape", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_meshy-remesh",
                Alternatives = new[] { "model_tencent-smarttopology" },
                Hint = "Rebuilds the mesh at a target polycount, in triangles or quads.",
            },
            new AssetStep
            {
                Id = "refine", Title = "Refine the shape from an image", Group = "Shape", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_ultrashape-1-0",
                Hint = "Turns a coarse mesh into a detailed one. Add an image of the object in Inputs.",
            },
            // 3D: rig and animate
            new AssetStep
            {
                // Uthana: Scenario's first pick, with a standard 52-bone (Mixamo-named) skeleton. Meshy is cheaper and adds walk and run clips.
                Id = "rig", Title = "Rig a character", Group = "Rig and animate", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_uthana-character-rigging",
                Alternatives = new[] { "model_meshy-rigging", "model_cartwheel-character-rigging", "model_tripo-rigging-v1" },
                Hint = "A skeleton and skin weights for a humanoid. Works best on an A-pose or T-pose character. Meshy also adds walk and run clips.",
            },
            new AssetStep
            {
                Id = "rig-creature", Title = "Rig a creature", Group = "Rig and animate", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_tripo-rigging-v2-5",
                Hint = "Four legs, six or eight legs, wings, a snake or a fish body. Can add a walk cycle.",
            },
            new AssetStep
            {
                Id = "animate-3d", Title = "Animate from the motion library", Group = "Rig and animate", Kind = "3d", OutputType = Motion, Capability = "3d23d", DefaultModel = "model_meshy-animation",
                Alternatives = new[] { "model_tripo-rigging-v1" },
                Hint = "Pick one of Meshy's 656 motions: walks, runs, fights, dances. The character is rigged first.",
            },
            new AssetStep
            {
                Id = "motion", Title = "Animate from a prompt", Group = "Rig and animate", Kind = "3d", OutputType = Motion, Capability = "3d23d", DefaultModel = "model_cartwheel-text-to-motion",
                Alternatives = new[] { "model_uthana-text-to-motion-3.0", "model_scenario-gpt6-astra-3d" },
                Hint = "Describe the motion. The character is rigged automatically.",
            },
            new AssetStep
            {
                Id = "motion-video", Title = "Animate from a video", Group = "Rig and animate", Kind = "3d", OutputType = Motion, Capability = "video23d", DefaultModel = "model_cartwheel-video-to-motion",
                Alternatives = new[] { "model_uthana-video-to-motion-2.1" },
                Hint = "Motion capture from a video of a person: add the video in Inputs.",
            },
            // 3D: parts and style
            new AssetStep
            {
                Id = "parts", Title = "Split into parts", Group = "Parts and style", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_tripo-segmentation-v2",
                Alternatives = new[] { "model_hunyuan-3d-part", "model_hitem-3d-split", "model_rodin-hyper3d-bang", "model_tripo-segmentation-v1" },
                Hint = "Separate, editable parts. Rodin Bang also repaints each part and needs an image.",
            },
            new AssetStep
            {
                Id = "stylize", Title = "Stylize: LEGO, voxel, Voronoi", Group = "Parts and style", Kind = "3d", OutputType = Model3D, Capability = "3d23d", DefaultModel = "model_tripo-stylization",
                Alternatives = new[] { "model_hitem-3d-multicolor" },
                Hint = "The same object rebuilt in a style. Hitem3D Multicolor flattens it to a few colors.",
            },

            new AssetStep { Id = "upscale-video", Title = "Upscale", Kind = "video", OutputType = Video, Capability = "video2video", DefaultModel = "model_kandinsky-6-vsr-video-upscale" },
            new AssetStep { Id = "add-sound", Title = "Add sound", Kind = "video", OutputType = Audio, Capability = "video2audio", DefaultModel = "model_mirelo-sfx-2-0-video-to-audio" },
            new AssetStep
            {
                Id = "restyle", Title = "Restyle", Kind = "video", OutputType = Video, Capability = "video2video", DefaultModel = "model_bytedance-seedance-2-5",
                Alternatives = new[] { "model_minimax-h3-max-3d-to-video" },
                Hint = "MiniMax H3 Max 3D to Video turns a grey render or previs of your scene into a finished shot.",
            },
            new AssetStep
            {
                Id = "video-motion", Title = "Animate a character from this video", Kind = "video", OutputType = Motion, Capability = "video23d", DefaultModel = "model_cartwheel-video-to-motion",
                Alternatives = new[] { "model_uthana-video-to-motion-2.1" },
                Hint = "Motion capture of the person in the video. Add your character in Inputs, or leave it empty for a default one.",
            },
            new AssetStep { Id = "voice-change", Title = "Change the voice", Kind = "audio", OutputType = Audio, Capability = "audio2audio", DefaultModel = "model_elevenlabs-voice-changer" },
            new AssetStep { Id = "isolate", Title = "Isolate the voice", Kind = "audio", OutputType = Audio, Capability = "audio2audio", DefaultModel = "model_elevenlabs-voice-isolator" },
        };

        public static IEnumerable<AssetStep> StepsFor(string kind) => Steps.Where(s => s.Kind == kind);

        public static AssetStep Step(string id, string kind = null) => Steps.FirstOrDefault(s => s.Id == id && (kind == null || s.Kind == kind));
    }
}
