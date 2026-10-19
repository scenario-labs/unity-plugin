using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>
    /// Reads a Scenario MCP HTTP reply, plain JSON or a server-sent event stream,
    /// and returns the final JSON-RPC message that answers one request id.
    /// </summary>
    public static class Sse
    {
        /// <summary>
        /// ok(envelope) where envelope holds "result" or "error". A notification
        /// (requestId == null) with an empty body is ok(empty object).
        /// </summary>
        public static Result<JObject> Parse(string body, string contentType, object requestId)
        {
            var source = (body ?? "").TrimStart('﻿');
            if (source.Trim().Length == 0)
            {
                if (requestId == null) return Result<JObject>.Success(new JObject());
                return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario sent an empty reply.");
            }
            var trimmed = source.TrimStart();
            var isStream = (contentType ?? "").ToLowerInvariant().Contains("text/event-stream")
                || trimmed.StartsWith("data:") || trimmed.StartsWith("event:") || trimmed.StartsWith(":");
            var messages = new List<JToken>();
            if (isStream)
            {
                messages = StreamMessages(source);
            }
            else
            {
                var payload = Json.Parse(source);
                if (payload == null) return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario returned invalid JSON.");
                if (payload is JArray array) messages.AddRange(array);
                else messages.Add(payload);
            }
            if (requestId == null) return Result<JObject>.Success(new JObject());
            JObject found = null;
            foreach (var message in messages)
            {
                if (message is JObject obj && obj["id"] != null && SameId(obj["id"], requestId)
                    && (obj["result"] != null || obj["error"] != null))
                    found = obj; // the last matching message wins (progress first, result last)
            }
            if (found == null) return Result<JObject>.Fail(ErrorKind.Protocol, "Scenario did not return a complete reply for this request.");
            return Result<JObject>.Success(found);
        }

        static List<JToken> StreamMessages(string source)
        {
            var messages = new List<JToken>();
            var dataLines = new List<string>();
            var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            void Flush()
            {
                if (dataLines.Count == 0) return;
                var value = string.Join("\n", dataLines);
                if (value.Trim() != "[DONE]")
                {
                    var parsed = Json.Parse(value);
                    if (parsed != null) messages.Add(parsed); // a malformed progress event must not hide a later result
                }
                dataLines.Clear();
            }
            foreach (var line in lines)
            {
                if (line.Length == 0) Flush();
                else if (line.StartsWith("data:"))
                {
                    var data = line.Substring(5);
                    if (data.StartsWith(" ")) data = data.Substring(1);
                    dataLines.Add(data);
                }
            }
            Flush();
            return messages;
        }

        static bool SameId(JToken a, object b)
        {
            if ((a.Type == JTokenType.Integer || a.Type == JTokenType.Float) && (b is int || b is long || b is double))
                return (long)a.Value<double>() == System.Convert.ToInt64(b);
            return a.ToString() == b.ToString();
        }
    }
}
