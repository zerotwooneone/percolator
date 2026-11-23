using Percolator.Application.Ingress;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

public interface ISealedEnvelopeUnwrapper
{
    DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope, PublicIdentityId recipientId);
}
