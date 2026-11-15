using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application.Delivery.Ports;

/// <summary>
/// Opens authenticated mailbox streams against a relay. Separated from
/// <see cref="IRelayMailboxProvisioner"/> (registration/prekey publication) so the
/// streaming lifecycle is owned by a dedicated adapter.
/// </summary>
public interface IRelayMailboxClient
{
    Task<DomainResult<IMailboxStreamSession>> OpenMailboxStreamAsync(
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default);
}
