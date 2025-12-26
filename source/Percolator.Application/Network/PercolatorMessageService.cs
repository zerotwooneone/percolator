using System;
using System.Linq;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ingress;
using Percolator.Contracts;
using Percolator.Prekey.Handlers;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Network
{
    public class PercolatorMessageService : TransportService.TransportServiceBase
    {
        private readonly ILogger<PercolatorMessageService> _logger;
        private readonly IMessageIngress _messageIngress;
        private readonly IEstablishDirectSessionService _establishService;

        public PercolatorMessageService(
            ILogger<PercolatorMessageService> logger,
            IMessageIngress messageIngress,
            IEstablishDirectSessionService establishService)
        {
            _logger = logger;
            _messageIngress = messageIngress;
            _establishService = establishService;
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
                requestCorrelationId = await _establishService.QueueInviteAsync(
                        request.InviterIdentityKey.ToByteArray(),
                        request.Payload.ToByteArray(),
                        request.PayloadSignature.ToByteArray(),
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
            var correlationId = context.RequestHeaders
                .FirstOrDefault(h => string.Equals(h.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            var ingressPayload = new IngressOpaquePayload(
                PayloadBytes: request.Payload.ToByteArray(),
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
    }
}