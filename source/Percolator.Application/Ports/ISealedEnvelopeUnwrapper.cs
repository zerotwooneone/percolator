using Percolator.Application2.Ingress;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application2.Ports;

public interface ISealedEnvelopeUnwrapper
{
    DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope);
}
