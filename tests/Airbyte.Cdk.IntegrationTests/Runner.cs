#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Json.Schema;
using System.Threading.Tasks;

namespace Airbyte.Cdk.IntegrationTests
{
    public enum Status
    {
        Passed,
        Failed,
        Skipped
    }

    public sealed record StreamResult(string Name, Status Status, int RecordCount, IReadOnlyList<string> Errors);

    public sealed record TestResult(
        string Name,
        Status Status,
        IReadOnlyList<StreamResult> Streams,
        IReadOnlyList<string> Errors,
        string? SkipReason = null);

    public sealed record ConnectorResult(
        string ConnectorPath,
        Status Status,
        IReadOnlyList<TestResult> Tests,
        IReadOnlyList<string> Errors,
        string? ArtifactsDirectory = null)
    {
        /// <summary>
        /// Prefix that marks a declaration error rather than a connector failure.
        /// </summary>
        public const string SpecErrorPrefix = "spec: ";

        public bool IsSpecError => Errors.Any(x => x.StartsWith(SpecErrorPrefix, StringComparison.Ordinal));
    }

    public sealed record RunOptions(bool NoSkip, bool KeepArtifacts, bool Verbose);

    public static class ExitCodes
    {
        public static int Map(ConnectorResult result, bool noSkip)
        {
            if (result.IsSpecError)
                return 2;

            return result.Status switch
            {
                Status.Passed => 0,
                Status.Skipped => noSkip ? 1 : 0,
                _ => 1
            };
        }
    }

    /// <summary>
    /// Runs every declared scenario of one connector: check, discover, read, then validate the records.
    /// </summary>
    public sealed class Runner
    {
        private readonly ISecretSource _secrets;
        private readonly RunOptions _options;

        public Runner(ISecretSource secrets, RunOptions options)
        {
            _secrets = secrets;
            _options = options;
        }

