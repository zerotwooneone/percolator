// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using IPortReservationService = Percolator.Desktop.Common.Services.IPortReservationService;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Tests.Startup;

[TestFixture]
public sealed class IdentityGateViewModelTests
{
    private IAuthenticatedSession _session = null!;

    private Mock<IPersonaDirectory> _directory = null!;
    private Mock<IExchangeDirectory> _exchanges = null!;
    private Mock<IPortReservationService> _ports = null!;
    private Mock<IDesktopSettingsRepository> _settings = null!;
    private readonly IIdenticonGenerator _identicons = new IdenticonGenerator();
    private Mock<IWindowHandoffCoordinator> _handoff = null!;
    private Mock<IWindowManager> _windows = null!;
    private IdentityGateViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        _session = Mock.Of<IAuthenticatedSession>();
        _directory = new Mock<IPersonaDirectory>(MockBehavior.Strict);
        _exchanges = new Mock<IExchangeDirectory>(MockBehavior.Strict);
        _ports = new Mock<IPortReservationService>(MockBehavior.Strict);
        _settings = new Mock<IDesktopSettingsRepository>(MockBehavior.Strict);
        _handoff = new Mock<IWindowHandoffCoordinator>(MockBehavior.Strict);
        _windows = new Mock<IWindowManager>(MockBehavior.Strict);

        // Defaults exercised by every LoadAsync/launch — individual tests override.
        _exchanges
            .Setup(e => e.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExchangeDirectoryEntry>());
        _exchanges
            .Setup(e => e.ProvisionLinkedMailboxesAsync(
                It.IsAny<IEnumerable<PublicIdentityId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());
        _settings
            .Setup(s => s.GetAppSettingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _settings
            .Setup(s => s.SetAppSettingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm = new IdentityGateViewModel(
            _directory.Object,
            _exchanges.Object,
            _ports.Object,
            _settings.Object,
            _identicons,
            _handoff.Object,
            _windows.Object,
            NullLogger<IdentityGateViewModel>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _vm.Dispose();
        await _session.DisposeAsync();
    }

    // The synchronized view implements both IList and IList<T>; go through the generic
    // interface so Count/indexer are unambiguous.
    private IList<SelectablePersonaModel> Personas =>
        (IList<SelectablePersonaModel>)_vm.Personas;

    private static PersonaDirectoryEntry Entry(
        string name, int port, bool autoStart = false, int? lastBound = null) =>
        new(Guid.NewGuid(), name, port, lastBound, autoStart, 0);

    private void ArrangeDirectory(params PersonaDirectoryEntry[] entries) =>
        _directory
            .Setup(d => d.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry>(entries));

    [Test]
    public async Task LoadAsync_PopulatesPersonas_AndProbesPreferredPort()
    {
        var entry = Entry("Alice", 5100);
        ArrangeDirectory(entry);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));

        await _vm.LoadAsync(_session);

        Personas.Count.Should().Be(1);
        Personas[0].DisplayName.Should().Be("Alice");
        Personas[0].PortStatus.Should().Be(PortStatus.Ready);
        Personas[0].EffectivePort.Should().Be(5100);
        _vm.HasPersonas.Should().BeTrue();
        _vm.HasEmptyState.Should().BeFalse();
        _vm.IsLoading.Should().BeFalse();
    }

    [Test]
    public async Task LoadAsync_EmptyDirectory_ShowsEmptyState()
    {
        ArrangeDirectory();

        await _vm.LoadAsync(_session);

        _vm.HasPersonas.Should().BeFalse();
        _vm.HasEmptyState.Should().BeTrue();
        Personas.Count.Should().Be(0);
    }

    [Test]
    public async Task LoadAsync_PortConflict_MarksCardAutoSelected()
    {
        ArrangeDirectory(Entry("Alice", 5100));
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.AutoSelected, 5101));

        await _vm.LoadAsync(_session);

