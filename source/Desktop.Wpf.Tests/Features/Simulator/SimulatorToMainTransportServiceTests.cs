using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Grpc.Core;
using Moq;
using NUnit.Framework;
using Percolator.Contracts;

namespace Desktop.Wpf.Tests.Features.Simulator;

[TestFixture]
public class SimulatorToMainTransportServiceTests
{
    private Mock<ISimulatorGrpcClientFactory> _clientFactoryMock;
    private Mock<TransportService.TransportServiceClient> _clientMock;
    private SimulatorToMainTransportService _sut;

    [SetUp]
    public void Setup()
    {
        _clientFactoryMock = new Mock<ISimulatorGrpcClientFactory>();
        _clientMock = new Mock<TransportService.TransportServiceClient>();
        _clientFactoryMock.Setup(f => f.CreateClient()).Returns(_clientMock.Object);

        _sut = new SimulatorToMainTransportService(_clientFactoryMock.Object);
    }

    [Test]
    public async Task SendOpaqueMessageToMainAsync_GivenValidRequest_InvokesGrpcClient()
    {
        // ARRANGE
        var request = new DeliverOpaqueMessageRequest { Version = 1 };
        var expectedResponse = new DeliverOpaqueMessageResponse { Version = 1 };

        _clientMock.Setup(c => c.DeliverOpaqueMessageAsync(
            request, null, null, It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<DeliverOpaqueMessageResponse>(
                Task.FromResult(expectedResponse),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // ACT
        var result = await _sut.SendOpaqueMessageToMainAsync(request, CancellationToken.None);

        // ASSERT
        result.Should().BeEquivalentTo(expectedResponse);
        _clientMock.Verify(c => c.DeliverOpaqueMessageAsync(request, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void SendOpaqueMessageToMainAsync_GivenNullRequest_ThrowsArgumentNullException()
    {
        // ARRANGE
        DeliverOpaqueMessageRequest? nullRequest = null;

        // ACT
        var act = async () => await _sut.SendOpaqueMessageToMainAsync(nullRequest!, CancellationToken.None);

        // ASSERT
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task SendEstablishSessionToMainAsync_GivenValidRequest_InvokesGrpcClient()
    {
        // ARRANGE
        var request = new EstablishSessionRequest { Version = 1 };
        var expectedResponse = new EstablishSessionResponse { Version = 1 };

        _clientMock.Setup(c => c.EstablishSessionAsync(
            request, null, null, It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<EstablishSessionResponse>(
                Task.FromResult(expectedResponse),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // ACT
        var result = await _sut.SendEstablishSessionToMainAsync(request, CancellationToken.None);

        // ASSERT
        result.Should().BeEquivalentTo(expectedResponse);
        _clientMock.Verify(c => c.EstablishSessionAsync(request, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void SendEstablishSessionToMainAsync_GivenNullRequest_ThrowsArgumentNullException()
    {
        // ARRANGE
        EstablishSessionRequest? nullRequest = null;

        // ACT
        var act = async () => await _sut.SendEstablishSessionToMainAsync(nullRequest!, CancellationToken.None);

        // ASSERT
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task SendEstablishDirectSessionToMainAsync_GivenValidRequest_InvokesGrpcClient()
    {
        // ARRANGE
        var request = new EstablishDirectSessionRequest { Version = 1 };
        var expectedResponse = new EstablishDirectSessionResponse { Version = 1 };

        _clientMock.Setup(c => c.EstablishDirectSessionAsync(
            request, null, null, It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<EstablishDirectSessionResponse>(
                Task.FromResult(expectedResponse),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // ACT
        await _sut.SendEstablishDirectSessionToMainAsync(request, CancellationToken.None);

        // ASSERT
        _clientMock.Verify(c => c.EstablishDirectSessionAsync(request, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void SendEstablishDirectSessionToMainAsync_GivenNullRequest_ThrowsArgumentNullException()
    {
        // ARRANGE
        EstablishDirectSessionRequest? nullRequest = null;

        // ACT
        var act = async () => await _sut.SendEstablishDirectSessionToMainAsync(nullRequest!, CancellationToken.None);

        // ASSERT
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task DeliverInviteHandshakeResponseToMainAsync_GivenValidRequest_InvokesGrpcClient()
    {
        // ARRANGE
        var response = new InviteHandshakeResponse { Version = 1 };
        var expectedResponse = new DeliverInviteHandshakeResponseAck { Version = 1 };

        _clientMock.Setup(c => c.DeliverInviteHandshakeResponseAsync(
            response, null, null, It.IsAny<CancellationToken>()))
            .Returns(new AsyncUnaryCall<DeliverInviteHandshakeResponseAck>(
                Task.FromResult(expectedResponse),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // ACT
        var result = await _sut.DeliverInviteHandshakeResponseToMainAsync(response, CancellationToken.None);

        // ASSERT
        result.Should().BeEquivalentTo(expectedResponse);
        _clientMock.Verify(c => c.DeliverInviteHandshakeResponseAsync(response, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void DeliverInviteHandshakeResponseToMainAsync_GivenNullRequest_ThrowsArgumentNullException()
    {
        // ARRANGE
        InviteHandshakeResponse? nullResponse = null;

        // ACT
        var act = async () => await _sut.DeliverInviteHandshakeResponseToMainAsync(nullResponse!, CancellationToken.None);

        // ASSERT
        act.Should().ThrowAsync<ArgumentNullException>();
    }
}
