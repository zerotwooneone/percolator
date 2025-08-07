using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography.Primitives;
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
    private readonly Dictionary<SkippedMessageKeyIdentifier, byte[]> _skippedMessageKeys = new();
    private readonly RatchetIdentityKey _remoteIdentityPublicKey;
    private readonly ILogger<DoubleRatchetSession> _logger;
    private readonly CryptographyOptions _options;

    private DoubleRatchetSession(SharedSecret sharedSecret, RatchetIdentityKey remoteIdentityPublicKey, ILogger<DoubleRatchetSession> logger, CryptographyOptions? options = null)
    {
        _remoteIdentityPublicKey = remoteIdentityPublicKey;
        _rootKey = new RootKey(sharedSecret.Value);
        _logger = logger;
        _options = options ?? CryptographyOptions.CreateSecureDefault();
    }

    public DoubleRatchetSession(DoubleRatchetSessionState state, ILogger<DoubleRatchetSession> logger, CryptographyOptions? options = null)
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
            _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _dhRatchetKey.ImportECPrivateKey(state.DhRatchetPrivateKey.Value, out _);
        }
        _skippedMessageKeys = state.SkippedMessageKeys;
        _remoteIdentityPublicKey = state.TheirIdentityPublicKey;
        _logger = logger;
        _options = options ?? CryptographyOptions.CreateSecureDefault();
    }

    public static DoubleRatchetSession AsInitiator(
        SharedSecret sharedSecret, 
        RatchetIdentityKey remoteIdentityPublicKey, 
        RatchetEphemeralKey remoteRatchetPublicKey,
        ILogger<DoubleRatchetSession> logger,
        CryptographyOptions? options = null)
    {
        logger.LogDebug("Creating initiator session");
        
        // Create Double Ratchet session with shared secret
        var session = new DoubleRatchetSession(sharedSecret, remoteIdentityPublicKey, logger, options);
        
        // Create DH key pair for the initiator
        session._dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // Log the DH key material for debugging
        session.LogDebugCryptoMaterial("Initiator DH key hash: {DhKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(session._dhRatchetKey.PublicKey.ExportSubjectPublicKeyInfo())));
        
        // Log the root key hash to verify consistency
        session.LogDebugCryptoMaterial("Initiator root key hash: {RootKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(session._rootKey.Value)));
        
        // CRITICAL FIX: Properly derive the sending chain key for the initiator
        using var remoteRatchetKeyImport = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteRatchetKeyImport.ImportSubjectPublicKeyInfo(remoteRatchetPublicKey.Value, out _);
        
        // Store the remote ratchet key for future use
        session._remoteRatchetKey = remoteRatchetPublicKey;
        
        // Derive DH secret using the imported remote ratchet key
        var dhSecret = session._dhRatchetKey.DeriveKeyMaterial(remoteRatchetKeyImport.PublicKey);
        
        session.LogDebugCryptoMaterial("Initiator DH secret hash: {DhSecretHash}", 
            Convert.ToBase64String(SHA256.HashData(dhSecret)));
        
        // Perform KDF to get the initial root key and sending chain key
        var kdfOutput = CryptoUtils.KDF(session._rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        session._rootKey = new RootKey(kdfOutput[..CryptoUtils.KeySize]);
        session._sendingChainKey = new ChainKey(kdfOutput[CryptoUtils.KeySize..]);
        
        // Initialize counters
        session._sendingCounter = 0;
        session._receivingCounter = 0;
        session._previousChainLength = 0;
        
        session._logger.LogInformation("Initiator session fully initialized - Root key hash: {RootKeyHash}, Sending chain key hash: {SendingChainKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(session._rootKey.Value)),
            Convert.ToBase64String(SHA256.HashData(session._sendingChainKey.Value)));
        
        return session;
    }

    public static DoubleRatchetSession AsResponder(
        SharedSecret sharedSecret, 
        RatchetIdentityKey remoteIdentityPublicKey, 
        ECDiffieHellman localRatchetKey,
        ILogger<DoubleRatchetSession> logger,
        CryptographyOptions? options = null)
    {
        logger.LogDebug("Creating responder session");
        
        // Create Double Ratchet session with shared secret
        var session = new DoubleRatchetSession(sharedSecret, remoteIdentityPublicKey, logger, options);
        
        // Set the local ratchet key provided by the caller
        session._dhRatchetKey = localRatchetKey;
        
        // Log the session initialization parameters for debugging
        session.LogDebugCryptoMaterial("Responder DH key hash: {LocalRatchetKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(localRatchetKey.PublicKey.ExportSubjectPublicKeyInfo())));
        
        session.LogDebugCryptoMaterial("Responder root key hash: {RootKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(session._rootKey.Value)));
            
        // CRITICAL FIX: Initialize the responder's session state consistently
        // The responder doesn't set up chain keys immediately; this happens
        // when receiving the first message from the initiator
        session._sendingChainKey = null;  // Will be created during the first DH ratchet
        session._receivingChainKey = null; // Will be created during the first Decrypt
        
        // Initialize counters
        session._sendingCounter = 0;
        session._receivingCounter = 0;
        session._previousChainLength = 0;
        
        session._logger.LogInformation("Responder session initialized - waiting for first message. Root key hash: {RootKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(session._rootKey.Value)));
        
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
            TheirDhRatchetPublicKey = _remoteRatchetKey,
            DhRatchetPrivateKey = _dhRatchetKey is not null ? new PrivateEphemeralKey(_dhRatchetKey.ExportECPrivateKey()) : null,
            TheirIdentityPublicKey = _remoteIdentityPublicKey
        };
    }

    public SessionRatchetMessage Encrypt(Plaintext plaintext)
    {
        if (_sendingChainKey is null)
        {
            if (_remoteRatchetKey is null)
                throw new InvalidOperationException("Remote ratchet key is not available.");
                
            _logger.LogInformation("Initial encryption - Need to perform first ratchet step. " +
                "Current state: SendingCounter={SendingCounter}, PreviousChainLength={PreviousChainLength}", 
                _sendingCounter, _previousChainLength);
                
            // First message, perform initial ratchet
            _dhRatchetKey?.Dispose();
            _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var remoteRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            remoteRatchetKey.ImportSubjectPublicKeyInfo(_remoteRatchetKey.Value, out _);
            
            // CRITICAL FIX: Update previous chain length before ratcheting
            // This preserves the number of messages sent in the previous chain
            _previousChainLength = _sendingCounter;
            _logger.LogDebug("First encrypt: Setting previous chain length to {PreviousChainLength}", _previousChainLength);
            
            var dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteRatchetKey.PublicKey);
            var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
            _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
            _sendingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);
            
            // Add debug logging for initial ratchet
            _logger.LogDebug("Initial ratchet performed");
            LogDebugCryptoMaterial("Remote ratchet key hash: {RemoteRatchetKeyHash}", 
                Convert.ToBase64String(SHA256.HashData(_remoteRatchetKey.Value)));
            LogDebugCryptoMaterial("New root key hash: {RootKeyHash}", 
                Convert.ToBase64String(SHA256.HashData(_rootKey.Value)));
            LogDebugCryptoMaterial("New sending chain key hash: {SendingChainKeyHash}", 
                Convert.ToBase64String(SHA256.HashData(_sendingChainKey.Value)));
                
            // Reset sending counter for new chain
            _sendingCounter = 0;
            
            _logger.LogInformation("Initial ratchet complete - New state: SendingCounter={SendingCounter}, PreviousChainLength={PreviousChainLength}",
                _sendingCounter, _previousChainLength);
        }

        var messageKey = CryptoUtils.KDF(null, _sendingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
        _sendingChainKey = new ChainKey(CryptoUtils.KDF(null, _sendingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

        LogDebugCryptoMaterial("Encryption message key hash: {MessageKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(messageKey)));
        
        // Add message key debug logging
        LogDebugCryptoMaterial("Message key hash: {MessageKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(messageKey)));
        LogDebugCryptoMaterial("New sending chain key hash: {SendingChainKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(_sendingChainKey.Value)));
        _logger.LogDebug("Sending counter: {SendingCounter}, Previous chain length: {PreviousChainLength}", 
            _sendingCounter, _previousChainLength);

        // Create ephemeral key from our current ratchet key
        var ourPublicKey = new RatchetEphemeralKey(_dhRatchetKey!.PublicKey.ExportSubjectPublicKeyInfo());
        
        // Encrypt plaintext
        var header = (ourPublicKey, _sendingCounter, _previousChainLength);
        var associatedData = SessionRatchetMessage.GetAssociatedData(header, new byte[0]);
        
        // Log detailed header and associated data information
        _logger.LogInformation("Encryption header - RatchetKey hash: {RatchetKeyHash}, Counter: {Counter}, PreviousChainLength: {PreviousChainLength}", 
            Convert.ToBase64String(SHA256.HashData(ourPublicKey.Value)),
            _sendingCounter,
            _previousChainLength);
        _logger.LogInformation("Encryption associated data hash: {AssociatedDataHash}", 
            Convert.ToBase64String(SHA256.HashData(associatedData)));
            
        var ciphertext = CryptoUtils.EncryptAesGcm(plaintext.Value, messageKey, associatedData);
        
        // Create and return ratchet message with encrypted data
        var message = SessionRatchetMessage.Create(ourPublicKey, _sendingCounter, _previousChainLength, new Ciphertext(ciphertext));
        
        // Increment the sending counter after successful encryption and message creation
        _sendingCounter++;
        
        _logger.LogDebug("After encrypt: Sending counter now {SendingCounter}", _sendingCounter);
        
        // DIAGNOSTIC: Log the state before encryption
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogWarning("ENCRYPT - State before encryption: SendingCounter={SendingCounter}, ReceivingCounter={ReceivingCounter}, PreviousChainLength={PreviousChainLength}, DHKeyPair={KeyHash}",
                _sendingCounter, _receivingCounter, _previousChainLength, 
                Convert.ToBase64String(SHA256.HashData(ourPublicKey.Value)));

            // DIAGNOSTIC: Log the header in detail
            _logger.LogWarning("ENCRYPT - Header details: RatchetKey={RatchetKeyHash}, Counter={Counter}, PreviousChainLength={PreviousChainLength}",
                Convert.ToBase64String(SHA256.HashData(header.Item1.Value)),
                header.Item2, header.Item3);

            // DIAGNOSTIC: Log the associated data
            _logger.LogWarning("ENCRYPT - Associated data hash: {AssociatedDataHash}, length: {Length}",
                Convert.ToBase64String(SHA256.HashData(associatedData)),
                associatedData.Length);
            
            // DIAGNOSTIC: Log message details 
            _logger.LogWarning("ENCRYPT - Message created: PayloadHash={PayloadHash}, Length={Length}",
                Convert.ToBase64String(SHA256.HashData(message.Value)),
                message.Value.Length);
        }
            
        return message;
    }

    public Plaintext Decrypt(SessionRatchetMessage cipherMessage)
    {
        // Extract header and ciphertext from the message
        var header = cipherMessage.GetHeader();
        var ciphertext = cipherMessage.GetCiphertext();
        
        // Log message details for debugging
        _logger.LogInformation("Decrypting message with header: RatchetKey hash={RatchetKeyHash}, Counter={Counter}, PreviousChainLength={PreviousChainLength}, Current state: SendingCounter={SendingCounter}, ReceivingCounter={ReceivingCounter}, MyPreviousChainLength={MyPreviousChainLength}", 
            Convert.ToBase64String(SHA256.HashData(header.RatchetKey.Value)),
            header.Counter,
            header.PreviousChainLength,
            _sendingCounter,
            _receivingCounter,
            _previousChainLength);
        
        // First check if this is a message that we have skipped
        if (TryGetSkippedMessageKey(header.RatchetKey, header.Counter, out var skippedMessageKey))
        {
            // Get associated data using tuple header format
            var tupleHeader = (header.RatchetKey, header.Counter, header.PreviousChainLength);
            var associatedData = SessionRatchetMessage.GetAssociatedData(tupleHeader, new byte[0]);
                
            // Decrypt using the skipped message key
            var decryptedBytes = CryptoUtils.DecryptAesGcm(ciphertext.Value, skippedMessageKey, associatedData);
            return new Plaintext(decryptedBytes);
        }

        // Determine if the message is from a new chain (indicated by a new ratchet key)
        // or from the current receiving chain
        bool isFromNewChain = _remoteRatchetKey is null || !_remoteRatchetKey.Equals(header.RatchetKey);

        _logger.LogDebug("Message is from {ChainType} chain. Message counter={Counter}, Our receiving counter={ReceivingCounter}", 
            isFromNewChain ? "new" : "current", header.Counter, _receivingCounter);

        if (isFromNewChain)
        {
            // This message has a new ratchet key from the other party
            _logger.LogDebug("New ratchet key detected, performing DH ratchet");
            
            // CRITICAL FIX: Before DH ratchet, log state values for diagnostics
            LogDebugCryptoMaterial("Before DH ratchet - Root key hash: {RootKeyHash}, Previous chain length: {PreviousChainLength}", 
                Convert.ToBase64String(SHA256.HashData(_rootKey.Value)), 
                _previousChainLength);
                
            // CRITICAL FIX: Store our sending counter as our previous chain length
            // before performing the ratchet
            _previousChainLength = _sendingCounter;
            _logger.LogDebug("Set our previous chain length to {PreviousChainLength} (our sending counter)", _previousChainLength);
            
            // CRITICAL FIX: Perform the DH ratchet with the new ratchet key
            DoDhRatchet(header.RatchetKey);
            
            // CRITICAL FIX: After DH ratchet, log state values for diagnostics
            LogDebugCryptoMaterial("After DH ratchet - Root key hash: {RootKeyHash}, Previous chain length: {PreviousChainLength}", 
                Convert.ToBase64String(SHA256.HashData(_rootKey.Value)), 
                _previousChainLength);
        }
        
        // Symmetrically ratchet forward to the current message.
        SkipMessageKeys(header.Counter);

        if (_receivingChainKey is null)
        {
            throw new InvalidOperationException("Receiving chain key is null.");
        }

        // Derive the message key
        var messageKey = CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);

        LogDebugCryptoMaterial("Decryption message key hash: {MessageKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(messageKey)));

        // Advance the receiving chain key
        _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));
        _receivingCounter++;

        _logger.LogDebug("After decryption: Receiving counter now {ReceivingCounter}", _receivingCounter);

        try
        {
            var associatedData = SessionRatchetMessage.GetAssociatedData(header, new byte[0]);
            
            // Log detailed header and associated data information
            _logger.LogInformation("Decryption header - RatchetKey hash: {RatchetKeyHash}, Counter: {Counter}, PreviousChainLength: {PreviousChainLength}", 
                Convert.ToBase64String(SHA256.HashData(header.RatchetKey.Value)),
                header.Counter,
                header.PreviousChainLength);
            _logger.LogInformation("Decryption associated data hash: {AssociatedDataHash}", 
                Convert.ToBase64String(SHA256.HashData(associatedData)));
                
            var decryptedBytes = CryptoUtils.DecryptAesGcm(ciphertext.Value, messageKey, associatedData);
            return new Plaintext(decryptedBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Decryption failed: {Message}", ex.Message);
            throw;
        }
    }

    private bool TryGetSkippedMessageKey(RatchetEphemeralKey ratchetKey, ulong counter, out byte[] messageKey)
    {
        var key = new SkippedMessageKeyIdentifier(ratchetKey, counter);
        if (_skippedMessageKeys.TryGetValue(key, out messageKey))
        {
            _skippedMessageKeys.Remove(key);
            LogDebugCryptoMaterial("Using skipped message key for ratchet key hash {RatchetKeyHash} and counter {Counter}", 
                Convert.ToBase64String(SHA256.HashData(ratchetKey.Value)), counter);
            return true;
        }
        
        messageKey = Array.Empty<byte>();
        return false;
    }

    private void SkipMessageKeys(ulong until)
    {
        // Only skip keys if the until value is greater than the current receiving counter
        if (until <= _receivingCounter || _receivingChainKey == null)
        {
            return;
        }
        
        _logger.LogDebug("Skipping message keys from {ReceivingCounter} to {Until}", _receivingCounter, until);

        // Skip enough message keys to get to the target counter
        while (_receivingCounter < until)
        {
            // Store the skipped message key for future use
            var messageKey = CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);
            
            // Create a tuple key with the current ratchet key and counter
            var key = new SkippedMessageKeyIdentifier(_remoteRatchetKey!, _receivingCounter);
            _skippedMessageKeys[key] = messageKey;
            
            // Prevent memory attacks by limiting the number of skipped messages
            if (_skippedMessageKeys.Count > MaxSkippedMessages)
            {
                // Remove oldest key - need to implement a proper way to determine this
                var oldestKey = _skippedMessageKeys.Keys.First();
                _skippedMessageKeys.Remove(oldestKey);
                _logger.LogDebug("Removed oldest skipped message key to prevent DoS");
            }

            // Advance the receiving chain key
            _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));
            _receivingCounter++;
        }
    }

    private void DoDhRatchet(RatchetEphemeralKey remoteRatchetKey)
    {
        // CRITICAL FIX: We no longer update previous chain length here because
        // it's now set correctly in Decrypt() from the message header
        // before calling this method
        LogDebugCryptoMaterial("Using previous chain length: {PreviousChainLength}", 
            _previousChainLength);
            
        // Enhanced logging to debug session state
        _logger.LogInformation("DoDhRatchet - Initial state: SendingCounter={SendingCounter}, ReceivingCounter={ReceivingCounter}, PreviousChainLength={PreviousChainLength}", 
            _sendingCounter, _receivingCounter, _previousChainLength);
        
        // Update remote ratchet key
        _remoteRatchetKey = remoteRatchetKey;
        
        // Save old DH key for disposal
        var oldDhKey = _dhRatchetKey;
        
        // Generate new DH key pair
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // DH ratchet step 1 - use old private key with new remote public key
        using var remoteKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteKey.ImportSubjectPublicKeyInfo(remoteRatchetKey.Value, out _);
        
        var dhSecret = oldDhKey!.DeriveKeyMaterial(remoteKey.PublicKey);
        
        // Important: Log this to help diagnose session state symmetry
        LogDebugCryptoMaterial("DH Secret 1 hash: {DhSecretHash}", 
            Convert.ToBase64String(SHA256.HashData(dhSecret)));
            
        var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _receivingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);
        
        LogDebugCryptoMaterial("After DH step 1: Root key hash: {RootKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(_rootKey.Value)));
        
        // Reset receiving counter for the new receiving chain
        _receivingCounter = 0;
        
        // DH ratchet step 2 - use new private key with remote public key
        dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteKey.PublicKey);
        
        // Important: Log this to help diagnose session state symmetry
        LogDebugCryptoMaterial("DH Secret 2 hash: {DhSecretHash}", 
            Convert.ToBase64String(SHA256.HashData(dhSecret)));
            
        kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _sendingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);
        
        LogDebugCryptoMaterial("After DH step 2: Root key hash: {RootKeyHash}", 
            Convert.ToBase64String(SHA256.HashData(_rootKey.Value)));
        
        // Reset sending counter for the new sending chain
        _sendingCounter = 0;
        
        // Enhanced logging to debug final session state after ratchet
        _logger.LogInformation("DoDhRatchet - Final state: SendingCounter={SendingCounter}, ReceivingCounter={ReceivingCounter}, PreviousChainLength={PreviousChainLength}", 
            _sendingCounter, _receivingCounter, _previousChainLength);
            
        // Dispose of old DH key
        oldDhKey.Dispose();
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

    private void LogDebugCryptoMaterial(string message, params object[] args)
    {
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogDebug(message, args);
        }
    }

    public class DoubleRatchetSessionState
    {
        public RootKey? RootKey { get; set; }
        public ChainKey? SendingChainKey { get; set; }
        public ChainKey? ReceivingChainKey { get; set; }
        public ulong SendingCounter { get; set; }
        public ulong ReceivingCounter { get; set; }
        public ulong PreviousChainLength { get; set; }
        public Dictionary<SkippedMessageKeyIdentifier, byte[]> SkippedMessageKeys { get; set; } = new();
        public RatchetIdentityKey? TheirIdentityPublicKey { get; set; }
        public RatchetEphemeralKey? TheirDhRatchetPublicKey { get; set; }
        public PrivateEphemeralKey? DhRatchetPrivateKey { get; set; }
    }
}