using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Simulator.Features.Chaos;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Chaos;

[TestFixture]
public sealed class SimulatorChaosTransportDispatcherDecoratorTests
{
    private SimulatedNodeId _nodeId;
    private SimulatorChaosController _chaosController = null!;
    private Mock<ITransportDispatcher> _innerDispatcherMock = null!;
    private Mock<ISimulatorLogSink> _logSinkMock = null!;
    private SimulatorChaosTransportDispatcherDecorator _decorator = null!;

    [SetUp]
    public void SetUp()
    {
        _nodeId = SimulatedNodeId.CreateNew();
        _chaosController = new SimulatorChaosController();
        _innerDispatcherMock = new Mock<ITransportDispatcher>(MockBehavior.Strict);
        _logSinkMock = new Mock<ISimulatorLogSink>(MockBehavior.Loose);
        _decorator = new SimulatorChaosTransportDispatcherDecorator(
            _innerDispatcherMock.Object,
            _chaosController,
            _nodeId,
            _logSinkMock.Object);
    }

    private static OutboxJob CreateTestJob()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        return OutboxJob.Create(
            new ChannelId(Guid.NewGuid()),
            PublicIdentityId.New(),
            PublicIdentityId.New(),
            new DeliveryRoute(
                DeliveryRouteType.RelayedOneToOne,
                new BlindedRoutingToken(Guid.NewGuid()),
                RelayEndpoint.FromHostAndPort("127.0.0.1", 5100),
                DeliveryToken.GenerateRandom()),
            new byte[] { 1, 2, 3, 4 },
            timeProvider).Value!;
    }

    [Test]
    public async Task SendAsync_WhenPass_DelegatesToInnerDispatcher()
    {
        var job = CreateTestJob();
        _innerDispatcherMock.Setup(d => d.SendAsync(job, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.Pass);

        var result = await _decorator.SendAsync(job);

        result.IsSuccess.Should().BeTrue();
        _innerDispatcherMock.Verify(d => d.SendAsync(job, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SendAsync_WhenDropEnvelope_ReturnsFailureWithoutCallingInner()
    {
        var job = CreateTestJob();
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.DropEnvelope);

        var result = await _decorator.SendAsync(job);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CHAOS_DROPPED");
        _innerDispatcherMock.Verify(d => d.SendAsync(It.IsAny<OutboxJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SendAsync_WhenSimulateTransientError_ReturnsTransientFailureWithoutCallingInner()
    {
        var job = CreateTestJob();
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.SimulateTransientError);

        var result = await _decorator.SendAsync(job);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("TRANSIENT_FAULT");
        _innerDispatcherMock.Verify(d => d.SendAsync(It.IsAny<OutboxJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SendAsync_WhenInjectArtificialDelay_DelaysAndDelegatesToInner()
    {
        var job = CreateTestJob();
        _innerDispatcherMock.Setup(d => d.SendAsync(job, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.InjectArtificialDelay);

        var sw = Stopwatch.StartNew();
        var result = await _decorator.SendAsync(job);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400);
        result.IsSuccess.Should().BeTrue();
        _innerDispatcherMock.Verify(d => d.SendAsync(job, It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; }
        public FixedTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;
    }
}
