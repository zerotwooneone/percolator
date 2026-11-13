using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Delivery;

/// <summary>
/// Driving service behind the Desktop "Exchanges" surface: manages <see cref="RelayRegistration"/>
/// aggregates (local relay hosts and remote relays) and coordinates mailbox provisioning through
/// <see cref="IRelayMailboxProvisioner"/>.
/// </summary>
public sealed class RelayDirectoryService : IRelayDirectory
{
    private readonly IRelayRegistrationRepository _relayRepository;
    private readonly IIdentityProfileRepository _profileRepository;
    private readonly ILocalIdentityKeyStore _identityKeyStore;
    private readonly IPrivatePreKeyStore _preKeyStore;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IRelayMailboxProvisioner _provisioner;

    public RelayDirectoryService(
        IRelayRegistrationRepository relayRepository,
        IIdentityProfileRepository profileRepository,
        ILocalIdentityKeyStore identityKeyStore,
        IPrivatePreKeyStore preKeyStore,
        ICryptoEngine cryptoEngine,
        IDateTimeProvider timeProvider,
        IRelayMailboxProvisioner provisioner)
    {
        _relayRepository = relayRepository;
        _profileRepository = profileRepository;
        _identityKeyStore = identityKeyStore;
        _preKeyStore = preKeyStore;
        _cryptoEngine = cryptoEngine;
        _timeProvider = timeProvider;
        _provisioner = provisioner;
    }

    public async Task<IReadOnlyList<RelayDirectoryEntry>> ListAsync(CancellationToken ct = default)
    {
        var registrations = await _relayRepository.GetAllAsync(ct).ConfigureAwait(false);
        var profiles = await _profileRepository.GetAllAsync(ct).ConfigureAwait(false);
        var relayProfiles = profiles
            .Where(p => p.Role == IdentityRole.RelayHost)
            .ToDictionary(p => p.Id);

        var entries = new List<RelayDirectoryEntry>(registrations.Count);
        foreach (var registration in registrations)
        {
            var isLocal = relayProfiles.TryGetValue(registration.Id, out var profile);
            entries.Add(new RelayDirectoryEntry(
                registration.Id,
                isLocal ? profile!.DisplayName : registration.Endpoint.Value,
                registration.Endpoint,
                isLocal,
                registration.Bindings.Select(b => b.OwnerIdentityId).ToList()));
        }

        return entries;
    }

    public async Task<DomainResult<RelayDirectoryEntry>> CreateLocalRelayHostAsync(
        PublicIdentityId relayIdentityId,
        string displayName,
        RelayEndpoint endpoint,
        IEnumerable<PublicIdentityId> initialLinkedOwnerIds,
        CancellationToken ct = default)
    {
        var profileResult = IdentityProfile.Create(relayIdentityId, displayName, IdentityRole.RelayHost, _timeProvider);
        if (profileResult.IsFailure)
        {
            return DomainResult<RelayDirectoryEntry>.Failure(profileResult.Error);
        }

        var (priv, pub) = _cryptoEngine.GenerateEphemeralKeyPair();
        using var profileKey = ProfileKey.Generate();
        try
        {
            await _identityKeyStore
                .SaveIdentityKeysAsync(relayIdentityId, priv, IdentityKey.FromSpan(pub.Span), profileKey, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            priv.Zeroize();
        }

        await _profileRepository.SaveAsync(profileResult.Value!, ct).ConfigureAwait(false);

        var registrationResult = RelayRegistration.Create(relayIdentityId, endpoint, null, _timeProvider);
        if (registrationResult.IsFailure)
        {
            return DomainResult<RelayDirectoryEntry>.Failure(registrationResult.Error);
        }

        var registration = registrationResult.Value!;
        var linkedOwners = new List<PublicIdentityId>();
        foreach (var ownerId in initialLinkedOwnerIds)
        {
            var linkResult = registration.LinkIdentity(ownerId, _timeProvider);
            if (linkResult.IsFailure)
            {
                return DomainResult<RelayDirectoryEntry>.Failure(linkResult.Error);
            }

            linkedOwners.Add(ownerId);
        }

        await _relayRepository.SaveAsync(registration, ct).ConfigureAwait(false);

        return DomainResult<RelayDirectoryEntry>.Success(new RelayDirectoryEntry(
            relayIdentityId,
            profileResult.Value!.DisplayName,
            endpoint,
            IsLocalRelayHost: true,
            linkedOwners));
    }

    public async Task<DomainResult<RelayDirectoryEntry>> RegisterRemoteRelayAsync(
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        CertificatePin? pinnedCertificate = null,
        CancellationToken ct = default)
    {
        var registrationResult = RelayRegistration.Create(relayIdentityId, endpoint, pinnedCertificate, _timeProvider);
        if (registrationResult.IsFailure)
        {
            return DomainResult<RelayDirectoryEntry>.Failure(registrationResult.Error);
        }

        var registration = registrationResult.Value!;
        await _relayRepository.SaveAsync(registration, ct).ConfigureAwait(false);

        return DomainResult<RelayDirectoryEntry>.Success(new RelayDirectoryEntry(
            relayIdentityId,
            endpoint.Value,
            endpoint,
            IsLocalRelayHost: false,
            new List<PublicIdentityId>()));
    }

    public async Task<DomainResult<RelayMailboxBinding>> LinkIdentityAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId ownerIdentityId,
        BlindedRoutingToken? routingToken = null,
        DeliveryToken? drainToken = null,
        CancellationToken ct = default)
    {
        var registration = await _relayRepository.GetByIdAsync(relayIdentityId, ct).ConfigureAwait(false);
        if (registration is null)
        {
            return DomainResult<RelayMailboxBinding>.Failure(
                new DomainError("RELAY_NOT_FOUND", "No relay registration exists for the given relay identity."));
        }

        var linkResult = registration.LinkIdentity(ownerIdentityId, _timeProvider, routingToken, drainToken);
        if (linkResult.IsFailure)
        {
            return linkResult;
        }

        await _relayRepository.SaveAsync(registration, ct).ConfigureAwait(false);
        return linkResult;
    }

