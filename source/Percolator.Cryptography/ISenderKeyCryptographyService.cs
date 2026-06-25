using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public interface ISenderKeyCryptographyService
{
    SenderKeyDistributionMessageBytes CreateSenderKeyDistributionMessage(
        ConversationId conversationId,
        PeerId localPeerId,
        DeviceId deviceId);

    void ProcessSenderKeyDistributionMessage(
        ConversationId conversationId,
        PeerId senderPeerId,
        DeviceId senderDeviceId,
        SenderKeyDistributionMessageBytes distributionMessage);

    byte[] EncryptGroupMessage(
        ConversationId conversationId,
        PeerId localPeerId,
        DeviceId deviceId,
        ReadOnlySpan<byte> plaintext);

    byte[] DecryptGroupMessage(
        ConversationId conversationId,
        PeerId senderPeerId,
        DeviceId senderDeviceId,
        ReadOnlySpan<byte> ciphertext);
}
