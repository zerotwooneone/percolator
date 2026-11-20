namespace Percolator.Application.Connections;

using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;

/// <summary>
/// Domain application service port for processing quarantined inbound connection/contact requests.
/// Coordinates contact approval, handshake completion, and pending handshake record lifecycle ([SYS-BLOCK-WIRE-INDISTINGUISHABILITY]).
/// </summary>
public interface IInboundConnectionWorkflow
{
    Task<DomainResult<bool>> AcceptRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default);

    Task<DomainResult<bool>> RejectRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default);

    Task<DomainResult<bool>> BlockRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default);
}
