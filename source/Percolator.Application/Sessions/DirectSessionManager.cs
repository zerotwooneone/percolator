using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.Sessions
{
    public class DirectSessionManager
    {
        private readonly IDoubleRatchetSessionStore _doubleRatchetSessionStore;
        private readonly IConversationStore _conversationStore;
        private readonly IMessageStore _messageRepository;
        private readonly ActiveIdentityContext _activeIdentityContext;

        public DirectSessionManager(
            IDoubleRatchetSessionStore doubleRatchetSessionStore,
            IConversationStore conversationStore,
            IMessageStore messageRepository,
            ActiveIdentityContext activeIdentityContext)
        {
            _doubleRatchetSessionStore = doubleRatchetSessionStore;
            _conversationStore = conversationStore;
            _messageRepository = messageRepository;
            _activeIdentityContext = activeIdentityContext;
        }

        public async Task<ConversationId> EstablishSessionAsync(SessionPeerId remotePeerId, byte[] sharedSecret, byte[] initialRatchetPublicKey)
        {
            var activeIdentity = _activeIdentityContext;
            if (activeIdentity?.Certificate is null)
            {
                throw new InvalidOperationException("No active identity found to establish session.");
            }

            // For simplicity, assuming the one calling EstablishSessionAsync is the initiator
            // In a real scenario, this might be passed as an argument or derived from context
            // The identityKey for DoubleRatchetSession should be the ECDiffieHellman key from the active identity's certificate.
            using var identityKey = activeIdentity.Certificate.GetECDHKeyPair();

            // Create a new DoubleRatchetSession as Initiator
            using var doubleRatchetSession = DoubleRatchetSession.AsInitiator(
                sharedSecret,
                identityKey,
                remotePeerId.Value.ToByteArray(), // Remote identity public key (as byte[])
                initialRatchetPublicKey
            );

            // Create a new DirectConversation
            var conversationId = new ConversationId(Guid.NewGuid());
            var localPeerId = new SessionPeerId(Guid.Parse(activeIdentity.IdentityName!));
            var conversation = new DirectConversation(conversationId, localPeerId, remotePeerId, ConversationState.Active);

            // Save session state and conversation
            await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());
            await _conversationStore.SaveConversationAsync(conversation);

            return conversationId;
        }

        public async Task<RatchetMessage> SendMessageAsync(ConversationId conversationId, string plaintext)
        {
            var activeIdentity = _activeIdentityContext;
            if (activeIdentity?.Certificate is null)
            {
                throw new InvalidOperationException("No active identity found to send message.");
            }

            var conversation = await _conversationStore.GetConversationAsync(conversationId);
            if (conversation == null)
            {
                throw new InvalidOperationException($"Conversation with ID {conversationId} not found.");
            }

            var localPeerId = new SessionPeerId(Guid.Parse(activeIdentity.IdentityName!));
            var remotePeerId = conversation.GetRemotePeer(localPeerId);

            // Retrieve session state
            var sessionState = await _doubleRatchetSessionStore.GetSessionStateAsync(remotePeerId, conversationId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }
            
            using var identityKey = activeIdentity.Certificate.GetECDHKeyPair();


            // Reconstruct DoubleRatchetSession from state
            using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

            // Encrypt message
            var encryptedContent = doubleRatchetSession.Encrypt(System.Text.Encoding.UTF8.GetBytes(plaintext));

            // Save updated session state
            await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

            // Create and save DirectMessage
            var message = new DirectMessage(new MessageId(Guid.NewGuid()), conversationId, localPeerId, new OpaqueContent(encryptedContent.Ciphertext), DateTimeOffset.UtcNow);
            await _messageRepository.AddMessageAsync(message);

            return encryptedContent;
        }

        public async Task<byte[]> ReceiveMessageAsync(ConversationId conversationId, RatchetMessage encryptedMessage)
        {
            var activeIdentity = _activeIdentityContext;
            if (activeIdentity?.Certificate is null)
            {
                throw new InvalidOperationException("No active identity found to send message.");
            }
            
            var conversation = await _conversationStore.GetConversationAsync(conversationId);
            if (conversation == null)
            {
                throw new InvalidOperationException($"Conversation with ID {conversationId} not found.");
            }

            var localPeerId = new SessionPeerId(Guid.Parse(activeIdentity.IdentityName!));
            var remotePeerId = conversation.GetRemotePeer(localPeerId);

            // Retrieve session state
            var sessionState = await _doubleRatchetSessionStore.GetSessionStateAsync(remotePeerId, conversationId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }
            
            using var identityKey = activeIdentity.Certificate.GetECDHKeyPair();

            // Reconstruct DoubleRatchetSession from state
            using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

            // Decrypt message
            var decryptedBytes = doubleRatchetSession.Decrypt(encryptedMessage);

            // Save updated session state
            await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

            // Create and save DirectMessage (assuming we want to store received messages too)
            // Note: The sender PeerId for received messages would need to be derived or passed.
            // For now, we'll use the remotePeerId from the conversation as the sender.
            var message = new DirectMessage(new MessageId(Guid.NewGuid()), conversationId, remotePeerId, new OpaqueContent(encryptedMessage.Ciphertext), DateTimeOffset.UtcNow);
            await _messageRepository.AddMessageAsync(message);

            return decryptedBytes;
        }

        public async Task<DirectConversation?> GetConversationAsync(ConversationId conversationId)
        {
            return await _conversationStore.GetConversationAsync(conversationId);
        }
    }
}