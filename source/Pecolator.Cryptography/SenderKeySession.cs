using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pecolator.Cryptography
{
    public class SenderKeySession : IDisposable
    {
        private const int MaxSkippedMessages = 1000;

        private const int MaxMessageKeys = 40;
        private readonly byte[] _context;
        public byte[] Context => (byte[])_context.Clone();

        private readonly ECDsa _signingKey;
        private byte[] _chainKey;
        private uint _iteration;
        private readonly Dictionary<uint, byte[]> _messageKeyCache = new();

        public SenderKeySession(byte[] sessionKey, byte[] context)
        {
            _context = context;
            _chainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, 32, Encoding.UTF8.GetBytes("SenderKey-InitialChainKey"));

            var privateKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, 32, Encoding.UTF8.GetBytes("SenderKey-SigningKey"));
            _signingKey = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privateKey
            });

            _iteration = 0;
        }

        private SenderKeySession(SenderKeySessionState state)
        {
            _context = state.Context;
            _chainKey = state.ChainKey;
            _iteration = state.Iteration;
            _messageKeyCache = state.MessageKeyCache;

            _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _signingKey.ImportPkcs8PrivateKey(state.SigningKeyPrivate, out _);
        }

        public static SenderKeySession LoadState(byte[] stateBytes)
        {
            var state = JsonSerializer.Deserialize<SenderKeySessionState>(stateBytes);
            if (state is null)
            {
                throw new ArgumentException("Invalid session state data.", nameof(stateBytes));
            }
            return new SenderKeySession(state);
        }

        public byte[] SaveState()
        {
            var state = new SenderKeySessionState
            {
                Context = _context,
                ChainKey = _chainKey,
                Iteration = _iteration,
                MessageKeyCache = _messageKeyCache,
                SigningKeyPrivate = _signingKey.ExportPkcs8PrivateKey()
            };

            return JsonSerializer.SerializeToUtf8Bytes(state);
        }

        public SenderKeyMessage Encrypt(byte[] plaintext)
        {
            var messageKey = KDF_CK(_chainKey, "SenderKey-MessageKey");
            var nextChainKey = KDF_CK(_chainKey, "SenderKey-ChainKey");

            var ciphertext = CryptoUtils.EncryptAesGcm(messageKey, _iteration, plaintext, _context);
            var signature = SignMessage(ciphertext);

            var message = new SenderKeyMessage
            {
                Iteration = _iteration,
                Ciphertext = ciphertext,
                Signature = signature
            };

            _chainKey = nextChainKey;
            _iteration++;

            return message;
        }

        public byte[] Decrypt(SenderKeyMessage message)
        {
            if (!VerifySignature(message))
            {
                throw new CryptographicException("Invalid signature.");
            }

            if (message.Iteration < _iteration)
            {
                if (_messageKeyCache.TryGetValue(message.Iteration, out var cachedKey))
                {
                    var plaintext = CryptoUtils.DecryptAesGcm(cachedKey, message.Iteration, message.Ciphertext!, _context);
                    _messageKeyCache.Remove(message.Iteration);
                    return plaintext;
                }
                throw new InvalidOperationException("Received an old message that was not in the cache.");
            }

            if (message.Iteration > _iteration)
            {
                while (_iteration < message.Iteration)
                {
                    var skippedMessageKey = KDF_CK(_chainKey, "SenderKey-MessageKey");
                    _messageKeyCache[_iteration] = skippedMessageKey;
                    _chainKey = KDF_CK(_chainKey, "SenderKey-ChainKey");
                    _iteration++;
                }
            }

            // At this point, message.Iteration == _iteration
            var messageKey = KDF_CK(_chainKey, "SenderKey-MessageKey");
            _chainKey = KDF_CK(_chainKey, "SenderKey-ChainKey");
            _iteration++;

            return CryptoUtils.DecryptAesGcm(messageKey, message.Iteration, message.Ciphertext!, _context);
        }

        private byte[] SignMessage(byte[] ciphertext)
        {
            var dataToSign = new byte[_context.Length + sizeof(uint) + ciphertext.Length];
            _context.CopyTo(dataToSign, 0);
            BitConverter.GetBytes(_iteration).CopyTo(dataToSign, _context.Length);
            ciphertext.CopyTo(dataToSign, _context.Length + sizeof(uint));
            return _signingKey.SignData(dataToSign, HashAlgorithmName.SHA256);
        }

        private bool VerifySignature(SenderKeyMessage message)
        {
            if (message.Ciphertext is null || message.Signature is null)
            {
                return false;
            }

            var dataToVerify = new byte[_context.Length + sizeof(uint) + message.Ciphertext.Length];
            _context.CopyTo(dataToVerify, 0);
            BitConverter.GetBytes(message.Iteration).CopyTo(dataToVerify, _context.Length);
            message.Ciphertext.CopyTo(dataToVerify, _context.Length + sizeof(uint));
            return _signingKey.VerifyData(dataToVerify, message.Signature, HashAlgorithmName.SHA256);
        }

        private byte[] KDF_CK(byte[] key, string info)
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, Encoding.UTF8.GetBytes(info));
        }

        public void Dispose()
        {
            _signingKey.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
