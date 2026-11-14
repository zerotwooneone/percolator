// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Security.ValueObjects;
using IPortReservationService = Percolator.Desktop.Common.Services.IPortReservationService;

namespace Percolator.Desktop.Tests.Startup;

[TestFixture]
public sealed class VaultUnlockViewModelTests
{
    private Mock<IDatabaseUnlockCoordinator> _unlockCoordinator = null!;
    private Mock<IWindowHandoffCoordinator> _handoff = null!;
    private Mock<IPersonaDirectory> _personas = null!;
    private Mock<IPortReservationService> _ports = null!;
    private VaultUnlockViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        _unlockCoordinator = new Mock<IDatabaseUnlockCoordinator>(MockBehavior.Strict);
        _handoff = new Mock<IWindowHandoffCoordinator>(MockBehavior.Strict);
        _personas = new Mock<IPersonaDirectory>(MockBehavior.Strict);
        _ports = new Mock<IPortReservationService>(MockBehavior.Strict);
        _vm = new VaultUnlockViewModel(
            _unlockCoordinator.Object, _handoff.Object, _personas.Object, _ports.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _vm.Dispose();
    }

    [Test]
    public async Task Unlock_MasterSuccess_WithoutAutoStart_TransitionsToGateWithSession()
    {
        var session = new Mock<IAuthenticatedSession>();
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
        _personas
            .Setup(d => d.ListAsync(session.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry>
            {
                new(System.Guid.NewGuid(), "Alice", 5100, null, false, 0)
            });
        _handoff
            .Setup(h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "correct";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()),
            Times.Once);
        _vm.ErrorMessage.Should().BeNull();
        _vm.IsBusy.Should().BeFalse();
    }

    [Test]
    public async Task Unlock_InvalidCredentials_SetsInlineError_AndIncrementsShakeNonce()
    {
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.InvalidCredentials());

