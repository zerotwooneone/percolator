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
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Tests.Startup;

/// <summary>
/// State-based tests for the Phase 7 exchange surface: tile overflow, wizard defaults,
/// manage-surface link/unlink, and the gate's onboarding + launch-provisioning behavior.
/// </summary>
[TestFixture]
public sealed class ExchangeViewModelTests
{
    private readonly IIdenticonGenerator _identicons = new IdenticonGenerator();

    private static AvatarPresentation Avatar() =>
        AvatarPresentation.FromIdenticon(new IdenticonGenerator().Generate(Guid.NewGuid()));

    private static ExchangeDirectoryEntry Entry(
        string name,
        bool selfHosted = true,
        int port = 5200,
        params Guid[] linkedPersonas) =>
        new(Guid.NewGuid(), name, $"127.0.0.1:{port}", port, selfHosted,
            linkedPersonas.ToList());

    private static PersonaDirectoryEntry Persona(string name) =>
        new(Guid.NewGuid(), name, 5100, null, false, 0);

    // ---- Tile ----

    [Test]
    public void ExchangeTile_CapsAvatarStripAtFive_WithOverflowCount()
    {
        var entry = Entry("Hub", linkedPersonas: Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray());
        var chips = Enumerable.Range(0, 7)
            .Select(i => new PersonaAvatarChip(Guid.NewGuid(), $"P{i}", Avatar()))
            .ToList();

        var tile = new ExchangeTileViewModel(entry, chips, Avatar());

        tile.VisibleLinkedPersonas.Count.Should().Be(5, "the strip renders at most five avatars");
        tile.OverflowLinkedPersonaCount.Should().Be(2, "the remainder rolls into the +N chip");
        tile.HasLinkedPersonas.Should().BeTrue();
        tile.HasLinkedOverflow.Should().BeTrue();
    }

    [Test]
    public void ExchangeTile_FiveOrFewerLinked_NoOverflow()
    {
        var entry = Entry("Hub");
        var chips = Enumerable.Range(0, 5)
            .Select(i => new PersonaAvatarChip(Guid.NewGuid(), $"P{i}", Avatar()))
            .ToList();

        var tile = new ExchangeTileViewModel(entry, chips, Avatar());

        tile.VisibleLinkedPersonas.Count.Should().Be(5);
        tile.OverflowLinkedPersonaCount.Should().Be(0);
        tile.HasLinkedOverflow.Should().BeFalse();
        tile.IsSelfHosted.Should().BeTrue();
    }

    // ---- Creation wizard ----

    [Test]
    public void Wizard_PersonaCheckboxes_DefaultUnchecked()
    {
        var personas = new[] { Persona("Alice"), Persona("Bob"), Persona("Carol") };
        var wizard = new ExchangeCreationViewModel(_identicons);
        wizard.Initialize(personas);

        wizard.CurrentStep.Should().Be(0);
        wizard.FramingMessage.Should().Contain("only open while Percolator is unlocked");
        wizard.LinkablePersonas.Count.Should().Be(3);
        wizard.LinkablePersonas.Should().OnlyContain(p => !p.IsLinked,
            "linking is explicit opt-in — every checkbox ships unchecked");
    }

    [Test]
    public void Wizard_Navigation_ProfileGateAndFinishResult()
    {
        var personas = new[] { Persona("Alice"), Persona("Bob") };
        var wizard = new ExchangeCreationViewModel(_identicons);
        wizard.Initialize(personas);
        var closed = false;
        wizard.RequestClose += (_, _) => closed = true;

        wizard.NextCommand.CanExecute(null).Should().BeTrue("framing step advances freely");
        wizard.NextCommand.Execute(null);
        wizard.CurrentStep.Should().Be(1);

        wizard.NextCommand.CanExecute(null).Should().BeFalse(
            "an untouched empty display name must block the profile step");

        wizard.DisplayName = "My Post-Office";
        wizard.NextCommand.Execute(null);
        wizard.CurrentStep.Should().Be(2);

        wizard.LinkablePersonas[1].IsLinked = true;
        wizard.FinishCommand.Execute(null);

        closed.Should().BeTrue();
        wizard.Result.Should().NotBeNull();
        wizard.Result!.DisplayName.Should().Be("My Post-Office");
        wizard.Result.LinkedPersonaIds.Should().ContainSingle()
            .Which.Value.Should().Be(personas[1].IdentityId);
    }

