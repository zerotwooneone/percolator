using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Tests.Delivery;

[TestFixture]
public sealed class RelayDirectoryServiceTests
{
    private sealed class InMemoryIdentityProfileRepository : IIdentityProfileRepository
    {
        private readonly Dictionary<PublicIdentityId, IdentityProfile> _profiles = new();

        public Task<IdentityProfile?> GetByIdAsync(PublicIdentityId id, CancellationToken cancellationToken = default)
        {
            _profiles.TryGetValue(id, out var profile);
            return Task.FromResult(profile);
        }

        public Task<IReadOnlyList<IdentityProfile>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IdentityProfile>>(_profiles.Values.ToList());

        public Task SaveAsync(IdentityProfile profile, CancellationToken cancellationToken = default)
        {
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(PublicIdentityId id, CancellationToken cancellationToken = default)
        {
            _profiles.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRelayRegistrationRepository : IRelayRegistrationRepository
    {
        private readonly Dictionary<PublicIdentityId, RelayRegistration> _registrations = new();

        public Task<RelayRegistration?> GetByIdAsync(PublicIdentityId relayIdentityId, CancellationToken ct = default)
        {
            _registrations.TryGetValue(relayIdentityId, out var registration);
            return Task.FromResult(registration);
        }

        public Task<IReadOnlyList<RelayRegistration>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RelayRegistration>>(_registrations.Values.ToList());

        public Task<IReadOnlyList<RelayRegistration>> GetByOwnerIdentityAsync(PublicIdentityId ownerIdentityId, CancellationToken ct = default)
        {
            var matches = _registrations.Values
                .Where(r => r.Bindings.Any(b => b.OwnerIdentityId == ownerIdentityId))
                .ToList();
            return Task.FromResult<IReadOnlyList<RelayRegistration>>(matches);
        }

        public Task SaveAsync(RelayRegistration registration, CancellationToken ct = default)
        {
            _registrations[registration.Id] = registration;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(PublicIdentityId relayIdentityId, CancellationToken ct = default)
        {
            _registrations.Remove(relayIdentityId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRelayMailboxProvisioner : IRelayMailboxProvisioner
    {
        public List<(PublicIdentityId OwnerId, PublicIdentityId RelayId, RelayEndpoint Endpoint, MailboxTokenTriad Tokens)> Provisions { get; } = new();
        public List<(PublicIdentityId OwnerId, DeviceId DeviceId, RelayEndpoint Endpoint, MailboxTokenTriad Tokens, int OpkCount)> Publications { get; } = new();
        public DomainResult? ProvisionResult { get; set; }
        public DomainResult? PublishResult { get; set; }

        public Task<DomainResult> ProvisionMailboxAsync(
            PublicIdentityId ownerIdentityId,
            PublicIdentityId relayIdentityId,
            RelayEndpoint endpoint,
            MailboxTokenTriad tokens,
            CancellationToken ct = default)
        {
            Provisions.Add((ownerIdentityId, relayIdentityId, endpoint, tokens));
            return Task.FromResult(ProvisionResult ?? DomainResult.Success());
        }

        public Task<DomainResult> PublishPreKeyBundleAsync(
            PublicIdentityId ownerIdentityId,
            DeviceId deviceId,
            RelayEndpoint endpoint,
            MailboxTokenTriad tokens,
            int newOneTimePreKeyCount = 25,
            CancellationToken ct = default)
        {
            Publications.Add((ownerIdentityId, deviceId, endpoint, tokens, newOneTimePreKeyCount));
            return Task.FromResult(PublishResult ?? DomainResult.Success());
        }

        public Task<DomainResult<int>> DrainMailboxOnceAsync(
            PublicIdentityId ownerIdentityId,
            RelayEndpoint endpoint,
            MailboxTokenTriad tokens,
            CancellationToken ct = default)
            => Task.FromResult(DomainResult<int>.Success(0));

        public Task StopMailboxStreamAsync(
            PublicIdentityId ownerIdentityId,
            PublicIdentityId relayIdentityId,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private InMemoryRelayRegistrationRepository _relayRepository = null!;
    private InMemoryIdentityProfileRepository _profileRepository = null!;
    private InMemoryLocalIdentityKeyStore _identityKeyStore = null!;
    private InMemoryPrivatePreKeyStore _preKeyStore = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private RecordingRelayMailboxProvisioner _provisioner = null!;
    private RelayDirectoryService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _relayRepository = new InMemoryRelayRegistrationRepository();
        _profileRepository = new InMemoryIdentityProfileRepository();
        _identityKeyStore = new InMemoryLocalIdentityKeyStore();
        _preKeyStore = new InMemoryPrivatePreKeyStore();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _timeProvider = new TestDateTimeProvider();
        _provisioner = new RecordingRelayMailboxProvisioner();
        _service = new RelayDirectoryService(
            _relayRepository,
            _profileRepository,
            _identityKeyStore,
            _preKeyStore,
            _cryptoEngine,
            _timeProvider,
            _provisioner);
    }

    private static RelayEndpoint NewEndpoint(int port) => RelayEndpoint.FromHostAndPort("127.0.0.1", port);

    private PublicIdentityId RegisterIdentityWithKeys()
    {
        var ownerId = PublicIdentityId.New();
        var (priv, pub) = _cryptoEngine.GenerateEphemeralKeyPair();
        _identityKeyStore.RegisterKey(ownerId, priv, IdentityKey.FromSpan(pub.Span), ProfileKey.Generate());
        return ownerId;
    }

    [Test]
    public async Task CreateLocalRelayHostAsync_CreatesProfileKeysAndRegistration()
    {
        var relayId = PublicIdentityId.New();
        var owner1 = PublicIdentityId.New();
        var owner2 = PublicIdentityId.New();

        var result = await _service.CreateLocalRelayHostAsync(
            relayId, "My Relay", NewEndpoint(5100), new[] { owner1, owner2 });

        result.IsSuccess.Should().BeTrue();
        var entry = result.Value!;
        entry.IsLocalRelayHost.Should().BeTrue();
        entry.DisplayName.Should().Be("My Relay");
        entry.LinkedOwnerIdentityIds.Should().BeEquivalentTo(new[] { owner1, owner2 });

        var profile = await _profileRepository.GetByIdAsync(relayId);
        profile.Should().NotBeNull();
        profile!.Role.Should().Be(IdentityRole.RelayHost);
        profile.DisplayName.Should().Be("My Relay");

        (await _identityKeyStore.GetIdentityPublicKeyAsync(relayId)).Should().NotBeNull();
        (await _identityKeyStore.GetProfileKeyAsync(relayId)).Should().NotBeNull();

        var registration = await _relayRepository.GetByIdAsync(relayId);
        registration.Should().NotBeNull();
        registration!.Endpoint.Value.Should().Be("127.0.0.1:5100");
        registration.Bindings.Select(b => b.OwnerIdentityId).Should().BeEquivalentTo(new[] { owner1, owner2 });
    }

    [Test]
    public async Task ListAsync_DistinguishesLocalRelayHostsFromRemoteRelays()
    {
        var localId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var owner = PublicIdentityId.New();

        await _service.CreateLocalRelayHostAsync(localId, "Local Exchange", NewEndpoint(5100), new[] { owner });
        await _service.RegisterRemoteRelayAsync(remoteId, NewEndpoint(5299));

        var entries = await _service.ListAsync();

        var local = entries.Single(e => e.RelayIdentityId == localId);
        local.IsLocalRelayHost.Should().BeTrue();
        local.DisplayName.Should().Be("Local Exchange");
        local.LinkedOwnerIdentityIds.Should().ContainSingle().Which.Should().Be(owner);

        var remote = entries.Single(e => e.RelayIdentityId == remoteId);
        remote.IsLocalRelayHost.Should().BeFalse();
        remote.DisplayName.Should().Be(remote.Endpoint.Value);
    }

    [Test]
    public async Task GetMailboxTokensAsync_ReturnsDerivedDeliveryTokenAndPersistedCsprngTokens()
    {
        var ownerId = RegisterIdentityWithKeys();
        var relayId = PublicIdentityId.New();

        await _service.RegisterRemoteRelayAsync(relayId, NewEndpoint(5200));
        var link = await _service.LinkIdentityAsync(relayId, ownerId);
        link.IsSuccess.Should().BeTrue();
        var binding = link.Value!;

        var result = await _service.GetMailboxTokensAsync(ownerId, relayId);

        result.IsSuccess.Should().BeTrue();
        var triad = result.Value!;
        triad.RoutingToken.ToByteArray().Should().Equal(binding.RoutingToken.ToByteArray());
        triad.DrainToken.Span.ToArray().Should().Equal(binding.DrainToken.Span.ToArray());
        triad.PreviousDrainToken.Should().BeNull();

        var publicKey = (await _identityKeyStore.GetIdentityPublicKeyAsync(ownerId))!;
        var profileKey = (await _identityKeyStore.GetProfileKeyAsync(ownerId))!;
        var expectedDelivery = DeliveryToken.DeriveFromProfileKey(publicKey, profileKey, _cryptoEngine);
        triad.DeliveryToken.Span.ToArray().Should().Equal(expectedDelivery.Span.ToArray());
    }

    [Test]
    public async Task GetMailboxTokensAsync_NoIdentityKeys_FailsWithIdentityKeysMissing()
    {
        var ownerId = PublicIdentityId.New();
        var relayId = PublicIdentityId.New();
        await _service.RegisterRemoteRelayAsync(relayId, NewEndpoint(5200));
        await _service.LinkIdentityAsync(relayId, ownerId);

        var result = await _service.GetMailboxTokensAsync(ownerId, relayId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IDENTITY_KEYS_MISSING");
    }

    [Test]
    public async Task GetMailboxTokensAsync_OwnerNotLinked_FailsWithBindingNotFound()
    {
        var ownerId = RegisterIdentityWithKeys();
        var relayId = PublicIdentityId.New();
        await _service.RegisterRemoteRelayAsync(relayId, NewEndpoint(5200));

        var result = await _service.GetMailboxTokensAsync(ownerId, relayId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MAILBOX_BINDING_NOT_FOUND");
    }

    [Test]
    public async Task LinkAndUnlinkIdentityAsync_PersistWithoutInvokingProvisioner()
    {
        var ownerId = PublicIdentityId.New();
        var relayId = PublicIdentityId.New();
        await _service.RegisterRemoteRelayAsync(relayId, NewEndpoint(5200));

        var link = await _service.LinkIdentityAsync(relayId, ownerId);
        link.IsSuccess.Should().BeTrue();
        (await _relayRepository.GetByIdAsync(relayId))!.Bindings.Should().HaveCount(1);

        var unlink = await _service.UnlinkIdentityAsync(relayId, ownerId);
        unlink.IsSuccess.Should().BeTrue();
        (await _relayRepository.GetByIdAsync(relayId))!.Bindings.Should().BeEmpty();

        _provisioner.Provisions.Should().BeEmpty();
        _provisioner.Publications.Should().BeEmpty();
    }

    [Test]
    public async Task LinkIdentityAsync_UnknownRelay_FailsWithRelayNotFound()
    {
        var result = await _service.LinkIdentityAsync(PublicIdentityId.New(), PublicIdentityId.New());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RELAY_NOT_FOUND");
    }

    [Test]
    public async Task ProvisionLinkedMailboxesAsync_ProvisionsAndPublishesPerLinkedRelay()
    {
        var ownerId = RegisterIdentityWithKeys();
        var relay1 = PublicIdentityId.New();
        var relay2 = PublicIdentityId.New();
        await _service.RegisterRemoteRelayAsync(relay1, NewEndpoint(5201));
        await _service.RegisterRemoteRelayAsync(relay2, NewEndpoint(5202));
        await _service.LinkIdentityAsync(relay1, ownerId);
        await _service.LinkIdentityAsync(relay2, ownerId);

        var result = await _service.ProvisionLinkedMailboxesAsync(ownerId);

        result.IsSuccess.Should().BeTrue();
        _provisioner.Provisions.Select(p => p.RelayId).Should().BeEquivalentTo(new[] { relay1, relay2 });
        _provisioner.Publications.Should().HaveCount(2);
        _provisioner.Publications.Should().OnlyContain(p => p.DeviceId == DeviceId.Primary && p.OpkCount == 25);
        _provisioner.Publications.Select(p => p.Tokens.RoutingToken)
            .Should().BeEquivalentTo(_provisioner.Provisions.Select(p => p.Tokens.RoutingToken));
    }

    [Test]
    public async Task ProvisionLinkedMailboxesAsync_NullProvisioner_ReportsProvisionerUnavailable()
    {
        var ownerId = RegisterIdentityWithKeys();
        var relayId = PublicIdentityId.New();
        await _service.RegisterRemoteRelayAsync(relayId, NewEndpoint(5200));
        await _service.LinkIdentityAsync(relayId, ownerId);

        var service = new RelayDirectoryService(
            _relayRepository,
            _profileRepository,
            _identityKeyStore,
            _preKeyStore,
            _cryptoEngine,
            _timeProvider,
            new NullRelayMailboxProvisioner());

        var result = await service.ProvisionLinkedMailboxesAsync(ownerId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PROVISIONER_UNAVAILABLE");
    }
}
