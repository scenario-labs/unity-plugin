using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
#if SCENARIO_URP
using UnityEngine.Rendering.Universal;
#endif
using Object = UnityEngine.Object;

namespace Scenario.Editor
{
    /// <summary>
    /// Sets a scene's light and mood in one step: the sun's angle, color and strength, fog and ambient light,
    /// and in URP a global post-processing volume (tone mapping, exposure, contrast, saturation, warmth, bloom,
    /// vignette) rendered by the scene's cameras. A preset gives the starting look; any value can be set alone.
    /// </summary>
    public static class Atmosphere
    {
        public const string VolumeName = "Scenario Atmosphere";

        public sealed class Look
        {
            public float SunElevation, SunIntensity;
            public Color SunColor;
            public float FogDensity;
            public Color FogColor;
            /// <summary>Ambient light from above, the horizon and below, when the scene has no skybox.</summary>
            public Color Sky, Horizon, Ground;
            /// <summary>Strength of the ambient light from the skybox.</summary>
            public float Ambient = 1f;
            public float Exposure, Contrast, Saturation, Temperature, Bloom, Vignette;
        }

        public static readonly Dictionary<string, Look> Presets = new Dictionary<string, Look>
        {
            ["golden_hour"] = new Look
            {
                SunElevation = 12, SunIntensity = 1.6f, SunColor = new Color(1f, 0.78f, 0.55f),
                FogDensity = 0.004f, FogColor = new Color(0.93f, 0.78f, 0.62f),
                Sky = new Color(0.62f, 0.66f, 0.78f), Horizon = new Color(0.86f, 0.7f, 0.55f), Ground = new Color(0.3f, 0.24f, 0.18f),
                Exposure = 0.2f, Contrast = 12, Saturation = 8, Temperature = 15, Bloom = 0.4f, Vignette = 0.25f,
            },
            ["midday"] = new Look
            {
                SunElevation = 60, SunIntensity = 1.3f, SunColor = new Color(1f, 0.97f, 0.92f),
                FogDensity = 0.0015f, FogColor = new Color(0.75f, 0.82f, 0.9f),
                Sky = new Color(0.55f, 0.68f, 0.9f), Horizon = new Color(0.72f, 0.78f, 0.85f), Ground = new Color(0.32f, 0.3f, 0.26f),
                Exposure = 0f, Contrast = 8, Saturation = 5, Temperature = 0, Bloom = 0.2f, Vignette = 0.15f,
            },
            ["overcast"] = new Look
            {
                SunElevation = 45, SunIntensity = 0.6f, SunColor = new Color(0.85f, 0.88f, 0.92f),
                FogDensity = 0.008f, FogColor = new Color(0.7f, 0.73f, 0.76f),
                Sky = new Color(0.62f, 0.65f, 0.7f), Horizon = new Color(0.6f, 0.62f, 0.64f), Ground = new Color(0.28f, 0.28f, 0.27f),
                Exposure = 0.1f, Contrast = -5, Saturation = -15, Temperature = -5, Bloom = 0.1f, Vignette = 0.2f,
            },
            ["misty_morning"] = new Look
            {
                SunElevation = 8, SunIntensity = 0.9f, SunColor = new Color(1f, 0.85f, 0.72f),
                FogDensity = 0.02f, FogColor = new Color(0.82f, 0.84f, 0.86f),
                Sky = new Color(0.66f, 0.7f, 0.76f), Horizon = new Color(0.78f, 0.76f, 0.72f), Ground = new Color(0.26f, 0.26f, 0.24f), Ambient = 0.85f,
                Exposure = 0.15f, Contrast = 5, Saturation = -10, Temperature = -8, Bloom = 0.5f, Vignette = 0.25f,
            },
            ["sunset"] = new Look
            {
                SunElevation = 3, SunIntensity = 1.2f, SunColor = new Color(1f, 0.55f, 0.32f),
                FogDensity = 0.006f, FogColor = new Color(0.9f, 0.55f, 0.4f),
                Sky = new Color(0.45f, 0.42f, 0.6f), Horizon = new Color(0.9f, 0.55f, 0.4f), Ground = new Color(0.22f, 0.15f, 0.12f), Ambient = 0.7f,
                Exposure = 0.1f, Contrast = 15, Saturation = 12, Temperature = 25, Bloom = 0.6f, Vignette = 0.3f,
            },
            ["night"] = new Look
            {
                SunElevation = 35, SunIntensity = 0.25f, SunColor = new Color(0.55f, 0.65f, 1f),
                FogDensity = 0.01f, FogColor = new Color(0.08f, 0.1f, 0.16f),
                Sky = new Color(0.08f, 0.1f, 0.18f), Horizon = new Color(0.06f, 0.08f, 0.14f), Ground = new Color(0.03f, 0.03f, 0.05f), Ambient = 0.3f,
                Exposure = 0.3f, Contrast = 10, Saturation = -20, Temperature = -20, Bloom = 0.8f, Vignette = 0.35f,
            },
        };

