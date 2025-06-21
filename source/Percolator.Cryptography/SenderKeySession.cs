using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Percolator.Cryptography
{
    public class SenderKeySession : IDisposable
    {
        private const int MaxSkippedMessages = 1000;

        private const int MaxMessageKeys = 40;
        public byte[] Context { get; }

        private byte[] _chainKey;
        private uint _iteration = 0;
        private readonly ECDsa _signingKey;
        private readonly Dictionary<uint, byte[]> _messageKeyCache = new();

        public SenderKeySession(byte[] sessionKey, byte[] context)
        {
            Context = context;

            _chainKey = CryptoUtils.KDF(null, sessionKey, "SenderKey-InitialChainKey", CryptoUtils.KeySize);

            var privateKey = CryptoUtils.KDF(null, sessionKey, "SenderKey-SigningKey", CryptoUtils.KeySize);
            _signingKey = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privateKey
            });
        }

        private SenderKeySession(SenderKeySessionState state)
        {
            Context = state.Context;
            _chainKey = state.ChainKey;
            _iteration = state.Iteration;
            _messageKeyCache = state.MessageKeyCache;

            _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _signingKey.ImportPkcs8PrivateKey(state.SigningKeyPrivate, out _);
        }

        public static SenderKeySession LoadState(byte[] encryptedState, byte[] masterKey)
        {
            var plaintextState = CryptoUtils.DecryptAtRest(masterKey, encryptedState, Encoding.UTF8.GetBytes("SenderKeySessionState"));
            if (plaintextState is null)
            {
                throw new CryptographicException("Failed to decrypt or authenticate session state.");
            }

            var state = JsonSerializer.Deserialize<SenderKeySessionState>(plaintextState);
            if (state is null)
            {
                throw new InvalidOperationException("Failed to deserialize session state.");
            }

            return new SenderKeySession(state);
        }

        public byte[] SaveState(byte[] masterKey)
        {
            var state = new SenderKeySessionState
            {
                Context = Context,
                ChainKey = _chainKey,
                Iteration = _iteration,
                MessageKeyCache = _messageKeyCache,
                SigningKeyPrivate = _signingKey.ExportECPrivateKey()
            };

            var plaintextState = JsonSerializer.SerializeToUtf8Bytes(state);
            return CryptoUtils.EncryptAtRest(masterKey, plaintextState, Encoding.UTF8.GetBytes("SenderKeySessionState"));
        }

        public SenderKeyMessage Encrypt(byte[] plaintext)
        {
            var messageKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-MessageKey", CryptoUtils.KeySize);
            _chainKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-ChainKey", CryptoUtils.KeySize);

            var ciphertext = CryptoUtils.EncryptAesGcm(messageKey, _iteration, plaintext, Context);
            var signature = SignMessage(ciphertext);

            var message = new SenderKeyMessage
            {
                Iteration = _iteration,
                Ciphertext = ciphertext,
                Signature = signature
            };

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
                    var plaintext = CryptoUtils.DecryptAesGcm(cachedKey, message.Iteration, message.Ciphertext!, Context);
                    _messageKeyCache.Remove(message.Iteration);
                    return plaintext;
                }
                if (_messageKeyCache.ContainsKey(message.Iteration))
                {
                    throw new CryptographicException("Received an old message that was already decrypted.");
                }
                throw new CryptographicException("Received an old message that was not in the cache.");
            }

            if (message.Iteration > _iteration)
            {
                while (_iteration < message.Iteration)
                {
                    var skippedMessageKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-MessageKey", CryptoUtils.KeySize);
                    _messageKeyCache.Add(_iteration, skippedMessageKey);
                    _chainKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-ChainKey", CryptoUtils.KeySize);
                    _iteration++;
                }
            }

            // At this point, message.Iteration == _iteration
            var messageKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-MessageKey", CryptoUtils.KeySize);
            _chainKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-ChainKey", CryptoUtils.KeySize);
            _iteration++;

            return CryptoUtils.DecryptAesGcm(messageKey, message.Iteration, message.Ciphertext!, Context);
        }

        private byte[] SignMessage(byte[] ciphertext)
        {
            var dataToSign = new byte[Context.Length + sizeof(uint) + ciphertext.Length];
            Context.CopyTo(dataToSign, 0);
            BitConverter.GetBytes(_iteration).CopyTo(dataToSign, Context.Length);
            ciphertext.CopyTo(dataToSign, Context.Length + sizeof(uint));
            return _signingKey.SignData(dataToSign, HashAlgorithmName.SHA256);
        }

        private bool VerifySignature(SenderKeyMessage message)
        {
            if (message.Ciphertext is null || message.Signature is null)
            {
                return false;
            }

            var dataToVerify = new byte[Context.Length + sizeof(uint) + message.Ciphertext.Length];
            Context.CopyTo(dataToVerify, 0);
            BitConverter.GetBytes(message.Iteration).CopyTo(dataToVerify, Context.Length);
            message.Ciphertext.CopyTo(dataToVerify, Context.Length + sizeof(uint));
            return _signingKey.VerifyData(dataToVerify, message.Signature, HashAlgorithmName.SHA256);
        }

        public void Dispose()
        {
            _signingKey.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
