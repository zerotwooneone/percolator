using System.Net;
using AutoFixture;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Dht.Messages;

namespace Percolator.Dht.Tests;

[TestFixture]
public class PingRequestHandlerTests
{
    private Fixture _fixture;
    private Mock<IDhtNodeRepository> _mockRepository;
    private PingRequestHandler _sut;

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _fixture.Customize<DnsEndPoint>(c => c.FromFactory(() => new DnsEndPoint(_fixture.Create<string>(), _fixture.Create<ushort>())));
        _mockRepository = new Mock<IDhtNodeRepository>();
        _sut = new PingRequestHandler(_mockRepository.Object);
    }

    [Test]
    public async Task Handle_WhenCalled_ReturnsResponse()
    {
        // Arrange
        var request = _fixture.Create<PingRequest>();

        // Act
        var response = await _sut.Handle(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
    }

    [Test]
    public async Task Handle_WhenNodeIsNew_AddsNodeToRepository()
    {
        // Arrange
        var request = _fixture.Create<PingRequest>();
        DhtNode? capturedNode = null;

        _mockRepository.Setup(r => r.GetAsync(request.SenderId))
            .ReturnsAsync((DhtNode?)null);

        _mockRepository.Setup(r => r.AddAsync(It.IsAny<DhtNode>()))
            .Callback<DhtNode>(node => capturedNode = node)
            .Returns(Task.CompletedTask);

        // Act
        await _sut.Handle(request, CancellationToken.None);

        // Assert
        capturedNode.Should().NotBeNull();
        capturedNode!.Id.Should().Be(request.SenderId);
        capturedNode.EndPoint.Should().Be(request.SenderEndPoint);
    }
}
