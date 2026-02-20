using MediatR;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    public class DeliverOpaqueMessageCommand : IRequest<DeliverOpaqueMessageResult>
    {
        public byte[] PayloadBytes { get; set; } = Array.Empty<byte>();

        public SelfId SelfIdentityId { get; set; }
    }

    public class DeliverOpaqueMessageResult
    {
        public byte[]? ResponsePayloadBytes { get; set; }
    }
}
