#nullable enable
using System;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Sources;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Per-process command context. Replaces the static connector/options/logger fields so a command
    /// can be constructed and tested without process-wide state.
    /// </summary>
    internal sealed class CliContext
    {
        public CliContext(Options options, Connector connector, AirbyteLogger logger)
        {
            Options = options;
            Connector = connector;
            Logger = logger;
        }

        public Options Options { get; }
        public Connector Connector { get; }
        public AirbyteLogger Logger { get; }

        public Source GetSource() =>
            Connector as Source
            ?? throw new InvalidOperationException(
                $"Could not instantiate Source as current type ({Connector.GetType().Name}) is not implementing {nameof(Source)}");

        public Destination GetDestination() =>
            Connector as Destination
            ?? throw new InvalidOperationException(
                $"Could not instantiate Destination as current type ({Connector.GetType().Name}) is not implementing {nameof(Destination)}");
    }
}
