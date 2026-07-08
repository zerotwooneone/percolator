using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public interface ISenderKeyInteropBridge
{
    bool TryLoadSenderKey(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId deviceId, out byte[] recordBytes);
    void StoreSenderKey(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId deviceId, byte[] recordBytes);
}
