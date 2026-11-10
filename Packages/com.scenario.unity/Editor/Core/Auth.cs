using System;
using System.Text;
using System.Threading.Tasks;

namespace Scenario.Editor.Core
{
    public enum AuthMethod
    {
        None,
        OAuth,
        ApiKey,
        Environment,
    }

    /// <summary>Supplies the Authorization header for Scenario MCP calls.</summary>
    public interface IAuthProvider
    {
        AuthMethod Method { get; }
        bool HasCredentials { get; }
        /// <summary>OAuth callers must send team_id and project_id; an API key is scoped by the key.</summary>
        bool NeedsScope { get; }
        Task<string> AuthorizationHeaderAsync();
        /// <summary>One refresh after a 401. True when a new credential is ready.</summary>
        Task<bool> RefreshAsync();
        /// <summary>A short label for the account ("you@studio.com", "API key ab12...").</summary>
        string Label { get; }
    }

    public sealed class NoAuth : IAuthProvider
    {
        public AuthMethod Method => AuthMethod.None;
        public bool HasCredentials => false;
        public bool NeedsScope => false;
        public Task<string> AuthorizationHeaderAsync() => Task.FromResult("");
        public Task<bool> RefreshAsync() => Task.FromResult(false);
        public string Label => "";
    }

    /// <summary>Basic auth with a project API key and secret.</summary>
    public sealed class ApiKeyAuth : IAuthProvider
    {
        readonly string _header;

        public ApiKeyAuth(string key, string secret, bool fromEnvironment = false)
        {
            Key = (key ?? "").Trim();
            var cleanSecret = (secret ?? "").Trim();
            Method = fromEnvironment ? AuthMethod.Environment : AuthMethod.ApiKey;
            HasCredentials = Key.Length > 0 && cleanSecret.Length > 0;
            _header = HasCredentials ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Key + ":" + cleanSecret)) : "";
        }

        public string Key { get; }
        public AuthMethod Method { get; }
        public bool HasCredentials { get; }
        public bool NeedsScope => false;
        public Task<string> AuthorizationHeaderAsync() => Task.FromResult(_header);
        public Task<bool> RefreshAsync() => Task.FromResult(false);
        public string Label => Key.Length > 6 ? $"API key {Key.Substring(0, 6)}…" : "API key";

        /// <summary>Accepts "key:secret", "key secret", or two lines, as copied from the web app.</summary>
        public static bool TrySplitPasted(string pasted, out string key, out string secret)
        {
            key = secret = "";
            var text = (pasted ?? "").Trim();
            if (text.Length == 0) return false;
            var separators = new[] { ':', '\n', '\r', ' ', '\t', ',' };
            var parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            key = parts[0].Trim().Trim('"');
            secret = parts[1].Trim().Trim('"');
            return key.Length > 0 && secret.Length > 0;
        }
    }
}
