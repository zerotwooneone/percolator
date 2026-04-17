using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Chat.State;
using Desktop.Wpf.Shared.Mvvm;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public class ChatViewModelTests
{
    [Test]
    public void CanSend_reflects_non_empty_input()
    {
        var ctx = new SessionContext();
        using var chatState = new ChatStateService();
        var reloadCoordinator = new Mock<IChatReloadCoordinator>(MockBehavior.Loose);
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var vm = new ChatViewModel(ctx, chatState, reloadCoordinator.Object, mediator.Object, ui.Object);

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
        var reloadCoordinator = new Mock<IChatReloadCoordinator>(MockBehavior.Loose);
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var vm = new ChatViewModel(ctx, chatState, reloadCoordinator.Object, mediator.Object, ui.Object);
        var testSessionId = Guid.NewGuid().ToString("N");
        vm.SetSession(testSessionId);

        vm.MessageInput.Value = "hi";
        vm.CanSend.Value.Should().BeTrue();

        // Execute send synchronously and await observable effect deterministically
        vm.SendCommand.Execute(null);

        // Allow async command to run
        await Task.Delay(50);

        vm.MessageInput.Value.Should().Be(string.Empty);
        mediator.Verify(m => m.Send(
            It.Is<Percolator.Chat.App.Commands.PostTextMessageCommand>(cmd =>
                cmd.LookupKey.DirectSessionId == Guid.Parse(testSessionId) &&
                cmd.Content == "hi"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void SendCommand_CanExecute_tracks_CanSend()
    {
        var ctx = new SessionContext();
        using var chatState = new ChatStateService();
        var reloadCoordinator = new Mock<IChatReloadCoordinator>(MockBehavior.Loose);
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var vm = new ChatViewModel(ctx, chatState, reloadCoordinator.Object, mediator.Object, ui.Object);

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
        var reloadCoordinator = new Mock<IChatReloadCoordinator>(MockBehavior.Loose);
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var ui = new Mock<IUiDispatcher>(MockBehavior.Loose);
        var vm = new ChatViewModel(ctx, chatState, reloadCoordinator.Object, mediator.Object, ui.Object);

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
