using Google.Protobuf;
using Percolator.Contracts;

namespace Percolator.Cryptography;

/// <summary>
/// Implements group message encryption/decryption using AEAD with the provided BlobKey.
/// </summary>
public sealed class GroupMessageCryptographyService : IGroupMessageCryptographyService
{
    public Ciphertext EncryptGroupContent(BlobKey blobKey, GroupContent content)
    {
        throw new NotImplementedException();
    }

    public GroupContent DecryptGroupContent(BlobKey blobKey, Ciphertext ciphertext)
    {
        throw new NotImplementedException();
    }
}
