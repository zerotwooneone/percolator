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
    public async Task SendCommand_appends_message_and_clears_input()
    {
        var ctx = new SessionContext();
        var chatState = new ChatStateService();
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var activeIdentity = new ActiveIdentityContext();
        var vm = new ChatViewModel(ctx, chatState, mediator.Object, ui.Object, activeIdentity);
        var testSessionId = new DirectSessionId(Guid.NewGuid());
        vm.SetSession(testSessionId);

        vm.MessageInput.Value = "hi";
        vm.CanSend.Value.Should().BeTrue();

        // Execute send synchronously and await observable effect deterministically
        vm.SendCommand.Execute(null);

        // Allow async command to run
        await Task.Delay(50);

        vm.MessageInput.Value.Should().Be(string.Empty);
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
