using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.Network.Handshake
{
    // Client-side processor for opaque relayed payloads. Do not parse here; hand the bytes to client logic.
    public record ProcessRelayedOpaquePayloadCommand(byte[] OpaquePayload) : IRequest;

    internal class ProcessRelayedOpaquePayloadHandler : IRequestHandler<ProcessRelayedOpaquePayloadCommand>
    {
        private readonly ILogger<ProcessRelayedOpaquePayloadHandler> _logger;

        public ProcessRelayedOpaquePayloadHandler(ILogger<ProcessRelayedOpaquePayloadHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(ProcessRelayedOpaquePayloadCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received relayed opaque payload (len={Len})", request.OpaquePayload?.Length ?? 0);
            // TODO: hand off 'request.OpaquePayload' to the peer-side opaque processor (X3DH/DR) within the client app layer.
            return Task.CompletedTask;
        }
    }
}
