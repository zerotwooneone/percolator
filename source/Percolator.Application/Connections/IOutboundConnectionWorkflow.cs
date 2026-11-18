using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;

namespace Percolator.Application.Connections;

public interface IOutboundConnectionWorkflow
{
    Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
        PeerInvitationPayload payload,
        TimeSpan probeTimeout,
        CancellationToken ct = default);
}
