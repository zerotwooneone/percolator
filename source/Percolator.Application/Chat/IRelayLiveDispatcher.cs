using Percolator.Chat.Messaging.ValueObjects;
using System.Threading.Channels;
using Percolator.Chat.GroupLedger;
using Percolator.Contracts;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;

namespace Percolator.Application.Chat;

public interface IRelayLiveDispatcher
{
    void RegisterStream(PublicIdentityId publicIdentityId, Channel<ServerRelayStream> channel);
    void UnregisterStream(PublicIdentityId publicIdentityId);

    Task PushGroupMessageAsync(
        ConversationId conversationId,
        uint senderKeyId,
        CiphertextBytes ciphertext,
        IReadOnlyList<PublicIdentityId> targetIdentities,
        CancellationToken ct);

    Task PushOpaqueMessageAsync(
        PublicIdentityId target,
        Guid ackId,
        byte[] payload,
        CancellationToken ct);
}
