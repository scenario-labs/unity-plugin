using System;
using System.Collections.Generic;

namespace Scenario.Editor.Core
{
    /// <summary>What went wrong, in terms the money rules care about.</summary>
    public enum ErrorKind
    {
        Auth,
        Forbidden,
        Quota,
        Moderated,
        RateLimit,
        NotFound,
        Invalid,
        Transport,
        Timeout,
        Http5xx,
        Protocol,
        Local,
        EstimateJob,
        Scenario,
        Canceled,
    }

    public sealed class ScenarioError
    {
        public ErrorKind Kind;
        public string Message;
        public string Detail = "";
        /// <summary>Scenario may have received and run the request (a paid call is then "unknown").</summary>
        public bool Ambiguous;
        /// <summary>The request never left the plugin (session setup failed first).</summary>
        public bool NotSent;
        public bool SessionExpired;
        public int Status;
        public double RetryAfter = -1;
        public string JobId = "";

        public ScenarioError(ErrorKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public override string ToString() => $"{Kind}: {Message}";
    }

    /// <summary>A value or an error. The core never throws for expected failures.</summary>
    public sealed class Result<T>
    {
        public bool Ok { get; private set; }
        public T Value { get; private set; }
        public ScenarioError Error { get; private set; }

        public static Result<T> Success(T value) => new Result<T> { Ok = true, Value = value };
        public static Result<T> Fail(ScenarioError error) => new Result<T> { Ok = false, Error = error };

        public static Result<T> Fail(ErrorKind kind, string message, Action<ScenarioError> extra = null)
        {
            var error = new ScenarioError(kind, message);
            extra?.Invoke(error);
            return Fail(error);
        }

        public Result<TOut> Map<TOut>(Func<T, TOut> map) => Ok ? Result<TOut>.Success(map(Value)) : Result<TOut>.Fail(Error);
        public Result<TOut> Cast<TOut>() => Result<TOut>.Fail(Error);
    }

    public static class Errors
    {
        /// <summary>Failures where Scenario may still have received and run the request.</summary>
        public static readonly HashSet<ErrorKind> AmbiguousKinds = new HashSet<ErrorKind>
        {
            ErrorKind.Transport, ErrorKind.Timeout, ErrorKind.Http5xx, ErrorKind.Protocol,
        };

        /// <summary>Failures worth retrying, for allow-listed reads only.</summary>
        public static readonly HashSet<ErrorKind> RetryableKinds = new HashSet<ErrorKind>
        {
            ErrorKind.RateLimit, ErrorKind.Transport, ErrorKind.Timeout, ErrorKind.Http5xx,
        };

        static readonly string[] ModerationWords = { "moderat", "nsfw", "sensitive content", "content policy", "flagged", "safety system" };
        static readonly string[] QuotaWords = { "insufficient", "not enough credit", "not enough creative", "out of credit", "quota" };
        static readonly string[] AuthWords = { "unauthorized", "unauthenticated", "invalid api key", "invalid credentials", "authentication", "invalid_token" };

        static readonly Dictionary<ErrorKind, string> Messages = new Dictionary<ErrorKind, string>
        {
            { ErrorKind.Auth, "Scenario did not accept the sign-in. Sign in again or check the API key in Scenario settings." },
            { ErrorKind.Forbidden, "This account or key is not allowed to do that. Check its role in Scenario." },
            { ErrorKind.Quota, "Not enough Creative Units for this run." },
            { ErrorKind.Moderated, "Blocked by Scenario's content moderation. Change the prompt or the reference." },
            { ErrorKind.RateLimit, "Scenario is limiting requests right now. Try again in a moment." },
            { ErrorKind.NotFound, "Scenario could not find that item." },
            { ErrorKind.Invalid, "Scenario refused the parameters." },
            { ErrorKind.Transport, "Could not reach Scenario. Check the connection and try again." },
            { ErrorKind.Timeout, "Scenario took too long to answer." },
            { ErrorKind.Http5xx, "Scenario had a server error. Try again in a moment." },
            { ErrorKind.Protocol, "Scenario returned a response the plugin could not read." },
            { ErrorKind.Canceled, "Canceled." },
        };

        public static bool IsModerated(string text)
        {
            var lower = (text ?? "").ToLowerInvariant();
            foreach (var word in ModerationWords)
                if (lower.Contains(word)) return true;
            return false;
        }

        /// <summary>Kind of a failure from the server's detail text and HTTP status (0 inside a 200 JSON-RPC reply).</summary>
        public static ErrorKind Classify(string detail, int status = 0)
        {
            var lower = (detail ?? "").ToLowerInvariant();
            if (IsModerated(lower)) return ErrorKind.Moderated;
            switch (status)
            {
                case 401: return ErrorKind.Auth;
                case 402: return ErrorKind.Quota;
                case 403: return ErrorKind.Forbidden;
                case 404: return ErrorKind.NotFound;
                case 429: return ErrorKind.RateLimit;
            }
            if (status >= 500) return ErrorKind.Http5xx;
            if (HasAny(lower, QuotaWords)) return ErrorKind.Quota;
            if (status == 400 || status == 422) return ErrorKind.Invalid;
            if (HasAny(lower, AuthWords)) return ErrorKind.Auth;
            if (lower.Contains("rate limit") || lower.Contains("too many requests")) return ErrorKind.RateLimit;
            if (lower.Contains("forbidden") || lower.Contains("not allowed")) return ErrorKind.Forbidden;
            return ErrorKind.Scenario;
        }

        /// <summary>The plain explanation, plus the server's detail when it adds something.</summary>
        public static string MessageFor(ErrorKind kind, string detail = "")
        {
            var baseText = Messages.TryGetValue(kind, out var m) ? m : "Scenario reported an error.";
            var clean = (detail ?? "").Trim();
            if (clean.Length == 0 || kind == ErrorKind.Transport || kind == ErrorKind.Timeout) return baseText;
            if (clean.Length > 300) clean = clean.Substring(0, 300) + "...";
            return $"{baseText} ({clean})";
        }

        static bool HasAny(string text, string[] words)
        {
            foreach (var word in words)
                if (text.Contains(word)) return true;
            return false;
        }
    }
}
