using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application.Delivery;

/// <summary>
/// Placeholder <see cref="IRelayMailboxClient"/> for hosts with no transport adapter.
/// Stream opens report <c>RELAY_MAILBOX_CLIENT_UNAVAILABLE</c> so callers fail deterministically.
/// </summary>
public sealed class NullRelayMailboxClient : IRelayMailboxClient
{
    public Task<DomainResult<IMailboxStreamSession>> OpenMailboxStreamAsync(
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
        => Task.FromResult(DomainResult<IMailboxStreamSession>.Failure(
            new DomainError("RELAY_MAILBOX_CLIENT_UNAVAILABLE", "No relay mailbox client is configured.")));
}
