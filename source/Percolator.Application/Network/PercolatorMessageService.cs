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
            var payload = EstablishDirectSessionRequest.Types.DirectInitiatorPayload.Parser.ParseFrom(request.InitiatorBundle.SignedPayload);
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
                IdentitySigningKeyBytes = request.InitiatorBundle.IdentitySigningKey.ToByteArray(),
                SignedPayloadBytes = request.InitiatorBundle.SignedPayload.ToByteArray(),
                PayloadSignatureBytes = request.InitiatorBundle.PayloadSignature.ToByteArray(),
                OneTimePreKeyBytes = request.InitiatorBundle.HasOneTimePreKey ? request.InitiatorBundle.OneTimePreKey.ToByteArray() : null,
                PreKeyBytes = payload.SignedPreKey.ToByteArray(),
                ClientCertificate = clientCertificate,
                PeerEndPoint = peerEndPoint
            };

            var result = await _mediator.Send(command, context.CancellationToken);

            return new EstablishDirectSessionResponse
            {
                Response = new EstablishDirectSessionResponse.Types.Response
                {
                    IdentitySigningKey = ByteString.CopyFrom(result.IdentitySigningKeyBytes),
                    ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes),
                    PayloadSignature = ByteString.CopyFrom(result.PayloadSignatureBytes)
                }
            };
        }

        public override async Task<DeliverOpaqueMessageResponse> DeliverOpaqueMessage(DeliverOpaqueMessageRequest request, ServerCallContext context)
        {
            var command = new DeliverOpaqueMessageCommand
            {
                SessionId = Guid.Parse(request.SessionId),
                PayloadBytes = request.Payload.ToByteArray()
            };

            var result = await _mediator.Send(command, context.CancellationToken);
            var response = new DeliverOpaqueMessageResponse();
            if (result.ResponsePayloadBytes is not null)
            {
                response.ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes);
            }
            return response;
        }
    }
}