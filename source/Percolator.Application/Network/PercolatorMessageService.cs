using System;
using System.Linq;
using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ingress;
using Percolator.Contracts;
using Google.Protobuf;
using Percolator.Prekey.Handlers;

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
            // Map Protobuf to app command
            var payload = EstablishDirectSessionRequest.Types.DirectInitiatorPayload.Parser.ParseFrom(request.ResponderBundle.SignedPayload);
            if (!payload.HasCallbackPort || payload.CallbackPort < 1024 || payload.CallbackPort > 65535)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Invalid callback port: {payload.CallbackPort}"));
            }

            // Extract peer endpoint from context.Peer and payload callback port
            var peerGrpcEnpointParts = context.Peer.Split(':').Skip(1).ToArray();
            if (peerGrpcEnpointParts.Length != 2)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Invalid endpoint: {context.Peer}"));
            }
            var peerEndPoint = new DnsEndPoint(peerGrpcEnpointParts[0], (int)payload.CallbackPort);

            // HttpContext for client certificate (may be null in tests)
            var clientCertificate = await context.GetHttpContext().Connection.GetClientCertificateAsync().ConfigureAwait(false);

            var command = new EstablishDirectSessionCommand
            {
                RemoteIdentityKeyBytes = request.ResponderBundle.IdentitySigningKey.ToByteArray(),
                SignedPayloadBytes = request.ResponderBundle.SignedPayload.ToByteArray(),
                PayloadSignatureBytes = request.ResponderBundle.PayloadSignature.ToByteArray(),
                OneTimePreKeyBytes = request.ResponderBundle.HasOneTimePreKey ? request.ResponderBundle.OneTimePreKey.ToByteArray() : null,
                RemoteEphemeral = payload.ResponderEphemeralKey.ToByteArray(),
                ClientCertificate = clientCertificate,
                PeerEndPoint = peerEndPoint
            };

            var result = await _establishService.EstablishAsync(command, context.CancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                return new EstablishDirectSessionResponse()
                {
                    Never = new EstablishDirectSessionResponse.Types.Never()
                };
            }

            return new EstablishDirectSessionResponse
            {
                Response = new EstablishDirectSessionResponse.Types.Response
                {
                    InitiatorIdentityKey = ByteString.CopyFrom(result.IdentitySigningKeyBytes),
                    RatchetMessage = ByteString.CopyFrom(result.RatchetMessageBytes),
                    InitiatorEphemeralKey = ByteString.CopyFrom(result.RemoteEphemeralKeyBytes)
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