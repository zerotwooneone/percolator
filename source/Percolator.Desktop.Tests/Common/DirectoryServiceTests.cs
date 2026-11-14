// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Transport;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Tests.Common;

/// <summary>
/// CTX-03 facade unit tests: the directories ctor-inject their session ports, so tests
/// supply mocked dependencies directly — no <c>Mock&lt;IAuthenticatedSession&gt;</c>
/// or scope plumbing.
/// </summary>
[TestFixture]
public sealed class DirectoryServiceTests
{
    private static readonly IDateTimeProvider Time = SystemDateTimeProvider.Instance;

    private static IdentityProfile Profile(
        string name,
        IdentityRole role = IdentityRole.UserPersona,
        PublicIdentityId? id = null) =>
        IdentityProfile.Rehydrate(
            id ?? new PublicIdentityId(Guid.NewGuid()),
            name,
            role,
            IdentityState.Active,
            DateTimeOffset.UtcNow.AddDays(-1));

    // ---- PersonaDirectoryService ----

    private static PersonaDirectoryService PersonaService(
        out Mock<IIdentityProfileRepository> profiles,
        out Mock<ILocalIdentityKeyStore> keys,
        out Mock<IPrivatePreKeyStore> preKeys,
        out Mock<IDesktopSettingsRepository> settings)
    {
        profiles = new Mock<IIdentityProfileRepository>(MockBehavior.Strict);
        keys = new Mock<ILocalIdentityKeyStore>(MockBehavior.Strict);
        preKeys = new Mock<IPrivatePreKeyStore>(MockBehavior.Strict);
        var crypto = new Mock<ICryptoEngine>(MockBehavior.Strict);
        settings = new Mock<IDesktopSettingsRepository>(MockBehavior.Strict);
        return new PersonaDirectoryService(
            profiles.Object, keys.Object, preKeys.Object, crypto.Object, Time, settings.Object);
    }

