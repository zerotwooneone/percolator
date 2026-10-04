using System.Collections.Concurrent;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Handshake;

/// <summary>
/// In-memory sliding-window cache for tracking recently observed handshake ephemeral keys.
/// Discards duplicate invitations before computing expensive Curve25519 scalar multiplications.
/// </summary>
public sealed class MemoryHandshakeReplayFilter : IHandshakeReplayFilter
{
    public const int DefaultCapacity = 5000;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<DhPublicKey, DateTimeOffset> _seenKeys = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public MemoryHandshakeReplayFilter(int capacity = DefaultCapacity, TimeSpan? ttl = null)
    {
        _capacity = capacity;
        _ttl = ttl ?? DefaultTtl;
    }

    public bool TryRecordAndValidate(DhPublicKey ephemeralKey, DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(ephemeralKey);

        PruneExpired(receivedAtUtc);

        if (_seenKeys.ContainsKey(ephemeralKey))
        {
            return false; // Replay detected!
        }

        if (_seenKeys.Count >= _capacity)
        {
            // Bounded eviction: remove oldest
            var oldest = _seenKeys.OrderBy(x => x.Value).FirstOrDefault();
            if (oldest.Key != null)
            {
                _seenKeys.TryRemove(oldest.Key, out _);
            }
        }

        return _seenKeys.TryAdd(ephemeralKey, receivedAtUtc);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        if (_seenKeys.Count == 0) return;

        var threshold = now - _ttl;
        foreach (var entry in _seenKeys)
        {
            if (entry.Value < threshold)
            {
                _seenKeys.TryRemove(entry.Key, out _);
            }
        }
    }
}
