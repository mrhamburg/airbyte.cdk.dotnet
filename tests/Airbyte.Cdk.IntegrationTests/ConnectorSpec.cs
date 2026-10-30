#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Raised when a connector's integration test declaration is missing or invalid.
    /// </summary>
    public sealed class ConnectorSpecException : Exception
    {
        public ConnectorSpecException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The integration test declaration of a single connector, loaded from <c>integration_tests.yaml</c>.
    /// </summary>
    public sealed class ConnectorSpec
    {
        public ConnectorSpec(string directory, string module, string projectFile,
            IReadOnlyDictionary<string, string> secrets, IReadOnlyList<TestCaseSpec> tests)
        {
            Directory = directory;
            Module = module;
            ProjectFile = projectFile;
            Secrets = secrets;
            Tests = tests;
        }

        /// <summary>Absolute connector directory.</summary>
        public string Directory { get; }

        /// <summary>Name of the <c>Connector</c> subclass, passed as <c>AIRBYTE_IMPL_MODULE</c>.</summary>
        public string Module { get; }

        /// <summary>Absolute path of the connector project file.</summary>
        public string ProjectFile { get; }

        /// <summary>Maps a config property to the secret key that supplies its value.</summary>
        public IReadOnlyDictionary<string, string> Secrets { get; }

        public IReadOnlyList<TestCaseSpec> Tests { get; }
    }

    /// <summary>
    /// One scenario: a config, optional record limits, and the streams to read.
    /// </summary>
    public sealed class TestCaseSpec
    {
        public TestCaseSpec(string name, JsonElement config, int? limit, int timeoutSeconds,
            IReadOnlyList<StreamSpec> streams)
        {
            Name = name;
            Config = config;
            Limit = limit;
            TimeoutSeconds = timeoutSeconds;
            Streams = streams;
        }

        public string Name { get; }

        /// <summary>Public config values; secret-backed values are merged in at run time.</summary>
        public JsonElement Config { get; }

        /// <summary>
        /// Maximum number of records to consume. The harness stops the connector once the cap is reached,
        /// because connectors reject the CDK's <c>_limit</c> key when their spec forbids unknown properties.
        /// </summary>
        public int? Limit { get; }

        public int TimeoutSeconds { get; }

        public IReadOnlyList<StreamSpec> Streams { get; }
    }

    /// <summary>
    /// A stream to read and the assertions to make about its output.
    /// </summary>
    public sealed record StreamSpec(
        string Name,
        string SyncMode = "full_refresh",
        JsonElement? State = null,
        int MinRecords = 1,
        string[]? CursorField = null);
}