    [Test]
    public void Wizard_FinishFromProfileStep_SkipsLinking()
    {
        var wizard = new ExchangeCreationViewModel(_identicons);
        wizard.Initialize(new[] { Persona("Alice") });
        wizard.NextCommand.Execute(null);
        wizard.DisplayName = "Solo Exchange";
        wizard.FinishCommand.Execute(null);

        wizard.Result.Should().NotBeNull();
        wizard.Result!.LinkedPersonaIds.Should().BeEmpty(
            "an exchange can launch with zero linked personas");
    }

    // ---- Manage surface ----

    private static (
        ExchangeManageViewModel vm,
        Mock<IExchangeDirectory> exchanges,
        Mock<IWindowManager> windows) ManageFixture(
            IReadOnlyList<PersonaDirectoryEntry> personas,
            ExchangeTileViewModel tile,
            IAuthenticatedSession session)
    {
        var exchanges = new Mock<IExchangeDirectory>(MockBehavior.Strict);
        var windows = new Mock<IWindowManager>(MockBehavior.Strict);
        var vm = new ExchangeManageViewModel(
            exchanges.Object,
            windows.Object,
            new IdenticonGenerator(),
            NullLogger<ExchangeManageViewModel>.Instance);
        vm.Initialize(session, tile, personas);
        return (vm, exchanges, windows);
    }

