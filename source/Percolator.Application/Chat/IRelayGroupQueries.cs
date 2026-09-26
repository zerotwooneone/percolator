using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Network.ValueObjects;

namespace Percolator.Application.Chat;

public sealed record RelayGroupStateDto(RelayGroupEpoch Epoch, EncryptedEntriesBlobBytes EncryptedEntriesBlob);

public interface IRelayGroupQueries
{
    Task<RelayGroupStateDto?> GetGroupStateAsync(ConversationId conversationId, CancellationToken cancellationToken);
}
