using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ScenarioDev
{
    /// <summary>
    /// Dev only (not in the package): renders an EditorWindow into a PNG through the
    /// internal GUIView.GrabPixels, so screenshots work without reading the screen.
    /// </summary>
    public static class WindowShots
    {
        static readonly FieldInfo ParentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

        public static string Capture(EditorWindow window, string path)
        {
            var parent = ParentField?.GetValue(window);
            if (parent == null) return "no host view";
            var grab = parent.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "GrabPixels" && m.GetParameters().Length == 2);
            if (grab == null) return "no GrabPixels on " + parent.GetType().Name;
            var size = window.position.size;
            var scale = EditorGUIUtility.pixelsPerPoint;
            var w = Mathf.RoundToInt(size.x * scale);
            var h = Mathf.RoundToInt(size.y * scale);
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            try
            {
                typeof(EditorWindow).GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(window, null);
                grab.Invoke(parent, new object[] { rt, new Rect(0, 0, w, h) });
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                RenderTexture.active = previous;
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    var pixels = tex.GetPixels32();
                    var flipped = new Color32[pixels.Length];
                    for (var y = 0; y < h; y++) Array.Copy(pixels, y * w, flipped, (h - 1 - y) * w, w);
                    tex.SetPixels32(flipped);
                }
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                return "";
            }
            catch (Exception e)
            {
                return (e.InnerException ?? e).Message;
            }
            finally
            {
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
