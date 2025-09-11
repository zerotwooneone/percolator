using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Apps.Chat
{
    public interface IAtRestKeyProvider
    {
        Task<byte[]> GetMasterKeyAsync(CancellationToken ct);
    }
}
