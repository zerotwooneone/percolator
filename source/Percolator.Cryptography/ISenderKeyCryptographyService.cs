using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public interface ISenderKeyCryptographyService
{
    SenderKeyDistributionMessageBytes CreateSenderKeyDistributionMessage(
        ConversationId conversationId,
        CryptoPublicIdentity publicIdentityId,
        DeviceId deviceId);

    void ProcessSenderKeyDistributionMessage(
        ConversationId conversationId,
        CryptoPublicIdentity senderPublicIdentityId,
        DeviceId senderDeviceId,
        SenderKeyDistributionMessageBytes distributionMessage);

    byte[] EncryptGroupMessage(
        ConversationId conversationId,
        CryptoPublicIdentity publicIdentityId,
        DeviceId deviceId,
        ReadOnlySpan<byte> plaintext);

    byte[] DecryptGroupMessage(
        ConversationId conversationId,
        CryptoPublicIdentity senderPublicIdentityId,
        DeviceId senderDeviceId,
        ReadOnlySpan<byte> ciphertext);
}
