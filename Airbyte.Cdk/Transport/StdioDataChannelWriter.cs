#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// The original STDIO/JSON transport: one JSON object per line on stdout.
    /// </summary>
    public sealed class StdioDataChannelWriter : IDataChannelWriter
    {
        private readonly TextWriter _output;
        private readonly object _sync = new();

        public StdioDataChannelWriter(TextWriter output) => _output = output;

        public ValueTask WriteAsync(AirbyteMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sync)
                _output.WriteLine(JsonSerializer.Serialize(message, AirbyteProtocolJson.Options));

            return ValueTask.CompletedTask;
        }

        public Task CompleteAsync(Exception? error = null) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
