using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Application.Session;
using Percolator.Application.Session.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Identities;

namespace Percolator.Desktop.Tests.Windowing;

[TestFixture]
public sealed class WindowHandoffCoordinatorTests
{
    private Mock<IDatabaseUnlockCoordinator> _unlockCoordinator = null!;
    private Mock<Func<StartupHostViewModel>> _startupHostFactory = null!;
    private WindowHandoffCoordinator _coordinator = null!;

    [SetUp]
    public void SetUp()
    {
        _unlockCoordinator = new Mock<IDatabaseUnlockCoordinator>(MockBehavior.Strict);
        _startupHostFactory = new Mock<Func<StartupHostViewModel>>(MockBehavior.Strict);
        _coordinator = new WindowHandoffCoordinator(
            _unlockCoordinator.Object,
            _startupHostFactory.Object,
            NullLogger<WindowHandoffCoordinator>.Instance);
    }

    [Test]
    public async Task TransitionToMainShellAsync_SeedsActiveIdentitySet_BeforeStartingSession()
    {
        // Arrange
        var publisher = new FakeEventPublisher();
        var activeSet = new ActiveIdentitySet(publisher, NullLogger<ActiveIdentitySet>.Instance);
        var personaId = PublicIdentityId.New();
        var lease = new Mock<IPortLease>();
        lease.SetupGet(l => l.Port).Returns(5100);

        var personas = new List<PersonaLaunchDescriptor>
        {
            new(personaId, "Alice", 5100, lease.Object)
        };
        var exchanges = Array.Empty<ExchangeLaunchDescriptor>();

        var callOrder = new List<string>();
        var session = new Mock<IAuthenticatedSession>(MockBehavior.Strict);

        var serviceProvider = new Mock<IServiceProvider>(MockBehavior.Strict);
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IActiveIdentitySet)))
            .Returns(activeSet);

        session.SetupGet(s => s.Services).Returns(serviceProvider.Object);
        session
            .Setup(s => s.StartAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                callOrder.Add("StartAsync");
                // Invariant: active set must ALREADY contain the persona at the moment StartAsync executes
                activeSet.Contains(personaId).Should().BeTrue("active set must be seeded before background workers launch");
            })
            .Returns(Task.CompletedTask);

        // Act
        await _coordinator.TransitionToMainShellAsync(session.Object, personas, exchanges);

        // Assert
        activeSet.Current.Should().Contain(personaId);
        callOrder.Should().ContainSingle().Which.Should().Be("StartAsync");
    }

    [Test]
    public void TransitionToMainShellAsync_NullArguments_ThrowsArgumentNullException()
    {
        var session = new Mock<IAuthenticatedSession>();
        var personas = Array.Empty<PersonaLaunchDescriptor>();
        var exchanges = Array.Empty<ExchangeLaunchDescriptor>();

        Assert.ThrowsAsync<ArgumentNullException>(() =>
            _coordinator.TransitionToMainShellAsync(null!, personas, exchanges));

        Assert.ThrowsAsync<ArgumentNullException>(() =>
            _coordinator.TransitionToMainShellAsync(session.Object, null!, exchanges));

        Assert.ThrowsAsync<ArgumentNullException>(() =>
            _coordinator.TransitionToMainShellAsync(session.Object, personas, null!));
    }

    [Test]
    public async Task LockSessionAsync_AwaitsUnlockCoordinatorLock_AndDisposesLeases()
    {
        // Arrange
        var publisher = new FakeEventPublisher();
        var activeSet = new ActiveIdentitySet(publisher, NullLogger<ActiveIdentitySet>.Instance);
        var personaId = PublicIdentityId.New();
        var personaLease = new Mock<IPortLease>(MockBehavior.Strict);
        personaLease.Setup(l => l.Dispose());
        personaLease.SetupGet(l => l.Port).Returns(5100);

        var exchangeLease = new Mock<IPortLease>(MockBehavior.Strict);
        exchangeLease.Setup(l => l.Dispose());
        exchangeLease.SetupGet(l => l.Port).Returns(5200);

        var personas = new List<PersonaLaunchDescriptor>
        {
            new(personaId, "Alice", 5100, personaLease.Object)
        };
        var exchanges = new List<ExchangeLaunchDescriptor>
        {
            new(Guid.NewGuid(), "Relay", 5200, exchangeLease.Object)
        };

        var session = new Mock<IAuthenticatedSession>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IActiveIdentitySet))).Returns(activeSet);
        session.SetupGet(s => s.Services).Returns(serviceProvider.Object);
        session.Setup(s => s.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _unlockCoordinator
            .Setup(u => u.LockAsync())
            .Returns(Task.CompletedTask);

        await _coordinator.TransitionToMainShellAsync(session.Object, personas, exchanges);

        // Act
        await _coordinator.LockSessionAsync();

        // Assert
        personaLease.Verify(l => l.Dispose(), Times.Once);
        exchangeLease.Verify(l => l.Dispose(), Times.Once);
        _unlockCoordinator.Verify(u => u.LockAsync(), Times.Once);
    }

    private sealed class FakeEventPublisher : IEventPublisher
    {
        public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IApplicationEvent
        {
            return ValueTask.CompletedTask;
        }
    }
}
