using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using MediatR;
using Percolator.Contracts;
using Google.Protobuf;
using Percolator.Prekey.Handlers;

namespace Percolator.Application.Network
{
    public class PercolatorMessageService : TransportService.TransportServiceBase
    {
        private readonly ILogger<PercolatorMessageService> _logger;
        private readonly IMediator _mediator;

        public PercolatorMessageService(
            ILogger<PercolatorMessageService> logger, 
            IMediator mediator)
        {
            _logger = logger;
            _mediator = mediator;
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
            var clientCertificate = await context.GetHttpContext().Connection.GetClientCertificateAsync();

            var command = new EstablishDirectSessionCommand
            {
                IdentitySigningKeyBytes = request.ResponderBundle.IdentitySigningKey.ToByteArray(),
                SignedPayloadBytes = request.ResponderBundle.SignedPayload.ToByteArray(),
                PayloadSignatureBytes = request.ResponderBundle.PayloadSignature.ToByteArray(),
                OneTimePreKeyBytes = request.ResponderBundle.HasOneTimePreKey ? request.ResponderBundle.OneTimePreKey.ToByteArray() : null,
                PreKeyBytes = payload.ResponderEphemeralKey.ToByteArray(),
                ClientCertificate = clientCertificate,
                PeerEndPoint = peerEndPoint
            };

            var result = await _mediator.Send(command, context.CancellationToken);

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
            var command = new DeliverOpaqueMessageCommand
            {
                PayloadBytes = request.Payload.ToByteArray()
            };

            var result = await _mediator.Send(command, context.CancellationToken);
            var resp = new DeliverOpaqueMessageResponse { Version = 1 };
            if (result.ResponsePayloadBytes is not null)
            {
                resp.ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                {
                    Version = 1,
                    ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes)
                };
            }
            return resp;
        }
    }
}