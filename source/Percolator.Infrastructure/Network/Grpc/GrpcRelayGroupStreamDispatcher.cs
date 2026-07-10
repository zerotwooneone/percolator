using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Percolator.Infrastructure.Network.Grpc;

internal sealed class GrpcRelayGroupStreamDispatcher : IRelayGroupStreamDispatcher
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<Percolator.Contracts.GroupStreamResponse>>> _streamMatrix = new();

    public Task DispatchAsync(Guid conversationId, ReadOnlyMemory<byte> ciphertext, uint epoch, Guid senderPublicIdentityId, CancellationToken ct)
    {
        if (!_streamMatrix.TryGetValue(conversationId, out var conversationStreams))
        {
            // No streams registered for this conversation, nothing to dispatch
            return Task.CompletedTask;
        }

        var response = new Percolator.Contracts.GroupStreamResponse
        {
            ConversationId = Google.Protobuf.ByteString.CopyFrom(conversationId.ToByteArray()),
            Ciphertext = Google.Protobuf.ByteString.CopyFrom(ciphertext.ToArray()),
            Epoch = epoch,
            SenderPublicIdentityId = Google.Protobuf.ByteString.CopyFrom(senderPublicIdentityId.ToByteArray())
        };

        foreach (var (_, channel) in conversationStreams)
        {
            channel.Writer.TryWrite(response);
        }

        return Task.CompletedTask;
    }

    public ChannelReader<Percolator.Contracts.GroupStreamResponse> RegisterStream(Guid conversationId, Guid publicIdentityId)
    {
        var conversationStreams = _streamMatrix.GetOrAdd(conversationId, _ => new ConcurrentDictionary<Guid, Channel<Percolator.Contracts.GroupStreamResponse>>());
        
        var channel = Channel.CreateUnbounded<Percolator.Contracts.GroupStreamResponse>();
        conversationStreams.TryAdd(publicIdentityId, channel);
        
        return channel.Reader;
    }

    public void UnregisterStream(Guid conversationId, Guid publicIdentityId)
    {
        if (_streamMatrix.TryGetValue(conversationId, out var conversationStreams))
        {
            if (conversationStreams.TryRemove(publicIdentityId, out var channel))
            {
                channel.Writer.Complete();
            }

            // Clean up empty conversation entries
            if (conversationStreams.IsEmpty)
            {
                _streamMatrix.TryRemove(conversationId, out _);
            }
        }
    }
}
