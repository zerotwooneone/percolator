using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.MessageQueue.Commands;
using Percolator.Network;

namespace Percolator.Application.Apps.Chat
{
    // Listens for local key adoption and attempts to notify the acting admin.
    // Routing to acting admin is best-effort for now; if we cannot resolve, we log and return.
    internal sealed class KeyVersionAdoptedHandler : INotificationHandler<KeyVersionAdoptedNotification>
    {
        private readonly ILogger<KeyVersionAdoptedHandler> _logger;
        private readonly IMediator _mediator;
        private readonly ISigningService _signingService;
        private readonly IRecipientPkhResolver _pkhResolver;
        private readonly IActingAdminResolver _actingAdminResolver;

        public KeyVersionAdoptedHandler(
            ILogger<KeyVersionAdoptedHandler> logger,
            IMediator mediator,
            ISigningService signingService,
            ActiveIdentityContext activeIdentityContext,
            IRecipientPkhResolver pkhResolver,
            IActingAdminResolver actingAdminResolver)
        {
            _logger = logger;
            _mediator = mediator;
            _signingService = signingService;
            _pkhResolver = pkhResolver;
            _actingAdminResolver = actingAdminResolver;
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
            var internalEnvelope = new InternalEnvelope { ChatEnvelope = chat };
            var messageBytes = internalEnvelope.ToByteArray();

            var adminPeerId = await _actingAdminResolver.GetActingAdminPeerIdAsync(notification.ConversationId, ct);
            if (adminPeerId is null)
            {
                _logger.LogWarning("[KeyVersionAdoptedHandler] Acting admin unresolved for conversation {ConversationId}; skipping confirmation send.", notification.ConversationId);
                return;
            }
            var pkh = await _pkhResolver.GetActivePkhAsync(adminPeerId.Value, ct);
            if (pkh is null)
            {
                _logger.LogWarning("[KeyVersionAdoptedHandler] No active PKH for acting admin {PeerId}; skipping.", adminPeerId.Value);
                return;
            }
            await _mediator.Send(new EnqueueOpaqueMessageCommand(pkh, messageBytes), ct);
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
