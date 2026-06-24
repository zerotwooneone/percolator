using Percolator.Chat.GroupLedger;

namespace Percolator.Application.Chat;

public sealed record RelayGroupStateDto(uint Epoch, RelayGroupPublicParamsBytes GroupPublicParams);

public interface IRelayGroupQueries
{
    Task<RelayGroupStateDto?> GetGroupStateAsync(Guid conversationId, CancellationToken cancellationToken);
}
