using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pecolator.Cryptography
{
    public class DoubleRatchetSession : IDisposable
    {
        private const int KeySize = 32; // 256 bits
        private const int HeaderSize = 4; // For message counter (uint)
        private const int MaxSkippedMessages = 1000;

        private readonly SessionRole _role;
        private byte[] _rootKey;
        private readonly ECDiffieHellman _identityKey; // Long-term key, not owned by session
        private ECDiffieHellman _dhRatchetKey; // Ephemeral ratchet key, owned by session
        private byte[]? _remoteRatchetKeyBytes;

        private byte[]? _sendingChainKey;
        private byte[]? _receivingChainKey;
        private int _sendingCounter;
        private int _receivingCounter;
        private readonly Dictionary<uint, byte[]> _skippedMessageKeys = new();

        internal byte[]? SendingChainKey => _sendingChainKey;
        internal byte[]? ReceivingChainKey => _receivingChainKey;

        public byte[] RatchetPublicKey => _dhRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();
        public byte[] IdentityPublicKey => _identityKey.PublicKey.ExportSubjectPublicKeyInfo();

        public DoubleRatchetSession(byte[] sharedSecret, ECDiffieHellman identityKey, SessionRole role, byte[]? remoteInitialRatchetKey = null, ECDiffieHellman? ownInitialRatchetKey = null)
        {
            _role = role;
            _identityKey = identityKey; // Reference to the long-term key
            _rootKey = sharedSecret;

            if (role == SessionRole.Initiator)
            {
                if (remoteInitialRatchetKey is null)
                {
                    throw new ArgumentNullException(nameof(remoteInitialRatchetKey), "Initiator requires an initial remote ratchet key.");
                }
                _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                _remoteRatchetKeyBytes = remoteInitialRatchetKey;
                
                // Perform initial DH ratchet to get a sending key
                using var remoteKey = ECDiffieHellman.Create();
                remoteKey.ImportSubjectPublicKeyInfo(_remoteRatchetKeyBytes, out _);
                var dhResult = _dhRatchetKey.DeriveKeyMaterial(remoteKey.PublicKey);
                (_rootKey, _sendingChainKey) = KDF_RK(_rootKey, dhResult);
            }
            else // Responder
            {
                if (ownInitialRatchetKey is null)
                {
                    throw new ArgumentNullException(nameof(ownInitialRatchetKey), "Responder requires its own initial ratchet key (pre-key).");
                }
                // Create a new key instance from the provided key's parameters to avoid lifecycle conflicts.
                _dhRatchetKey = ECDiffieHellman.Create(ownInitialRatchetKey.ExportParameters(true));
            }
        }

        private DoubleRatchetSession(DoubleRatchetSessionState state, ECDiffieHellman identityKey)
        {
            _role = state.Role;
            _rootKey = state.RootKey;
            _remoteRatchetKeyBytes = state.RemoteRatchetKeyBytes;
            _sendingChainKey = state.SendingChainKey;
            _receivingChainKey = state.ReceivingChainKey;
            _sendingCounter = state.SendingCounter;
            _receivingCounter = state.ReceivingCounter;
            _skippedMessageKeys = state.SkippedMessageKeys;

            _identityKey = identityKey;
            _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _dhRatchetKey.ImportECPrivateKey(state.RatchetKeyPrivate, out _);
        }

        public static DoubleRatchetSession LoadState(byte[] stateBytes, ECDiffieHellman identityKey)
        {
            var state = JsonSerializer.Deserialize<DoubleRatchetSessionState>(stateBytes);
            if (state is null)
            {
                throw new ArgumentException("Invalid session state data.", nameof(stateBytes));
            }
            return new DoubleRatchetSession(state, identityKey);
        }

        public byte[] SaveState()
        {
            var state = new DoubleRatchetSessionState
            {
                Role = _role,
                RootKey = _rootKey,
                RemoteRatchetKeyBytes = _remoteRatchetKeyBytes,
                SendingChainKey = _sendingChainKey,
                ReceivingChainKey = _receivingChainKey,
                SendingCounter = _sendingCounter,
                ReceivingCounter = _receivingCounter,
                SkippedMessageKeys = _skippedMessageKeys,
                RatchetKeyPrivate = _dhRatchetKey.ExportECPrivateKey()
            };

            return JsonSerializer.SerializeToUtf8Bytes(state);
        }

        public RatchetMessage Encrypt(byte[] plaintext)
        {
            if (_sendingChainKey is null)
            {
                throw new InvalidOperationException("Session is not initialized for sending. The responder must receive a message before sending.");
            }

            var (messageKey, nextSendingKey) = CryptoUtils.KDF(_sendingChainKey);
            _sendingChainKey = nextSendingKey;
            _sendingCounter++;

            var ephemeralPublicKey = _dhRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();
            var ciphertextWithTag = CryptoUtils.EncryptAesGcm(messageKey, (uint)_sendingCounter, plaintext, ephemeralPublicKey);

            var header = BitConverter.GetBytes((uint)_sendingCounter);
            var ciphertextPayload = new byte[HeaderSize + ciphertextWithTag.Length];
            Buffer.BlockCopy(header, 0, ciphertextPayload, 0, HeaderSize);
            Buffer.BlockCopy(ciphertextWithTag, 0, ciphertextPayload, HeaderSize, ciphertextWithTag.Length);

            return new RatchetMessage(ephemeralPublicKey, ciphertextPayload);
        }

        public byte[] Decrypt(RatchetMessage message)
        {
            var ciphertextWithTag = new byte[message.CiphertextPayload.Length - HeaderSize];
            Buffer.BlockCopy(message.CiphertextPayload, HeaderSize, ciphertextWithTag, 0, ciphertextWithTag.Length);

            var header = new byte[HeaderSize];
            Buffer.BlockCopy(message.CiphertextPayload, 0, header, 0, HeaderSize);
            var messageCounter = BitConverter.ToUInt32(header, 0);

            // A new remote key always triggers a DH ratchet step.
            if (!message.EphemeralPublicKey.SequenceEqual(_remoteRatchetKeyBytes ?? Array.Empty<byte>()))
            {
                DHRatchetStep(message.EphemeralPublicKey);
            }

            // Try to decrypt with a saved key for a skipped message.
            if (_skippedMessageKeys.TryGetValue(messageCounter, out var storedMessageKey))
            {
                _skippedMessageKeys.Remove(messageCounter);
                try
                {
                    return CryptoUtils.DecryptAesGcm(storedMessageKey, messageCounter, ciphertextWithTag, message.EphemeralPublicKey);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new InvalidMessageOrderException("AEAD authentication failed for a skipped message.", ex);
                }
            }

            if (messageCounter <= _receivingCounter)
            {
                throw new InvalidMessageOrderException($"Received out-of-order or duplicate message that was not in the cache. Last: {_receivingCounter}, this: {messageCounter}");
            }

            if (messageCounter - _receivingCounter > MaxSkippedMessages)
            {
                throw new InvalidOperationException($"Cannot process message. Exceeds the maximum number of {MaxSkippedMessages} skipped messages.");
            }

            var tempReceivingKey = _receivingChainKey;
            if (tempReceivingKey is null) 
            {
                throw new InvalidMessageOrderException("Cannot decrypt. Receiving chain not initialized. The first message may have been lost.");
            }

            byte[] currentMessageKey;

            for (uint i = (uint)_receivingCounter + 1; i < messageCounter; i++)
            {
                var (skippedMessageKey, nextChainKey) = CryptoUtils.KDF(tempReceivingKey);
                if (_skippedMessageKeys.Count < MaxSkippedMessages)
                {
                    _skippedMessageKeys.Add(i, skippedMessageKey);
                }
                tempReceivingKey = nextChainKey;
            }

            (currentMessageKey, tempReceivingKey) = CryptoUtils.KDF(tempReceivingKey);

            _receivingChainKey = tempReceivingKey;
            _receivingCounter = (int)messageCounter;

            try
            {
                return CryptoUtils.DecryptAesGcm(currentMessageKey, messageCounter, ciphertextWithTag, message.EphemeralPublicKey);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new InvalidMessageOrderException("AEAD authentication failed.", ex);
            }
        }

        private void DHRatchetStep(byte[] newRemoteKeyBytes)
        {        
            _skippedMessageKeys.Clear();
            _receivingCounter = 0;
            _sendingCounter = 0;
            _remoteRatchetKeyBytes = newRemoteKeyBytes;

            using var remoteKey = ECDiffieHellman.Create();
            remoteKey.ImportSubjectPublicKeyInfo(newRemoteKeyBytes, out _);

            // DH for receiving chain
            var dhRecv = _dhRatchetKey.DeriveKeyMaterial(remoteKey.PublicKey);
            (_rootKey, _receivingChainKey) = KDF_RK(_rootKey, dhRecv);

            // DH for sending chain (with new keypair)
            _dhRatchetKey.Dispose();
            _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var dhSend = _dhRatchetKey.DeriveKeyMaterial(remoteKey.PublicKey);
            (_rootKey, _sendingChainKey) = KDF_RK(_rootKey, dhSend);
        }

        private (byte[] newRootKey, byte[] newChainKey) KDF_RK(byte[] rootKey, byte[] dhResult)
        {
            var prk = HKDF.Extract(HashAlgorithmName.SHA256, dhResult, rootKey);
            var newRootKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, Encoding.UTF8.GetBytes("dr-root"));
            var newChainKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, Encoding.UTF8.GetBytes("dr-chain"));
            return (newRootKey, newChainKey);
        }

        public void Dispose()
        {
            // Only dispose the ephemeral ratchet key. The identity key is owned by the caller.
            _dhRatchetKey?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
