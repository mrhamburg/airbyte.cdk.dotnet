#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Airbyte.Cdk.IntegrationTests
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            try
            {
                return args[0] switch
                {
                    "run" => await RunAsync(args.Skip(1).ToArray()),
                    "list-changed" => ListChanged(args.Skip(1).ToArray()),
                    _ => UnknownVerb(args[0])
                };
            }
            catch (UsageException e)
            {
                Console.Error.WriteLine(e.Message);
                PrintUsage();
                return 2;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var connectors = new List<string>();
            var noSkip = false;
            var keepArtifacts = false;
            var verbose = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--connector":
                        connectors.Add(Value(args, ref i));
                        break;
                    case "--no-skip":
                        noSkip = true;
                        break;
                    case "--keep-artifacts":
                        keepArtifacts = true;
                        break;
                    case "--verbose":
                        verbose = true;
                        break;
                    default:
                        throw new UsageException($"Unknown option: {args[i]}");
                }
            }

            if (connectors.Count == 0)
                throw new UsageException("run requires at least one --connector <path>.");

            var options = new RunOptions(noSkip, keepArtifacts, verbose);
            var runner = new Runner(SecretSourceFactory.Create(), options);
            var results = new List<ConnectorResult>();

            foreach (var connector in connectors)
                results.Add(await runner.RunAsync(connector, CancellationToken.None));

            Report(results, options);

            var codes = results.Select(x => ExitCodes.Map(x, noSkip)).ToList();
            return codes.Count == 0 ? 0 : codes.Max();
        }

        private static int ListChanged(string[] args)
        {
            string? changedFiles = null;
            var repositoryRoot = Directory.GetCurrentDirectory();
            var asJson = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--changed-files":
                        changedFiles = Value(args, ref i);
                        break;
                    case "--repo":
                        repositoryRoot = Value(args, ref i);
                        break;
                    case "--json":
                        asJson = true;
                        break;
                    default:
                        throw new UsageException($"Unknown option: {args[i]}");
                }
            }

            if (changedFiles == null)
                throw new UsageException("list-changed requires --changed-files <path>.");

            var paths = File.ReadAllLines(changedFiles)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0);

            var connectors = ChangedConnectors.FromChangedFiles(paths, repositoryRoot)
                .Select(x => Path.GetRelativePath(repositoryRoot, x).Replace('\\', '/'))
                .ToList();

            if (asJson)
            {
                Console.WriteLine("[" + string.Join(",", connectors.Select(x => $"\"{x}\"")) + "]");
            }
            else
            {
                foreach (var connector in connectors)
                    Console.WriteLine(connector);
            }

            return 0;
        }

        private static void Report(IReadOnlyList<ConnectorResult> results, RunOptions options)
        {
            foreach (var result in results)
            {
                Console.WriteLine();
                Console.WriteLine($"{StatusLabel(result.Status)} {result.ConnectorPath}");

                foreach (var error in result.Errors)
                    WriteError(error);

                foreach (var test in result.Tests)
                {
                    Console.WriteLine($"  {StatusLabel(test.Status)} {test.Name}");
                    if (test.SkipReason != null)
                        WriteWarning($"{test.Name}: {test.SkipReason}");

                    foreach (var stream in test.Streams)
                        Console.WriteLine(
                            $"      {StatusLabel(stream.Status)} {stream.Name}: {stream.RecordCount} record(s)");

                    foreach (var error in test.Errors)
                        WriteError($"{test.Name}: {error}");

                    foreach (var stream in test.Streams)
                        foreach (var error in stream.Errors.Take(options.Verbose ? int.MaxValue : 5))
                            WriteError($"{test.Name}/{stream.Name}: {error}");
                }

                if (result.ArtifactsDirectory != null)
                    Console.WriteLine($"    artifacts: {result.ArtifactsDirectory}");
            }

            WriteStepSummary(results);
        }

        private static void WriteStepSummary(IReadOnlyList<ConnectorResult> results)
        {
            var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (string.IsNullOrWhiteSpace(summaryPath))
                return;

            var builder = new StringBuilder();
            builder.AppendLine("### Connector integration tests");
            builder.AppendLine();
            builder.AppendLine("| Connector | Status | Scenario | Streams |");
            builder.AppendLine("|---|---|---|---|");

            foreach (var result in results)
            {
                var connector = Path.GetFileName(result.ConnectorPath);
                if (result.Tests.Count == 0)
                {
                    builder.AppendLine($"| {connector} | {StatusLabel(result.Status)} | — | — |");
                    continue;
                }

                foreach (var test in result.Tests)
                    builder.AppendLine(
                        $"| {connector} | {StatusLabel(test.Status)} | {test.Name} | " +
                        string.Join("<br>", test.Streams.Select(x => $"{x.Name}: {x.RecordCount}")) + " |");
            }

            try
            {
                File.AppendAllText(summaryPath!, builder.ToString());
            }
            catch (IOException)
            {
                // The summary is advisory; never fail the run over it.
            }
        }

        private static string StatusLabel(Status status) => status switch
        {
            Status.Passed => "PASS",
            Status.Skipped => "SKIP",
            _ => "FAIL"
        };

        private static void WriteError(string message)
        {
            Console.WriteLine($"    {message}");
            if (UsesGitHubAnnotations())
                Console.WriteLine($"::error::{Escape(message)}");
        }

        private static void WriteWarning(string message)
        {
            if (UsesGitHubAnnotations())
                Console.WriteLine($"::warning::{Escape(message)}");
        }

        private static bool UsesGitHubAnnotations() =>
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

        private static string Escape(string message) =>
            message.Replace("\r", " ").Replace("\n", " ").Replace("%", "%25");

        private static string Value(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
                throw new UsageException($"Missing value for {args[index]}.");

            return args[++index];
        }

        private static int UnknownVerb(string verb)
        {
            Console.Error.WriteLine($"Unknown command: {verb}");
            PrintUsage();
            return 2;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Airbyte connector integration tests

  run --connector <path> [--connector <path>...] [--no-skip] [--keep-artifacts] [--verbose]
      Builds each connector, then runs check, discover and read for every scenario declared in
      integration_tests.yaml, validating every record against the stream schema.

  list-changed --changed-files <path> [--repo <path>] [--json]
      Maps `git diff --name-only` output to the connector directories that changed.

Exit codes: 0 passed (or skipped), 1 failed, 2 invalid usage or invalid integration_tests.yaml.");
        }

        private sealed class UsageException : Exception
        {
            public UsageException(string message) : base(message)
            {
            }
        }
    }
}