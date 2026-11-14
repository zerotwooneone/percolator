// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Desktop.Tests.Startup;

[TestFixture]
public sealed class VaultSetupViewModelTests
{
    private Mock<IDatabaseUnlockCoordinator> _unlockCoordinator = null!;
    private Mock<IWindowHandoffCoordinator> _handoff = null!;
    private Mock<IDialogService> _dialogs = null!;
    private VaultSetupViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        _unlockCoordinator = new Mock<IDatabaseUnlockCoordinator>(MockBehavior.Strict);
        _handoff = new Mock<IWindowHandoffCoordinator>(MockBehavior.Strict);
        _dialogs = new Mock<IDialogService>(MockBehavior.Strict);
        _vm = new VaultSetupViewModel(_unlockCoordinator.Object, _handoff.Object, _dialogs.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _vm.Dispose();
    }

    [Test]
    public async Task Continue_WhitespaceOnlyMaster_ShowsValidationError_WithoutInitializing()
    {
        _vm.MasterPassword = "   ";
        _vm.ConfirmPassword = "   ";

        await _vm.ContinueCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Contain("whitespace");
        _unlockCoordinator.Verify(
            c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Continue_ConfirmationMismatch_ShowsError_WithoutInitializing()
    {
        _vm.MasterPassword = "correct horse";
        _vm.ConfirmPassword = "different";

        await _vm.ContinueCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Contain("do not match");
        _unlockCoordinator.Verify(
            c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Continue_DuressEqualsMaster_ShowsError_WithoutInitializing()
    {
        _vm.MasterPassword = "shared-secret";
        _vm.ConfirmPassword = "shared-secret";
        _vm.DuressPassword = "shared-secret";

        await _vm.ContinueCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Contain("cannot match");
        _unlockCoordinator.Verify(
            c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Continue_WhitespaceOnlyDuress_ShowsValidationError_WithoutInitializing()
    {
        _vm.MasterPassword = "pw";
        _vm.ConfirmPassword = "pw";
        _vm.DuressPassword = "   ";

        await _vm.ContinueCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Contain("whitespace");
        _unlockCoordinator.Verify(
            c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Continue_WhileBusy_DoesNothing()
    {
        _vm.MasterPassword = "pw";
        _vm.ConfirmPassword = "pw";
        _vm.IsBusy = true;

        await _vm.ContinueCommand.ExecuteAsync(null);

        _dialogs.Verify(
            d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _unlockCoordinator.Verify(
            c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Continue_BlankPassword_PromptsConfirmation_AndCancelsWhenDeclined()
    {
        var promptShown = false;
        _dialogs
            .Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => promptShown = true)
            .ReturnsAsync(DialogResult.None);

        await _vm.ContinueCommand.ExecuteAsync(null);

        promptShown.Should().BeTrue("an empty master password requires explicit confirmation");
        _unlockCoordinator.Verify(
            c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Continue_BlankPassword_InitializesAfterConfirmation_AndTransitionsToGate()
    {
        var session = new Mock<IAuthenticatedSession>();
        _unlockCoordinator
            .Setup(c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<IAuthenticatedSession>.Success(session.Object));
        _handoff
            .Setup(h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _dialogs
            .Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DialogResult.Primary);

        await _vm.ContinueCommand.ExecuteAsync(null);

        _unlockCoordinator.Verify(
            c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _handoff.Verify(
            h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Continue_ValidInput_PassesDuressToCoordinator_AndTransitionsToGate()
    {
        var session = new Mock<IAuthenticatedSession>();
        VaultPassphrase? capturedDuress = null;
        _unlockCoordinator
            .Setup(c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()))
            .Callback<VaultPassphrase, VaultPassphrase?, CancellationToken>((_, d, _) => capturedDuress = d)
            .ReturnsAsync(DomainResult<IAuthenticatedSession>.Success(session.Object));
        _handoff
            .Setup(h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.MasterPassword = "correct horse battery staple";
        _vm.ConfirmPassword = "correct horse battery staple";
        _vm.IsDuressExpanded = true;
        _vm.DuressPassword = "decoy-phrase";

        await _vm.ContinueCommand.ExecuteAsync(null);

        capturedDuress.Should().NotBeNull("an expanded non-empty duress field must be forwarded to the coordinator");
        _handoff.Verify(
            h => h.TransitionToGateAsync(session.Object, It.IsAny<CancellationToken>()),
            Times.Once);
        _vm.IsBusy.Should().BeFalse();
    }

    [Test]
    public async Task Continue_InitializeFails_SurfacesErrorMessage()
    {
        _unlockCoordinator
            .Setup(c => c.InitializeAsync(It.IsAny<VaultPassphrase>(), It.IsAny<VaultPassphrase?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<IAuthenticatedSession>.Failure(
                new DomainError("Vault.CanaryFailed", "Canary probe failed")));

        _vm.MasterPassword = "pw";
        _vm.ConfirmPassword = "pw";

        await _vm.ContinueCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("Canary probe failed");
        _handoff.Verify(
            h => h.TransitionToGateAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
