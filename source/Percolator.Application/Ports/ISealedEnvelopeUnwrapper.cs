using Percolator.Application.Ingress;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application.Ports;

public interface ISealedEnvelopeUnwrapper
{
    DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope);
    DomainResult<InboundEnvelope> UnwrapDirect(ReadOnlyMemory<byte> payloadBytes);
}
