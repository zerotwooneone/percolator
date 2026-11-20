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
using Percolator.Application.Connections;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using R3;

[TestFixture]
public sealed class InboundRequestStateServiceTests
{
    private Mock<IPeerContactQueryService> _queryServiceMock = null!;
    private Mock<IInboundConnectionWorkflow> _workflowMock = null!;
    private Mock<ICurrentPersonaService> _currentPersonaMock = null!;
    private Subject<ActivePersonaModel?> _currentPersonaChanged = null!;
    private PublicIdentityId _personaId;
    private ActivePersonaModel _personaModel = null!;

    [SetUp]
    public void SetUp()
    {
        _queryServiceMock = new Mock<IPeerContactQueryService>();
        _workflowMock = new Mock<IInboundConnectionWorkflow>();
        _currentPersonaMock = new Mock<ICurrentPersonaService>();
        _currentPersonaChanged = new Subject<ActivePersonaModel?>();

        _personaId = PublicIdentityId.New();
        _personaModel = new ActivePersonaModel(_personaId, "Alice", 5124);

        _currentPersonaMock.SetupGet(p => p.CurrentPersona).Returns(_personaModel);
        _currentPersonaMock.SetupGet(p => p.CurrentPersonaChanged).Returns(_currentPersonaChanged);

        _workflowMock
            .Setup(w => w.AcceptRequestAsync(It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<bool>.Success(true));

        _workflowMock
            .Setup(w => w.RejectRequestAsync(It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<bool>.Success(true));

        _workflowMock
            .Setup(w => w.BlockRequestAsync(It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<bool>.Success(true));
    }

    [TearDown]
    public void TearDown()
    {
        _currentPersonaChanged.Dispose();
    }

    private InboundRequestStateService CreateService()
    {
        return new InboundRequestStateService(
            _queryServiceMock.Object,
            _workflowMock.Object,
            _currentPersonaMock.Object,
            NullLogger<InboundRequestStateService>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<InboundRequestStateService>.Instance;

        Action act1 = () => new InboundRequestStateService(null!, _workflowMock.Object, _currentPersonaMock.Object, logger);
        Action act2 = () => new InboundRequestStateService(_queryServiceMock.Object, null!, _currentPersonaMock.Object, logger);
        Action act3 = () => new InboundRequestStateService(_queryServiceMock.Object, _workflowMock.Object, null!, logger);
        Action act4 = () => new InboundRequestStateService(_queryServiceMock.Object, _workflowMock.Object, _currentPersonaMock.Object, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task LoadRequestsForPersonaAsync_PopulatesRequestsAndUpdatesCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Bob Initiator", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.Requests.First().RequesterId.Should().Be(requesterId);
        service.Requests.First().DisplayName.Should().Be("Bob Initiator");
        service.PendingCount.CurrentValue.Should().Be(1);
    }

    [Test]
    public async Task AcceptRequestAsync_RemovesItemFromQueueAndDecrementsCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Bob", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.PendingCount.CurrentValue.Should().Be(1);

        await service.AcceptRequestAsync(_personaId, requesterId);

        service.Requests.Should().BeEmpty();
        service.PendingCount.CurrentValue.Should().Be(0);
    }

    [Test]
    public async Task RejectRequestAsync_RemovesItemFromQueueAndDecrementsCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Charlie", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.PendingCount.CurrentValue.Should().Be(1);

        await service.RejectRequestAsync(_personaId, requesterId);

        service.Requests.Should().BeEmpty();
        service.PendingCount.CurrentValue.Should().Be(0);
    }

    [Test]
    public async Task BlockRequestAsync_RemovesItemFromQueueAndDecrementsCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Eve Spammer", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.PendingCount.CurrentValue.Should().Be(1);

        await service.BlockRequestAsync(_personaId, requesterId);

        service.Requests.Should().BeEmpty();
        service.PendingCount.CurrentValue.Should().Be(0);
    }
}
