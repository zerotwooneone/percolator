using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Chat.State;
using Desktop.Wpf.Shared.Mvvm;
using FluentAssertions;
using MediatR;
using Moq;
using NUnit.Framework;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Network;
using Percolator.Application.Identity;

namespace Desktop.Wpf.Tests;

[TestFixture]
public class ChatViewModelTests
{
    [Test]
    public void CanSend_reflects_non_empty_input()
    {
        var ctx = new SessionContext();
        using var chatState = new ChatStateService();
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var activeIdentity = new ActiveIdentityContext();
        var vm = new ChatViewModel(ctx, chatState, mediator.Object, ui.Object, activeIdentity);

        vm.MessageInput.Value = "";
        vm.CanSend.Value.Should().BeFalse();

        vm.MessageInput.Value = "hello";
        vm.CanSend.Value.Should().BeTrue();
    }

    [Test]
    public async Task SendCommand_WhenExecuted_SendsMessageAndClearsInput()
    {
        // Arrange
        var ctx = new SessionContext();
        var chatState = new ChatStateService();
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        mediator.Setup(m => m.Send(It.IsAny<PostTextMessageCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var activeIdentity = new ActiveIdentityContext();
        activeIdentity.SetActiveIdentity(new Percolator.Identity.Model.IdentityRecord(
            new Percolator.Identity.SelfId(1),
            new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000002")),
            new Percolator.Identity.DeviceId(1),
            "test"));
        var vm = new ChatViewModel(ctx, chatState, mediator.Object, ui.Object, activeIdentity);
        var testSessionId = new DirectSessionId(new Guid("00000000-0000-0000-0000-000000000001"));
        vm.SetSession(testSessionId);

        vm.MessageInput.Value = "hi";
        vm.CanSend.Value.Should().BeTrue();

        // Act
        vm.SendCommand.Execute(null);
        await Task.Yield(); // Allow async command to start

        // Assert - Verify message was sent via mediator (public contract)
        mediator.Verify(m => m.Send(
            It.Is<PostTextMessageCommand>(cmd =>
                cmd.LookupKey.DirectSessionId.Value == testSessionId.Value &&
                cmd.Content == "hi"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void SendCommand_CanExecute_tracks_CanSend()
    {
        var ctx = new SessionContext();
        using var chatState = new ChatStateService();
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var activeIdentity = new ActiveIdentityContext();
        var vm = new ChatViewModel(ctx, chatState, mediator.Object, ui.Object, activeIdentity);

        vm.MessageInput.Value = string.Empty;
        vm.SendCommand.CanExecute(null).Should().BeFalse();

        vm.MessageInput.Value = "hello";
        vm.SendCommand.CanExecute(null).Should().BeTrue();
    }

    [Test]
    public void RouteIcon_and_Text_change_with_IsRelayed()
    {
        var ctx = new SessionContext();
        using var chatState = new ChatStateService();
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var activeIdentity = new ActiveIdentityContext();
        var vm = new ChatViewModel(ctx, chatState, mediator.Object, ui.Object, activeIdentity);

        // Direct state
        vm.IsRelayed.Value = false;
        vm.RouteText.Value.Should().Be("Direct Route");
        vm.RouteIcon.Value.Should().NotBeNullOrWhiteSpace();

        // Relay state
        vm.IsRelayed.Value = true;
        vm.RouteText.Value.Should().Be("Relayed Route");
        vm.RouteIcon.Value.Should().NotBeNullOrWhiteSpace();
    }
}
