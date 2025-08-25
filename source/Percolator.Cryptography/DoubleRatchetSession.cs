using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class DoubleRatchetSession : IDisposable
{
    private const int MaxSkippedMessages = 1000;

    private RootKey _rootKey;
    private bool _ratchetFlag = false; 
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
    private readonly CryptographyOptions _cryptographyOptions;

    // Internal get-only properties for testing and debugging
    internal RootKey RootKey => _rootKey;
    internal bool RatchetFlag => _ratchetFlag;
    internal ChainKey? SendingChainKey => _sendingChainKey;
    internal ChainKey? ReceivingChainKey => _receivingChainKey;
    internal ulong SendingCounter => _sendingCounter;
    internal ulong ReceivingCounter => _receivingCounter;
    internal ulong PreviousChainLength => _previousChainLength;
    internal RatchetEphemeralKey? RemoteRatchetKey => _remoteRatchetKey;
    internal RatchetIdentityKey RemoteIdentityPublicKey => _remoteIdentityPublicKey;
    internal IReadOnlyDictionary<SkippedMessageKeyIdentifier, byte[]> SkippedMessageKeys => _skippedMessageKeys;
    internal CryptographyOptions CryptographyOptions => _cryptographyOptions;
    
    /// <summary>
    /// Returns the current DH ratchet private key as a byte array, or null if no key is available.
    /// Used for testing purposes only.
    /// </summary>
    internal byte[]? GetDhRatchetPrivateKeyBytes()
    {
        if (_dhRatchetKey == null)
        {
            return null;
        }
        
        return _dhRatchetKey.ExportECPrivateKey();
    }

    private DoubleRatchetSession(SharedSecret sharedSecret, RatchetIdentityKey remoteIdentityPublicKey, ILogger<DoubleRatchetSession> logger, IOptions<CryptographyOptions> options)
    {
        _remoteIdentityPublicKey = remoteIdentityPublicKey;
        _rootKey = new RootKey(sharedSecret.Value);
        _logger = logger;
        _cryptographyOptions = options.Value;
    }

    public DoubleRatchetSession(DoubleRatchetSessionState state, ILogger<DoubleRatchetSession> logger, IOptions<CryptographyOptions> options)
    {
        _rootKey = state.RootKey ?? throw new ArgumentNullException(nameof(state.RootKey));
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
        _remoteIdentityPublicKey = state.TheirIdentityPublicKey ?? throw new ArgumentNullException(nameof(state.TheirIdentityPublicKey));
        _logger = logger;
        _cryptographyOptions = options.Value;
        _ratchetFlag = state.RatchetFlag;
    }

    public static DoubleRatchetSession AsInitiator(
        SharedSecret sharedSecret,
        RatchetIdentityKey remoteIdentityPublicKey,
        RatchetEphemeralKey remoteRatchetPublicKey,
        ECDiffieHellman localEphemeralKey,
        ILogger<DoubleRatchetSession> logger,
        IOptions<CryptographyOptions> cryptographyOptions)
    {
        logger.LogDebug("Creating initiator session with initial state from X3DH handshake.");

        // Create a new session using a basic constructor.
        // We assume a private constructor exists that initializes the logger and options.
        var session = new DoubleRatchetSession(sharedSecret,remoteIdentityPublicKey, logger, cryptographyOptions);

        // 1. The initial RootKey IS the shared secret from the handshake.
        session._rootKey = new RootKey(sharedSecret.Value);

        // 2. Store the public keys of the remote party (the responder).
        session._remoteRatchetKey = remoteRatchetPublicKey;

        // 3. Initialize all counters and flags to their default starting state.
        //    No messages have been sent or received, and no ratchet has occurred yet.
        session._sendingCounter = 0;
        session._receivingCounter = 0;
        session._previousChainLength = 0;
        session._ratchetFlag = false; 

        // 4. There are no chain keys or local DH keys yet. They will be created during
        //    the first call to the Encrypt() method.
        session._sendingChainKey = null;
        session._receivingChainKey = null;
        session._dhRatchetKey = localEphemeralKey;

        if (cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            logger.LogInformation("Initiator session created. Root key : {RootKey}",
                Convert.ToBase64String(session._rootKey.Value));
        }

        return session;
    }

    public static DoubleRatchetSession AsResponder(
        SharedSecret sharedSecret, 
        RatchetIdentityKey remoteIdentityPublicKey,  
        RatchetEphemeralKey remoteRatchetPublicKey, 
        ECDiffieHellman localRatchetKey,
        ILogger<DoubleRatchetSession> logger,
        IOptions<CryptographyOptions> cryptographyOptions)
    {
        logger.LogDebug("Creating responder session");
        
        // Create Double Ratchet session with shared secret
        var session = new DoubleRatchetSession(sharedSecret, remoteIdentityPublicKey, logger, cryptographyOptions);
        
        // Set the local ratchet key provided by the caller
        session._dhRatchetKey = localRatchetKey;
        session._remoteRatchetKey = remoteRatchetPublicKey;

        if (cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            logger.LogInformation("Responder DH key: {LocalRatchetKey}", 
                Convert.ToBase64String(localRatchetKey.PublicKey.ExportSubjectPublicKeyInfo()));
            logger.LogInformation("Responder root key: {RootKey}", 
                Convert.ToBase64String(session._rootKey.Value));
        }
            
        // CRITICAL FIX: Initialize the responder's session state consistently
        // The responder doesn't set up chain keys immediately; this happens
        // when receiving the first message from the initiator
        session._sendingChainKey = null;  
        session._receivingChainKey = null; 

        // Initialize counters
        session._sendingCounter = 0;
        session._receivingCounter = 0;
        session._previousChainLength = 0;

        if (cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            logger.LogInformation(
                "Responder session initialized - waiting for first message. Root key : {RootKey}",
                Convert.ToBase64String(session._rootKey.Value));
        }

        return session;
    }
    
    public DoubleRatchetSessionState GetState()
    {
        return new DoubleRatchetSessionState
        {
            RootKey = _rootKey,
            RatchetFlag = _ratchetFlag,
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

    private void PerformSendingRatchet()
    {
        if (_remoteRatchetKey is null)
            throw new InvalidOperationException("Cannot perform sender ratchet: Remote ratchet key is not available.");

        _logger.LogInformation("Performing sender's DH ratchet step.");

        // This is the CRITICAL FIX for your unit test.
        // Preserve the number of messages sent in the chain we are about to replace.
        _previousChainLength = _sendingCounter;
        _logger.LogDebug("Setting previous chain length to {PreviousChainLength}", _previousChainLength);

        // Generate a new key pair for this new sending chain.
        _dhRatchetKey?.Dispose();
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Perform DH with our new private key and the other party's public key.
        using var remoteKeyHandle = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteKeyHandle.ImportSubjectPublicKeyInfo(_remoteRatchetKey.Value, out _);
        var dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteKeyHandle.PublicKey);

        // Derive the new Root Key and Sending Chain Key from the DH secret.
        var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _sendingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);

        // Reset the sending counter for the new chain.
        _sendingCounter = 0;

        // The ratchet is complete, so we clear the flag.
        _ratchetFlag = false;

        _logger.LogInformation(
            "Sender's ratchet complete. New state: SendingCounter={SendingCounter}, PreviousChainLength={PreviousChainLength}",
            _sendingCounter, _previousChainLength);
    }

    public SessionRatchetMessage Encrypt(Plaintext plaintext)
    {
        // A ratchet is needed for the initiator's first message OR if the other party just ratcheted.
        if (_sendingChainKey is null || _ratchetFlag)
        {
            PerformSendingRatchet();
        }

        // Now, derive keys and encrypt as usual from the current sending chain.
        var messageKey = CryptoUtils.KDF(null, _sendingChainKey!.Value, "message-key-kdf", CryptoUtils.KeySize);
        _sendingChainKey =
            new ChainKey(CryptoUtils.KDF(null, _sendingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));

        // Create the header with our current ratchet public key and counters.
        var ourPublicKey = new RatchetEphemeralKey(_dhRatchetKey!.PublicKey.ExportSubjectPublicKeyInfo());
        var headerTuple = (ourPublicKey, _sendingCounter, _previousChainLength);
        var associatedData = SessionRatchetMessage.GetAssociatedData(headerTuple, new byte[0]);

        if (_cryptographyOptions.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Encrypt header - RatchetKey: {RatchetKey}, Counter: {Counter}, PreviousChainLength: {PreviousChainLength}",
                Convert.ToBase64String(ourPublicKey.Value),
                _sendingCounter,
                _previousChainLength);
            _logger.LogInformation("Encrypt associated data: {AssociatedData}",
                Convert.ToBase64String(associatedData));
            _logger.LogInformation("Encrypt message key: {MessageKey}",
                Convert.ToBase64String(messageKey));
        }

        // Encrypt the plaintext.
        var ciphertext = CryptoUtils.EncryptAesGcm(plaintext.Value, messageKey, associatedData);

        // Create and return the final message object.
        var message = SessionRatchetMessage.Create(ourPublicKey, _sendingCounter, _previousChainLength,
            new Ciphertext(ciphertext));

        // Increment the sending counter for the next message in this chain.
        _sendingCounter++;

        // Your diagnostic logging can remain here...

        return message;
    }

    public Plaintext Decrypt(SessionRatchetMessage cipherMessage)
    {
        // Extract header and ciphertext from the message
        var header = cipherMessage.GetHeader();
        var ciphertext = cipherMessage.GetCiphertext();

        if (_cryptographyOptions.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Decrypting message with header: RatchetKey hash={RatchetKey}", 
                Convert.ToBase64String(header.RatchetKey.Value));
        }
        // Log message details for debugging
        _logger.LogDebug("Decrypting message with header: Counter={Counter}, PreviousChainLength={PreviousChainLength}, Current state: SendingCounter={SendingCounter}, ReceivingCounter={ReceivingCounter}, MyPreviousChainLength={MyPreviousChainLength}", 
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

            if (_cryptographyOptions.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("Before DH ratchet - Root key : {RootKey}, Previous chain length: {PreviousChainLength}", 
                    Convert.ToBase64String(_rootKey.Value), 
                    _previousChainLength);
            }
                
            // CRITICAL FIX: Store our sending counter as our previous chain length
            // before performing the ratchet
            _previousChainLength = _sendingCounter;
            _logger.LogDebug("Set our previous chain length to {PreviousChainLength} (our sending counter)", _previousChainLength);
            
            // CRITICAL FIX: Perform the DH ratchet with the new ratchet key
            DoDhRatchet(header.RatchetKey);

            if (_cryptographyOptions.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("After DH ratchet - Root key : {RootKey}, Previous chain length: {PreviousChainLength}", 
                    Convert.ToBase64String(_rootKey.Value), 
                    _previousChainLength);
            }
        }
        
        // Symmetrically ratchet forward to the current message.
        SkipMessageKeys(header.Counter);

        if (_receivingChainKey is null)
        {
            throw new InvalidOperationException("Receiving chain key is null.");
        }

        // Derive the message key
        var messageKey = CryptoUtils.KDF(null, _receivingChainKey.Value, "message-key-kdf", CryptoUtils.KeySize);

        if (_cryptographyOptions.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Decryption message key : {MessageKey}", 
                Convert.ToBase64String(messageKey));
        }

        // Advance the receiving chain key
        _receivingChainKey = new ChainKey(CryptoUtils.KDF(null, _receivingChainKey.Value, "ratchet-chain-kdf", CryptoUtils.KeySize));
        _receivingCounter++;

        _logger.LogDebug("After decryption: Receiving counter now {ReceivingCounter}", _receivingCounter);

        try
        {
            var associatedData = SessionRatchetMessage.GetAssociatedData(header, new byte[0]);

            if (_cryptographyOptions.EnableCryptographicMaterialLogging)
            {
                // Log detailed header and associated data information
                _logger.LogInformation("Decryption header - RatchetKey : {RatchetKey}, Counter: {Counter}, PreviousChainLength: {PreviousChainLength}", 
                    Convert.ToBase64String(header.RatchetKey.Value),
                    header.Counter,
                    header.PreviousChainLength);
                _logger.LogInformation("Decryption associated data : {AssociatedData}", 
                    Convert.ToBase64String(associatedData));
                _logger.LogInformation("Decryption message key (again) : {MessageKey}",
                    Convert.ToBase64String(messageKey));
            }
                
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
            if (_cryptographyOptions.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("Using skipped message key for ratchet key  {RatchetKey} and counter {Counter}", 
                    Convert.ToBase64String(ratchetKey.Value), counter);
            }
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
        _logger.LogInformation("DoDhRatchet - Performing receiver's ratchet step.");

        // Update the remote ratchet key we've received from the other party.
        _remoteRatchetKey = remoteRatchetKey;

        // Perform a single DH calculation using our CURRENT private ratchet key
        // and the new remote public key from the message header.
        using var remoteKeyHandle = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteKeyHandle.ImportSubjectPublicKeyInfo(remoteRatchetKey.Value, out _);

        // _dhRatchetKey is our current key pair.
        var dhSecret = _dhRatchetKey!.DeriveKeyMaterial(remoteKeyHandle.PublicKey);

        if (_cryptographyOptions.EnableCryptographicMaterialLogging)
        {
            // Log the secret and root key for diagnostics.
            _logger.LogInformation("DH Secret : {DhSecret}",
                Convert.ToBase64String(dhSecret));
            _logger.LogInformation("Old Root key : {OldRootKey}",
                Convert.ToBase64String(_rootKey.Value));
        }

        // Use a KDF to derive the new Root Key and a new Receiving Chain Key from the DH secret.
        var kdfResult = CryptoUtils.KDF(_rootKey.Value, dhSecret, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = new RootKey(kdfResult[..CryptoUtils.KeySize]);
        _receivingChainKey = new ChainKey(kdfResult[CryptoUtils.KeySize..]);

        if (_cryptographyOptions.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("New Root key : {NewRootKey}",
                Convert.ToBase64String(_rootKey.Value));
            _logger.LogInformation("New Receiving Chain key : {NewReceivingChainKey}",
                Convert.ToBase64String(_receivingChainKey.Value));
        }

        // We also need to generate a new key pair for OUR next message, but we don't use it yet.
        _dhRatchetKey.Dispose(); // Dispose of the old key pair.
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Reset the receiving counter for this new chain.
        _receivingCounter = 0;

        _logger.LogInformation("DoDhRatchet - Receiver's ratchet step complete.");
        _ratchetFlag = true;
    }

    public void Dispose()
    {
        _dhRatchetKey?.Dispose();
        GC.SuppressFinalize(this);
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
        public bool RatchetFlag { get; set; }
    }
}