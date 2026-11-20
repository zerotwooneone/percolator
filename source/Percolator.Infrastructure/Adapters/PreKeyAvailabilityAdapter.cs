namespace Percolator.Infrastructure.Adapters;

using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;

public sealed class PreKeyAvailabilityAdapter : IPreKeyAvailabilityPort
{
    private readonly IRelayPreKeyRepository _relayPreKeyRepository;

    public PreKeyAvailabilityAdapter(IRelayPreKeyRepository relayPreKeyRepository)
    {
        _relayPreKeyRepository = relayPreKeyRepository ?? throw new ArgumentNullException(nameof(relayPreKeyRepository));
    }

    public async ValueTask<bool> HasActiveSignedPreKeyAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        return await _relayPreKeyRepository.HasActivePreKeyAsync(routingToken, ct).ConfigureAwait(false);
    }
}
