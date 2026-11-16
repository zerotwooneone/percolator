// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using R3;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class OutboundPendingRetryWorkerTests
{
    private FakePeerConnectionService _fakePeerConnectionService = null!;
    private FakeTimeProvider _fakeTimeProvider = null!;
    private PublicIdentityId _peerId;
    private OutboundPendingConnectionModel _testModel = null!;

    [SetUp]
    public void SetUp()
    {
        _fakePeerConnectionService = new FakePeerConnectionService();
        _fakeTimeProvider = new FakeTimeProvider();
        _peerId = PublicIdentityId.New();

        var payload = new PeerInvitationPayload(
            _peerId,
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            new[] { "127.0.0.1" },
            PortRange.Single(5100));

        _testModel = new OutboundPendingConnectionModel(
            _peerId,
            "Alice Direct",
            payload,
            DateTimeOffset.UtcNow);
    }

    private OutboundPendingRetryWorker CreateWorker()
    {
        return new OutboundPendingRetryWorker(
            _fakePeerConnectionService,
            NullLogger<OutboundPendingRetryWorker>.Instance,
            _fakeTimeProvider);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<OutboundPendingRetryWorker>.Instance;
        var time = _fakeTimeProvider;

        Action act1 = () => new OutboundPendingRetryWorker(null!, logger, time);
        Action act2 = () => new OutboundPendingRetryWorker(_fakePeerConnectionService, null!, time);
        Action act3 = () => new OutboundPendingRetryWorker(_fakePeerConnectionService, logger, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void BackoffSchedule_MatchesSpecifiedProgression()
    {
        OutboundPendingRetryWorker.GetBackoffDelay(0).Should().Be(TimeSpan.FromSeconds(5));
        OutboundPendingRetryWorker.GetBackoffDelay(1).Should().Be(TimeSpan.FromSeconds(15));
        OutboundPendingRetryWorker.GetBackoffDelay(2).Should().Be(TimeSpan.FromSeconds(30));
        OutboundPendingRetryWorker.GetBackoffDelay(3).Should().Be(TimeSpan.FromSeconds(60));
        OutboundPendingRetryWorker.GetBackoffDelay(10).Should().Be(TimeSpan.FromSeconds(60), "backoff must be capped at 60s");
    }

    [Test]
    public void EnqueueRetry_RecordsPeerAsActive()
    {
        using var worker = CreateWorker();

        worker.EnqueueRetry(_testModel);

        worker.IsRetrying(_peerId).Should().BeTrue();
        worker.ActiveRetryPeerIds.Should().Contain(_peerId);
    }

    [Test]
    public void EnqueueRetry_WhenAlreadyActive_DoesNotDuplicate()
    {
        using var worker = CreateWorker();

        worker.EnqueueRetry(_testModel);
        worker.EnqueueRetry(_testModel);

        worker.ActiveRetryPeerIds.Count(id => id == _peerId).Should().Be(1);
    }

    [Test]
    public void CancelRetry_RemovesActivePeer()
    {
        using var worker = CreateWorker();

        worker.EnqueueRetry(_testModel);
        worker.IsRetrying(_peerId).Should().BeTrue();

        worker.CancelRetry(_peerId);

        worker.IsRetrying(_peerId).Should().BeFalse();
        worker.ActiveRetryPeerIds.Should().NotContain(_peerId);
    }

    [Test]
    public async Task RetryLoop_WhenPeerBecomesReachable_EmitsConnectionSucceededAndStops()
    {
        using var worker = CreateWorker();
        _fakePeerConnectionService.NextResult = DomainResult<PeerConnectionResult>.Success(
            new PeerConnectionResult(ChannelId.New(), DeviceId.Primary));

        var tcs = new TaskCompletionSource<OutboundPendingConnectionModel>();
        using var sub = worker.ConnectionSucceeded.Subscribe(m => tcs.TrySetResult(m));

        worker.EnqueueRetry(_testModel);

        // Advance past initial 5-second backoff delay
        for (int i = 0; i < 20 && !tcs.Task.IsCompleted; i++)
        {
            _fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        var succeeded = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        succeeded.Should().NotBeNull();
        succeeded.PeerId.Should().Be(_peerId);
        worker.IsRetrying(_peerId).Should().BeFalse("retry loop must exit upon successful connection");
    }

    [Test]
    public async Task RetryLoop_WhenPeerRemainsOffline_AdvancesBackoffSchedule()
    {
        using var worker = CreateWorker();
        _fakePeerConnectionService.NextResult = DomainResult<PeerConnectionResult>.Failure(
            new DomainError("OFFLINE", "Peer unreachable"));

        worker.EnqueueRetry(_testModel);

        // Advance 5s (attempt 0)
        for (int i = 0; i < 6; i++)
        {
            _fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        worker.IsRetrying(_peerId).Should().BeTrue();

        // Advance 15s (attempt 1)
        for (int i = 0; i < 16; i++)
        {
            _fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        worker.IsRetrying(_peerId).Should().BeTrue();

        // Cancel retry
        worker.CancelRetry(_peerId);
        worker.IsRetrying(_peerId).Should().BeFalse();
    }

    [Test]
    public async Task StopAsync_CancelsAllActiveRetries()
    {
        using var worker = CreateWorker();
        _fakePeerConnectionService.NextResult = DomainResult<PeerConnectionResult>.Failure(
            new DomainError("OFFLINE", "Peer unreachable"));

        worker.EnqueueRetry(_testModel);
        worker.IsRetrying(_peerId).Should().BeTrue();

        await worker.StopAsync(CancellationToken.None);

        await Task.Yield();
        worker.IsRetrying(_peerId).Should().BeFalse();
    }

    [Test]
    public async Task RetryLoop_WithIpv6Host_BuildsValidUriWithoutException()
    {
        using var worker = CreateWorker();
        var ipv6Payload = new PeerInvitationPayload(
            _peerId,
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            new[] { "::1" },
            PortRange.Single(5200));

        var model = new OutboundPendingConnectionModel(_peerId, "IPv6 Peer", ipv6Payload, DateTimeOffset.UtcNow);

        var tcs = new TaskCompletionSource<OutboundPendingConnectionModel>();
        using var sub = worker.ConnectionSucceeded.Subscribe(m => tcs.TrySetResult(m));

        _fakePeerConnectionService.NextResult = DomainResult<PeerConnectionResult>.Success(
            new PeerConnectionResult(ChannelId.New(), DeviceId.Primary));

        worker.EnqueueRetry(model);

        for (int i = 0; i < 20 && !tcs.Task.IsCompleted; i++)
        {
            _fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        var succeeded = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        succeeded.Should().NotBeNull();
        _fakePeerConnectionService.LastDirectEndpoint.Should().NotBeNull();
        _fakePeerConnectionService.LastDirectEndpoint!.Host.Should().Be("[::1]");
        _fakePeerConnectionService.LastDirectEndpoint.Port.Should().Be(5200);
    }
}

internal sealed class FakePeerConnectionService : IPeerConnectionService
{
    public DomainResult<PeerConnectionResult> NextResult { get; set; } =
        DomainResult<PeerConnectionResult>.Failure(new DomainError("OFFLINE", "Default offline fake"));

    public List<PublicIdentityId> ConnectionAttempts { get; } = new();
    public Uri? LastDirectEndpoint { get; private set; }

    public Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
        PublicIdentityId targetPeerId,
        DeviceId? targetDeviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        ConnectionAttempts.Add(targetPeerId);
        return Task.FromResult(NextResult);
    }

    public Task<DomainResult<PeerConnectionResult>> ConnectToDirectPeerAsync(
        PublicIdentityId targetPeerId,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        Uri directEndpoint,
        DeviceId? targetDeviceId = null,
        CancellationToken ct = default)
    {
        ConnectionAttempts.Add(targetPeerId);
        LastDirectEndpoint = directEndpoint;
        return Task.FromResult(NextResult);
    }
}
