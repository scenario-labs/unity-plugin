using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>File naming, type sniffing and output roles for downloaded results.</summary>
    public static class Files
    {
        public const string DefaultRoot = "Assets/Scenario";
        public const string Downloads = "Library/Scenario/downloads";

        /// <summary>
        /// Scenario asset metadata.type to the role an importer uses. PATINA labels its
        /// roughness map "texture-smoothness": the pixels are roughness (checked on
        /// 2026-10-04 in the Godot plugin), so it goes to the roughness slot.
        /// </summary>
        public static string RoleFor(JObject asset)
        {
            var type = Json.Str(asset?["metadata"], "type");
            switch (type)
            {
                case "texture-albedo":
                case "texture-basecolor":
                    return "albedo";
                case "texture-normal":
                    return "normal";
                case "texture-smoothness":
                case "texture-roughness":
                    return "roughness";
                case "texture-metallic":
                case "texture-metalness":
                    return "metallic";
                case "texture-height":
                    return "height";
                case "texture-ao":
                case "texture-occlusion":
                    return "ao";
                case "inference-txt2img-texture":
                    return "base";
                case "skybox-base-360":
                    return "panorama";
                default:
                    return "main";
            }
        }

        static string Mime(JObject asset)
        {
            var mime = Json.Str(asset, "mimeType");
            if (mime.Length == 0) mime = Json.Str(asset?["metadata"], "mimeType");
            return mime.ToLowerInvariant();
        }

        /// <summary>
        /// Copies a 3D result carries next to its model: OBJ and MTL versions and the PBR
        /// maps (metadata.type "3d-texture-*"). The GLB already holds them, so they are not imported.
        /// </summary>
        public static bool IsModelCompanion(JObject asset)
        {
            var type = Json.Str(asset?["metadata"], "type");
            var mime = Mime(asset);
            return type.StartsWith("3d-texture") || mime == "model/obj" || mime == "model/mtl";
        }

        /// <summary>A 3D property Scenario measured (boneCount, materialCount...), from properties or metadata.</summary>
        public static JToken ModelProperty(JObject asset, string name) => asset?["properties"]?[name] ?? asset?["metadata"]?[name];

        public static int BoneCount(JObject asset) => (int)Json.ToDouble(ModelProperty(asset, "boneCount"));

        /// <summary>
        /// A skeleton preview (Meshy Rigging returns its walk and run on a bare armature
        /// too): bones, no material, no UVs. The textured files carry the same clips.
        /// </summary>
        public static bool IsSkeletonOnly(JObject asset) =>
            Json.Truthy(ModelProperty(asset, "hasSkeleton")) && ModelProperty(asset, "materialCount") != null
            && Json.ToDouble(ModelProperty(asset, "materialCount")) == 0 && !Json.Truthy(ModelProperty(asset, "hasUVs"));

        /// <summary>Gaussian splats (.spz, .ply from the splat models): Unity has no importer for them.</summary>
        public static bool IsSplat(JObject asset)
        {
            var mime = Mime(asset);
            var type = Json.Str(asset?["metadata"], "type");
            return mime == "model/spz" || mime.Contains("splat") || type.Contains("splat") || (mime.Contains("ply") && type.Contains("2splat"));
        }

        /// <summary>The asset kind Scenario reports: image, video, audio, 3d, document, json...</summary>
        public static string KindOf(JObject asset)
        {
            var kind = Json.Str(asset, "kind");
            if (kind.Length == 0) kind = Json.Str(asset?["metadata"], "kind");
            if (kind.Length > 0) return kind;
            var mime = Json.Str(asset, "mimeType");
            if (mime.Length == 0) mime = Json.Str(asset?["metadata"], "mimeType");
            return KindForMime(mime);
        }

        public static string KindForMime(string mime)
        {
            mime = (mime ?? "").ToLowerInvariant();
            if (mime.StartsWith("image/")) return "image";
            if (mime.StartsWith("video/")) return "video";
            if (mime.StartsWith("audio/")) return "audio";
            if (mime.StartsWith("model/")) return "3d";
            if (mime.StartsWith("text/") || mime == "application/json") return "text";
            return "";
        }

        /// <summary>Lowercase words joined by dashes, at most maxLength characters.</summary>
        public static string Slug(string text, int maxLength = 40)
        {
            var output = new StringBuilder();
            var dash = false;
            foreach (var c in (text ?? "").ToLowerInvariant())
            {
                var keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (keep)
                {
                    if (dash && output.Length > 0) output.Append('-');
                    output.Append(c);
                    dash = false;
                }
                else
                {
                    dash = true;
                }
                if (output.Length > maxLength + 1) break;
            }
            var slug = output.ToString();
            if (slug.Length > maxLength)
            {
                // Cut at a word boundary when one is close enough, else hard.
                var cut = slug.LastIndexOf('-', maxLength);
                slug = cut >= maxLength / 2 ? slug.Substring(0, cut) : slug.Substring(0, maxLength);
            }
            slug = slug.TrimEnd('-');
            return slug.Length > 0 ? slug : "scenario";
        }

        /// <summary>Object name from the prompt: up to three meaningful words in Title Case.</summary>
        public static string ObjectName(string prompt, string fallback = "Scenario Asset")
        {
            var words = new System.Collections.Generic.List<string>();
            foreach (var word in Slug(prompt, 80).Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word == "a" || word == "an" || word == "the" || word == "of" || word == "with" || word == "and" || word == "in" || word == "on" || word == "scenario")
                    continue;
                words.Add(char.ToUpperInvariant(word[0]) + word.Substring(1));
                if (words.Count == 3) break;
            }
            return words.Count > 0 ? string.Join(" ", words) : fallback;
        }

        /// <summary>"&lt;folder&gt;/&lt;slug&gt;-&lt;asset tail&gt;[-&lt;role&gt;]" without extension.</summary>
        public static string BasePath(string folder, string prompt, string assetId, string role)
        {
            var id = (assetId ?? "").StartsWith("asset_") ? assetId.Substring(6) : assetId ?? "";
            var tail = id.Length > 6 ? id.Substring(id.Length - 6) : id;
            var name = $"{Slug(prompt)}-{tail.ToLowerInvariant()}";
            if (!string.IsNullOrEmpty(role) && role != "main") name += "-" + role;
            return $"{folder.TrimEnd('/')}/{name}";
        }

        /// <summary>The dated output folder for a type: Assets/Scenario/Image/2026-10-18.</summary>
        public static string OutputFolder(string root, string typeFolder, DateTime date, bool dated = true)
        {
            var folder = $"{(string.IsNullOrEmpty(root) ? DefaultRoot : root.TrimEnd('/'))}/{typeFolder}";
            return dated ? $"{folder}/{date:yyyy-MM-dd}" : folder;
        }

        /// <summary>File extension from the first bytes, or "" when unknown.</summary>
        public static string SniffExtension(byte[] head)
        {
            if (head == null) return "";
            bool Ascii(int start, string text)
            {
                if (head.Length < start + text.Length) return false;
                for (var i = 0; i < text.Length; i++)
                    if (head[start + i] != (byte)text[i]) return false;
                return true;
            }
            if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return "png";
            if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
            if (Ascii(0, "RIFF") && Ascii(8, "WEBP")) return "webp";
            if (Ascii(0, "RIFF") && Ascii(8, "WAVE")) return "wav";
            if (Ascii(0, "glTF")) return "glb";
            if (Ascii(0, "OggS")) return "ogg";
            if (Ascii(0, "ID3")) return "mp3";
            if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "mp3";
            if (Ascii(0, "fLaC")) return "flac";
            if (head.Length >= 4 && head[0] == 0x76 && head[1] == 0x2F && head[2] == 0x31 && head[3] == 0x01) return "exr";
            if (Ascii(0, "#?RADIANCE") || Ascii(0, "#?RGBE")) return "hdr";
            if (Ascii(4, "ftyp"))
            {
                if (Ascii(8, "qt  ")) return "mov";
                if (Ascii(8, "M4A ")) return "m4a";
                return "mp4";
            }
            if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return "webm";
            if (Ascii(0, "Kaydara FBX Binary")) return "fbx";
            if (Ascii(0, "PK")) return "zip";
            if (Ascii(0, "ply\n") || Ascii(0, "ply\r")) return "ply";
            if (Ascii(0, "<svg") || Ascii(0, "<?xml")) return "svg";
            if (head.Length >= 1 && head[0] == 0x7B) return "json";
            return "";
        }

        public static string SniffFile(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var head = new byte[32];
                    var read = stream.Read(head, 0, head.Length);
                    Array.Resize(ref head, read);
                    var ext = SniffExtension(head);
                    // A JSON-looking glTF is "gltf"; plain JSON from an LLM stays "json".
                    if (ext == "json" && read > 0)
                    {
                        stream.Position = 0;
                        var buffer = new byte[Math.Min(4096, (int)stream.Length)];
                        stream.Read(buffer, 0, buffer.Length);
                        if (Encoding.UTF8.GetString(buffer).Contains("\"asset\"") && Encoding.UTF8.GetString(buffer).Contains("\"version\"")) return "gltf";
                    }
                    return ext;
                }
            }
            catch (IOException)
            {
                return "";
            }
        }

        /// <summary>Extension from a MIME type, the fallback when the bytes say nothing.</summary>
        public static string ExtensionForMime(string mime)
        {
            switch ((mime ?? "").ToLowerInvariant().Split(';')[0].Trim())
            {
                case "image/png": return "png";
                case "image/jpeg":
                case "image/jpg": return "jpg";
                case "image/webp": return "webp";
                case "image/svg+xml": return "svg";
                case "image/vnd.radiance": return "hdr";
                case "image/x-exr": return "exr";
                case "model/gltf-binary": return "glb";
                case "model/gltf+json": return "gltf";
                case "model/x-fbx": return "fbx";
                case "model/obj": return "obj";
                case "model/spz": return "spz";
                case "model/ply":
                case "application/ply": return "ply";
                case "model/x-3d-vox": return "vox";
                case "audio/mpeg":
                case "audio/mp3": return "mp3";
                case "audio/wav":
                case "audio/x-wav":
                case "audio/wave": return "wav";
                case "audio/ogg": return "ogg";
                case "audio/flac": return "flac";
                case "video/mp4": return "mp4";
                case "video/quicktime": return "mov";
                case "video/webm": return "webm";
                case "text/plain": return "txt";
                case "application/json": return "json";
            }
            return "";
        }

        public sealed class UploadType
        {
            public string Mime;
            public string Kind;
        }

        /// <summary>MIME type and Scenario asset kind for a file to upload as a reference, or null.</summary>
        public static UploadType UploadTypeFor(string path)
        {
            switch (Path.GetExtension(path ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "png": return new UploadType { Mime = "image/png", Kind = "image" };
                case "jpg":
                case "jpeg": return new UploadType { Mime = "image/jpeg", Kind = "image" };
                case "webp": return new UploadType { Mime = "image/webp", Kind = "image" };
                case "glb": return new UploadType { Mime = "model/gltf-binary", Kind = "3d" };
                case "fbx": return new UploadType { Mime = "model/x-fbx", Kind = "3d" };
                case "obj": return new UploadType { Mime = "model/obj", Kind = "3d" };
                case "wav": return new UploadType { Mime = "audio/wav", Kind = "audio" };
                case "mp3": return new UploadType { Mime = "audio/mpeg", Kind = "audio" };
                case "ogg": return new UploadType { Mime = "audio/ogg", Kind = "audio" };
                case "mp4": return new UploadType { Mime = "video/mp4", Kind = "video" };
                case "mov": return new UploadType { Mime = "video/quicktime", Kind = "video" };
                case "webm": return new UploadType { Mime = "video/webm", Kind = "video" };
            }
            return null;
        }

        /// <summary>Only Scenario's CDN hosts are downloaded from, without credentials.</summary>
        public static bool IsScenarioCdn(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "cdn.cloud.scenario.com" || host == "cdn.scenario.com" || host.EndsWith(".cdn.scenario.com") || host.EndsWith(".cloud.scenario.com");
        }
    }
}
