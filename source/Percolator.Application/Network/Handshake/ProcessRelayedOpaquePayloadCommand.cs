using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Cryptography;
using Percolator.Application.Services;
using Percolator.Network;

namespace Percolator.Application.Network.Handshake
{
    // Client-side processor for opaque relayed payloads. These bytes are already decrypted from Host↔Client.
    // We now parse the inner InternalEnvelope and, if it contains a handshake hello, complete responder-side handshake.
    public record ProcessRelayedOpaquePayloadCommand(SelfId SelfIdentityId, Payload OpaquePayload, Percolator.Identity.PeerId RelayHostPeerId) : IRequest<ProcessRelayedOpaquePayloadResponse>;

    internal record ProcessRelayedOpaquePayloadResponse
    {
        public bool WasSuccess { get; private init; }
        public static ProcessRelayedOpaquePayloadResponse Success => new() { WasSuccess = true };
        public static ProcessRelayedOpaquePayloadResponse Failure => new() { WasSuccess = false };  
    };
    internal class ProcessRelayedOpaquePayloadHandler : IRequestHandler<ProcessRelayedOpaquePayloadCommand, ProcessRelayedOpaquePayloadResponse>
    {
        private readonly ILogger<ProcessRelayedOpaquePayloadHandler> _logger;
        private readonly IMediator _mediator;
        private readonly ISecureMessagingService _secureMessaging;
        private readonly IMessageTransportService _transport;
        private readonly IDirectSessionLocator _directSessions;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IEstablishDirectSessionService _establishDirectSessionService;
        private readonly IInviteHandshakeResponseIngress _inviteHandshakeResponseIngress;
        private readonly IStandardHandshakeIngress _standardHandshakeIngress;
        private readonly IInitiatorFinalizeService _initiatorFinalize;

        private static readonly HashSet<InternalEnvelope.ApplicationPayloadOneofCase> AllowedCases = new()
        {
            InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.FileShareEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.SubmitPreKeyBundleResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.EnqueueOpaqueMessageResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.FetchQueuedMessagesResponse
        };

        public ProcessRelayedOpaquePayloadHandler(
            ILogger<ProcessRelayedOpaquePayloadHandler> logger,
            IMediator mediator,
            ISecureMessagingService secureMessaging,
            IMessageTransportService transport,
            IDirectSessionLocator directSessions,
            IDirectSessionRepository directSessionRepository,
            IEstablishDirectSessionService establishDirectSessionService,
            IInviteHandshakeResponseIngress inviteHandshakeResponseIngress,
            IStandardHandshakeIngress standardHandshakeIngress,
            IInitiatorFinalizeService initiatorFinalize)
        {
            _logger = logger;
            _mediator = mediator;
            _secureMessaging = secureMessaging;
            _transport = transport;
            _directSessions = directSessions;
            _directSessionRepository = directSessionRepository;
            _establishDirectSessionService = establishDirectSessionService;
            _inviteHandshakeResponseIngress = inviteHandshakeResponseIngress;
            _standardHandshakeIngress = standardHandshakeIngress;
            _initiatorFinalize = initiatorFinalize;
        }

