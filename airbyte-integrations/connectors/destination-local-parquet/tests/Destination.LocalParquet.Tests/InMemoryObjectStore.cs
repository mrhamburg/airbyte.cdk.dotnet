using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using destination_local_parquet.OpenDal;

namespace Destination.LocalParquet.Tests
{
    internal sealed class InMemoryObjectStore : IObjectStore
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, byte[]> Objects => _objects;

        public IReadOnlyList<string> Keys =>
            _objects.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        {
            _objects[key] = content;
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            _objects.TryGetValue(key, out var value)
                ? Task.FromResult(value)
                : Task.FromException<byte[]>(new FileNotFoundException(key));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string prefix,
            CancellationToken cancellationToken = default)
        {
            var normalized = prefix.Trim('/');
            var matches = _objects.Keys
                .Where(x => normalized.Length == 0 || x.StartsWith(normalized + "/", StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(matches);
        }

        public void Dispose()
        {
        }
    }
}