    [Test]
    public async Task PersonaList_FiltersToUserPersonas_MergesSettings_AndOrders()
    {
        var svc = PersonaService(
            out var profiles, out _, out _, out var settings);
        var aliceId = new PublicIdentityId(Guid.NewGuid());
        var bobId = new PublicIdentityId(Guid.NewGuid());
        profiles
            .Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IdentityProfile>
            {
                Profile("relay", IdentityRole.RelayHost),
                Profile("Alice", id: aliceId),
                Profile("Bob", id: bobId)
            });
        settings
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(PublicIdentityId, IdentityLaunchSettings)>
            {
                (bobId, new IdentityLaunchSettings(5200, 5201, true, 0))
            });

        var entries = await svc.ListAsync();

        entries.Should().HaveCount(2, "relay-host profiles are never exposed as personas");
        entries[0].DisplayName.Should().Be("Bob", "sidebar order wins before display name");
        entries[0].PreferredPort.Should().Be(5200);
        entries[0].LastBoundPort.Should().Be(5201);
        entries[0].AutoStart.Should().BeTrue();
        entries[1].DisplayName.Should().Be("Alice");
        entries[1].PreferredPort.Should().Be(PersonaDirectoryService.DefaultPreferredPort,
            "a persona with no settings row falls back to the default port");
        entries[1].AutoStart.Should().BeFalse();
    }

    [Test]
    public async Task PersonaRename_RehydratesPreservingRoleStateDates_AndSaves()
    {
        var svc = PersonaService(
            out var profiles, out _, out _, out _);
        var existing = Profile("Old Name");
        profiles
            .Setup(p => p.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        IdentityProfile? saved = null;
        profiles
            .Setup(p => p.SaveAsync(It.IsAny<IdentityProfile>(), It.IsAny<CancellationToken>()))
            .Callback<IdentityProfile, CancellationToken>((p, _) => saved = p)
            .Returns(Task.CompletedTask);

        await svc.RenameAsync(existing.Id, "  New Name  ");

        saved.Should().NotBeNull();
        saved!.Id.Should().Be(existing.Id);
        saved.DisplayName.Should().Be("New Name", "the rename trims surrounding whitespace");
        saved.Role.Should().Be(existing.Role);
        saved.State.Should().Be(existing.State);
        saved.CreatedAtUtc.Should().Be(existing.CreatedAtUtc);
    }

    [Test]
    public void PersonaRename_MissingPersona_Throws()
    {
        var svc = PersonaService(
            out var profiles, out _, out _, out _);
        profiles
            .Setup(p => p.GetByIdAsync(It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdentityProfile?)null);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RenameAsync(new PublicIdentityId(Guid.NewGuid()), "New"));
    }

    [Test]
    public async Task PersonaShredAndDelete_RemovesKeysProfileAndSettings()
    {
        var svc = PersonaService(
            out var profiles, out var keys, out _, out var settings);
        var id = new PublicIdentityId(Guid.NewGuid());
        keys
            .Setup(k => k.DeleteIdentityKeysAsync(id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        profiles
            .Setup(p => p.DeleteAsync(id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        settings
            .Setup(s => s.DeleteAsync(id.Value, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await svc.ShredAndDeleteAsync(id);

        keys.Verify(k => k.DeleteIdentityKeysAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        profiles.Verify(p => p.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        settings.Verify(s => s.DeleteAsync(id.Value, It.IsAny<CancellationToken>()), Times.Once,
            "the desktop launch-settings row must leave with the persona");
    }

    [Test]
    public async Task PersonaShredAndDelete_SettingsRowIsGone()
    {
        // STOR-04 parity: the shred must reach the vault's identity_settings row — assert
        // state (the row is gone afterward) through a real repository over an in-memory
        // store, not just an interaction verify.
        var fakeSettings = new FakeDesktopSettingsRepository();
        var keys = new Mock<ILocalIdentityKeyStore>(MockBehavior.Strict);
        var profiles = new Mock<IIdentityProfileRepository>(MockBehavior.Strict);
        keys
            .Setup(k => k.DeleteIdentityKeysAsync(It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        profiles
            .Setup(p => p.DeleteAsync(It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var svc = new PersonaDirectoryService(
            profiles.Object, keys.Object,
            new Mock<IPrivatePreKeyStore>(MockBehavior.Strict).Object,
            new Mock<ICryptoEngine>(MockBehavior.Strict).Object,
            Time,
            fakeSettings);

        var id = new PublicIdentityId(Guid.NewGuid());
        await fakeSettings.SaveAsync(id.Value, new IdentityLaunchSettings(5100, 5101, true, 2));
        (await fakeSettings.GetAsync(id.Value)).Should().NotBeNull();

        await svc.ShredAndDeleteAsync(id);

        (await fakeSettings.GetAsync(id.Value)).Should().BeNull(
            "a shredded persona must not leave its launch-settings row behind in the vault");
    }

    /// <summary>Minimal in-memory <see cref="IDesktopSettingsRepository"/> for state assertions.</summary>
    private sealed class FakeDesktopSettingsRepository : IDesktopSettingsRepository
    {
        private readonly Dictionary<Guid, IdentityLaunchSettings> _rows = new();
        private readonly Dictionary<string, string> _appSettings = new();

        public Task<IdentityLaunchSettings?> GetAsync(Guid identityId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(identityId, out var s) ? s : null);

        public Task<IReadOnlyList<(PublicIdentityId IdentityId, IdentityLaunchSettings Settings)>> GetAllAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(PublicIdentityId, IdentityLaunchSettings)>>(
                _rows.Select(kv => (new PublicIdentityId(kv.Key), kv.Value)).ToList());

        public Task SaveAsync(Guid identityId, IdentityLaunchSettings settings, CancellationToken ct = default)
        {
            _rows[identityId] = settings;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid identityId, CancellationToken ct = default)
        {
            _rows.Remove(identityId);
            return Task.CompletedTask;
        }

        public Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_appSettings.TryGetValue(key, out var v) ? v : null);

        public Task SetAppSettingAsync(string key, string value, CancellationToken ct = default)
        {
            _appSettings[key] = value;
            return Task.CompletedTask;
        }
    }

    // ---- ExchangeDirectoryService ----

    private static ExchangeDirectoryService ExchangeService(
        out Mock<IRelayDirectory> directory,
        out Mock<IIdentityProfileRepository> profiles,
        out Mock<IRelayMailboxCapabilityStore> capabilities,
        out Mock<IPeerTransportServer> transport,
        out Mock<IDesktopSettingsRepository> settings)
    {
        directory = new Mock<IRelayDirectory>(MockBehavior.Strict);
        profiles = new Mock<IIdentityProfileRepository>(MockBehavior.Strict);
        capabilities = new Mock<IRelayMailboxCapabilityStore>(MockBehavior.Strict);
        transport = new Mock<IPeerTransportServer>(MockBehavior.Strict);
        settings = new Mock<IDesktopSettingsRepository>(MockBehavior.Strict);
        return new ExchangeDirectoryService(
            directory.Object, profiles.Object, capabilities.Object, transport.Object, settings.Object);
    }

    [Test]
    public async Task ExchangeList_MergesSavedPortPreferences()
    {
        var svc = ExchangeService(
            out var directory, out _, out _, out _, out var settings);
        var relayId = new PublicIdentityId(Guid.NewGuid());
        var linked = new PublicIdentityId(Guid.NewGuid());
        directory
            .Setup(d => d.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RelayDirectoryEntry>
            {
                new(relayId, "Hub", RelayEndpoint.FromHostAndPort("127.0.0.1", 5200), true,
                    new List<PublicIdentityId> { linked })
            });
        settings
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(PublicIdentityId, IdentityLaunchSettings)>
            {
                (relayId, new IdentityLaunchSettings(5300, null, false, 2))
            });

        var entries = await svc.ListAsync();

        entries.Should().ContainSingle();
        entries[0].RelayIdentityId.Should().Be(relayId.Value);
        entries[0].PreferredPort.Should().Be(5300, "the settings row overrides the endpoint port");
        entries[0].IsSelfHosted.Should().BeTrue();
        entries[0].LinkedPersonaIds.Should().Equal(linked.Value);
    }

    [Test]
    public async Task ExchangeLink_ForwardsDirectoryResult()
    {
        var svc = ExchangeService(
            out var directory, out _, out _, out _, out _);
        var relayId = new PublicIdentityId(Guid.NewGuid());
        var personaId = new PublicIdentityId(Guid.NewGuid());
        directory
            .Setup(d => d.LinkIdentityAsync(
                relayId,
                personaId,
                It.IsAny<BlindedRoutingToken?>(),
                It.IsAny<DeliveryToken?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => DomainResult<RelayMailboxBinding>.Success(new RelayMailboxBinding(
                personaId,
                default,
                DeliveryToken.GenerateRandom(),
                DateTimeOffset.UtcNow)));

        var ok = await svc.LinkPersonaAsync(relayId, personaId);
        ok.IsSuccess.Should().BeTrue();

        directory
            .Setup(d => d.LinkIdentityAsync(
                relayId,
                personaId,
                It.IsAny<BlindedRoutingToken?>(),
                It.IsAny<DeliveryToken?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayMailboxBinding>.Failure(
                new DomainError("ALREADY_LINKED", "already linked")));

        var failed = await svc.LinkPersonaAsync(relayId, personaId);
        failed.IsFailure.Should().BeTrue();
        failed.Error.Code.Should().Be("ALREADY_LINKED",
            "directory failures must surface to the gate, not be swallowed");
    }

    [Test]
    public async Task ExchangeUnlink_WithoutPurge_NeverTouchesCapabilityStore()
    {
        var svc = ExchangeService(
            out var directory, out _, out var capabilities, out _, out _);
        var relayId = new PublicIdentityId(Guid.NewGuid());
        var personaId = new PublicIdentityId(Guid.NewGuid());
        directory
            .Setup(d => d.UnlinkIdentityAsync(relayId, personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        var result = await svc.UnlinkPersonaAsync(relayId, personaId, purgeQueuedEnvelopes: false);

        result.IsSuccess.Should().BeTrue();
        capabilities.Verify(
            c => c.DrainEnvelopesAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a non-purge unlink is a pure binding row delete");
        directory.Verify(
            d => d.GetMailboxTokensAsync(
                It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ExchangeStart_WhenTransportAlreadyRunning_ReturnsBoundPort()
    {
        var svc = ExchangeService(
            out _, out _, out _, out var transport, out _);
        transport.SetupGet(t => t.IsRunning).Returns(true);
        transport.SetupGet(t => t.BoundPort).Returns(new PortNumber(5400));

        var descriptor = new ExchangeLaunchDescriptor(
            Guid.NewGuid(), "Hub", 5200, new Mock<IPortLease>().Object);
        var bound = await svc.StartSelfHostedEndpointAsync(descriptor);

        bound.Should().Be(5400,
            "a second self-hosted exchange shares the already-bound listener");
        transport.Verify(
            t => t.StartAsync(
                It.IsAny<System.Net.IPAddress>(),
                It.IsAny<PortNumber>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
