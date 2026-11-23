namespace Percolator.Domain.Delivery.Hosting;

using System;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

public enum RegistrationModerationStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2,
    Blocked = 3
}

public sealed record PendingMailboxRegistration(
    PublicIdentityId RelayIdentityId,
    BlindedRoutingToken ApplicantRoutingToken,
    RegistrationModerationStatus Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc = null);
