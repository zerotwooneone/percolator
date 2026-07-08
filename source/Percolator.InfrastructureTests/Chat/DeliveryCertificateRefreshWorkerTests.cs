using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
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
        var queriesMock = new Mock<IDeliveryCertificateQueries>();
        var storeMock = new Mock<IDeliveryCertificateStore>();
        var orchestratorMock = new Mock<ICertificateOrchestrator>();
        var loggerMock = new Mock<ILogger<DeliveryCertificateRefreshWorker>>();
        var fakeTimeProvider = new FakeTimeProvider();

        var refreshCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selfId = new ChatSelfId(1);
        var relayPeerId = new ChatPeerId(2);

        serviceScopeMock.As<IAsyncDisposable>()
            .Setup(ad => ad.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        serviceScopeMock.Setup(s => s.ServiceProvider)
            .Returns(serviceProviderMock.Object);

        scopeFactoryMock.Setup(sf => sf.CreateScope())
            .Returns(serviceScopeMock.Object);

        serviceProviderMock.Setup(sp => sp.GetService(typeof(IDeliveryCertificateQueries)))
            .Returns(queriesMock.Object);
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IDeliveryCertificateStore)))
            .Returns(storeMock.Object);
        serviceProviderMock.Setup(sp => sp.GetService(typeof(ICertificateOrchestrator)))
            .Returns(orchestratorMock.Object);

        queriesMock.Setup(q => q.GetActiveRelayAssignmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (selfId, relayPeerId) });
        storeMock.Setup(s => s.GetCertificateAsync(selfId, relayPeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeliveryCertificate?)null);
        orchestratorMock.Setup(o => o.RefreshLocalCertificateAsync(It.IsAny<ChatSelfId>(), It.IsAny<ChatPeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => refreshCompletedTcs.SetResult(true));

        var worker = new DeliveryCertificateRefreshWorker(
            scopeFactoryMock.Object,
            loggerMock.Object,
            fakeTimeProvider);

        // ACT
        // Start the BackgroundService
        await worker.StartAsync(CancellationToken.None);

        // Wait for the first refresh to complete
        await refreshCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Stop the service
        await worker.StopAsync(CancellationToken.None);

        // ASSERT
        orchestratorMock.Verify(o => o.RefreshLocalCertificateAsync(selfId, relayPeerId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Test]
    public async Task RefreshesCertificateWhenExpiredOrMissing()
    {
        // ARRANGE
        var serviceProviderMock = new Mock<IServiceProvider>();
        var serviceScopeMock = new Mock<IServiceScope>();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var queriesMock = new Mock<IDeliveryCertificateQueries>();
        var storeMock = new Mock<IDeliveryCertificateStore>();
        var orchestratorMock = new Mock<ICertificateOrchestrator>();
        var loggerMock = new Mock<ILogger<DeliveryCertificateRefreshWorker>>();
        var fakeTimeProvider = new FakeTimeProvider();

        var refreshCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selfId = new ChatSelfId(1);
        var relayPeerId = new ChatPeerId(2);

        serviceScopeMock.As<IAsyncDisposable>()
            .Setup(ad => ad.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        serviceScopeMock.Setup(s => s.ServiceProvider)
            .Returns(serviceProviderMock.Object);

        scopeFactoryMock.Setup(sf => sf.CreateScope())
            .Returns(serviceScopeMock.Object);

        serviceProviderMock.Setup(sp => sp.GetService(typeof(IDeliveryCertificateQueries)))
            .Returns(queriesMock.Object);
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IDeliveryCertificateStore)))
            .Returns(storeMock.Object);
        serviceProviderMock.Setup(sp => sp.GetService(typeof(ICertificateOrchestrator)))
            .Returns(orchestratorMock.Object);

        queriesMock.Setup(q => q.GetActiveRelayAssignmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (selfId, relayPeerId) });
        storeMock.Setup(s => s.GetCertificateAsync(selfId, relayPeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeliveryCertificate?)null);
        orchestratorMock.Setup(o => o.RefreshLocalCertificateAsync(It.IsAny<ChatSelfId>(), It.IsAny<ChatPeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => refreshCompletedTcs.SetResult(true));

        var worker = new DeliveryCertificateRefreshWorker(
            scopeFactoryMock.Object,
            loggerMock.Object,
            fakeTimeProvider);

        // ACT
        await worker.StartAsync(CancellationToken.None);

        await refreshCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None);

        // ASSERT
        orchestratorMock.Verify(o => o.RefreshLocalCertificateAsync(selfId, relayPeerId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Test]
    public async Task SkipsRefreshWhenCertificateIsValid()
    {
        // ARRANGE
        var serviceProviderMock = new Mock<IServiceProvider>();
        var serviceScopeMock = new Mock<IServiceScope>();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var queriesMock = new Mock<IDeliveryCertificateQueries>();
        var storeMock = new Mock<IDeliveryCertificateStore>();
        var orchestratorMock = new Mock<ICertificateOrchestrator>();
        var loggerMock = new Mock<ILogger<DeliveryCertificateRefreshWorker>>();
        var fakeTimeProvider = new FakeTimeProvider();

        var selfId = new ChatSelfId(1);
        var relayPeerId = new ChatPeerId(2);
        var validCert = new DeliveryCertificate(
            DeliveryCertificatePayloadBytes.FromBytes(new byte[40]),
            SignatureBytes.FromBytes(new byte[64]),
            fakeTimeProvider.GetUtcNow() + TimeSpan.FromHours(10));

        serviceScopeMock.As<IAsyncDisposable>()
            .Setup(ad => ad.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        serviceScopeMock.Setup(s => s.ServiceProvider)
            .Returns(serviceProviderMock.Object);

        scopeFactoryMock.Setup(sf => sf.CreateScope())
            .Returns(serviceScopeMock.Object);

        serviceProviderMock.Setup(sp => sp.GetService(typeof(IDeliveryCertificateQueries)))
            .Returns(queriesMock.Object);
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IDeliveryCertificateStore)))
            .Returns(storeMock.Object);
        serviceProviderMock.Setup(sp => sp.GetService(typeof(ICertificateOrchestrator)))
            .Returns(orchestratorMock.Object);

        queriesMock.Setup(q => q.GetActiveRelayAssignmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (selfId, relayPeerId) });
        storeMock.Setup(s => s.GetCertificateAsync(selfId, relayPeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validCert);
        orchestratorMock.Setup(o => o.RefreshLocalCertificateAsync(It.IsAny<ChatSelfId>(), It.IsAny<ChatPeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new DeliveryCertificateRefreshWorker(
            scopeFactoryMock.Object,
            loggerMock.Object,
            fakeTimeProvider);

        // ACT
        using var cts = new CancellationTokenSource();

        await worker.StartAsync(CancellationToken.None);

        // Advance time to complete one cycle
        fakeTimeProvider.Advance(TimeSpan.FromHours(1));

        // Give the worker time to process
        await Task.Delay(100);

        await worker.StopAsync(CancellationToken.None);

        // ASSERT
        orchestratorMock.Verify(o => o.RefreshLocalCertificateAsync(It.IsAny<ChatSelfId>(), It.IsAny<ChatPeerId>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
