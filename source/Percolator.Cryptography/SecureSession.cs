using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public class SecureSession
{
    public SessionId Id { get; }
    public PeerId RemotePeerId { get; }
    public ProtocolVersion ProtocolVersion { get; }
    private RatchetState _state;
    public RatchetState State => _state;
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastUsedAtUtc { get; private set; }
    private readonly Dictionary<ulong, SessionRatchetMessage> _skippedBuffer = new();
    private readonly bool _isInitiator;
    private readonly ISessionCrypto _crypto;

    public int SkippedKeysCount => _skippedBuffer.Count;

    private SecureSession(SessionId id, PeerId remotePeerId, ProtocolVersion protocolVersion, RatchetState state, ISessionCrypto crypto, DateTimeOffset now, bool isInitiator)
    {
        Id = id;
        RemotePeerId = remotePeerId;
        ProtocolVersion = protocolVersion;
        _state = state;
        CreatedAtUtc = now;
        LastUsedAtUtc = now;
        _isInitiator = isInitiator;
        _crypto = crypto;
    }

    public static SecureSession EstablishFromX3DH(
        PreKeyBundle remoteBundle,
        IKeyStore keyStore,
        ISessionCrypto sessionCrypto,
        PeerId remotePeerId,
        ProtocolVersion protocolVersion,
        IClock clock)
    {
        if (remoteBundle is null) throw new ArgumentNullException(nameof(remoteBundle));
        if (keyStore is null) throw new ArgumentNullException(nameof(keyStore));
        if (sessionCrypto is null) throw new ArgumentNullException(nameof(sessionCrypto));
        if (clock is null) throw new ArgumentNullException(nameof(clock));

        var (sharedSecret, _ephPub) = sessionCrypto.X3DH_Initiate(keyStore.GetIdentityPrivateKey(), remoteBundle);
        var state = new RatchetState(RootKey.FromBytesOwned(sharedSecret.ToArray()), null, 0, null, 0, 0, null, null, 1000);
        return new SecureSession(SessionId.NewId(), remotePeerId, protocolVersion, state, sessionCrypto, clock.UtcNow, true);
    }

    public static SecureSession Create(SessionId id, PeerId remotePeerId, ProtocolVersion protocolVersion, RatchetState state, ISessionCrypto sessionCrypto, IClock clock)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (sessionCrypto is null) throw new ArgumentNullException(nameof(sessionCrypto));
        return new SecureSession(id, remotePeerId, protocolVersion, state, sessionCrypto, clock.UtcNow, false);
    }

    public void TouchLastUsed(IClock clock)
    {
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        LastUsedAtUtc = clock.UtcNow;
    }

    public SessionRatchetMessage Encrypt(Plaintext plaintext, IClock clock)
    {
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        LastUsedAtUtc = clock.UtcNow;
        var counter = _state.SendingCounter;
        var previousChainLength = counter;
        var (ct, headerKey, newState) = _crypto.DR_Encrypt(_state, plaintext, AssociatedData.None, counter, previousChainLength);
        var msg = SessionRatchetMessage.Create(headerKey, counter, previousChainLength, ct);
        _state = newState;
        return msg;
    }

    public SessionRatchetMessage Encrypt(Plaintext plaintext, AssociatedData associatedData, IClock clock)
    {
        if (associatedData is null) throw new ArgumentNullException(nameof(associatedData));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        LastUsedAtUtc = clock.UtcNow;
        var counter = _state.SendingCounter;
        var previousChainLength = counter;
        var (ct, headerKey, newState) = _crypto.DR_Encrypt(_state, plaintext, associatedData, counter, previousChainLength);
        var msg = SessionRatchetMessage.Create(headerKey, counter, previousChainLength, ct);
        _state = newState;
        return msg;
    }

    public Plaintext Decrypt(SessionRatchetMessage message, IClock clock)
    {
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        LastUsedAtUtc = clock.UtcNow;
        var (preKey, ctr, _) = message.GetHeader();
        var recvCounter = _state.ReceivingCounter;
        if (ctr > recvCounter)
        {
            if (_skippedBuffer.Count >= _state.SkippedKeyLimit)
                throw new InvalidOperationException("Skipped-key buffer limit reached.");
            _skippedBuffer[ctr] = message;
            return Plaintext.Empty;
        }
        if (ctr < recvCounter)
        {
            throw new InvalidOperationException("Replay detected.");
        }
        Plaintext pt;
        try
        {
            var r = _crypto.DR_Decrypt(_state, message, AssociatedData.None);
            pt = r.Plaintext;
            _state = r.NewState;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Malformed or unauthentic frame.");
        }
        return pt;
    }

    public Plaintext Decrypt(SessionRatchetMessage message, AssociatedData associatedData, IClock clock)
    {
        if (associatedData is null) throw new ArgumentNullException(nameof(associatedData));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        LastUsedAtUtc = clock.UtcNow;
        var (preKey, ctr, _) = message.GetHeader();
        var recvCounter = _state.ReceivingCounter;
        if (ctr > recvCounter)
        {
            if (_skippedBuffer.Count >= _state.SkippedKeyLimit)
                throw new InvalidOperationException("Skipped-key buffer limit reached.");
            _skippedBuffer[ctr] = message;
            return Plaintext.Empty;
        }
        if (ctr < recvCounter)
        {
            throw new InvalidOperationException("Replay detected.");
        }
        Plaintext pt;
        try
        {
            var r = _crypto.DR_Decrypt(_state, message, associatedData);
            pt = r.Plaintext;
            _state = r.NewState;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Malformed or unauthentic frame.");
        }
        return pt;
    }
}
