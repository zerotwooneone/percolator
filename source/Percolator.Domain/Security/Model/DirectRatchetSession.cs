using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

public sealed class DirectRatchetSession : AggregateRoot<Guid>, ISensitiveSecret
{
    private const uint MaxSkipThreshold = 2000;

    public override Guid Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public DeviceId OwnerDeviceId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public DeviceId RemoteDeviceId { get; }

    private byte[]? _sendingChainKeyBytes;
    private byte[]? _receivingChainKeyBytes;
    private readonly Dictionary<uint, MessageKey> _skippedMessageKeys = [];

    public uint SendingCounter { get; private set; }
    public uint ReceivingCounter { get; private set; }
    public bool IsZeroized { get; private set; }

    public DirectRatchetSession(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        ChainKey? sendingChainKey,
        ChainKey? receivingChainKey,
        uint sendingCounter = 0,
        uint receivingCounter = 0,
        Guid? sessionId = null)
    {
        Id = sessionId ?? Guid.NewGuid();
        OwnerIdentityId = ownerIdentityId;
        OwnerDeviceId = ownerDeviceId;
        RemotePeerId = remotePeerId;
        RemoteDeviceId = remoteDeviceId;

        if (sendingChainKey != null)
        {
            _sendingChainKeyBytes = sendingChainKey.Span.ToArray();
        }

        if (receivingChainKey != null)
        {
            _receivingChainKeyBytes = receivingChainKey.Span.ToArray();
        }

        SendingCounter = sendingCounter;
        ReceivingCounter = receivingCounter;
    }

    public DomainResult<(uint MessageCounter, MessageKey Key)> StepSendingChain(ICryptoEngine engine)
    {
        if (IsZeroized || _sendingChainKeyBytes == null)
        {
            return DomainResult<(uint, MessageKey)>.Failure(new DomainError("INVALID_SESSION_STATE", "Sending chain key is not available or has been zeroized."));
        }

        var currentChainKey = ChainKey.FromSpan(_sendingChainKeyBytes);
        var (nextChainKey, messageKey) = engine.StepRatchet(currentChainKey);

        CryptographicOperations.ZeroMemory(_sendingChainKeyBytes);
        _sendingChainKeyBytes = nextChainKey.Span.ToArray();

        uint counter = SendingCounter++;
        return DomainResult<(uint, MessageKey)>.Success((counter, messageKey));
    }

    public DomainResult<(uint MessageCounter, MessageKey Key)> StepReceivingChain(ICryptoEngine engine, uint targetCounter)
    {
        if (IsZeroized || _receivingChainKeyBytes == null)
        {
            return DomainResult<(uint, MessageKey)>.Failure(new DomainError("INVALID_SESSION_STATE", "Receiving chain key is not available or has been zeroized."));
        }

        if (targetCounter < ReceivingCounter)
        {
            return DomainResult<(uint, MessageKey)>.Failure(new DomainError("COUNTER_ALREADY_PASSED", $"Counter {targetCounter} is behind receiving counter {ReceivingCounter}."));
        }

        if (targetCounter - ReceivingCounter > MaxSkipThreshold)
        {
            return DomainResult<(uint, MessageKey)>.Failure(new DomainError("SKIP_THRESHOLD_EXCEEDED", $"Skipping {targetCounter - ReceivingCounter} messages exceeds max threshold of {MaxSkipThreshold}."));
        }

        // Cache all skipped keys between current ReceivingCounter and targetCounter
        while (ReceivingCounter < targetCounter)
        {
            var currentChainKey = ChainKey.FromSpan(_receivingChainKeyBytes);
            var (nextChainKey, skippedMessageKey) = engine.StepRatchet(currentChainKey);

            CryptographicOperations.ZeroMemory(_receivingChainKeyBytes);
            _receivingChainKeyBytes = nextChainKey.Span.ToArray();

            _skippedMessageKeys[ReceivingCounter] = skippedMessageKey;
            ReceivingCounter++;
        }

        // Step for the targetCounter itself
        var targetChainKey = ChainKey.FromSpan(_receivingChainKeyBytes);
        var (nextTargetChainKey, targetMessageKey) = engine.StepRatchet(targetChainKey);

        CryptographicOperations.ZeroMemory(_receivingChainKeyBytes);
        _receivingChainKeyBytes = nextTargetChainKey.Span.ToArray();

        uint current = ReceivingCounter++;
        return DomainResult<(uint, MessageKey)>.Success((current, targetMessageKey));
    }

    public bool HasSkippedKey(uint counter) => _skippedMessageKeys.ContainsKey(counter);

    public DomainResult<MessageKey> TryConsumeSkippedKey(uint counter)
    {
        if (_skippedMessageKeys.Remove(counter, out var key))
        {
            return DomainResult<MessageKey>.Success(key);
        }

        return DomainResult<MessageKey>.Failure(new DomainError("KEY_NOT_FOUND", $"No skipped key found for counter {counter}."));
    }

    public void Zeroize()
    {
        if (_sendingChainKeyBytes != null)
        {
            CryptographicOperations.ZeroMemory(_sendingChainKeyBytes);
            _sendingChainKeyBytes = null;
        }

        if (_receivingChainKeyBytes != null)
        {
            CryptographicOperations.ZeroMemory(_receivingChainKeyBytes);
            _receivingChainKeyBytes = null;
        }

        _skippedMessageKeys.Clear();
        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