        var card = Personas[0];
        card.PortStatus.Should().Be(PortStatus.AutoSelected);
        card.EffectivePort.Should().Be(5101);
        card.PortPillText.Should().Contain("5100").And.Contain("5101");
    }

    [Test]
    public async Task LaunchPersonas_AcquiresLease_PersistsBoundPort_AndTransitions()
    {
        var entry = Entry("Alice", 5100);
        ArrangeDirectory(entry);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        await _vm.LoadAsync(_session);

        var card = Personas[0];
        var lease = new Mock<IPortLease>();
        lease.SetupGet(l => l.Port).Returns(5100);
        _ports
            .Setup(p => p.AcquireAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease.Object);
        _settings
            .Setup(s => s.GetAsync(entry.IdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopIdentitySettingsEntity?)null);
        _settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _handoff
            .Setup(h => h.TransitionToMainShellAsync(
                _session,
                It.Is<IReadOnlyList<PersonaLaunchDescriptor>?>(l =>
                    l != null && l.Count == 1 && l[0].Lease == lease.Object && l[0].RequestedPort == 5100),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _vm.LaunchPersonasCommand.CanExecute(null).Should().BeFalse(
            "launch must stay disabled until a persona is checked");
        card.IsSelected = true;

        await _vm.LaunchPersonasCommand.ExecuteAsync(null);

        _handoff.Verify(
            h => h.TransitionToMainShellAsync(
                _session,
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _settings.Verify(
            s => s.SaveAsync(
                It.Is<DesktopIdentitySettingsEntity>(e =>
                    e.IdentityId == entry.IdentityId && e.LastBoundPort == 5100),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the bound port must persist so the next launch prefers it");
        lease.Verify(l => l.Dispose(), Times.Never,
            "lease ownership transfers to the handoff coordinator for the session lifetime");
    }

    [Test]
    public async Task LaunchPersonas_LeaseLandsOnOtherPort_SurfacesRecoveryBanner()
    {
        ArrangeDirectory(Entry("Alice", 5100));
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        await _vm.LoadAsync(_session);

        var card = Personas[0];
        var lease = new Mock<IPortLease>();
        lease.SetupGet(l => l.Port).Returns(5101);
        _ports
            .Setup(p => p.AcquireAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease.Object);
        _settings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopIdentitySettingsEntity?)null);
        _settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _handoff
            .Setup(h => h.TransitionToMainShellAsync(
                _session,
                It.Is<IReadOnlyList<PersonaLaunchDescriptor>?>(l =>
                    l != null && l.Count == 1 && l[0].RequestedPort == 5101),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        card.IsSelected = true;
        await _vm.LaunchPersonasCommand.ExecuteAsync(null);

        card.EffectivePort.Should().Be(5101);
        card.PortStatus.Should().Be(PortStatus.AutoSelected);
        _vm.BannerMessage.Should().NotBeNullOrEmpty(
            "a port claimed between probe and launch must surface an inline recovery banner");
    }

    [Test]
    public async Task LaunchPersonas_RangeExhausted_MarksUnavailable_WithoutTransition()
    {
        ArrangeDirectory(Entry("Alice", 5100));
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        await _vm.LoadAsync(_session);

        var card = Personas[0];
        _ports
            .Setup(p => p.AcquireAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPortLease?)null);

        card.IsSelected = true;
        await _vm.LaunchPersonasCommand.ExecuteAsync(null);

        card.PortStatus.Should().Be(PortStatus.Unavailable);
        _vm.BannerMessage.Should().NotBeNullOrEmpty();
        _handoff.Verify(
            h => h.TransitionToMainShellAsync(
                It.IsAny<IAuthenticatedSession>(),
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _vm.LaunchPersonasCommand.CanExecute(null).Should().BeFalse(
            "unresolved conflicts must keep the launch button disabled");
    }

    [Test]
    public async Task AutoStartToggle_OnSingleSelection_PersistsFlag()
    {
        var entry = Entry("Alice", 5100);
        ArrangeDirectory(entry);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        await _vm.LoadAsync(_session);

        _settings
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DesktopIdentitySettingsEntity>
            {
                new(entry.IdentityId, 5100, null, false, 0)
            });
        var saves = new List<DesktopIdentitySettingsEntity>();
        _settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Callback<DesktopIdentitySettingsEntity, CancellationToken>((e, _) => saves.Add(e))
            .Returns(Task.CompletedTask);

        Personas[0].IsSelected = true;
        _vm.IsAutoStartApplicable.Should().BeTrue();
        _vm.AutoStartRequested = true;

        // The persist pipeline is async (ObservePropertyChanged → SelectAwait) — wait for the
        // condition rather than a fixed delay.
        SpinWait.SpinUntil(
                () => saves.Any(e => e.IdentityId == entry.IdentityId && e.AutoStart),
                TimeSpan.FromSeconds(5))
            .Should().BeTrue(
                "the single-persona auto-start checkbox must persist through the settings store");
        saves.Count(e => e.IdentityId == entry.IdentityId && e.AutoStart).Should().Be(1);
    }

    [Test]
    public async Task DeletePersona_Confirmed_ShredsAndRemovesCard()
    {
        var entry = Entry("Alice", 5100);
        ArrangeDirectory(entry);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        await _vm.LoadAsync(_session);

        var card = Personas[0];
        _windows
            .Setup(w => w.ShowDialogAsync<DeletePersonaDialogViewModel, bool>(
                It.IsAny<Action<DeletePersonaDialogViewModel>?>(),
                It.IsAny<Avalonia.Controls.Window?>()))
            .Callback<Action<DeletePersonaDialogViewModel>?, Avalonia.Controls.Window?>(
                (configure, _) => configure?.Invoke(new DeletePersonaDialogViewModel()))
            .ReturnsAsync(true);
        _directory
            .Setup(d => d.ShredAndDeleteAsync(
                It.Is<PublicIdentityId>(id => id.Value == entry.IdentityId),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _vm.DeletePersonaCommand.ExecuteAsync(card);

        _directory.Verify(
            d => d.ShredAndDeleteAsync(
                It.Is<PublicIdentityId>(id => id.Value == entry.IdentityId),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "deletion must shred the key material, profile, and settings atomically");
        Personas.Count.Should().Be(0);
        _vm.HasEmptyState.Should().BeTrue();
    }

    [Test]
    public async Task LaunchPersonas_SecondAcquireFails_DisposesEarlierLease_WithoutTransition()
    {
        var alice = Entry("Alice", 5100);
        var bob = Entry("Bob", 5101);
        ArrangeDirectory(alice, bob);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        _ports
            .Setup(p => p.ProbeAsync(5101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5101));
        await _vm.LoadAsync(_session);

        var firstCard = Personas[0];
        var secondCard = Personas[1];
        var firstLease = new Mock<IPortLease>();
        firstLease.SetupGet(l => l.Port).Returns(5100);
        _ports
            .Setup(p => p.AcquireAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstLease.Object);
        _ports
            .Setup(p => p.AcquireAsync(5101, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPortLease?)null);
        _settings
            .Setup(s => s.GetAsync(alice.IdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopIdentitySettingsEntity?)null);
        _settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        firstCard.IsSelected = true;
        secondCard.IsSelected = true;
        await _vm.LaunchPersonasCommand.ExecuteAsync(null);

        firstLease.Verify(l => l.Dispose(), Times.Once,
            "a lease acquired for a persona that cannot launch must be released");
        secondCard.PortStatus.Should().Be(PortStatus.Unavailable);
        _handoff.Verify(
            h => h.TransitionToMainShellAsync(
                It.IsAny<IAuthenticatedSession>(),
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task LaunchPersonas_HandoffThrows_DisposesAcquiredLeases()
    {
        var entry = Entry("Alice", 5100);
        ArrangeDirectory(entry);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        await _vm.LoadAsync(_session);

        var lease = new Mock<IPortLease>();
        lease.SetupGet(l => l.Port).Returns(5100);
        _ports
            .Setup(p => p.AcquireAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease.Object);
        _settings
            .Setup(s => s.GetAsync(entry.IdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopIdentitySettingsEntity?)null);
        _settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _handoff
            .Setup(h => h.TransitionToMainShellAsync(
                It.IsAny<IAuthenticatedSession>(),
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated handoff failure"));

        Personas[0].IsSelected = true;

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _vm.LaunchPersonasCommand.ExecuteAsync(null));
        lease.Verify(l => l.Dispose(), Times.Once,
            "a failed handoff must release leases so no bound socket lingers (stealth invariant)");
    }

    [Test]
    public async Task AutoStartToggle_ClearsFlagOnOtherPersonas()
    {
        var alice = Entry("Alice", 5100);
        var bob = Entry("Bob", 5101);
        ArrangeDirectory(alice, bob);
        _ports
            .Setup(p => p.ProbeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int port, CancellationToken _) =>
                new PortProbeResult(PortProbeStatus.Ready, port));
        await _vm.LoadAsync(_session);

        _settings
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DesktopIdentitySettingsEntity>
            {
                new(alice.IdentityId, 5100, null, false, 0),
                new(bob.IdentityId, 5101, null, true, 1) // Bob already auto-starts
            });
        var saves = new List<DesktopIdentitySettingsEntity>();
        _settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Callback<DesktopIdentitySettingsEntity, CancellationToken>((e, _) => saves.Add(e))
            .Returns(Task.CompletedTask);

        Personas[0].IsSelected = true; // Alice is the single selection
        _vm.AutoStartRequested = true;

        SpinWait.SpinUntil(
                () => saves.Any(e => e.IdentityId == bob.IdentityId && !e.AutoStart),
                TimeSpan.FromSeconds(5))
            .Should().BeTrue(
                "enabling auto-start on one persona must clear the flag on all others (single-flag invariant)");
        saves.Any(e => e.IdentityId == alice.IdentityId && e.AutoStart).Should().BeTrue();
    }

    [Test]
    public async Task ApplyPortOverride_PersistsPreference_AndReprobes()
    {
        var entry = Entry("Alice", 5100);
        ArrangeDirectory(entry);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));
        await _vm.LoadAsync(_session);

        var card = Personas[0];
        card.PortEditorValue = 5150;
        card.IsPortEditorOpen = true;
        _settings
            .Setup(s => s.GetAsync(entry.IdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopIdentitySettingsEntity(entry.IdentityId, 5100, null, false, 0));
        _settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ports
            .Setup(p => p.ProbeAsync(5150, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5150));

        await _vm.ApplyPortOverrideCommand.ExecuteAsync(card);

        _settings.Verify(
            s => s.SaveAsync(
                It.Is<DesktopIdentitySettingsEntity>(e =>
                    e.IdentityId == entry.IdentityId && e.PreferredPort == 5150),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the inline port editor must persist the new preference");
        card.PreferredPort.Should().Be(5150);
        card.EffectivePort.Should().Be(5150);
        card.IsPortEditorOpen.Should().BeFalse();
        card.PortStatus.Should().Be(PortStatus.Ready);
    }

    [Test]
    public async Task CreatePersona_Confirmed_AddsSelectedProbedCard()
    {
        ArrangeDirectory(); // start from empty-vault state
        await _vm.LoadAsync(_session);

        var identityId = Guid.NewGuid();
        var created = new PersonaDirectoryEntry(identityId, "Bob", 5100, null, false, 0);
        _windows
            .Setup(w => w.ShowDialogAsync<CreatePersonaDialogViewModel, PersonaEditorResult?>(
                It.IsAny<Action<CreatePersonaDialogViewModel>?>(),
                It.IsAny<Avalonia.Controls.Window?>()))
            .ReturnsAsync(new PersonaEditorResult(new PublicIdentityId(identityId), "Bob", 5100));
        _directory
            .Setup(d => d.CreateAsync(
                It.Is<PublicIdentityId>(id => id.Value == identityId),
                "Bob",
                5100,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        _ports
            .Setup(p => p.ProbeAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortProbeResult(PortProbeStatus.Ready, 5100));

        await _vm.CreatePersonaCommand.ExecuteAsync(null);

        _directory.Verify(
            d => d.CreateAsync(
                It.Is<PublicIdentityId>(id => id.Value == identityId),
                "Bob",
                5100,
                It.IsAny<CancellationToken>()),
            Times.Once);
        Personas.Count.Should().Be(1);
        Personas[0].IsSelected.Should().BeTrue("a newly created persona is pre-selected for launch");
        Personas[0].PortStatus.Should().Be(PortStatus.Ready);
        _vm.HasEmptyState.Should().BeFalse();
    }
}
