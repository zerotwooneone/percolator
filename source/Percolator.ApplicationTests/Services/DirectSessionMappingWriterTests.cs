using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Services;
using Percolator.Network;

namespace Percolator.ApplicationTests.Services;

[TestFixture]
public sealed class DirectSessionMappingWriterTests
{
    [Test]
    public async Task WriteMappingAsync_WhenRepositoryThrows_LogsWarningAndDoesNotThrow()
    {
        // Arrange
        var remotePeerId = PeerId.NewId();
        var sessionId = DirectSessionId.NewId();
        var selfIdentityId = 7;

        var directSessionRepository = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        directSessionRepository
            .Setup(r => r.UpsertAsync(remotePeerId, sessionId, selfIdentityId))
            .Throws(new InvalidOperationException("Database connection failed"));

        var logger = new Mock<ILogger<DirectSessionMappingWriter>>(MockBehavior.Loose);

        var sut = new DirectSessionMappingWriter(directSessionRepository.Object, logger.Object);

        // Act
        Exception? exception = null;
        try
        {
            await sut.WriteMappingAsync(remotePeerId, sessionId, selfIdentityId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        // Assert
        Assert.That(exception, Is.Null, "WriteMappingAsync should not throw exception");
        
        // Verify warning was logged
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
