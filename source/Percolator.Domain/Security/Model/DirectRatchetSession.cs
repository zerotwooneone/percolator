using System.ComponentModel;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

public readonly record struct SkippedMessageKeyId(DhPublicKey? EphemeralPublicKey, uint Counter);

public sealed record DirectRatchetSessionSnapshot(
    SessionId SessionId,
    PublicIdentityId OwnerIdentityId,
    DeviceId OwnerDeviceId,
    PublicIdentityId RemotePeerId,
    DeviceId RemoteDeviceId,
    byte[]? RootKeyBytes,
    byte[]? SendingChainKeyBytes,
    byte[]? ReceivingChainKeyBytes,
    DhPublicKey? RemoteEphemeralPublicKey,
    byte[]? LocalEphemeralPrivateKeyBytes,
    DhPublicKey? LocalEphemeralPublicKey,
    uint SendingCounter,
    uint ReceivingCounter,
    uint PreviousSendingChainLength,
    IReadOnlyDictionary<uint, byte[]> SkippedMessageKeys);

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
    public DhPublicKey? LocalEphemeralPublicKey { get; private set; }
    public DhPublicKey? RemoteEphemeralPublicKey { get; private set; }
    private ChainKey? _sendingChainKey;
    private ChainKey? _receivingChainKey;
    private readonly Dictionary<uint, MessageKey> _skippedMessageKeys = [];
    private readonly Dictionary<SkippedMessageKeyId, MessageKey> _epochSkippedMessageKeys = [];
    private readonly Queue<SkippedMessageKeyId> _skippedKeyOrder = new();

    public uint SendingCounter { get; private set; }
    public uint ReceivingCounter { get; private set; }
    public uint PreviousSendingChainLength { get; private set; }
    public bool IsZeroized { get; private set; }
    public int SkippedKeyCount => _skippedMessageKeys.Count;
    public int EpochSkippedKeyCount => _epochSkippedMessageKeys.Count;

    public DirectRatchetSession(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        ChainKey? rootKey,
        ChainKey? sendingChainKey,
        ChainKey? receivingChainKey,
        DhPublicKey? remoteEphemeralPublicKey = null,
        EphemeralPrivateKey? localEphemeralPrivateKey = null,
        DhPublicKey? localEphemeralPublicKey = null,
        uint sendingCounter = 0,
        uint receivingCounter = 0,
        SessionId? sessionId = null,
        IEnumerable<KeyValuePair<uint, MessageKey>>? skippedKeys = null,
        IEnumerable<KeyValuePair<SkippedMessageKeyId, MessageKey>>? epochSkippedKeys = null)
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

        if (epochSkippedKeys != null)
        {
            foreach (var kvp in epochSkippedKeys)
            {
                StoreSkippedKey(kvp.Key.EphemeralPublicKey, kvp.Key.Counter, MessageKey.FromSpan(kvp.Value.Span));
            }
        }
        else if (skippedKeys != null)
        {
            foreach (var kvp in skippedKeys)
            {
                StoreSkippedKey(remoteEphemeralPublicKey, kvp.Key, MessageKey.FromSpan(kvp.Value.Span));
            }
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public DirectRatchetSessionSnapshot ExportSnapshot()
    {
        var skippedDict = new Dictionary<uint, byte[]>(_skippedMessageKeys.Count);
        foreach (var (k, v) in _skippedMessageKeys)
        {
            skippedDict[k] = v.Span.ToArray();
        }

        return new DirectRatchetSessionSnapshot(
            Id,
            OwnerIdentityId,
            OwnerDeviceId,
            RemotePeerId,
            RemoteDeviceId,
            _rootKey != null && !IsZeroized ? _rootKey.Span.ToArray() : [],
            _sendingChainKey != null && !IsZeroized ? _sendingChainKey.Span.ToArray() : null,
            _receivingChainKey != null && !IsZeroized ? _receivingChainKey.Span.ToArray() : null,
            RemoteEphemeralPublicKey,
            _localEphemeralPrivateKey != null && !IsZeroized ? _localEphemeralPrivateKey.Span.ToArray() : null,
            LocalEphemeralPublicKey,
            SendingCounter,
            ReceivingCounter,
            PreviousSendingChainLength,
            skippedDict);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static DirectRatchetSession Rehydrate(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        ChainKey? rootKey,
        ChainKey? sendingChainKey,
        ChainKey? receivingChainKey,
        DhPublicKey? remoteEphemeralPublicKey = null,
        EphemeralPrivateKey? localEphemeralPrivateKey = null,
        DhPublicKey? localEphemeralPublicKey = null,
        uint sendingCounter = 0,
        uint receivingCounter = 0,
        uint previousSendingChainLength = 0,
        SessionId? sessionId = null,
        IEnumerable<KeyValuePair<uint, MessageKey>>? skippedKeys = null)
    {
        if (!ownerIdentityId.IsValid) throw new ArgumentException("OwnerIdentityId cannot be empty.", nameof(ownerIdentityId));
        if (!remotePeerId.IsValid) throw new ArgumentException("RemotePeerId cannot be empty.", nameof(remotePeerId));
        if (ownerIdentityId == remotePeerId) throw new ArgumentException("Cannot rehydrate session with self.", nameof(remotePeerId));
        if (!ownerDeviceId.IsValid) throw new ArgumentException("OwnerDeviceId cannot be empty.", nameof(ownerDeviceId));
        if (!remoteDeviceId.IsValid) throw new ArgumentException("RemoteDeviceId cannot be empty.", nameof(remoteDeviceId));

        var session = new DirectRatchetSession(
            ownerIdentityId,
            ownerDeviceId,
            remotePeerId,
            remoteDeviceId,
            rootKey,
            sendingChainKey,
            receivingChainKey,
            remoteEphemeralPublicKey,
            localEphemeralPrivateKey,
            localEphemeralPublicKey,
            sendingCounter,
            receivingCounter,
            sessionId,
            skippedKeys)
        {
            PreviousSendingChainLength = previousSendingChainLength
        };

        return session;
    }

    /// <summary>
    /// Creates an outbound Double Ratchet session initialized directly from the results of a mutual X3DH agreement.
    /// Derives the initial sending chain and root key from the X3DH master shared secret.
    /// </summary>
    public static DomainResult<DirectRatchetSession> CreateFromX3dhInitiator(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        X3dhInitiatorResult x3DhResult,
        DhPublicKey remoteSignedPreKey,
        ICryptoEngine engine,
        SessionId? sessionId = null)
    {
        if (ownerIdentityId == remotePeerId)
        {
            return DomainResult<DirectRatchetSession>.Failure(new DomainError(
                "SELF_SESSION_NOT_ALLOWED", "Cannot initiate Double Ratchet session with self."));
        }

        ArgumentNullException.ThrowIfNull(x3DhResult);
        ArgumentNullException.ThrowIfNull(remoteSignedPreKey);
        ArgumentNullException.ThrowIfNull(engine);

        using var initialRoot = ChainKey.FromSpan(x3DhResult.MasterSecret.Span);
        var (nextRoot, sendingChain) = engine.KdfRk(initialRoot, x3DhResult.MasterSecret);

        var session = new DirectRatchetSession(
            ownerIdentityId,
            ownerDeviceId,
            remotePeerId,
            remoteDeviceId,
            rootKey: nextRoot,
            sendingChainKey: sendingChain,
            receivingChainKey: null,
            remoteEphemeralPublicKey: remoteSignedPreKey,
            localEphemeralPrivateKey: x3DhResult.EphemeralPrivateKey,
            localEphemeralPublicKey: x3DhResult.EphemeralPublicKey,
            sessionId: sessionId);

        nextRoot.Dispose();
        sendingChain.Dispose();

        return DomainResult<DirectRatchetSession>.Success(session);
    }

    /// <summary>
    /// Creates an inbound Double Ratchet session initialized directly from the results of a mutual X3DH responder agreement.
    /// Derives the matching initial receiving chain and root key from the X3DH master shared secret.
    /// </summary>
    public static DomainResult<DirectRatchetSession> CreateFromX3dhResponder(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        SharedSecret masterSecret,
        DhPublicKey remoteEphemeralPublicKey,
        ICryptoEngine engine,
        SessionId? sessionId = null)
    {
        if (ownerIdentityId == remotePeerId)
        {
            return DomainResult<DirectRatchetSession>.Failure(new DomainError(
                "SELF_SESSION_NOT_ALLOWED", "Cannot initiate Double Ratchet session with self."));
        }

        ArgumentNullException.ThrowIfNull(masterSecret);
        ArgumentNullException.ThrowIfNull(remoteEphemeralPublicKey);
        ArgumentNullException.ThrowIfNull(engine);

        using var initialRoot = ChainKey.FromSpan(masterSecret.Span);
        var (nextRoot, receivingChain) = engine.KdfRk(initialRoot, masterSecret);

        var session = new DirectRatchetSession(
            ownerIdentityId,
            ownerDeviceId,
            remotePeerId,
            remoteDeviceId,
            rootKey: nextRoot,
            sendingChainKey: null,
            receivingChainKey: receivingChain,
            remoteEphemeralPublicKey: remoteEphemeralPublicKey,
            sessionId: sessionId);

        nextRoot.Dispose();
        receivingChain.Dispose();

        return DomainResult<DirectRatchetSession>.Success(session);
    }

    /// <summary>
    /// Initiates an outbound Double Ratchet session using the remote peer's pre-key bundle.
    /// Verifies the signed pre-key signature against the peer's identity key before deriving the initial sending chain.
    /// </summary>
    public static DomainResult<DirectRatchetSession> InitiateOutbound(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PreKeyBundle peerBundle,
        ICryptoEngine engine,
        SessionId? sessionId = null)
    {
        if (peerBundle == null)
        {
            return DomainResult<DirectRatchetSession>.Failure(new DomainError(
                "NULL_PREKEY_BUNDLE", "Remote pre-key bundle cannot be null."));
        }

        ArgumentNullException.ThrowIfNull(engine);

        if (ownerIdentityId == peerBundle.IdentityId)
        {
            return DomainResult<DirectRatchetSession>.Failure(new DomainError(
                "SELF_SESSION_NOT_ALLOWED", "Cannot initiate Double Ratchet session with self."));
        }

        if (!engine.VerifyEd25519Signature(
                peerBundle.IdentityKey,
                peerBundle.SignedPreKey.Span,
                peerBundle.SignedPreKeySignature.Span))
        {
            return DomainResult<DirectRatchetSession>.Failure(new DomainError(
                "INVALID_SIGNED_PREKEY_SIGNATURE", "Remote signed prekey signature is invalid or forged."));
        }

        // Generate Alice's initial ephemeral key pair
        var (ephemeralPriv, ephemeralPub) = engine.GenerateEphemeralKeyPair();

        // Perform initial DH between Alice's ephemeral private key and Bob's signed pre-key
        using var dhSecret = engine.ComputeDiffieHellman(ephemeralPriv.Span, peerBundle.SignedPreKey.Span);

        // Derive root key and initial sending chain key
        Span<byte> zeroRoot = stackalloc byte[32];
        using var initialRoot = ChainKey.FromSpan(zeroRoot);
        var (nextRoot, sendingChain) = engine.KdfRk(initialRoot, dhSecret);

        var session = new DirectRatchetSession(
            ownerIdentityId,
            ownerDeviceId,
            peerBundle.IdentityId,
            peerBundle.DeviceId,
            rootKey: nextRoot,
            sendingChainKey: sendingChain,
            receivingChainKey: null,
            remoteEphemeralPublicKey: peerBundle.SignedPreKey,
            localEphemeralPrivateKey: ephemeralPriv,
            localEphemeralPublicKey: ephemeralPub,
            sessionId: sessionId);

        nextRoot.Dispose();
        sendingChain.Dispose();
        ephemeralPriv.Dispose();

        return DomainResult<DirectRatchetSession>.Success(session);
    }

    /// <summary>
    /// Initiates an inbound Double Ratchet session upon receiving an initial message with the sender's ephemeral public key.
    /// Computes the matching root key and initial receiving chain key, ready to decrypt incoming messages.
    /// </summary>
    public static DomainResult<DirectRatchetSession> InitiateInbound(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        EphemeralPrivateKey localSignedPreKeyPriv,
        DhPublicKey remoteEphemeralPublicKey,
        ICryptoEngine engine,
        SessionId? sessionId = null)
    {
        if (ownerIdentityId == remotePeerId)
        {
            return DomainResult<DirectRatchetSession>.Failure(new DomainError(
                "SELF_SESSION_NOT_ALLOWED", "Cannot initiate Double Ratchet session with self."));
        }

        // Perform initial DH between Bob's signed pre-key private key and Alice's ephemeral public key
        using var dhSecret = engine.ComputeDiffieHellman(localSignedPreKeyPriv.Span, remoteEphemeralPublicKey.Span);

        // Derive matching root key and initial receiving chain key
        Span<byte> zeroRoot = stackalloc byte[32];
        using var initialRoot = ChainKey.FromSpan(zeroRoot);
        var (nextRoot, receivingChain) = engine.KdfRk(initialRoot, dhSecret);

        var session = new DirectRatchetSession(
            ownerIdentityId,
            ownerDeviceId,
            remotePeerId,
            remoteDeviceId,
            rootKey: nextRoot,
            sendingChainKey: null,
            receivingChainKey: receivingChain,
            remoteEphemeralPublicKey: remoteEphemeralPublicKey,
            sessionId: sessionId);

        nextRoot.Dispose();
        receivingChain.Dispose();

        return DomainResult<DirectRatchetSession>.Success(session);
    }

    public DomainResult<(uint MessageCounter, MessageKey Key, DhPublicKey? EphemeralPublicKey)> StepSendingChain(ICryptoEngine engine)
    {
        if (IsZeroized)
        {
            return DomainResult<(uint, MessageKey, DhPublicKey?)>.Failure(new DomainError("INVALID_SESSION_STATE", "Sending chain key is not available or has been zeroized."));
        }

        if (_sendingChainKey == null && RemoteEphemeralPublicKey != null)
        {
            var ratchetResult = StepDhRatchet(RemoteEphemeralPublicKey, engine);
            if (ratchetResult.IsFailure)
            {
                return DomainResult<(uint, MessageKey, DhPublicKey?)>.Failure(ratchetResult.Error);
            }
        }

        if (_sendingChainKey == null)
        {
            return DomainResult<(uint, MessageKey, DhPublicKey?)>.Failure(new DomainError("INVALID_SESSION_STATE", "Sending chain key is not available or has been zeroized."));
        }

        var previousChainKey = _sendingChainKey;
        var (nextChainKey, messageKey) = engine.StepRatchet(previousChainKey);

        _sendingChainKey = nextChainKey;
        previousChainKey.Dispose();

        uint counter = SendingCounter++;
        return DomainResult<(uint, MessageKey, DhPublicKey?)>.Success((counter, messageKey, LocalEphemeralPublicKey));
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

            StoreSkippedKey(RemoteEphemeralPublicKey, ReceivingCounter, skippedMessageKey);
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

    public DomainResult StepDhRatchet(
        DhPublicKey newRemoteEphemeralKey,
        ICryptoEngine engine,
        uint previousChainLength = 0)
    {
        if (IsZeroized || _rootKey == null)
        {
            return DomainResult.Failure(new DomainError("INVALID_SESSION_STATE", "Root key is not available or has been zeroized."));
        }

        if (newRemoteEphemeralKey == null)
        {
            return DomainResult.Failure(new DomainError("NULL_EPHEMERAL_KEY", "Remote ephemeral public key cannot be null."));
        }

        if (RemoteEphemeralPublicKey != null && RemoteEphemeralPublicKey == newRemoteEphemeralKey && _sendingChainKey != null)
        {
            // Already ratcheted to this ephemeral key and sending chain is ready
            return DomainResult.Success();
        }

        // Signal Double Ratchet specification: Before replacing the receiving chain on a DH ratchet step,
        // stage any remaining skipped message keys from the previous receiving chain up to PN (previousChainLength).
        if (_receivingChainKey != null && RemoteEphemeralPublicKey != newRemoteEphemeralKey && previousChainLength > ReceivingCounter)
        {
            if (previousChainLength - ReceivingCounter > MaxSkipThreshold)
            {
                return DomainResult.Failure(new DomainError(
                    "SKIP_THRESHOLD_EXCEEDED",
                    $"Skipping {previousChainLength - ReceivingCounter} previous-chain messages exceeds max threshold of {MaxSkipThreshold}."));
            }

            while (ReceivingCounter < previousChainLength)
            {
                var prevKey = _receivingChainKey;
                var (nextChainKey, skippedMessageKey) = engine.StepRatchet(prevKey);

                _receivingChainKey = nextChainKey;
                prevKey.Dispose();

                StoreSkippedKey(RemoteEphemeralPublicKey, ReceivingCounter, skippedMessageKey);
                ReceivingCounter++;
            }
        }

        // 1. DH Receive step using existing local private key and new remote public key
        if (_localEphemeralPrivateKey != null && RemoteEphemeralPublicKey != newRemoteEphemeralKey)
        {
            using var dhRecv = engine.ComputeDiffieHellman(_localEphemeralPrivateKey.Span, newRemoteEphemeralKey.Span);
            var (r1, receivingChain) = engine.KdfRk(_rootKey, dhRecv);

            _rootKey.Dispose();
            _rootKey = r1;

            _receivingChainKey?.Dispose();
            _receivingChainKey = receivingChain;

            PreviousSendingChainLength = SendingCounter;
            SendingCounter = 0;
            ReceivingCounter = 0;

            _localEphemeralPrivateKey.Dispose();
            _localEphemeralPrivateKey = null;
            LocalEphemeralPublicKey = null;
        }

        RemoteEphemeralPublicKey = newRemoteEphemeralKey;

        // 2. DH Send step: generate new local ephemeral pair, perform DH with new remote public key
        var (newLocalPriv, newLocalPub) = engine.GenerateEphemeralKeyPair();
        _localEphemeralPrivateKey = newLocalPriv;
        LocalEphemeralPublicKey = newLocalPub;

        using var dhSend = engine.ComputeDiffieHellman(_localEphemeralPrivateKey.Span, RemoteEphemeralPublicKey.Span);
        var (r2, sendingChain) = engine.KdfRk(_rootKey, dhSend);

        _rootKey.Dispose();
        _rootKey = r2;

        _sendingChainKey?.Dispose();
        _sendingChainKey = sendingChain;

        return DomainResult.Success();
    }

    public DomainResult<MessageKey> TryGetSkippedKey(uint counter, DhPublicKey? remoteEphemeralKey = null)
    {
        if (IsZeroized)
        {
            return DomainResult<MessageKey>.Failure(new DomainError("INVALID_SESSION_STATE", "Session has been zeroized."));
        }

        // 1. If explicit header ephemeral key is provided, look up exact (ephemeralKey, counter) pair
        if (remoteEphemeralKey != null)
        {
            var exactId = new SkippedMessageKeyId(remoteEphemeralKey, counter);
            if (_epochSkippedMessageKeys.Remove(exactId, out var exactKey))
            {
                if (_skippedMessageKeys.TryGetValue(counter, out var currentIndexed) && ReferenceEquals(currentIndexed, exactKey))
                {
                    _skippedMessageKeys.Remove(counter);
                }

                return DomainResult<MessageKey>.Success(exactKey);
            }
        }

        // 2. Check current RemoteEphemeralPublicKey + counter
        var currentChainId = new SkippedMessageKeyId(RemoteEphemeralPublicKey, counter);
        if (_epochSkippedMessageKeys.Remove(currentChainId, out var key))
        {
            _skippedMessageKeys.Remove(counter);
            return DomainResult<MessageKey>.Success(key);
        }

        // 3. Fallback when caller omitted remoteEphemeralKey: check _skippedMessageKeys index
        if (remoteEphemeralKey == null && _skippedMessageKeys.Remove(counter, out var fallbackKey))
        {
            var matchingEntry = _epochSkippedMessageKeys.FirstOrDefault(kvp => kvp.Key.Counter == counter && ReferenceEquals(kvp.Value, fallbackKey));
            if (matchingEntry.Value != null)
            {
                _epochSkippedMessageKeys.Remove(matchingEntry.Key);
            }

            return DomainResult<MessageKey>.Success(fallbackKey);
        }

        return DomainResult<MessageKey>.Failure(new DomainError("SKIPPED_KEY_NOT_FOUND", $"No skipped key cached for counter {counter}."));
    }

    private void StoreSkippedKey(DhPublicKey? ephemeralKey, uint counter, MessageKey key)
    {
        while (_epochSkippedMessageKeys.Count >= MaxTotalSkippedKeys && _skippedKeyOrder.TryDequeue(out var oldestId))
        {
            if (_epochSkippedMessageKeys.Remove(oldestId, out var evictedKey))
            {
                if (_skippedMessageKeys.TryGetValue(oldestId.Counter, out var indexed) && ReferenceEquals(indexed, evictedKey))
                {
                    _skippedMessageKeys.Remove(oldestId.Counter);
                }

                evictedKey.Dispose();
                break;
            }
        }

        var id = new SkippedMessageKeyId(ephemeralKey, counter);
        if (_epochSkippedMessageKeys.Remove(id, out var existingKey))
        {
            existingKey.Dispose();
        }

        _epochSkippedMessageKeys[id] = key;
        _skippedMessageKeys[counter] = key;
        _skippedKeyOrder.Enqueue(id);
    }

    public void Zeroize()
    {
        if (IsZeroized) return;

        _rootKey?.Dispose();
        _rootKey = null;

        _localEphemeralPrivateKey?.Dispose();
        _localEphemeralPrivateKey = null;
        LocalEphemeralPublicKey = null;
        RemoteEphemeralPublicKey = null;

        _sendingChainKey?.Dispose();
        _sendingChainKey = null;

        _receivingChainKey?.Dispose();
        _receivingChainKey = null;

        foreach (var key in _epochSkippedMessageKeys.Values)
        {
            key.Dispose();
        }

        foreach (var key in _skippedMessageKeys.Values)
        {
            key.Dispose();
        }

        _epochSkippedMessageKeys.Clear();
        _skippedMessageKeys.Clear();
        _skippedKeyOrder.Clear();

        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
