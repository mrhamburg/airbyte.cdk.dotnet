#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Runs a destination connector against a source connector over Protobuf Unix domain sockets and
    /// validates the Parquet files the destination wrote.
    /// </summary>
    internal static class DestinationRunner
    {
        private static readonly TimeSpan SpecTimeout = TimeSpan.FromMinutes(10);

        public static async Task<ConnectorResult> RunAsync(ConnectorSpec spec, string artifacts,
            IReadOnlyList<string> secretValues, CancellationToken cancellationToken)
        {
            var result = await RunCoreAsync(spec, artifacts, cancellationToken);
            return Runner.Redact(result, secretValues);
        }

        private static async Task<ConnectorResult> RunCoreAsync(ConnectorSpec spec, string artifacts,
            CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var tests = new List<TestResult>();

            var destination = new ConnectorProcess(spec.Directory, spec.ProjectFile, spec.Module,
                Path.Combine(artifacts, "destination"));
            var destinationBuild = await destination.BuildAsync(cancellationToken);
            if (destinationBuild.ExitCode != 0)
            {
                errors.Add(destinationBuild.TimedOut
                    ? "dotnet build timed out."
                    : $"dotnet build failed with exit code {destinationBuild.ExitCode}:{Environment.NewLine}{Runner.Tail(destinationBuild.Output)}");
                return new ConnectorResult(spec.Directory, Status.Failed, tests, errors);
            }

            var destinationSpec = await destination.RunAsync("spec", null, null, null, SpecTimeout,
                cancellationToken, verb: "write");
            if (destinationSpec.TimedOut)
                errors.Add("destination spec timed out.");
            else if (destinationSpec.ExitCode != 0)
                errors.Add($"destination spec failed with exit code {destinationSpec.ExitCode}:{Environment.NewLine}{Runner.Tail(destinationSpec.Output)}");
            else if (!destinationSpec.Messages.Any(x => MessageParser.TypeOf(x) == "SPEC"))
                errors.Add("destination spec produced no SPEC message.");

            if (errors.Count > 0)
                return new ConnectorResult(spec.Directory, Status.Failed, tests, errors);

            ConnectorSpec source;
            try
            {
                source = ConnectorSpecLoader.Load(spec.Source!.ConnectorDirectory);
            }
            catch (ConnectorSpecException e)
            {
                return new ConnectorResult(spec.Directory, Status.Failed, tests,
                    new[] { ConnectorResult.SpecErrorPrefix + e.Message });
            }

            if (source.Type != ConnectorType.Source)
                return new ConnectorResult(spec.Directory, Status.Failed, tests,
                    new[] { $"{source.Directory} is declared as a {source.Type}; a destination scenario needs a source." });

            var sourceProcess = new ConnectorProcess(source.Directory, source.ProjectFile, source.Module,
                Path.Combine(artifacts, "source"));
            var sourceBuild = await sourceProcess.BuildAsync(cancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["UseLocalCdk"] = "true" });
            if (sourceBuild.ExitCode != 0)
            {
                errors.Add(sourceBuild.TimedOut
                    ? "source dotnet build timed out."
                    : $"source dotnet build failed with exit code {sourceBuild.ExitCode}:{Environment.NewLine}{Runner.Tail(sourceBuild.Output)}");
                return new ConnectorResult(spec.Directory, Status.Failed, tests, errors);
            }

            foreach (var test in spec.Tests)
                tests.Add(await RunScenarioAsync(destination, sourceProcess, spec, test, artifacts,
                    cancellationToken));

            return new ConnectorResult(spec.Directory, Runner.Aggregate(tests, errors), tests, errors);
        }

        private static async Task<TestResult> RunScenarioAsync(ConnectorProcess destination,
            ConnectorProcess sourceProcess, ConnectorSpec spec, TestCaseSpec test, string artifacts,
            CancellationToken cancellationToken)
        {
            var scenarioDirectory = Path.Combine(artifacts, Runner.Sanitize(test.Name));
            Directory.CreateDirectory(scenarioDirectory);
            var timeout = TimeSpan.FromSeconds(test.TimeoutSeconds);
            var errors = new List<string>();

            var parquetDirectory = Path.Combine(scenarioDirectory, "parquet");
            Directory.CreateDirectory(parquetDirectory);

            var config = Runner.BuildConfig(test);
            config["output_path"] = parquetDirectory;
            var configPath = Path.Combine(scenarioDirectory, "config.json");
            File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var destinationCheck = await destination.RunAsync("check", configPath, null, null, timeout,
                cancellationToken, verb: "write");
            if (destinationCheck.TimedOut)
                return Runner.Failed(test,
                    errors.Append($"destination check timed out after {test.TimeoutSeconds}s.").ToList());

            var connectionStatus = destinationCheck.Messages
                .Where(x => MessageParser.TypeOf(x) == "CONNECTION_STATUS").ToList();
            if (destinationCheck.ExitCode != 0)
                errors.Add($"destination check failed with exit code {destinationCheck.ExitCode}:{Environment.NewLine}{Runner.Tail(destinationCheck.Output)}");
            else if (connectionStatus.Count == 0)
                errors.Add("destination check produced no CONNECTION_STATUS message.");
            else if (!Runner.Succeeded(connectionStatus[0]))
                errors.Add($"destination check reported a failed connection: {connectionStatus[0].GetRawText()}");

            if (errors.Count > 0)
                return Runner.Failed(test, errors);

            var sourceConfigPath = Path.Combine(scenarioDirectory, "source_config.json");
            File.WriteAllText(sourceConfigPath, spec.Source!.Config.GetRawText());

            var sourceCheck = await sourceProcess.RunAsync("check", sourceConfigPath, null, null, timeout,
                cancellationToken);
            if (sourceCheck.TimedOut)
                return Runner.Failed(test,
                    errors.Append($"source check timed out after {test.TimeoutSeconds}s.").ToList());

            var sourceStatus = sourceCheck.Messages
                .Where(x => MessageParser.TypeOf(x) == "CONNECTION_STATUS").ToList();
            if (sourceCheck.ExitCode != 0)
                errors.Add($"source check failed with exit code {sourceCheck.ExitCode}:{Environment.NewLine}{Runner.Tail(sourceCheck.Output)}");
            else if (sourceStatus.Count == 0)
                errors.Add("source check produced no CONNECTION_STATUS message.");
            else if (!Runner.Succeeded(sourceStatus[0]))
                errors.Add($"source check reported a failed connection: {sourceStatus[0].GetRawText()}");

            if (errors.Count > 0)
                return Runner.Failed(test, errors);

            var discover = await sourceProcess.RunAsync("discover", sourceConfigPath, null, null, timeout,
                cancellationToken);
            if (discover.TimedOut)
                return Runner.Failed(test,
                    errors.Append($"source discover timed out after {test.TimeoutSeconds}s.").ToList());
            if (discover.ExitCode != 0)
                return Runner.Failed(test,
                    errors.Append($"source discover failed with exit code {discover.ExitCode}:{Environment.NewLine}{Runner.Tail(discover.Output)}").ToList());

            var catalogMessage = discover.Messages.FirstOrDefault(x => MessageParser.TypeOf(x) == "CATALOG");
            if (catalogMessage.ValueKind != JsonValueKind.Object
                || !catalogMessage.TryGetProperty("catalog", out var discovered)
                || !discovered.TryGetProperty("streams", out _))
                return Runner.Failed(test, errors.Append("source discover produced no catalog.").ToList());

            string catalogJson;
            try
            {
                catalogJson = CatalogBuilder.Build(discovered, test.Streams);
            }
            catch (ConnectorSpecException e)
            {
                return Runner.Failed(test, errors.Append(e.Message).ToList());
            }

            var catalogPath = Path.Combine(scenarioDirectory, "configured_catalog.json");
            File.WriteAllText(catalogPath, catalogJson);

            var statePath = Runner.WriteState(test, scenarioDirectory);

            var socketDirectory = Path.Combine(Path.GetTempPath(), "ab-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(socketDirectory);
            var socketPaths = new[]
            {
                Path.Combine(socketDirectory, "s1.sock"),
                Path.Combine(socketDirectory, "s2.sock")
            };

            try
            {
                var seeded = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var stream in test.Streams)
                {
                    var streamDirectory = Path.Combine(parquetDirectory, stream.Name);
                    Directory.CreateDirectory(streamDirectory);
                    var stale = Path.Combine(streamDirectory, "stale.parquet");
                    File.WriteAllText(stale, "stale");
                    seeded[stream.Name] = stale;
                }

                using var sourceRun = sourceProcess.Start("read", sourceConfigPath, catalogPath, statePath,
                    socketPaths, closeStandardInput: false);
                using var destinationRun = destination.Start("write", configPath, catalogPath, null,
                    socketPaths, closeStandardInput: true);

                using var scenarioCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                scenarioCancellation.CancelAfter(timeout);

                var sourceWait = sourceRun.WaitAsync(timeout, scenarioCancellation.Token);
                var destinationWait = destinationRun.WaitAsync(timeout, scenarioCancellation.Token);
                var destinationKilled = false;
                var sourceKilled = false;

                var first = await Task.WhenAny(sourceWait, destinationWait);
                if (ReferenceEquals(first, destinationWait))
                {
                    var firstDestinationExit = await destinationWait;
                    if (destinationRun.TimedOut || firstDestinationExit != 0)
                    {
                        sourceRun.Kill();
                        sourceKilled = true;
                    }
                }
                else
                {
                    var firstSourceExit = await sourceWait;
                    if (sourceRun.TimedOut || firstSourceExit != 0)
                    {
                        destinationRun.Kill();
                        destinationKilled = true;
                    }
                }

                var sourceExit = await sourceWait;
                var destinationExit = await destinationWait;

                if (sourceRun.TimedOut)
                    errors.Add($"source timed out after {test.TimeoutSeconds}s.");
                else if (sourceExit != 0 && !sourceKilled)
                    errors.Add($"source failed with exit code {sourceExit}:{Environment.NewLine}{Runner.Tail(sourceRun.Output)}");

                if (destinationRun.TimedOut)
                    errors.Add($"destination timed out after {test.TimeoutSeconds}s.");
                else if (destinationExit != 0 && !destinationKilled)
                    errors.Add($"destination failed with exit code {destinationExit}:{Environment.NewLine}{Runner.Tail(destinationRun.Output)}");

                foreach (var trace in sourceRun.Messages.Where(MessageParser.IsErrorTrace))
                    errors.Add("source emitted an error trace: " + trace.GetRawText());

                foreach (var trace in destinationRun.Messages.Where(MessageParser.IsErrorTrace))
                    errors.Add("destination emitted an error trace: " + trace.GetRawText());

                if (!destinationRun.Messages.Any(x => MessageParser.TypeOf(x) == "STATE"))
                    errors.Add("destination emitted no STATE message.");

                var streamResults = new List<StreamResult>();
                foreach (var stream in test.Streams)
                {
                    var parquet = ParquetOutputValidator.Validate(parquetDirectory, stream, discovered);
                    streamResults.Add(new StreamResult(parquet.Name, parquet.Status,
                        (int)Math.Min(parquet.RowCount, int.MaxValue), parquet.Errors));

                    var stale = seeded[stream.Name];
                    if (stream.SyncMode == "incremental")
                    {
                        if (!File.Exists(stale))
                            errors.Add($"append scenario deleted {Path.GetFileName(stale)} for stream '{stream.Name}'.");
                    }
                    else if (File.Exists(stale))
                    {
                        errors.Add($"overwrite scenario did not delete {Path.GetFileName(stale)} for stream '{stream.Name}'.");
                    }
                }

                if (streamResults.Any(x => x.Status == Status.Failed))
                    errors.Add("one or more streams did not produce valid parquet output.");

                return new TestResult(test.Name, errors.Count == 0 ? Status.Passed : Status.Failed, streamResults,
                    errors);
            }
            finally
            {
                try
                {
                    Directory.Delete(socketDirectory, true);
                }
                catch (IOException)
                {
                    // Socket files are scratch; failing to delete them must not fail the run.
                }
            }
        }
    }
}
