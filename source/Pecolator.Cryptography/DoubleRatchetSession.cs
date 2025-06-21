using System;
using System.Security.Cryptography;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Percolator.CryptographyTests")]

namespace Pecolator.Cryptography
{
    public class DoubleRatchetSession
    {
        private const int KeySize = 32;
        private const int HeaderSize = 4; // For message counter (uint)

        private ECDiffieHellman _dhKeyPair;
        private ECDiffieHellman? _dhKeyPairForSending; // A queued key pair for the next DH ratchet step.
        private byte[] _remotePublicKey;
        private byte[] _rootKey;

        private byte[] _sendingChainKey;
        private byte[] _receivingChainKey;
        private uint _sendingCounter = 0;
        private uint _receivingCounter = 0;

        internal byte[] SendingChainKey => _sendingChainKey;
        internal byte[] ReceivingChainKey => _receivingChainKey;

        public byte[] PublicKey => _dhKeyPair.PublicKey.ExportSubjectPublicKeyInfo();

        public DoubleRatchetSession(byte[] sharedSecret, ECDiffieHellman initialKeyPair, byte[] remotePublicKey, SessionRole role)
        {
            _dhKeyPair = initialKeyPair;
            _remotePublicKey = remotePublicKey;
            _dhKeyPairForSending = null;

            // Initial keys are derived symmetrically from the shared secret.
            var prk = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, salt: new byte[0]);
            _rootKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, System.Text.Encoding.UTF8.GetBytes("dr-root"));
            var chainKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, System.Text.Encoding.UTF8.GetBytes("dr-chain"));
            _sendingChainKey = chainKey;
            _receivingChainKey = chainKey.ToArray(); // Ensure it's a copy

            if (role == SessionRole.Initiator)
            {
                // The initiator immediately queues up a new key to start the ratchet.
                _dhKeyPairForSending = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            }
        }

        public RatchetMessage Encrypt(byte[] plaintext)
        {
            // --- Diffie-Hellman Ratchet Step (if it's our turn) ---
            if (_dhKeyPairForSending != null)
            {
                (_rootKey, _sendingChainKey) = DHRatchetStep(_remotePublicKey, _dhKeyPairForSending);
                _dhKeyPair.Dispose();
                _dhKeyPair = _dhKeyPairForSending;
                _dhKeyPairForSending = null;
                _sendingCounter = 0;
            }

            // --- Symmetric Ratchet Step ---
            _sendingCounter++;
            var (newSendingKey, messageKey) = RatchetStep(_sendingChainKey);
            _sendingChainKey = newSendingKey;

            var encryptedData = Xor(plaintext, messageKey);

            var header = BitConverter.GetBytes(_sendingCounter);
            var ciphertextPayload = new byte[HeaderSize + encryptedData.Length];
            Buffer.BlockCopy(header, 0, ciphertextPayload, 0, HeaderSize);
            Buffer.BlockCopy(encryptedData, 0, ciphertextPayload, HeaderSize, encryptedData.Length);

            return new RatchetMessage(PublicKey, ciphertextPayload);
        }

        public byte[] Decrypt(RatchetMessage message)
        {
            // --- Diffie-Hellman Ratchet Step ---
            if (!message.EphemeralPublicKey.SequenceEqual(_remotePublicKey))
            {
                (_rootKey, _receivingChainKey) = DHRatchetStep(message.EphemeralPublicKey, _dhKeyPair);
                _remotePublicKey = message.EphemeralPublicKey;
                _receivingCounter = 0; // The counter for this chain resets.

                // After a DH ratchet, we queue up a new key pair for our next sent message.
                _dhKeyPairForSending = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            }

            // --- Symmetric Ratchet Step ---
            if (message.CiphertextPayload.Length < HeaderSize)
            {
                throw new InvalidMessageOrderException("Invalid message format.");
            }

            var header = new byte[HeaderSize];
            Buffer.BlockCopy(message.CiphertextPayload, 0, header, 0, HeaderSize);
            var messageCounter = BitConverter.ToUInt32(header, 0);

            if (messageCounter <= _receivingCounter)
            {
                throw new InvalidMessageOrderException($"Received out-of-order or duplicate message. Last: {_receivingCounter}, this: {messageCounter}");
            }

            var tempReceivingKey = _receivingChainKey;
            byte[] messageKey = Array.Empty<byte>();
            for (uint i = _receivingCounter + 1; i <= messageCounter; i++)
            {
                (tempReceivingKey, messageKey) = RatchetStep(tempReceivingKey);
            }

            _receivingChainKey = tempReceivingKey;
            _receivingCounter = messageCounter;

            var encryptedData = new byte[message.CiphertextPayload.Length - HeaderSize];
            Buffer.BlockCopy(message.CiphertextPayload, HeaderSize, encryptedData, 0, encryptedData.Length);

            return Xor(encryptedData, messageKey);
        }

        private (byte[] newRootKey, byte[] newChainKey) DHRatchetStep(byte[] peerKeyBytes, ECDiffieHellman ourKeyPair)
        {
            using var peerKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            peerKey.ImportSubjectPublicKeyInfo(peerKeyBytes, out _);
            var dhSharedSecret = ourKeyPair.DeriveKeyMaterial(peerKey.PublicKey);

            var dhPrk = HKDF.Extract(HashAlgorithmName.SHA256, dhSharedSecret, _rootKey);
            var newRootKey = HKDF.Expand(HashAlgorithmName.SHA256, dhPrk, KeySize, System.Text.Encoding.UTF8.GetBytes("dr-root"));
            var newChainKey = HKDF.Expand(HashAlgorithmName.SHA256, dhPrk, KeySize, System.Text.Encoding.UTF8.GetBytes("dr-chain"));
            return (newRootKey, newChainKey);
        }

        private (byte[] newChainKey, byte[] messageKey) RatchetStep(byte[] currentChainKey)
        {
            using var hmac = new HMACSHA256(currentChainKey);
            var newChainKey = hmac.ComputeHash(new byte[] { 0x01 });
            var messageKey = hmac.ComputeHash(new byte[] { 0x02 });
            return (newChainKey, messageKey);
        }

        private static byte[] Xor(byte[] buffer1, byte[] buffer2)
        {
            var result = new byte[buffer1.Length];
            for (var i = 0; i < buffer1.Length; i++)
            {
                result[i] = (byte)(buffer1[i] ^ buffer2[i % buffer2.Length]);
            }
            return result;
        }
    }
}
