using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Application.Ingress;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using ProtobufDeliveryCertificate = Percolator.Contracts.DeliveryCertificate;

namespace Percolator.Infrastructure.Network.Grpc;

public class PercolatorMessageService : TransportService.TransportServiceBase
{
    private readonly ILogger<PercolatorMessageService> _logger;
    private readonly IMessageIngress _messageIngress;
    private readonly IEstablishDirectSessionService _establishService;
    private readonly IInviteHandshakeResponseIngress _inviteHandshakeResponseIngress;
    private readonly IStandardHandshakeIngress _standardHandshakeIngress;
    private readonly ActiveIdentityContext _active;
    private readonly ILocalIdentitySigner _localIdentitySigner;
    private readonly ISelfIdentityQueries _selfIdentityQueries;

    public PercolatorMessageService(
        ILogger<PercolatorMessageService> logger,
        IMessageIngress messageIngress,
        IEstablishDirectSessionService establishService,
        IInviteHandshakeResponseIngress inviteHandshakeResponseIngress,
        IStandardHandshakeIngress standardHandshakeIngress,
        ActiveIdentityContext active,
        ILocalIdentitySigner localIdentitySigner,
        ISelfIdentityQueries selfIdentityQueries)
    {
        _logger = logger;
        _messageIngress = messageIngress;
        _establishService = establishService;
        _inviteHandshakeResponseIngress = inviteHandshakeResponseIngress;
        _standardHandshakeIngress = standardHandshakeIngress;
        _active = active;
        _localIdentitySigner = localIdentitySigner;
        _selfIdentityQueries = selfIdentityQueries;
    }

    public override Task<EstablishSessionResponse> EstablishSession(EstablishSessionRequest request, ServerCallContext context)
    {
        if (_active.Identity is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Active identity not loaded."));
        }

        return _standardHandshakeIngress.HandleAsync(_active.Identity.SelfIdentityId, request, context.CancellationToken);
    }

    public override async Task<EstablishDirectSessionResponse> EstablishDirectSession(EstablishDirectSessionRequest request, ServerCallContext context)
    {
        if (!request.HasInviterIdentityKey || request.InviterIdentityKey.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"{nameof(EstablishDirectSessionRequest.InviterIdentityKey)} is required."));
        }

        if (!request.HasPayload || request.Payload.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "payload is required."));
        }

        if (!request.HasPayloadSignature || request.PayloadSignature.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "payload_signature is required."));
        }

        RequestCorrelationId requestCorrelationId;
        try
        {
            if (_active.Identity is null)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Active identity not loaded."));
            }

            requestCorrelationId = await _establishService.QueueInviteAsync(
                    _active.Identity.SelfIdentityId,
                    request.InviterIdentityKey.ToByteArray(),
                    request.Payload.ToByteArray(),
                    request.PayloadSignature.ToByteArray(),
                    isRelayed: false,
                    relayHostPeerId: null,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        return new EstablishDirectSessionResponse
        {
            Version = 1,
            Queued = new EstablishDirectSessionResponse.Types.Queued
            {
                Version = 1,
                RequestCorrelationId = requestCorrelationId.ToString()
            }
        };
    }

    public override async Task<DeliverOpaqueMessageResponse> DeliverOpaqueMessage(DeliverOpaqueMessageRequest request, ServerCallContext context)
    {
        if (_active.Identity is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Active identity not loaded."));
        }

        var correlationId = context.RequestHeaders
            .FirstOrDefault(h => string.Equals(h.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var ingressPayload = new IngressOpaquePayload(
            PayloadBytes: request.Payload.ToByteArray(),
            SelfIdentityId: _active.Identity.SelfIdentityId,
            RemotePeerId: null,
            TransportPeer: context.Peer,
            CorrelationId: correlationId);

        var result = await _messageIngress.DeliverOpaqueAsync(ingressPayload, context.CancellationToken).ConfigureAwait(false);

        if (result.Disposition == IngressDisposition.Accepted)
        {
            var resp = new DeliverOpaqueMessageResponse { Version = 1 };
            if (result.ResponseBytes is not null)
            {
                resp.ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                {
                    Version = 1,
                    ResponsePayload = ByteString.CopyFrom(result.ResponseBytes)
                };
            }
            return resp;
        }

        if (result.Disposition == IngressDisposition.Rejected_NotReady)
        {
            return new DeliverOpaqueMessageResponse
            {
                Version = 1,
                NotUntil = new DeliverOpaqueMessageResponse.Types.NotUntil { Version = 1 }
            };
        }

        if (result.Disposition == IngressDisposition.Rejected_Invalid || result.Disposition == IngressDisposition.Rejected_Unsupported)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Ingress rejected message: {result.Disposition}"));
        }

        throw new RpcException(new Status(StatusCode.Internal, $"Ingress failed: {result.Disposition}"));
    }

    public override async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponse(InviteHandshakeResponse request, ServerCallContext context)
    {
        if (!request.HasRequestCorrelationId || string.IsNullOrWhiteSpace(request.RequestCorrelationId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_correlation_id is required."));
        }
        if (!request.HasAcceptorIdentityKey || request.AcceptorIdentityKey.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "acceptor_identity_key is required."));
        }
        if (!request.HasAcceptorX3DhEphemeralKey || request.AcceptorX3DhEphemeralKey.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "acceptor_x3dh_ephemeral_key is required."));
        }
        if (!request.HasInitialRatchetMessage || request.InitialRatchetMessage.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "initial_ratchet_message is required."));
        }

        _logger.LogInformation("Received InviteHandshakeResponse for correlation {CorrelationId}", request.RequestCorrelationId);

        if (_active.Identity is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Active identity not loaded."));
        }

        await _inviteHandshakeResponseIngress.HandleAsync(_active.Identity.SelfIdentityId, request, context.CancellationToken).ConfigureAwait(false);
        return new DeliverInviteHandshakeResponseAck { Version = 1 };
    }

    public override async Task<GetDeliveryCertificateResponse> GetDeliveryCertificate(GetDeliveryCertificateRequest request, ServerCallContext context)
    {
        if (_active.Identity is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Active identity not loaded."));
        }

        // Get the cryptographic fingerprint (Rule 6 compliance - use PKH, not PeerId)
        var fingerprint = await _selfIdentityQueries.GetActiveIdentityFingerprintAsync(context.CancellationToken);
        if (fingerprint is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "No active identity key fingerprint found."));
        }

        // Construct the certificate payload (32-byte fingerprint + 8-byte expiration timestamp)
        var expiration = DateTimeOffset.UtcNow.AddHours(24);
        var payload = new byte[40]; // 32 bytes fingerprint + 8 bytes expiration timestamp
        fingerprint.Span.CopyTo(payload.AsSpan(0, 32));
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32, 8), expiration.ToUnixTimeSeconds());

        // Delegate the payload directly to the signer without inspecting raw keys
        var signature = await _localIdentitySigner.SignWithRelayRootKeyAsync(payload, context.CancellationToken);

        // Package the raw bytes from signature.Span into DeliveryCertificateResponse and return
        return new GetDeliveryCertificateResponse
        {
            Certificate = new ProtobufDeliveryCertificate
            {
                CertificateData = Google.Protobuf.ByteString.CopyFrom(payload),
                Signature = Google.Protobuf.ByteString.CopyFrom(signature.Span)
            }
        };
    }
}
