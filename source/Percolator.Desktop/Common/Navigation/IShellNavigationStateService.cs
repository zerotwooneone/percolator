// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Common.Navigation;

using System;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Session-scoped presentation state service coordinating navigation targets across the Left Rail,
/// Right Sidebar conversations, and Right-Anchored Inspector Drawer.
/// </summary>
public interface IShellNavigationStateService : IDisposable
{
    /// <summary>
    /// Current selection in the Left Sidebar (Direct Messages or a specific Relay).
    /// </summary>
    ReadOnlyReactiveProperty<LeftRailTarget> ActiveLeftRailTarget { get; }

    /// <summary>
    /// Current conversation ID selected in the Right Sidebar (null if none selected).
    /// </summary>
    ReadOnlyReactiveProperty<ChannelId?> SelectedConversationId { get; }

    /// <summary>
    /// True when the right-anchored inspector slide-over panel is open.
    /// </summary>
    ReadOnlyReactiveProperty<bool> IsInspectorOpen { get; }

    /// <summary>
    /// Active target (peer, relay, or group) being inspected in the inspector drawer.
    /// </summary>
    ReadOnlyReactiveProperty<InspectorTarget?> ActiveInspectorTarget { get; }

    void NavigateLeftRail(LeftRailTarget target);
    void NavigateToDirectMessages();
    void NavigateToRelay(PublicIdentityId relayId);
    void SelectConversation(ChannelId? conversationId);
    void OpenInspector(InspectorTarget target);
    void CloseInspector();
    void ToggleInspector();
}
