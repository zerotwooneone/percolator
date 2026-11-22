// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Delivery.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

namespace Percolator.Desktop.Tests.Startup;

[TestFixture]
public sealed class CreatePersonaDialogViewModelTests
{
    private Mock<IDialogContext<PersonaEditorInput, PersonaEditorResult?>> _dialogContext = null!;
    private readonly IIdenticonGenerator _identicons = new IdenticonGenerator();
    private Mock<IPersonaRelayQueryService> _relayQuery = null!;
    private Mock<IRelayDirectory> _relayDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _dialogContext = new Mock<IDialogContext<PersonaEditorInput, PersonaEditorResult?>>(MockBehavior.Strict);
        _relayQuery = new Mock<IPersonaRelayQueryService>(MockBehavior.Strict);
        _relayDirectory = new Mock<IRelayDirectory>(MockBehavior.Strict);

        _dialogContext.SetupGet(d => d.Data).Returns(PersonaEditorInput.ForCreate(5100));
    }


    [Test]
    public void InitializeForCreate_DefaultsToCreateStateAndCanRerollAvatar()
    {
        var vm = new CreatePersonaDialogViewModel(
            _dialogContext.Object,
            _identicons,
            _relayQuery.Object,
            _relayDirectory.Object);

        vm.Title.Should().Be("Create Persona");
        vm.PrimaryText.Should().Be("Create Persona");
        vm.CanRerollAvatar.Should().BeTrue();
        vm.HasConnectedRelays.Should().BeFalse();
        vm.ConnectedRelays.Should().BeEmpty();
    }

    [Test]
    public async Task InitializeForEdit_LoadsConnectedRelaysFromQueryService()
    {
        var personaId = PublicIdentityId.New();
        var relayId = PublicIdentityId.New();
        var endpoint = RelayEndpoint.FromHostAndPort("relay.example.com", 5102);

        var links = new List<PersonaRelayLinkReadModel>
        {
            new(relayId, "Primary Relay", endpoint, IsLocalRelayHost: false, RelayLinkStatus.Enabled)
        };

        _relayQuery
            .Setup(q => q.GetRelayLinksForPersonaAsync(personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        var input = PersonaEditorInput.ForEdit("Alice", 5100, personaId);
        _dialogContext.SetupGet(d => d.Data).Returns(input);

        var vm = new CreatePersonaDialogViewModel(
            _dialogContext.Object,
            _identicons,
            _relayQuery.Object,
            _relayDirectory.Object);

        // Await background relay load
        await vm.LoadRelaysAsync(personaId);

        vm.Title.Should().Be("Edit Profile");
        vm.PrimaryText.Should().Be("Save Changes");
        vm.CanRerollAvatar.Should().BeFalse();
        vm.HasConnectedRelays.Should().BeTrue();
        vm.ConnectedRelays.Should().HaveCount(1);
        vm.ConnectedRelays[0].DisplayName.Should().Be("Primary Relay");
        vm.ConnectedRelays[0].IsEnabled.Should().BeTrue();
    }

    [Test]
    public async Task Save_PersistsUpdatedRelayLinkStatus()
    {
        var personaId = PublicIdentityId.New();
        var relayId = PublicIdentityId.New();
        var endpoint = RelayEndpoint.FromHostAndPort("relay.example.com", 5102);

        var links = new List<PersonaRelayLinkReadModel>
        {
            new(relayId, "Primary Relay", endpoint, IsLocalRelayHost: false, RelayLinkStatus.Enabled)
        };

        _relayQuery
            .Setup(q => q.GetRelayLinksForPersonaAsync(personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        _relayDirectory
            .Setup(d => d.SetMailboxBindingStatusAsync(relayId, personaId, RelayLinkStatus.Disabled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        PersonaEditorResult? savedResult = null;
        _dialogContext
            .Setup(d => d.Close(It.IsAny<PersonaEditorResult>()))
            .Callback<PersonaEditorResult?>(r => savedResult = r);

        var input = PersonaEditorInput.ForEdit("Alice", 5100, personaId);
        _dialogContext.SetupGet(d => d.Data).Returns(input);

        var vm = new CreatePersonaDialogViewModel(
            _dialogContext.Object,
            _identicons,
            _relayQuery.Object,
            _relayDirectory.Object);

        await vm.LoadRelaysAsync(personaId);

        // User disables the relay
        vm.ConnectedRelays[0].IsEnabled = false;

        await vm.SaveCommand.ExecuteAsync(null);

        _relayDirectory.Verify(
            d => d.SetMailboxBindingStatusAsync(relayId, personaId, RelayLinkStatus.Disabled, It.IsAny<CancellationToken>()),
            Times.Once);

        savedResult.Should().NotBeNull();
        savedResult!.DisplayName.Should().Be("Alice");
    }
}
