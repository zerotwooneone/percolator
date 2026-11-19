using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.Delivery;

[TestFixture]
public sealed class PreKeyMaintenanceServiceTests
{
    private static readonly PublicIdentityId OwnerId = PublicIdentityId.New();
    private static readonly PublicIdentityId RelayId = PublicIdentityId.New();
    private static readonly RelayEndpoint Endpoint = RelayEndpoint.FromHostAndPort("localhost", 5001);

    private InMemoryPrivatePreKeyStore _preKeyStore = null!;
    private InMemoryLocalIdentityKeyStore _identityStore = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private FakeProvisioner _provisioner = null!;
    private FakeDirectory _directory = null!;
    private TestDateTimeProvider _time = null!;
    private PreKeyMaintenanceService _service = null!;
    private MailboxDrainTarget _target = null!;

    [SetUp]
    public async Task SetUp()
    {
        _preKeyStore = new InMemoryPrivatePreKeyStore();
        _identityStore = new InMemoryLocalIdentityKeyStore();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _provisioner = new FakeProvisioner();
        _directory = new FakeDirectory();
        _time = new TestDateTimeProvider();
        _service = new PreKeyMaintenanceService(
            _preKeyStore,
            _identityStore,
            _cryptoEngine,
            _provisioner,
            _directory,
            _time,
            NullLogger<PreKeyMaintenanceService>.Instance);
        _target = new MailboxDrainTarget(OwnerId, RelayId, Endpoint);

        // Seed one SPK generation (id 1, current) and the owner's identity key so
        // rotation can sign.
        var (idPriv, idPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        _identityStore.RegisterKey(OwnerId, idPriv, IdentityKey.FromSpan(idPub.Span));
        var (spkPriv, spkPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var sig = DeviceLinkProof.FromSpan(_cryptoEngine.SignEd25519(idPriv.Span, spkPub.Span));
        await _preKeyStore.StoreSignedPreKeyAsync(OwnerId, DeviceId.Primary, spkPriv, spkPub, sig);
    }

    [Test]
    public async Task EvaluateAsync_ThrottlesUnforcedChecks_WithinStatusInterval()
    {
        await _service.EvaluateAsync(_target, forced: false);
        await _service.EvaluateAsync(_target, forced: false);
        await _service.EvaluateAsync(_target, forced: false);

        _provisioner.StatusCalls.Should().Be(1);
    }

    [Test]
    public async Task EvaluateAsync_Forced_BypassesThrottle()
    {
        await _service.EvaluateAsync(_target, forced: false);
        await _service.EvaluateAsync(_target, forced: true);
        await _service.EvaluateAsync(_target, forced: true);

        _provisioner.StatusCalls.Should().Be(3);
    }

    [Test]
    public async Task EvaluateAsync_UnforcedAfterInterval_ChecksAgain()
    {
        await _service.EvaluateAsync(_target, forced: false);
        _time.UtcNow += PreKeyMaintenanceService.StatusCheckInterval + TimeSpan.FromMinutes(1);
        await _service.EvaluateAsync(_target, forced: false);

        _provisioner.StatusCalls.Should().Be(2);
    }

    [Test]
    public async Task EvaluateAsync_BundleNotFound_PublishesFullPool()
    {
        _provisioner.StatusOverride = DomainResult<RelayPreKeyStatus>.Success(
            new RelayPreKeyStatus(false, 0, SignedPreKeyId.None));

        await _service.EvaluateAsync(_target, forced: true);

        _provisioner.Publications.Should().HaveCount(1);
        _provisioner.Publications[0].OpkCount.Should().Be(PreKeyMaintenanceService.TargetPoolSize);
    }

    [Test]
    public async Task EvaluateAsync_LowOneTimePreKeyCount_TopsUpToTarget()
    {
        _provisioner.StatusOverride = DomainResult<RelayPreKeyStatus>.Success(
            new RelayPreKeyStatus(true, 30u, SignedPreKeyId.First));

        await _service.EvaluateAsync(_target, forced: true);

        _provisioner.Publications.Should().HaveCount(1);
        _provisioner.Publications[0].OpkCount
            .Should().Be(PreKeyMaintenanceService.TargetPoolSize - 30);
    }

    [Test]
    public async Task EvaluateAsync_HealthyBundle_NoPublish()
    {
        _provisioner.StatusOverride = DomainResult<RelayPreKeyStatus>.Success(
            new RelayPreKeyStatus(true, 60u, SignedPreKeyId.First));

        await _service.EvaluateAsync(_target, forced: true);

        _provisioner.Publications.Should().BeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_StaleSignedPreKeyId_RepublishesWithoutNewOpks()
    {
        // Relay still advertises a superseded SPK generation — republish carries
        // the current key but no OPK top-up while the hosted pool is healthy.
        _provisioner.StatusOverride = DomainResult<RelayPreKeyStatus>.Success(
            new RelayPreKeyStatus(true, 60u, new SignedPreKeyId(99)));

        await _service.EvaluateAsync(_target, forced: true);

        _provisioner.Publications.Should().HaveCount(1);
        _provisioner.Publications[0].OpkCount.Should().Be(0);
    }

    [Test]
    public async Task EvaluateAsync_SpkOlderThanRotationAge_RotatesAndRetainsPriorGeneration()
    {
        _time.UtcNow += PreKeyMaintenanceService.SignedPreKeyRotationAge + TimeSpan.FromMinutes(1);

        await _service.EvaluateAsync(_target, forced: true);

        var current = await _preKeyStore.GetSignedPreKeyAsync(OwnerId, DeviceId.Primary);
        current.Should().NotBeNull();
        current!.KeyId.Should().Be(new SignedPreKeyId(2));

        // The superseded generation stays decryptable for in-flight handshakes.
        var oldPrivate = await _preKeyStore.GetSignedPreKeyPrivateAsync(
            OwnerId, DeviceId.Primary, new SignedPreKeyId(1));
        oldPrivate.Should().NotBeNull();
    }

    [Test]
    public async Task EvaluateAsync_TokenResolutionFails_SkipsStatusCheck()
    {
        _directory.TokensOverride = DomainResult<MailboxTokenTriad>.Failure(
            new DomainError("TOKEN_MISSING", "no tokens"));

        await _service.EvaluateAsync(_target, forced: true);

        _provisioner.StatusCalls.Should().Be(0);
        _provisioner.Publications.Should().BeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_StatusCheckFails_SkipsPublish_AndRetriesNextTime()
    {
        _provisioner.StatusOverride = DomainResult<RelayPreKeyStatus>.Failure(
            new DomainError("STATUS_FAILED", "rpc failed"));

        await _service.EvaluateAsync(_target, forced: false);
        _provisioner.StatusCalls.Should().Be(1);
        _provisioner.Publications.Should().BeEmpty();

        // Failure leaves the throttle unrecorded — the next unforced eval retries.
        await _service.EvaluateAsync(_target, forced: false);
        _provisioner.StatusCalls.Should().Be(2);
    }

    [Test]
    public void NotifyOneTimePreKeyConsumed_TriggersForcedEvaluation()
    {
        _service.NotifyOneTimePreKeyConsumed(_target);

        SpinWait.SpinUntil(() => _provisioner.StatusCalls > 0, TimeSpan.FromSeconds(5))
            .Should().BeTrue();
    }

    private sealed class FakeProvisioner : IRelayMailboxProvisioner
    {
        public int StatusCalls { get; private set; }
        public List<(PublicIdentityId Owner, DeviceId Device, uint OpkCount)> Publications { get; } = new();
        public DomainResult<RelayPreKeyStatus>? StatusOverride { get; set; }
        public DomainResult? PublishOverride { get; set; }

        public Task<DomainResult> ProvisionMailboxAsync(
            PublicIdentityId ownerIdentityId, PublicIdentityId relayIdentityId,
            RelayEndpoint endpoint, MailboxTokenTriad tokens, CancellationToken ct = default)
            => Task.FromResult(DomainResult.Success());

        public Task<DomainResult> PublishPreKeyBundleAsync(
            PublicIdentityId ownerIdentityId, DeviceId deviceId, RelayEndpoint endpoint,
            MailboxTokenTriad tokens, uint newOneTimePreKeyCount, CancellationToken ct = default)
        {
            Publications.Add((ownerIdentityId, deviceId, newOneTimePreKeyCount));
            return Task.FromResult(PublishOverride ?? DomainResult.Success());
        }

        public Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
            DeviceId deviceId, RelayEndpoint endpoint, MailboxTokenTriad tokens, CancellationToken ct = default)
        {
            StatusCalls++;
            return Task.FromResult(StatusOverride
                ?? DomainResult<RelayPreKeyStatus>.Success(new RelayPreKeyStatus(true, 60, SignedPreKeyId.First)));
        }
    }

    private sealed class FakeDirectory : IRelayDirectory
    {
        public int TokenCalls { get; private set; }
        public DomainResult<MailboxTokenTriad>? TokensOverride { get; set; }

        public Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(
            PublicIdentityId ownerIdentityId, PublicIdentityId relayIdentityId, CancellationToken ct = default)
        {
            TokenCalls++;
            return Task.FromResult(TokensOverride ?? DomainResult<MailboxTokenTriad>.Success(new MailboxTokenTriad(
                BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom())));
        }

        public Task<IReadOnlyList<RelayDirectoryEntry>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DomainResult> EnsureMailboxProvisionedAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DomainResult<RelayDirectoryEntry>> CreateLocalRelayHostAsync(
            PublicIdentityId relayIdentityId, string displayName, RelayEndpoint endpoint,
            IEnumerable<PublicIdentityId> initialLinkedOwnerIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DomainResult<RelayDirectoryEntry>> RegisterRemoteRelayAsync(
            PublicIdentityId relayIdentityId, RelayEndpoint endpoint, CertificatePin? pinnedCertificate = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DomainResult<RelayMailboxBinding>> LinkIdentityAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId,
            BlindedRoutingToken? routingToken = null, DeliveryToken? drainToken = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DomainResult> UnlinkIdentityAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DomainResult> SetMailboxBindingStatusAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, RelayLinkStatus status, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DomainResult> ProvisionLinkedMailboxesAsync(
            PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

}