        public sealed class Spec
        {
            public string Preset = "";
            public float? SunElevation, SunAzimuth, SunIntensity, FogDensity;
            public Color? SunColor, FogColor, Ambient;
            public bool? Fog;
            public float? Exposure, Contrast, Saturation, Temperature, Bloom, Vignette;
            /// <summary>Post-processing in URP (default true).</summary>
            public bool Post = true;
        }

        public sealed class Result
        {
            public Light Sun;
            public GameObject Volume;
            public string Profile = "";
            public List<string> Notes = new List<string>();
        }

        public static Result Apply(Spec spec)
        {
            Look look = null;
            if (spec.Preset.Length > 0 && !Presets.TryGetValue(spec.Preset, out look))
                throw new ArgumentException($"Unknown preset {spec.Preset}: use {string.Join(", ", Presets.Keys)}.");
            var result = new Result();

            var sun = FindSun();
            if (sun == null)
            {
                var go = new GameObject("Sun");
                Undo.RegisterCreatedObjectUndo(go, "Atmosphere");
                sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
                go.transform.rotation = Quaternion.Euler(45, -30, 0);
            }
            Undo.RecordObject(sun, "Atmosphere");
            Undo.RecordObject(sun.transform, "Atmosphere");
            var angles = sun.transform.eulerAngles;
            var elevation = spec.SunElevation ?? look?.SunElevation;
            if (elevation.HasValue || spec.SunAzimuth.HasValue)
                sun.transform.rotation = Quaternion.Euler(Mathf.Clamp(elevation ?? angles.x, -90, 90), spec.SunAzimuth ?? angles.y, 0);
            var sunColor = spec.SunColor ?? look?.SunColor;
            if (sunColor.HasValue) sun.color = sunColor.Value;
            var sunIntensity = spec.SunIntensity ?? look?.SunIntensity;
            if (sunIntensity.HasValue) sun.intensity = Mathf.Max(0, sunIntensity.Value);
            if (sun.shadows == LightShadows.None) sun.shadows = LightShadows.Soft;

            var settings = RenderSettingsObject();
            if (settings != null) Undo.RecordObject(settings, "Atmosphere");
            RenderSettings.sun = sun;
            var fog = spec.Fog ?? (look != null ? look.FogDensity > 0 : (bool?)null) ?? (spec.FogDensity.HasValue || spec.FogColor.HasValue ? true : (bool?)null);
            if (fog.HasValue) RenderSettings.fog = fog.Value;
            if (RenderSettings.fog)
            {
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                var density = spec.FogDensity ?? look?.FogDensity;
                if (density.HasValue) RenderSettings.fogDensity = Mathf.Max(0, density.Value);
                var fogColor = spec.FogColor ?? look?.FogColor;
                if (fogColor.HasValue) RenderSettings.fogColor = fogColor.Value;
            }
            // Ambient light: from the skybox when there is one, so the scene is lit like its sky; else a three-color gradient.
            if (spec.Ambient.HasValue)
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = spec.Ambient.Value;
            }
            else if (look != null)
            {
                if (RenderSettings.skybox != null)
                {
                    RenderSettings.ambientMode = AmbientMode.Skybox;
                    RenderSettings.ambientIntensity = look.Ambient;
                    if (spec.Preset != "midday") result.Notes.Add("The skybox is kept: if its sky does not match this light, generate one that does and set it with skybox_set.");
                }
                else
                {
                    RenderSettings.ambientMode = AmbientMode.Trilight;
                    RenderSettings.ambientSkyColor = look.Sky;
                    RenderSettings.ambientEquatorColor = look.Horizon;
                    RenderSettings.ambientGroundColor = look.Ground;
                }
            }
            DynamicGI.UpdateEnvironment();

