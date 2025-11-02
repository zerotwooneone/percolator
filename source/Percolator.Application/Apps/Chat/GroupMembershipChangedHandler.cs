using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Apps.Chat;
using Percolator.Chat.App;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Application.Identity;
using Google.Protobuf;
using Percolator.Contracts;
using System.Security.Cryptography;
using Percolator.Cryptography;
using Percolator.Application.Network;
using Percolator.Identity;
using ChatMembershipChanged = Percolator.Chat.App.GroupMembershipChangedNotification;

namespace Percolator.Application.Apps.Chat
{
    // Reacts to membership changes by preparing distribution of a new group key version (acting-admin path)
    internal sealed class GroupMembershipChangedHandler : INotificationHandler<ChatMembershipChanged>
    {
        private readonly ILogger<GroupMembershipChangedHandler> _logger;
        private readonly IConversationRepository _conversationRepository;
        private readonly ISelfParticipantIdProvider _selfProvider;
        private readonly IGroupManagerResolver _groupManagerResolver;
        private readonly ITransportKeyResolver _transportKeyResolver;
        private readonly IMediator _mediator;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IGroupAdminStateStore _adminStateStore;
        private readonly IGroupManagerStateStore _gmStateStore;
        private readonly IAtRestKeyProvider _atRestKeyProvider;
        private readonly IRecipientPkhResolver _recipientPkhResolver;
        private readonly IRemoteEnvelopeSender _sender;

        public GroupMembershipChangedHandler(
            ILogger<GroupMembershipChangedHandler> logger,
            IConversationRepository conversationRepository,
            ISelfParticipantIdProvider selfProvider,
            IGroupManagerResolver groupManagerResolver,
            ITransportKeyResolver transportKeyResolver,
            IMediator mediator,
            ActiveIdentityContext activeIdentityContext,
            IGroupAdminStateStore adminStateStore,
            IGroupManagerStateStore gmStateStore,
            IAtRestKeyProvider atRestKeyProvider,
            IRecipientPkhResolver recipientPkhResolver,
            IRemoteEnvelopeSender sender)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _selfProvider = selfProvider;
            _groupManagerResolver = groupManagerResolver;
            _transportKeyResolver = transportKeyResolver;
            _mediator = mediator;
            _activeIdentityContext = activeIdentityContext;
            _adminStateStore = adminStateStore;
            _gmStateStore = gmStateStore;
            _atRestKeyProvider = atRestKeyProvider;
            _recipientPkhResolver = recipientPkhResolver;
            _sender = sender;
        }

        public async Task Handle(ChatMembershipChanged notification, CancellationToken cancellationToken)
        {
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogWarning("[GroupMembershipChanged] No active identity");
                return;
            }
            // Resolve conversation
            var self = _selfProvider.Get();
            var selfIdentityId = _activeIdentityContext.Identity?.SelfIdentityId;
            var convo = await _conversationRepository.GetByIdAsync(new ConversationId(notification.ConversationId), selfIdentityId: selfIdentityId.Value).ConfigureAwait(false);
            if (convo is null)
            {
                _logger.LogWarning("[GroupMembershipChanged] Conversation {ConversationId} not found", notification.ConversationId);
                return;
            }

            // Ensure we have a GroupManager for this conversation
            if (!_groupManagerResolver.TryGet(notification.ConversationId, out var manager))
            {
                _logger.LogWarning("[GroupMembershipChanged] No GroupManager available for conversation {ConversationId}", notification.ConversationId);
                return;
            }

            // Determine next key version
            var adminState = await _adminStateStore.GetAsync(notification.ConversationId, cancellationToken).ConfigureAwait(false);
            var nextVersion = (adminState?.LastCommittedKeyVersion ?? 0) + 1;

            // Generate fresh group key material and import locally (acting admin sets the key first)
            var newKey = RandomNumberGenerator.GetBytes(CryptoUtils.KeySize);
            manager.ImportKey(new GroupKeyVersionC(nextVersion), new GroupKeyMaterial(newKey));
            var masterKey = await _atRestKeyProvider.GetMasterKeyAsync(cancellationToken).ConfigureAwait(false);
            var stateBlob = manager.SaveState(masterKey);
            await _gmStateStore.SaveAsync(notification.ConversationId, stateBlob, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

            // Build and send per-member KeyDistributionPayload
            foreach (var participant in convo.Participants)
            {
                // Skip self
                if (participant.Value == _activeIdentityContext.Identity?.Id)
                    continue;

                // Resolve transport AEAD key for this recipient
                var aeadKey = await _transportKeyResolver.GetAeadKeyAsync(notification.ConversationId, participant.Value, cancellationToken).ConfigureAwait(false);
                if (aeadKey is null)
                {
                    _logger.LogWarning("[GroupMembershipChanged] No AEAD key for recipient {Participant} in conversation {ConversationId}", participant.Value, notification.ConversationId);
                    continue;
                }

                // Encrypt new group key into KeyEnvelope v1 (alg=1 AES-GCM-256)
                var nonce = RandomNumberGenerator.GetBytes(12);
                using var aead = new AesGcm(aeadKey, 16);
                var ciphertext = new byte[newKey.Length];
                var tag = new byte[16];
                aead.Encrypt(nonce, newKey, ciphertext, tag, associatedData: null);

                // Serialize envelope: [ver=1][alg=1][nLen][nonce][tLen][tag][cLen(4)][cipher]
                byte ver = 1;
                ushort alg = 1;
                var buf = new byte[1 + 2 + 1 + nonce.Length + 1 + tag.Length + 4 + ciphertext.Length];
                int offset = 0;
                buf[offset++] = ver;
                buf[offset++] = (byte)(alg >> 8);
                buf[offset++] = (byte)(alg & 0xFF);
                buf[offset++] = (byte)nonce.Length;
                Array.Copy(nonce, 0, buf, offset, nonce.Length); offset += nonce.Length;
                buf[offset++] = (byte)tag.Length;
                Array.Copy(tag, 0, buf, offset, tag.Length); offset += tag.Length;
                buf[offset++] = (byte)((ciphertext.Length >> 24) & 0xFF);
                buf[offset++] = (byte)((ciphertext.Length >> 16) & 0xFF);
                buf[offset++] = (byte)((ciphertext.Length >> 8) & 0xFF);
                buf[offset++] = (byte)(ciphertext.Length & 0xFF);
                Array.Copy(ciphertext, 0, buf, offset, ciphertext.Length); offset += ciphertext.Length;

                // Build ChatEnvelope.KeyDistributionPayload
                var payload = new KeyDistributionPayload
                {
                    Version = 1,
                    GroupConversationGuid = ByteString.CopyFrom(notification.ConversationId.ToByteArray()),
                    KeyVersion = nextVersion,
                    EncryptedGroupKeyForRecipient = ByteString.CopyFrom(buf)
                };
                var chat = new ChatEnvelope { KeyDistribution = payload };

                // Resolve recipient PKH (for host-enqueue fallback)
                var pkh = await _recipientPkhResolver.GetActivePkhAsync(participant.Value, cancellationToken).ConfigureAwait(false);
                var identityPeerId = new Percolator.Identity.PeerId(participant.Value);
                await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(identityPeerId, pkh), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
