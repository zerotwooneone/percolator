using System.ComponentModel;
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

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IdentityProfile Rehydrate(
        PublicIdentityId id,
        string displayName,
        IdentityRole role,
        IdentityState state,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? disabledAtUtc = null)
    {
        if (!id.IsValid) throw new ArgumentException("PublicIdentityId cannot be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("DisplayName cannot be null or whitespace.", nameof(displayName));
        if (state == IdentityState.Disabled && !disabledAtUtc.HasValue) throw new ArgumentException("Disabled identity must have a DisabledAtUtc timestamp.", nameof(disabledAtUtc));
        if (state != IdentityState.Disabled && disabledAtUtc.HasValue) throw new ArgumentException("Non-disabled identity cannot have a DisabledAtUtc timestamp.", nameof(disabledAtUtc));

        var profile = new IdentityProfile(id, displayName, role, state, createdAtUtc)
        {
            DisabledAtUtc = disabledAtUtc
        };
        return profile;
    }

    public static DomainResult<IdentityProfile> Create(
        PublicIdentityId id,
        string displayName,
        IdentityRole role,
        IDateTimeProvider timeProvider)
    {
        if (!id.IsValid)
        {
            return DomainResult<IdentityProfile>.Failure(new DomainError("INVALID_IDENTITY_ID", "PublicIdentityId cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return DomainResult<IdentityProfile>.Failure(new DomainError("INVALID_DISPLAY_NAME", "DisplayName cannot be null or whitespace."));
        }

        var profile = new IdentityProfile(id, displayName.Trim(), role, IdentityState.Active, timeProvider.UtcNow);
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

        return DomainResult.Success();
    }
}
