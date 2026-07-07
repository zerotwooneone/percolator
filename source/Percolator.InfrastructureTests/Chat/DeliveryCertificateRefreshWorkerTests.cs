using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
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
        var fakeTimeProvider = new FakeTimeProvider();

        var refreshCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        serviceScopeMock.As<IAsyncDisposable>()
            .Setup(ad => ad.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        serviceScopeMock.Setup(s => s.ServiceProvider)
            .Returns(serviceProviderMock.Object);

        scopeFactoryMock.Setup(sf => sf.CreateScope())
            .Returns(serviceScopeMock.Object);

        serviceProviderMock.Setup(sp => sp.GetService(typeof(ICertificateOrchestrator)))
            .Returns(orchestratorMock.Object);

        orchestratorMock.Setup(o => o.RefreshLocalCertificateAsync(It.IsAny<ChatSelfId>(), It.IsAny<ChatPeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => refreshCompletedTcs.SetResult(true));

        var worker = new DeliveryCertificateRefreshWorker(
            scopeFactoryMock.Object,
            loggerMock.Object,
            fakeTimeProvider);

        // ACT

        // 1. Start the BackgroundService
        await worker.StartAsync(CancellationToken.None);

        // 2. Wait deterministically for the first refresh
        await refreshCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 3. Stop the service (This cancels the stoppingToken and waits for ExecuteAsync to finish safely)
        var stopTask = worker.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        // 4. Assert Graceful Shutdown
        Assert.That(stopTask.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task ErrorBackoff_RetriesAfterFiveMinutes()
    {
        // ARRANGE
        var serviceProviderMock = new Mock<IServiceProvider>();
        var serviceScopeMock = new Mock<IServiceScope>();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var orchestratorMock = new Mock<ICertificateOrchestrator>();
        var loggerMock = new Mock<ILogger<DeliveryCertificateRefreshWorker>>();
        var fakeTimeProvider = new FakeTimeProvider();

        var refreshCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        serviceScopeMock.As<IAsyncDisposable>()
            .Setup(ad => ad.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        serviceScopeMock.Setup(s => s.ServiceProvider)
            .Returns(serviceProviderMock.Object);

        scopeFactoryMock.Setup(sf => sf.CreateScope())
            .Returns(serviceScopeMock.Object);

        serviceProviderMock.Setup(sp => sp.GetService(typeof(ICertificateOrchestrator)))
            .Returns(orchestratorMock.Object);

        var callCount = 0;
        orchestratorMock.Setup(o => o.RefreshLocalCertificateAsync(It.IsAny<ChatSelfId>(), It.IsAny<ChatPeerId>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;

                // 1. First call fails, triggering the 5-minute error backoff delay
                if (callCount == 1)
                {
                    throw new Exception("Transient error");
                }

                // 2. Second call (the retry) succeeds, signaling the test thread
                refreshCompletedTcs.TrySetResult(true);
                return Task.CompletedTask;
            });

        var worker = new DeliveryCertificateRefreshWorker(
            scopeFactoryMock.Object,
            loggerMock.Object,
            fakeTimeProvider);

        // ACT

        // Start the BackgroundService. It will immediately execute Call 1, throw, catch,
        // and park itself on the 5-minute FakeTimeProvider delay.
        await worker.StartAsync(CancellationToken.None);

        // Advance time by 5 minutes to instantly complete the error delay and trigger the retry.
        fakeTimeProvider.Advance(TimeSpan.FromMinutes(5));

        // Wait deterministically for the retry to complete successfully
        await refreshCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Stop the service (which cancels the 20-hour success delay the worker is now sitting on)
        var stopTask = worker.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        // ASSERT
        // Verify the orchestrator was called exactly twice (Initial Failure + Successful Retry)
        orchestratorMock.Verify(o => o.RefreshLocalCertificateAsync(It.IsAny<ChatSelfId>(), It.IsAny<ChatPeerId>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        // Verify graceful shutdown
        Assert.That(stopTask.IsCompletedSuccessfully, Is.True);
    }
}
