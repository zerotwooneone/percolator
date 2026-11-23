namespace Percolator.Application.Hosting;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

public sealed class ExchangeRegistrationModerationService : IExchangeRegistrationModerationService
{
    private readonly IPendingMailboxRegistrationRepository _pendingRegistrationRepo;

    public ExchangeRegistrationModerationService(IPendingMailboxRegistrationRepository pendingRegistrationRepo)
    {
        _pendingRegistrationRepo = pendingRegistrationRepo ?? throw new ArgumentNullException(nameof(pendingRegistrationRepo));
    }

    public async Task<IReadOnlyList<PendingMailboxRegistration>> GetPendingRegistrationsAsync(
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default)
    {
        return await _pendingRegistrationRepo.GetPendingByRelayIdAsync(relayIdentityId, ct).ConfigureAwait(false);
    }

    public async Task<DomainResult> ApproveAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default)
    {
        await _pendingRegistrationRepo.UpdateStatusAsync(
            relayIdentityId,
            applicantRoutingToken,
            RegistrationModerationStatus.Approved,
            ct).ConfigureAwait(false);

        return DomainResult.Success();
    }

    public async Task<DomainResult> RejectAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default)
    {
        await _pendingRegistrationRepo.UpdateStatusAsync(
            relayIdentityId,
            applicantRoutingToken,
            RegistrationModerationStatus.Rejected,
            ct).ConfigureAwait(false);

        return DomainResult.Success();
    }

    public async Task<DomainResult> BlockAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default)
    {
        await _pendingRegistrationRepo.UpdateStatusAsync(
            relayIdentityId,
            applicantRoutingToken,
            RegistrationModerationStatus.Blocked,
            ct).ConfigureAwait(false);

        return DomainResult.Success();
    }
}