        var previousNonce = _vm.FailedAttemptNonce;
        _vm.Password = "wrong";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("Incorrect password. Please try again.");
        _vm.FailedAttemptNonce.Should().Be(previousNonce + 1,
            "the view's shake animation triggers off the nonce increment");
        _handoff.Verify(
            h => h.TransitionToGateAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _handoff.Verify(
            h => h.TransitionToMainShellAsync(
                It.IsAny<IAuthenticatedSession>(),
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Unlock_MasterSuccess_WithAutoStart_LeasesPort_AndLaunchesShell()
    {
        var session = new Mock<IAuthenticatedSession>();
        var lease = new Mock<IPortLease>();
        lease.SetupGet(l => l.Port).Returns(5100);
        var persona = new PersonaDirectoryEntry(System.Guid.NewGuid(), "Alice", 5100, null, true, 0);

        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
        _personas
            .Setup(d => d.ListAsync(session.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry> { persona });
        _ports
            .Setup(p => p.AcquireAsync(persona.PreferredPort, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease.Object);
        _handoff
            .Setup(h => h.TransitionToMainShellAsync(
                session.Object,
                It.Is<IReadOnlyList<PersonaLaunchDescriptor>?>(l =>
                    l != null && l.Count == 1 && l[0].Lease == lease.Object && l[0].RequestedPort == 5100),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "correct";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToMainShellAsync(
                session.Object,
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a sole auto-start persona must skip the gate and launch straight into the shell");
        _handoff.Verify(
            h => h.TransitionToGateAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Unlock_MasterSuccess_AutoStartPortUnavailable_FallsBackToGate()
    {
        var session = new Mock<IAuthenticatedSession>();
        var persona = new PersonaDirectoryEntry(System.Guid.NewGuid(), "Alice", 5100, null, true, 0);

        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
        _personas
            .Setup(d => d.ListAsync(session.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry> { persona });
        _ports
            .Setup(p => p.AcquireAsync(persona.PreferredPort, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPortLease?)null);
        _handoff
            .Setup(h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "correct";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()),
            Times.Once,
            "an exhausted port range must surface the gate so the user can see the conflict");
    }

    [Test]
    public async Task ForceGateUnlock_WithAutoStart_SkipsAutoStart_AndTransitionsToGate()
    {
        var session = new Mock<IAuthenticatedSession>();
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
        _handoff
            .Setup(h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "correct";
        await _vm.ForceGateUnlockCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()),
            Times.Once,
            "the Shift-override path must always land on the Identity Gate");
        _personas.Verify(
            d => d.ListAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the Shift-override path must not evaluate auto-start preferences");
    }

    [Test]
    public async Task Unlock_WhileBusy_DoesNothing()
    {
        _vm.Password = "anything";
        _vm.IsBusy = true;

        await _vm.UnlockCommand.ExecuteAsync(null);

        _unlockCoordinator.Verify(
            c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a second unlock must not start while key derivation is in flight");
        _vm.FailedAttemptNonce.Should().Be(0);
    }

    [Test]
    public async Task Unlock_DuressActivated_TransitionsToGateWithDecoySession_NoError()
    {
        var decoySession = new Mock<IAuthenticatedSession>();
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.DuressActivated(decoySession.Object));
        _handoff
            .Setup(h => h.TransitionToGateAsync(decoySession.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "duress-phrase";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToGateAsync(decoySession.Object, It.IsAny<CancellationToken>()),
            Times.Once,
            "duress must transition to the gate with the decoy-vault session so personas persist");
        _vm.ErrorMessage.Should().BeNull("duress activation must never surface error UI");
        _vm.FailedAttemptNonce.Should().Be(0, "duress must not trigger the failed-attempt shake");
        _vm.IsBusy.Should().BeFalse();
    }

    [Test]
    public async Task Unlock_CoordinatorThrows_SurfacesGenericUnlockFailure()
    {
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Decoy vault initialization failed."));

        _vm.Password = "duress-phrase";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("The vault could not be unlocked. Please try again.",
            "a decoy-init failure must surface as a generic unlock failure");
        _vm.FailedAttemptNonce.Should().Be(0,
            "a generic failure must not increment the failed-attempt shake nonce — " +
            "it is indistinguishable from a credential rejection to observers");
        _vm.IsBusy.Should().BeFalse();
        _handoff.Verify(
            h => h.TransitionToGateAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Unlock_AutoStartHandoffThrows_DisposesLease()
    {
        var session = new Mock<IAuthenticatedSession>();
        var lease = new Mock<IPortLease>();
        lease.SetupGet(l => l.Port).Returns(5100);
        var persona = new PersonaDirectoryEntry(System.Guid.NewGuid(), "Alice", 5100, null, true, 0);

        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
        _personas
            .Setup(d => d.ListAsync(session.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry> { persona });
        _ports
            .Setup(p => p.AcquireAsync(persona.PreferredPort, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease.Object);
        _handoff
            .Setup(h => h.TransitionToMainShellAsync(
                session.Object,
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated handoff failure"));

        _vm.Password = "correct";

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _vm.UnlockCommand.ExecuteAsync(null));
        lease.Verify(l => l.Dispose(), Times.Once,
            "a failed handoff must release the auto-start lease so no bound socket lingers");
    }

    [Test]
    public async Task Unlock_TwoAutoStartPersonas_FallsBackToGate()
    {
        var session = new Mock<IAuthenticatedSession>();
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
        _personas
            .Setup(d => d.ListAsync(session.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry>
            {
                new(System.Guid.NewGuid(), "Alice", 5100, null, true, 0),
                new(System.Guid.NewGuid(), "Bob", 5101, null, true, 1)
            });
        _handoff
            .Setup(h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "correct";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()),
            Times.Once,
            "auto-start requires exactly one flagged persona — two must surface the gate");
        _ports.Verify(
            p => p.AcquireAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Unlock_AutoStart_PrefersLastBoundPortOverPreferred()
    {
        var session = new Mock<IAuthenticatedSession>();
        var lease = new Mock<IPortLease>();
        lease.SetupGet(l => l.Port).Returns(5101);
        // LastBoundPort=5101 was persisted by the previous launch — auto-start should try it first.
        var persona = new PersonaDirectoryEntry(System.Guid.NewGuid(), "Alice", 5100, 5101, true, 0);

        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
        _personas
            .Setup(d => d.ListAsync(session.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry> { persona });
        _ports
            .Setup(p => p.AcquireAsync(5101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease.Object);
        _handoff
            .Setup(h => h.TransitionToMainShellAsync(
                session.Object,
                It.Is<IReadOnlyList<PersonaLaunchDescriptor>?>(l =>
                    l != null && l.Count == 1 && l[0].RequestedPort == 5101),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "correct";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _ports.Verify(
            p => p.AcquireAsync(5101, It.IsAny<CancellationToken>()),
            Times.Once,
            "the last successfully bound port takes precedence over the configured preference");
    }
}
