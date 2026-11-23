namespace Percolator.Domain.Delivery.Ports;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

public interface IPendingMailboxRegistrationRepository
{
    Task<PendingMailboxRegistration?> GetByApplicantRoutingTokenAsync(
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<PendingMailboxRegistration>> GetPendingByRelayIdAsync(
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default);

    Task SaveAsync(
        PendingMailboxRegistration registration,
        CancellationToken ct = default);

    Task UpdateStatusAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        RegistrationModerationStatus status,
        CancellationToken ct = default);
}
