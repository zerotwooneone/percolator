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
        // Initialize a local ratchet key for the initiator as well
        session._dhRatchetKey = ECDiffieHellman.Create();
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

    public SessionRatchetMessage Encrypt(Plaintext plaintext)
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

        // Create ephemeral key from our current ratchet key
        var ratchetKey = new RatchetEphemeralKey(_dhRatchetKey!.PublicKey.ExportSubjectPublicKeyInfo());
        var counter = _sendingCounter;
        
        // First create the SessionRatchetMessage without ciphertext
        var tempMessage = SessionRatchetMessage.Create(ratchetKey, counter, new Ciphertext(Array.Empty<byte>()));
        
        // Now we can get the associated data through the helper method
        var associatedData = tempMessage.GetHeaderAssociatedData();
        
        // Encrypt the plaintext with the correct associated data
        var ciphertext = new Ciphertext(CryptoUtils.EncryptAesGcm(messageKey, counter, plaintext.Value, associatedData));

        _sendingCounter++;
        
        // Create the final SessionRatchetMessage with the real ciphertext
        return SessionRatchetMessage.Create(ratchetKey, counter, ciphertext);
    }

    public Plaintext Decrypt(SessionRatchetMessage message)
    {
        // Extract header and ciphertext from SessionRatchetMessage
        var header = message.GetHeader();
        var ciphertext = message.GetCiphertext();
        var associatedData = message.GetHeaderAssociatedData();

        // Try to decrypt using skipped message keys
        var plaintext = TrySkippedMessageKeys(header, ciphertext, associatedData);
        if (plaintext is not null) return plaintext;

        if (header.Counter < _receivingCounter)
        {
            throw new CryptographicException("Message was received out of order and has already been processed.");
        }

        if (header.Counter - _receivingCounter > MaxSkippedMessages)
        {
            throw new CryptographicException("Message exceeds the maximum number of skippable messages.");
        }

        if (_remoteRatchetKey is null || !header.RatchetKey.Equals(_remoteRatchetKey))
        {
            // This message has a new ratchet key from the other party.
            DoDhRatchet(header.RatchetKey);
        }

        // Symmetrically ratchet forward to the current message.
        SkipMessageKeys(header.Counter);

        if (_receivingChainKey is null)
        {
            throw new CryptographicException("Session is not properly initialized to decrypt messages.");
        }

        var messageKey = CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
        _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

        var decryptedBytes = CryptoUtils.DecryptAesGcm(messageKey, header.Counter, ciphertext.Value, associatedData);
        _receivingCounter++;
        return new Plaintext(decryptedBytes);
    }

    private Plaintext? TrySkippedMessageKeys((RatchetEphemeralKey RatchetKey, ulong Counter) header, Ciphertext ciphertext, byte[] associatedData)
    {
        // Check if we have a skipped message key for this message
        if (_skippedMessageKeys.TryGetValue(header.Counter, out var messageKey))
        {
            _skippedMessageKeys.Remove(header.Counter);
            var decryptedBytes = CryptoUtils.DecryptAesGcm(messageKey.Value, header.Counter, ciphertext.Value, associatedData);
            return new Plaintext(decryptedBytes);
        }
        return null;
    }

    private void SkipMessageKeys(ulong until)
    {
        if (_receivingChainKey is null) return;

        // Skip all message keys until we reach the target counter.
        while (_receivingCounter < until)
        {
            var messageKey = CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
            _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

            // Store the skipped message key for future use
            _skippedMessageKeys[_receivingCounter] = new MessageKey(messageKey);
            
            // Prevent memory attacks by limiting the number of skipped messages
            if (_skippedMessageKeys.Count > MaxSkippedMessages)
            {
                var oldestKey = _skippedMessageKeys.Keys.Min();
                _skippedMessageKeys.Remove(oldestKey);
            }

            _receivingCounter++;
        }
    }

    private void DoDhRatchet(RatchetEphemeralKey remoteRatchetKey)
    {
        _remoteRatchetKey = remoteRatchetKey;
        _receivingCounter = 0;

        // DH ratchet to derive a new root key and receiving chain key
        using var remoteKey = ECDiffieHellman.Create();
        remoteKey.ImportSubjectPublicKeyInfo(remoteRatchetKey.Value, out _);
        var dhSecret = _dhRatchetKey!.DeriveKeyMaterial(remoteKey.PublicKey);
        var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _receivingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);

        // Also generate a new DH key pair for sending
        _dhRatchetKey?.Dispose();
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // DH ratchet again to derive a new root key and sending chain key
        dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteKey.PublicKey);
        kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _sendingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);
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