        public async Task<ConnectorResult> RunAsync(string connectorDirectory, CancellationToken cancellationToken)
        {
            var directory = Path.GetFullPath(connectorDirectory);

            ConnectorSpec spec;
            try
            {
                spec = ConnectorSpecLoader.Load(directory);
            }
            catch (ConnectorSpecException e)
            {
                return new ConnectorResult(directory, Status.Failed, Array.Empty<TestResult>(),
                    new[] { ConnectorResult.SpecErrorPrefix + e.Message });
            }

            var artifacts = Path.Combine(Path.GetTempPath(), "airbyte-integration-tests",
                ConnectorLocator.ConnectorName(directory), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(artifacts);

            var tests = new List<TestResult>();
            var errors = new List<string>();

            var process = new ConnectorProcess(directory, spec.ProjectFile, spec.Module, artifacts);
            try
            {
                var build = await process.BuildAsync(cancellationToken);
                if (build.ExitCode != 0)
                {
                    errors.Add(build.TimedOut
                        ? "dotnet build timed out."
                        : $"dotnet build failed with exit code {build.ExitCode}:{Environment.NewLine}{Tail(build.Output)}");
                }
                else
                {
                    foreach (var test in spec.Tests)
                        tests.Add(await RunTestAsync(process, spec, test, artifacts, cancellationToken));
                }
            }
            finally
            {
                var keep = _options.KeepArtifacts || errors.Count > 0 || tests.Any(x => x.Status == Status.Failed);
                if (!keep)
                    TryDelete(artifacts);
            }

            return new ConnectorResult(directory, Aggregate(tests, errors), tests, errors,
                Directory.Exists(artifacts) ? artifacts : null);
        }

        private async Task<TestResult> RunTestAsync(ConnectorProcess process, ConnectorSpec spec, TestCaseSpec test,
            string artifacts, CancellationToken cancellationToken)
        {
            var testDirectory = Path.Combine(artifacts, Sanitize(test.Name));
            Directory.CreateDirectory(testDirectory);

            var config = BuildConfig(test);

            var missingSecrets = new List<string>();
            foreach (var secret in spec.Secrets)
            {
                var value = await _secrets.GetAsync(secret.Value, cancellationToken);
                if (string.IsNullOrEmpty(value))
                    missingSecrets.Add($"{secret.Key} ({secret.Value})");
                else
                    config[secret.Key] = value;
            }

            if (missingSecrets.Count > 0)
            {
                var reason = "missing secrets: " + string.Join(", ", missingSecrets);
                return new TestResult(test.Name, Status.Skipped, Array.Empty<StreamResult>(),
                    new[] { reason }, reason);
            }

            var configPath = Path.Combine(testDirectory, "config.json");
            File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var timeout = TimeSpan.FromSeconds(test.TimeoutSeconds);
            var errors = new List<string>();

            var check = await process.RunAsync("check", configPath, null, null, timeout, cancellationToken);
            if (check.TimedOut)
                return Failed(test, errors.Append($"check timed out after {test.TimeoutSeconds}s.").ToList());

            var connectionStatus = check.Messages.Where(x => MessageParser.TypeOf(x) == "CONNECTION_STATUS").ToList();
            if (check.ExitCode != 0)
                errors.Add($"check failed with exit code {check.ExitCode}:{Environment.NewLine}{Tail(check.Output)}");
            else if (connectionStatus.Count == 0)
                errors.Add("check produced no CONNECTION_STATUS message.");
            else if (!Succeeded(connectionStatus[0]))
                errors.Add($"check reported a failed connection: {connectionStatus[0].GetRawText()}");

            if (errors.Count > 0)
                return Failed(test, errors);

            var discover = await process.RunAsync("discover", configPath, null, null, timeout, cancellationToken);
            if (discover.TimedOut)
                return Failed(test, errors.Append($"discover timed out after {test.TimeoutSeconds}s.").ToList());
            if (discover.ExitCode != 0)
                return Failed(test,
                    errors.Append($"discover failed with exit code {discover.ExitCode}:{Environment.NewLine}{Tail(discover.Output)}").ToList());

            var catalogMessage = discover.Messages.FirstOrDefault(x => MessageParser.TypeOf(x) == "CATALOG");
            if (catalogMessage.ValueKind != JsonValueKind.Object
                || !catalogMessage.TryGetProperty("catalog", out var discovered)
                || !discovered.TryGetProperty("streams", out _))
                return Failed(test, errors.Append("discover produced no catalog.").ToList());

            string catalogJson;
            try
            {
                catalogJson = CatalogBuilder.Build(discovered, test.Streams);
            }
            catch (ConnectorSpecException e)
            {
                return Failed(test, errors.Append(e.Message).ToList());
            }

            var catalogPath = Path.Combine(testDirectory, "configured_catalog.json");
            File.WriteAllText(catalogPath, catalogJson);

            var statePath = WriteState(test, testDirectory);

            var read = await process.RunAsync("read", configPath, catalogPath, statePath, timeout, cancellationToken,
                test.Streams.Select(x => x.Name).ToList(), test.Limit);
            if (read.TimedOut)
                return Failed(test, errors.Append($"read timed out after {test.TimeoutSeconds}s.").ToList());
            if (!read.StoppedEarly && read.ExitCode != 0)
                errors.Add($"read failed with exit code {read.ExitCode}:{Environment.NewLine}{Tail(read.Output)}");

            var errorTraces = read.Messages.Where(MessageParser.IsErrorTrace).ToList();
            foreach (var trace in errorTraces)
                errors.Add("connector emitted an error trace: " + trace.GetRawText());

            var streamResults = new List<StreamResult>();
            foreach (var stream in test.Streams)
                streamResults.Add(ValidateStream(discovered, stream, read.Messages));

            if (streamResults.Any(x => x.Status == Status.Failed))
                errors.Add("one or more streams did not match their schema.");

            var status = errors.Count == 0 ? Status.Passed : Status.Failed;
            return new TestResult(test.Name, status, streamResults, errors);
        }

        private static StreamResult ValidateStream(JsonElement discovered, StreamSpec spec, IReadOnlyList<JsonElement> messages)
        {
            var schema = discovered.GetProperty("streams").EnumerateArray()
                .FirstOrDefault(x => x.TryGetProperty("name", out var name) && name.GetString() == spec.Name);

            var records = messages
                .Where(x => MessageParser.TypeOf(x) == "RECORD")
                .Select(x => x.GetProperty("record"))
                .Where(x => x.TryGetProperty("stream", out var stream) && stream.GetString() == spec.Name)
                .ToList();

            if (records.Count < spec.MinRecords)
                return new StreamResult(spec.Name, Status.Failed, records.Count,
                    new[] { $"expected at least {spec.MinRecords} record(s) but the connector emitted {records.Count}." });

            if (!schema.TryGetProperty("json_schema", out var schemaElement))
                return new StreamResult(spec.Name, Status.Failed, records.Count,
                    new[] { "discover did not report a json_schema for this stream." });

            JsonSchema schemaValidator;
            try
            {
                schemaValidator = StrictSchema.Build(schemaElement);
            }
            catch (Exception e)
            {
                return new StreamResult(spec.Name, Status.Failed, records.Count, new[] { e.Message });
            }

            var errors = new List<string>();
            for (var index = 0; index < records.Count; index++)
            {
                var data = records[index].GetProperty("data");
                var violations = RecordValidator.Validate(schemaValidator, data);
                foreach (var violation in violations)
                    errors.Add($"record {index} ({spec.Name}) {violation}");
            }

            return new StreamResult(spec.Name, errors.Count == 0 ? Status.Passed : Status.Failed, records.Count, errors);
        }

        private static JsonObject BuildConfig(TestCaseSpec test)
        {
            var config = new JsonObject();
            if (test.Config.ValueKind == JsonValueKind.Object)
                foreach (var property in test.Config.EnumerateObject())
                    config[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            return config;
        }

        private static string? WriteState(TestCaseSpec test, string testDirectory)
        {
            var state = new JsonObject();
            foreach (var stream in test.Streams.Where(x => x.State.HasValue))
                if (stream.State!.Value.ValueKind == JsonValueKind.Object)
                    foreach (var property in stream.State.Value.EnumerateObject())
                        state[property.Name] = JsonNode.Parse(property.Value.GetRawText());

            if (state.Count == 0)
                return null;

            var path = Path.Combine(testDirectory, "state.json");
            File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        private static bool Succeeded(JsonElement connectionStatus)
        {
            if (!connectionStatus.TryGetProperty("connectionStatus", out var status))
                return false;

            return status.TryGetProperty("status", out var value)
                   && string.Equals(value.GetString(), "SUCCEEDED", StringComparison.OrdinalIgnoreCase);
        }

        private static TestResult Failed(TestCaseSpec test, IReadOnlyList<string> errors) =>
            new(test.Name, Status.Failed, Array.Empty<StreamResult>(), errors);

        private static Status Aggregate(IReadOnlyList<TestResult> tests, IReadOnlyList<string> errors)
        {
            if (errors.Count > 0 || tests.Any(x => x.Status == Status.Failed))
                return Status.Failed;
            if (tests.Count > 0 && tests.All(x => x.Status == Status.Skipped))
                return Status.Skipped;
            return Status.Passed;
        }

        private static string Sanitize(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private static string Tail(string output, int lines = 40)
        {
            var split = output.Split('\n');
            return string.Join(Environment.NewLine, split.Skip(Math.Max(0, split.Length - lines)));
        }

        private static void TryDelete(string directory)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // Artifacts are scratch; failing to delete them must not fail the run.
            }
        }
    }
}