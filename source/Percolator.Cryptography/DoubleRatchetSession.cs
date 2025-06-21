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
    private ECDiffieHellman? _dhRatchetKey;
    private byte[]? _remoteRatchetKeyBytes;
    private Dictionary<uint, byte[]> _skippedMessageKeys = new();
    private byte[] _remoteIdentityPublicKey;

    // Combined private constructor to resolve ambiguity
    private DoubleRatchetSession(ECDiffieHellman identityKey, ECDiffieHellman key1, ECDiffieHellman key2, bool isInitiator)
    {
        if (isInitiator)
        {
            // For initiator: key1 is remoteIdentityKey, key2 is remoteRatchetKey
            _remoteIdentityPublicKey = key1.PublicKey.ExportSubjectPublicKeyInfo();
            _remoteRatchetKeyBytes = key2.PublicKey.ExportSubjectPublicKeyInfo();
            var sharedSecret = identityKey.DeriveKeyMaterial(key1.PublicKey);
            _rootKey = SHA256.HashData(sharedSecret);
        }
        else
        {
            // For responder: key1 is remoteIdentityKey, key2 is ourRatchetKey
            _dhRatchetKey = key2;
            _remoteIdentityPublicKey = key1.PublicKey.ExportSubjectPublicKeyInfo();
            var sharedSecret = identityKey.DeriveKeyMaterial(key1.PublicKey);
            _rootKey = SHA256.HashData(sharedSecret);
        }
    }

    public DoubleRatchetSession(DoubleRatchetSessionState state)
    {
        _rootKey = state.RootKey;
        _sendingChainKey = state.SendingChainKey;
        _receivingChainKey = state.ReceivingChainKey;
        _sendingCounter = state.SendingCounter;
        _receivingCounter = state.ReceivingCounter;
        _remoteRatchetKeyBytes = state.TheirDhRatchetPublicKey;
        if (state.DhRatchetPrivateKey is not null)
        {
            _dhRatchetKey = ECDiffieHellman.Create();
            _dhRatchetKey.ImportECPrivateKey(state.DhRatchetPrivateKey, out _);
        }
        _skippedMessageKeys = state.SkippedMessageKeys;
        _remoteIdentityPublicKey = state.TheirIdentityPublicKey ?? Array.Empty<byte>();
    }

    public static DoubleRatchetSession CreateInitiatorSession(ECDiffieHellman identityKey, ECDiffieHellman remoteIdentityKey, ECDiffieHellman remoteRatchetKey)
    {
        return new DoubleRatchetSession(identityKey, remoteIdentityKey, remoteRatchetKey, true);
    }

    public static DoubleRatchetSession CreateResponderSession(ECDiffieHellman identityKey, ECDiffieHellman ratchetKey, ECDiffieHellman remoteIdentityKey)
    {
        return new DoubleRatchetSession(identityKey, remoteIdentityKey, ratchetKey, false);
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
            SkippedMessageKeys = _skippedMessageKeys,
            TheirIdentityPublicKey = _remoteIdentityPublicKey,
            TheirDhRatchetPublicKey = _remoteRatchetKeyBytes,
            DhRatchetPrivateKey = _dhRatchetKey?.ExportECPrivateKey()
        };
    }

    public RatchetMessage Encrypt(byte[] plaintext)
    {
        if (_sendingChainKey is null)
        {
            // First message, perform initial ratchet
            _dhRatchetKey?.Dispose();
            _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var remoteRatchetKey = ECDiffieHellman.Create();
            remoteRatchetKey.ImportSubjectPublicKeyInfo(_remoteRatchetKeyBytes, out _);
            var dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteRatchetKey.PublicKey);
            var dhResult = SHA256.HashData(dhSecret);
            var kdfResult = CryptoUtils.KDF(_rootKey, dhResult, "ratchet-kdf", CryptoUtils.KeySize * 2);
            _rootKey = kdfResult[..CryptoUtils.KeySize];
            _sendingChainKey = kdfResult[CryptoUtils.KeySize..];
        }

        var messageKey = CryptoUtils.KDF(null, _sendingChainKey, "message-key-kdf", CryptoUtils.KeySize);
        _sendingChainKey = CryptoUtils.KDF(null, _sendingChainKey, "ratchet-chain-kdf", CryptoUtils.KeySize);

        var header = new RatchetHeader
        {
            RatchetKey = _dhRatchetKey!.PublicKey.ExportSubjectPublicKeyInfo(),
            Counter = _sendingCounter
        };

        var associatedData = header.ToAssociatedData();
        var ciphertext = CryptoUtils.EncryptAesGcm(messageKey, _sendingCounter, plaintext, associatedData);

        _sendingCounter++;
        return new RatchetMessage
        {
            Header = header,
            Ciphertext = ciphertext
        };
    }

    public byte[] Decrypt(RatchetMessage message)
    {
        var plaintext = TrySkippedMessageKeys(message);
        if (plaintext is not null)
        {
            return plaintext;
        }

        if (_remoteRatchetKeyBytes is null || !message.Header.RatchetKey.SequenceEqual(_remoteRatchetKeyBytes))
        {
            DoDhRatchet(message.Header.RatchetKey);
        }

        SkipMessageKeys(message.Header.Counter);

        if (_receivingChainKey is null)
        {
            // This is the first message, so we need to initialize the receiving chain
            using var remoteRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            remoteRatchetKey.ImportSubjectPublicKeyInfo(message.Header.RatchetKey, out _);
            var dhSecret = _dhRatchetKey!.DeriveKeyMaterial(remoteRatchetKey.PublicKey);
            var dhResult = SHA256.HashData(dhSecret);
            var kdfResult = CryptoUtils.KDF(_rootKey, dhResult, "ratchet-kdf", CryptoUtils.KeySize * 2);
            _rootKey = kdfResult[..CryptoUtils.KeySize];
            _receivingChainKey = kdfResult[CryptoUtils.KeySize..];
        }

        // TODO: Handle skipped messages
        if (message.Header.Counter < _receivingCounter)
        {
            throw new InvalidOperationException("Received message is out of order.");
        }

        var messageKey = CryptoUtils.KDF(null, _receivingChainKey, "message-key-kdf", CryptoUtils.KeySize);
        _receivingChainKey = CryptoUtils.KDF(null, _receivingChainKey, "ratchet-chain-kdf", CryptoUtils.KeySize);

        var associatedData = message.Header.ToAssociatedData();
        plaintext = CryptoUtils.DecryptAesGcm(messageKey, message.Header.Counter, message.Ciphertext, associatedData);
        _receivingCounter++;
        return plaintext;
    }

    private byte[]? TrySkippedMessageKeys(RatchetMessage message)
    {
        if (_skippedMessageKeys.TryGetValue(message.Header.Counter, out var key))
        {
            var associatedData = message.Header.ToAssociatedData();
            var plaintext = CryptoUtils.DecryptAesGcm(key, message.Header.Counter, message.Ciphertext, associatedData);
            _skippedMessageKeys.Remove(message.Header.Counter);
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
            _receivingChainKey = CryptoUtils.KDF(null, _receivingChainKey, "ratchet-chain-kdf", CryptoUtils.KeySize);

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

        var dhSecret = _dhRatchetKey!.DeriveKeyMaterial(remoteRatchetKey.PublicKey);
        var dhResult = SHA256.HashData(dhSecret);
        var kdfResult = CryptoUtils.KDF(_rootKey, dhResult, "ratchet-kdf", CryptoUtils.KeySize * 2);
        _rootKey = kdfResult[..CryptoUtils.KeySize];
        _receivingChainKey = kdfResult[CryptoUtils.KeySize..];

        _dhRatchetKey.Dispose();
        _dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        dhSecret = _dhRatchetKey.DeriveKeyMaterial(remoteRatchetKey.PublicKey);
        dhResult = SHA256.HashData(dhSecret);
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

    public class DoubleRatchetSessionState
    {
        public byte[] RootKey { get; set; } = Array.Empty<byte>();
        public byte[]? SendingChainKey { get; set; }
        public byte[]? ReceivingChainKey { get; set; }
        public uint SendingCounter { get; set; }
        public uint ReceivingCounter { get; set; }
        public Dictionary<uint, byte[]> SkippedMessageKeys { get; set; } = new();
        public byte[]? TheirIdentityPublicKey { get; set; }
        public byte[]? TheirDhRatchetPublicKey { get; set; }
        public byte[]? DhRatchetPrivateKey { get; set; }
    }
}