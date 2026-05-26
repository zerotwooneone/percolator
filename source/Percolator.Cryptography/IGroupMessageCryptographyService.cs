using Percolator.Contracts;

namespace Percolator.Cryptography
{
    /// <summary>
    /// Service interface for encrypting and decrypting group message content.
    /// </summary>
    public interface IGroupMessageCryptographyService
    {
        /// <summary>
        /// Encrypts group content using the provided BlobKey.
        /// </summary>
        /// <param name="blobKey">The symmetric key for encryption (derived from GroupMasterKey).</param>
        /// <param name="content">The plaintext GroupContent to encrypt.</param>
        /// <returns>A Ciphertext containing the encrypted GroupContent bytes.</returns>
        Ciphertext EncryptGroupContent(BlobKey blobKey, GroupContent content);

        /// <summary>
        /// Decrypts group content using the provided BlobKey.
        /// </summary>
        /// <param name="blobKey">The symmetric key for decryption (derived from GroupMasterKey).</param>
        /// <param name="ciphertext">The Ciphertext bytes to decrypt.</param>
        /// <returns>The decrypted GroupContent.</returns>
        GroupContent DecryptGroupContent(BlobKey blobKey, Ciphertext ciphertext);
    }
}
