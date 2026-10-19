using System.IO;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>Turns an equirectangular panorama into the scene's skybox.</summary>
    public static class SkyboxBuilder
    {
        /// <summary>Configures the texture import for a panorama.</summary>
        public static void PrepareTexture(string texturePath)
        {
            if (!(AssetImporter.GetAtPath(texturePath) is TextureImporter importer)) return;
            importer.textureType = TextureImporterType.Default;
            importer.wrapModeU = TextureWrapMode.Repeat;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = 8192;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            if (MaterialBuilder.Current == Pipeline.HighDefinition)
                importer.textureShape = TextureImporterShape.TextureCube;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Built-in and URP: a Skybox/Panoramic material set in Lighting. HDRP draws its
        /// sky from a Volume, so there the cubemap is prepared and the material is
        /// still created for reference; the message says what to do next.
        /// </summary>
        public static (Material material, string message) Apply(string texturePath, bool setAsSkybox)
        {
            PrepareTexture(texturePath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null) return (null, "The panorama could not be loaded.");
            var shader = Shader.Find("Skybox/Panoramic");
            if (shader == null) return (null, "This project has no Skybox/Panoramic shader.");
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(texturePath) + " Skybox" };
            material.SetTexture("_MainTex", texture);
            material.SetFloat("_Mapping", 1); // latitude-longitude layout
            material.SetFloat("_ImageType", 0); // 360 degrees
            var matPath = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(texturePath, null) + "-skybox.mat");
            AssetDatabase.CreateAsset(material, matPath);
            if (MaterialBuilder.Current == Pipeline.HighDefinition)
                return (material, "HDRP draws the sky from a Volume: set this cubemap in its HDRI Sky.");
            if (setAsSkybox)
            {
                var proxy = RenderSettingsProxy();
                if (proxy != null) Undo.RecordObject(proxy, "Set Scenario skybox");
                RenderSettings.skybox = material;
                DynamicGI.UpdateEnvironment();
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            }
            return (material, "");
        }

        /// <summary>The scene's RenderSettings object, so Undo can track the skybox change.</summary>
        static Object RenderSettingsProxy()
        {
            var method = typeof(RenderSettings).GetMethod("GetRenderSettings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return method?.Invoke(null, null) as Object;
        }
    }
}
