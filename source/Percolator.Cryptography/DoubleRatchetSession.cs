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
    private ulong _previousChainLength;
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
        _previousChainLength = state.PreviousChainLength;
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
        
        // Log the session initialization parameters for debugging
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][INITIATOR] Creating session with remote ratchet key hash: {Convert.ToBase64String(SHA256.HashData(remoteRatchetPublicKey.Value))}");
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][INITIATOR] Shared secret hash: {Convert.ToBase64String(SHA256.HashData(sharedSecret.Value))}");
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][INITIATOR] Root key hash: {Convert.ToBase64String(SHA256.HashData(session._rootKey.Value))}");
        
        return session;
    }

    public static DoubleRatchetSession AsResponder(SharedSecret sharedSecret, RatchetIdentityKey remoteIdentityPublicKey, ECDiffieHellman localRatchetKey)
    {
        var session = new DoubleRatchetSession(sharedSecret, remoteIdentityPublicKey);
        session._dhRatchetKey = localRatchetKey;
        
        // Log the session initialization parameters for debugging
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][RESPONDER] Creating session with local ratchet key hash: {Convert.ToBase64String(SHA256.HashData(localRatchetKey.PublicKey.ExportSubjectPublicKeyInfo()))}");
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][RESPONDER] Shared secret hash: {Convert.ToBase64String(SHA256.HashData(sharedSecret.Value))}");
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][RESPONDER] Root key hash: {Convert.ToBase64String(SHA256.HashData(session._rootKey.Value))}");
        
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
            PreviousChainLength = _previousChainLength,
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
            
            // Add debug logging for initial ratchet
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][ENCRYPT] Initial ratchet performed");
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][ENCRYPT] Remote ratchet key hash: {Convert.ToBase64String(SHA256.HashData(_remoteRatchetKey.Value))}");
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][ENCRYPT] New root key hash: {Convert.ToBase64String(SHA256.HashData(_rootKey.Value))}");
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][ENCRYPT] New sending chain key hash: {Convert.ToBase64String(SHA256.HashData(_sendingChainKey.Value))}");
        }

        var messageKey = CryptoUtils.KDF(null, _sendingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
        _sendingChainKey = new ChainKey(CryptoUtils.KDF(null, _sendingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

        // Add message key debug logging
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][ENCRYPT] Message key hash: {Convert.ToBase64String(SHA256.HashData(messageKey))}");
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][ENCRYPT] New sending chain key hash: {Convert.ToBase64String(SHA256.HashData(_sendingChainKey.Value))}");
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][ENCRYPT] Sending counter: {_sendingCounter}");

        // Create ephemeral key from our current ratchet key
        var ourPublicKey = new RatchetEphemeralKey(_dhRatchetKey!.PublicKey.ExportSubjectPublicKeyInfo());
        
        // Encrypt plaintext
        var header = new Tuple<RatchetEphemeralKey, ulong, ulong>(ourPublicKey, _sendingCounter, _previousChainLength);
        var associatedData = SessionRatchetMessage.GetAssociatedData(header, new byte[0]);
        var ciphertext = CryptoUtils.EncryptAesGcm(plaintext.Value, messageKey, associatedData);
        
        // Increment the sending counter after successful encryption
        _sendingCounter++;
        
        // Create and return ratchet message with encrypted data
        return SessionRatchetMessage.Create(ourPublicKey, _sendingCounter - 1, _previousChainLength, new Ciphertext(ciphertext));
    }

    public Plaintext Decrypt(SessionRatchetMessage message)
    {
        var header = message.GetHeader();
        var ciphertext = message.GetCiphertext();
        var associatedData = SessionRatchetMessage.GetAssociatedData(new Tuple<RatchetEphemeralKey, ulong, ulong>(header.RatchetKey, header.Counter, header.PreviousChainLength), new byte[0]);

        System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] Decrypting message with counter {header.Counter} from ratchet key hash: {Convert.ToBase64String(SHA256.HashData(header.RatchetKey.Value))}");
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] Current receiving counter: {_receivingCounter}, previous chain length: {_previousChainLength}");
        
        // If we haven't seen this ratchet key before, it's from a new chain
        bool isFromNewChain = _remoteRatchetKey is null || !header.RatchetKey.Equals(_remoteRatchetKey);
        
        // If message is from previous chain (different key but already processed)
        if (isFromNewChain && header.RatchetKey.Equals(_remoteRatchetKey) == false)
        {
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] Message is from a different ratchet chain");
            
            // For messages from a previous chain, apply the previous chain length validation
            // Only enforce this check when previous chain length is explicitly set (non-zero)
            if (header.PreviousChainLength > 0 && header.Counter >= header.PreviousChainLength)
            {
                System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] Message counter {header.Counter} exceeds previous chain length {header.PreviousChainLength}");
                throw new CryptographicException($"Message counter exceeds the previous chain length.");
            }
            
            // Try to decrypt using skipped message keys for previous chain
            var skippedPlaintext = TrySkippedMessageKeys(header, ciphertext, associatedData);
            if (skippedPlaintext != null)
            {
                return skippedPlaintext;
            }
        }
        
        // Message is from current chain, standard counter checking
        if (!isFromNewChain && header.Counter < _receivingCounter)
        {
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] Message was received out of order and has already been processed");
            
            // Try to decrypt with skipped message keys before failing
            var decryptedPlaintext = TrySkippedMessageKeys(header, ciphertext, associatedData);
            if (decryptedPlaintext != null)
            {
                return decryptedPlaintext;
            }
            
            throw new CryptographicException("Message was already processed.");
        }
        
        if (isFromNewChain)
        {
            // This message has a new ratchet key from the other party
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] New ratchet key detected, performing DH ratchet");
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

        var plaintext = new Plaintext(CryptoUtils.DecryptAesGcm(ciphertext.Value, messageKey, associatedData));
        
        // Increment the counter after successful decryption
        _receivingCounter++;
        
        return plaintext;
    }

    private Plaintext? TrySkippedMessageKeys((RatchetEphemeralKey RatchetKey, ulong Counter, ulong PreviousChainLength) header, Ciphertext ciphertext, byte[] associatedData)
    {
        // Check for skipped message keys
        if (_skippedMessageKeys.TryGetValue(header.Counter, out var messageKey))
        {
            // Remove the used message key from the dictionary to prevent replay attacks
            _skippedMessageKeys.Remove(header.Counter);
            
            // Decrypt using the skipped message key
            System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] Found skipped message key for counter {header.Counter}");
            try
            {
                var decryptedBytes = CryptoUtils.DecryptAesGcm(ciphertext.Value, messageKey.Value, associatedData);
                return new Plaintext(decryptedBytes);
            }
            catch (CryptographicException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CRYPTO][DECRYPT] Failed to decrypt with skipped message key: {ex.Message}");
                return null;
            }
        }
        
        return null;
    }

    private void SkipMessageKeys(ulong until)
    {
        if (_receivingChainKey == null)
        {
            return;
        }

        // Only skip keys if the until value is greater than the current receiving counter
        if (until <= _receivingCounter)
        {
            return;
        }
        
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][SKIP] Skipping message keys from {_receivingCounter} to {until - 1}");

        // Skip enough message keys to get to the target counter
        while (_receivingCounter < until)
        {
            // Store the skipped message key for future use
            var messageKey = CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
            _skippedMessageKeys[_receivingCounter] = new MessageKey(messageKey);
            
            // Prevent memory attacks by limiting the number of skipped messages
            if (_skippedMessageKeys.Count > MaxSkippedMessages)
            {
                var oldestKey = _skippedMessageKeys.Keys.Min();
                _skippedMessageKeys.Remove(oldestKey);
            }

            // Advance the receiving chain key
            _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));
            _receivingCounter++;
        }
    }

    private void DoDhRatchet(RatchetEphemeralKey remoteRatchetKey)
    {
        // Store the current sending chain length as the previous chain length
        // This MUST be done before resetting the sending counter
        _previousChainLength = _sendingCounter;
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][RATCHET] Setting previous chain length to {_previousChainLength}");
        
        // Update remote ratchet key
        _remoteRatchetKey = remoteRatchetKey;
        
        // Save old DH key for disposal
        var oldDhKey = _dhRatchetKey;
        
        // Generate new DH key pair
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // DH ratchet step 1 - use old private key with new remote public key
        using var remoteKey = ECDiffieHellman.Create();
        remoteKey.ImportSubjectPublicKeyInfo(remoteRatchetKey.Value, out _);
        var dhSecret = oldDhKey!.DeriveKeyMaterial(remoteKey.PublicKey);
        var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _receivingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);
        
        // Reset receiving counter for the new receiving chain
        _receivingCounter = 0;
        
        // DH ratchet step 2 - use new private key with remote public key
        dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteKey.PublicKey);
        kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _sendingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);
        
        // Reset sending counter for the new sending chain
        _sendingCounter = 0;
        
        // Clean up old DH key
        oldDhKey?.Dispose();
        
        System.Diagnostics.Debug.WriteLine($"[CRYPTO][RATCHET] DH ratchet complete with PreviousChainLength={_previousChainLength}");
    }

    public void Dispose()
    {
        _dhRatchetKey?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Expose counters as internal properties for testing
    internal ulong SendingCounter => _sendingCounter;
    internal ulong ReceivingCounter => _receivingCounter;
    internal ulong PreviousChainLength => _previousChainLength;

    public class DoubleRatchetSessionState
    {
        public RootKey RootKey { get; set; } = new([]);
        public ChainKey? SendingChainKey { get; set; }
        public ChainKey? ReceivingChainKey { get; set; }
        public ulong SendingCounter { get; set; }
        public ulong ReceivingCounter { get; set; }
        public ulong PreviousChainLength { get; set; }
        public Dictionary<ulong, MessageKey> SkippedMessageKeys { get; set; } = new();
        public RatchetIdentityKey TheirIdentityPublicKey { get; set; } = new([]);
        public RatchetEphemeralKey? TheirDhRatchetPublicKey { get; set; }
        public PrivateEphemeralKey? DhRatchetPrivateKey { get; set; }
    }
}