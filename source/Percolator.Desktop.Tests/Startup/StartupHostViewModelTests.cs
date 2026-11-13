// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Vault;

namespace Percolator.Desktop.Tests.Startup;

[TestFixture]
public sealed class StartupHostViewModelTests
{
    private Mock<IDatabaseUnlockCoordinator> _unlockCoordinator = null!;
    private Mock<INativeCryptoProbe> _cryptoProbe = null!;
    private Mock<IHostApplicationLifetime> _appLifetime = null!;
    private Mock<IWindowHandoffCoordinator> _handoff = null!;
    private Mock<IDialogService> _dialogs = null!;
    private Mock<ILauncherService> _launcher = null!;
    private Mock<IPersonaDirectory> _personas = null!;
    private Mock<IExchangeDirectory> _exchanges = null!;
    private Mock<IPortReservationService> _ports = null!;
    private Mock<IDesktopSettingsRepository> _settings = null!;
    private Mock<IIdenticonGenerator> _identicons = null!;
    private Mock<IWindowManager> _windows = null!;
    private VaultStorageOptions _storageOptions = null!;

    [SetUp]
    public void SetUp()
    {
        _unlockCoordinator = new Mock<IDatabaseUnlockCoordinator>(MockBehavior.Strict);
        _cryptoProbe = new Mock<INativeCryptoProbe>(MockBehavior.Strict);
        _appLifetime = new Mock<IHostApplicationLifetime>(MockBehavior.Strict);
        _handoff = new Mock<IWindowHandoffCoordinator>(MockBehavior.Strict);
        _dialogs = new Mock<IDialogService>(MockBehavior.Strict);
        _launcher = new Mock<ILauncherService>(MockBehavior.Strict);
        _personas = new Mock<IPersonaDirectory>(MockBehavior.Strict);
        _exchanges = new Mock<IExchangeDirectory>(MockBehavior.Strict);
        _ports = new Mock<IPortReservationService>(MockBehavior.Strict);
        _settings = new Mock<IDesktopSettingsRepository>(MockBehavior.Strict);
        _identicons = new Mock<IIdenticonGenerator>(MockBehavior.Strict);
        _windows = new Mock<IWindowManager>(MockBehavior.Strict);
        _appLifetime.Setup(a => a.StopApplication());

        _cryptoProbe.Setup(p => p.TestConnection()).Returns(42);
        _personas
            .Setup(d => d.ListAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PersonaDirectoryEntry>());
        _exchanges
            .Setup(e => e.ListAsync(It.IsAny<IAuthenticatedSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExchangeDirectoryEntry>());
        _settings
            .Setup(s => s.GetAppSettingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _storageOptions = new VaultStorageOptions
        {
            BaseDirectory = Path.Combine(Path.GetTempPath(), "percolator-tests", Guid.NewGuid().ToString("N"))
        };
    }

    private IdentityGateViewModel CreateGateVm() => new(
        _personas.Object,
        _exchanges.Object,
        _ports.Object,
        _settings.Object,
        _identicons.Object,
        _handoff.Object,
        _windows.Object,
        NullLogger<IdentityGateViewModel>.Instance);

    private StartupHostViewModel CreateVm() => new(
        _unlockCoordinator.Object,
        _cryptoProbe.Object,
        _storageOptions,
        () => new VaultSetupViewModel(_unlockCoordinator.Object, _handoff.Object, _dialogs.Object),
        () => new VaultUnlockViewModel(
            _unlockCoordinator.Object, _handoff.Object, _personas.Object, _ports.Object),
        CreateGateVm,
        _appLifetime.Object,
        _dialogs.Object,
        _launcher.Object,
        NullLogger<StartupHostViewModel>.Instance);

    [Test]
    public async Task Initialize_UninitializedVault_ShowsSetupScreen()
    {
        _unlockCoordinator
            .Setup(c => c.DetermineStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DatabaseSetupStatus.Uninitialized);

        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.CurrentScreen.Should().Be(StartupScreen.VaultSetup);
        vm.CurrentViewModel.Should().BeOfType<VaultSetupViewModel>();
    }

    [Test]
    public async Task Initialize_LockedVault_ShowsUnlockScreen()
    {
        _unlockCoordinator
            .Setup(c => c.DetermineStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DatabaseSetupStatus.Locked);

        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.CurrentScreen.Should().Be(StartupScreen.VaultUnlock);
        vm.CurrentViewModel.Should().BeOfType<VaultUnlockViewModel>();
    }

    [Test]
    public async Task Initialize_CryptoEngineFailure_ShowsErrorModal_AndStopsApp()
    {
        _cryptoProbe.Setup(p => p.TestConnection()).Returns(-1);

        var shownRequest = (DialogRequest?)null;
        _dialogs
            .Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DialogRequest, CancellationToken>((r, _) => shownRequest = r)
            .ReturnsAsync(DialogResult.None);

        var vm = CreateVm();
        await vm.InitializeAsync();

        shownRequest.Should().NotBeNull();
        shownRequest!.Title.Should().Be("Security Engine Error");
        shownRequest.PrimaryButtonText.Should().Be("View Diagnostic Log");
        _appLifetime.Verify(a => a.StopApplication(), Times.Once);
        vm.CurrentScreen.Should().Be(StartupScreen.None, "no screen should appear after a terminal crypto failure");
    }

    [Test]
    public async Task Initialize_DamagedHeader_ShowsVaultDamagedModal()
    {
        // Coordinator reports Damaged when the header exists on disk but cannot be parsed.
        _unlockCoordinator
            .Setup(c => c.DetermineStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DatabaseSetupStatus.Damaged);

        var shownRequest = (DialogRequest?)null;
        _dialogs
            .Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DialogRequest, CancellationToken>((r, _) => shownRequest = r)
            .ReturnsAsync(DialogResult.Primary);
        _launcher
            .Setup(l => l.OpenDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var vm = CreateVm();
        await vm.InitializeAsync();

        shownRequest.Should().NotBeNull();
        shownRequest!.Title.Should().Be("Vault Data Damaged");
        shownRequest.PrimaryButtonText.Should().Be("Open Data Folder");
        _launcher.Verify(
            l => l.OpenDirectoryAsync(_storageOptions.BaseDirectory, It.IsAny<CancellationToken>()),
            Times.Once,
            "choosing 'Open Data Folder' must launch the vault directory before exit");
        _appLifetime.Verify(a => a.StopApplication(), Times.Once);
        vm.CurrentScreen.Should().Be(StartupScreen.None,
            "a damaged vault must never reach first-time setup (which would overwrite it)");
    }

    [Test]
    public async Task NavigateToGate_ShowsIdentityGate_AndParksSession()
    {
        var session = new Mock<IAuthenticatedSession>();
        var vm = CreateVm();

        await vm.NavigateToGateAsync(session.Object);

        vm.CurrentScreen.Should().Be(StartupScreen.IdentityGate);
        vm.CurrentViewModel.Should().BeOfType<IdentityGateViewModel>();
        vm.PendingSession.Should().BeSameAs(session.Object,
            "the authenticated session must be parked for the persona launch handoff");
        _personas.Verify(
            d => d.ListAsync(session.Object, It.IsAny<CancellationToken>()),
            Times.Once,
            "gate navigation must load personas against the authenticated session");
    }

    [Test]
    public async Task NavigateToUnlock_ClearsParkedSession_AndReturnsToUnlockScreen()
    {
        var session = new Mock<IAuthenticatedSession>();
        var vm = CreateVm();
        await vm.NavigateToGateAsync(session.Object);

        vm.NavigateToUnlock();

        vm.CurrentScreen.Should().Be(StartupScreen.VaultUnlock);
        vm.CurrentViewModel.Should().BeOfType<VaultUnlockViewModel>();
        vm.PendingSession.Should().BeNull(
            "the relock path must drop the parked session before returning to the unlock prompt");
    }
}
