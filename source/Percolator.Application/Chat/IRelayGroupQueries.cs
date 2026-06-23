using Percolator.Chat.GroupLedger;
using Percolator.Cryptography.GroupLedger;

namespace Percolator.Application.Chat;

public sealed record RelayGroupStateDto(uint Epoch, Percolator.Chat.GroupLedger.RelayGroupPublicParamsBytes GroupPublicParams);

public interface IRelayGroupQueries
{
    Task<RelayGroupStateDto?> GetGroupStateAsync(Guid conversationId, CancellationToken cancellationToken);
}
