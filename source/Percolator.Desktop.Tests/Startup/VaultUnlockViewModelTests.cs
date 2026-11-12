// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Desktop.Tests.Startup;

[TestFixture]
public sealed class VaultUnlockViewModelTests
{
    private Mock<IDatabaseUnlockCoordinator> _unlockCoordinator = null!;
    private Mock<IWindowHandoffCoordinator> _handoff = null!;
    private VaultUnlockViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        _unlockCoordinator = new Mock<IDatabaseUnlockCoordinator>(MockBehavior.Strict);
        _handoff = new Mock<IWindowHandoffCoordinator>(MockBehavior.Strict);
        _vm = new VaultUnlockViewModel(_unlockCoordinator.Object, _handoff.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _vm.Dispose();
    }

    [Test]
    public async Task Unlock_MasterSuccess_TransitionsToGateWithSession()
    {
        var session = new Mock<IAuthenticatedSession>();
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.Success(session.Object));
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
            h => h.TransitionToGateAsync(It.IsAny<IAuthenticatedSession?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _handoff.Verify(
            h => h.TransitionToMainShellAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
    public async Task Unlock_DuressActivated_TransitionsToGateSilently_WithNoSessionOrError()
    {
        _unlockCoordinator
            .Setup(c => c.UnlockAsync(It.IsAny<VaultPassphrase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResult.DuressActivated());
        _handoff
            .Setup(h => h.TransitionToGateAsync(null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.Password = "duress-phrase";
        await _vm.UnlockCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToGateAsync(null, It.IsAny<CancellationToken>()),
            Times.Once,
            "duress must transition to the empty-vault gate with zero error surfacing");
        _vm.ErrorMessage.Should().BeNull("duress activation must never surface error UI");
        _vm.FailedAttemptNonce.Should().Be(0, "duress must not trigger the failed-attempt shake");
        _vm.IsBusy.Should().BeFalse();
    }
}
