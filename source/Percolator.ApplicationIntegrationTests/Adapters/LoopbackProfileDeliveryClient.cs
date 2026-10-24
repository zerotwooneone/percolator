using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class LoopbackProfileDeliveryClient : IProfileDeliveryClient
{
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;
    private readonly IDateTimeProvider _timeProvider;

    public LoopbackProfileDeliveryClient(InMemoryLoopbackNetworkSwitch networkSwitch, IDateTimeProvider timeProvider)
    {
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult<ProfileCiphertextPackage>> FetchProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        CancellationToken ct = default)
    {
        // 1. Check all registered Relays
        foreach (var relay in _networkSwitch.AllRelays)
        {
            var slot = await relay.ProfileRepository.GetSlotAsync(locator, ct).ConfigureAwait(false);
            if (slot is not null)
            {
                return slot.TryGet(_timeProvider);
            }
        }

        // 2. Check all registered peer local profile stores (for Direct P2P)
        foreach (var store in _networkSwitch.AllPeerProfileStores)
        {
            var slot = await store.GetSlotAsync(locator, ct).ConfigureAwait(false);
            if (slot is not null)
            {
                return slot.TryGet(_timeProvider);
            }
        }

        return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError("PROFILE_NOT_FOUND", "Profile slot not found."));
    }

    public async ValueTask<DomainResult> UploadProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct = default)
    {
        var targetRelays = _networkSwitch.AllRelays;
        if (targetRelays.Count == 0)
        {
            return DomainResult.Failure(new DomainError("NO_RELAY_AVAILABLE", "No relay registered on network switch."));
        }

        foreach (var relay in targetRelays)
        {
            var existing = await relay.ProfileRepository.GetSlotAsync(locator, ct).ConfigureAwait(false);
            if (existing is null)
            {
                var createResult = RelayHostedProfileSlot.Create(locator, writeToken, package, revision, expiresAtUtc);
                if (createResult.IsFailure) return DomainResult.Failure(createResult.Error);
                await relay.ProfileRepository.SaveSlotAsync(createResult.Value!, ct).ConfigureAwait(false);
            }
            else
            {
                var updateResult = existing.Update(writeToken, package, revision, expiresAtUtc);
                if (updateResult.IsFailure) return updateResult;
                await relay.ProfileRepository.SaveSlotAsync(existing, ct).ConfigureAwait(false);
            }
        }

        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> DeleteProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        CancellationToken ct = default)
    {
        bool deletedAny = false;
        foreach (var relay in _networkSwitch.AllRelays)
        {
            var result = await relay.ProfileRepository.DeleteSlotAsync(locator, writeToken, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                deletedAny = true;
            }
        }

        foreach (var store in _networkSwitch.AllPeerProfileStores)
        {
            var result = await store.DeleteSlotAsync(locator, writeToken, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                deletedAny = true;
            }
        }

        return deletedAny
            ? DomainResult.Success()
            : DomainResult.Failure(new DomainError("PROFILE_NOT_FOUND", "Profile slot not found for deletion."));
    }
}
