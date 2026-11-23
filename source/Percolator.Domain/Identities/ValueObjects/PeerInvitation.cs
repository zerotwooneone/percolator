namespace Percolator.Domain.Identities.ValueObjects;

using System;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.Ports;

public sealed record PeerInvitation
{
    public PublicIdentityId InviterIdentityId { get; }
    public IdentityKey InviterIdentityKey { get; }
    public RelayEndpoint RelayEndpoint { get; }
    public BlindedRoutingToken RoutingToken { get; }

    private PeerInvitation(
        PublicIdentityId inviterIdentityId,
        IdentityKey inviterIdentityKey,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken routingToken)
    {
        InviterIdentityId = inviterIdentityId;
        InviterIdentityKey = inviterIdentityKey;
        RelayEndpoint = relayEndpoint;
        RoutingToken = routingToken;
    }

    public static DomainResult<PeerInvitation> Create(
        PublicIdentityId inviterIdentityId,
        IdentityKey inviterIdentityKey,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken routingToken)
    {
        if (!inviterIdentityId.IsValid)
            return DomainResult<PeerInvitation>.Failure(new DomainError("INVALID_IDENTITY_ID", "InviterIdentityId cannot be empty."));
        if (inviterIdentityKey is null)
            return DomainResult<PeerInvitation>.Failure(new DomainError("MISSING_IDENTITY_KEY", "InviterIdentityKey is required to pin peer identity."));
        if (relayEndpoint is null)
            return DomainResult<PeerInvitation>.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "RelayEndpoint is required."));
        if (!routingToken.IsValid)
            return DomainResult<PeerInvitation>.Failure(new DomainError("INVALID_ROUTING_TOKEN", "RoutingToken cannot be empty."));

        return DomainResult<PeerInvitation>.Success(
            new PeerInvitation(inviterIdentityId, inviterIdentityKey, relayEndpoint, routingToken));
    }

    public DomainResult VerifyFetchedBundle(PreKeyBundle fetchedBundle, ICryptoEngine cryptoEngine)
    {
        ArgumentNullException.ThrowIfNull(cryptoEngine);
        if (fetchedBundle is null)
            return DomainResult.Failure(new DomainError("MISSING_PREKEY_BUNDLE", "Fetched PreKeyBundle cannot be null."));
        if (!fetchedBundle.DeviceId.IsValid)
            return DomainResult.Failure(new DomainError("INVALID_DEVICE_ID", "DeviceId in fetched PreKeyBundle must be valid."));
        if (fetchedBundle.IdentityKey is null || fetchedBundle.SignedPreKey is null || fetchedBundle.SignedPreKeySignature is null)
            return DomainResult.Failure(new DomainError("NULL_KEY_MATERIAL", "IdentityKey, SignedPreKey, and signature cannot be null."));

        if (!CryptographicOperations.FixedTimeEquals(InviterIdentityKey.Span, fetchedBundle.IdentityKey.Span))
            return DomainResult.Failure(new DomainError("IDENTITY_KEY_MISMATCH", "Fetched PreKeyBundle IdentityKey does not match pinned invitation IdentityKey."));

        var valid = cryptoEngine.VerifyEd25519Signature(
            InviterIdentityKey,
            fetchedBundle.SignedPreKey.Span,
            fetchedBundle.SignedPreKeySignature.Span);

        return valid
            ? DomainResult.Success()
            : DomainResult.Failure(new DomainError("INVALID_PREKEY_SIGNATURE", "Signed pre-key signature verification failed against pinned identity key."));
    }
}
