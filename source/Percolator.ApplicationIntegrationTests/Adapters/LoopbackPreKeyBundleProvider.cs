using Percolator.Application2.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class LoopbackPreKeyBundleProvider : IPreKeyBundleProvider
{
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;

    public LoopbackPreKeyBundleProvider(InMemoryLoopbackNetworkSwitch networkSwitch)
    {
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
    }

    public async Task<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
        PublicIdentityId targetPeerId,
        DeviceId? deviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        var targetDevice = deviceId ?? DeviceId.Primary;

        RelayNode? relay = null;
        if (relayEndpoint != null)
        {
            relay = _networkSwitch.AllRelays.FirstOrDefault(r =>
                $"localhost:{r.Port.Value}".Equals(relayEndpoint.Value, StringComparison.OrdinalIgnoreCase) ||
                r.RelayIdentityId.ToString().Equals(relayEndpoint.Value, StringComparison.OrdinalIgnoreCase) ||
                (relayEndpoint.Port.HasValue && r.Port.Value == relayEndpoint.Port.Value));
        }

        relay ??= _networkSwitch.AllRelays.FirstOrDefault();
        if (relay == null)
        {
            return DomainResult<PreKeyBundle>.Failure(new DomainError("RELAY_NOT_FOUND", "No relay found on network switch."));
        }

        var hostedBundle = await relay.PreKeyRepository.GetAsync(targetPeerId, targetDevice, ct).ConfigureAwait(false);
        if (hostedBundle == null)
        {
            return DomainResult<PreKeyBundle>.Failure(new DomainError("PREKEY_BUNDLE_NOT_FOUND", $"No prekey bundle hosted for peer '{targetPeerId}'."));
        }

        return hostedBundle.ConsumeBundle();
    }
}
