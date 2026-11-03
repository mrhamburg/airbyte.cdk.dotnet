#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// JSONL input from a text reader, used by STDIO and by the destination's control channel.
    /// </summary>
    public sealed class StdioDataChannelReader : IDataChannelReader
    {
        private readonly TextReader _input;
        private readonly System.Action<string>? _onInvalid;

        public StdioDataChannelReader(TextReader input, System.Action<string>? onInvalid = null)
        {
            _input = input;
            _onInvalid = onInvalid;
        }

        public async IAsyncEnumerable<AirbyteMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await _input.ReadLineAsync(cancellationToken);
                if (line == null)
                    yield break;

                if (AirbyteProtocolReader.TryParseMessage(line, out var message) && message != null)
                    yield return message;
                else
                    _onInvalid?.Invoke("Dropping input that is not a valid AirbyteMessage");
            }
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }
}
