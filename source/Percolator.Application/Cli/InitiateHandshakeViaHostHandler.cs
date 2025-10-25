using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Cli;

public sealed class InitiateHandshakeViaHostHandler : IRequestHandler<InitiateHandshakeViaHostCommand, Unit>
{
    private readonly ILogger<InitiateHandshakeViaHostHandler> _logger;
    private readonly IConversationService _conversationService;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transport;
    private readonly IPeerRepository _peerRepository;
    private readonly IMediator _mediator;

    public InitiateHandshakeViaHostHandler(
        ILogger<InitiateHandshakeViaHostHandler> logger,
        IConversationService conversationService,
        IDirectSessionManager sessionManager,
        IMessageTransportService transport,
        IPeerRepository peerRepository,
        IMediator mediator)
    {
        _logger = logger;
        _conversationService = conversationService;
        _sessionManager = sessionManager;
        _transport = transport;
        _peerRepository = peerRepository;
        _mediator = mediator;
    }

    public async Task<Unit> Handle(InitiateHandshakeViaHostCommand request, CancellationToken cancellationToken)
    {
        if (request.TargetPublicKeyHash is null || request.TargetPublicKeyHash.Length == 0)
            throw new ArgumentException("TargetPublicKeyHash must be provided.", nameof(request.TargetPublicKeyHash));

        // Resolve Host and ensure an existing direct session to Host
        var hostPeer = await _peerRepository.GetByNameAsync(request.HostPeerName)
            ?? throw new InvalidOperationException($"Peer '{request.HostPeerName}' not found.");
        var hostSession = await _conversationService.GetExistingDirectSessionAsync(hostPeer)
            ?? throw new InvalidOperationException("Direct session to Host not found. Establish a session before initiating handshake.");

        // 1) Request pre-key bundle for target PKH from Host
        var getReq = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                {
                    Version = 1,
                    PublicKeyHash = ByteString.CopyFrom(request.TargetPublicKeyHash)
                }
            }
        };

        var plaintext = new Plaintext(getReq.ToByteArray());
        var cryptoHostSessionId = new SessionId(hostSession.Value);
        var ratchetMessage = await _sessionManager.EncryptMessageAsync(cryptoHostSessionId, plaintext);
        _logger.LogInformation("Requesting pre-key bundle for PKH via Host {PeerId}", hostPeer.Id);
        var deliverResp = await _transport.SendMessageAsync(hostPeer.Id, hostSession, ratchetMessage, cancellationToken);

        if (deliverResp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
            || deliverResp.ResponsePayload is null
            || !deliverResp.ResponsePayload.HasResponsePayload)
        {
            throw new InvalidOperationException("No response payload returned for GetPreKeyBundle.");
        }

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var respPlain = await _sessionManager.ReceiveMessageAsync(cryptoHostSessionId, respCipher)
            ?? throw new InvalidOperationException("Could not decrypt GetPreKeyBundle response payload.");

        var internalResp = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
        if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse)
            throw new InvalidOperationException("Unexpected response type for GetPreKeyBundle.");
        var bundleMsg = internalResp.GetPreKeyBundleResponse?.PreKeyBundle
            ?? throw new InvalidOperationException("No pre-key bundle found in response.");

        // 2) Orchestrate Initiator Hello enqueue via existing command
        var remoteIdentitySpki = bundleMsg.IdentityKey?.ToByteArray() ?? Array.Empty<byte>();
        var remoteSignedPreKeySpki = bundleMsg.SignedPreKey?.ToByteArray() ?? Array.Empty<byte>();
        if (remoteIdentitySpki.Length == 0 || remoteSignedPreKeySpki.Length == 0)
            throw new InvalidOperationException("Incomplete pre-key bundle returned by Host.");

        var signedPreKeyId = bundleMsg.HasSignedPreKeyId
            ? new Guid(bundleMsg.SignedPreKeyId.ToByteArray())
            : throw new InvalidOperationException("SignedPreKeyId missing in bundle.");
        Guid? oneTimePreKeyId = bundleMsg.HasOneTimeKeyId ? new Guid(bundleMsg.OneTimeKeyId.ToByteArray()) : (Guid?)null;

        await _mediator.Send(new ComposeAndEnqueueInitiatorHelloCommand(
            RecipientPublicKeyHash: request.TargetPublicKeyHash,
            RemoteIdentityKeySpki: remoteIdentitySpki,
            SignedPreKeyId: signedPreKeyId,
            OneTimePreKeyId: oneTimePreKeyId,
            RemotePreKeySpki: remoteSignedPreKeySpki,
            InitiatorPayload: request.InitiatorPayload
        ), cancellationToken);

        _logger.LogInformation("Initiator Hello enqueued via Host MQ for PKH target.");
        return Unit.Value;
    }
}