            if (spec.Post) Post(spec, look, result);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            result.Sun = sun;
            return result;
        }

        /// <summary>The scene's sun: the one Lighting uses, else its first directional light.</summary>
        static Light FindSun()
        {
            if (RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional) return RenderSettings.sun;
            return Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
                .Where(l => l.type == LightType.Directional && l.gameObject.scene.IsValid())
                .OrderByDescending(l => l.intensity)
                .FirstOrDefault();
        }

        /// <summary>The scene's RenderSettings object, so fog and ambient changes are undoable. Null when Unity hides it.</summary>
        static Object RenderSettingsObject()
        {
            var method = typeof(RenderSettings).GetMethod("GetRenderSettings", BindingFlags.NonPublic | BindingFlags.Static);
            return method?.Invoke(null, null) as Object;
        }

        static void Post(Spec spec, Look look, Result result)
        {
#if SCENARIO_URP
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
            {
                result.Notes.Add("Post-processing needs the Universal Render Pipeline as the active pipeline: sun, fog and ambient are set.");
                return;
            }
            var volume = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None).FirstOrDefault(v => v.name == VolumeName && v.gameObject.scene.IsValid());
            if (volume == null)
            {
                var go = new GameObject(VolumeName);
                Undo.RegisterCreatedObjectUndo(go, "Atmosphere");
                volume = go.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10;
            }
            Undo.RecordObject(volume, "Atmosphere");
            var profile = volume.sharedProfile;
            if (profile == null || !AssetDatabase.Contains(profile))
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                var folder = World.Root.TrimEnd('/') + "/Atmosphere";
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
                var scene = SceneManager.GetActiveScene().name;
                AssetDatabase.CreateAsset(profile, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{(scene.Length > 0 ? scene : "Scene")} Atmosphere.asset"));
                volume.sharedProfile = profile;
            }
            Undo.RecordObject(profile, "Atmosphere");

            T Part<T>() where T : VolumeComponent
            {
                if (!profile.TryGet<T>(out var part))
                {
                    part = profile.Add<T>();
                    part.name = typeof(T).Name;
                    part.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(part, profile);
                }
                Undo.RecordObject(part, "Atmosphere");
                part.active = true;
                return part;
            }
            void Set(VolumeParameter<float> parameter, float? value)
            {
                if (value.HasValue) parameter.Override(value.Value);
            }

            Part<Tonemapping>().mode.Override(TonemappingMode.ACES);
            var adjust = Part<ColorAdjustments>();
            Set(adjust.postExposure, spec.Exposure ?? look?.Exposure);
            Set(adjust.contrast, spec.Contrast ?? look?.Contrast);
            Set(adjust.saturation, spec.Saturation ?? look?.Saturation);
            Set(Part<WhiteBalance>().temperature, spec.Temperature ?? look?.Temperature);
            var bloom = Part<Bloom>();
            Set(bloom.intensity, spec.Bloom ?? look?.Bloom);
            bloom.threshold.Override(0.9f);
            Set(Part<Vignette>().intensity, spec.Vignette ?? look?.Vignette);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            // A camera renders post-processing only when it is turned on for it.
            foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Where(c => c.gameObject.scene.IsValid()))
            {
                var data = camera.GetUniversalAdditionalCameraData();
                if (data.renderPostProcessing) continue;
                Undo.RecordObject(data, "Atmosphere");
                data.renderPostProcessing = true;
            }
            result.Volume = volume.gameObject;
            result.Profile = AssetDatabase.GetAssetPath(profile);
#else
            result.Notes.Add("Post-processing (exposure, contrast, bloom, vignette) needs the Universal Render Pipeline: sun, fog and ambient are set.");
#endif
        }
    }
}
