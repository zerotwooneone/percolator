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
}
