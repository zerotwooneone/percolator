using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Apps.Chat
{
    public interface IActingAdminResolver
    {
        Task<Guid?> GetActingAdminPeerIdAsync(Guid conversationId, CancellationToken ct);
    }
}
