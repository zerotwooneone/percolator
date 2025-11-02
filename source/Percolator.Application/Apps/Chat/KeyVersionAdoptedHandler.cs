using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Network;
using Percolator.Application.Network;

namespace Percolator.Application.Apps.Chat
{
    // Listens for local key adoption and attempts to notify the acting admin.
    // Routing to acting admin is best-effort for now; if we cannot resolve, we log and return.
    internal sealed class KeyVersionAdoptedHandler : INotificationHandler<KeyVersionAdoptedNotification>
    {
        private readonly ILogger<KeyVersionAdoptedHandler> _logger;
        private readonly IMediator _mediator;
        private readonly Percolator.Network.ISigningService _signingService;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IRecipientPkhResolver _pkhResolver;
        private readonly IActingAdminResolver _actingAdminResolver;
        private readonly IRemoteEnvelopeSender _sender;

        public KeyVersionAdoptedHandler(
            ILogger<KeyVersionAdoptedHandler> logger,
            IMediator mediator,
            Percolator.Network.ISigningService signingService,
            ActiveIdentityContext activeIdentityContext,
            IRecipientPkhResolver pkhResolver,
            IActingAdminResolver actingAdminResolver,
            IRemoteEnvelopeSender sender)
        {
            _logger = logger;
            _mediator = mediator;
            _signingService = signingService;
            _activeIdentityContext = activeIdentityContext;
            _pkhResolver = pkhResolver;
            _actingAdminResolver = actingAdminResolver;
            _sender = sender;
        }

        public async Task Handle(KeyVersionAdoptedNotification notification, CancellationToken ct)
        {
            // Build KeyAdoptionConfirmation
            var convoGuidBytes = notification.ConversationId.ToByteArray();
            var adopterPubKey = _signingService.GetActivePublicKey();

            var payloadBytes = BuildCanonicalConfirmationPayload(convoGuidBytes, notification.KeyVersion, adopterPubKey.Value);
            var signature = _signingService.Sign(new Payload(payloadBytes));

            var chat = new ChatEnvelope
            {
                KeyAdoptionConfirmation = new SignedKeyAdoptionConfirmation
                {
                    GroupConversationGuid = ByteString.CopyFrom(convoGuidBytes),
                    KeyVersion = notification.KeyVersion,
                    AdopterIdentityKey = ByteString.CopyFrom(adopterPubKey.Value),
                    SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Signature = ByteString.CopyFrom(signature.Value)
                }
            };
            // Chat envelope ready for dispatch

            var adminPeerId = await _actingAdminResolver.GetActingAdminPeerIdAsync(notification.ConversationId, ct).ConfigureAwait(false);
            if (adminPeerId is null)
            {
                _logger.LogWarning("[KeyVersionAdoptedHandler] Acting admin unresolved for conversation {ConversationId}; skipping confirmation send.", notification.ConversationId);
                return;
            }
            var pkh = await _pkhResolver.GetActivePkhAsync(adminPeerId.Value, ct).ConfigureAwait(false);
            var route = new RecipientRoute(new Percolator.Identity.PeerId(adminPeerId.Value), pkh);
            await _sender.SendChatEnvelopeToPeerAsync(chat, route, ct).ConfigureAwait(false);
        }

        private static byte[] BuildCanonicalConfirmationPayload(byte[] conversationGuid, uint keyVersion, byte[] adopterPublicKey)
        {
            // Deterministic concatenation: GUID(16) || keyVersion (uint32 big-endian) || adopter pubkey bytes
            var bytes = new byte[16 + 4 + adopterPublicKey.Length];
            Buffer.BlockCopy(conversationGuid, 0, bytes, 0, 16);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), keyVersion);
            Buffer.BlockCopy(adopterPublicKey, 0, bytes, 20, adopterPublicKey.Length);
            return bytes;
        }
    }
}
