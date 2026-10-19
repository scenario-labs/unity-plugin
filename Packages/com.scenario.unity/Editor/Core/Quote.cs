using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Scenario.Editor.Core
{
    /// <summary>
    /// Single-use price quotes bound to the exact request.
    ///
    /// Generate is allowed only with a quote whose fingerprint (model, scope and
    /// canonical parameters) matches the form now. Generating consumes it; any edit
    /// produces a different fingerprint, so a stale price is never charged.
    /// </summary>
    public sealed class QuoteBook
    {
        public const double MaxAgeSeconds = 600;

        public sealed class Quote
        {
            public string Fingerprint;
            public double Cu;
            public double Discount;
            public double At;
        }

        readonly Dictionary<string, Quote> _quotes = new Dictionary<string, Quote>();

        public static string Fingerprint(string modelId, JObject parameters, string scope = "")
            => Json.Sha256($"{modelId}\n{scope}\n{Json.Canonical(parameters ?? new JObject())}");

        public void Store(string fingerprint, double cu, double discount, double now)
            => _quotes[fingerprint] = new Quote { Fingerprint = fingerprint, Cu = cu, Discount = discount, At = now };

        /// <summary>The quote for this fingerprint if it is fresh, else null.</summary>
        public Quote Usable(string fingerprint, double now)
        {
            if (!_quotes.TryGetValue(fingerprint, out var quote)) return null;
            return now - quote.At > MaxAgeSeconds ? null : quote;
        }

        /// <summary>Takes the quote out (single use). Null when there is none to take.</summary>
        public Quote Consume(string fingerprint, double now)
        {
            var quote = Usable(fingerprint, now);
            _quotes.Remove(fingerprint);
            return quote;
        }

        public void Clear() => _quotes.Clear();
    }
}
