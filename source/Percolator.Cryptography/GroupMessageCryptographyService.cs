using Percolator.Cryptography.Primitives;
using Percolator.Contracts;

namespace Percolator.Cryptography;

/// <summary>
/// Implements group message encryption/decryption using Signal SenderKey protocol.
/// Delegates to the infrastructure SenderKeyCryptographyService which handles the native FFI integration.
/// </summary>
public sealed class GroupMessageCryptographyService : IGroupMessageCryptographyService
{
    private readonly ISenderKeyCryptographyService _senderKeyCryptoService;

    public GroupMessageCryptographyService(ISenderKeyCryptographyService senderKeyCryptoService)
    {
        _senderKeyCryptoService = senderKeyCryptoService;
    }

    public Ciphertext EncryptGroupContent(
        Primitives.ConversationId conversationId,
        CryptoPublicIdentity publicIdentityId,
        DeviceId deviceId,
        GroupContent content)
    {
        // Serialize GroupContent protobuf to bytes
        var contentBytes = Google.Protobuf.MessageExtensions.ToByteArray(content);

        // Use SenderKey protocol for encryption
        var ciphertextBytes = _senderKeyCryptoService.EncryptGroupMessage(
            conversationId,
            publicIdentityId,
            deviceId,
            contentBytes);

        return Ciphertext.FromBytesOwned(ciphertextBytes);
    }

    public GroupContent DecryptGroupContent(
        Primitives.ConversationId conversationId,
        CryptoPublicIdentity senderPublicIdentityId,
        DeviceId senderDeviceId,
        Ciphertext ciphertext)
    {
        // Use SenderKey protocol for decryption
        var plaintextBytes = _senderKeyCryptoService.DecryptGroupMessage(
            conversationId,
            senderPublicIdentityId,
            senderDeviceId,
            ciphertext.Span.ToArray());

        // Deserialize bytes back to GroupContent protobuf
        return GroupContent.Parser.ParseFrom(plaintextBytes);
    }
}
