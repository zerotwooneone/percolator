using Percolator.Cryptography.Primitives;
using Percolator.Contracts;

namespace Percolator.Cryptography
{
    /// <summary>
    /// Service interface for encrypting and decrypting group message content using Signal SenderKey protocol.
    /// </summary>
    public interface IGroupMessageCryptographyService
    {
        /// <summary>
        /// Encrypts group content using Signal SenderKey protocol.
        /// </summary>
        /// <param name="conversationId">The group conversation identifier.</param>
        /// <param name="publicIdentityId">The sender's public identity.</param>
        /// <param name="deviceId">The sender's device ID.</param>
        /// <param name="content">The plaintext GroupContent to encrypt.</param>
        /// <returns>A Ciphertext containing the encrypted GroupContent bytes.</returns>
        Ciphertext EncryptGroupContent(
            Primitives.ConversationId conversationId,
            CryptoPublicIdentity publicIdentityId,
            DeviceId deviceId,
            GroupContent content);

        /// <summary>
        /// Decrypts group content using Signal SenderKey protocol.
        /// </summary>
        /// <param name="conversationId">The group conversation identifier.</param>
        /// <param name="senderPublicIdentityId">The sender's public identity.</param>
        /// <param name="senderDeviceId">The sender's device ID.</param>
        /// <param name="ciphertext">The Ciphertext bytes to decrypt.</param>
        /// <returns>The decrypted GroupContent.</returns>
        GroupContent DecryptGroupContent(
            Primitives.ConversationId conversationId,
            CryptoPublicIdentity senderPublicIdentityId,
            DeviceId senderDeviceId,
            Ciphertext ciphertext);
    }
}
