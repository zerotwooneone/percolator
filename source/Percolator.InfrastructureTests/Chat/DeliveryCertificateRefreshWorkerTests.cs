using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat;
using Percolator.Infrastructure.Chat;

namespace Percolator.InfrastructureTests.Chat;

[TestFixture]
public class DeliveryCertificateRefreshWorkerTests
{
    [Test]
    public async Task Shutdown_CancelsGracefully()
    {
        // ARRANGE
        var serviceProviderMock = new Mock<IServiceProvider>();
        var serviceScopeMock = new Mock<IServiceScope>();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var orchestratorMock = new Mock<ICertificateOrchestrator>();
        var loggerMock = new Mock<ILogger<DeliveryCertificateRefreshWorker>>();
        var lifetimeMock = new Mock<IHostApplicationLifetime>();

        var cts = new CancellationTokenSource();

        serviceProviderMock.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactoryMock.Object);

        serviceScopeMock.As<IAsyncDisposable>()
            .Setup(ad => ad.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        serviceScopeMock.Setup(s => s.ServiceProvider)
            .Returns(serviceProviderMock.Object);

        scopeFactoryMock.Setup(sf => sf.CreateScope())
            .Returns(serviceScopeMock.Object);

        serviceProviderMock.Setup(sp => sp.GetService(typeof(ICertificateOrchestrator)))
            .Returns(orchestratorMock.Object);

        orchestratorMock.Setup(o => o.RefreshLocalCertificateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new DeliveryCertificateRefreshWorker(
            serviceProviderMock.Object,
            loggerMock.Object,
            lifetimeMock.Object);

        // Use reflection to call the private RunRefreshLoopAsync method directly
        var runRefreshLoopMethod = worker.GetType()
            .GetMethod("RunRefreshLoopAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // ACT - Start the refresh loop and cancel it
        var loopTask = (Task?)runRefreshLoopMethod?.Invoke(worker, new object[] { cts.Token });

        // Wait a bit for the first refresh to complete
        await Task.Delay(100);

        // Cancel the token
        cts.Cancel();

        // Wait for the worker to exit
        await Task.Delay(200);

        // ASSERT
        // Verify that the orchestrator was called at least once (the immediate refresh)
        orchestratorMock.Verify(o => o.RefreshLocalCertificateAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce());

        // Verify that no error was logged for OperationCanceledException
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<OperationCanceledException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

        // Verify that info log was logged for shutdown
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
