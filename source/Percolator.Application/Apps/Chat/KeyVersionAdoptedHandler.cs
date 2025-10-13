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
using Percolator.Application.Sessions;
using Percolator.Cryptography;

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
        private readonly IDirectSessionManager _sessions;
        private readonly IDirectSessionRepository _directSessionRepository;

        public KeyVersionAdoptedHandler(
            ILogger<KeyVersionAdoptedHandler> logger,
            IMediator mediator,
            Percolator.Network.ISigningService signingService,
            ActiveIdentityContext activeIdentityContext,
            IRecipientPkhResolver pkhResolver,
            IActingAdminResolver actingAdminResolver,
            IDirectSessionManager sessions,
            IDirectSessionRepository directSessionRepository)
        {
            _logger = logger;
            _mediator = mediator;
            _signingService = signingService;
            _activeIdentityContext = activeIdentityContext;
            _pkhResolver = pkhResolver;
            _actingAdminResolver = actingAdminResolver;
            _sessions = sessions;
            _directSessionRepository = directSessionRepository;
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
            // Encrypt the envelope for the acting admin using the host↔recipient session
            var selfIdentityId = _activeIdentityContext.Identity?.SelfIdentityId ?? 0;
            var direct = await _directSessionRepository.GetByRemotePeerIdAsync(new PeerId(adminPeerId.Value), selfIdentityId);
            if (direct is null)
            {
                _logger.LogWarning("[KeyVersionAdoptedHandler] No direct session with acting admin {PeerId}; skipping.", adminPeerId.Value);
                return;
            }
            var dr = await _sessions.EncryptMessageAsync(new SessionId(direct.SessionId.Value), new Plaintext(messageBytes));
            await _mediator.Send(new EnqueueOpaqueMessageCommand(pkh, dr.Value), ct);
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