    public async Task<DomainResult> UnlinkIdentityAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId ownerIdentityId,
        CancellationToken ct = default)
    {
        var registration = await _relayRepository.GetByIdAsync(relayIdentityId, ct).ConfigureAwait(false);
        if (registration is null)
        {
            return DomainResult.Failure(
                new DomainError("RELAY_NOT_FOUND", "No relay registration exists for the given relay identity."));
        }

        var unlinkResult = registration.UnlinkIdentity(ownerIdentityId);
        if (unlinkResult.IsFailure)
        {
            return unlinkResult;
        }

        await _relayRepository.SaveAsync(registration, ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    public async Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default)
    {
        var publicKey = await _identityKeyStore.GetIdentityPublicKeyAsync(ownerIdentityId, ct).ConfigureAwait(false);
        if (publicKey is null)
        {
            return DomainResult<MailboxTokenTriad>.Failure(
                new DomainError("IDENTITY_KEYS_MISSING", "No identity keys stored for the given owner identity."));
        }

        var profileKey = await _identityKeyStore.GetProfileKeyAsync(ownerIdentityId, ct).ConfigureAwait(false);
        if (profileKey is null)
        {
            // Pre-Phase-2 vault row: mint a profile key explicitly. Rotation is safe here because
            // no DeliveryToken was ever derivable without it.
            profileKey = await _identityKeyStore.RotateProfileKeyAsync(ownerIdentityId, ct).ConfigureAwait(false);
        }

        var registration = await _relayRepository.GetByIdAsync(relayIdentityId, ct).ConfigureAwait(false);
        var binding = registration?.Bindings.FirstOrDefault(b => b.OwnerIdentityId == ownerIdentityId);
        if (binding is null)
        {
            return DomainResult<MailboxTokenTriad>.Failure(
                new DomainError("MAILBOX_BINDING_NOT_FOUND", "Owner identity is not linked to the given relay."));
        }

        var delivery = DeliveryToken.DeriveFromProfileKey(publicKey, profileKey, _cryptoEngine);
        return DomainResult<MailboxTokenTriad>.Success(new MailboxTokenTriad(
            binding.RoutingToken,
            delivery,
            binding.DrainToken,
            binding.PreviousDrainToken));
    }

    public async Task<DomainResult> ProvisionLinkedMailboxesAsync(
        PublicIdentityId ownerIdentityId,
        CancellationToken ct = default)
    {
        var registrations = await _relayRepository.GetByOwnerIdentityAsync(ownerIdentityId, ct).ConfigureAwait(false);
        foreach (var registration in registrations)
        {
            var tokensResult = await GetMailboxTokensAsync(ownerIdentityId, registration.Id, ct).ConfigureAwait(false);
            if (tokensResult.IsFailure)
            {
                return DomainResult.Failure(tokensResult.Error);
            }

            var tokens = tokensResult.Value!;

            var provisionResult = await _provisioner
                .ProvisionMailboxAsync(ownerIdentityId, registration.Id, registration.Endpoint, tokens, ct)
                .ConfigureAwait(false);
            if (provisionResult.IsFailure)
            {
                return DomainResult.Failure(provisionResult.Error);
            }

            var publishResult = await _provisioner
                .PublishPreKeyBundleAsync(ownerIdentityId, DeviceId.Primary, registration.Endpoint, tokens, 25, ct)
                .ConfigureAwait(false);
            if (publishResult.IsFailure)
            {
                return DomainResult.Failure(publishResult.Error);
            }
        }

        return DomainResult.Success();
    }
}
