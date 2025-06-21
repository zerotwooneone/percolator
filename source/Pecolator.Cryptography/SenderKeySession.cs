using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Pecolator.Cryptography
{
    public class SenderKeySession : IDisposable
    {
        private const int MaxSkippedMessages = 1000;

        private readonly byte[] _context;
        private readonly ECDsa _signingKey;
        private byte[] _chainKey;
        private uint _iteration;
        private readonly Dictionary<uint, byte[]> _skippedMessageKeys = new();

        public SenderKeySession(byte[] sessionKey, byte[]? context = null)
        {
            _context = context ?? Array.Empty<byte>();
            _chainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, 32, Encoding.UTF8.GetBytes("SenderKey-InitialChainKey"));

            var privateKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, 32, Encoding.UTF8.GetBytes("SenderKey-SigningKey"));

            _signingKey = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privateKey
            });

            _iteration = 0;
        }

        public SenderKeyMessage Encrypt(byte[] plaintext)
        {
            var (messageKey, nextChainKey) = CryptoUtils.KDF(_chainKey);
            _chainKey = nextChainKey;

            var ciphertextWithTag = CryptoUtils.EncryptAesGcm(messageKey, _iteration, plaintext, null);

            var dataToSign = new byte[_context.Length + sizeof(uint) + ciphertextWithTag.Length];
            _context.CopyTo(dataToSign, 0);
            BitConverter.GetBytes(_iteration).CopyTo(dataToSign, _context.Length);
            ciphertextWithTag.CopyTo(dataToSign, _context.Length + sizeof(uint));
            var signature = _signingKey.SignData(dataToSign, HashAlgorithmName.SHA256);

            var message = new SenderKeyMessage
            {
                Iteration = _iteration,
                Ciphertext = ciphertextWithTag,
                Signature = signature
            };

            _iteration++;

            return message;
        }

        public byte[] Decrypt(SenderKeyMessage message)
        {
            if (message.Ciphertext is null)
            {
                throw new ArgumentNullException(nameof(message.Ciphertext));
            }
            if (message.Signature is null)
            {
                throw new CryptographicException("Message is not signed.");
            }

            var dataToVerify = new byte[_context.Length + sizeof(uint) + message.Ciphertext.Length];
            _context.CopyTo(dataToVerify, 0);
            BitConverter.GetBytes(message.Iteration).CopyTo(dataToVerify, _context.Length);
            message.Ciphertext.CopyTo(dataToVerify, _context.Length + sizeof(uint));

            if (!_signingKey.VerifyData(dataToVerify, message.Signature, HashAlgorithmName.SHA256))
            {
                throw new CryptographicException("Invalid signature.");
            }

            // First, try to find the key in the cache of skipped messages.
            if (_skippedMessageKeys.TryGetValue(message.Iteration, out var storedMessageKey))
            {
                _skippedMessageKeys.Remove(message.Iteration);
                return CryptoUtils.DecryptAesGcm(storedMessageKey, message.Iteration, message.Ciphertext, null);
            }

            // If the message is older than our current state and wasn't in the cache, it's an error.
            if (message.Iteration < _iteration)
            {
                throw new InvalidOperationException("Received an old message that was not in the cache.");
            }

            // If the message is in the future, advance the ratchet and cache the intermediate keys.
            if (message.Iteration - _iteration > MaxSkippedMessages)
            {
                throw new InvalidOperationException($"Cannot process message. Exceeds the maximum number of {MaxSkippedMessages} skipped messages.");
            }

            var tempChainKey = _chainKey;
            for (uint i = _iteration; i < message.Iteration; i++)
            {
                var (skippedMessageKey, nextChainKey) = CryptoUtils.KDF(tempChainKey);
                if (_skippedMessageKeys.Count < MaxSkippedMessages)
                {
                    _skippedMessageKeys.Add(i, skippedMessageKey);
                }
                tempChainKey = nextChainKey;
            }

            // Now, derive the key for the current message and update the session state.
            var (currentMessageKey, finalNextChainKey) = CryptoUtils.KDF(tempChainKey);
            _chainKey = finalNextChainKey;
            _iteration = message.Iteration + 1;

            return CryptoUtils.DecryptAesGcm(currentMessageKey, message.Iteration, message.Ciphertext, null);
        }

        public void Dispose()
        {
            _signingKey?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
