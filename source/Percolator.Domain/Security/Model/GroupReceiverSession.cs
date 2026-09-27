using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

/// <summary>
/// Aggregate root representing the recipient-side tracking of a remote peer's group sender key chain.
/// Caches skipped message keys for out-of-order delivery with bounded LRU eviction and zero-allocation cleanup.
/// </summary>
public sealed class GroupReceiverSession : AggregateRoot<SessionId>, ISensitiveSecret
{
    public const uint MaxSkipThreshold = 2000;
    public const int MaxTotalSkippedKeys = 1000;

    public override SessionId Id { get; }
    public ConversationId ConversationId { get; }
    public PublicIdentityId AuthorId { get; }
    public DeviceId AuthorDeviceId { get; }

    private ChainKey? _chainKey;
    public uint ReceivingCounter { get; private set; }
    public bool IsZeroized { get; private set; }

    private readonly Dictionary<uint, MessageKey> _skippedMessageKeys = [];
    private readonly Queue<uint> _skippedKeyOrder = new();

    public GroupReceiverSession(
        ConversationId conversationId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ChainKey initialChainKey,
        uint initialIteration = 0,
        SessionId? id = null)
    {
        Id = id ?? SessionId.New();
        ConversationId = conversationId;
        AuthorId = authorId;
        AuthorDeviceId = authorDeviceId;
        _chainKey = ChainKey.FromSpan(initialChainKey.Span);
        ReceivingCounter = initialIteration;
    }

    public DomainResult<MessageKey> AdvanceTo(uint targetIteration, ICryptoEngine engine)
    {
        if (IsZeroized || _chainKey == null)
        {
            return DomainResult<MessageKey>.Failure(new DomainError("INVALID_SESSION_STATE", "Group receiver session has been zeroized or is invalid."));
        }

        if (targetIteration < ReceivingCounter)
        {
            // Check if we have this key cached in skipped keys
            if (_skippedMessageKeys.Remove(targetIteration, out var skippedKey))
            {
                return DomainResult<MessageKey>.Success(skippedKey);
            }

            return DomainResult<MessageKey>.Failure(new DomainError(
                "COUNTER_ALREADY_PASSED",
                $"Iteration {targetIteration} is behind receiving counter {ReceivingCounter} and key is not cached."));
        }

        if (targetIteration - ReceivingCounter > MaxSkipThreshold)
        {
            return DomainResult<MessageKey>.Failure(new DomainError(
                "SKIP_THRESHOLD_EXCEEDED",
                $"Skipping {targetIteration - ReceivingCounter} messages exceeds max threshold of {MaxSkipThreshold}."));
        }

        // Cache intermediate keys
        while (ReceivingCounter < targetIteration)
        {
            var prevKey = _chainKey;
            var (nextChainKey, skippedMessageKey) = engine.StepRatchet(prevKey);

            _chainKey = nextChainKey;
            prevKey.Dispose();

            StoreSkippedKey(ReceivingCounter, skippedMessageKey);
            ReceivingCounter++;
        }

        // Derive target key
        var currentKey = _chainKey;
        var (nextTargetChainKey, targetMessageKey) = engine.StepRatchet(currentKey);

        _chainKey = nextTargetChainKey;
        currentKey.Dispose();

        ReceivingCounter++;
        return DomainResult<MessageKey>.Success(targetMessageKey);
    }

    public bool HasSkippedKey(uint iteration) => _skippedMessageKeys.ContainsKey(iteration);

    public DomainResult<MessageKey> TryConsumeSkippedKey(uint iteration)
    {
        if (_skippedMessageKeys.Remove(iteration, out var key))
        {
            return DomainResult<MessageKey>.Success(key);
        }

        return DomainResult<MessageKey>.Failure(new DomainError("KEY_NOT_FOUND", $"No skipped key found for iteration {iteration}."));
    }

    private void StoreSkippedKey(uint iteration, MessageKey key)
    {
        if (_skippedMessageKeys.Count >= MaxTotalSkippedKeys)
        {
            while (_skippedKeyOrder.Count > 0)
            {
                uint oldest = _skippedKeyOrder.Dequeue();
                if (_skippedMessageKeys.Remove(oldest, out var evicted))
                {
                    evicted.Dispose();
                    break;
                }
            }
        }

        _skippedMessageKeys[iteration] = key;
        _skippedKeyOrder.Enqueue(iteration);
    }

    public void Zeroize()
    {
        if (_chainKey != null)
        {
            _chainKey.Dispose();
            _chainKey = null;
        }

        foreach (var key in _skippedMessageKeys.Values)
        {
            key.Dispose();
        }
        _skippedMessageKeys.Clear();
        _skippedKeyOrder.Clear();

        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