    [Test]
    public async Task Manage_CheckingPersona_LinksImmediately()
    {
        var session = Mock.Of<IAuthenticatedSession>();
        var personas = new[] { Persona("Alice"), Persona("Bob") };
        var entry = Entry("Hub", linkedPersonas: personas[0].IdentityId);
        var tile = new ExchangeTileViewModel(entry, Array.Empty<PersonaAvatarChip>(), Avatar());
        var (vm, exchanges, _) = ManageFixture(personas, tile, session);

        vm.Personas[0].IsLinked.Should().BeTrue("already-linked personas arrive checked");
        vm.Personas[1].IsLinked.Should().BeFalse();
        vm.IsSelfHosted.Should().BeTrue();

        exchanges
            .Setup(e => e.LinkPersonaAsync(
                session,
                It.Is<PublicIdentityId>(r => r.Value == tile.RelayIdentityId),
                It.Is<PublicIdentityId>(p => p.Value == personas[1].IdentityId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        vm.Personas[1].IsLinked = true;
        await vm.TogglePersonaCommand.ExecutionTask!;

        exchanges.Verify(e => e.LinkPersonaAsync(
                session,
                It.IsAny<PublicIdentityId>(),
                It.Is<PublicIdentityId>(p => p.Value == personas[1].IdentityId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        vm.Personas[1].IsLinked.Should().BeTrue();
    }

    [Test]
    public async Task Manage_UncheckingConfirmed_UnlinksWithPurge_WhenSelfHosted()
    {
        var session = Mock.Of<IAuthenticatedSession>();
        var personas = new[] { Persona("Alice") };
        var entry = Entry("Hub", linkedPersonas: personas[0].IdentityId);
        var tile = new ExchangeTileViewModel(entry, Array.Empty<PersonaAvatarChip>(), Avatar());
        var (vm, exchanges, windows) = ManageFixture(personas, tile, session);

        windows
            .Setup(w => w.ShowDialogAsync<StopUsingExchangeDialogViewModel, bool>(
                It.IsAny<Action<StopUsingExchangeDialogViewModel>?>(),
                It.IsAny<Avalonia.Controls.Window?>()))
            .Callback<Action<StopUsingExchangeDialogViewModel>?, Avalonia.Controls.Window?>(
                (configure, _) =>
                {
                    var dialog = new StopUsingExchangeDialogViewModel();
                    configure?.Invoke(dialog);
                    dialog.CanPurgeQueue.Should().BeTrue(
                        "self-hosted exchanges expose the local queue purge option");
                    dialog.PurgeQueuedEnvelopes = true;
                })
            .ReturnsAsync(true);
        exchanges
            .Setup(e => e.UnlinkPersonaAsync(
                session,
                It.Is<PublicIdentityId>(r => r.Value == tile.RelayIdentityId),
                It.Is<PublicIdentityId>(p => p.Value == personas[0].IdentityId),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        vm.Personas[0].IsLinked = false;
        await vm.TogglePersonaCommand.ExecutionTask!;

        exchanges.Verify(e => e.UnlinkPersonaAsync(
                session,
                It.IsAny<PublicIdentityId>(),
                It.Is<PublicIdentityId>(p => p.Value == personas[0].IdentityId),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the purge flag must flow to the service when the user opts in");
        vm.Personas[0].IsLinked.Should().BeFalse();
    }

    [Test]
    public async Task Manage_UncheckingCancelled_RevertsCheckbox_WithoutUnlink()
    {
        var session = Mock.Of<IAuthenticatedSession>();
        var personas = new[] { Persona("Alice") };
        var entry = Entry("Hub", selfHosted: false, linkedPersonas: personas[0].IdentityId);
        var tile = new ExchangeTileViewModel(entry, Array.Empty<PersonaAvatarChip>(), Avatar());
        var (vm, exchanges, windows) = ManageFixture(personas, tile, session);

        windows
            .Setup(w => w.ShowDialogAsync<StopUsingExchangeDialogViewModel, bool>(
                It.IsAny<Action<StopUsingExchangeDialogViewModel>?>(),
                It.IsAny<Avalonia.Controls.Window?>()))
            .ReturnsAsync(false);

        vm.Personas[0].IsLinked = false;
        await vm.TogglePersonaCommand.ExecutionTask!;

        exchanges.Verify(e => e.UnlinkPersonaAsync(
                It.IsAny<IAuthenticatedSession>(),
                It.IsAny<PublicIdentityId>(),
                It.IsAny<PublicIdentityId>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        vm.Personas[0].IsLinked.Should().BeTrue(
            "a cancelled revocation must restore the checkbox");
    }

    [Test]
    public async Task Manage_UncheckingNonSelfHosted_NeverOffersQueuePurge()
    {
        var session = Mock.Of<IAuthenticatedSession>();
        var personas = new[] { Persona("Alice") };
        var entry = Entry("Remote", selfHosted: false, linkedPersonas: personas[0].IdentityId);
        var tile = new ExchangeTileViewModel(entry, Array.Empty<PersonaAvatarChip>(), Avatar());
        var (vm, _, windows) = ManageFixture(personas, tile, session);

        windows
            .Setup(w => w.ShowDialogAsync<StopUsingExchangeDialogViewModel, bool>(
                It.IsAny<Action<StopUsingExchangeDialogViewModel>?>(),
                It.IsAny<Avalonia.Controls.Window?>()))
            .Callback<Action<StopUsingExchangeDialogViewModel>?, Avalonia.Controls.Window?>(
                (configure, _) =>
                {
                    var dialog = new StopUsingExchangeDialogViewModel();
                    configure?.Invoke(dialog);
                    dialog.CanPurgeQueue.Should().BeFalse(
                        "remote exchanges never expose a queue purge — their mail is not in this vault");
                })
            .ReturnsAsync(false);

        vm.Personas[0].IsLinked = false;
        await vm.TogglePersonaCommand.ExecutionTask!;
    }

    [Test]
    public async Task Manage_EditProfile_PersistsRenameAndPort()
    {
        var session = Mock.Of<IAuthenticatedSession>();
        var personas = new[] { Persona("Alice") };
        var entry = Entry("Hub", port: 5200);
        var tile = new ExchangeTileViewModel(entry, Array.Empty<PersonaAvatarChip>(), Avatar());
        var (vm, exchanges, windows) = ManageFixture(personas, tile, session);

        CreatePersonaDialogViewModel? dialog = null;
        windows
            .Setup(w => w.ShowDialogAsync<CreatePersonaDialogViewModel, PersonaEditorResult?>(
                It.IsAny<Action<CreatePersonaDialogViewModel>?>(),
                It.IsAny<Avalonia.Controls.Window?>()))
            .Callback<Action<CreatePersonaDialogViewModel>?, Avalonia.Controls.Window?>(
                (configure, _) =>
                {
                    dialog = new CreatePersonaDialogViewModel(new IdenticonGenerator());
                    configure?.Invoke(dialog);
                    dialog.DisplayName = "Renamed Hub";
                    dialog.PreferredPort = 5300;
                    dialog.SaveCommand.Execute(null);
                })
            .ReturnsAsync(() => dialog!.Result);
        exchanges
            .Setup(e => e.UpdateSelfHostedProfileAsync(
                session,
                It.Is<PublicIdentityId>(r => r.Value == tile.RelayIdentityId),
                "Renamed Hub",
                5300,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        await vm.EditProfileCommand.ExecuteAsync(null);

        exchanges.Verify(e => e.UpdateSelfHostedProfileAsync(
                session,
                It.IsAny<PublicIdentityId>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the rename + port must flow to the vault through the directory");
        vm.ExchangeDisplayName.Should().Be("Renamed Hub");
        vm.DoneCommand.Execute(null);
        vm.Result.Should().BeTrue("a saved profile edit must flag the tile for refresh");
    }

    // ---- Gate: onboarding + launch provisioning ----

    private sealed class GateFixture
    {
        public required IAuthenticatedSession Session;
        public required Mock<IPersonaDirectory> Directory;
        public required Mock<IExchangeDirectory> Exchanges;
        public required Mock<IPortReservationService> Ports;
        public required Mock<IDesktopSettingsRepository> Settings;
        public required Mock<IWindowHandoffCoordinator> Handoff;
        public required Mock<IWindowManager> Windows;
        public required IdentityGateViewModel Vm;

        public IList<ExchangeTileViewModel> Tiles =>
            (IList<ExchangeTileViewModel>)Vm.Exchanges;
        public IList<SelectablePersonaModel> Personas =>
            (IList<SelectablePersonaModel>)Vm.Personas;

        public static GateFixture Create()
        {
            var session = Mock.Of<IAuthenticatedSession>();
            var directory = new Mock<IPersonaDirectory>(MockBehavior.Strict);
            var exchanges = new Mock<IExchangeDirectory>(MockBehavior.Strict);
            var ports = new Mock<IPortReservationService>(MockBehavior.Strict);
            var settings = new Mock<IDesktopSettingsRepository>(MockBehavior.Strict);
            var handoff = new Mock<IWindowHandoffCoordinator>(MockBehavior.Strict);
            var windows = new Mock<IWindowManager>(MockBehavior.Strict);

            directory
                .Setup(d => d.ListAsync(session, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PersonaDirectoryEntry>());
            exchanges
                .Setup(e => e.ListAsync(session, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ExchangeDirectoryEntry>());
            settings
                .Setup(s => s.GetAppSettingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
            settings
                .Setup(s => s.SetAppSettingAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var vm = new IdentityGateViewModel(
                directory.Object,
                exchanges.Object,
                ports.Object,
                settings.Object,
                new IdenticonGenerator(),
                handoff.Object,
                windows.Object,
                NullLogger<IdentityGateViewModel>.Instance);

            return new GateFixture
            {
                Session = session,
                Directory = directory,
                Exchanges = exchanges,
                Ports = ports,
                Settings = settings,
                Handoff = handoff,
                Windows = windows,
                Vm = vm
            };
        }
    }

    [Test]
    public async Task Gate_OnboardingActive_DisablesExchangeCreation()
    {
        var f = GateFixture.Create();
        await f.Vm.LoadAsync(f.Session);

        f.Vm.IsOnboardingActive.Should().BeTrue(
            "an unset completion flag means first-run onboarding is active");
        f.Vm.IsExchangeCreateEnabled.Should().BeFalse();
        f.Vm.CreateExchangeCommand.CanExecute(null).Should().BeFalse(
            "the hollow + tile is inert until onboarding completes");
    }

    [Test]
    public async Task Gate_DisableOnboarding_UnlocksExchangeCreation()
    {
        var f = GateFixture.Create();
        await f.Vm.LoadAsync(f.Session);

        await f.Vm.DisableOnboardingCommand.ExecuteAsync(null);

        f.Settings.Verify(s => s.SetAppSettingAsync(
                IdentityGateViewModel.OnboardingCompletedKey,
                "1",
                It.IsAny<CancellationToken>()),
            Times.Once);
        f.Vm.IsOnboardingActive.Should().BeFalse();
        f.Vm.CreateExchangeCommand.CanExecute(null).Should().BeTrue();
    }

    [Test]
    public async Task Gate_CompletedOnboarding_TileEnabledOnLoad()
    {
        var f = GateFixture.Create();
        f.Settings
            .Setup(s => s.GetAppSettingAsync(
                IdentityGateViewModel.OnboardingCompletedKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("1");

        await f.Vm.LoadAsync(f.Session);

        f.Vm.IsOnboardingActive.Should().BeFalse();
        f.Vm.IsExchangeCreateEnabled.Should().BeTrue();
    }

    [Test]
    public async Task Gate_CompletedOnboarding_NotifiesExchangeCreateEnabled()
    {
        var f = GateFixture.Create();
        f.Settings
            .Setup(s => s.GetAppSettingAsync(
                IdentityGateViewModel.OnboardingCompletedKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("1");
        var fired = new List<string?>();
        f.Vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        await f.Vm.LoadAsync(f.Session);

        // _session assignment is silent and IsOnboardingActive=false is a no-op on a
        // returning vault — without an explicit notification the + tile keeps its
        // initially-bound IsEnabled=false forever.
        fired.Should().Contain(nameof(IdentityGateViewModel.IsExchangeCreateEnabled));
        f.Vm.CreateExchangeCommand.CanExecute(null).Should().BeTrue();
    }

    [Test]
    public async Task Launch_SelectedSelfHostedExchange_StartsEndpoints_AndProvisionsMailboxes()
    {
        var f = GateFixture.Create();
        var persona = Persona("Alice");
        var exchange = Entry("Hub", port: 5200);
        f.Directory
            .Setup(d => d.ListAsync(f.Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaDirectoryEntry> { persona });
        f.Exchanges
            .Setup(e => e.ListAsync(f.Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExchangeDirectoryEntry> { exchange });
        f.Ports
            .Setup(p => p.ProbeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int port, CancellationToken _) =>
                new PortProbeResult(PortProbeStatus.Ready, port));
        await f.Vm.LoadAsync(f.Session);

        var personaLease = new Mock<IPortLease>();
        personaLease.SetupGet(l => l.Port).Returns(5100);
        var exchangeLease = new Mock<IPortLease>();
        exchangeLease.SetupGet(l => l.Port).Returns(5200);
        f.Ports
            .Setup(p => p.AcquireAsync(5100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(personaLease.Object);
        f.Ports
            .Setup(p => p.AcquireAsync(5200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exchangeLease.Object);
        f.Settings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopIdentitySettingsEntity?)null);
        f.Settings
            .Setup(s => s.SaveAsync(It.IsAny<DesktopIdentitySettingsEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        f.Exchanges
            .Setup(e => e.StartSelfHostedEndpointAsync(
                f.Session,
                It.Is<ExchangeLaunchDescriptor>(d =>
                    d.RelayIdentityId == exchange.RelayIdentityId && d.RequestedPort == 5200),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(5200);
        f.Exchanges
            .Setup(e => e.ProvisionLinkedMailboxesAsync(
                f.Session,
                It.Is<IEnumerable<PublicIdentityId>>(ids =>
                    ids.Count() == 1 && ids.First().Value == persona.IdentityId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());
        f.Handoff
            .Setup(h => h.TransitionToMainShellAsync(
                f.Session,
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.Is<IReadOnlyList<ExchangeLaunchDescriptor>?>(l =>
                    l != null && l.Count == 1 && l[0].Lease == exchangeLease.Object),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        f.Personas[0].IsSelected = true;
        f.Tiles[0].IsSelectedForLaunch = true;
        f.Vm.LaunchPersonasCommand.CanExecute(null).Should().BeTrue();

        await f.Vm.LaunchPersonasCommand.ExecuteAsync(null);

        f.Exchanges.Verify(e => e.StartSelfHostedEndpointAsync(
                f.Session,
                It.Is<ExchangeLaunchDescriptor>(d => d.RelayIdentityId == exchange.RelayIdentityId),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "selected self-hosted exchanges must map relay endpoints before handoff");
        f.Exchanges.Verify(e => e.ProvisionLinkedMailboxesAsync(
                f.Session,
                It.Is<IEnumerable<PublicIdentityId>>(ids =>
                    ids.Single().Value == persona.IdentityId),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "selected personas must provision mailboxes at launch");
        f.Handoff.Verify(h => h.TransitionToMainShellAsync(
                f.Session,
                It.IsAny<IReadOnlyList<PersonaLaunchDescriptor>?>(),
                It.IsAny<IReadOnlyList<ExchangeLaunchDescriptor>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        f.Settings.Verify(s => s.SetAppSettingAsync(
                IdentityGateViewModel.OnboardingCompletedKey,
                "1",
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a successful launch completes onboarding");
        f.Vm.IsOnboardingActive.Should().BeFalse();
    }
}
