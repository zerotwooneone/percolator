using MediatR;

namespace Percolator.Application.Network
{
    public class DeliverOpaqueMessageCommand : IRequest<DeliverOpaqueMessageResult>
    {
        public byte[] PayloadBytes { get; set; } = Array.Empty<byte>();
    }

    public class DeliverOpaqueMessageResult
    {
        public byte[]? ResponsePayloadBytes { get; set; }
    }
}
