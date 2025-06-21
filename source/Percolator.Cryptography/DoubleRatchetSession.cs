using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Percolator.Cryptography;

public class DoubleRatchetSession : IDisposable
{
    private const int MaxSkippedMessages = 100;

    private byte[] _rootKey;
    private byte[]? _sendingChainKey;
    private byte[]? _receivingChainKey;
    private uint _sendingCounter;
    private uint _receivingCounter;
    private ECDiffieHellman _dhRatchetKey;
    private byte[]? _remoteRatchetKeyBytes;
    private readonly Dictionary<uint, byte[]> _skippedMessageKeys = new();

    // Initiator constructor
    public DoubleRatchetSession(ECDiffieHellman identityKey, byte[] remoteIdentityKeyBytes, byte[] remoteRatchetKeyBytes)
    {
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteRatchetKeyBytes = remoteRatchetKeyBytes;

        using var remoteIdentityKey = ECDiffieHellman.Create();
        remoteIdentityKey.ImportSubjectPublicKeyInfo(remoteIdentityKeyBytes, out _);
        using var remoteRatchetKey = ECDiffieHellman.Create();
        remoteRatchetKey.ImportSubjectPublicKeyInfo(remoteRatchetKeyBytes, out _);

        var sk = identityKey.DeriveKeyFromHash(remoteIdentityKey.PublicKey, HashAlgorithmName.SHA256);
        var dhOutput = _dhRatchetKey.DeriveKeyFromHash(remoteRatchetKey.PublicKey, HashAlgorithmName.SHA256);

        var kdfResult = CryptoUtils.KDF(sk, dhOutput, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = kdfResult[..CryptoUtils.KeySize];
        _sendingChainKey = kdfResult[CryptoUtils.KeySize..];
    }

    // Responder constructor
    public DoubleRatchetSession(ECDiffieHellman identityKey, ECDiffieHellman ratchetKey, byte[] remoteIdentityKeyBytes)
    {
        _dhRatchetKey = ratchetKey;
        using var remoteIdentityKey = ECDiffieHellman.Create();
        remoteIdentityKey.ImportSubjectPublicKeyInfo(remoteIdentityKeyBytes, out _);
        _rootKey = identityKey.DeriveKeyFromHash(remoteIdentityKey.PublicKey, HashAlgorithmName.SHA256);
    }

    private DoubleRatchetSession(SessionState state)
    {
        _rootKey = state.RootKey;
        _sendingChainKey = state.SendingChainKey;
        _receivingChainKey = state.ReceivingChainKey;
        _sendingCounter = state.SendingCounter;
        _receivingCounter = state.ReceivingCounter;
        _dhRatchetKey = ECDiffieHellman.Create();
        _dhRatchetKey.ImportECPrivateKey(state.DhRatchetPrivateKey, out _);
        _remoteRatchetKeyBytes = state.RemoteRatchetKey;
        _skippedMessageKeys = state.SkippedMessageKeys;
    }

    public byte[] SaveState(byte[] masterKey)
    {
        var state = new SessionState
        {
            RootKey = _rootKey,
            SendingChainKey = _sendingChainKey,
            ReceivingChainKey = _receivingChainKey,
            SendingCounter = _sendingCounter,
            ReceivingCounter = _receivingCounter,
            DhRatchetPrivateKey = _dhRatchetKey.ExportECPrivateKey(),
            RemoteRatchetKey = _remoteRatchetKeyBytes,
            SkippedMessageKeys = _skippedMessageKeys
        };
        var serializedState = JsonSerializer.SerializeToUtf8Bytes(state);
        return CryptoUtils.EncryptAtRest(masterKey, serializedState, null);
    }

    public static DoubleRatchetSession LoadState(byte[] masterKey, byte[] encryptedState)
    {
        var decryptedState = CryptoUtils.DecryptAtRest(masterKey, encryptedState, null);
        var state = JsonSerializer.Deserialize<SessionState>(decryptedState)!;
        return new DoubleRatchetSession(state);
    }

    public RatchetMessage Encrypt(byte[] plaintext)
    {
        if (_sendingChainKey is null)
        {
            throw new InvalidOperationException("Session not initialized for sending.");
        }

        var messageKey = CryptoUtils.KDF(null, _sendingChainKey, "message-key-kdf", CryptoUtils.KeySize);
        _sendingChainKey = CryptoUtils.KDF(null, _sendingChainKey, "chain-key-kdf", CryptoUtils.KeySize);

        var ciphertext = CryptoUtils.EncryptAesGcm(messageKey, _sendingCounter, plaintext, null);
        var message = new RatchetMessage
        {
            RatchetPublicKey = _dhRatchetKey.PublicKey.ExportSubjectPublicKeyInfo(),
            Nonce = _sendingCounter,
            Ciphertext = ciphertext
        };

        _sendingCounter++;
        return message;
    }

    public byte[] Decrypt(RatchetMessage message)
    {
        var plaintext = TrySkippedMessageKeys(message);
        if (plaintext is not null)
        {            return plaintext;
        }

        if (_remoteRatchetKeyBytes is null || !message.RatchetPublicKey.SequenceEqual(_remoteRatchetKeyBytes))
        {
            DoDhRatchet(message.RatchetPublicKey);
        }

        SkipMessageKeys(message.Nonce);

        if (_receivingChainKey is null)
        {
            throw new InvalidOperationException("Session not initialized for receiving.");
        }

        var messageKey = CryptoUtils.KDF(null, _receivingChainKey, "message-key-kdf", CryptoUtils.KeySize);
        _receivingChainKey = CryptoUtils.KDF(null, _receivingChainKey, "chain-key-kdf", CryptoUtils.KeySize);

        plaintext = CryptoUtils.DecryptAesGcm(messageKey, message.Nonce, message.Ciphertext, null);
        _receivingCounter++;
        return plaintext;
    }

    private byte[]? TrySkippedMessageKeys(RatchetMessage message)
    {
        if (_skippedMessageKeys.TryGetValue(message.Nonce, out var key))
        {
            var plaintext = CryptoUtils.DecryptAesGcm(key, message.Nonce, message.Ciphertext, null);
            _skippedMessageKeys.Remove(message.Nonce);
            return plaintext;
        }
        return null;
    }

    private void SkipMessageKeys(uint until)
    {
        if (_receivingChainKey is null) return;

        while (_receivingCounter < until)
        {
            var skippedMessageKey = CryptoUtils.KDF(null, _receivingChainKey, "message-key-kdf", CryptoUtils.KeySize);
            _receivingChainKey = CryptoUtils.KDF(null, _receivingChainKey, "chain-key-kdf", CryptoUtils.KeySize);

            if (_skippedMessageKeys.Count < MaxSkippedMessages)
            {
                _skippedMessageKeys.Add(_receivingCounter, skippedMessageKey);
            }
            _receivingCounter++;
        }
    }

    private void DoDhRatchet(byte[] remoteRatchetKeyBytes)
    {
        _remoteRatchetKeyBytes = remoteRatchetKeyBytes;
        using var remoteRatchetKey = ECDiffieHellman.Create();
        remoteRatchetKey.ImportSubjectPublicKeyInfo(remoteRatchetKeyBytes, out _);

        var dhResult = _dhRatchetKey.DeriveKeyFromHash(remoteRatchetKey.PublicKey, HashAlgorithmName.SHA256);
        var kdfResult = CryptoUtils.KDF(_rootKey, dhResult, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = kdfResult[..CryptoUtils.KeySize];
        _receivingChainKey = kdfResult[CryptoUtils.KeySize..];

        _dhRatchetKey.Dispose();
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        dhResult = _dhRatchetKey.DeriveKeyFromHash(remoteRatchetKey.PublicKey, HashAlgorithmName.SHA256);
        kdfResult = CryptoUtils.KDF(_rootKey, dhResult, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = kdfResult[..CryptoUtils.KeySize];
        _sendingChainKey = kdfResult[CryptoUtils.KeySize..];

        _receivingCounter = 0;
        _sendingCounter = 0;
    }

    public void Dispose()
    {
        _dhRatchetKey?.Dispose();
        GC.SuppressFinalize(this);
    }

    private class SessionState
    {
        [JsonInclude]
        public byte[] RootKey { get; set; } = null!;
        [JsonInclude]
        public byte[]? SendingChainKey { get; set; }
        [JsonInclude]
        public byte[]? ReceivingChainKey { get; set; }
        [JsonInclude]
        public uint SendingCounter { get; set; }
        [JsonInclude]
        public uint ReceivingCounter { get; set; }
        [JsonInclude]
        public byte[] DhRatchetPrivateKey { get; set; } = null!;
        [JsonInclude]
        public byte[]? RemoteRatchetKey { get; set; }
        [JsonInclude]
        public Dictionary<uint, byte[]> SkippedMessageKeys { get; set; } = new();
    }
}