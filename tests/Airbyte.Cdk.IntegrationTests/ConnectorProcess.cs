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

        public async Task<ProcessOutcome> BuildAsync(CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? properties = null)
        {
            Directory.CreateDirectory(_buildOutput);

            var arguments = new List<string>
            {
                "build", _projectFile, "-c", "Release", "-o", _buildOutput, "--nologo", "-v", "quiet"
            };
            if (properties != null)
                foreach (var property in properties)
                    arguments.Add($"-p:{property.Key}={property.Value}");

            var outcome = await ExecuteAsync("dotnet", arguments,
                TimeSpan.FromMinutes(10), parseMessages: false, streams: null, recordsPerStream: null,
                socketMode: false, cancellationToken);

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
            IReadOnlyCollection<string>? streams = null, int? recordsPerStream = null, bool socketMode = false,
            string verb = "read")
        {
            var arguments = new List<string> { AssemblyPath, verb, "--command", command };
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
                socketMode, cancellationToken);
        }

        /// <summary>
        /// Starts a connector for a long-running operation such as a socket-mode read/write and returns it
        /// immediately. The caller owns the process and must wait for it or dispose it.
        /// </summary>
        public RunningConnector Start(string verb, string? configPath, string? catalogPath, string? statePath,
            IReadOnlyList<string> socketPaths, bool closeStandardInput)
        {
            var arguments = new List<string> { AssemblyPath, verb, "--command", verb };
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

            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                WorkingDirectory = _connectorDirectory
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            ApplyEnvironment(startInfo, socketPaths);

            var process = new Process { StartInfo = startInfo };
            process.Start();

            try
            {
                if (closeStandardInput)
                    process.StandardInput.Close();
            }
            catch
            {
                TryKill(process);
                process.Dispose();
                throw;
            }

            return new RunningConnector(process);
        }

        private void ApplyEnvironment(ProcessStartInfo startInfo, IReadOnlyList<string> socketPaths)
        {
            var socketMode = socketPaths.Count > 0;
            startInfo.Environment["AIRBYTE_IMPL_PATH"] = AssemblyPath;
            startInfo.Environment["AIRBYTE_IMPL_MODULE"] = _module;
            SecretEnvironment.RemoveStoreVariables(startInfo.Environment);
            startInfo.Environment[DataChannelEnvironment.Medium] = socketMode ? "SOCKET" : "STDIO";
            startInfo.Environment[DataChannelEnvironment.Format] = socketMode ? "PROTOBUF" : "JSONL";
            if (socketMode)
                startInfo.Environment[DataChannelEnvironment.SocketPaths] = string.Join(",", socketPaths);
            else
                startInfo.Environment.Remove(DataChannelEnvironment.SocketPaths);
        }

        private async Task<ProcessOutcome> ExecuteAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, bool parseMessages, IReadOnlyCollection<string>? streams, int? recordsPerStream,
            bool socketMode, CancellationToken cancellationToken)
        {
            var socketDirectory = socketMode
                ? Path.Combine(Path.GetTempPath(), "ab-" + Guid.NewGuid().ToString("N")[..8])
                : null;
            var socketPaths = new List<string>();
            if (socketDirectory != null)
            {
                Directory.CreateDirectory(socketDirectory);
                socketPaths.Add(Path.Combine(socketDirectory, "s1.sock"));
                socketPaths.Add(Path.Combine(socketDirectory, "s2.sock"));
            }

            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _connectorDirectory
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            ApplyEnvironment(startInfo, socketPaths);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            var gate = new object();
            var messages = new List<JsonElement>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var seenStates = new HashSet<string>(StringComparer.Ordinal);
            var raw = new StringBuilder();
            var stopped = false;
            var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            bool AddMessage(JsonElement message)
            {
                lock (gate)
                {
                    // The source duplicates STATE to stdout (bookkeeper) and sockets (destination).
                    // Keep one copy so callers see each checkpoint once.
                    if (MessageParser.TypeOf(message) == "STATE" &&
                        !seenStates.Add(message.GetRawText()))
                        return false;

                    if (!recordsPerStream.HasValue || streams == null || streams.Count == 0
                        || !TryRecordStream(message, out var streamName))
                    {
                        messages.Add(message);
                        return false;
                    }

                    // Keep counting every record so the run ends only when every stream reached the cap, but
                    // retain only the capped number: a chatty first stream would otherwise hold an unbounded
                    // number of records in memory while the later streams catch up.
                    counts[streamName] = counts.TryGetValue(streamName, out var seen) ? seen + 1 : 1;
                    if (counts[streamName] <= recordsPerStream.Value)
                        messages.Add(message);

                    if (!streams.All(x => counts.TryGetValue(x, out var count) && count >= recordsPerStream.Value))
                        return false;

                    stopped = true;
                    return true;
                }
            }

            var readTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token);
                    if (line == null)
                        return false;

                    if (!parseMessages || !MessageParser.TryParse(line, out var message))
                    {
                        lock (gate)
                            raw.AppendLine(line);
                        continue;
                    }

                    if (!AddMessage(message))
                        continue;

                    TryKill(process);
                    return true;
                }
            }, timeoutSource.Token);

            var socketTasks = socketPaths.Select(path => Task.Run(async () =>
            {
                try
                {
                    var client = new SocketConnectorClient(path);
                    await client.ReadAsync(message =>
                    {
                        if (AddMessage(message))
                            TryKill(process);
                        return Task.CompletedTask;
                    }, timeoutSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // The reader is cancelled together with the process.
                }
                catch (Exception e)
                {
                    lock (gate)
                        raw.AppendLine($"socket reader failed ({path}): {e.Message}");
                }
            }, timeoutSource.Token)).ToList();

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

            if (socketTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(socketTasks);
                }
                catch (OperationCanceledException)
                {
                    // Socket readers are cancelled together with the process.
                }
            }

            lock (gate)
            {
                stoppedEarly |= stopped;
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
                lock (gate)
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

            if (socketDirectory != null)
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

            List<JsonElement> snapshot;
            string rawText;
            lock (gate)
            {
                snapshot = messages.ToList();
                rawText = raw.ToString().TrimEnd();
            }

            return new ProcessOutcome(exitCode, snapshot, rawText, timedOut, stoppedEarly);
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