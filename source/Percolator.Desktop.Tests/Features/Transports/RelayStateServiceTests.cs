// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Transports;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Delivery.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.PluginSdk;
using R3;

[TestFixture]
public sealed class RelayStateServiceTests
{
    private Mock<IPersonaRelayQueryService> _queryServiceMock = null!;
    private Mock<IRelayDirectory> _directoryMock = null!;
    private Mock<ICurrentPersonaService> _currentPersonaMock = null!;
    private Subject<ActivePersonaModel?> _currentPersonaChanged = null!;
    private PublicIdentityId _personaId;
    private ActivePersonaModel _personaModel = null!;

    [SetUp]
    public void SetUp()
    {
        _queryServiceMock = new Mock<IPersonaRelayQueryService>();
        _directoryMock = new Mock<IRelayDirectory>();
        _currentPersonaMock = new Mock<ICurrentPersonaService>();
        _currentPersonaChanged = new Subject<ActivePersonaModel?>();

        _personaId = PublicIdentityId.New();
        _personaModel = new ActivePersonaModel(_personaId, "Alice", 5124);

        _currentPersonaMock.SetupGet(p => p.CurrentPersona).Returns(_personaModel);
        _currentPersonaMock.SetupGet(p => p.CurrentPersonaChanged).Returns(_currentPersonaChanged);

        _directoryMock
            .Setup(d => d.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RelayDirectoryEntry>());

        _directoryMock
            .Setup(d => d.RegisterRemoteRelayAsync(It.IsAny<PublicIdentityId>(), It.IsAny<RelayEndpoint>(), It.IsAny<CertificatePin?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublicIdentityId id, RelayEndpoint ep, CertificatePin? pin, CancellationToken ct) =>
                DomainResult<RelayDirectoryEntry>.Success(new RelayDirectoryEntry(id, ep.Host, ep, false, new[] { _personaId })));

        _directoryMock
            .Setup(d => d.LinkIdentityAsync(It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<BlindedRoutingToken?>(), It.IsAny<DeliveryToken?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayMailboxBinding>.Failure(new DomainError("MOCK", "mock binding")));
    }

    [TearDown]
    public void TearDown()
    {
        _currentPersonaChanged.Dispose();
    }

    private RelayStateService CreateService()
    {
        return new RelayStateService(
            _queryServiceMock.Object,
            _directoryMock.Object,
            _currentPersonaMock.Object,
            NullLogger<RelayStateService>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<RelayStateService>.Instance;

        Action act1 = () => new RelayStateService(null!, _directoryMock.Object, _currentPersonaMock.Object, logger);
        Action act2 = () => new RelayStateService(_queryServiceMock.Object, null!, _currentPersonaMock.Object, logger);
        Action act3 = () => new RelayStateService(_queryServiceMock.Object, _directoryMock.Object, null!, logger);
        Action act4 = () => new RelayStateService(_queryServiceMock.Object, _directoryMock.Object, _currentPersonaMock.Object, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task LoadRelaysForPersonaAsync_PopulatesRelaysCollection()
    {
        var relayId = PublicIdentityId.New();
        var ep = RelayEndpoint.Parse("percolator://relay1.percolator.net");
        var link = new PersonaRelayLinkReadModel(
            relayId, "Primary Post Office", ep, false, RelayLinkStatus.Enabled);

        _queryServiceMock
            .Setup(q => q.GetRelayLinksForPersonaAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaRelayLinkReadModel> { link });

        using var service = CreateService();
        await service.LoadRelaysForPersonaAsync(_personaId);

        service.Relays.Should().ContainSingle();
        var item = service.Relays.First();
        item.RelayIdentityId.Should().Be(relayId);
        item.Endpoint.Should().Be(ep);
        item.DisplayName.Should().Be("Primary Post Office");
    }

    [Test]
    public async Task AddRelayAsync_RegistersRelayAndAddsToCollectionImmediately()
    {
        using var service = CreateService();

        var endpoint = RelayEndpoint.Parse("percolator://new-relay.org");
        var result = await service.AddRelayAsync(_personaId, endpoint, friendlyName: "My New Relay");

        result.Should().NotBeNull();
        result.Endpoint.Should().Be(endpoint);
        result.DisplayName.Should().Be("My New Relay");

        service.Relays.Should().ContainSingle(r => r.Endpoint == endpoint);
        var added = service.Relays.First(r => r.Endpoint == endpoint);
        added.RelayIdentityId.Should().Be(result.RelayIdentityId);
        added.DisplayName.Should().Be("My New Relay");
    }

    [Test]
    public async Task UpdateRelayStatus_UpdatesLiveStatusAndLatency()
    {
        using var service = CreateService();
        var endpoint = RelayEndpoint.Parse("percolator://relay.org");
        var added = await service.AddRelayAsync(_personaId, endpoint);

        service.UpdateRelayStatus(added.RelayIdentityId, RelayConnectionStatus.Connected, latencyMs: 42);

        var item = service.Relays.First(r => r.RelayIdentityId == added.RelayIdentityId);
        item.Status.Should().Be(RelayConnectionStatus.Connected);
        item.LatencyMs.Should().Be(42);
    }
}
