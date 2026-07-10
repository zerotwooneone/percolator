using System.Threading.Channels;

namespace Percolator.Infrastructure.Network.Grpc;

public interface IRelayGroupStreamDispatcher
{
    Task DispatchAsync(Guid conversationId, ReadOnlyMemory<byte> ciphertext, uint epoch, Guid senderPublicIdentityId, CancellationToken ct);
    ChannelReader<Percolator.Contracts.GroupStreamResponse> RegisterStream(Guid conversationId, Guid publicIdentityId);
    void UnregisterStream(Guid conversationId, Guid publicIdentityId);
}
