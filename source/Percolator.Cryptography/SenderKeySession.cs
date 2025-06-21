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
        public byte[] SessionKey { get; }

        private byte[] _chainKey;
        private uint _iteration = 0;
        private readonly ECDsa _signingKey;
        private readonly Dictionary<uint, byte[]> _messageKeyCache = new();

        public SenderKeySession(byte[]? sessionKey, byte[] context)
        {
            Context = context;
            SessionKey = sessionKey ?? RandomNumberGenerator.GetBytes(32);

            _chainKey = CryptoUtils.KDF(null, SessionKey, "SenderKey-InitialChainKey", CryptoUtils.KeySize);

            var privateKey = CryptoUtils.KDF(null, SessionKey, "SenderKey-SigningKey", CryptoUtils.KeySize);
            _signingKey = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privateKey
            });
        }

        public SenderKeySession(SenderKeySessionState state)
        {
            Context = state.Context;
            SessionKey = state.SessionKey;
            _chainKey = state.ChainKey;
            _iteration = state.Iteration;
            _messageKeyCache = state.MessageKeyCache;

            _signingKey = ECDsa.Create();
            _signingKey.ImportECPrivateKey(state.SigningKeyPrivate, out _);
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

        public SenderKeySessionState GetState()
        {
            return new SenderKeySessionState
            {
                Context = Context,
                SessionKey = SessionKey,
                ChainKey = _chainKey,
                Iteration = _iteration,
                MessageKeyCache = _messageKeyCache,
                SigningKeyPrivate = _signingKey.ExportECPrivateKey()
            };
        }

        public byte[] SaveState(byte[] masterKey)
        {
            var state = GetState();
            var plaintextState = JsonSerializer.SerializeToUtf8Bytes(state);
            return CryptoUtils.EncryptAtRest(masterKey, plaintextState, Encoding.UTF8.GetBytes("SenderKeySessionState"));
        }

        public SenderKeyMessage Encrypt(byte[] plaintext)
        {
            var messageKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-MessageKey", CryptoUtils.KeySize);
            _chainKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-ChainKey", CryptoUtils.KeySize);

            var header = new SenderKeyHeader { Iteration = _iteration };
            var associatedData = header.ToAssociatedData(Context);

            var ciphertext = CryptoUtils.EncryptAesGcm(messageKey, _iteration, plaintext, associatedData);
            var signature = SignMessage(associatedData, ciphertext);

            var message = new SenderKeyMessage
            {
                Header = header,
                Ciphertext = ciphertext,
                Signature = signature
            };

            _iteration++;
            return message;
        }

        public byte[] Decrypt(SenderKeyMessage message)
        {
            var associatedData = message.Header.ToAssociatedData(Context);
            if (!VerifySignature(message, associatedData))
            {
                throw new CryptographicException("Invalid signature.");
            }

            if (message.Header.Iteration < _iteration)
            {
                if (_messageKeyCache.TryGetValue(message.Header.Iteration, out var cachedKey))
                {
                    var plaintext = CryptoUtils.DecryptAesGcm(cachedKey, message.Header.Iteration, message.Ciphertext!, associatedData);
                    _messageKeyCache.Remove(message.Header.Iteration);
                    return plaintext;
                }
                if (_messageKeyCache.ContainsKey(message.Header.Iteration))
                {
                    throw new CryptographicException("Received an old message that was already decrypted.");
                }
                throw new CryptographicException("Received an old message that was not in the cache.");
            }

            if (message.Header.Iteration > _iteration)
            {
                if (message.Header.Iteration - _iteration > MaxSkippedMessages)
                {
                    throw new CryptographicException($"Cannot process message with iteration {message.Header.Iteration} because it exceeds the maximum number of skippable messages ({MaxSkippedMessages}).");
                }

                while (_iteration < message.Header.Iteration)
                {
                    var skippedMessageKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-MessageKey", CryptoUtils.KeySize);
                    _messageKeyCache.Add(_iteration, skippedMessageKey);
                    _chainKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-ChainKey", CryptoUtils.KeySize);
                    _iteration++;
                }
            }

            // At this point, message.Header.Iteration == _iteration
            var messageKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-MessageKey", CryptoUtils.KeySize);
            _chainKey = CryptoUtils.KDF(null, _chainKey, "SenderKey-ChainKey", CryptoUtils.KeySize);
            _iteration++;

            return CryptoUtils.DecryptAesGcm(messageKey, message.Header.Iteration, message.Ciphertext!, associatedData);
        }

        private byte[] SignMessage(byte[] associatedData, byte[] ciphertext)
        {
            var dataToSign = new byte[associatedData.Length + ciphertext.Length];
            associatedData.CopyTo(dataToSign, 0);
            ciphertext.CopyTo(dataToSign, associatedData.Length);
            return _signingKey.SignData(dataToSign, HashAlgorithmName.SHA256);
        }

        private bool VerifySignature(SenderKeyMessage message, byte[] associatedData)
        {
            if (message.Ciphertext is null || message.Signature is null)
            {
                return false;
            }

            var dataToVerify = new byte[associatedData.Length + message.Ciphertext.Length];
            associatedData.CopyTo(dataToVerify, 0);
            message.Ciphertext.CopyTo(dataToVerify, associatedData.Length);
            return _signingKey.VerifyData(dataToVerify, message.Signature, HashAlgorithmName.SHA256);
        }

        public void Dispose()
        {
            _signingKey.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
