#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Reads protocol messages from the platform. The destination consumes one merged stream even
    /// when records arrive over several sockets.
    /// </summary>
    public interface IDataChannelReader
    {
        IAsyncEnumerable<AirbyteMessage> ReadAllAsync(CancellationToken cancellationToken = default);

        Task CompleteAsync();
    }
}
