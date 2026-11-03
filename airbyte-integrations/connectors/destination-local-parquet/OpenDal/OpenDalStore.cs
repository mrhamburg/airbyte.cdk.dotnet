using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenDAL;
using OpenDAL.Options;

namespace destination_local_parquet.OpenDal
{
    internal sealed class OpenDalStore : IObjectStore
    {
        private readonly Operator _operator;
        private readonly string _prefix;

        public OpenDalStore(OpenDalConfig config, string prefix)
        {
            _operator = config.CreateOperator();
            _prefix = prefix;
        }

        public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            var key = ObjectKey.Combine(_prefix, $".airbyte-write-test-{Guid.NewGuid():N}");
            var payload = Encoding.UTF8.GetBytes("airbyte");
            try
            {
                await _operator.WriteAsync(key, payload, cancellationToken: cancellationToken);
                var read = await _operator.ReadAsync(key, cancellationToken: cancellationToken);
                if (!payload.SequenceEqual(read))
                    throw new IOException($"OpenDAL service did not return the bytes written to '{key}'.");
                await _operator.ListAsync(_prefix, new ListOptions { Recursive = true }, cancellationToken);
            }
            finally
            {
                try
                {
                    await _operator.DeleteAsync(key, cancellationToken: CancellationToken.None);
                }
                catch (OpenDALException)
                {
                    // The probe is best effort after the check result is known.
                }
            }
        }

        public Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken = default) =>
            _operator.WriteAsync(key, content, cancellationToken: cancellationToken);

        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            _operator.ReadAsync(key, cancellationToken: cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            _operator.DeleteAsync(key, cancellationToken: cancellationToken);

        public async Task<IReadOnlyList<string>> ListFilesAsync(string prefix,
            CancellationToken cancellationToken = default)
        {
            var entries = await _operator.ListAsync(prefix, new ListOptions { Recursive = true }, cancellationToken);
            return entries
                .Where(x => !x.Path.EndsWith("/", StringComparison.Ordinal))
                .Select(x => x.Path)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        public void Dispose() => _operator.Dispose();
    }
}
