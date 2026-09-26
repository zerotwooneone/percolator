using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

public sealed class DirectRatchetSession : AggregateRoot<Guid>, ISensitiveSecret
{
    public const uint MaxSkipThreshold = 2000;
    public const int MaxTotalSkippedKeys = 1000;

    public override Guid Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public DeviceId OwnerDeviceId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public DeviceId RemoteDeviceId { get; }

    private byte[]? _rootKeyBytes;
    private byte[]? _localEphemeralPrivateKey;
    public IdentityPublicKey? LocalEphemeralPublicKey { get; private set; }
    public IdentityPublicKey? RemoteEphemeralPublicKey { get; private set; }

    private byte[]? _sendingChainKeyBytes;
    private byte[]? _receivingChainKeyBytes;
    private readonly Dictionary<uint, MessageKey> _skippedMessageKeys = [];

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
        byte[]? localEphemeralPrivateKey = null,
        IdentityPublicKey? localEphemeralPublicKey = null,
        uint sendingCounter = 0,
        uint receivingCounter = 0,
        Guid? sessionId = null)
    {
        Id = sessionId ?? Guid.NewGuid();
        OwnerIdentityId = ownerIdentityId;
        OwnerDeviceId = ownerDeviceId;
        RemotePeerId = remotePeerId;
        RemoteDeviceId = remoteDeviceId;

        if (rootKey != null)
        {
            _rootKeyBytes = rootKey.Span.ToArray();
        }

        if (sendingChainKey != null)
        {
            _sendingChainKeyBytes = sendingChainKey.Span.ToArray();
        }

        if (receivingChainKey != null)
        {
            _receivingChainKeyBytes = receivingChainKey.Span.ToArray();
        }

        if (localEphemeralPrivateKey != null)
        {
            _localEphemeralPrivateKey = (byte[])localEphemeralPrivateKey.Clone();
        }

        LocalEphemeralPublicKey = localEphemeralPublicKey;
        RemoteEphemeralPublicKey = remoteEphemeralPublicKey;

        SendingCounter = sendingCounter;
        ReceivingCounter = receivingCounter;
    }

    public DomainResult<(uint MessageCounter, MessageKey Key, IdentityPublicKey? EphemeralPublicKey)> StepSendingChain(ICryptoEngine engine)
    {
        if (IsZeroized || _sendingChainKeyBytes == null)
        {
            return DomainResult<(uint, MessageKey, IdentityPublicKey?)>.Failure(new DomainError("INVALID_SESSION_STATE", "Sending chain key is not available or has been zeroized."));
        }

        var currentChainKey = ChainKey.FromSpan(_sendingChainKeyBytes);
        var (nextChainKey, messageKey) = engine.StepRatchet(currentChainKey);

        CryptographicOperations.ZeroMemory(_sendingChainKeyBytes);
        _sendingChainKeyBytes = nextChainKey.Span.ToArray();

        uint counter = SendingCounter++;
        return DomainResult<(uint, MessageKey, IdentityPublicKey?)>.Success((counter, messageKey, LocalEphemeralPublicKey));
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

            StoreSkippedKey(ReceivingCounter, skippedMessageKey);
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

    public DomainResult StepDhRatchet(IdentityPublicKey newRemoteEphemeralKey, ICryptoEngine engine)
    {
        if (IsZeroized || _rootKeyBytes == null)
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
            var dhRecv = engine.ComputeDiffieHellman(_localEphemeralPrivateKey, newRemoteEphemeralKey.Span);
            var currentRootKey = ChainKey.FromSpan(_rootKeyBytes);
            var (nextRootRecv, receivingChainKey) = engine.KdfRk(currentRootKey, dhRecv);

            CryptographicOperations.ZeroMemory(_rootKeyBytes);
            _rootKeyBytes = nextRootRecv.Span.ToArray();

            if (_receivingChainKeyBytes != null) CryptographicOperations.ZeroMemory(_receivingChainKeyBytes);
            _receivingChainKeyBytes = receivingChainKey.Span.ToArray();
        }

        // 2. Generate new local ephemeral keypair
        var (newLocalPriv, newLocalPub) = engine.GenerateEphemeralKeyPair();
        if (_localEphemeralPrivateKey != null) CryptographicOperations.ZeroMemory(_localEphemeralPrivateKey);
        _localEphemeralPrivateKey = newLocalPriv;
        LocalEphemeralPublicKey = newLocalPub;

        // 3. DH Send step using new local private key and new remote public key
        var dhSend = engine.ComputeDiffieHellman(_localEphemeralPrivateKey, newRemoteEphemeralKey.Span);
        var intermediateRootKey = ChainKey.FromSpan(_rootKeyBytes);
        var (nextRootSend, sendingChainKey) = engine.KdfRk(intermediateRootKey, dhSend);

        CryptographicOperations.ZeroMemory(_rootKeyBytes);
        _rootKeyBytes = nextRootSend.Span.ToArray();

        if (_sendingChainKeyBytes != null) CryptographicOperations.ZeroMemory(_sendingChainKeyBytes);
        _sendingChainKeyBytes = sendingChainKey.Span.ToArray();

        // 4. Update state
        PreviousSendingChainLength = SendingCounter;
        SendingCounter = 0;
        ReceivingCounter = 0;
        RemoteEphemeralPublicKey = newRemoteEphemeralKey;

        return DomainResult.Success();
    }

    private void StoreSkippedKey(uint counter, MessageKey key)
    {
        // Enforce total upper limit with LRU eviction
        if (_skippedMessageKeys.Count >= MaxTotalSkippedKeys)
        {
            uint oldestCounter = _skippedMessageKeys.Keys.Min();
            if (_skippedMessageKeys.Remove(oldestCounter, out var evictedKey))
            {
                evictedKey.Zeroize();
            }
        }

        _skippedMessageKeys[counter] = key;
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
        if (_rootKeyBytes != null)
        {
            CryptographicOperations.ZeroMemory(_rootKeyBytes);
            _rootKeyBytes = null;
        }

        if (_localEphemeralPrivateKey != null)
        {
            CryptographicOperations.ZeroMemory(_localEphemeralPrivateKey);
            _localEphemeralPrivateKey = null;
        }

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

        foreach (var key in _skippedMessageKeys.Values)
        {
            key.Zeroize();
        }
        _skippedMessageKeys.Clear();

        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
