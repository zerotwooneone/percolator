using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

public sealed class DirectRatchetSession : AggregateRoot<SessionId>, ISensitiveSecret
{
    public const uint MaxSkipThreshold = 2000;
    public const int MaxTotalSkippedKeys = 1000;

    public override SessionId Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public DeviceId OwnerDeviceId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public DeviceId RemoteDeviceId { get; }

    private ChainKey? _rootKey;
    private EphemeralPrivateKey? _localEphemeralPrivateKey;
    public IdentityPublicKey? LocalEphemeralPublicKey { get; private set; }
    public IdentityPublicKey? RemoteEphemeralPublicKey { get; private set; }

    private ChainKey? _sendingChainKey;
    private ChainKey? _receivingChainKey;
    private readonly Dictionary<uint, MessageKey> _skippedMessageKeys = [];
    private readonly Queue<uint> _skippedKeyOrder = new();

    public uint SendingCounter { get; private set; }
    public uint ReceivingCounter { get; private set; }
    public uint PreviousSendingChainLength { get; private set; }
    public bool IsZeroized { get; private set; }

    public DirectRatchetSession(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        ChainKey? rootKey,
        ChainKey? sendingChainKey,
        ChainKey? receivingChainKey,
        IdentityPublicKey? remoteEphemeralPublicKey = null,
        EphemeralPrivateKey? localEphemeralPrivateKey = null,
        IdentityPublicKey? localEphemeralPublicKey = null,
        uint sendingCounter = 0,
        uint receivingCounter = 0,
        SessionId? sessionId = null)
    {
        Id = sessionId ?? SessionId.New();
        OwnerIdentityId = ownerIdentityId;
        OwnerDeviceId = ownerDeviceId;
        RemotePeerId = remotePeerId;
        RemoteDeviceId = remoteDeviceId;

        _rootKey = rootKey != null ? ChainKey.FromSpan(rootKey.Span) : null;
        _sendingChainKey = sendingChainKey != null ? ChainKey.FromSpan(sendingChainKey.Span) : null;
        _receivingChainKey = receivingChainKey != null ? ChainKey.FromSpan(receivingChainKey.Span) : null;
        _localEphemeralPrivateKey = localEphemeralPrivateKey != null ? EphemeralPrivateKey.FromSpan(localEphemeralPrivateKey.Span) : null;

        LocalEphemeralPublicKey = localEphemeralPublicKey;
        RemoteEphemeralPublicKey = remoteEphemeralPublicKey;

        SendingCounter = sendingCounter;
        ReceivingCounter = receivingCounter;
    }

    public DomainResult<(uint MessageCounter, MessageKey Key, IdentityPublicKey? EphemeralPublicKey)> StepSendingChain(ICryptoEngine engine)
    {
        if (IsZeroized || _sendingChainKey == null)
        {
            return DomainResult<(uint, MessageKey, IdentityPublicKey?)>.Failure(new DomainError("INVALID_SESSION_STATE", "Sending chain key is not available or has been zeroized."));
        }

        var previousChainKey = _sendingChainKey;
        var (nextChainKey, messageKey) = engine.StepRatchet(previousChainKey);

        _sendingChainKey = nextChainKey;
        previousChainKey.Dispose();

        uint counter = SendingCounter++;
        return DomainResult<(uint, MessageKey, IdentityPublicKey?)>.Success((counter, messageKey, LocalEphemeralPublicKey));
    }

    public DomainResult<(uint MessageCounter, MessageKey Key)> StepReceivingChain(ICryptoEngine engine, uint targetCounter)
    {
        if (IsZeroized || _receivingChainKey == null)
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
            var prevKey = _receivingChainKey;
            var (nextChainKey, skippedMessageKey) = engine.StepRatchet(prevKey);

            _receivingChainKey = nextChainKey;
            prevKey.Dispose();

            StoreSkippedKey(ReceivingCounter, skippedMessageKey);
            ReceivingCounter++;
        }

        // Step for the targetCounter itself
        var currentKey = _receivingChainKey;
        var (nextTargetChainKey, targetMessageKey) = engine.StepRatchet(currentKey);

        _receivingChainKey = nextTargetChainKey;
        currentKey.Dispose();

        uint current = ReceivingCounter++;
        return DomainResult<(uint, MessageKey)>.Success((current, targetMessageKey));
    }

    public DomainResult StepDhRatchet(IdentityPublicKey newRemoteEphemeralKey, ICryptoEngine engine)
    {
        if (IsZeroized || _rootKey == null)
        {
            return DomainResult.Failure(new DomainError("INVALID_SESSION_STATE", "Root key is not available or has been zeroized."));
        }

        if (newRemoteEphemeralKey == null)
        {
            return DomainResult.Failure(new DomainError("NULL_EPHEMERAL_KEY", "Remote ephemeral public key cannot be null."));
        }

        if (RemoteEphemeralPublicKey != null && RemoteEphemeralPublicKey == newRemoteEphemeralKey)
        {
            // Already ratcheted to this ephemeral key
            return DomainResult.Success();
        }

        // 1. DH Receive step using existing local private key and new remote public key
        if (_localEphemeralPrivateKey != null)
        {
            using var dhRecv = engine.ComputeDiffieHellman(_localEphemeralPrivateKey.Span, newRemoteEphemeralKey.Span);
            var prevRoot = _rootKey;
            var (nextRootRecv, receivingChainKey) = engine.KdfRk(prevRoot, dhRecv);

            _rootKey = nextRootRecv;
            prevRoot.Dispose();

            _receivingChainKey?.Dispose();
            _receivingChainKey = receivingChainKey;
        }

        // 2. Generate new local ephemeral keypair
        var (newLocalPriv, newLocalPub) = engine.GenerateEphemeralKeyPair();
        _localEphemeralPrivateKey?.Dispose();
        _localEphemeralPrivateKey = newLocalPriv;
        LocalEphemeralPublicKey = newLocalPub;

        // 3. DH Send step using new local private key and new remote public key
        using var dhSend = engine.ComputeDiffieHellman(_localEphemeralPrivateKey.Span, newRemoteEphemeralKey.Span);
        var intermediateRoot = _rootKey;
        var (nextRootSend, sendingChainKey) = engine.KdfRk(intermediateRoot, dhSend);

        _rootKey = nextRootSend;
        intermediateRoot.Dispose();

        _sendingChainKey?.Dispose();
        _sendingChainKey = sendingChainKey;

        // 4. Update state
        PreviousSendingChainLength = SendingCounter;
        SendingCounter = 0;
        ReceivingCounter = 0;
        RemoteEphemeralPublicKey = newRemoteEphemeralKey;

        return DomainResult.Success();
    }

    private void StoreSkippedKey(uint counter, MessageKey key)
    {
        // Enforce total upper limit with O(1) LRU queue eviction
        if (_skippedMessageKeys.Count >= MaxTotalSkippedKeys)
        {
            while (_skippedKeyOrder.Count > 0)
            {
                uint oldestCounter = _skippedKeyOrder.Dequeue();
                if (_skippedMessageKeys.Remove(oldestCounter, out var evictedKey))
                {
                    evictedKey.Dispose();
                    break;
                }
            }
        }

        _skippedMessageKeys[counter] = key;
        _skippedKeyOrder.Enqueue(counter);
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
        _rootKey?.Dispose();
        _rootKey = null;

        _localEphemeralPrivateKey?.Dispose();
        _localEphemeralPrivateKey = null;

        _sendingChainKey?.Dispose();
        _sendingChainKey = null;

        _receivingChainKey?.Dispose();
        _receivingChainKey = null;

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
