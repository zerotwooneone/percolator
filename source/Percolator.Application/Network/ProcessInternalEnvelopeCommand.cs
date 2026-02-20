using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Contracts;
using Percolator.Network;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    // Context available from the caller (session/peer identity, etc.). Extend as needed.
    public sealed record SessionContext(Guid? SessionId, SelfId SelfIdentityId, Guid? RemotePeerGuid);

    // Request: provide a parsed InternalEnvelope and related context.
    public sealed record ProcessInternalEnvelopeCommand(
        InternalEnvelope Envelope,
        SessionContext Context
    ) : IRequest<InternalEnvelope?>;
}