        public async Task<ProcessRelayedOpaquePayloadResponse> Handle(ProcessRelayedOpaquePayloadCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received relayed opaque payload (len={Len})", request.OpaquePayload.Span.Length);

            if (request.OpaquePayload is null || request.OpaquePayload.Span.Length == 0)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            // First attempt: treat as a DR SessionRatchetMessage opaque to the host.
            var selfIdentityId = request.SelfIdentityId.Value;

            SessionRatchetMessage ratchetMessage;
            try
            {
                ratchetMessage = SessionRatchetMessage.FromSpan(request.OpaquePayload.Span);
            }
            catch
            {
                // Not a valid ratchet message: try known non-session payload types (still opaque to relay).
                return await TryHandleNonSessionPayloadAsync(request.SelfIdentityId, request.RelayHostPeerId, request.OpaquePayload.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            (RatchetEphemeralKey PreKey, ulong Counter, ulong PreviousChainLength) header;
            try
            {
                header = ratchetMessage.GetHeader();
            }
            catch (Exception drEx)
            {
                // Not a valid ratchet message header: try known non-session payload types (still opaque to relay).
                return await TryHandleNonSessionPayloadAsync(request.SelfIdentityId, request.RelayHostPeerId, request.OpaquePayload.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            // Fast/slow path via SecureMessagingService
            var resolved = await _secureMessaging.DecryptInboundAsync(selfIdentityId, ratchetMessage, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            var sid = resolved.Value.sessionId;
            var plaintext = resolved.Value.plaintext;

            if (plaintext is null)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }

            InternalEnvelope inner;
            try
            {
                inner = InternalEnvelope.Parser.ParseFrom(plaintext.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Decrypted relayed payload was not a valid InternalEnvelope; dropping");
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }

            if (!AllowedCases.Contains(inner.ApplicationPayloadCase))
            {
                _logger.LogWarning("Relayed InternalEnvelope case {Case} not allowed; dropping", inner.ApplicationPayloadCase);
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            _logger.LogDebug("Relayed InternalEnvelope allowed case {Case}; delegating to orchestrator", inner.ApplicationPayloadCase);

            uint? remotePeerGuid = null;
            try
            {
                var directSession = await _directSessionRepository
                    .GetBySessionIdAsync(new DirectSessionId(sid.Value), selfIdentityId)
                    .ConfigureAwait(false);
                remotePeerGuid = directSession?.RemotePeerId.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve remote peer for relayed session {SessionId}; continuing with null RemotePeerGuid", sid);
            }

            await _mediator.Send(new Percolator.Application.Network.ProcessInternalEnvelopeCommand(
                inner,
                new Percolator.Application.Network.SessionContext(sid.Value, request.SelfIdentityId, remotePeerGuid)
            ), cancellationToken).ConfigureAwait(false);

            return ProcessRelayedOpaquePayloadResponse.Success;
        }

        private async Task<ProcessRelayedOpaquePayloadResponse> TryHandleNonSessionPayloadAsync(
            SelfId selfIdentityId,
            Percolator.Identity.PeerId relayHostPeerId,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            // Standard signal bootstrap delivered through dumb relay queue: HandshakeInitiatorHello bytes.
            try
            {
                var hello = HandshakeInitiatorHello.Parser.ParseFrom(bytes);
                if (hello is not null
                    && hello.HasInitiatorIdentityKeySpki && hello.InitiatorIdentityKeySpki.Length > 0
                    && hello.HasInitiatorEphemeralKeySpki && hello.InitiatorEphemeralKeySpki.Length > 0
                    && hello.HasSignedPreKeyId && hello.SignedPreKeyId.Length > 0
                    && hello.HasInitiatorPublicIdentityId && hello.InitiatorPublicIdentityId.Length > 0)
                {
                    var establish = new EstablishSessionRequest
                    {
                        Version = 1,
                        IdentitySigningKey = hello.InitiatorIdentityKeySpki,
                        EphemeralKey = hello.InitiatorEphemeralKeySpki,
                        PrekeyId = hello.SignedPreKeyId,
                        PublicIdentityId = hello.InitiatorPublicIdentityId
                    };

                    if (hello.HasOneTimePreKeyId && hello.OneTimePreKeyId.Length > 0)
                    {
                        establish.OnetimePrekeyId = hello.OneTimePreKeyId;
                    }

                    var response = await _standardHandshakeIngress.HandleAsync(selfIdentityId, establish, cancellationToken).ConfigureAwait(false);

                    if (response?.Response is not null && response.Response.HasResponsePayload && response.Response.ResponsePayload.Length > 0)
                    {
                        var initiatorSpki = hello.InitiatorIdentityKeySpki.ToByteArray();
                        var initiatorPkh = SHA256.HashData(initiatorSpki);
                        await EnqueueResponseToRelayHostAsync(
                                selfIdentityId,
                                relayHostPeerId,
                                initiatorPkh,
                                response.ToByteArray(),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    return ProcessRelayedOpaquePayloadResponse.Success;
                }
            }
            catch
            {
                // Not a HandshakeInitiatorHello.
            }

            // Reverse-signal invite ingress delivered through dumb relay queue: EstablishDirectSessionRequest bytes.
            try
            {
                var req = EstablishDirectSessionRequest.Parser.ParseFrom(bytes);
                if (req is not null
                    && req.HasInviterIdentityKey && req.InviterIdentityKey.Length > 0
                    && req.HasPayload && req.Payload.Length > 0
                    && req.HasPayloadSignature && req.PayloadSignature.Length > 0)
                {
                    _ = await _establishDirectSessionService.QueueInviteAsync(
                        selfIdentityId,
                        req.InviterIdentityKey.ToByteArray(),
                        req.Payload.ToByteArray(),
                        req.PayloadSignature.ToByteArray(),
                        isRelayed: true,
                        relayHostPeerId: relayHostPeerId,
                        cancellationToken).ConfigureAwait(false);

                    return ProcessRelayedOpaquePayloadResponse.Success;
                }
            }
            catch
            {
                // Not an EstablishDirectSessionRequest.
            }

            // Reverse-signal response delivered through dumb relay queue: InviteHandshakeResponse bytes.
            try
            {
                var resp = InviteHandshakeResponse.Parser.ParseFrom(bytes);
                if (resp is not null
                    && resp.HasRequestCorrelationId
                    && resp.HasAcceptorIdentityKey && resp.AcceptorIdentityKey.Length > 0
                    && resp.HasAcceptorX3DhEphemeralKey && resp.AcceptorX3DhEphemeralKey.Length > 0
                    && resp.HasInitialRatchetMessage && resp.InitialRatchetMessage.Length > 0)
                {
                    await _inviteHandshakeResponseIngress.HandleAsync(selfIdentityId, resp, cancellationToken).ConfigureAwait(false);
                    return ProcessRelayedOpaquePayloadResponse.Success;
                }
            }
            catch
            {
                // Not an InviteHandshakeResponse.
            }

            // Standard handshake response delivered through dumb relay queue: EstablishSessionResponse bytes.
            try
            {
                var resp = EstablishSessionResponse.Parser.ParseFrom(bytes);
                if (resp is not null
                    && resp.Response is not null
                    && resp.Response.HasIdentitySigningKey && resp.Response.IdentitySigningKey.Length > 0
                    && resp.Response.HasResponsePayload && resp.Response.ResponsePayload.Length > 0
                    && resp.Response.HasPayloadSignature && resp.Response.PayloadSignature.Length > 0)
                {
                    var sid = await _initiatorFinalize
                        .TryFinalizeFromEstablishSessionResponseAsync(selfIdentityId, resp, relayHostPeerId, cancellationToken)
                        .ConfigureAwait(false);
                    return sid is null ? ProcessRelayedOpaquePayloadResponse.Failure : ProcessRelayedOpaquePayloadResponse.Success;
                }
            }
            catch
            {
                // Not an EstablishSessionResponse.
            }

            return ProcessRelayedOpaquePayloadResponse.Failure;
        }

        private async Task EnqueueResponseToRelayHostAsync(
            SelfId selfIdentityId,
            Percolator.Identity.PeerId relayHostPeerId,
            byte[] recipientPublicKeyHash,
            byte[] messageBlob,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
            if (messageBlob is null) throw new ArgumentNullException(nameof(messageBlob));

            var directSessionId = await _directSessions.GetAsync(relayHostPeerId, selfIdentityId.Value, cancellationToken).ConfigureAwait(false);
            if (directSessionId is null)
            {
                _logger.LogWarning("Cannot enqueue standard handshake response to relay host {RelayHostPeerId}: no direct session", relayHostPeerId);
                return;
            }

            var mqReq = new EnqueueOpaqueMessageRequest
            {
                Version = 1,
                RecipientPublicKeyHash = ByteString.CopyFrom(recipientPublicKeyHash),
                MessageBlob = ByteString.CopyFrom(messageBlob)
            };

            var env = new InternalEnvelope
            {
                MessageQueueEnvelope = new MessageQueueEnvelope
                {
                    Version = 1,
                    EnqueueOpaqueMessageRequest = mqReq
                }
            };

            var plain = Plaintext.FromBytes(env.ToByteArray());
            var sid = new Percolator.Cryptography.SessionId(directSessionId.Value.Value);
            var cipher = await _secureMessaging.EncryptAsync(sid, plain, cancellationToken).ConfigureAwait(false);

            _ = await _transport
                .SendMessageAsync(relayHostPeerId, directSessionId.Value, cipher, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    
}
