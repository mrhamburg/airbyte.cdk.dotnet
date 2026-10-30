#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// The outcome of one connector invocation.
    /// </summary>
    public sealed record ProcessOutcome(
        int ExitCode,
        IReadOnlyList<JsonElement> Messages,
        string Output,
        bool TimedOut,
        bool StoppedEarly = false);

    /// <summary>
    /// Builds a connector project and runs its entrypoint the way the Airbyte platform does: a child process
    /// that reads a config/catalog/state and writes protocol messages to stdout.
    /// </summary>
    public sealed class ConnectorProcess
    {
        private readonly string _connectorDirectory;
        private readonly string _projectFile;
        private readonly string _module;
        private readonly string _buildOutput;
        private string? _assemblyPath;

        public ConnectorProcess(string connectorDirectory, string projectFile, string module, string artifactsDirectory)
        {
            _connectorDirectory = connectorDirectory;
            _projectFile = projectFile;
            _module = module;
            _buildOutput = Path.Combine(artifactsDirectory, "build");
        }

        public string AssemblyPath =>
            _assemblyPath ?? Path.Combine(_buildOutput, Path.GetFileNameWithoutExtension(_projectFile) + ".dll");

        public async Task<ProcessOutcome> BuildAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_buildOutput);

            var outcome = await ExecuteAsync("dotnet",
                new[]
                {
                    "build", _projectFile, "-c", "Release", "-o", _buildOutput, "--nologo", "-v", "quiet"
                },
                TimeSpan.FromMinutes(10), parseMessages: false, streams: null, recordsPerStream: null,
                cancellationToken);

            _assemblyPath = AssemblyPath;
            if (outcome.ExitCode == 0 && !File.Exists(_assemblyPath))
                return new ProcessOutcome(1, Array.Empty<JsonElement>(),
                    $"Build succeeded but the assembly was not found at {_assemblyPath}.{Environment.NewLine}{outcome.Output}",
                    false);

            return outcome;
        }

        /// <summary>
        /// Runs one Airbyte command. When <paramref name="recordsPerStream"/> is set, the process is stopped once
        /// every stream in <paramref name="streams"/> has produced that many records, because connectors reject
        /// the CDK's own record limit whenever their spec forbids unknown config properties.
        /// </summary>
        public Task<ProcessOutcome> RunAsync(string command, string? configPath, string? catalogPath,
            string? statePath, TimeSpan timeout, CancellationToken cancellationToken,
            IReadOnlyCollection<string>? streams = null, int? recordsPerStream = null)
        {
            var arguments = new List<string> { AssemblyPath, "read", "--command", command };
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                arguments.Add("--config");
                arguments.Add(configPath!);
            }

            if (!string.IsNullOrWhiteSpace(catalogPath))
            {
                arguments.Add("--catalog");
                arguments.Add(catalogPath!);
            }

            if (!string.IsNullOrWhiteSpace(statePath))
            {
                arguments.Add("--state");
                arguments.Add(statePath!);
            }

            return ExecuteAsync("dotnet", arguments, timeout, parseMessages: true, streams, recordsPerStream,
                cancellationToken);
        }

        private async Task<ProcessOutcome> ExecuteAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, bool parseMessages, IReadOnlyCollection<string>? streams, int? recordsPerStream,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _connectorDirectory
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            startInfo.Environment["AIRBYTE_IMPL_PATH"] = AssemblyPath;
            startInfo.Environment["AIRBYTE_IMPL_MODULE"] = _module;

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            var messages = new List<JsonElement>();
            var raw = new StringBuilder();
            var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            var readTask = Task.Run(async () =>
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token);
                    if (line == null)
                        return false;

                    if (!parseMessages || !MessageParser.TryParse(line, out var message))
                    {
                        raw.AppendLine(line);
                        continue;
                    }

                    messages.Add(message);

                    if (!recordsPerStream.HasValue || streams == null || streams.Count == 0)
                        continue;

                    if (!TryRecordStream(message, out var streamName))
                        continue;

                    counts[streamName] = counts.TryGetValue(streamName, out var seen) ? seen + 1 : 1;
                    if (!streams.All(x => counts.TryGetValue(x, out var count) && count >= recordsPerStream.Value))
                        continue;

                    TryKill(process);
                    return true;
                }
            }, timeoutSource.Token);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                TryKill(process);
            }

            var stoppedEarly = false;
            try
            {
                stoppedEarly = await readTask;
            }
            catch (OperationCanceledException)
            {
                // The reader is cancelled together with the process.
            }

            var errors = string.Empty;
            try
            {
                errors = await standardError;
            }
            catch (OperationCanceledException)
            {
                // Stderr is best effort after a kill.
            }

            if (!string.IsNullOrWhiteSpace(errors))
                raw.AppendLine(errors.TrimEnd());

            var exitCode = -1;
            if (!timedOut)
            {
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                    exitCode = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    // The process handle is already released; the outcome stays a failure.
                }
            }

            return new ProcessOutcome(exitCode, messages, raw.ToString().TrimEnd(), timedOut, stoppedEarly);
        }

        private static bool TryRecordStream(JsonElement message, out string streamName)
        {
            streamName = string.Empty;
            if (MessageParser.TypeOf(message) != "RECORD"
                || !message.TryGetProperty("record", out var record)
                || record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("stream", out var stream)
                || stream.ValueKind != JsonValueKind.String)
                return false;

            streamName = stream.GetString()!;
            return true;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited between the check and the kill.
            }
        }
    }
}