using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
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
    public ChannelId ChannelId { get; }
    public PublicIdentityId AuthorId { get; }
    public DeviceId AuthorDeviceId { get; }
    public uint KeyId { get; }
    public IdentityKey? AuthorSigningKey { get; }

    private ChainKey? _chainKey;
    public uint ReceivingCounter { get; private set; }
    public bool IsZeroized { get; private set; }

    private readonly Dictionary<uint, MessageKey> _skippedMessageKeys = [];
    private readonly Queue<uint> _skippedKeyOrder = new();

    public GroupReceiverSession(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ChainKey initialChainKey,
        uint initialIteration = 0,
        uint keyId = 1,
        IdentityKey? authorSigningKey = null,
        SessionId? id = null)
    {
        Id = id ?? SessionId.New();
        ChannelId = channelId;
        AuthorId = authorId;
        AuthorDeviceId = authorDeviceId;
        KeyId = keyId;
        AuthorSigningKey = authorSigningKey;
        _chainKey = ChainKey.FromSpan(initialChainKey.Span);
        ReceivingCounter = initialIteration;
    }

    public DomainResult<MessageKey> TryAdvanceToIteration(uint targetIteration, ICryptoEngine engine)
    {
        if (IsZeroized || _chainKey == null)
        {
            return DomainResult<MessageKey>.Failure(new DomainError("INVALID_SESSION_STATE", "Group receiver session has been zeroized."));
        }

        if (targetIteration < ReceivingCounter)
        {
            if (_skippedMessageKeys.Remove(targetIteration, out var skippedKey))
            {
                return DomainResult<MessageKey>.Success(skippedKey);
            }

            return DomainResult<MessageKey>.Failure(new DomainError("EXPIRED_OR_DUPLICATE_MESSAGE", "The message key for this iteration has already been used or was never cached."));
        }

        if (targetIteration - ReceivingCounter > MaxSkipThreshold)
        {
            return DomainResult<MessageKey>.Failure(new DomainError("MAX_SKIP_THRESHOLD_EXCEEDED", $"Cannot skip more than {MaxSkipThreshold} keys. Potential denial of service."));
        }

        while (ReceivingCounter < targetIteration)
        {
            var previousKey = _chainKey;
            var (nextChainKey, skippedMessageKey) = engine.StepRatchet(previousKey);
            previousKey.Dispose();
            _chainKey = nextChainKey;

            EvictOldestSkippedKeyIfFull();
            _skippedMessageKeys[ReceivingCounter] = skippedMessageKey;
            _skippedKeyOrder.Enqueue(ReceivingCounter);

            ReceivingCounter++;
        }

        var currentChain = _chainKey;
        var (finalChainKey, derivedMessageKey) = engine.StepRatchet(currentChain);
        currentChain.Dispose();
        _chainKey = finalChainKey;
        ReceivingCounter++;

        return DomainResult<MessageKey>.Success(derivedMessageKey);
    }

    public DomainResult VerifyAuthorSignature(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ICryptoEngine engine)
    {
        if (AuthorSigningKey == null)
        {
            return DomainResult.Failure(new DomainError("MISSING_SIGNING_KEY", "Author signing public key is not set."));
        }

        if (!engine.VerifyEd25519Signature(AuthorSigningKey, message, signature))
        {
            return DomainResult.Failure(new DomainError("INVALID_SIGNATURE", "Group message signature verification failed. Possible forgery."));
        }

        return DomainResult.Success();
    }

    private void EvictOldestSkippedKeyIfFull()
    {
        while (_skippedMessageKeys.Count >= MaxTotalSkippedKeys && _skippedKeyOrder.TryDequeue(out var oldestIteration))
        {
            if (_skippedMessageKeys.Remove(oldestIteration, out var evictedKey))
            {
                evictedKey.Dispose();
            }
        }
    }

    public void Zeroize()
    {
        if (IsZeroized)
        {
            return;
        }

        _chainKey?.Dispose();
        _chainKey = null;

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
