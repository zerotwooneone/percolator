namespace Percolator.Cryptography.GroupLedger;

/// <summary>
/// Aggregate representing a cache of messages with unknown sender keys.
/// Used for FIFO eviction when a sender key is missing during decryption.
/// </summary>
public sealed class UnknownMessageCache
{
    private const int MaxCapacity = 100;

    public long Id { get; }
    public GroupId GroupId { get; }
    public uint MissingKeyId { get; }
    public Ciphertext Ciphertext { get; }
    public DateTimeOffset ReceivedAtUtc { get; }

    public UnknownMessageCache(
        long id,
        GroupId groupId,
        uint missingKeyId,
        Ciphertext ciphertext,
        DateTimeOffset receivedAtUtc)
    {
        Id = id;
        GroupId = groupId;
        MissingKeyId = missingKeyId;
        Ciphertext = ciphertext;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>
    /// Determines if the cache entry should be evicted based on FIFO policy.
    /// </summary>
    /// <param name="currentCacheSize">Current number of entries in the cache.</param>
    /// <param name="oldestReceivedAtUtc">Received timestamp of the oldest entry in the cache.</param>
    /// <returns>True if this entry should be evicted.</returns>
    public bool ShouldEvict(int currentCacheSize, DateTimeOffset oldestReceivedAtUtc)
    {
        if (currentCacheSize <= MaxCapacity)
        {
            return false;
        }

        // FIFO: evict the oldest entries first
        return ReceivedAtUtc == oldestReceivedAtUtc;
    }
}
