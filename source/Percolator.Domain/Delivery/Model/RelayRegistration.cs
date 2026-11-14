using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Delivery.Model;

/// <summary>
/// Aggregate representing a relay known to this vault, along with the local identities
/// linked to mailboxes hosted on it. Id is the relay's PublicIdentityId.
/// </summary>
public sealed class RelayRegistration : AggregateRoot<PublicIdentityId>
{
    private readonly List<RelayMailboxBinding> _bindings = [];

    public override PublicIdentityId Id { get; }
    public RelayEndpoint Endpoint { get; private set; }
    public CertificatePin? PinnedCertificate { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public IReadOnlyCollection<RelayMailboxBinding> Bindings => _bindings.AsReadOnly();

    private RelayRegistration(
        PublicIdentityId id,
        RelayEndpoint endpoint,
        CertificatePin? pinnedCertificate,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Endpoint = endpoint;
        PinnedCertificate = pinnedCertificate;
        CreatedAtUtc = createdAtUtc;
    }

    public static DomainResult<RelayRegistration> Create(
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        CertificatePin? pinnedCertificate,
        IDateTimeProvider timeProvider)
    {
        if (!relayIdentityId.IsValid)
        {
            return DomainResult<RelayRegistration>.Failure(
                new DomainError("INVALID_RELAY_ID", "Relay identity ID must be valid."));
        }

        var registration = new RelayRegistration(
            relayIdentityId,
            endpoint,
            pinnedCertificate,
            timeProvider.UtcNow);

        return DomainResult<RelayRegistration>.Success(registration);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static RelayRegistration Rehydrate(
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        CertificatePin? pinnedCertificate,
        DateTimeOffset createdAtUtc,
        IEnumerable<RelayMailboxBinding> bindings)
    {
        if (!relayIdentityId.IsValid)
        {
            throw new ArgumentException("RelayIdentityId cannot be empty.", nameof(relayIdentityId));
        }

        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(bindings);

        var registration = new RelayRegistration(
            relayIdentityId,
            endpoint,
            pinnedCertificate,
            createdAtUtc);

        registration._bindings.AddRange(bindings);
        return registration;
    }

    /// <summary>
    /// Links a local identity to a mailbox on this relay. Idempotent: re-linking an already
    /// linked identity returns the existing binding, preserving its CSPRNG tokens.
    /// </summary>
    public DomainResult<RelayMailboxBinding> LinkIdentity(
        PublicIdentityId ownerIdentityId,
        IDateTimeProvider timeProvider,
        BlindedRoutingToken? routingToken = null,
        DeliveryToken? drainToken = null)
    {
        if (!ownerIdentityId.IsValid)
        {
            return DomainResult<RelayMailboxBinding>.Failure(
                new DomainError("INVALID_OWNER_ID", "Owner identity ID must be valid."));
        }

        var existing = _bindings.FirstOrDefault(b => b.OwnerIdentityId == ownerIdentityId);
        if (existing is not null)
        {
            return DomainResult<RelayMailboxBinding>.Success(existing);
        }

        var binding = new RelayMailboxBinding(
            ownerIdentityId,
            routingToken ?? BlindedRoutingToken.New(),
            drainToken ?? DeliveryToken.GenerateRandom(),
            timeProvider.UtcNow);

        _bindings.Add(binding);
        return DomainResult<RelayMailboxBinding>.Success(binding);
    }

    /// <summary>
    /// Removes the identity's mailbox binding. No-op if the identity is not linked.
    /// </summary>
    public DomainResult UnlinkIdentity(PublicIdentityId ownerIdentityId)
    {
        _bindings.RemoveAll(b => b.OwnerIdentityId == ownerIdentityId);
        return DomainResult.Success();
    }

    /// <summary>
    /// Generates a fresh drain token for the linked identity's mailbox, retaining the
    /// prior token in PreviousDrainToken for RegisterMailboxRequest.existing_drain_token.
    /// </summary>
    public DomainResult<RelayMailboxBinding> RotateDrainToken(PublicIdentityId ownerIdentityId)
    {
        var index = _bindings.FindIndex(b => b.OwnerIdentityId == ownerIdentityId);
        if (index < 0)
        {
            return DomainResult<RelayMailboxBinding>.Failure(
                new DomainError("MAILBOX_BINDING_NOT_FOUND", "Identity is not linked to this relay."));
        }

        var existing = _bindings[index];
        var rotated = existing with
        {
            DrainToken = DeliveryToken.GenerateRandom(),
            PreviousDrainToken = existing.DrainToken
        };

        _bindings[index] = rotated;
        return DomainResult<RelayMailboxBinding>.Success(rotated);
    }

    /// <summary>
    /// Updates the relay's network endpoint only. The certificate pin is unchanged;
    /// use <see cref="SetCertificatePin"/> or <see cref="ClearCertificatePin"/> to modify it.
    /// </summary>
    public DomainResult UpdateEndpoint(RelayEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Endpoint = endpoint;
        return DomainResult.Success();
    }

    /// <summary>
    /// Sets or replaces the certificate pin.
    /// </summary>
    public DomainResult SetCertificatePin(CertificatePin pinnedCertificate)
    {
        ArgumentNullException.ThrowIfNull(pinnedCertificate);

        PinnedCertificate = pinnedCertificate;
        return DomainResult.Success();
    }

    /// <summary>
    /// Removes the certificate pin. No-op if no pin is set.
    /// </summary>
    public DomainResult ClearCertificatePin()
    {
        PinnedCertificate = null;
        return DomainResult.Success();
    }
}
