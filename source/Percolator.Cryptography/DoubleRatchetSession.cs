using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class DoubleRatchetSession : IDisposable
{
    private const int MaxSkippedMessages = 1000;

    private RootKey _rootKey;
    private ChainKey? _sendingChainKey;
    private ChainKey? _receivingChainKey;
    private ulong _sendingCounter;
    private ulong _receivingCounter;
    private ECDiffieHellman? _dhRatchetKey;
    private RatchetEphemeralKey? _remoteRatchetKey;
    private readonly Dictionary<ulong, MessageKey> _skippedMessageKeys = new();
    private readonly RatchetIdentityKey _remoteIdentityPublicKey;

    private DoubleRatchetSession(SharedSecret sharedSecret, RatchetIdentityKey remoteIdentityPublicKey)
    {
        _remoteIdentityPublicKey = remoteIdentityPublicKey;
        _rootKey = new RootKey(sharedSecret.Value);
    }

    public DoubleRatchetSession(DoubleRatchetSessionState state)
    {
        _rootKey = state.RootKey;
        _sendingChainKey = state.SendingChainKey;
        _receivingChainKey = state.ReceivingChainKey;
        _sendingCounter = state.SendingCounter;
        _receivingCounter = state.ReceivingCounter;
        _remoteRatchetKey = state.TheirDhRatchetPublicKey;
        if (state.DhRatchetPrivateKey is not null)
        {
            _dhRatchetKey = ECDiffieHellman.Create();
            _dhRatchetKey.ImportECPrivateKey(state.DhRatchetPrivateKey.Value, out _);
        }
        _skippedMessageKeys = state.SkippedMessageKeys;
        _remoteIdentityPublicKey = state.TheirIdentityPublicKey;
    }

    public static DoubleRatchetSession AsInitiator(SharedSecret sharedSecret, RatchetIdentityKey remoteIdentityPublicKey, RatchetEphemeralKey remoteRatchetPublicKey)
    {
        var session = new DoubleRatchetSession(sharedSecret, remoteIdentityPublicKey);
        session._remoteRatchetKey = remoteRatchetPublicKey;
        return session;
    }

    public static DoubleRatchetSession AsResponder(SharedSecret sharedSecret, RatchetIdentityKey remoteIdentityPublicKey, ECDiffieHellman localRatchetKey)
    {
        var session = new DoubleRatchetSession(sharedSecret, remoteIdentityPublicKey);
        session._dhRatchetKey = localRatchetKey;
        return session;
    }

    public DoubleRatchetSessionState GetState()
    {
        return new DoubleRatchetSessionState
        {
            RootKey = _rootKey,
            SendingChainKey = _sendingChainKey,
            ReceivingChainKey = _receivingChainKey,
            SendingCounter = _sendingCounter,
            ReceivingCounter = _receivingCounter,
            SkippedMessageKeys = _skippedMessageKeys,
            TheirIdentityPublicKey = _remoteIdentityPublicKey,
            TheirDhRatchetPublicKey = _remoteRatchetKey,
            DhRatchetPrivateKey = _dhRatchetKey is not null ? new PrivateEphemeralKey(_dhRatchetKey.ExportECPrivateKey()) : null
        };
    }

    public RatchetMessage Encrypt(Plaintext plaintext)
    {
        if (_sendingChainKey is null)
        {
            if (_remoteRatchetKey is null)
                throw new InvalidOperationException("Remote ratchet key is not available.");
            // First message, perform initial ratchet
            _dhRatchetKey?.Dispose();
            _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var remoteRatchetKey = ECDiffieHellman.Create();
            remoteRatchetKey.ImportSubjectPublicKeyInfo(_remoteRatchetKey.Value, out _);
            var dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteRatchetKey.PublicKey);
            var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
            _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
            _sendingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);
        }

        var messageKey = CryptoUtils.KDF(null, _sendingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
        _sendingChainKey = new ChainKey(CryptoUtils.KDF(null, _sendingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

        var header = new RatchetHeader
        {
            RatchetKey = new RatchetEphemeralKey(_dhRatchetKey!.PublicKey.ExportSubjectPublicKeyInfo()),
            Counter = _sendingCounter
        };

        var associatedData = header.ToAssociatedData();
        var ciphertext = CryptoUtils.EncryptAesGcm(messageKey, _sendingCounter, plaintext.Value, associatedData);

        _sendingCounter++;
        return new RatchetMessage
        {
            Header = header,
            Ciphertext = new Ciphertext(ciphertext)
        };
    }

    public Plaintext Decrypt(RatchetMessage message)
    {
        var associatedData = message.Header.ToAssociatedData();

        var plaintext = TrySkippedMessageKeys(message, associatedData);
        if (plaintext is not null) return plaintext;

        if (message.Header.Counter < _receivingCounter)
        {
            throw new CryptographicException("Message was received out of order and has already been processed.");
        }

        if (message.Header.Counter - _receivingCounter > MaxSkippedMessages)
        {
            throw new CryptographicException("Message exceeds the maximum number of skippable messages.");
        }

        if (_remoteRatchetKey is null || !message.Header.RatchetKey.Equals(_remoteRatchetKey))
        {
            // This message has a new ratchet key from the other party.
            DoDhRatchet(message.Header.RatchetKey);
        }

        // Symmetrically ratchet forward to the current message.
        SkipMessageKeys(message.Header.Counter);

        if (_receivingChainKey is null)
        {
            throw new CryptographicException("Session is not properly initialized to decrypt messages.");
        }

        var messageKey = CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
        _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

        var decryptedBytes = CryptoUtils.DecryptAesGcm(messageKey, message.Header.Counter, message.Ciphertext.Value, associatedData);
        _receivingCounter++;
        return new Plaintext(decryptedBytes);
    }

    private Plaintext? TrySkippedMessageKeys(RatchetMessage message, byte[] associatedData)
    {
        if (_skippedMessageKeys.TryGetValue(message.Header.Counter, out var key))
        {
            var plaintextBytes = CryptoUtils.DecryptAesGcm(key.Value, message.Header.Counter, message.Ciphertext.Value, associatedData);
            _skippedMessageKeys.Remove(message.Header.Counter);
            return new Plaintext(plaintextBytes);
        }
        return null;
    }

    private void SkipMessageKeys(ulong until)
    {
        if (_receivingChainKey is null) return;

        if (until - _receivingCounter > MaxSkippedMessages)
        {
            throw new CryptographicException("Attempted to skip too many messages.");
        }

        while (_receivingCounter < until)
        {
            var messageKey = new MessageKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize));
            _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

            if (_skippedMessageKeys.Count < MaxSkippedMessages)
            {
                _skippedMessageKeys.Add(_receivingCounter, messageKey);
            }
            _receivingCounter++;
        }
    }

    private void DoDhRatchet(RatchetEphemeralKey remoteRatchetKey)
    {
        if (_dhRatchetKey is null)
        {
            throw new InvalidOperationException("Cannot perform DH ratchet without a local ratchet key.");
        }

        _remoteRatchetKey = remoteRatchetKey;
        using var remoteDhKey = ECDiffieHellman.Create();
        remoteDhKey.ImportSubjectPublicKeyInfo(remoteRatchetKey.Value, out _);

        var dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteDhKey.PublicKey);
        var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _receivingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);

        _dhRatchetKey.Dispose();
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteDhKey.PublicKey);
        kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _sendingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);

        _receivingCounter = 0;
        _sendingCounter = 0;
    }

    public void Dispose()
    {
        _dhRatchetKey?.Dispose();
        GC.SuppressFinalize(this);
    }

    public class DoubleRatchetSessionState
    {
        public RootKey RootKey { get; set; } = new([]);
        public ChainKey? SendingChainKey { get; set; }
        public ChainKey? ReceivingChainKey { get; set; }
        public ulong SendingCounter { get; set; }
        public ulong ReceivingCounter { get; set; }
        public Dictionary<ulong, MessageKey> SkippedMessageKeys { get; set; } = new();
        public RatchetIdentityKey TheirIdentityPublicKey { get; set; } = new([]);
        public RatchetEphemeralKey? TheirDhRatchetPublicKey { get; set; }
        public PrivateEphemeralKey? DhRatchetPrivateKey { get; set; }
    }
}