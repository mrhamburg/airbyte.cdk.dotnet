using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace destination_opendal_parquet.OpenDal
{
    internal interface IObjectStore : IDisposable
    {
        Task EnsureReadyAsync(CancellationToken cancellationToken = default);

        Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken = default);

        Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default);

        Task DeleteAsync(string key, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> ListFilesAsync(string prefix, CancellationToken cancellationToken = default);
    }
}
