using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scenario.Editor
{
    public enum Pipeline
    {
        BuiltIn,
        Universal,
        HighDefinition,
        Other,
    }

    /// <summary>Builds a lit material for the active render pipeline from PBR maps keyed by role.</summary>
    public static class MaterialBuilder
    {
        public static Pipeline Current
        {
            get
            {
                var asset = GraphicsSettings.currentRenderPipeline;
                if (asset == null) return Pipeline.BuiltIn;
                var type = asset.GetType().Name;
                if (type.Contains("Universal")) return Pipeline.Universal;
                if (type.Contains("HDRenderPipeline")) return Pipeline.HighDefinition;
                return Pipeline.Other;
            }
        }

        public static Shader LitShader()
        {
            switch (Current)
            {
                case Pipeline.Universal: return Shader.Find("Universal Render Pipeline/Lit");
                case Pipeline.HighDefinition: return Shader.Find("HDRP/Lit");
                case Pipeline.BuiltIn: return Shader.Find("Standard");
                default:
                    return GraphicsSettings.currentRenderPipeline?.defaultMaterial?.shader ?? Shader.Find("Standard");
            }
        }

        /// <summary>A material showing one texture (image results applied to a renderer).</summary>
        public static Material Unlit(Texture texture, string path)
        {
            var shader = Current == Pipeline.Universal ? Shader.Find("Universal Render Pipeline/Unlit")
                : Current == Pipeline.HighDefinition ? Shader.Find("HDRP/Unlit") : Shader.Find("Unlit/Texture");
            var material = new Material(shader ?? LitShader()) { name = Path.GetFileNameWithoutExtension(path) };
            SetMain(material, texture);
            AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(path));
            return material;
        }

        public static void SetMain(Material material, Texture texture)
        {
            foreach (var name in new[] { "_BaseMap", "_BaseColorMap", "_UnlitColorMap", "_MainTex" })
                if (material.HasProperty(name)) material.SetTexture(name, texture);
            material.mainTexture = texture;
        }

        /// <summary>
        /// Creates a .mat next to the maps. maps: role -> project path (albedo, normal,
        /// roughness, metallic, height, ao). Metallic and roughness are packed into the
        /// pipeline's mask format (smoothness = 1 - roughness).
        /// </summary>
        public static Material Build(Dictionary<string, string> maps, string materialPath)
        {
            // Never write over another material or its packed map.
            materialPath = AssetDatabase.GenerateUniqueAssetPath(materialPath);
            var material = new Material(LitShader()) { name = Path.GetFileNameWithoutExtension(materialPath) };
            Texture Load(string role) => maps.TryGetValue(role, out var p) ? AssetDatabase.LoadAssetAtPath<Texture2D>(p) : null;
            var albedo = Load("albedo") ?? Load("base") ?? Load("main");
            if (albedo != null) SetMain(material, albedo);

            var normal = Load("normal");
            if (normal != null)
            {
                foreach (var name in new[] { "_BumpMap", "_NormalMap" })
                    if (material.HasProperty(name)) material.SetTexture(name, normal);
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }

            maps.TryGetValue("metallic", out var metallicPath);
            maps.TryGetValue("roughness", out var roughnessPath);
            maps.TryGetValue("ao", out var aoPath);
            var dir = Path.GetDirectoryName(materialPath)?.Replace('\\', '/') ?? "Assets";
            var stem = Path.GetFileNameWithoutExtension(materialPath);
            if (Current == Pipeline.HighDefinition)
            {
                if (metallicPath != null || roughnessPath != null || aoPath != null)
                {
                    var mask = PackMask(AssetDatabase.GenerateUniqueAssetPath($"{dir}/{stem}-mask.png"), metallicPath, aoPath, roughnessPath, hdrp: true);
                    if (mask != null)
                    {
                        material.SetTexture("_MaskMap", mask);
                        material.EnableKeyword("_MASKMAP");
                    }
                }
            }
            else
            {
                if (metallicPath != null || roughnessPath != null)
                {
                    var packed = PackMask(AssetDatabase.GenerateUniqueAssetPath($"{dir}/{stem}-metallic-smoothness.png"), metallicPath, null, roughnessPath, hdrp: false);
                    if (packed != null)
                    {
                        material.SetTexture("_MetallicGlossMap", packed);
                        material.EnableKeyword("_METALLICSPECGLOSSMAP");
                        material.EnableKeyword("_METALLICGLOSSMAP");
                        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 1f);
                        if (material.HasProperty("_GlossMapScale")) material.SetFloat("_GlossMapScale", 1f);
                    }
                }
                var ao = Load("ao");
                if (ao != null && material.HasProperty("_OcclusionMap"))
                {
                    material.SetTexture("_OcclusionMap", ao);
                    material.EnableKeyword("_OCCLUSIONMAP");
                }
            }
            var height = Load("height");
            if (height != null)
            {
                if (material.HasProperty("_ParallaxMap"))
                {
                    material.SetTexture("_ParallaxMap", height);
                    material.SetFloat("_Parallax", 0.01f);
                    material.EnableKeyword("_PARALLAXMAP");
                }
                else if (material.HasProperty("_HeightMap"))
                {
                    material.SetTexture("_HeightMap", height);
                }
            }
            AssetDatabase.CreateAsset(material, materialPath);
            return material;
        }

        /// <summary>
        /// URP/Built-in: R = metallic, A = smoothness. HDRP mask: R = metallic, G = AO,
        /// B = detail mask (white), A = smoothness. Written as a linear PNG.
        /// </summary>
        static Texture2D PackMask(string path, string metallicPath, string aoPath, string roughnessPath, bool hdrp)
        {
            var metallic = ReadPixels(metallicPath, out var w1, out var h1);
            var rough = ReadPixels(roughnessPath, out var w2, out var h2);
            var ao = ReadPixels(aoPath, out var w3, out var h3);
            var width = Mathf.Max(w1, Mathf.Max(w2, w3));
            var height = Mathf.Max(h1, Mathf.Max(h2, h3));
            if (width == 0 || height == 0) return null;
            var output = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                byte Sample(Color32[] source, int w, int h, byte fallback)
                {
                    if (source == null) return fallback;
                    var sx = x * w / width;
                    var sy = y * h / height;
                    return source[sy * w + sx].r;
                }
                var m = Sample(metallic, w1, h1, 0);
                var smooth = (byte)(255 - Sample(rough, w2, h2, 128));
                var occlusion = Sample(ao, w3, h3, 255);
                pixels[y * width + x] = hdrp ? new Color32(m, occlusion, 255, smooth) : new Color32(m, m, m, smooth);
            }
            output.SetPixels32(pixels);
            output.Apply();
            File.WriteAllBytes(path, output.EncodeToPNG());
            Object.DestroyImmediate(output);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.sRGBTexture = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Pixels from the file on disk (no need for a readable import).</summary>
        static Color32[] ReadPixels(string path, out int width, out int height)
        {
            width = height = 0;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path))) return null;
                width = texture.width;
                height = texture.height;
                return texture.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }
    }
}
