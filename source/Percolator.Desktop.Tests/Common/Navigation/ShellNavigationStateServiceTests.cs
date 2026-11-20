// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Common.Navigation;

using System;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Desktop.Common.Navigation;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

[TestFixture]
public sealed class ShellNavigationStateServiceTests
{
    private ShellNavigationStateService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new ShellNavigationStateService(NullLogger<ShellNavigationStateService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
    }

    [Test]
    public void InitialState_HasExpectedDefaults()
    {
        _service.ActiveLeftRailTarget.CurrentValue.Should().Be(LeftRailTarget.DirectMessages);
        _service.SelectedConversationId.CurrentValue.Should().BeNull();
        _service.IsInspectorOpen.CurrentValue.Should().BeFalse();
        _service.ActiveInspectorTarget.CurrentValue.Should().BeNull();
    }

    [Test]
    public void NavigateLeftRail_WithRelayTarget_UpdatesActiveLeftRailTarget()
    {
        var relayId = PublicIdentityId.New();
        var relayTarget = LeftRailTarget.Relay(relayId);

        _service.NavigateLeftRail(relayTarget);

        _service.ActiveLeftRailTarget.CurrentValue.Should().Be(relayTarget);
    }

    [Test]
    public void NavigateLeftRail_ToDirectMessages_UpdatesActiveLeftRailTarget()
    {
        var relayId = PublicIdentityId.New();
        _service.NavigateLeftRail(LeftRailTarget.Relay(relayId));

        _service.NavigateLeftRail(LeftRailTarget.DirectMessages);

        _service.ActiveLeftRailTarget.CurrentValue.Should().Be(LeftRailTarget.DirectMessages);
    }

    [Test]
    public void SelectConversation_UpdatesSelectedConversationId()
    {
        var convId = ChannelId.New();

        _service.SelectConversation(convId);

        _service.SelectedConversationId.CurrentValue.Should().Be(convId);

        _service.SelectConversation(null);

        _service.SelectedConversationId.CurrentValue.Should().BeNull();
    }

    [Test]
    public void OpenAndCloseInspector_UpdatesInspectorStateAndTarget()
    {
        var target = new InspectorTarget(InspectorTargetKind.DirectPeer, PublicIdentityId.New());

        _service.OpenInspector(target);

        _service.IsInspectorOpen.CurrentValue.Should().BeTrue();
        _service.ActiveInspectorTarget.CurrentValue.Should().Be(target);

        _service.CloseInspector();

        _service.IsInspectorOpen.CurrentValue.Should().BeFalse();
    }

    [Test]
    public void ToggleInspector_InvertsInspectorState()
    {
        _service.IsInspectorOpen.CurrentValue.Should().BeFalse();

        _service.ToggleInspector();
        _service.IsInspectorOpen.CurrentValue.Should().BeTrue();

        _service.ToggleInspector();
        _service.IsInspectorOpen.CurrentValue.Should().BeFalse();
    }
}
