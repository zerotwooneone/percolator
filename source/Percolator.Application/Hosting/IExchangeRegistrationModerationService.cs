namespace Percolator.Application.Hosting;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

public interface IExchangeRegistrationModerationService
{
    Task<IReadOnlyList<PendingMailboxRegistration>> GetPendingRegistrationsAsync(
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default);

    Task<DomainResult> ApproveAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default);

    Task<DomainResult> RejectAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default);

    Task<DomainResult> BlockAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default);
}
