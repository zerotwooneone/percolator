using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Chat.App
{
    public interface ISelfIdentityProvider
    {
        Task<Guid> GetPeerIdAsync(int selfIdentityId, CancellationToken cancellationToken);
    }
}
