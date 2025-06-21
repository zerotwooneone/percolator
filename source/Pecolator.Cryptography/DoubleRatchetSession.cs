using System;
using System.Security.Cryptography;

namespace Pecolator.Cryptography
{
    public class DoubleRatchetSession
    {
        private const int KeySize = 32;
        private const int HeaderSize = 4; // For message counter (uint)

        private byte[] _sendingChainKey;
        private byte[] _receivingChainKey;
        private uint _sendingCounter = 0;
        private uint _receivingCounter = 0;

        public DoubleRatchetSession(byte[] sharedSecret, SessionRole role)
        {
            // Use HKDF to derive initial, separate chain keys from the shared secret.
            var prk = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, new byte[0]);
            var infoSend = (role == SessionRole.Initiator) ? "send" : "recv";
            var infoRecv = (role == SessionRole.Initiator) ? "recv" : "send";

            _sendingChainKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, System.Text.Encoding.UTF8.GetBytes(infoSend));
            _receivingChainKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, KeySize, System.Text.Encoding.UTF8.GetBytes(infoRecv));
        }

        public byte[] Encrypt(byte[] plaintext)
        {
            _sendingCounter++;
            var (newSendingKey, messageKey) = RatchetStep(_sendingChainKey);
            _sendingChainKey = newSendingKey;

            var encryptedData = Xor(plaintext, messageKey);

            var header = BitConverter.GetBytes(_sendingCounter);
            var ciphertext = new byte[HeaderSize + encryptedData.Length];
            Buffer.BlockCopy(header, 0, ciphertext, 0, HeaderSize);
            Buffer.BlockCopy(encryptedData, 0, ciphertext, HeaderSize, encryptedData.Length);

            return ciphertext;
        }

        public byte[] Decrypt(byte[] ciphertext)
        {
            if (ciphertext.Length < HeaderSize)
            {
                throw new InvalidMessageOrderException("Invalid message format.");
            }

            var header = new byte[HeaderSize];
            Buffer.BlockCopy(ciphertext, 0, header, 0, HeaderSize);
            var messageCounter = BitConverter.ToUInt32(header, 0);

            if (messageCounter <= _receivingCounter)
            {
                throw new InvalidMessageOrderException($"Received out-of-order or duplicate message. Last: {_receivingCounter}, this: {messageCounter}");
            }

            // Catch up to the received message counter
            var tempReceivingKey = _receivingChainKey;
            byte[] messageKey = Array.Empty<byte>();
            for (uint i = _receivingCounter + 1; i <= messageCounter; i++)
            {
                (tempReceivingKey, messageKey) = RatchetStep(tempReceivingKey);
            }

            _receivingChainKey = tempReceivingKey;
            _receivingCounter = messageCounter;

            var encryptedData = new byte[ciphertext.Length - HeaderSize];
            Buffer.BlockCopy(ciphertext, HeaderSize, encryptedData, 0, encryptedData.Length);

            return Xor(encryptedData, messageKey);
        }

        private (byte[] newChainKey, byte[] messageKey) RatchetStep(byte[] currentChainKey)
        {
            using var hmac = new HMACSHA256(currentChainKey);
            var newChainKey = hmac.ComputeHash(new byte[] { 0x01 });
            var messageKey = hmac.ComputeHash(new byte[] { 0x02 });
            return (newChainKey, messageKey);
        }

        private static byte[] Xor(byte[] data, byte[] key)
        {
            var result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            }
            return result;
        }
    }
}
