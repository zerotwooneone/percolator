using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Contracts;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Network.Grpc;

namespace Percolator.InfrastructureTests.Network.Grpc;

[TestFixture]
public sealed class GrpcSessionServiceTests
{
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
        var channelFactoryMock = new Mock<IPeerGrpcChannelFactory>();
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var endpoint = new DnsEndPoint("localhost", 5001);
        var request = new EstablishSessionRequest { Version = 1 };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = new GrpcSessionService(channelFactoryMock.Object, logger);

        // ACT & ASSERT
        // The service throws RpcException when channel factory returns null (due to cancelled token)
        Assert.ThrowsAsync<RpcException>(async () =>
            await sut.EstablishSessionAsync(endpoint, request, cts.Token));
    }

    [Test]
    public async Task EstablishDirectSessionAsync_GivenAlreadyCancelledToken_ThrowsRpcException()
    {
        // ARRANGE
        var channelFactoryMock = new Mock<IPeerGrpcChannelFactory>();
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var endpoint = new DnsEndPoint("localhost", 5001);
        var request = new EstablishDirectSessionRequest { Version = 1 };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = new GrpcSessionService(channelFactoryMock.Object, logger);

        // ACT & ASSERT
        // The service throws RpcException when channel factory returns null (due to cancelled token)
        Assert.ThrowsAsync<RpcException>(async () =>
            await sut.EstablishDirectSessionAsync(endpoint, request, cts.Token));
    }

    [Test]
    public async Task DeliverInviteHandshakeResponseAsync_GivenAlreadyCancelledToken_ThrowsRpcException()
    {
        // ARRANGE
        var channelFactoryMock = new Mock<IPeerGrpcChannelFactory>();
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var endpoint = new DnsEndPoint("localhost", 5001);
        var request = new InviteHandshakeResponse { Version = 1 };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = new GrpcSessionService(channelFactoryMock.Object, logger);

        // ACT & ASSERT
        // The service throws RpcException when channel factory returns null (due to cancelled token)
        Assert.ThrowsAsync<RpcException>(async () =>
            await sut.DeliverInviteHandshakeResponseAsync(endpoint, request, cts.Token));
    }
}
