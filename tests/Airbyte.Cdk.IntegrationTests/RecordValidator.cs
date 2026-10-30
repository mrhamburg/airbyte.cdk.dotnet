#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Json.Schema;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Validates a record against a stream schema and reports every violation with its JSON pointer.
    /// </summary>
    public static class RecordValidator
    {
        private const int MaxErrors = 20;

        public static IReadOnlyList<string> Validate(JsonSchema schema, JsonElement record)
        {
            var results = schema.Evaluate(record, new EvaluationOptions { OutputFormat = OutputFormat.List });

            var errors = new List<string>();
            foreach (var detail in results.Details ?? new List<EvaluationResults>())
            {
                if (detail.Errors == null || detail.Errors.Count == 0)
                    continue;

                var message = string.Join("; ", detail.Errors.Select(x => $"{x.Key}: {x.Value}"));
                errors.Add($"{detail.InstanceLocation}: {message}");

                if (errors.Count >= MaxErrors)
                    break;
            }

            return errors;
        }
    }
}