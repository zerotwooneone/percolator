using System.Collections.Concurrent;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class InMemoryRelayProfileRepository : IRelayProfileRepository
{
    private readonly ConcurrentDictionary<BlindedProfileLocator, RelayHostedProfileSlot> _slots = new();

    public ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default)
    {
        _slots.TryGetValue(locator, out var slot);
        return ValueTask.FromResult(slot);
    }

    public ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        _slots[slot.Id] = slot;
        return ValueTask.FromResult(DomainResult.Success());
    }

    public ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default)
    {
        if (!_slots.TryGetValue(locator, out var slot))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError("PROFILE_NOT_FOUND", "Profile slot not found.")));
        }

        if (slot.WriteToken != writeToken)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError("UNAUTHORIZED_PROFILE_WRITE", "Invalid write token for profile slot.")));
        }

        _slots.TryRemove(locator, out _);
        return ValueTask.FromResult(DomainResult.Success());
    }
}
