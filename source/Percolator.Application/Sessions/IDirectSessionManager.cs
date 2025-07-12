using Percolator.Cryptography;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.Sessions;

public interface IDirectSessionManager
{
    Task EstablishSessionAsInitiatorAsync(
        ConversationId conversationId,
        SessionPeerId peerId,
        SessionIdentityKey remoteIdentityKey,
        SessionRatchetKey remoteRatchetKey,
        SharedSecret sharedSecret);

    Task EstablishSessionAsResponderAsync(
        ConversationId conversationId,
        SessionPeerId peerId,
        SessionIdentityKey remoteIdentityKey,
        SharedSecret sharedSecret);

    Task<(SessionPeerId RemotePeerId, RatchetMessage EncryptedMessage)?> EncryptMessageAsync(ConversationId conversationId, byte[] plaintext);

    Task<byte[]> ReceiveMessageAsync(ConversationId conversationId, RatchetMessage encryptedMessage);
}
