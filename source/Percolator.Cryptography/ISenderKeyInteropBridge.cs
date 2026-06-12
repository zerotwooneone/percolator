using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public interface ISenderKeyInteropBridge
{
    bool TryLoadSenderKey(ConversationId conversationId, PeerId senderId, DeviceId deviceId, out byte[] recordBytes);
    void StoreSenderKey(ConversationId conversationId, PeerId senderId, DeviceId deviceId, byte[] recordBytes);
}
