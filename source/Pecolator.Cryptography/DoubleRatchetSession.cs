using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

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

        public DoubleRatchetSession(byte[] sharedSecret, ECDiffieHellman identityKey, SessionRole role, byte[]? remoteInitialRatchetKey = null)
        {
            _role = role;
            _identityKey = identityKey; // Reference to the long-term key
            _rootKey = sharedSecret;
            _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            if (role == SessionRole.Initiator)
            {
                if (remoteInitialRatchetKey is null)
                {
                    throw new ArgumentNullException(nameof(remoteInitialRatchetKey), "Initiator requires an initial remote ratchet key.");
                }
                _remoteRatchetKeyBytes = remoteInitialRatchetKey;
                
                // Perform initial DH ratchet to get a sending key
                using var remoteKey = ECDiffieHellman.Create();
                remoteKey.ImportSubjectPublicKeyInfo(_remoteRatchetKeyBytes, out _);
                var dhResult = _dhRatchetKey.DeriveKeyMaterial(remoteKey.PublicKey);
                (_rootKey, _sendingChainKey) = KDF_RK(_rootKey, dhResult);
            }
            // Responder waits for the first message to perform its first DH ratchet.
        }

        public RatchetMessage Encrypt(byte[] plaintext)
        {
            if (_sendingChainKey is null)
            {
                throw new InvalidOperationException("Session is not initialized for sending. The responder must receive a message before sending.");
            }

            var (newSendingKey, messageKey) = KDF_CK(_sendingChainKey);
            _sendingChainKey = newSendingKey;
            _sendingCounter++;

            var ephemeralPublicKey = _dhRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();
            var ciphertextWithTag = EncryptAesGcm(messageKey, (uint)_sendingCounter, plaintext, ephemeralPublicKey);

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
                    return DecryptAesGcm(storedMessageKey, messageCounter, ciphertextWithTag, message.EphemeralPublicKey);
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
                var (nextChainKey, skippedMessageKey) = KDF_CK(tempReceivingKey);
                if (_skippedMessageKeys.Count < MaxSkippedMessages)
                {
                    _skippedMessageKeys.Add(i, skippedMessageKey);
                }
                tempReceivingKey = nextChainKey;
            }

            (tempReceivingKey, currentMessageKey) = KDF_CK(tempReceivingKey);

            _receivingChainKey = tempReceivingKey;
            _receivingCounter = (int)messageCounter;

            try
            {
                return DecryptAesGcm(currentMessageKey, messageCounter, ciphertextWithTag, message.EphemeralPublicKey);
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

        private (byte[] nextChainKey, byte[] messageKey) KDF_CK(byte[] chainKey)
        {
            using var hmac = new HMACSHA256(chainKey);
            var buffer = new byte[KeySize];
            
            // Message Key
            hmac.TryComputeHash(new byte[] { 1 }, buffer, out _);
            var messageKey = buffer.ToArray();

            // Next Chain Key
            hmac.TryComputeHash(new byte[] { 2 }, buffer, out _);
            var nextChainKey = buffer.ToArray();

            return (nextChainKey, messageKey);
        }

        private (byte[] newRootKey, byte[] newChainKey) KDF_RK(byte[] rootKey, byte[] dhResult)
        {
            var prk = HKDF.Extract(HashAlgorithmName.SHA256, dhResult, rootKey);
            var newRootKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, System.Text.Encoding.UTF8.GetBytes("dr-root"));
            var newChainKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, System.Text.Encoding.UTF8.GetBytes("dr-chain"));
            return (newRootKey, newChainKey);
        }

        private byte[] EncryptAesGcm(byte[] key, uint counter, byte[] plaintext, byte[] associatedData)
        {
            var nonce = new byte[12];
            BitConverter.GetBytes(counter).CopyTo(nonce, 0);

            using var aes = new AesGcm(key, 16);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var result = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(result, 0);
            tag.CopyTo(result, ciphertext.Length);
            return result;
        }

        private byte[] DecryptAesGcm(byte[] key, uint counter, byte[] ciphertextWithTag, byte[] associatedData)
        {
            var nonce = new byte[12];
            BitConverter.GetBytes(counter).CopyTo(nonce, 0);

            using var aes = new AesGcm(key, 16);
            var tag = new byte[16];
            var ciphertext = new byte[ciphertextWithTag.Length - 16];

            Array.Copy(ciphertextWithTag, ciphertextWithTag.Length - 16, tag, 0, 16);
            Array.Copy(ciphertextWithTag, 0, ciphertext, 0, ciphertextWithTag.Length - 16);

            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

            return plaintext;
        }

        public void Dispose()
        {
            // Only dispose the ephemeral ratchet key. The identity key is owned by the caller.
            _dhRatchetKey?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
