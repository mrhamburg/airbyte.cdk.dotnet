#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Masks credential values before they reach logs, error messages or retained artifacts.
    /// </summary>
    public static class Redaction
    {
        public const string Placeholder = "***";

        public static string Apply(string text, IEnumerable<string> secrets)
        {
            foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal))
                text = text.Replace(secret, Placeholder, StringComparison.Ordinal);

            return text;
        }
    }
}