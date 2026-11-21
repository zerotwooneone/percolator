// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;

[TestFixture]
public sealed class GroupCreationDialogViewModelTests
{
    private PublicIdentityId _activePersonaId;
    private Mock<IDialogContext<GroupCreationDialogParameter, GroupCreationDialogResult>> _dialogContextMock = null!;
    private Mock<IPeerContactQueryService> _contactQueryMock = null!;
    private Mock<IPersonaRelayQueryService> _relayQueryMock = null!;
    private FakeConversationStateService _conversationStateService = null!;

    private RelayEndpoint _relayEndpoint;
    private PublicIdentityId _relayId;
    private PublicIdentityId _contactId;

    [SetUp]
    public void SetUp()
    {
        _activePersonaId = PublicIdentityId.New();
        _relayId = PublicIdentityId.New();
        _contactId = PublicIdentityId.New();
        _relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);

        _dialogContextMock = new Mock<IDialogContext<GroupCreationDialogParameter, GroupCreationDialogResult>>();
        _dialogContextMock.Setup(d => d.Data).Returns(new GroupCreationDialogParameter(_activePersonaId, _relayEndpoint));

        _contactQueryMock = new Mock<IPeerContactQueryService>();
        _contactQueryMock.Setup(c => c.GetContactSummariesAsync(_activePersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>
            {
                new PeerContactSummaryReadModel(_contactId, "Bob", PeerTrustLevel.Verified, ContactState.Active, DateTimeOffset.UtcNow, true)
            });

        _relayQueryMock = new Mock<IPersonaRelayQueryService>();
        _relayQueryMock.Setup(r => r.GetRelayLinksForPersonaAsync(_activePersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersonaRelayLinkReadModel>
            {
                new PersonaRelayLinkReadModel(_relayId, "Local Relay", _relayEndpoint, false, RelayLinkStatus.Enabled)
            });

        _conversationStateService = new FakeConversationStateService();
    }

    [TearDown]
    public void TearDown()
    {
        _conversationStateService.Dispose();
    }

    private GroupCreationDialogViewModel CreateViewModel()
    {
        return new GroupCreationDialogViewModel(
            _dialogContextMock.Object,
            _contactQueryMock.Object,
            _relayQueryMock.Object,
            _conversationStateService,
            NullLogger<GroupCreationDialogViewModel>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var act1 = () => new GroupCreationDialogViewModel(
            null!,
            _contactQueryMock.Object,
            _relayQueryMock.Object,
            _conversationStateService,
            NullLogger<GroupCreationDialogViewModel>.Instance);
        act1.Should().Throw<ArgumentNullException>().WithParameterName("dialogContext");

        var act2 = () => new GroupCreationDialogViewModel(
            _dialogContextMock.Object,
            null!,
            _relayQueryMock.Object,
            _conversationStateService,
            NullLogger<GroupCreationDialogViewModel>.Instance);
        act2.Should().Throw<ArgumentNullException>().WithParameterName("contactQueryService");
    }

    [Test]
    public async Task LoadAsync_PopulatesRelaysAndContacts()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync(_dialogContextMock.Object.Data);

        vm.AvailableRelays.Should().HaveCount(1);
        vm.SelectedRelay.Should().NotBeNull();
        vm.SelectedRelay!.Endpoint.Should().Be(_relayEndpoint);

        vm.Contacts.Should().HaveCount(1);
        vm.Contacts[0].DisplayName.Should().Be("Bob");
    }

    [Test]
    public async Task CanCreate_ValidatesRequiredFields()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync(_dialogContextMock.Object.Data);

        vm.CanCreate.Should().BeFalse(); // Name empty, contact not selected

        vm.GroupName = "Secret Project";
        vm.CanCreate.Should().BeFalse(); // Contact not yet selected

        vm.Contacts[0].IsSelected = true;
        vm.CanCreate.Should().BeTrue();
    }

    [Test]
    public async Task CreateGroupCommand_RegistersConversationAndClosesDialog()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync(_dialogContextMock.Object.Data);

        vm.GroupName = "Alpha Team";
        vm.Contacts[0].IsSelected = true;

        vm.CreateGroupCommand.Execute(null);

        _dialogContextMock.Verify(d => d.Close(It.Is<GroupCreationDialogResult>(r =>
            r.IsSuccess &&
            r.GroupName == "Alpha Team" &&
            r.RelayEndpoint == _relayEndpoint &&
            r.SelectedMemberIds != null &&
            r.SelectedMemberIds.Contains(_contactId))), Times.Once);
    }

    [Test]
    public void CancelCommand_ClosesDialogWithCancelledResult()
    {
        using var vm = CreateViewModel();

        vm.CancelCommand.Execute(null);

        _dialogContextMock.Verify(d => d.Close(It.Is<GroupCreationDialogResult>(r => !r.IsSuccess)), Times.Once);
    }
}
