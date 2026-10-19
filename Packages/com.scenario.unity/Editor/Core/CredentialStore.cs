using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>An OS secret store: macOS Keychain, Windows Credential Manager, Linux Secret Service.</summary>
    public interface ISecretStore
    {
        string Name { get; }
        bool Available { get; }
        bool Write(string account, string secret);
        string Read(string account);
        void Delete(string account);
    }

    /// <summary>
    /// Where the Scenario sign-in lives. Never in the project folder: a project is
    /// usually in version control, and a committed secret leaks.
    ///
    /// A per-user file holds the non-secret part (method, API key id, account
    /// label). The secret (API secret or OAuth refresh token) goes to the OS
    /// secret store, or, where none answers, into that file with 0600 permissions.
    /// Environment variables SCENARIO_API_KEY / SCENARIO_API_SECRET win over both.
    /// </summary>
    public sealed class CredentialStore
    {
        public const string Service = "Scenario Unity Plugin";

        public readonly string ConfigPath;
        public ISecretStore Secrets;

        public CredentialStore(string configPath, ISecretStore secrets)
        {
            ConfigPath = configPath ?? "";
            Secrets = secrets;
        }

        /// <summary>The default per-user location and the platform's secret store.</summary>
        public static CredentialStore ForUser()
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return new CredentialStore(Path.Combine(root, "Scenario", "Unity", "account.json"), PlatformStore());
        }

        public static ISecretStore PlatformStore()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return new WindowsCredentialStore();
                case PlatformID.MacOSX:
                case PlatformID.Unix:
                    if (File.Exists(MacKeychain.Security)) return new MacKeychain();
                    return new LinuxSecretTool();
                default:
                    return null;
            }
        }

        public sealed class Saved
        {
            /// <summary>"oauth", "apikey" or "" when nothing is saved.</summary>
            public string Method = "";
            public string ApiKey = "";
            public string Secret = "";
            /// <summary>"environment", "keychain", "file"... where the secret was found.</summary>
            public string Source = "";
            public JObject Info = new JObject();
        }

        public static Saved FromEnvironment()
        {
            var key = (Environment.GetEnvironmentVariable("SCENARIO_API_KEY") ?? "").Trim();
            var secret = (Environment.GetEnvironmentVariable("SCENARIO_API_SECRET") ?? "").Trim();
            if (key.Length == 0 || secret.Length == 0) return null;
            return new Saved { Method = "apikey", ApiKey = key, Secret = secret, Source = "environment" };
        }

        public Saved Load()
        {
            var data = ReadConfig();
            var method = Json.Str(data, "method");
            var saved = new Saved { Method = method, ApiKey = Json.Str(data, "api_key"), Info = data["info"] as JObject ?? new JObject() };
            if (method.Length == 0) return saved;
            var account = AccountFor(method, saved.ApiKey);
            if (Json.Str(data, "secret_store") == "file")
            {
                saved.Secret = Json.Str(data, "secret");
                saved.Source = saved.Secret.Length > 0 ? "file" : "";
            }
            else if (Secrets != null && Secrets.Available)
            {
                saved.Secret = Decode(Secrets.Read(account));
                saved.Source = saved.Secret.Length > 0 ? Secrets.Name : "";
            }
            if (saved.Secret.Length == 0) saved.Method = "";
            return saved;
        }

        /// <summary>Saves an API key pair. Returns where the secret went, or "" on failure.</summary>
        public string SaveApiKey(string key, string secret, JObject info = null)
            => Save("apikey", (key ?? "").Trim(), (secret ?? "").Trim(), info);

        /// <summary>Saves the OAuth refresh token with the account label and client registration.</summary>
        public string SaveRefreshToken(string refreshToken, JObject info)
            => Save("oauth", "", refreshToken ?? "", info);

        /// <summary>Updates the non-secret details (account label, workspace) without touching the secret.</summary>
        public void UpdateInfo(JObject info)
        {
            var data = ReadConfig();
            if (Json.Str(data, "method").Length == 0) return;
            data["info"] = info ?? new JObject();
            WriteConfig(data);
        }

        string Save(string method, string key, string secret, JObject info)
        {
            if (secret.Length == 0) return "";
            Clear();
            var account = AccountFor(method, key);
            var data = new JObject { ["method"] = method, ["api_key"] = key, ["info"] = info ?? new JObject() };
            if (Secrets != null && Secrets.Available && Secrets.Write(account, Encode(secret)) && Decode(Secrets.Read(account)) == secret)
            {
                data["secret_store"] = Secrets.Name;
                return WriteConfig(data) ? Secrets.Name : "";
            }
            data["secret_store"] = "file";
            data["secret"] = secret;
            return WriteConfig(data) ? "file" : "";
        }

        /// <summary>Signs out: removes the secret from the OS store and empties the file.</summary>
        public void Clear()
        {
            var data = ReadConfig();
            var method = Json.Str(data, "method");
            if (method.Length > 0 && Json.Str(data, "secret_store") != "file" && Secrets != null && Secrets.Available)
                Secrets.Delete(AccountFor(method, Json.Str(data, "api_key")));
            if (File.Exists(ConfigPath)) WriteConfig(new JObject());
        }

        static string AccountFor(string method, string key) => method == "oauth" ? "oauth" : "apikey:" + key;

        // Base64 keeps every secret inside the characters `security -i` reads without quoting.
        static string Encode(string secret) => "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

        static string Decode(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith("b64:", StringComparison.Ordinal)) return stored;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(4))); }
            catch (FormatException) { return ""; }
        }

        JObject ReadConfig()
        {
            try
            {
                if (ConfigPath.Length == 0 || !File.Exists(ConfigPath)) return new JObject();
                return Json.ParseObject(File.ReadAllText(ConfigPath)) ?? new JObject();
            }
            catch (IOException)
            {
                return new JObject();
            }
        }

        bool WriteConfig(JObject data)
        {
            if (ConfigPath.Length == 0) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath) ?? ".");
                File.WriteAllText(ConfigPath, data.ToString(Formatting.Indented));
                RestrictToUser(ConfigPath);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        static void RestrictToUser(string path)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return; // %APPDATA% is per-user already
            ProcessRunner.Run("/bin/chmod", new[] { "600", path }, null, 5000);
        }
    }

    /// <summary>Runs a short helper process, with optional stdin, and returns its exit code and stdout.</summary>
    static class ProcessRunner
    {
        public static (int code, string output) Run(string file, IEnumerable<string> args, string stdin, int timeoutMs)
        {
            try
            {
                var info = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = stdin != null,
                    CreateNoWindow = true,
                };
                info.Arguments = string.Join(" ", Quote(args));
                using (var process = Process.Start(info))
                {
                    if (process == null) return (-1, "");
                    if (stdin != null)
                    {
                        process.StandardInput.Write(stdin);
                        process.StandardInput.Close();
                    }
                    var output = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        return (-1, "");
                    }
                    return (process.ExitCode, output.Result);
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is IOException || e is InvalidOperationException)
            {
                return (-1, "");
            }
        }

        static IEnumerable<string> Quote(IEnumerable<string> args)
        {
            foreach (var arg in args)
                yield return arg.IndexOfAny(new[] { ' ', '"', '\'' }) >= 0 ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
        }
    }

    /// <summary>macOS Keychain through /usr/bin/security. The secret goes on stdin, never as an argument.</summary>
    public sealed class MacKeychain : ISecretStore
    {
        public const string Security = "/usr/bin/security";
        public string Name => "keychain";
        public bool Available => File.Exists(Security);

        public bool Write(string account, string secret)
        {
            // Account and secret are restricted to characters that need no quoting in `security -i`.
            if (!Safe(account) || !Safe(secret)) return false;
            var command = $"add-generic-password -U -s \"{CredentialStore.Service}\" -a \"{account}\" -w \"{secret}\"\n";
            var (code, _) = ProcessRunner.Run(Security, new[] { "-i" }, command, 10000);
            return code == 0;
        }

        public string Read(string account)
        {
            var (code, output) = ProcessRunner.Run(Security, new[] { "find-generic-password", "-s", CredentialStore.Service, "-a", account, "-w" }, null, 10000);
            return code == 0 ? output.Trim() : "";
        }

        public void Delete(string account)
            => ProcessRunner.Run(Security, new[] { "delete-generic-password", "-s", CredentialStore.Service, "-a", account }, null, 10000);

        static bool Safe(string text)
        {
            foreach (var c in text)
                if (!(char.IsLetterOrDigit(c) && c < 128) && "_-.=+/:@".IndexOf(c) < 0) return false;
            return text.Length > 0;
        }
    }

    /// <summary>Linux Secret Service through secret-tool (libsecret). The secret goes on stdin.</summary>
    public sealed class LinuxSecretTool : ISecretStore
    {
        static readonly string[] Paths = { "/usr/bin/secret-tool", "/usr/local/bin/secret-tool" };
        readonly string _tool = Array.Find(Paths, File.Exists);

        public string Name => "secret-service";
        public bool Available => _tool != null;

        public bool Write(string account, string secret)
            => ProcessRunner.Run(_tool, new[] { "store", "--label=" + CredentialStore.Service, "service", CredentialStore.Service, "account", account }, secret, 10000).code == 0;

        public string Read(string account)
        {
            var (code, output) = ProcessRunner.Run(_tool, new[] { "lookup", "service", CredentialStore.Service, "account", account }, null, 10000);
            return code == 0 ? output.Trim() : "";
        }

        public void Delete(string account)
            => ProcessRunner.Run(_tool, new[] { "clear", "service", CredentialStore.Service, "account", account }, null, 10000);
    }

    /// <summary>Windows Credential Manager (generic credentials).</summary>
    public sealed class WindowsCredentialStore : ISecretStore
    {
        public string Name => "credential-manager";
        public bool Available => Environment.OSVersion.Platform == PlatformID.Win32NT;

        static string Target(string account) => $"{CredentialStore.Service}/{account}";

        public bool Write(string account, string secret)
        {
            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Native.Credential
                {
                    Type = 1, // CRED_TYPE_GENERIC
                    TargetName = Target(account),
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                    UserName = account,
                };
                return Native.CredWrite(ref credential, 0);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeCoTaskMem(blob);
            }
        }

        public string Read(string account)
        {
            try
            {
                if (!Native.CredRead(Target(account), 1, 0, out var pointer)) return "";
                try
                {
                    var credential = Marshal.PtrToStructure<Native.Credential>(pointer);
                    if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
                    var bytes = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    return Encoding.Unicode.GetString(bytes);
                }
                finally
                {
                    Native.CredFree(pointer);
                }
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return "";
            }
        }

        public void Delete(string account)
        {
            try { Native.CredDelete(Target(account), 1, 0); }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { }
        }

        static class Native
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct Credential
            {
                public uint Flags;
                public uint Type;
                public string TargetName;
                public string Comment;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
                public uint CredentialBlobSize;
                public IntPtr CredentialBlob;
                public uint Persist;
                public uint AttributeCount;
                public IntPtr Attributes;
                public string TargetAlias;
                public string UserName;
            }

            [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool CredWrite(ref Credential credential, uint flags);

            [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

            [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool CredDelete(string target, uint type, uint flags);

            [DllImport("advapi32.dll")]
            public static extern void CredFree(IntPtr buffer);
        }
    }

    /// <summary>In-memory store for tests: the real Keychain is never touched.</summary>
    public sealed class MemorySecretStore : ISecretStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public bool Fail;
        public string Name => "memory";
        public bool Available => true;
        public bool Write(string account, string secret)
        {
            if (Fail) return false;
            Values[account] = secret;
            return true;
        }
        public string Read(string account) => Values.TryGetValue(account, out var v) ? v : "";
        public void Delete(string account) => Values.Remove(account);
    }
}
