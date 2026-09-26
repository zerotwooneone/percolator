using Percolator.Domain.Common;
using Percolator.Domain.Identities.Events;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.Model;

public sealed class IdentityProfile : AggregateRoot<PublicIdentityId>
{
    public override PublicIdentityId Id { get; }
    public string DisplayName { get; private set; }
    public IdentityRole Role { get; private set; }
    public IdentityState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DisabledAtUtc { get; private set; }

    private IdentityProfile(
        PublicIdentityId id,
        string displayName,
        IdentityRole role,
        IdentityState state,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        DisplayName = displayName;
        Role = role;
        State = state;
        CreatedAtUtc = createdAtUtc;
        DisabledAtUtc = null;
    }

    public static DomainResult<IdentityProfile> Create(
        PublicIdentityId id,
        string displayName,
        IdentityRole role,
        IDateTimeProvider timeProvider)
    {
        if (id.IsEmpty)
        {
            return DomainResult<IdentityProfile>.Failure(new DomainError("INVALID_IDENTITY_ID", "PublicIdentityId cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return DomainResult<IdentityProfile>.Failure(new DomainError("INVALID_DISPLAY_NAME", "DisplayName cannot be null or whitespace."));
        }

        var profile = new IdentityProfile(id, displayName.Trim(), role, IdentityState.Active, timeProvider.UtcNow);
        profile.AddDomainEvent(new IdentityCreatedEvent(id, role, timeProvider.UtcNow));

        return DomainResult<IdentityProfile>.Success(profile);
    }

    public DomainResult Disable(IDateTimeProvider timeProvider)
    {
        if (State == IdentityState.Suspended)
        {
            return DomainResult.Failure(new DomainError("SUSPENDED_IDENTITY", "Cannot disable a suspended identity."));
        }

        if (State == IdentityState.Disabled)
        {
            return DomainResult.Success();
        }

        State = IdentityState.Disabled;
        DisabledAtUtc = timeProvider.UtcNow;

        AddDomainEvent(new IdentityDisabledEvent(Id, DisabledAtUtc.Value));

        return DomainResult.Success();
    }

    public DomainResult Enable(IDateTimeProvider timeProvider)
    {
        if (State == IdentityState.Suspended)
        {
            return DomainResult.Failure(new DomainError("SUSPENDED_IDENTITY", "Cannot enable a suspended identity."));
        }

        if (State == IdentityState.Active)
        {
            return DomainResult.Success();
        }

        State = IdentityState.Active;
        DisabledAtUtc = null;

        AddDomainEvent(new IdentityEnabledEvent(Id, timeProvider.UtcNow));

        return DomainResult.Success();
    }
}
