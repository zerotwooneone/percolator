// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using R3;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class PeerConnectionStateServiceTests
{
    private FakeOutboundPendingRetryWorker _fakeRetryWorker = null!;
    private PublicIdentityId _peerId;
    private OutboundPendingConnectionModel _testModel = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeRetryWorker = new FakeOutboundPendingRetryWorker();
        _peerId = PublicIdentityId.New();

        var payload = new PeerInvitationPayload(
            _peerId,
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            new[] { "127.0.0.1" },
            PortRange.Single(5100));

        _testModel = new OutboundPendingConnectionModel(
            _peerId,
            "Bob Direct",
            payload,
            DateTimeOffset.UtcNow);
    }

    [TearDown]
    public void TearDown()
    {
        _fakeRetryWorker.Dispose();
    }

    private PeerConnectionStateService CreateService()
    {
        return new PeerConnectionStateService(
            _fakeRetryWorker,
            NullLogger<PeerConnectionStateService>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<PeerConnectionStateService>.Instance;

        Action act1 = () => new PeerConnectionStateService(null!, logger);
        Action act2 = () => new PeerConnectionStateService(_fakeRetryWorker, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void StagePendingOutbound_AddsToCollectionAndEnqueuesRetry()
    {
        using var service = CreateService();

        service.StagePendingOutbound(_testModel);

        service.PendingOutboundConnections.Should().ContainSingle();
        service.PendingOutboundConnections[0].PeerId.Should().Be(_peerId);
        _fakeRetryWorker.EnqueuedRetries.Should().ContainSingle();
        _fakeRetryWorker.EnqueuedRetries[0].PeerId.Should().Be(_peerId);
    }

    [Test]
    public void StagePendingOutbound_WithDuplicatePeerId_DoesNotDuplicate()
    {
        using var service = CreateService();

        service.StagePendingOutbound(_testModel);
        service.StagePendingOutbound(_testModel);

        service.PendingOutboundConnections.Count(p => p.PeerId == _peerId).Should().Be(1);
    }

    [Test]
    public void CancelOutboundRequest_RemovesFromCollectionAndCancelsWorker()
    {
        using var service = CreateService();
        service.StagePendingOutbound(_testModel);

        PublicIdentityId? cancelledId = null;
        using var sub = service.ConnectionCancelled.Subscribe(id => cancelledId = id);

        service.CancelOutboundRequest(_peerId);

        service.PendingOutboundConnections.Should().BeEmpty();
        _fakeRetryWorker.CancelledRetries.Should().Contain(_peerId);
        cancelledId.Should().Be(_peerId);
    }

    [Test]
    public void WorkerConnectionSucceeded_RemovesPendingItemAndEmitsEvent()
    {
        using var service = CreateService();
        service.StagePendingOutbound(_testModel);

        OutboundPendingConnectionModel? completed = null;
        using var sub = service.ConnectionSucceeded.Subscribe(m => completed = m);

        _fakeRetryWorker.SimulateSuccess(_testModel);

        service.PendingOutboundConnections.Should().BeEmpty();
        completed.Should().NotBeNull();
        completed!.PeerId.Should().Be(_peerId);
    }

    [Test]
    public void IsPending_ReturnsTrueWhenStaged_AndFalseWhenRemoved()
    {
        using var service = CreateService();
        service.IsPending(_peerId).Should().BeFalse();

        service.StagePendingOutbound(_testModel);
        service.IsPending(_peerId).Should().BeTrue();

        service.CancelOutboundRequest(_peerId);
        service.IsPending(_peerId).Should().BeFalse();
    }

    [Test]
    public void CancelOutboundRequest_WhenNotPending_DoesNotEmitEvent()
    {
        using var service = CreateService();
        PublicIdentityId? cancelledId = null;
        using var sub = service.ConnectionCancelled.Subscribe(id => cancelledId = id);

        service.CancelOutboundRequest(PublicIdentityId.New());

        cancelledId.Should().BeNull("cancelling an unlisted peer must not emit cancellation events");
    }
}

internal sealed class FakeOutboundPendingRetryWorker : IOutboundPendingRetryWorker
{
    private readonly Subject<OutboundPendingConnectionModel> _connectionSucceeded = new();
    public List<OutboundPendingConnectionModel> EnqueuedRetries { get; } = new();
    public List<PublicIdentityId> CancelledRetries { get; } = new();

    public Observable<OutboundPendingConnectionModel> ConnectionSucceeded => _connectionSucceeded;

    public IReadOnlyCollection<PublicIdentityId> ActiveRetryPeerIds =>
        EnqueuedRetries.Select(r => r.PeerId).Except(CancelledRetries).ToList();

    public void EnqueueRetry(OutboundPendingConnectionModel model)
    {
        EnqueuedRetries.Add(model);
    }

    public void CancelRetry(PublicIdentityId peerId)
    {
        CancelledRetries.Add(peerId);
    }

    public bool IsRetrying(PublicIdentityId peerId)
    {
        return EnqueuedRetries.Any(r => r.PeerId == peerId) && !CancelledRetries.Contains(peerId);
    }

    public void SimulateSuccess(OutboundPendingConnectionModel model)
    {
        _connectionSucceeded.OnNext(model);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _connectionSucceeded.OnCompleted();
        _connectionSucceeded.Dispose();
    }
}
