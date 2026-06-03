using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Contracts;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Network.Grpc;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Network.Grpc;

[TestFixture]
public sealed class GrpcSessionServiceTests
{
    [Test]
    public void Constructor_GivenNullChannelFactory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GrpcSessionService(
            null!,
            Mock.Of<ILogger<GrpcSessionService>>()));
    }

    [Test]
    public void Constructor_GivenNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GrpcSessionService(
            Mock.Of<IPeerGrpcChannelFactory>(),
            null!));
    }

    [Test]
    public void Constructor_GivenValidParameters_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => new GrpcSessionService(
            Mock.Of<IPeerGrpcChannelFactory>(),
            Mock.Of<ILogger<GrpcSessionService>>()));
    }

    [Test]
    public async Task EstablishSessionAsync_GivenAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        // ARRANGE
        var channelFactory = Mock.Of<IPeerGrpcChannelFactory>();
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var endpoint = new DnsEndPoint("localhost", 5001);
        var request = new EstablishSessionRequest { Version = 1 };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = new GrpcSessionService(channelFactory, logger);

        // ACT & ASSERT
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sut.EstablishSessionAsync(endpoint, request, cts.Token));
    }

    [Test]
    public async Task EstablishDirectSessionAsync_GivenAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        // ARRANGE
        var channelFactory = Mock.Of<IPeerGrpcChannelFactory>();
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var endpoint = new DnsEndPoint("localhost", 5001);
        var request = new EstablishDirectSessionRequest { Version = 1 };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = new GrpcSessionService(channelFactory, logger);

        // ACT & ASSERT
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sut.EstablishDirectSessionAsync(endpoint, request, cts.Token));
    }

    [Test]
    public async Task DeliverInviteHandshakeResponseAsync_GivenAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        // ARRANGE
        var channelFactory = Mock.Of<IPeerGrpcChannelFactory>();
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var endpoint = new DnsEndPoint("localhost", 5001);
        var request = new InviteHandshakeResponse { Version = 1 };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = new GrpcSessionService(channelFactory, logger);

        // ACT & ASSERT
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sut.DeliverInviteHandshakeResponseAsync(endpoint, request, cts.Token));
    }
}
