using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>Small, forgiving JSON helpers over Newtonsoft's JToken.</summary>
    public static class Json
    {
        public static JToken Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using (var reader = new JsonTextReader(new System.IO.StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
                    return JToken.ReadFrom(reader);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static JObject ParseObject(string text) => Parse(text) as JObject;

        public static string Str(JToken token, string key, string fallback = "")
        {
            var value = token is JObject obj ? obj[key] : null;
            if (value == null || value.Type == JTokenType.Null) return fallback;
            if (value.Type == JTokenType.String) return (string)value;
            if (value.Type == JTokenType.Date && value is JValue date && date.Value is DateTime dateTime)
                return dateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            return value.ToString(Formatting.None);
        }

        public static double Num(JToken token, string key, double fallback = 0)
        {
            var value = token is JObject obj ? obj[key] : null;
            return ToDouble(value, fallback);
        }

        public static double ToDouble(JToken value, double fallback = 0)
        {
            if (value == null) return fallback;
            switch (value.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return value.Value<double>();
                case JTokenType.String:
                    return double.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
                case JTokenType.Boolean:
                    return value.Value<bool>() ? 1 : 0;
                default:
                    return fallback;
            }
        }

        /// <summary>Schema values come as bools, numbers or strings ("true").</summary>
        public static bool Truthy(JToken value)
        {
            if (value == null) return false;
            switch (value.Type)
            {
                case JTokenType.Boolean: return value.Value<bool>();
                case JTokenType.Integer:
                case JTokenType.Float: return Math.Abs(value.Value<double>()) > double.Epsilon;
                case JTokenType.String:
                    var s = ((string)value).Trim().ToLowerInvariant();
                    return s == "true" || s == "1" || s == "yes";
                case JTokenType.Object:
                    // Scenario's raw model inputs use {"always": true} for required.
                    return Truthy(value["always"]);
                default: return false;
            }
        }

        public static bool IsEmpty(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined) return true;
            if (value.Type == JTokenType.String) return ((string)value).Trim().Length == 0;
            if (value is JArray array) return array.Count == 0;
            if (value is JObject obj) return !obj.HasValues;
            return false;
        }

        /// <summary>Keys sorted at every level, whole floats written as integers.</summary>
        public static JToken Normalize(JToken value)
        {
            switch (value)
            {
                case null:
                    return JValue.CreateNull();
                case JObject obj:
                    var ordered = new JObject();
                    foreach (var prop in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                        ordered[prop.Name] = Normalize(prop.Value);
                    return ordered;
                case JArray array:
                    return new JArray(array.Select(Normalize));
                case JValue v when v.Type == JTokenType.Float:
                    var d = v.Value<double>();
                    if (!double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 9.0e15 && Math.Floor(d) == d)
                        return new JValue((long)d);
                    return new JValue(d);
                default:
                    return value.DeepClone();
            }
        }

        public static string Canonical(JToken value) => Normalize(value).ToString(Formatting.None);

        public static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>2048.0 as "2048", 0.6 as "0.6".</summary>
        public static string NumberText(double number)
        {
            if (Math.Floor(number) == number && Math.Abs(number) < 9.0e15)
                return ((long)number).ToString(CultureInfo.InvariantCulture);
            return number.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>"2026-10-04T09:10:52.353Z" to Unix seconds, or -1.</summary>
        public static double IsoToUnix(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                return date.ToUnixTimeMilliseconds() / 1000.0;
            return -1;
        }

        public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
