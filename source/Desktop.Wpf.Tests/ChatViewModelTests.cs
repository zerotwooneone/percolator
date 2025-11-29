using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Features.Sessions;
using FluentAssertions;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public class ChatViewModelTests
{
    [Test]
    public void CanSend_reflects_non_empty_input()
    {
        var history = new InMemoryChatHistory();
        var ctx = new SessionContext();
        var vm = new ChatViewModel(history, ctx);

        vm.MessageInput.Value = "";
        vm.CanSend.Value.Should().BeFalse();

        vm.MessageInput.Value = "hello";
        vm.CanSend.Value.Should().BeTrue();
    }

    [Test]
    public async Task SendCommand_appends_message_and_clears_input()
    {
        var history = new InMemoryChatHistory();
        var ctx = new SessionContext();
        var vm = new ChatViewModel(history, ctx);
        vm.SetSession("test-session");

        vm.MessageInput.Value = "hi";
        vm.CanSend.Value.Should().BeTrue();

        // Execute send synchronously and await observable effect deterministically
        vm.SendCommand.Execute(null);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        while (vm.Messages.Count == 0 && !cts.IsCancellationRequested)
            await Task.Delay(10, cts.Token);

        vm.Messages.Count.Should().Be(1);
        vm.Messages.First().Text.Should().Be("hi");
        vm.MessageInput.Value.Should().Be(string.Empty);
    }

    [Test]
    public void SendCommand_CanExecute_tracks_CanSend()
    {
        var history = new InMemoryChatHistory();
        var ctx = new SessionContext();
        var vm = new ChatViewModel(history, ctx);

        vm.MessageInput.Value = string.Empty;
        vm.SendCommand.CanExecute(null).Should().BeFalse();

        vm.MessageInput.Value = "hello";
        vm.SendCommand.CanExecute(null).Should().BeTrue();
    }

    [Test]
    public void RouteIcon_and_Text_change_with_IsRelayed()
    {
        var history = new InMemoryChatHistory();
        var ctx = new SessionContext();
        var vm = new ChatViewModel(history, ctx);

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
