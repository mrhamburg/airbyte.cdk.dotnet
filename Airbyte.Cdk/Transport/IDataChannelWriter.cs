#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Writes protocol messages to the platform. STDIO and socket mode implement this differently,
    /// but connectors always produce the same <see cref="AirbyteMessage"/> instances.
    /// </summary>
    public interface IDataChannelWriter : IAsyncDisposable
    {
        ValueTask WriteAsync(AirbyteMessage message, CancellationToken cancellationToken = default);

        Task CompleteAsync(Exception? error = null);
    }
}
