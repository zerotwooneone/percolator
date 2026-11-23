using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;

using Percolator.Domain.Identities;

namespace Percolator.Application.Connections;

public interface IOutboundConnectionWorkflow
{
    Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
        PublicIdentityId ownerId,
        PeerInvitationPayload payload,
        TimeSpan probeTimeout,
        CancellationToken ct = default);
}
